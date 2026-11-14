using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NexZen.Application.Services;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Mirror;

namespace NexZen.Desktop.Mirror;

/// <summary>"NexZen Mirror": shows the screen of the connected phone (phase 1, view only).</summary>
public sealed class MirrorWindow : Window
{
    private readonly PhoneMirrorClient _client;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _sas = new()
    {
        FontSize = 40, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, LetterSpacing = 6, IsVisible = false,
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 15, Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")), TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 340,
    };
    private readonly Border _overlay;
    private readonly object _frameLock = new();
    private readonly object _decodeLock = new();
    private LibAvH264Decoder? _decoder;
    private string? _decoderError;
    private int _frameW, _frameH;
    private WriteableBitmap? _bitmap;
    private byte[]? _pendingFrame;
    private int _uiQueued;
    private bool _needKeyFrame = true;
    private readonly Border _toolbar;
    private readonly Border _banner;
    private readonly TextBlock _bannerText = new() { Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private bool _dimmed;
    private bool _pointerDown;
    // Typing goes through a hidden text box so the system input method (fcitx5: Lotus, Unikey…)
    // works; what changes in it is sent to the phone as backspaces + new text.
    private readonly TextBox _ime = new()
    {
        Opacity = 0, Width = 1, Height = 1, IsHitTestVisible = false, AcceptsReturn = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };
    private string _imeSent = string.Empty;
    private bool _imeResetting;
    private Avalonia.Controls.Presenters.TextPresenter? _imePresenter;
    private long _lastMoveTicks;
    private bool _sized;

    public MirrorWindow(INexShareOrchestrator orchestrator, INexShareCrypto crypto)
    {
        Title = "NexZen Mirror";
        Width = 420;
        Height = 860;
        MinWidth = 240;
        MinHeight = 320;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var stop = new Button { Content = "Stop", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        stop.Click += (_, _) => Close();
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E0101010")),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
                Margin = new Thickness(24),
                Children = { _sas, _status, stop },
            },
        };
        Button NavButton(string glyph, string tip, string key)
        {
            var b = new Button
            {
                Content = glyph, FontSize = 16, Width = 56, Height = 34, Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
                Foreground = Brushes.White,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => SendKey(key);
            return b;
        }
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#202020")),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 24,
                Children = { NavButton("◀", "Back (right click, Esc)", "back"), NavButton("●", "Home (middle click)", "home"),
                             NavButton("■", "Recent apps", "recents"), DimButton() },
            },
        };
        _banner = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#D0303030")), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8), Margin = new Thickness(12), IsVisible = false, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Child = _bannerText,
        };
        var screen = new Grid { Children = { _ime, _image, _banner, _overlay }, Background = Brushes.Black, Focusable = true };
        _ime.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) OnImeTextChanged();
        };
        // Input methods that compose a word before committing it (Lotus, Unikey with preedit) keep
        // it in the presenter's preedit text; send that too, so letters appear while typing.
        _ime.TemplateApplied += (_, e) =>
        {
            _imePresenter = e.NameScope.Find<Avalonia.Controls.Presenters.TextPresenter>("PART_TextPresenter");
            if (_imePresenter != null)
                _imePresenter.PropertyChanged += (_, pe) =>
                {
                    if (pe.Property == Avalonia.Controls.Presenters.TextPresenter.PreeditTextProperty) OnImeTextChanged();
                };
        };
        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Bottom);
        root.Children.Add(_toolbar);
        root.Children.Add(screen);
        Content = root;

        _image.PointerPressed += OnPointerPressed;
        _image.PointerMoved += OnPointerMoved;
        _image.PointerReleased += OnPointerReleased;
        _image.PointerWheelChanged += OnWheel;
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Activated += (_, _) => FocusTyping();

        _client = new PhoneMirrorClient(orchestrator, crypto);
        _client.StateChanged += (state, message) => Dispatcher.UIThread.Post(() => OnState(state, message));
        _client.ConfigReceived += config => Dispatcher.UIThread.Post(() => OnConfig(config));
        _client.VideoReceived += OnVideo;
        _client.PhoneStatusChanged += (screenOn, locked) => Dispatcher.UIThread.Post(() =>
        {
            _bannerText.Text = !screenOn
                ? (_client.CanControl ? "The phone screen is off. Click or press a key to turn it on." : "The phone screen is off.")
                : locked ? "Unlock the phone to continue." : string.Empty;
            _banner.IsVisible = _bannerText.Text.Length > 0;
        });
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _decoder = LibAvH264Decoder.TryCreate(out _decoderError);
        if (_decoder == null)
        {
            OnState(PhoneMirrorState.Ended, "FFmpeg was not found (" + _decoderError + "). Install ffmpeg to show the phone screen.");
            return;
        }
        await _client.StartAsync(Environment.MachineName);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _ = _client.StopAsync();
        _client.Dispose();
        lock (_decodeLock)
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }

    private void OnState(PhoneMirrorState state, string message)
    {
        _status.Text = message;
        _sas.IsVisible = state == PhoneMirrorState.AwaitingApproval && _client.Sas != null;
        if (_sas.IsVisible)
        {
            _sas.Text = _client.Sas!.Insert(3, " ");
            _status.Text = "Check that the phone shows the same code, then tap Allow on the phone.";
        }
        _overlay.IsVisible = state != PhoneMirrorState.Streaming;

    }

    private void OnConfig(MirrorStreamConfig config)
    {
        if (config.Codec != "h264") return;
        _toolbar.IsVisible = _client.CanControl;
        FocusTyping();
        _status.Text = _client.CanControl ? "" : _status.Text;
        _needKeyFrame = true;
        _ = _client.RequestKeyFrameAsync();
        FitToVideo(config.Width, config.Height);
    }

    /// <summary>Transport thread: decode right away (starting at a key frame) and hand the picture to the UI.</summary>
    private void OnVideo(byte[] accessUnit, bool keyFrame)
    {
        lock (_decodeLock)
        {
            var decoder = _decoder;
            if (decoder == null) return;
            if (_needKeyFrame)
            {
                if (!keyFrame) return;
                _needKeyFrame = false;
            }
            if (!decoder.Decode(accessUnit, OnDecoded))
            {
                _needKeyFrame = true; // resynchronise on the next key frame
                _ = _client.RequestKeyFrameAsync();
            }
        }
    }

    /// <summary>Keep only the newest picture and let the UI thread copy it.</summary>
    private void OnDecoded(byte[] frame, int width, int height)
    {
        lock (_frameLock)
        {
            if (_pendingFrame == null || _pendingFrame.Length != frame.Length)
                _pendingFrame = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, _pendingFrame, 0, frame.Length);
            _frameW = width;
            _frameH = height;
        }
        if (Interlocked.Exchange(ref _uiQueued, 1) == 0)
            Dispatcher.UIThread.Post(ShowFrame, DispatcherPriority.Render);
    }

    private void ShowFrame()
    {
        Interlocked.Exchange(ref _uiQueued, 0);
        lock (_frameLock)
        {
            if (_pendingFrame == null || _frameW <= 0) return;
            if (_bitmap == null || _bitmap.PixelSize.Width != _frameW || _bitmap.PixelSize.Height != _frameH)
            {
                _bitmap = new WriteableBitmap(new PixelSize(_frameW, _frameH), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _image.Source = _bitmap;
            }
            using var fb = _bitmap.Lock();
            if (fb.RowBytes == _frameW * 4)
            {
                Marshal.Copy(_pendingFrame, 0, fb.Address, _pendingFrame.Length);
            }
            else
            {
                for (var y = 0; y < _frameH; y++)
                    Marshal.Copy(_pendingFrame, y * _frameW * 4, fb.Address + y * fb.RowBytes, _frameW * 4);
            }
        }
        _image.InvalidateVisual();
    }

    private Button DimButton()
    {
        var b = new Button
        {
            Content = "🔅", FontSize = 16, Width = 56, Height = 34, Focusable = false, Background = Brushes.Transparent,
            Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(b, "Dim the phone screen while mirroring");
        b.Click += (_, _) =>
        {
            _dimmed = !_dimmed;
            SendKey(_dimmed ? "dim" : "undim");
            b.Content = _dimmed ? "🔆" : "🔅";
            ToolTip.SetTip(b, _dimmed ? "Restore the phone's brightness" : "Dim the phone screen while mirroring");
        };
        return b;
    }

    // ---- control (phase 2) ----------------------------------------------------------------------

    /// <summary>Pointer position as fractions of the picture (Stretch.Uniform leaves bars around it).</summary>
    private (double X, double Y)? ToPicture(Point p)
    {
        if (_bitmap == null) return null;
        double vw = _bitmap.PixelSize.Width, vh = _bitmap.PixelSize.Height;
        var b = _image.Bounds;
        var scale = Math.Min(b.Width / vw, b.Height / vh);
        if (scale <= 0) return null;
        var ox = (b.Width - vw * scale) / 2;
        var oy = (b.Height - vh * scale) / 2;
        var x = (p.X - ox) / (vw * scale);
        var y = (p.Y - oy) / (vh * scale);
        return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    private void Send(MirrorInputEvent e) => _ = _client.SendInputAsync(e);

    private void SendKey(string key) => Send(new MirrorInputEvent { Type = "key", Key = key });

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_client.CanControl) return;
        var props = e.GetCurrentPoint(_image).Properties;
        if (props.IsRightButtonPressed) { SendKey("back"); e.Handled = true; return; }
        if (props.IsMiddleButtonPressed) { SendKey("home"); e.Handled = true; return; }
        if (!props.IsLeftButtonPressed || ToPicture(e.GetPosition(_image)) is not { } pos) return;
        _pointerDown = true;
        e.Pointer.Capture(_image);
        ResetTyping(); // a tap usually moves the cursor on the phone
        FocusTyping();
        Send(new MirrorInputEvent { Type = "down", X = pos.X, Y = pos.Y });
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown || ToPicture(e.GetPosition(_image)) is not { } pos) return;
        var now = Environment.TickCount64;
        if (now - _lastMoveTicks < 16) return; // ~60 moves per second is plenty for a gesture path
        _lastMoveTicks = now;
        Send(new MirrorInputEvent { Type = "move", X = pos.X, Y = pos.Y });
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        e.Pointer.Capture(null);
        if (ToPicture(e.GetPosition(_image)) is { } pos)
            Send(new MirrorInputEvent { Type = "up", X = pos.X, Y = pos.Y });
        e.Handled = true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_client.CanControl || ToPicture(e.GetPosition(_image)) is not { } pos) return;
        // Wheel down (negative delta) shows what is below, like a finger swiping up.
        Send(new MirrorInputEvent { Type = "scroll", X = pos.X, Y = pos.Y, Dy = -e.Delta.Y });
        e.Handled = true;
    }

    private void FocusTyping()
    {
        if (_client.CanControl) _ime.Focus();
    }

    /// <summary>Forget what the input method may still edit (after Enter, a tap, or a long word).</summary>
    private void ResetTyping()
    {
        _imeResetting = true;
        _ime.Text = string.Empty;
        _imeResetting = false;
        _imeSent = string.Empty;
    }

    private void OnImeTextChanged()
    {
        if (_imeResetting) return;
        var now = (_ime.Text ?? string.Empty) + (_imePresenter?.PreeditText ?? string.Empty);
        var common = 0;
        while (common < now.Length && common < _imeSent.Length && now[common] == _imeSent[common]) common++;
        // The phone deletes one code point per backspace.
        if (common > 0 && char.IsHighSurrogate(now.Length > common - 1 ? now[common - 1] : '\0')) common--;
        var removed = 0;
        foreach (var _ in _imeSent[common..].EnumerateRunes()) removed++;
        if (_client.CanControl)
        {
            for (var k = 0; k < removed; k++) SendKey("backspace");
            if (now.Length > common) Send(new MirrorInputEvent { Type = "text", Text = now[common..] });
        }
        _imeSent = now;
        if (now.Length > 48 && now.EndsWith(' ') && string.IsNullOrEmpty(_imePresenter?.PreeditText)) ResetTyping();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_client.CanControl) return;
        switch (e.Key)
        {
            case Key.Escape:
                SendKey("back");
                break;
            case Key.Enter:
                SendKey("enter");
                ResetTyping();
                break;
            case Key.Delete:
                SendKey("delete");
                break;
            case Key.Back when string.IsNullOrEmpty(_ime.Text) && string.IsNullOrEmpty(_imePresenter?.PreeditText):
                SendKey("backspace"); // nothing local to delete: delete on the phone
                break;
            default:
                return; // letters and Backspace inside the current word go through the text box / IME
        }
        e.Handled = true;
    }

    private void FitToVideo(int width, int height)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var scale = screen.Scaling;
        var maxH = screen.WorkingArea.Height / scale * 0.88;
        var maxW = screen.WorkingArea.Width / scale * 0.88;
        var f = Math.Min(maxH / height, maxW / width);
        var newW = Math.Round(width * f);
        var newH = Math.Round(height * f);
        if (!_sized || Math.Abs(Width / Height - newW / newH) > 0.01)
        {
            Width = newW;
            Height = newH;
            _sized = true;
        }
    }
}
