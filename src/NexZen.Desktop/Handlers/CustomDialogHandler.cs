using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.IO;
using System.Linq;
using Xilium.CefGlue;

namespace NexZen.Desktop.Handlers;

public class CustomDialogHandler : Xilium.CefGlue.Common.Handlers.DialogHandler
{
    protected override bool OnFileDialog(CefBrowser browser, CefFileDialogMode mode, string title, string defaultFilePath, string[] acceptFilters, CefFileDialogCallback callback)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var lifetime = global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                var mainWindow = lifetime?.MainWindow;
                if (mainWindow == null)
                {
                    callback.Cancel();
                    return;
                }

                var storageProvider = mainWindow.StorageProvider;

                if (mode.HasFlag(CefFileDialogMode.Open) || mode.HasFlag(CefFileDialogMode.OpenMultiple))
                {
                    var options = new FilePickerOpenOptions
                    {
                        Title = string.IsNullOrEmpty(title) ? "Open File" : title,
                        AllowMultiple = mode.HasFlag(CefFileDialogMode.OpenMultiple)
                    };
                    
                    var files = await storageProvider.OpenFilePickerAsync(options);
                    if (files != null && files.Count > 0)
                    {
                        var paths = files.Select(f => f.Path.LocalPath).ToArray();
                        callback.Continue(paths);
                    }
                    else
                    {
                        callback.Cancel();
                    }
                }
                else if (mode.HasFlag(CefFileDialogMode.Save))
                {
                    var options = new FilePickerSaveOptions
                    {
                        Title = string.IsNullOrEmpty(title) ? "Save File" : title,
                        SuggestedFileName = Path.GetFileName(defaultFilePath)
                    };
                    
                    var file = await storageProvider.SaveFilePickerAsync(options);
                    if (file != null)
                    {
                        callback.Continue(new[] { file.Path.LocalPath });
                    }
                    else
                    {
                        callback.Cancel();
                    }
                }
                else if (mode.HasFlag(CefFileDialogMode.OpenFolder))
                {
                    var options = new FolderPickerOpenOptions
                    {
                        Title = string.IsNullOrEmpty(title) ? "Select Folder" : title,
                        AllowMultiple = mode.HasFlag(CefFileDialogMode.OpenMultiple)
                    };
                    
                    var folders = await storageProvider.OpenFolderPickerAsync(options);
                    if (folders != null && folders.Count > 0)
                    {
                        var paths = folders.Select(f => f.Path.LocalPath).ToArray();
                        callback.Continue(paths);
                    }
                    else
                    {
                        callback.Cancel();
                    }
                }
                else
                {
                    callback.Cancel();
                }
            }
            catch
            {
                callback.Cancel();
            }
        });

        return true;
    }
}
