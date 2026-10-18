using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public sealed class LocalLlamaBootstrap : IDisposable
{
    public const string UserAgent = "NexZenDesktop";
    public const string GitHubReleasesUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public LocalLlamaBootstrap(HttpClient? http = null)
    {
        if (http != null)
        {
            _http = http;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromHours(2)
            };
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _ownsHttp = true;
        }
    }

    public static string BinaryFileName =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    public static string[] PreferredAssetTokens(string os, string arch) => os switch
    {
        "windows" => [$"win-vulkan-{arch}", $"win-cpu-{arch}"],
        "macos" => [$"macos-{arch}"],
        _ => [$"ubuntu-vulkan-{arch}", $"ubuntu-{arch}"]
    };

    public static string? FindInstalledServer(string runtimeDirectory, string? bundleDirectory)
    {
        var name = BinaryFileName;
        var candidates = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(path);
        }

        if (!string.IsNullOrWhiteSpace(bundleDirectory))
        {
            Add(Path.Combine(bundleDirectory, name));
            Add(Path.Combine(bundleDirectory, "runtime", name));
            Add(Path.Combine(bundleDirectory, "runtime", "bin", name));
            Add(Path.Combine(bundleDirectory, "llama", name));
            Add(Path.Combine(bundleDirectory, "llama", "bin", name));
        }

        Add(Path.Combine(runtimeDirectory, name));
        Add(Path.Combine(runtimeDirectory, "bin", name));
        Add(Path.Combine(runtimeDirectory, "pkg", name));
        Add(Path.Combine(runtimeDirectory, "pkg", "bin", name));

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        if (Directory.Exists(runtimeDirectory))
        {
            var nested = FindLlamaServerBinary(runtimeDirectory);
            if (nested != null)
                return nested;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var path in new[]
                 {
                     Path.Combine(home, ".local", "bin", name),
                     Path.Combine(home, ".local", "src", "llama.cpp", "build", "bin", name)
                 })
        {
            if (File.Exists(path))
                return path;
        }

        return FindOnPath(name);
    }

    public static string? FindLlamaServerBinary(string root)
    {
        if (!Directory.Exists(root))
            return null;
        var name = BinaryFileName;
        try
        {
            return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static (string Url, string FileName)? SelectRuntimeAsset(
        JsonElement releases,
        string? os = null,
        Architecture? arch = null)
    {
        os ??= OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : "linux";
        var archName = (arch ?? RuntimeInformation.OSArchitecture) switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm64",
            _ => "x64"
        };
        var tokens = PreferredAssetTokens(os, archName);
        var list = releases.ValueKind == JsonValueKind.Array
            ? releases.EnumerateArray().ToArray()
            : new[] { releases };

        foreach (var release in list)
        {
            var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (tag.Length == 0 || tag[0] is not ('b' or 'B'))
                continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            var names = new List<(string Name, string Url)>();
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (name.Length > 0 && url.Length > 0)
                    names.Add((name, url));
            }

            foreach (var token in tokens)
            {
                var hit = names.FirstOrDefault(a => MatchesAsset(a.Name, token));
                if (!string.IsNullOrEmpty(hit.Name))
                    return (hit.Url, hit.Name);
            }
        }

        return null;
    }

    public static bool MatchesAsset(string fileName, string token)
    {
        if (fileName.Contains("cudart", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!fileName.StartsWith("llama-", StringComparison.OrdinalIgnoreCase))
            return false;
        var needle = "-bin-" + token;
        if (!fileName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return false;
        return fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static bool FileLooksComplete(string path, long expectedSize)
    {
        if (!File.Exists(path))
            return false;
        if (expectedSize <= 0)
            return new FileInfo(path).Length > 0;
        return new FileInfo(path).Length == expectedSize;
    }

    public static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
    }

    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // ignore platforms without chmod
        }
    }

    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string? expectedSha256,
        long expectedSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (FileLooksComplete(destinationPath, expectedSize))
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
                return;
            // Size already matches an installed GGUF; skip a multi-GB rehash on every Send.
            return;
        }

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        var partPath = destinationPath + ".part";
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
        if (expectedSize > 0 && existing > expectedSize)
        {
            File.Delete(partPath);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var append = false;
        var skipWrite = false;
        if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            append = true;
        else if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            skipWrite = true;
        else if (existing > 0)
        {
            existing = 0;
            try { File.Delete(partPath); } catch { /* ignore */ }
        }

        if (!skipWrite)
        {
            response.EnsureSuccessStatusCode();
            await WriteDownloadAsync(
                response,
                partPath,
                append,
                existing,
                expectedSize,
                Path.GetFileName(destinationPath),
                progress,
                cancellationToken);
        }

        var received = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
        if (expectedSize > 0 && received != expectedSize)
        {
            try { File.Delete(partPath); } catch { /* ignore */ }
            throw new InvalidOperationException(
                $"Download size mismatch for {Path.GetFileName(destinationPath)} ({LocalLlamaCatalog.FormatBytes(received)} vs {LocalLlamaCatalog.FormatBytes(expectedSize)}).");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            progress?.Report($"Verifying {Path.GetFileName(destinationPath)}…");
            var actual = Sha256Hex(partPath);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(partPath); } catch { /* ignore */ }
                throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(destinationPath)}.");
            }
        }

        File.Move(partPath, destinationPath, overwrite: true);
    }

    public async Task EnsureModelFilesAsync(
        LocalLlamaModel model,
        string modelsDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(modelsDirectory);
        foreach (var file in LocalLlamaCatalog.FilesToDownload(model))
        {
            var dest = Path.Combine(modelsDirectory, file.FileName);
            if (FileLooksComplete(dest, file.SizeBytes))
                continue;
            if (string.IsNullOrWhiteSpace(file.HfRepo))
                throw new InvalidOperationException($"Missing GGUF: {file.FileName}");
            progress?.Report($"Downloading {file.FileName}…");
            var url = LocalLlamaCatalog.HuggingFaceUrl(file.HfRepo, file.FileName);
            await DownloadFileAsync(url, dest, file.Sha256, file.SizeBytes, progress, cancellationToken);
        }
    }

    public async Task<string> EnsureRuntimeAsync(
        string runtimeDirectory,
        string? bundleDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existing = FindInstalledServer(runtimeDirectory, bundleDirectory);
        if (existing != null)
            return existing;

        Directory.CreateDirectory(runtimeDirectory);
        progress?.Report("Finding local AI runtime…");
        var asset = await ResolveGitHubAssetAsync(cancellationToken);
        if (asset == null)
            throw new InvalidOperationException("Could not find a llama.cpp runtime for this system.");

        progress?.Report($"Downloading {asset.Value.FileName}…");
        var archive = Path.Combine(runtimeDirectory, asset.Value.FileName);
        await DownloadFileAsync(asset.Value.Url, archive, expectedSha256: null, expectedSize: 0, progress, cancellationToken);

        progress?.Report("Extracting local AI runtime…");
        var pkg = Path.Combine(runtimeDirectory, "pkg");
        if (Directory.Exists(pkg))
            Directory.Delete(pkg, recursive: true);
        Directory.CreateDirectory(pkg);
        ExtractArchive(archive, pkg);
        try { File.Delete(archive); }
        catch { /* keep the archive if another process holds it */ }

        var binary = FindLlamaServerBinary(pkg)
                     ?? throw new InvalidOperationException("llama-server was not in the runtime archive.");
        MakeExecutable(binary);
        var binDir = Path.GetDirectoryName(binary);
        if (!string.IsNullOrEmpty(binDir))
        {
            foreach (var file in Directory.EnumerateFiles(binDir))
                MakeExecutable(file);
        }

        await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, "VERSION"), asset.Value.FileName, cancellationToken);
        return binary;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<(string Url, string FileName)?> ResolveGitHubAssetAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleasesUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return SelectRuntimeAsset(doc.RootElement);
    }

    private static async Task WriteDownloadAsync(
        HttpResponseMessage response,
        string partPath,
        bool append,
        long existing,
        long expectedSize,
        string displayName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        var total = expectedSize > 0
            ? expectedSize
            : contentLength.HasValue ? contentLength.Value + existing : 0;
        await using var output = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[256 * 1024];
        long received = existing;
        var lastReport = DateTime.UtcNow;
        progress?.Report(total > 0
            ? $"Downloading {displayName}… {LocalLlamaCatalog.FormatBytes(received)} / {LocalLlamaCatalog.FormatBytes(total)}"
            : $"Downloading {displayName}…");
        while (true)
        {
            var n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (n == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            received += n;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 400)
            {
                progress?.Report(total > 0
                    ? $"Downloading {displayName}… {LocalLlamaCatalog.FormatBytes(received)} / {LocalLlamaCatalog.FormatBytes(total)}"
                    : $"Downloading {displayName}… {LocalLlamaCatalog.FormatBytes(received)}");
                lastReport = DateTime.UtcNow;
            }
        }

        await output.FlushAsync(cancellationToken);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
