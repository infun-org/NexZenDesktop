using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NexZen.Infrastructure.Speech;

/// <summary>Download progress: bytes done and total for the whole install, and what is being fetched.</summary>
public readonly record struct SpeechInstallProgress(long Done, long Total, string Item);

/// <summary>
/// Fetches what read aloud needs into the speech folder: the zonos3-server build that NexZen's
/// release workflow makes (GitHub release <see cref="RuntimeTag"/>, checked against its SHA256SUMS)
/// and the ZONOS3-Mini model files and voices from Hugging Face at a pinned revision, each checked
/// against its SHA-256. Downloads resume after an interruption and land under their final name only
/// once verified.
/// </summary>
public sealed class SpeechInstaller : IDisposable
{
    public const string RuntimeTag = "speech-runtime-1";
    public const string RuntimeAsset = "zonos3-server-linux-x64.tar.gz";
    private const string ReleaseBase = "https://github.com/infun-org/NexZenDesktop/releases/download/" + RuntimeTag + "/";
    private const string ModelRevision = "d2d142a86b1e6aeb4252679673c86ecd86177bf8";
    private const string ModelBase = "https://huggingface.co/Zyphra/ZONOS3-Mini/resolve/" + ModelRevision + "/";

    /// <summary>Model files: Hugging Face path, path in the speech folder, size, SHA-256.</summary>
    public static readonly IReadOnlyList<(string Source, string Target, long Size, string Sha256)> ModelFiles =
    [
        ("gguf/backbone-ptq.gguf", "models/backbone-ptq.gguf", 663_150_624,
            "66fceb6d73a8c30bde80249e4112bb308380ab7f3384fa11c8e4048115e4ceec"),
        ("gguf/zac-keep2.gguf", "models/zac-keep2.gguf", 452_325_440,
            "834e5b70b88294afc3c702a478487e0664dcc6b574c16fc6167d72444aabeafd"),
        ("gguf/voices/Korean/Seoyeon_ko_F_1.embedding.npy", "voices/seoyeon.embedding.npy", 896,
            "dddb6aa68c3febe83ca656c959f4d78246c72e011cde72c3745d1a46a37d8345"),
        ("gguf/voices/Mandarin Chinese/Meilin_zh-energetic_F_1.embedding.npy", "voices/meilin.embedding.npy", 896,
            "8430a2852291d874f327666d05bd15ee854a009cc54b9d3cdba9005d6bc7f59b"),
    ];

    /// <summary>Approximate download size, for the offer shown before installing.</summary>
    public static long DownloadBytes => ModelFiles.Sum(f => f.Size) + 2_000_000;

    private readonly string _directory;
    private readonly HttpClient _http;

    public SpeechInstaller(string directory, HttpClient? http = null)
    {
        _directory = directory;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexZenDesktop", "1.0"));
    }

    public async Task InstallAsync(IProgress<SpeechInstallProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var total = DownloadBytes;
        long doneBefore = 0;

        foreach (var (source, target, size, sha) in ModelFiles)
        {
            var path = Path.Combine(_directory, target);
            if (!File.Exists(path) || new FileInfo(path).Length != size || !await HashMatchesAsync(path, sha, cancellationToken))
            {
                var label = Path.GetFileName(target);
                await DownloadAsync(new Uri(ModelBase + Uri.EscapeDataString(source).Replace("%2F", "/")), path, sha,
                    done => progress?.Report(new SpeechInstallProgress(doneBefore + done, total, label)), cancellationToken);
            }
            doneBefore += size;
            progress?.Report(new SpeechInstallProgress(doneBefore, total, Path.GetFileName(target)));
        }

        var server = Path.Combine(_directory, "bin", "zonos3-server");
        if (!File.Exists(server))
        {
            var sums = await _http.GetStringAsync(new Uri(ReleaseBase + "SHA256SUMS"), cancellationToken);
            var sha = ExpectedHash(sums, RuntimeAsset)
                      ?? throw new InvalidDataException(RuntimeAsset + " is not listed in SHA256SUMS");
            var archive = Path.Combine(_directory, RuntimeAsset);
            await DownloadAsync(new Uri(ReleaseBase + RuntimeAsset), archive, sha,
                done => progress?.Report(new SpeechInstallProgress(Math.Min(total, doneBefore + done), total, "zonos3-server")),
                cancellationToken);
            await ExtractServerAsync(archive, Path.Combine(_directory, "bin"), cancellationToken);
            File.Delete(archive);
        }
        progress?.Report(new SpeechInstallProgress(total, total, "done"));
    }

    /// <summary>Hash for <paramref name="file"/> in a sha256sum listing ("&lt;hash&gt;  &lt;name&gt;").</summary>
    public static string? ExpectedHash(string sums, string file)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*', ' ') == file)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private async Task DownloadAsync(Uri url, string path, string sha256, Action<long> onBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        for (var attempt = 0; ; attempt++)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0)
                request.Headers.Range = new RangeHeaderValue(have, null);
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(part); // stale partial file
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!append) have = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    int read;
                    var done = have;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        done += read;
                        onBytes(done);
                    }
                }
                if (!await HashMatchesAsync(part, sha256, cancellationToken))
                {
                    File.Delete(part);
                    throw new InvalidDataException("Checksum mismatch for " + url);
                }
                File.Move(part, path, overwrite: true);
                return;
            }
            catch (HttpRequestException) when (attempt < 3 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken);
            }
            catch (IOException) when (attempt < 3 && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken);
            }
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExtractServerAsync(string archive, string binDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(binDirectory);
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;
            // Flatten: only the file names matter (server binary and license texts).
            var name = Path.GetFileName(entry.Name);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                continue;
            var target = Path.Combine(binDirectory, name);
            await entry.ExtractToFileAsync(target + ".part", overwrite: true, cancellationToken);
            File.Move(target + ".part", target, overwrite: true);
            if (name == "zonos3-server" && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                             | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        if (!File.Exists(Path.Combine(binDirectory, "zonos3-server")))
            throw new InvalidDataException("zonos3-server missing from " + Path.GetFileName(archive));
    }

    public void Dispose() => _http.Dispose();
}
