using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
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
        Content = new Grid { Children = { _image, _overlay } };

        _client = new PhoneMirrorClient(orchestrator, crypto);
        _client.StateChanged += (state, message) => Dispatcher.UIThread.Post(() => OnState(state, message));
        _client.ConfigReceived += config => Dispatcher.UIThread.Post(() => OnConfig(config));
        _client.VideoReceived += OnVideo;
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
