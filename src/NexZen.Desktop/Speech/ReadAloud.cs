using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.Speech;
using NexZen.Desktop.Mirror;
using NexZen.Infrastructure.Speech;

namespace NexZen.Desktop.Speech;

public enum ReadAloudState { Idle, OfferDownload, Downloading, Preparing, Speaking, Paused }

public enum ReadAloudSpeed { Slow, Normal, Fast }

/// <summary>
/// Reads text aloud with the local ZONOS3 voice: Vietnamese text has its numbers, dates and units
/// spelled out first, then it is cut into sentences that are generated one after another while
/// the earlier ones play. One reading at a time for the whole app; a new one replaces it.
/// </summary>
public static class ReadAloud
{
    private static readonly ZonosSpeechServer Server = new();
    private static readonly object Gate = new();
    private static CancellationTokenSource? _current;
    private static PulseAudioPlayer? _player;
    private static string? _voiceId = LoadVoicePreference();
    private static (string Text, string? Title, object? Origin)? _pending;
    private static CancellationTokenSource? _install;
    private static ReadAloudSpeed _speed = LoadSpeedPreference();

    static ReadAloud()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Server.Dispose();
    }

    public static event Action? StateChanged;

    /// <summary>A sentence starts playing: its text as written (before numbers were spelled out).</summary>
    public static event Action<object?, string>? SentenceStarted;

    /// <summary>The window (or other owner) the current reading or download offer belongs to.</summary>
    public static object? Origin { get; private set; }

    public static ReadAloudState State { get; private set; }
    public static string Status { get; private set; } = "";
    public static string? Error { get; private set; }

    public static bool IsInstalled => Server.IsInstalled;
    public static string SpeechDirectory => Server.Directory;
    public static IReadOnlyList<SpeechVoice> Voices => Server.Voices();

    public static SpeechVoice? Voice
    {
        get
        {
            var voices = Voices;
            return voices.FirstOrDefault(v => v.Id == _voiceId)
                   ?? voices.FirstOrDefault(v => v.Id == "seoyeon")
                   ?? voices.FirstOrDefault();
        }
    }

    /// <summary>Voice for the next sentence (a running reading switches at its next sentence).</summary>
    public static void SetVoice(string id)
    {
        _voiceId = id;
        SaveVoicePreference(id);
        StateChanged?.Invoke();
    }

    public static ReadAloudSpeed Speed
    {
        get => _speed;
        set
        {
            _speed = value;
            SavePreference("read-aloud-speed.txt", value.ToString());
            StateChanged?.Invoke();
        }
    }

    /// <summary>The model's speaking-rate condition (UTF-8 bytes per second) for a speed.</summary>
    private static double? SpeakingRate(ReadAloudSpeed speed) => speed switch
    {
        ReadAloudSpeed.Slow => 19,
        ReadAloudSpeed.Fast => 33,
        _ => null,
    };

    public static void Speak(string text, string? title = null, object? origin = null)
    {
        Origin = origin;
        if (!Server.IsInstalled)
        {
            // First use: offer the one-time download, then read this text.
            _pending = (text, title, origin);
            if (State is not ReadAloudState.Downloading)
            {
                Error = null;
                var mb = SpeechInstaller.DownloadBytes / 1_000_000;
                Set(ReadAloudState.OfferDownload, $"Read aloud needs the NexZen voice (one-time download, {mb:N0} MB).");
            }
            return;
        }
        CancellationTokenSource cts;
        lock (Gate)
        {
            StopLocked();
            cts = new CancellationTokenSource();
            _current = cts;
        }
        Error = null;
        Set(ReadAloudState.Preparing, title == null ? "Preparing…" : "Preparing: " + title);
        _ = Task.Run(() => RunAsync(text, cts));
    }

    public static void TogglePause()
    {
        var player = _player;
        if (player == null || State is ReadAloudState.Idle or ReadAloudState.Preparing)
            return;
        player.Paused = !player.Paused;
        Set(player.Paused ? ReadAloudState.Paused : ReadAloudState.Speaking, Status);
    }

    public static void Stop()
    {
        _pending = null;
        _install?.Cancel();
        lock (Gate)
            StopLocked();
        Set(ReadAloudState.Idle, "");
    }

    /// <summary>Downloads the voice (server, model, voices), then reads the text that asked for it.</summary>
    public static void Install()
    {
        if (State == ReadAloudState.Downloading)
            return;
        var cts = new CancellationTokenSource();
        _install = cts;
        Error = null;
        Set(ReadAloudState.Downloading, "Downloading the NexZen voice…");
        _ = Task.Run(async () =>
        {
            try
            {
                using var installer = new SpeechInstaller(Server.Directory);
                var lastPercent = -1;
                await installer.InstallAsync(new Progress<SpeechInstallProgress>(p =>
                {
                    var percent = p.Total <= 0 ? 0 : (int)(p.Done * 100 / p.Total);
                    if (percent == lastPercent || State != ReadAloudState.Downloading) return;
                    lastPercent = percent;
                    Set(ReadAloudState.Downloading,
                        $"Downloading the NexZen voice… {percent}% ({p.Done / 1_000_000:N0} / {p.Total / 1_000_000:N0} MB)");
                }), cts.Token).ConfigureAwait(false);
                _install = null;
                var pending = _pending;
                _pending = null;
                if (pending is { } p && Server.IsInstalled)
                    Speak(p.Text, p.Title, p.Origin);
                else
                    Set(ReadAloudState.Idle, "");
            }
            catch (OperationCanceledException)
            {
                Set(ReadAloudState.Idle, "");
            }
            catch (Exception ex)
            {
                Program.StartupLog.Write("[ReadAloud] install failed: " + ex.GetType().Name + ": " + ex.Message);
                _install = null;
                Error = ex.Message;
                Set(ReadAloudState.OfferDownload, "Download failed: " + ex.Message + " Try again?");
            }
        });
    }

    private static void StopLocked()
    {
        _current?.Cancel();
        _current = null;
        var player = _player;
        _player = null;
        if (player != null)
        {
            player.Clear();
            player.Paused = false;
            player.Dispose();
        }
    }

    private static async Task RunAsync(string text, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var vietnamese = VietnameseTextNormalizer.LooksVietnamese(text);
            var language = vietnamese ? "vi" : "en_us";
            // Split the text as written, so each sentence can be found on the page while it plays.
            var pieces = SpeechTextSplitter.Split(text);
            if (pieces.Count == 0)
            {
                Set(ReadAloudState.Idle, "");
                return;
            }

            const int rate = ZonosSpeechServer.SampleRate;
            var player = PulseAudioPlayer.TryCreate(rate, 1, out var err,
                streamName: "Read aloud (NexZen)", maxQueuedMs: null)
                ?? throw new InvalidOperationException("No audio output: " + err);
            lock (Gate)
            {
                if (token.IsCancellationRequested)
                {
                    player.Dispose();
                    return;
                }
                _player = player;
            }

            // Sentences come out of the model at different levels; even them out.
            var leveller = new SpeechLevelNormalizer(rate);
            long enqueued = 0;
            var starts = new List<(long Sample, string Text)>();
            var origin = Origin;
            var announced = 0;
            void Announce()
            {
                // Played so far = everything queued minus what is still waiting.
                var played = Interlocked.Read(ref enqueued) - player.QueuedSamples;
                List<string>? due = null;
                lock (starts)
                {
                    while (announced < starts.Count && starts[announced].Sample <= played)
                        (due ??= []).Add(starts[announced++].Text);
                }
                if (due != null && !token.IsCancellationRequested)
                    foreach (var t in due)
                        SentenceStarted?.Invoke(origin, t);
            }
            using var ticker = new Timer(_ => Announce(), null, 100, 100);
            for (var i = 0; i < pieces.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var voice = Voice ?? throw new InvalidOperationException("No voice installed");
                var label = $"Reading {i + 1}/{pieces.Count}";
                lock (starts)
                    starts.Add((Interlocked.Read(ref enqueued), pieces[i]));
                var spoken = vietnamese ? VietnameseTextNormalizer.Normalize(pieces[i]) : pieces[i];
                void Play(float[] samples, int count)
                {
                    if (token.IsCancellationRequested) return;
                    player.Enqueue(samples, count);
                    Interlocked.Add(ref enqueued, count);
                    if (State == ReadAloudState.Preparing)
                        Set(ReadAloudState.Speaking, label);
                }
                leveller.BeginSentence();
                // A server that stops answering ends the reading with a message instead of hanging.
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(90));
                    try
                    {
                        await SpeakPieceAsync(spoken, vietnamese, voice,
                            (samples, count) => leveller.Push(samples, count, Play), limit.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("The voice did not answer for 90 seconds.");
                    }
                }
                token.ThrowIfCancellationRequested();
                leveller.EndSentence(Play);
                if (State != ReadAloudState.Paused)
                    Set(ReadAloudState.Speaking, label);
            }

            // Everything is generated; wait for it to play out.
            while (!token.IsCancellationRequested && (player.QueuedSamples > 0 || player.Paused))
                await Task.Delay(100, token).ConfigureAwait(false);
            await Task.Delay(250, token).ConfigureAwait(false);
            lock (Gate)
            {
                if (_current == cts)
                    StopLocked();
            }
            Set(ReadAloudState.Idle, "");
        }
        catch (OperationCanceledException)
        {
            // Stopped, or replaced by a new reading.
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write("[ReadAloud] " + ex.GetType().Name + ": " + ex.Message);
            lock (Gate)
            {
                if (_current == cts)
                    StopLocked();
            }
            Error = ex.Message;
            Set(ReadAloudState.Idle, "");
        }
    }

    /// <summary>Text to read from the page extract (title, then the content blocks or the fallback text).</summary>
    public static string TextFromPageJson(string json, out string? title)
    {
        title = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                title = t.GetString();
            var lines = new List<string>();
            if (root.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var bt) && bt.GetString() is { Length: > 0 } s)
                        lines.Add(s);
                }
            }
            if (lines.Count == 0 && root.TryGetProperty("fallback", out var fb) && fb.GetString() is { Length: > 0 } f)
                lines.Add(f);
            if (!string.IsNullOrWhiteSpace(title) && (lines.Count == 0 || !lines[0].StartsWith(title, StringComparison.Ordinal)))
                lines.Insert(0, title!);
            return string.Join("\n", lines);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static void Set(ReadAloudState state, string status)
    {
        State = state;
        Status = status;
        StateChanged?.Invoke();
    }

    private static string PreferencePath(string file) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop", file);

    private static string? LoadPreference(string file)
    {
        try { return File.Exists(PreferencePath(file)) ? File.ReadAllText(PreferencePath(file)).Trim() : null; }
        catch { return null; }
    }

    private static void SavePreference(string file, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath(file))!);
            File.WriteAllText(PreferencePath(file), value);
        }
        catch
        {
            // Not remembered.
        }
    }

    private static string? LoadVoicePreference() => LoadPreference("read-aloud-voice.txt");

    private static void SaveVoicePreference(string id) => SavePreference("read-aloud-voice.txt", id);

    private static ReadAloudSpeed LoadSpeedPreference() =>
        Enum.TryParse<ReadAloudSpeed>(LoadPreference("read-aloud-speed.txt"), out var s) ? s : ReadAloudSpeed.Normal;

    /// <summary>Speaks <paramref name="text"/> into a 16-bit WAV file (same voice, speed and levelling as reading aloud).</summary>
    public static async Task SaveToWavAsync(string text, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var voice = Voice ?? throw new InvalidOperationException("No voice installed");
        var vietnamese = VietnameseTextNormalizer.LooksVietnamese(text);
        var pieces = SpeechTextSplitter.Split(text);
        const int rate = ZonosSpeechServer.SampleRate;
        var leveller = new SpeechLevelNormalizer(rate);
        var audio = new List<float>();
        var pause = new float[rate / 5]; // 0.2 s between sentences
        for (var i = 0; i < pieces.Count; i++)
        {
            progress?.Report((double)i / pieces.Count);
            var spoken = vietnamese ? VietnameseTextNormalizer.Normalize(pieces[i]) : pieces[i];
            leveller.BeginSentence();
            void Keep(float[] s, int n) { for (var k = 0; k < n; k++) audio.Add(s[k]); }
            await SpeakPieceAsync(spoken, vietnamese, voice, (s, n) => leveller.Push(s, n, Keep), cancellationToken)
                .ConfigureAwait(false);
            leveller.EndSentence(Keep);
            audio.AddRange(pause);
        }
        var samples = audio.ToArray();
        await File.WriteAllBytesAsync(path, WavFile.Pcm16(samples, samples.Length, rate),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }

    public static bool CanMakeVoices => File.Exists(Server.EncoderPath);

    /// <summary>Fetches the speaker encoder (about 45 MB) used to make voices from recordings.</summary>
    public static async Task InstallVoiceMakerAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var installer = new SpeechInstaller(Server.Directory);
        await installer.InstallEncoderAsync(new Progress<SpeechInstallProgress>(p =>
            progress?.Report(p.Total <= 0 ? 0 : (double)p.Done / p.Total)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes a voice from a recording (any audio format ffmpeg reads) and selects it.</summary>
    public static async Task<SpeechVoice> MakeVoiceAsync(string name, byte[] audio, string fileName, CancellationToken cancellationToken)
    {
        var embedding = await Server.CreateVoiceEmbeddingAsync(audio, fileName, cancellationToken).ConfigureAwait(false);
        var voice = Server.SaveVoice(name, embedding);
        SetVoice(voice.Id);
        return voice;
    }

    private static Task SpeakPieceAsync(string text, bool vietnamese, SpeechVoice voice,
        Action<float[], int> onSamples, CancellationToken cancellationToken) =>
        Server.SpeakAsync(text, vietnamese ? "vi" : "en_us", voice, !vietnamese, onSamples, cancellationToken,
            SpeakingRate(_speed));

    public static void DeleteVoice(string id)
    {
        Server.DeleteVoice(id);
        if (_voiceId == id)
            SetVoice("seoyeon");
        StateChanged?.Invoke();
    }
}
