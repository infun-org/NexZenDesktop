using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Application.ViewModels;
using NexZen.Desktop.Helpers;
using NexZen.Desktop.Services;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Views;

public partial class AiFunctionView : UserControl
{
    private AvaloniaCefBrowser? _companionBrowser;
    private bool _companionLayoutHooked;
    private Window? _hostWindow;
    private bool _pasteBusy;
    private readonly DispatcherTimer _llamaIdleTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public AiFunctionView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            EnsureCompanionBrowser();
            AttachResearchBrowser();
        };
        AddHandler(InputElement.KeyDownEvent, OnViewPasteKeyDown, RoutingStrategies.Tunnel);
        _llamaIdleTimer.Tick += OnLlamaIdleTick;
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is AiFunctionViewModel vm)
            BindViewModel(vm);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureCompanionBrowser();
        AttachResearchBrowser();
        if (VisualRoot is Window window && !ReferenceEquals(window, _hostWindow))
        {
            if (_hostWindow != null)
                _hostWindow.PropertyChanged -= OnHostPropertyChanged;
            _hostWindow = window;
            _hostWindow.PropertyChanged += OnHostPropertyChanged;
        }
        if (DataContext is AiFunctionViewModel vm)
            BindViewModel(vm);
        SyncWindowVisible();
        if (!_llamaIdleTimer.IsEnabled)
            _llamaIdleTimer.Start();
    }

    private void AttachResearchBrowser()
    {
        var host = this.FindControl<Border>("ResearchHost");
        if (host == null)
            return;
        App.ServiceProvider?.GetService<CefResearchBrowser>()?.Attach(host);
    }

    private void BindViewModel(AiFunctionViewModel vm)
    {
        vm.EnsureVaultAsync = EnsureVaultAsync;
        vm.ExtractPageJsonAsync = ExtractPageJsonAsync;
        vm.ExtractRemotePageJsonAsync = ExtractRemotePageJsonAsync;
        vm.SaveConversationFileAsync = SaveConversationFileAsync;
        vm.PickImageFilesAsync = PickImageFilesAsync;
        vm.MessagesLayoutChanged = ScrollChatToEnd;
    }

    private async Task<string?> ExtractPageJsonAsync()
    {
        var tabId = (DataContext as AiFunctionViewModel)?.ActiveTabId
            ?? App.ServiceProvider?.GetService<AiFunctionViewModel>()?.ActiveTabId;
        if (string.IsNullOrWhiteSpace(tabId))
            return null;
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        foreach (var window in desktop.Windows.OfType<MainWindow>())
        {
            var json = await window.TryExtractPageJsonAsync(tabId);
            if (!string.IsNullOrWhiteSpace(json))
                return json;
        }
        if (desktop.MainWindow is MainWindow main)
            return await main.TryExtractPageJsonAsync(tabId);
        return null;
    }

    private async Task<string?> ExtractRemotePageJsonAsync(string pageUrl)
    {
        var tabId = (DataContext as AiFunctionViewModel)?.ActiveTabId
            ?? App.ServiceProvider?.GetService<AiFunctionViewModel>()?.ActiveTabId;
        if (string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(pageUrl))
            return null;
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        foreach (var window in desktop.Windows.OfType<MainWindow>())
        {
            var json = await window.TryExtractRemotePageJsonAsync(tabId, pageUrl);
            if (!string.IsNullOrWhiteSpace(json))
                return json;
        }
        if (desktop.MainWindow is MainWindow main)
            return await main.TryExtractRemotePageJsonAsync(tabId, pageUrl);
        return null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is AiFunctionViewModel vm)
        {
            if (ReferenceEquals(vm.MessagesLayoutChanged, (Action)ScrollChatToEnd))
                vm.MessagesLayoutChanged = null;
            vm.PickImageFilesAsync = null;
        }
        if (_hostWindow != null)
            _hostWindow.PropertyChanged -= OnHostPropertyChanged;
        _hostWindow = null;
        _llamaIdleTimer.Stop();
        DisposeCompanionBrowser();
        base.OnDetachedFromVisualTree(e);
    }

    private void ScrollChatToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ChatList.ItemCount > 0)
                ChatList.ScrollIntoView(ChatList.ItemCount - 1);
            var viewer = ChatList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            viewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void OnViewPasteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsPasteChord(e) || ShouldLetNativeTextPaste())
            return;
        e.Handled = true;
        _ = PasteIntoComposerAsync(MessageInputBox);
    }

    private void MessageInput_PastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _ = PasteIntoComposerAsync(sender as TextBox ?? MessageInputBox);
    }

    private void MessageInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        if (DataContext is not AiFunctionViewModel vm)
            return;
        e.Handled = true;
        if (sender is TextBox boxEnter)
            vm.MessageInput = boxEnter.Text ?? string.Empty;
        if (vm.SendMessageCommand.CanExecute(null))
            vm.SendMessageCommand.Execute(null);
    }

    private static bool IsPasteChord(KeyEventArgs e)
    {
        if (e.Key != Key.V)
            return false;
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
            return false;
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var meta = (e.KeyModifiers & KeyModifiers.Meta) != 0;
        return ctrl ^ meta;
    }

    private bool ShouldLetNativeTextPaste()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox box && !ReferenceEquals(box, MessageInputBox);
    }

    private async Task PasteIntoComposerAsync(TextBox? box)
    {
        if (_pasteBusy)
            return;
        _pasteBusy = true;
        try
        {
            if (await TryPasteClipboardImagesAsync())
            {
                MessageInputBox?.Focus();
                return;
            }
            await PasteClipboardTextAsync(box ?? MessageInputBox);
        }
        finally
        {
            _pasteBusy = false;
        }
    }

    private void Composer_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ComposerHasAttachableImages(e.DataTransfer)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Composer_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not AiFunctionViewModel vm)
            return;
        var attached = await AttachFromDataTransferAsync(vm, e.DataTransfer);
        if (attached)
            MessageInputBox?.Focus();
    }

    private async Task<IReadOnlyList<string>?> PickImageFilesAsync()
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        var share = host as AiFunctionWindow;
        if (share != null)
            share.SuppressHideOnDeactivate = true;
        try
        {
            var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach images",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"],
                        MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp"]
                    }
                ]
            });
            if (files == null || files.Count == 0)
                return null;

            var paths = new List<string>();
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
                else if (DataContext is AiFunctionViewModel vm)
                    await AttachStorageFileAsync(vm, file);
            }
            return paths;
        }
        finally
        {
            RestoreHostAfterPicker(share);
            MessageInputBox?.Focus();
        }
    }

    private static void RestoreHostAfterPicker(AiFunctionWindow? share)
    {
        if (share == null || share.ForceClose)
            return;
        share.Show();
        share.Activate();
        Dispatcher.UIThread.Post(() =>
        {
            share.SuppressHideOnDeactivate = false;
        }, DispatcherPriority.Background);
    }

    private async Task<bool> TryPasteClipboardImagesAsync()
    {
        if (DataContext is not AiFunctionViewModel vm)
            return false;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return false;

        try
        {
            using var transfer = await clipboard.TryGetDataAsync();
            if (transfer != null && await AttachFromAsyncTransferAsync(vm, transfer))
                return true;
        }
        catch { /* offer expired or format not offered */ }

        return false;
    }

    private static async Task<bool> AttachFromAsyncTransferAsync(AiFunctionViewModel vm, IAsyncDataTransfer transfer)
    {
        try
        {
            var bitmap = await transfer.TryGetBitmapAsync();
            if (bitmap != null)
            {
                try
                {
                    return AttachBitmap(vm, bitmap);
                }
                finally
                {
                    bitmap.Dispose();
                }
            }
        }
        catch { /* format not offered */ }

        foreach (var format in transfer.Formats)
        {
            if (format is not DataFormat<byte[]> bytesFormat)
                continue;
            if (!IsLikelyImageFormat(bytesFormat.Identifier))
                continue;
            try
            {
                var bytes = await transfer.TryGetValueAsync(bytesFormat);
                if (bytes is { Length: > 0 } &&
                    vm.TryAddPendingImageBytes(bytes, ExtensionFromImageBytes(bytes, bytesFormat.Identifier)))
                    return true;
            }
            catch { /* format not offered */ }
        }

        try
        {
            var files = await transfer.TryGetFilesAsync();
            if (files != null && await AttachStorageItemsAsync(vm, files))
                return true;
        }
        catch { /* format not offered */ }

        try
        {
            var text = await transfer.TryGetTextAsync();
            if (TryAttachImagePathText(vm, text))
                return true;
        }
        catch { /* format not offered */ }

        return false;
    }

    private static bool IsLikelyImageFormat(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return false;
        var id = identifier.Trim();
        if (id.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return true;
        return id.Equals("PNG", StringComparison.OrdinalIgnoreCase)
            || id.Equals("JFIF", StringComparison.OrdinalIgnoreCase)
            || id.Equals("JPEG", StringComparison.OrdinalIgnoreCase)
            || id.Equals("JPG", StringComparison.OrdinalIgnoreCase)
            || id.Equals("BMP", StringComparison.OrdinalIgnoreCase)
            || id.Equals("GIF", StringComparison.OrdinalIgnoreCase)
            || id.Equals("WEBP", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtensionFromImageBytes(byte[] bytes, string? formatHint)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            return ".gif";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return ".webp";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return ".bmp";

        var hint = (formatHint ?? "").Trim().ToLowerInvariant();
        if (hint.Contains("jpeg") || hint.Contains("jpg") || hint == "jfif")
            return ".jpg";
        if (hint.Contains("webp"))
            return ".webp";
        if (hint.Contains("gif"))
            return ".gif";
        if (hint.Contains("bmp"))
            return ".bmp";
        return ".png";
    }

    private static bool TryAttachImagePathText(AiFunctionViewModel vm, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim();
        if (text.IndexOfAny(['\r', '\n']) >= 0)
            return false;
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.IsFile)
        {
            try
            {
                text = Uri.UnescapeDataString(uri.LocalPath);
            }
            catch
            {
                return false;
            }
        }
        var ext = Path.GetExtension(text).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"))
            return false;
        return vm.TryAddPendingImageFile(text);
    }

    private async Task PasteClipboardTextAsync(TextBox? box)
    {
        if (box == null)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(text))
            return;
        InsertText(box, text);
        if (DataContext is AiFunctionViewModel vm)
            vm.MessageInput = box.Text ?? string.Empty;
    }

    private static void InsertText(TextBox box, string text)
    {
        var current = box.Text ?? "";
        var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, current.Length);
        box.Text = current[..start] + text + current[end..];
        var caret = start + text.Length;
        box.CaretIndex = caret;
        box.SelectionStart = caret;
        box.SelectionEnd = caret;
    }

    private static bool ComposerHasAttachableImages(IDataTransfer? transfer)
    {
        if (transfer == null)
            return false;
        var files = transfer.TryGetFiles();
        if (files != null && files.Any(LooksLikeImage))
            return true;
        return transfer.TryGetBitmap() != null;
    }

    private static async Task<bool> AttachFromDataTransferAsync(AiFunctionViewModel vm, IDataTransfer? transfer)
    {
        if (transfer == null)
            return false;
        var files = transfer.TryGetFiles();
        if (files != null && await AttachStorageItemsAsync(vm, files))
            return true;
        var bitmap = transfer.TryGetBitmap();
        return bitmap != null && AttachBitmap(vm, bitmap);
    }

    private static async Task<bool> AttachStorageItemsAsync(AiFunctionViewModel vm, IEnumerable<IStorageItem> items)
    {
        var any = false;
        foreach (var item in items)
        {
            if (item is IStorageFile file)
            {
                if (await AttachStorageFileAsync(vm, file))
                    any = true;
            }
            else
            {
                var path = item.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path) && vm.TryAddPendingImageFile(path))
                    any = true;
            }
        }
        return any;
    }

    private static async Task<bool> AttachStorageFileAsync(AiFunctionViewModel vm, IStorageFile file)
    {
        var path = file.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && vm.TryAddPendingImageFile(path))
            return true;
        if (!LooksLikeImage(file))
            return false;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return vm.TryAddPendingImageBytes(ms.ToArray(), Path.GetExtension(file.Name));
        }
        catch
        {
            return false;
        }
    }

    private static bool AttachBitmap(AiFunctionViewModel vm, Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return vm.TryAddPendingImageBytes(ms.ToArray(), ".png");
    }

    private static bool LooksLikeImage(IStorageItem item)
    {
        var name = item.TryGetLocalPath() ?? item.Name ?? "";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
    }

    private async Task<bool> SaveConversationFileAsync(string suggestedName, string contents)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return false;

        var share = host as AiFunctionWindow;
        if (share != null)
            share.SuppressHideOnDeactivate = true;
        try
        {
            var file = await host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save conversation",
                SuggestedFileName = suggestedName,
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text") { Patterns = ["*.txt"] }
                ]
            });
            if (file == null)
                return false;

            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                await File.WriteAllTextAsync(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return true;
            }

            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false))
                await writer.WriteAsync(contents);
            return true;
        }
        finally
        {
            if (share != null && !share.ForceClose)
            {
                share.Show();
                share.Activate();
                Dispatcher.UIThread.Post(() =>
                {
                    share.SuppressHideOnDeactivate = false;
                }, DispatcherPriority.Background);
            }
        }
    }

    private async Task<bool> EnsureVaultAsync()
    {
        var auth = App.ServiceProvider?.GetService<PasswordVaultAuthenticationService>();
        var host = TopLevel.GetTopLevel(this) as Window;
        if (auth == null || host == null)
            return false;

        var share = host as AiFunctionWindow;
        if (share != null)
            share.SuppressHideOnDeactivate = true;
        try
        {
            return await auth.EnsureReadyForSaveAsync(host);
        }
        finally
        {
            if (share != null && !share.ForceClose)
            {
                share.Show();
                share.Activate();
                Dispatcher.UIThread.Post(() =>
                {
                    share.SuppressHideOnDeactivate = false;
                }, DispatcherPriority.Background);
            }
        }
    }

    private void EnsureCompanionBrowser()
    {
        var host = CompanionHost;
        if (host == null || _companionBrowser != null)
            return;
        if (host.Bounds.Width < 8 || host.Bounds.Height < 8)
        {
            if (_companionLayoutHooked)
                return;
            _companionLayoutHooked = true;
            host.LayoutUpdated += OnCompanionHostLayoutUpdated;
            return;
        }

        CreateCompanionBrowser(host);
    }

    private void OnCompanionHostLayoutUpdated(object? sender, System.EventArgs e)
    {
        var host = CompanionHost;
        if (host == null || _companionBrowser != null)
            return;
        if (host.Bounds.Width < 8 || host.Bounds.Height < 8)
            return;
        host.LayoutUpdated -= OnCompanionHostLayoutUpdated;
        _companionLayoutHooked = false;
        CreateCompanionBrowser(host);
    }

    private void CreateCompanionBrowser(Border host)
    {
        if (_companionBrowser != null)
            return;
        var browser = new AvaloniaCefBrowser { Address = BlankPage.InternalUrl + "?ai=1" };
        browser.Settings.WindowlessFrameRate = 30;
        browser.ContextMenuHandler = new SilentContextMenuHandler();
        browser.ConsoleMessage += (_, message) =>
        {
            Program.StartupLog.Write($"[AI Function] companion {message.Level} line={message.Line}: {message.Message}");
        };
        _companionBrowser = browser;
        host.Child = browser;
    }

    public void ReloadCompanionLighting()
    {
        void Run()
        {
            try
            {
                _companionBrowser?.ExecuteJavaScript("window.reloadCompanionLighting&&window.reloadCompanionLighting()");
            }
            catch { /* companion page may still be loading */ }
        }
        Run();
        Dispatcher.UIThread.Post(Run, DispatcherPriority.Background);
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(250);
            Run();
        });
    }

    private void DisposeCompanionBrowser()
    {
        var host = CompanionHost;
        if (host != null && _companionLayoutHooked)
        {
            host.LayoutUpdated -= OnCompanionHostLayoutUpdated;
            _companionLayoutHooked = false;
        }
        var browser = _companionBrowser;
        _companionBrowser = null;
        if (host != null && ReferenceEquals(host.Child, browser))
            host.Child = null;
        browser?.Dispose();
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != IsVisibleProperty)
            return;
        var visible = change.NewValue is true;
        SyncWindowVisible(visible);
        if (visible)
            Dispatcher.UIThread.Post(ReloadCompanionLighting, DispatcherPriority.Background);
    }

    private void OnLlamaIdleTick(object? sender, EventArgs e)
    {
        if (DataContext is AiFunctionViewModel vm)
            _ = vm.StopIdleLocalServerIfDueAsync();
    }

    private void SyncWindowVisible(bool? visible = null)
    {
        if (DataContext is not AiFunctionViewModel vm)
            return;
        vm.NotifyWindowVisible(visible ?? _hostWindow?.IsVisible == true);
    }

    private sealed class SilentContextMenuHandler : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
        {
            model.Clear();
        }
    }
}
