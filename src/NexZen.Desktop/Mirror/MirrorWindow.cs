using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly StackPanel _controlButtons;
    private readonly object _audioLock = new();
    private LibAvAudioDecoder? _audioDecoder;
    private PulseAudioPlayer? _audioPlayer;
    private bool _audioMuted;
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

    private readonly INexShareOrchestrator _orchestrator;
    private readonly TextBlock _routeLabel = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#9AA0A6")), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };

    public MirrorWindow(INexShareOrchestrator orchestrator, INexShareCrypto crypto)
    {
        _orchestrator = orchestrator;
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
                Content = glyph, FontSize = 16, Width = 44, Height = 34, Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, Background = Brushes.Transparent,
                Foreground = Brushes.White,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => SendKey(key);
            return b;
        }
        _controlButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { NavButton("◀", "Back (right click, Esc)", "back"), NavButton("●", "Home (middle click)", "home"),
                         NavButton("■", "Recent apps", "recents"), DimButton(), PhoneSpeakerButton() },
        };
        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#202020")),
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4,
                Children = { _controlButtons, MuteButton(), QualityButton() },
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
        _client.AudioConfigReceived += OnAudioConfig;
        _client.AudioReceived += OnAudio;
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
        _route = await ChooseRouteAsync();
        _routeLabel.Text = _route;
        UpdateTitle();
        await _client.StartAsync(Environment.MachineName);
    }

    /// <summary>
    /// USB first: with a phone attached over USB debugging, NexShare reconnects through an adb tunnel
    /// (much more bandwidth, less delay; the phone raises the quality for it). Otherwise the current
    /// LAN / Wi-Fi Direct connection is used.
    /// </summary>
    private async Task<string> ChooseRouteAsync()
    {
        _status.Text = "Looking for a phone on USB...";
        var tunnel = await System.Threading.Tasks.Task.Run(AdbTunnel.OpenAsync);
        if (tunnel == null)
        {
            Program.StartupLog.Write("[Mirror] no USB phone (adb=" + (AdbTunnel.FindAdb() ?? "not found") + "): using Wi-Fi");
            return "Wi-Fi";
        }
        _status.Text = "Connecting over USB...";
        if (!await _orchestrator.ConnectOverTunnelAsync(tunnel.Value.LocalPort, "USB"))
        {
            Program.StartupLog.Write($"[Mirror] USB tunnel localhost:{tunnel.Value.LocalPort} did not connect: using Wi-Fi");
            return "Wi-Fi";
        }
        // The phone's id arrives with its HELLO.
        for (var i = 0; i < 40 && string.IsNullOrEmpty(_orchestrator.ConnectedDeviceId); i++)
            await Task.Delay(100);
        Program.StartupLog.Write($"[Mirror] USB tunnel {tunnel.Value.Serial} -> localhost:{tunnel.Value.LocalPort}, peer={_orchestrator.ConnectedDeviceId}");
        return "USB";
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
        lock (_audioLock)
        {
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _audioDecoder?.Dispose();
            _audioDecoder = null;
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
        _toolbar.IsVisible = true;
        if (s_mode != "smooth" && !_qualitySent)
        {
            _qualitySent = true;
            _ = _client.SetQualityAsync(s_mode);
        }
        _controlButtons.IsVisible = _client.CanControl;
        FocusTyping();
        _status.Text = _client.CanControl ? "" : _status.Text;
        _needKeyFrame = true;
        _ = _client.RequestKeyFrameAsync();
        FitToVideo(config.Width, config.Height);
    }

    /// <summary>Transport thread: decode right away (starting at a key frame) and hand the picture to the UI.</summary>
    // Per-5-second statistics in startup.log: frames received / decoded / shown, decode time.
    private int _statRecv, _statDecoded, _statShown;
    private double _statDecodeMs;
    private long _statSince = Environment.TickCount64;

    private void Stats()
    {
        var now = Environment.TickCount64;
        var span = now - Interlocked.Read(ref _statSince);
        if (span < 5000) return;
        Interlocked.Exchange(ref _statSince, now);
        var recv = Interlocked.Exchange(ref _statRecv, 0);
        var dec = Interlocked.Exchange(ref _statDecoded, 0);
        var shown = Interlocked.Exchange(ref _statShown, 0);
        Program.StartupLog.Write($"[Mirror] {Title}: recv {recv * 1000.0 / span:0.0} fps, decoded {dec * 1000.0 / span:0.0} fps, " +
            $"shown {shown * 1000.0 / span:0.0} fps, decode {(_statDecodeMs / Math.Max(1, dec)):0.0} ms, size {_frameW}x{_frameH}");
        _statDecodeMs = 0;
    }

    private void OnVideo(byte[] accessUnit, bool keyFrame)
    {
        Interlocked.Increment(ref _statRecv);
        Stats();
        lock (_decodeLock)
        {
            var decoder = _decoder;
            if (decoder == null) return;
            if (_needKeyFrame)
            {
                if (!keyFrame) return;
                _needKeyFrame = false;
            }
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var ok = decoder.Decode(accessUnit, _sink ??= new BitmapSink(this));
            _statDecodeMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!ok)
            {
                _needKeyFrame = true; // resynchronise on the next key frame
                _ = _client.RequestKeyFrameAsync();
            }
        }
    }

    // Triple buffering: the decoder converts straight into a WriteableBitmap that is neither shown
    // nor waiting to be shown, then hands it to the UI thread, which only swaps Image.Source.
    // (Copying each ~10 MB picture twice on the UI thread limited the display to ~7–17 fps.)
    private sealed class BitmapSink : LibAvH264Decoder.IFrameSink
    {
        private readonly MirrorWindow _w;
        private Avalonia.Platform.ILockedFramebuffer? _locked;
        private int _writing = -1;
        public BitmapSink(MirrorWindow w) => _w = w;

        public bool Begin(int width, int height, out IntPtr destination, out int stride)
        {
            destination = IntPtr.Zero;
            stride = 0;
            lock (_w._swapLock)
            {
                if (_w._buffers[0] == null || _w._buffers[0]!.PixelSize.Width != width || _w._buffers[0]!.PixelSize.Height != height)
                {
                    for (var k = 0; k < 3; k++)
                        _w._buffers[k] = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                            PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    _w._front = -1;
                    _w._pending = -1;
                }
                _writing = 0;
                while (_writing == _w._front || _writing == _w._pending) _writing++;
            }
            _locked = _w._buffers[_writing]!.Lock();
            destination = _locked.Address;
            stride = _locked.RowBytes;
            return true;
        }

        public void End(int width, int height)
        {
            _locked?.Dispose();
            _locked = null;
            lock (_w._swapLock)
                _w._pending = _writing;
            _w._frameW = width;
            _w._frameH = height;
            Interlocked.Increment(ref _w._statDecoded);
            if (Interlocked.Exchange(ref _w._uiQueued, 1) == 0)
                Dispatcher.UIThread.Post(_w.ShowFrame, DispatcherPriority.Render);
        }
    }

    private readonly object _swapLock = new();
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[3];
    private int _front = -1, _pending = -1;
    private BitmapSink? _sink;

    private void ShowFrame()
    {
        Interlocked.Exchange(ref _uiQueued, 0);
        WriteableBitmap? next;
        lock (_swapLock)
        {
            if (_pending < 0) return;
            _front = _pending;
            _pending = -1;
            next = _buffers[_front];
        }
        if (next == null) return;
        _bitmap = next;
        if (!ReferenceEquals(_image.Source, next)) _image.Source = next;
        _image.InvalidateVisual();
        Interlocked.Increment(ref _statShown);
    }

    // ---- audio (phase 3) ------------------------------------------------------------------------

    private void OnAudioConfig(MirrorAudioConfig config)
    {
        lock (_audioLock)
        {
            _audioPlayer?.Dispose();
            _audioDecoder?.Dispose();
            _audioDecoder = LibAvAudioDecoder.TryCreate(new MirrorAudio(config.Codec, config.Rate, config.Channels), out var err);
            _audioPlayer = _audioDecoder == null ? null : PulseAudioPlayer.TryCreate(config.Rate, config.Channels, out err);
            if (_audioPlayer != null) _audioPlayer.Muted = _audioMuted;
            else Console.WriteLine("[Mirror] no phone audio: " + err);
        }
    }

    /// <summary>Transport thread: decode and queue for playback.</summary>
    private void OnAudio(byte[] packet)
    {
        lock (_audioLock)
        {
            var player = _audioPlayer;
            if (player == null) return;
            _audioDecoder?.Decode(packet, player.Enqueue);
        }
    }

    private string _route = "";
    private bool _qualitySent;
    // Quality, remembered for the next mirroring session while NexZen runs.
    private static readonly string[] Modes = { "fast", "smooth", "sharp" };
    private static string s_mode = "smooth";

    private static string Label(string mode) => mode switch { "fast" => "FAST", "sharp" => "HD", _ => "SD" };
    private static string Name(string mode) => mode switch { "fast" => "Fast", "sharp" => "Sharp", _ => "Smooth" };

    /// <summary>The bottom bar is often too narrow for labels; the title shows route and quality.</summary>
    private void UpdateTitle() => Title = $"NexZen Mirror · {_route} · {Name(s_mode)}";

    private Button QualityButton()
    {
        var b = new Button
        {
            Content = Label(s_mode), FontSize = 12, FontWeight = FontWeight.Bold, Width = 50, Height = 34, Padding = new Thickness(0),
            Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        void Tip() => ToolTip.SetTip(b, s_mode switch
        {
            "fast" => "Fast: 1000 px, highest frame rate. Click for Smooth.",
            "sharp" => "Sharp: 1920 px, more bitrate. Click for Fast.",
            _ => "Smooth: 1600 px. Click for Sharp.",
        });
        Tip();
        b.Click += async (_, _) =>
        {
            s_mode = Modes[(Array.IndexOf(Modes, s_mode) + 1) % Modes.Length];
            b.Content = Label(s_mode);
            Tip();
            UpdateTitle();
            await _client.SetQualityAsync(s_mode);
        };
        return b;
    }

    private Button MuteButton()
    {
        var b = new Button
        {
            Content = "🔊", FontSize = 16, Width = 44, Height = 34, Focusable = false, Background = Brushes.Transparent,
            Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(b, "Mute the phone's sound on this computer");
        b.Click += (_, _) =>
        {
            _audioMuted = !_audioMuted;
            lock (_audioLock)
                if (_audioPlayer != null) _audioPlayer.Muted = _audioMuted;
            b.Content = _audioMuted ? "🔇" : "🔊";
            ToolTip.SetTip(b, _audioMuted ? "Play the phone's sound on this computer" : "Mute the phone's sound on this computer");
        };
        return b;
    }

    /// <summary>The phone stays silent while its sound plays here; this lets it play on the phone too.</summary>
    private Button PhoneSpeakerButton()
    {
        var on = false;
        var b = new Button
        {
            Content = "📵", FontSize = 16, Width = 44, Height = 34, Focusable = false, Background = Brushes.Transparent,
            Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(b, "Also play the sound on the phone");
        b.Click += (_, _) =>
        {
            on = !on;
            SendKey(on ? "phone_speaker_on" : "phone_speaker_off");
            b.Content = on ? "📱" : "📵";
            ToolTip.SetTip(b, on ? "Silence the phone (sound plays only here)" : "Also play the sound on the phone");
        };
        return b;
    }

    private Button DimButton()
    {
        var b = new Button
        {
            Content = "🔅", FontSize = 16, Width = 44, Height = 34, Focusable = false, Background = Brushes.Transparent,
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
