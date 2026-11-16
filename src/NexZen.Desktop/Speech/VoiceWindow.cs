using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NexZen.Desktop.Speech;

/// <summary>
/// NexZen Voice: read any text aloud, save it as a WAV file, pick the voice and speed, and make a
/// new voice from a recording (the microphone or an audio file).
/// </summary>
public sealed class VoiceWindow : Window
{
    private static VoiceWindow? _open;

    private readonly TextBox _text;
    private readonly ComboBox _voice;
    private readonly ComboBox _speed;
    private readonly Button _deleteVoice;
    private readonly Button _play;
    private readonly Button _save;
    private readonly Button _download;
    private readonly TextBlock _status;

    private readonly TextBox _voiceName;
    private readonly Button _record;
    private readonly Button _chooseFile;
    private readonly ProgressBar _level;
    private readonly TextBlock _recordingInfo;
    private readonly CheckBox _consent;
    private readonly Button _makeVoice;
    private readonly TextBlock _voiceStatus;

    /// <summary>A paragraph to read while recording: steady, natural speech for 10–15 seconds.</summary>
    private const string PromptText =
        "Xin chào, đây là giọng nói của tôi. Hôm nay trời rất đẹp, tôi muốn đọc cho bạn nghe một đoạn văn ngắn. " +
        "Cảm ơn bạn đã lắng nghe và hẹn gặp lại.";

    private byte[]? _clip;
    private string _clipName = "recording.wav";
    private CancellationTokenSource? _recording;
    private CancellationTokenSource? _saving;
    private bool _syncing;

    /// <summary>Opens the window (one for the app), optionally with text to read.</summary>
    public static void Open(Window? owner, string? text)
    {
        if (_open == null)
        {
            _open = new VoiceWindow();
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            _open.Activate();
        }
        if (!string.IsNullOrWhiteSpace(text))
            _open._text.Text = text.Trim();
    }

    private VoiceWindow()
    {
        Title = "NexZen Voice";
        Width = 680;
        Height = 720;
        MinWidth = 480;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 210,
            FontSize = 14,
            PlaceholderText = "Type or paste the text to read…",
        };
        _voice = new ComboBox { MinWidth = 170 };
        _voice.SelectionChanged += (_, _) =>
        {
            if (_syncing || _voice.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            ReadAloud.SetVoice(id);
        };
        _speed = new ComboBox { MinWidth = 110 };
        foreach (var s in Enum.GetValues<ReadAloudSpeed>())
            _speed.Items.Add(new ComboBoxItem { Content = s.ToString(), Tag = s });
        _speed.SelectionChanged += (_, _) =>
        {
            if (_syncing || _speed.SelectedItem is not ComboBoxItem { Tag: ReadAloudSpeed s }) return;
            ReadAloud.Speed = s;
        };
        _deleteVoice = SmallButton("Delete voice");
        _deleteVoice.Click += (_, _) =>
        {
            if (ReadAloud.Voice is { BuiltIn: false } v)
            {
                ReadAloud.DeleteVoice(v.Id);
                Refresh();
            }
        };
        _play = PrimaryButton("▶  Read aloud");
        _play.Click += (_, _) => TogglePlay();
        _save = SmallButton("Save as WAV…");
        _save.Click += async (_, _) => await SaveAsync();
        _download = PrimaryButton("Download the voice");
        _download.Click += (_, _) => ReadAloud.Install();
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) };

        _voiceName = new TextBox { PlaceholderText = "Voice name, e.g. My voice", MinWidth = 220 };
        _record = SmallButton("● Record 15 s");
        _record.Click += async (_, _) => await ToggleRecordingAsync();
        _chooseFile = SmallButton("Choose audio file…");
        _chooseFile.Click += async (_, _) => await ChooseFileAsync();
        _level = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        _recordingInfo = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) };
        _consent = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "This is my own voice, or its owner has allowed me to use it.",
                TextWrapping = TextWrapping.Wrap,
            },
        };
        _consent.IsCheckedChanged += (_, _) => UpdateMakeVoice();
        _voiceName.TextChanged += (_, _) => UpdateMakeVoice();
        _makeVoice = PrimaryButton("Make voice");
        _makeVoice.Click += async (_, _) => await MakeVoiceAsync();
        _voiceStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) };

        var readCard = Card(
            Heading("Read text aloud"),
            _text,
            Row(Label("Voice"), _voice, Label("Speed"), _speed, _deleteVoice),
            Row(_play, _save, _download),
            _status);
        var makeCard = Card(
            Heading("Make a voice from a recording"),
            new TextBlock
            {
                Text = "Record or choose 10–20 seconds of clear speech by one person, with little background noise. " +
                       "The voice stays on this computer.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            },
            _voiceName,
            new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF1, 0xF8, 0xE9)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8),
                Child = new TextBlock
                {
                    Text = "When recording, read this aloud:\n“" + PromptText + "”",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            Row(_record, _chooseFile),
            _level,
            _recordingInfo,
            _consent,
            _makeVoice,
            _voiceStatus);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 14,
                Children = { readCard, makeCard },
            },
        };

        ReadAloud.StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            ReadAloud.StateChanged -= OnStateChanged;
            _recording?.Cancel();
            _saving?.Cancel();
            if (ReferenceEquals(ReadAloud.Origin, this))
                ReadAloud.Stop();
        };
        Refresh();
        UpdateMakeVoice();
    }

    private void OnStateChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var installed = ReadAloud.IsInstalled;
        var state = ReadAloud.State;
        var mine = ReferenceEquals(ReadAloud.Origin, this);
        var reading = mine && state is ReadAloudState.Preparing or ReadAloudState.Speaking or ReadAloudState.Paused;

        _play.Content = reading ? "■  Stop" : "▶  Read aloud";
        _play.IsEnabled = installed;
        _save.IsEnabled = installed && _saving == null;
        _download.IsVisible = !installed && state != ReadAloudState.Downloading;
        _voice.IsEnabled = installed;
        _speed.IsEnabled = installed;
        _record.IsEnabled = installed;
        _chooseFile.IsEnabled = installed;

        if (!installed)
            _status.Text = state == ReadAloudState.Downloading
                ? ReadAloud.Status
                : $"The NexZen voice is not installed yet (one-time download, about {Infrastructure.Speech.SpeechInstaller.DownloadBytes / 1_000_000:N0} MB).";
        else if (_saving == null)
            _status.Text = mine && state != ReadAloudState.Idle ? ReadAloud.Status : ReadAloud.Error ?? "";

        _syncing = true;
        try
        {
            var voices = ReadAloud.Voices;
            var current = ReadAloud.Voice;
            _voice.Items.Clear();
            foreach (var v in voices)
                _voice.Items.Add(new ComboBoxItem { Content = v.BuiltIn ? v.Name : v.Name + " (yours)", Tag = v.Id });
            _voice.SelectedItem = _voice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current?.Id);
            _deleteVoice.IsVisible = current is { BuiltIn: false };
            _speed.SelectedItem = _speed.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (ReadAloudSpeed?)i.Tag == ReadAloud.Speed);
        }
        finally
        {
            _syncing = false;
        }
        UpdateMakeVoice();
    }

    private void TogglePlay()
    {
        if (ReferenceEquals(ReadAloud.Origin, this)
            && ReadAloud.State is ReadAloudState.Preparing or ReadAloudState.Speaking or ReadAloudState.Paused)
        {
            ReadAloud.Stop();
            return;
        }
        var text = _text.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _status.Text = "Type some text first.";
            return;
        }
        ReadAloud.Speak(text, null, this);
    }

    private async Task SaveAsync()
    {
        var text = _text.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _status.Text = "Type some text first.";
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save speech",
            SuggestedFileName = "nexzen-voice.wav",
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path == null)
            return;
        _saving = new CancellationTokenSource();
        Refresh();
        try
        {
            await ReadAloud.SaveToWavAsync(text, path,
                new Progress<double>(p => _status.Text = $"Saving… {p:P0}"), _saving.Token);
            _status.Text = "Saved " + Path.GetFileName(path) + ".";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save: " + ex.Message;
        }
        finally
        {
            _saving = null;
            _save.IsEnabled = true;
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (_recording != null)
        {
            _recording.Cancel();
            return;
        }
        var cts = new CancellationTokenSource();
        _recording = cts;
        _record.Content = "■ Stop recording";
        _level.IsVisible = true;
        _recordingInfo.Text = "Recording… speak now.";
        const int rate = 44_100;
        try
        {
            var samples = await Task.Run(() => PulseAudioRecorder.Record(rate, 15,
                peak => Dispatcher.UIThread.Post(() => _level.Value = Math.Min(1, peak)), cts.Token));
            var seconds = samples.Length / (double)rate;
            if (seconds < 3)
            {
                _clip = null;
                _recordingInfo.Text = $"Only {seconds:F1} s recorded; please record at least 5 seconds.";
            }
            else
            {
                _clip = WavFile.Pcm16(samples, samples.Length, rate);
                _clipName = "recording.wav";
                _recordingInfo.Text = $"Recorded {seconds:F1} s.";
            }
        }
        catch (Exception ex)
        {
            _clip = null;
            _recordingInfo.Text = "Recording failed: " + ex.Message;
        }
        finally
        {
            _recording = null;
            _record.Content = "● Record 15 s";
            _level.IsVisible = false;
            UpdateMakeVoice();
        }
    }

    private async Task ChooseFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a recording",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.mp3", "*.m4a", "*.ogg", "*.opus", "*.flac", "*.aac", "*.webm"] },
                FilePickerFileTypes.All,
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path == null)
            return;
        var info = new FileInfo(path);
        if (info.Length > 30_000_000)
        {
            _recordingInfo.Text = "That file is too large; use a short clip (10–20 seconds).";
            return;
        }
        _clip = await File.ReadAllBytesAsync(path);
        _clipName = Path.GetFileName(path);
        _recordingInfo.Text = "Using " + _clipName + ".";
        if (string.IsNullOrWhiteSpace(_voiceName.Text))
            _voiceName.Text = Path.GetFileNameWithoutExtension(path);
        UpdateMakeVoice();
    }

    private void UpdateMakeVoice()
    {
        _makeVoice.IsEnabled = _clip != null && _consent.IsChecked == true && !string.IsNullOrWhiteSpace(_voiceName.Text);
    }

    private async Task MakeVoiceAsync()
    {
        if (_clip == null || _consent.IsChecked != true || string.IsNullOrWhiteSpace(_voiceName.Text))
            return;
        if (!FfmpegAvailable())
        {
            _voiceStatus.Text = "Making a voice needs ffmpeg to read the recording. Install it (for example: sudo apt install ffmpeg) and try again.";
            return;
        }
        _makeVoice.IsEnabled = false;
        try
        {
            if (!ReadAloud.CanMakeVoices)
            {
                _voiceStatus.Text = "Downloading the voice maker (45 MB)…";
                await ReadAloud.InstallVoiceMakerAsync(
                    new Progress<double>(p => _voiceStatus.Text = $"Downloading the voice maker… {p:P0}"), CancellationToken.None);
            }
            _voiceStatus.Text = "Making the voice…";
            // A stuck server must not leave the window waiting forever.
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            Infrastructure.Speech.SpeechVoice voice;
            try
            {
                voice = await ReadAloud.MakeVoiceAsync(_voiceName.Text!.Trim(), _clip, _clipName, limit.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                throw new TimeoutException("the voice server did not answer within 2 minutes");
            }
            _voiceStatus.Text = $"Voice \"{voice.Name}\" is ready and selected. Try it with Read aloud.";
            _clip = null;
            _recordingInfo.Text = "";
            _consent.IsChecked = false;
            Refresh();
        }
        catch (Exception ex)
        {
            _voiceStatus.Text = "Could not make the voice: " + ex.Message;
        }
        finally
        {
            UpdateMakeVoice();
        }
    }

    private static bool FfmpegAvailable()
    {
        var custom = Environment.GetEnvironmentVariable("ZONOS3_FFMPEG");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
            return true;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, "ffmpeg")));
    }

    private static Border Card(params Control[] children) => new()
    {
        Background = Brushes.White,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(16),
        BoxShadow = BoxShadows.Parse("0 1 6 0 #22000000"),
        Child = new StackPanel { Spacing = 10, Children = { } }.With(children),
    };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold };

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params Control[] children) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }.With(children);

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 6),
        CornerRadius = new CornerRadius(8),
    };

    private static Button PrimaryButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(16, 7),
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
        Foreground = Brushes.White,
    };
}

internal static class PanelExtensions
{
    public static T With<T>(this T panel, Control[] children) where T : Panel
    {
        foreach (var c in children)
            panel.Children.Add(c);
        return panel;
    }
}
