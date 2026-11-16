using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NexZen.Infrastructure.Speech;

/// <summary>A voice NexZen can read with: a ZONOS3 speaker embedding (float32[192] .npy).</summary>
public sealed record SpeechVoice(string Id, string Name, string EmbeddingPath);

/// <summary>
/// Runs Zyphra's ZONOS3-Mini text-to-speech server (zonos3-server, ggml) on localhost and streams
/// speech from it. Everything stays on this computer. Layout of the speech folder
/// (~/.local/share/NexZenDesktop/speech, or NEXZEN_SPEECH_DIR):
///   bin/zonos3-server
///   models/backbone-ptq.gguf, models/zac-keep2.gguf (decoder), models/spk-encoder.gguf (optional)
///   voices/&lt;id&gt;.embedding.npy
/// The server starts on first use and stops after a while without speech, freeing its memory.
/// </summary>
public sealed class ZonosSpeechServer : IDisposable
{
    public const int SampleRate = 44_100;
    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Timer _idleTimer;
    private Process? _process;
    private Uri? _baseUri;
    private int _activeRequests;
    private DateTime _lastUse = DateTime.UtcNow;

    public ZonosSpeechServer(string? directory = null)
    {
        Directory = directory
            ?? Environment.GetEnvironmentVariable("NEXZEN_SPEECH_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexZenDesktop", "speech");
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string Directory { get; }
    public string ServerPath => Path.Combine(Directory, "bin", OperatingSystem.IsWindows() ? "zonos3-server.exe" : "zonos3-server");
    public string BackbonePath => Path.Combine(Directory, "models", "backbone-ptq.gguf");
    public string DecoderPath => Path.Combine(Directory, "models", "zac-keep2.gguf");
    public string VoicesDirectory => Path.Combine(Directory, "voices");
    public string LogPath => Path.Combine(Directory, "server.log");

    /// <summary>The server, the model and at least one voice are in place.</summary>
    public bool IsInstalled => File.Exists(ServerPath) && File.Exists(BackbonePath) && File.Exists(DecoderPath)
                               && Voices().Count > 0;

    public string MissingDescription()
    {
        var missing = new List<string>();
        if (!File.Exists(ServerPath)) missing.Add(ServerPath);
        if (!File.Exists(BackbonePath)) missing.Add(BackbonePath);
        if (!File.Exists(DecoderPath)) missing.Add(DecoderPath);
        if (Voices().Count == 0) missing.Add(Path.Combine(VoicesDirectory, "<voice>.embedding.npy"));
        return string.Join(", ", missing);
    }

    public IReadOnlyList<SpeechVoice> Voices()
    {
        if (!System.IO.Directory.Exists(VoicesDirectory))
            return [];
        return System.IO.Directory.GetFiles(VoicesDirectory, "*.embedding.npy")
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var id = Path.GetFileName(path)[..^".embedding.npy".Length];
                var name = id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];
                return new SpeechVoice(id, name, path);
            })
            .ToList();
    }

    /// <summary>
    /// Streams speech for <paramref name="text"/> as mono float32 samples at <see cref="SampleRate"/>.
    /// <paramref name="language"/> is a ZONOS language code ("vi", "en_us", ...).
    /// </summary>
    public async Task SpeakAsync(string text, string language, SpeechVoice voice, bool normalizeText,
        Action<float[], int> onSamples, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeRequests);
        try
        {
            var baseUri = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["text"] = text,
                ["language"] = language,
                ["text_normalization"] = normalizeText,
                ["stream"] = true,
                ["format"] = "pcm",
                ["speaker_mode"] = "embedding",
                ["speaker_embedding_base64"] = Convert.ToBase64String(await File.ReadAllBytesAsync(voice.EmbeddingPath, cancellationToken).ConfigureAwait(false)),
                ["speaker_embedding_name"] = voice.Id,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v2/generate"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Speech server {(int)response.StatusCode}: {Trim(detail)}");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await ReadFloatsAsync(stream, onSamples, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            Interlocked.Decrement(ref _activeRequests);
        }
    }

    /// <summary>Little-endian float32 PCM → sample blocks; a block can end mid-sample, so the remainder is kept.</summary>
    public static async Task ReadFloatsAsync(Stream stream, Action<float[], int> onSamples, CancellationToken cancellationToken)
    {
        var bytes = new byte[64 * 1024];
        var samples = new float[bytes.Length / 4 + 1];
        var carry = 0;
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(carry, bytes.Length - carry), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var available = carry + read;
            var whole = available / 4;
            Buffer.BlockCopy(bytes, 0, samples, 0, whole * 4);
            if (whole > 0)
                onSamples(samples, whole);
            carry = available - whole * 4;
            if (carry > 0)
                Buffer.BlockCopy(bytes, whole * 4, bytes, 0, carry);
        }
    }

    private async Task<Uri> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        _lastUse = DateTime.UtcNow;
        if (_baseUri != null && _process is { HasExited: false })
            return _baseUri;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_baseUri != null && _process is { HasExited: false })
                return _baseUri;
            if (!IsInstalled)
                throw new FileNotFoundException("NexZen speech is not installed: missing " + MissingDescription());

            StopProcess();
            var port = FreePort();
            var psi = new ProcessStartInfo(ServerPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Directory,
            };
            foreach (var arg in new[] { BackbonePath, "--host", "127.0.0.1", "--port", port.ToString() })
                psi.ArgumentList.Add(arg);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("Speech server did not start");
            var log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            process.Exited += (_, _) => { lock (log) log.Dispose(); };
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"Speech server exited ({process.ExitCode}); see {LogPath}");
                try
                {
                    using var probe = await _http.GetAsync(new Uri(baseUri, "v2/capabilities"), cancellationToken).ConfigureAwait(false);
                    if (probe.IsSuccessStatusCode)
                    {
                        _baseUri = baseUri;
                        return baseUri;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            StopProcess();
            throw new TimeoutException("Speech server did not answer within " + StartTimeout.TotalSeconds + " s");
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void StopIfIdle()
    {
        if (_process == null || Volatile.Read(ref _activeRequests) > 0 || DateTime.UtcNow - _lastUse < IdleStop)
            return;
        if (!_startGate.Wait(0))
            return;
        try
        {
            StopProcess();
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _baseUri = null;
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Already gone.
        }
        process.Dispose();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose()
    {
        _idleTimer.Dispose();
        StopProcess();
        _http.Dispose();
    }
}
