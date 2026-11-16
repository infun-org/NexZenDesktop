using System.Text;
using System.Text.Json;

namespace NexZen.Infrastructure.Speech;

/// <summary>A voice NexZen can read with: a ZONOS3 speaker embedding (float32[192] .npy).</summary>
public sealed record SpeechVoice(string Id, string Name, string EmbeddingPath, bool BuiltIn);

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
    private readonly LocalSpeechProcess _server;
    private HttpClient _http => _server.Http;

    public ZonosSpeechServer(string? directory = null)
    {
        Directory = directory
            ?? Environment.GetEnvironmentVariable("NEXZEN_SPEECH_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexZenDesktop", "speech");
        _server = new LocalSpeechProcess("Speech server", LogPath, "v2/capabilities", TimeSpan.FromMinutes(10));
    }

    public string Directory { get; }
    public string ServerPath => Path.Combine(Directory, "bin", OperatingSystem.IsWindows() ? "zonos3-server.exe" : "zonos3-server");
    public string BackbonePath => Path.Combine(Directory, "models", "backbone-ptq.gguf");
    public string DecoderPath => Path.Combine(Directory, "models", "zac-keep2.gguf");
    /// <summary>Speaker encoder: only needed to make a voice from a recording.</summary>
    public string EncoderPath => Path.Combine(Directory, "models", "spk-encoder.gguf");
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

    public static readonly IReadOnlySet<string> BuiltInVoices = new HashSet<string> { "seoyeon", "meilin" };

    /// <summary>Installed voices: built-in ones first, then the user's own (named by a .name.txt beside them).</summary>
    public IReadOnlyList<SpeechVoice> Voices()
    {
        if (!System.IO.Directory.Exists(VoicesDirectory))
            return [];
        return System.IO.Directory.GetFiles(VoicesDirectory, "*.embedding.npy")
            .Select(path =>
            {
                var id = Path.GetFileName(path)[..^".embedding.npy".Length];
                var namePath = Path.Combine(VoicesDirectory, id + ".name.txt");
                var name = File.Exists(namePath) ? File.ReadAllText(namePath).Trim() : "";
                if (name.Length == 0)
                    name = id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];
                return new SpeechVoice(id, name, path, BuiltInVoices.Contains(id));
            })
            .OrderBy(v => v.BuiltIn ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Saves a voice made from a recording; returns it.</summary>
    public SpeechVoice SaveVoice(string name, float[] embedding)
    {
        System.IO.Directory.CreateDirectory(VoicesDirectory);
        var baseId = VoiceId(name);
        var id = baseId;
        for (var n = 2; File.Exists(Path.Combine(VoicesDirectory, id + ".embedding.npy")) || BuiltInVoices.Contains(id); n++)
            id = baseId + "-" + n;
        var path = Path.Combine(VoicesDirectory, id + ".embedding.npy");
        File.WriteAllBytes(path, Npy.Float32Vector(embedding));
        File.WriteAllText(Path.Combine(VoicesDirectory, id + ".name.txt"), name.Trim());
        return new SpeechVoice(id, name.Trim(), path, false);
    }

    public void DeleteVoice(string id)
    {
        if (BuiltInVoices.Contains(id))
            throw new InvalidOperationException("Built-in voices cannot be deleted");
        File.Delete(Path.Combine(VoicesDirectory, id + ".embedding.npy"));
        File.Delete(Path.Combine(VoicesDirectory, id + ".name.txt"));
    }

    /// <summary>File-safe id: ASCII letters and digits ("Giọng của tôi" → "giong-cua-toi").</summary>
    public static string VoiceId(string name)
    {
        var decomposed = name.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var id = sb.ToString().Trim('-');
        return id.Length == 0 ? "voice" : id.Length > 40 ? id[..40] : id;
    }

    /// <summary>
    /// Turns a recording (any format ffmpeg reads; the server decodes it with ffmpeg) into a
    /// speaker embedding with the speaker encoder.
    /// </summary>
    public async Task<float[]> CreateVoiceEmbeddingAsync(byte[] audio, string fileName, CancellationToken cancellationToken)
    {
        if (!File.Exists(EncoderPath))
            throw new FileNotFoundException("Speaker encoder missing: " + EncoderPath);
        using (_server.Use())
        {
            var baseUri = await EnsureStartedAsync(cancellationToken, needEncoder: true).ConfigureAwait(false);
            var session = "nexzen-" + Guid.NewGuid().ToString("N");
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["speaker_audio_base64"] = Convert.ToBase64String(audio),
                ["speaker_audio_name"] = fileName,
            });
            using var create = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v2/speakers"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            create.Headers.Add("X-TTS-Session-ID", session);
            using var created = await _http.SendAsync(create, cancellationToken).ConfigureAwait(false);
            var createdJson = await created.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!created.IsSuccessStatusCode)
                throw new InvalidOperationException("Could not read the recording: " + Detail(createdJson));
            using var createdDoc = JsonDocument.Parse(createdJson);
            var id = createdDoc.RootElement.GetProperty("id").GetString()!;

            using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "v2/speakers/" + Uri.EscapeDataString(id) + "/embedding"));
            get.Headers.Add("X-TTS-Session-ID", session);
            using var got = await _http.SendAsync(get, cancellationToken).ConfigureAwait(false);
            var gotJson = await got.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!got.IsSuccessStatusCode)
                throw new InvalidOperationException("Could not make the voice: " + Detail(gotJson));
            using var gotDoc = JsonDocument.Parse(gotJson);
            return gotDoc.RootElement.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        }
    }

    private static string Detail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("detail", out var d))
                return d.ValueKind == JsonValueKind.String ? d.GetString()! : Trim(d.GetRawText());
        }
        catch (JsonException)
        {
        }
        return Trim(json);
    }

    /// <summary>
    /// Streams speech for <paramref name="text"/> as mono float32 samples at <see cref="SampleRate"/>.
    /// <paramref name="language"/> is a ZONOS language code ("vi", "en_us", ...).
    /// </summary>
    public async Task SpeakAsync(string text, string language, SpeechVoice voice, bool normalizeText,
        Action<float[], int> onSamples, CancellationToken cancellationToken, double? speakingRate = null)
    {
        using (_server.Use())
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
                // UTF-8 bytes per second (the model's own rate condition); null = the voice's natural pace.
                ["condition_values"] = speakingRate is { } rate ? new Dictionary<string, double> { ["speaking_rate"] = rate } : null,
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

    private Task<Uri> EnsureStartedAsync(CancellationToken cancellationToken, bool needEncoder = false)
    {
        if (!IsInstalled)
            throw new FileNotFoundException("NexZen speech is not installed: missing " + MissingDescription());
        // The server loads the speaker encoder found beside the model when it starts; making a voice
        // restarts a server that started without it.
        return _server.EnsureStartedAsync(ServerPath, Directory,
            port => [BackbonePath, "--host", "127.0.0.1", "--port", port.ToString()],
            startedWith: File.Exists(EncoderPath),
            usable: loaded => !needEncoder || loaded is true,
            cancellationToken);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>Frees the model's memory now (unless a request is running).</summary>
    public bool StopIfUnused() => _server.StopIfUnused();

    public void Dispose() => _server.Dispose();
}
