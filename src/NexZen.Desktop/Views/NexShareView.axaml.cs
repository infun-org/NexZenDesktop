using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexZen.Application.ViewModels;
using NexZen.Desktop.Helpers;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Views;

public partial class NexShareView : UserControl
{
    private NexShareViewModel? _chatVm;
    private ScrollViewer? _chatScroll;
    private Window? _hostWindow;
    private readonly HashSet<int> _seenMessages = new();
    private bool _stickToBottom = true;
    private bool _suppressStick;
    private bool _pinPosted;
    private bool _jumpToLatest;
    private AvaloniaCefBrowser? _companionBrowser;
    private bool _companionLayoutHooked;

    public NexShareView()
    {
        InitializeComponent();
        ChatList.TemplateApplied += OnChatTemplateApplied;
        Loaded += (_, _) => EnsureCompanionBrowser();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_chatVm != null)
            _chatVm.ChatMessages.CollectionChanged -= OnChatMessagesChanged;

        if (DataContext is NexShareViewModel vm)
        {
            vm.UiInvoker = action => Dispatcher.UIThread.Post(action);
            _chatVm = vm;
            _seenMessages.Clear();
            _stickToBottom = true;
            vm.ChatMessages.CollectionChanged += OnChatMessagesChanged;
            FollowLatest();
        }
        else
        {
            _chatVm = null;
            HideNewMessages();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachScrollViewer();
        EnsureCompanionBrowser();
        if (VisualRoot is Window window && !ReferenceEquals(window, _hostWindow))
        {
            if (_hostWindow != null)
                _hostWindow.PropertyChanged -= OnHostPropertyChanged;
            _hostWindow = window;
            _hostWindow.PropertyChanged += OnHostPropertyChanged;
        }
        if (_stickToBottom)
            FollowLatest();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_hostWindow != null)
            _hostWindow.PropertyChanged -= OnHostPropertyChanged;
        _hostWindow = null;
        if (_chatScroll != null)
            _chatScroll.ScrollChanged -= OnChatScrollChanged;
        _chatScroll = null;
        DisposeCompanionBrowser();
        base.OnDetachedFromVisualTree(e);
    }

    private void OpenCompanion_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Avalonia.Application.Current is NexZen.Desktop.App app)
            app.ShowCompanionStudio();
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
        var browser = new AvaloniaCefBrowser { Address = BlankPage.InternalUrl + "?embed=1" };
        browser.Settings.WindowlessFrameRate = 30;
        browser.ContextMenuHandler = new SilentContextMenuHandler();
        browser.ConsoleMessage += (_, message) =>
        {
            NexZen.Desktop.Program.StartupLog.Write($"[NexShare] companion {message.Level} line={message.Line}: {message.Message}");
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
            await System.Threading.Tasks.Task.Delay(250);
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

    private sealed class SilentContextMenuHandler : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
        {
            model.Clear();
        }
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty && change.NewValue is true)
        {
            if (_stickToBottom)
                FollowLatest();
            Dispatcher.UIThread.Post(ReloadCompanionLighting, DispatcherPriority.Background);
        }
    }

    private void OnChatTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        AttachScrollViewer();
        if (_stickToBottom)
            FollowLatest();
    }

    private void OnChatMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _seenMessages.Clear();
            _stickToBottom = true;
            FollowLatest();
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            var mine = false;
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    if (item is ChatMessageItem msg && msg.IsMine)
                        mine = true;
                }
            }

            if (mine || _stickToBottom)
            {
                _stickToBottom = true;
                FollowLatest();
            }
            else
            {
                Dispatcher.UIThread.Post(UpdateUnread, DispatcherPriority.Background);
            }
            return;
        }

        Dispatcher.UIThread.Post(UpdateUnread, DispatcherPriority.Background);
    }

    private void FollowLatest()
    {
        var messages = _chatVm?.ChatMessages;
        if (messages == null || messages.Count == 0)
        {
            HideNewMessages();
            return;
        }

        _stickToBottom = true;
        _suppressStick = true;
        ChatList.ScrollIntoView(messages[^1]);
        _suppressStick = false;
        EnsurePinned();
    }

    private void EnsurePinned()
    {
        if (!_stickToBottom || _pinPosted)
            return;
        _pinPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pinPosted = false;
            if (!_stickToBottom)
                return;
            var sv = ChatScroll;
            if (sv == null || sv.Viewport.Height <= 1)
                return;
            var max = System.Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (System.Math.Abs(sv.Offset.Y - max) > 1)
            {
                _suppressStick = true;
                sv.Offset = new Vector(0, max);
                _suppressStick = false;
            }
            UpdateUnread();
        }, DispatcherPriority.Background);
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.Viewport.Height <= 1 || _suppressStick)
            return;

        var extentChanged = System.Math.Abs(e.ExtentDelta.Y) > 0.5
            || System.Math.Abs(e.ViewportDelta.Y) > 0.5;
        var userMoved = System.Math.Abs(e.OffsetDelta.Y) > 0.5 && !extentChanged;
        if (userMoved)
            _stickToBottom = IsAtBottom(sv);
        else if (_stickToBottom && !IsAtBottom(sv))
        {
            EnsurePinned();
            return;
        }

        UpdateUnread();
    }

    private void UpdateUnread()
    {
        var messages = _chatVm?.ChatMessages;
        var sv = ChatScroll;
        if (messages == null || messages.Count == 0 || sv == null || sv.Viewport.Height <= 1)
        {
            HideNewMessages();
            return;
        }

        int? firstVisible = null;
        int? lastVisible = null;
        for (var i = 0; i < messages.Count; i++)
        {
            if (ChatList.ContainerFromIndex(i) is not Control container)
                continue;
            if (!IntersectsViewport(container, sv))
                continue;
            _seenMessages.Add(i);
            firstVisible ??= i;
            lastVisible = i;
        }

        if (firstVisible == null || lastVisible == null)
            return;

        var above = 0;
        for (var i = 0; i < firstVisible.Value; i++)
        {
            if (!_seenMessages.Contains(i))
                above++;
        }

        var below = 0;
        for (var i = lastVisible.Value + 1; i < messages.Count; i++)
        {
            if (!_seenMessages.Contains(i))
                below++;
        }

        if (below > 0 && !_stickToBottom)
            ShowNewMessages(below, jumpToLatest: true);
        else if (above > 0)
            ShowNewMessages(above, jumpToLatest: false);
        else
            HideNewMessages();
    }

    private static bool IntersectsViewport(Control container, ScrollViewer sv)
    {
        var point = container.TranslatePoint(new Point(0, 0), sv);
        if (point == null)
            return false;
        var top = point.Value.Y;
        var bottom = top + container.Bounds.Height;
        return bottom > 1 && top < sv.Viewport.Height - 1;
    }

    private static bool IsAtBottom(ScrollViewer sv)
    {
        var max = sv.Extent.Height - sv.Viewport.Height;
        if (max <= 1)
            return true;
        return max - sv.Offset.Y <= 24;
    }

    private ScrollViewer? ChatScroll
    {
        get
        {
            if (_chatScroll == null)
                AttachScrollViewer();
            return _chatScroll;
        }
    }

    private void AttachScrollViewer()
    {
        var sv = ChatList.FindDescendantOfType<ScrollViewer>();
        if (sv == null || ReferenceEquals(sv, _chatScroll))
            return;
        if (_chatScroll != null)
            _chatScroll.ScrollChanged -= OnChatScrollChanged;
        _chatScroll = sv;
        _chatScroll.ScrollChanged += OnChatScrollChanged;
    }

    private void ShowNewMessages(int count, bool jumpToLatest)
    {
        _jumpToLatest = jumpToLatest;
        ChatNewMessagesButton.Content = count == 1 ? "1 new message" : $"{count} new messages";
        ChatNewMessagesButton.VerticalAlignment = jumpToLatest
            ? Avalonia.Layout.VerticalAlignment.Bottom
            : Avalonia.Layout.VerticalAlignment.Top;
        ChatNewMessagesButton.IsVisible = true;
    }

    private void HideNewMessages()
    {
        ChatNewMessagesButton.IsVisible = false;
    }

    private void ChatNewMessagesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_jumpToLatest)
        {
            _stickToBottom = true;
            FollowLatest();
            return;
        }

        var messages = _chatVm?.ChatMessages;
        if (messages == null)
            return;

        var target = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (_seenMessages.Contains(i))
                continue;
            target = i;
            break;
        }

        if (target < 0)
        {
            HideNewMessages();
            return;
        }

        _stickToBottom = false;
        _suppressStick = true;
        ChatList.ScrollIntoView(messages[target]);
        _suppressStick = false;
        Dispatcher.UIThread.Post(UpdateUnread, DispatcherPriority.Background);
    }

    private void DeviceLabel_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not NexShareViewModel vm)
            return;
        e.Handled = true;
        vm.SaveDeviceLabelCommand.Execute(null);
    }

    private void MessageInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
            return;
        if (DataContext is not NexShareViewModel vm)
            return;
        e.Handled = true;
        if (sender is TextBox box)
            vm.MessageInput = box.Text ?? string.Empty;
        vm.SendMessageCommand.Execute(null);
    }

    private async void MirrorPhone_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not NexShareViewModel vm)
            return;
        if (!await vm.EnsurePeerForSendAsync())
            return;
        if (App.ServiceProvider?.GetService(typeof(NexZen.Core.NexShare.Interfaces.INexShareCrypto))
            is not NexZen.Core.NexShare.Interfaces.INexShareCrypto crypto)
            return;
        new NexZen.Desktop.Mirror.MirrorWindow(vm.Orchestrator, crypto).Show();
    }

    private async void OfferFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not NexShareViewModel vm)
            return;
        if (!await vm.EnsurePeerForSendAsync())
            return;

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;

        var share = host as NexShareWindow;
        if (share != null)
            share.SuppressHideOnDeactivate = true;
        try
        {
            var files = await host.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select File to Send",
                AllowMultiple = false
            });

            if (files != null && files.Count > 0)
            {
                var filePath = files[0].Path.LocalPath;
                await vm.SendFileAsync(filePath);
            }
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
}
