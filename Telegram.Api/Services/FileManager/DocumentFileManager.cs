﻿// 
// This is the source code of Telegram for Windows Phone v. 3.x.x.
// It is licensed under GNU GPL v. 2 or later.
// You should have received a copy of the license in this archive (see LICENSE).
// 
// Copyright Evgeny Nadymov, 2013-present.
// 
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Telegram.Api.Aggregator;
using Telegram.Api.Extensions;
using Telegram.Api.Helpers;
using Telegram.Api.TL;

namespace Telegram.Api.Services.FileManager
{
    public class DocumentFileManager : FileManagerBase, IDocumentFileManager
    {
        public DocumentFileManager(ITelegramEventAggregator eventAggregator, IMTProtoService mtProtoService) : base(eventAggregator, mtProtoService)
        {
            for (var i = 0; i < Constants.BigFileWorkersNumber; i++)
            {
                var worker = new Worker(OnDownloading, "documentDownloader" + i);
                _workers.Add(worker);
            }
        }

        private void OnDownloading(object state)
        {
            DownloadablePart part = null;
            lock (_itemsSyncRoot)
            {
                for (var i = 0; i < _items.Count; i++)
                {
                    var item = _items[i];
                    if (item.Canceled)
                    {
                        _items.RemoveAt(i--);
                        try
                        {
                            //_eventAggregator.Publish(new UploadingCanceledEventArgs(item));
                        }
                        catch (Exception e)
                        {
                            TLUtils.WriteException(e);
                        }
                    }
                }

                foreach (var item in _items)
                {
                    part = item.Parts.FirstOrDefault(x => x.Status == PartStatus.Ready);
                    if (part != null)
                    {
                        part.Status = PartStatus.Processing;
                        break;
                    }
                }
            }

            if (part == null)
            {
                var currentWorker = (Worker)state;
                currentWorker.Stop();
                return;
            }

            var partName = part.ParentItem.InputLocation.GetPartFileName(part.Number, "document");
            bool canceled;
            ProcessFilePart(part, part.ParentItem.DCId, part.ParentItem.InputLocation, out canceled);
            if (canceled)
            {
                lock (_itemsSyncRoot)
                {
                    part.ParentItem.Canceled = true;
                    part.Status = PartStatus.Processed;
                    _items.Remove(part.ParentItem);
                }

                return;
            }
            //part.File = GetFile(part.ParentItem.DCId, part.ParentItem.InputDocumentLocation, part.Offset, part.Limit);
            //while (part.File == null)
            //{
            //    part.File = GetFile(part.ParentItem.DCId, part.ParentItem.InputDocumentLocation, part.Offset, part.Limit);
            //}

            // indicate progress
            // indicate complete
            bool isComplete;
            bool isCanceled;
            var progress = 0.0;
            lock (_itemsSyncRoot)
            {
                part.Status = PartStatus.Processed;

                if (!part.ParentItem.SuppressMerge)
                {
                    FileUtils.CheckMissingPart(_itemsSyncRoot, part, partName);
                }

                isCanceled = part.ParentItem.Canceled;

                isComplete = part.ParentItem.Parts.All(x => x.Status == PartStatus.Processed);
                if (!isComplete)
                {
                    var downloadedCount = part.ParentItem.Parts.Count(x => x.Status == PartStatus.Processed);
                    var count = part.ParentItem.Parts.Count;
                    progress = downloadedCount / (double)count;
                }
                else
                {
                    //var id = part.ParentItem.InputDocumentLocation.Id;
                    //var accessHash = part.ParentItem.InputDocumentLocation.AccessHash;
                    //var fileExtension = Path.GetExtension(part.ParentItem.FileName.ToString());
                    //var fileName = string.Format("document{0}_{1}{2}", id, accessHash, fileExtension);

                    //if (fileName.EndsWith(".mp4"))
                    //{
                    //    Logs.Log.SyncWrite("FileManager.IsComplete " + fileName + " hash=" + part.ParentItem.GetHashCode());
                    //}

                    _items.Remove(part.ParentItem);
                }
            }

            if (!isCanceled)
            {
                if (isComplete)
                {
                    //var id = part.ParentItem.InputDocumentLocation.Id;
                    //var accessHash = part.ParentItem.InputDocumentLocation.AccessHash;
                    //var version = part.ParentItem.InputDocumentLocation.Version;
                    var fileExtension = Path.GetExtension(part.ParentItem.FileName.ToString());
                    var fileName = part.ParentItem.InputLocation.GetFileName("document", fileExtension);
                    Func<DownloadablePart, string> getPartName = x => part.ParentItem.InputLocation.GetPartFileName(x.Number, "document");

                    if (!part.ParentItem.SuppressMerge)
                    {
                        FileUtils.MergePartsToFile(getPartName, part.ParentItem.Parts, fileName);
                    }

                    part.ParentItem.IsoFileName = fileName;
                    if (part.ParentItem.Callback != null)
                    {
                        Execute.BeginOnThreadPool(() =>
                        {
                            part.ParentItem.Callback(part.ParentItem);
                            if (part.ParentItem.Callbacks != null)
                            {
                                foreach (var callback in part.ParentItem.Callbacks)
                                {
                                    callback.SafeInvoke(part.ParentItem);
                                }
                            }
                        });
                    }
                    else
                    {
                        Execute.BeginOnThreadPool(() => _eventAggregator.Publish(part.ParentItem));
                    }
                }
                else
                {
                    if (part.NotifyProgress)
                    {
                        Execute.BeginOnThreadPool(() => _eventAggregator.Publish(new ProgressChangedEventArgs(part.ParentItem, progress)));
                    }
                }
            }
        }

        public void DownloadFileAsync(TLString originalFileName, TLInt dcId, TLInputFileLocationBase fileLocation, TLObject owner, TLInt fileSize, Action<double> startCallback, Action<DownloadableItem> callback = null)
        {
            Execute.BeginOnThreadPool(() =>
            {
                var downloadableItem = GetDownloadableItem(originalFileName, dcId, fileLocation, owner, fileSize, callback);

                var downloadedCount = downloadableItem.Parts.Count(x => x.Status == PartStatus.Processed);
                var count = downloadableItem.Parts.Count;
                var isComplete = downloadedCount == count;

                if (isComplete)
                {
                    var fileExtension = Path.GetExtension(downloadableItem.FileName.ToString());
                    var fileName = downloadableItem.InputLocation.GetFileName("document", fileExtension);
                    Func<DownloadablePart, string> getPartName = x => downloadableItem.InputLocation.GetPartFileName(x.Number, "document");

                    FileUtils.MergePartsToFile(getPartName, downloadableItem.Parts, fileName);

                    downloadableItem.IsoFileName = fileName;
                    _eventAggregator.Publish(downloadableItem);
                }
                else
                {
                    var progress = downloadedCount/(double) count;
                    startCallback.SafeInvoke(progress);

                    lock (_itemsSyncRoot)
                    {
                        bool addFile = true;
                        foreach (var item in _items)
                        {
                            if (item.InputLocation.LocationEquals(fileLocation))
                            {

                                //item.SuppressMerge = true;

                                if (callback != null)
                                {
                                    if (item.Callbacks == null)
                                    {
                                        item.Callbacks = new List<Action<DownloadableItem>>();
                                    }
                                    item.Callbacks.Add(callback);
                                    addFile = false;
                                    break;
                                }
                                //item.
                                if (item.Owner == owner)
                                {
                                    addFile = false;
                                    break;
                                }
                            }
                        }

                        if (addFile)
                        {
                            _items.Add(downloadableItem);
                        }
                    }

                    StartAwaitingWorkers();
                }
            });
        }

        private DownloadableItem GetDownloadableItem(TLString fileName, TLInt dcId, TLInputFileLocationBase location, TLObject owner, TLInt fileSize, Action<DownloadableItem> callback)
        {
            var item = new DownloadableItem
            {
                DCId = dcId,
                FileName = fileName,
                Owner = owner,
                InputLocation = location,
                Callback = callback
            };
            item.Parts = GetItemParts(fileSize, item);

            return item;
        }

        protected override List<DownloadablePart> GetItemParts(TLInt size, DownloadableItem item)
        {
            var chunkSize = size.Value > 1024*1024 ? Constants.DownloadedBigChunkSize : Constants.DownloadedChunkSize;
            var parts = new List<DownloadablePart>();
            var partsCount = size.Value / chunkSize + (size.Value % chunkSize > 0 ? 1 : 0);

            var step = partsCount / 25;

            for (var i = 0; i < partsCount; i++)
            {
                var part = new DownloadablePart(item, new TLInt(i * chunkSize), size.Value == 0 ? new TLInt(1024 * 1024) : new TLInt(chunkSize), i);
                var partName = item.InputLocation.GetPartFileName(part.Number, "document");
                var partLength = FileUtils.GetLocalFileLength(partName);

                if (partLength >= 0)
                {
                    var isCompletePart = (part.Number + 1 == partsCount) || partLength == part.Limit.Value;
                    part.Status = isCompletePart ? PartStatus.Processed : PartStatus.Ready;
                }

                part.NotifyProgress = part.Status == PartStatus.Ready && (step == 0 || i % step == 0);

                parts.Add(part);
            }

            return parts;
        }
    }
}
