using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using NexZen.Application.ViewModels;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers
{
    public class CustomDownloadHandler : DownloadHandler
    {
        private readonly MainViewModel _viewModel;
        private readonly ConcurrentDictionary<uint, NativeSlot> _native = new();

        private sealed class NativeSlot
        {
            public DownloadItemViewModel? Item;
            public string FilePath = string.Empty;
            public string ObservedPath = string.Empty;
            public long Received;
            public long Total;
            public int Percent = -1;
            public int LastLoggedPercent = -1;
            public bool Complete;
            public bool Failed;
            public bool Finished;
            public volatile bool CancelRequested;
            public volatile bool PauseRequested;
            public volatile bool ResumeRequested;
        }

        public CustomDownloadHandler(MainViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        protected override bool OnBeforeDownload(CefBrowser browser, CefDownloadItem downloadItem, string suggestedName, CefBeforeDownloadCallback callback)
        {
            if (downloadItem == null || !downloadItem.IsValid)
            {
                Log("[Download] OnBeforeDownload skipped: invalid item");
                return false;
            }

            var id = downloadItem.Id;
            var url = downloadItem.Url ?? string.Empty;
            var filePath = MainViewModel.AllocateDownloadPath(SafeFileName(suggestedName, downloadItem));
            var slot = _native.GetOrAdd(id, _ => new NativeSlot());
            slot.FilePath = filePath;
            slot.Total = SanitizeBytes(downloadItem.TotalBytes);
            slot.Received = SanitizeBytes(downloadItem.ReceivedBytes);
            TryObservePath(downloadItem, slot);

            Log($"[Download] continue id={id} path={filePath} url={url} total={slot.Total}");
            callback.Continue(filePath, showDialog: false);

            RunOnUi(() =>
            {
                EnsureItem(slot, url, filePath);
                Apply(slot);
                _ = WatchFileAsync(slot);
            });
            return true;
        }

        protected override void OnDownloadUpdated(CefBrowser browser, CefDownloadItem downloadItem, CefDownloadItemCallback callback)
        {
            if (downloadItem == null || !downloadItem.IsValid)
                return;

            var id = downloadItem.Id;
            var slot = _native.GetOrAdd(id, _ => new NativeSlot());

            if (slot.CancelRequested)
            {
                slot.CancelRequested = false;
                slot.Failed = true;
                try { callback.Cancel(); }
                catch (Exception ex) { Log($"[Download] cancel failed id={id}: {ex.Message}"); }
                RunOnUi(() => Apply(slot));
                return;
            }

            if (slot.PauseRequested)
            {
                slot.PauseRequested = false;
                try { callback.Pause(); }
                catch (Exception ex) { Log($"[Download] pause failed id={id}: {ex.Message}"); }
            }
            else if (slot.ResumeRequested)
            {
                slot.ResumeRequested = false;
                try { callback.Resume(); }
                catch (Exception ex) { Log($"[Download] resume failed id={id}: {ex.Message}"); }
            }

            TryObservePath(downloadItem, slot);
            slot.Received = SanitizeBytes(downloadItem.ReceivedBytes);
            slot.Total = SanitizeBytes(downloadItem.TotalBytes);
            var percent = downloadItem.PercentComplete;
            slot.Percent = percent is >= 0 and <= 100 ? percent : -1;
            slot.Complete = downloadItem.IsComplete;
            slot.Failed = downloadItem.IsCanceled || downloadItem.IsInterrupted;

            if (ShouldLog(slot))
            {
                slot.LastLoggedPercent = slot.Percent;
                Log($"[Download] updated id={id} recv={slot.Received} total={slot.Total} pct={slot.Percent} complete={slot.Complete} failed={slot.Failed} path={slot.FilePath} observed={slot.ObservedPath}");
            }

            RunOnUi(() => Apply(slot));
        }

        private void EnsureItem(NativeSlot slot, string url, string filePath)
        {
            if (slot.Item != null)
                return;

            var item = _viewModel.CreateDownloadItem(url, Path.GetFileName(filePath), filePath);
            slot.Item = item;
            item.OnPauseRequested = _ => slot.PauseRequested = true;
            item.OnResumeRequested = _ => slot.ResumeRequested = true;
            item.OnCancelRequested = _ => slot.CancelRequested = true;
        }

        private void Apply(NativeSlot slot)
        {
            var item = slot.Item;
            if (item == null || slot.Finished)
                return;

            var disk = FileSize(slot);
            var received = slot.Received > 0 ? slot.Received : disk;
            var total = slot.Total > 0 ? slot.Total : 0;
            if (received > 0)
                item.DownloadedBytes = received;
            if (total > 0)
                item.TotalBytes = total;
            if (slot.Percent >= 0)
                item.Progress = slot.Percent;
            else if (total > 0 && received > 0)
                item.Progress = (int)Math.Clamp(received * 100 / total, 0, 99);

            if ((slot.Complete || (total > 0 && disk >= total && disk > 0)) && item.State != "Cancelled")
            {
                MarkCompleted(slot, item, disk > 0 ? disk : received);
                return;
            }

            if (slot.Failed && item.State is not "Cancelled" and not "Paused" and not "Completed")
            {
                if (disk > 0)
                {
                    MarkCompleted(slot, item, disk);
                    return;
                }
                item.State = "Error";
                _ = RecheckFileAsync(slot);
            }
        }

        private static void MarkCompleted(NativeSlot slot, DownloadItemViewModel item, long size)
        {
            if (size > 0)
            {
                if (item.TotalBytes == 0) item.TotalBytes = size;
                item.DownloadedBytes = size;
            }
            item.State = "Completed";
            item.Progress = 100;
            slot.Finished = true;
            Log($"[Download] completed path={slot.FilePath} size={size}");
            _ = HideLater(item);
        }

        private async Task WatchFileAsync(NativeSlot slot)
        {
            while (!slot.Finished)
            {
                await Task.Delay(250);
                await Dispatcher.UIThread.InvokeAsync(() => Apply(slot));
            }
        }

        private static async Task RecheckFileAsync(NativeSlot slot)
        {
            await Task.Delay(400);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var item = slot.Item;
                if (item == null || item.State == "Cancelled" || slot.Finished)
                    return;
                var size = FileSize(slot);
                if (size > 0 || slot.Complete)
                    MarkCompleted(slot, item, size);
            });
        }

        private static async Task HideLater(DownloadItemViewModel item)
        {
            await Task.Delay(2000);
            await Dispatcher.UIThread.InvokeAsync(() => item.IsDialogVisible = false);
        }

        private static void TryObservePath(CefDownloadItem downloadItem, NativeSlot slot)
        {
            try
            {
                var full = downloadItem.FullPath;
                if (!string.IsNullOrEmpty(full))
                    slot.ObservedPath = full;
            }
            catch (Exception ex)
            {
                Log($"[Download] FullPath failed: {ex.Message}");
            }
        }

        private static bool ShouldLog(NativeSlot slot)
        {
            if (slot.Complete || slot.Failed)
                return true;
            if (slot.Percent < 0)
                return slot.Received > 0 && slot.LastLoggedPercent < 0;
            return slot.LastLoggedPercent < 0 || slot.Percent / 10 != slot.LastLoggedPercent / 10;
        }

        private static void RunOnUi(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }

        private static long SanitizeBytes(long value) =>
            value is > 0 and < (1L << 40) ? value : 0;

        private static long FileSize(NativeSlot slot)
        {
            var best = LengthOf(slot.FilePath);
            best = Math.Max(best, LengthOf(slot.FilePath + ".crdownload"));
            if (!string.IsNullOrEmpty(slot.ObservedPath))
            {
                best = Math.Max(best, LengthOf(slot.ObservedPath));
                best = Math.Max(best, LengthOf(slot.ObservedPath + ".crdownload"));
            }
            return best;
        }

        private static long LengthOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return 0;
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
            Program.StartupLog.Write(message);
        }

        private static string SafeFileName(string? suggestedName, CefDownloadItem item)
        {
            var name = suggestedName;
            if (string.IsNullOrWhiteSpace(name) && item.IsValid)
                name = item.SuggestedFileName;
            if (string.IsNullOrWhiteSpace(name) && item.IsValid)
            {
                try
                {
                    name = Path.GetFileName(new Uri(item.Url ?? string.Empty).LocalPath);
                }
                catch
                {
                    name = string.Empty;
                }
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                var mime = item.IsValid ? (item.MimeType ?? string.Empty) : string.Empty;
                name = mime switch
                {
                    "model/gltf-binary" => "model.glb",
                    "model/gltf+json" => "model.gltf",
                    "model/obj" => "model.obj",
                    "model/fbx" => "model.fbx",
                    "application/zip" => "model.zip",
                    "application/octet-stream" => "model.glb",
                    _ => "download.bin"
                };
            }
            return name;
        }
    }
}
