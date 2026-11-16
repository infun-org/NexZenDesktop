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

public enum ReadAloudState { Idle, Preparing, Speaking, Paused }

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

    static ReadAloud()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Server.Dispose();
    }

    public static event Action? StateChanged;

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
            var voices = Server.Voices();
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
    }

    public static void Speak(string text, string? title = null)
    {
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
        lock (Gate)
            StopLocked();
        Set(ReadAloudState.Idle, "");
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
            if (vietnamese)
                text = VietnameseTextNormalizer.Normalize(text);
            var pieces = SpeechTextSplitter.Split(text);
            if (pieces.Count == 0)
            {
                Set(ReadAloudState.Idle, "");
                return;
            }

            var player = PulseAudioPlayer.TryCreate(ZonosSpeechServer.SampleRate, 1, out var err,
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
            var leveller = new SpeechLevelNormalizer(ZonosSpeechServer.SampleRate);
            for (var i = 0; i < pieces.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var voice = Voice ?? throw new InvalidOperationException("No voice installed");
                var label = $"Reading {i + 1}/{pieces.Count}";
                void Play(float[] samples, int count)
                {
                    if (token.IsCancellationRequested) return;
                    player.Enqueue(samples, count);
                    if (State == ReadAloudState.Preparing)
                        Set(ReadAloudState.Speaking, label);
                }
                leveller.BeginSentence();
                await Server.SpeakAsync(pieces[i], language, voice, normalizeText: !vietnamese,
                    (samples, count) => leveller.Push(samples, count, Play), token).ConfigureAwait(false);
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

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop", "read-aloud-voice.txt");

    private static string? LoadVoicePreference()
    {
        try { return File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath).Trim() : null; }
        catch { return null; }
    }

    private static void SaveVoicePreference(string id)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, id);
        }
        catch
        {
            // Not remembered.
        }
    }
}
