using System.Net;

namespace NexZen.Core.AI;

public sealed record LocalLlamaDownload(
    string HfRepo,
    string Sha256,
    long SizeBytes,
    string? MmprojSha256 = null,
    long MmprojSizeBytes = 0);

public sealed record LocalLlamaModel(
    string Id,
    string Alias,
    string Label,
    string FileName,
    string? MmprojFileName = null,
    bool GeneratesImages = false,
    string[]? ExtraAliases = null,
    LocalLlamaDownload? Download = null)
{
    public bool IsInstalled { get; init; } = true;

    public override string ToString() => IsInstalled ? Label : Label + " · download";
}

public sealed record LocalLlamaSwitchResult(bool Ok, string Message, string? Alias);

public readonly record struct LocalLlamaFileNeed(
    string FileName,
    string? HfRepo,
    string? Sha256,
    long SizeBytes);

public static class LocalLlamaCatalog
{
    public static readonly LocalLlamaModel[] Known =
    [
        new(
            "qwen35-2b",
            "qwen35-2b-unc",
            "Fast (Text Only)",
            "Qwen3.5-2B-Uncensored-Zero-Refusal-Q4_K_M.gguf",
            Download: new LocalLlamaDownload(
                "AkaumiKaede/Qwen3.5-2B-Uncensored-Zero-Refusal-GGUF",
                "fb9fd7c051fd6d8666618605da5056b0a6404b057cf03d74b263eb4de7e36b12",
                1274396256)),
        new(
            "bonsai2-4b",
            "bonsai2-4b",
            "Medium",
            "Ternary-Bonsai-2-4B-Q2_0.gguf",
            "Ternary-Bonsai-2-4B-mmproj-Q8_0.gguf",
            false,
            ["ternary-bonsai-2-4b", "bonsai-2-4b", "ternary-bonsai2-4b"],
            new LocalLlamaDownload(
                "prism-ml/Ternary-Bonsai-2-4B-gguf",
                "64a5183b44921bdb5ac4a6f5f92007821d6ebe5c66997d3c147fd214ab9433d4",
                1212103520,
                "db939d5f9b7014137810205e91d05e5f54afd72b0b3af9d33bd7d82f7906a55d",
                366894624)),
        new(
            "bonsai2-9b",
            "bonsai2-9b",
            "High",
            "Ternary-Bonsai-2-9B-Q2_0.gguf",
            "Ternary-Bonsai-2-9B-mmproj-Q8_0.gguf",
            false,
            ["ternary-bonsai-2-9b", "bonsai-2-9b", "ternary-bonsai2-9b"],
            new LocalLlamaDownload(
                "prism-ml/Ternary-Bonsai-2-9B-gguf",
                "1f884aa56aacd2bbbb2e4b7abaead2b1102b74a9c75a23fc33dab916813a384d",
                2556640128,
                "e0cb6f6988cea323ef798cd77d019bbb819af75582f034eaab0e7232314953db",
                624229664)),
    ];

    public static LocalLlamaModel? FindByIdOrAlias(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return null;
        return Known.FirstOrDefault(m =>
            string.Equals(m.Id, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Alias, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.FileName, text, StringComparison.OrdinalIgnoreCase)
            || (m.ExtraAliases != null && m.ExtraAliases.Any(a =>
                string.Equals(a, text, StringComparison.OrdinalIgnoreCase))));
    }

    public static IEnumerable<LocalLlamaFileNeed> FilesToDownload(LocalLlamaModel model)
    {
        yield return new(
            model.FileName,
            model.Download?.HfRepo,
            model.Download?.Sha256,
            model.Download?.SizeBytes ?? 0);
        if (!string.IsNullOrWhiteSpace(model.MmprojFileName))
        {
            yield return new(
                model.MmprojFileName,
                model.Download?.HfRepo,
                model.Download?.MmprojSha256,
                model.Download?.MmprojSizeBytes ?? 0);
        }
    }

    public static string HuggingFaceUrl(string repo, string fileName)
    {
        var cleanRepo = (repo ?? "").Trim().Trim('/');
        var cleanFile = (fileName ?? "").Trim().TrimStart('/');
        return $"https://huggingface.co/{cleanRepo}/resolve/main/{cleanFile}?download=true";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1024 && i < units.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : $"{value:0.##} {units[i]}";
    }

    public static bool IsLoopback(string? baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var host = uri.IdnHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    public static (string Host, int Port) LoopbackEndpoint(string? baseUrl)
    {
        if (Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var port = uri.IsDefaultPort ? 8080 : uri.Port;
            var host = string.IsNullOrWhiteSpace(uri.Host) ? "127.0.0.1" : uri.Host;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                host = "127.0.0.1";
            return (host, port);
        }

        return ("127.0.0.1", 8080);
    }
}

public interface ILocalLlamaServer
{
    IReadOnlyList<LocalLlamaModel> ListInstalled();
    Task<string?> ProbeRunningAliasAsync(string? baseUrl, CancellationToken cancellationToken = default);
    Task<LocalLlamaSwitchResult> SwitchAsync(
        LocalLlamaModel model,
        string? baseUrl,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
    Task StopAsync();
}

public sealed record LocalImageResult(bool Ok, string? ImagePath, string? Text, string? Error);

public interface ILocalImageGenerator
{
    bool IsReady { get; }
    bool Supports(LocalLlamaModel model);
    Task<LocalImageResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}
