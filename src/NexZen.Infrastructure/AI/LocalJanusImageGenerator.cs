using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public sealed class LocalJanusImageGenerator : ILocalImageGenerator
{
    private readonly string _wrapperPath;
    private readonly string _modelDir;
    private readonly string _outputDir;

    public LocalJanusImageGenerator(
        string? wrapperPath = null,
        string? modelDir = null,
        string? outputDir = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(data, "NexZenDesktop");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _wrapperPath = wrapperPath ?? Path.Combine(home, ".local", "bin", "nexzen-janus-t2i");
        _modelDir = modelDir ?? Path.Combine(root, "janus-t2i", "model");
        _outputDir = outputDir ?? Path.Combine(root, "ai-images");
    }

    public bool IsReady =>
        File.Exists(_wrapperPath)
        && File.Exists(Path.Combine(_modelDir, "model.safetensors"))
        && File.Exists(Path.Combine(_modelDir, "config.json"));

    public bool Supports(LocalLlamaModel model) => model.GeneratesImages;

    public async Task<LocalImageResult> GenerateAsync(
        string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return new LocalImageResult(false, null, null, "Type a prompt to generate an image.");
        if (!File.Exists(_wrapperPath))
            return new LocalImageResult(false, null, null, "Missing nexzen-janus-t2i. Janus text-to-image is not installed.");
        if (!File.Exists(Path.Combine(_modelDir, "model.safetensors")))
            return new LocalImageResult(false, null, null, "Missing Janus-Pro-1B weights. Place model.safetensors under janus-t2i/model.");

        Directory.CreateDirectory(_outputDir);
        var dest = Path.Combine(_outputDir, $"janus-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = _wrapperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        psi.ArgumentList.Add("--prompt");
        psi.ArgumentList.Add(prompt.Trim());
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(dest);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(_modelDir);

        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stdout.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stderr.AppendLine(e.Data);
            };
            if (!proc.Start())
                return new LocalImageResult(false, null, null, "Janus text-to-image did not start.");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            using var cancelReg = cancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* ignore */ }
            });
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var parsed = ParseResult(stdout.ToString(), dest);
            if (proc.ExitCode == 0 && parsed.Ok)
                return parsed;
            var error = parsed.Error
                ?? FirstErrorLine(stderr.ToString())
                ?? $"Janus text-to-image failed (exit {proc.ExitCode}).";
            return new LocalImageResult(false, null, null, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalImageResult(false, null, null, "Could not run Janus text-to-image: " + ex.Message);
        }
    }

    private static LocalImageResult ParseResult(string stdout, string expectedPath)
    {
        foreach (var raw in stdout.Split('\n').Reverse())
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                var path = root.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                    ? pathEl.GetString()
                    : null;
                var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString()
                    : null;
                if (ok && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return new LocalImageResult(true, path, null, null);
                if (!string.IsNullOrWhiteSpace(error))
                    return new LocalImageResult(false, null, null, error);
            }
            catch (JsonException)
            {
                // keep scanning
            }
        }

        if (File.Exists(expectedPath))
            return new LocalImageResult(true, expectedPath, null, null);
        return new LocalImageResult(false, null, null, "Janus did not write an image.");
    }

    private static string? FirstErrorLine(string stderr)
    {
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("Traceback", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("File ", StringComparison.Ordinal))
                continue;
            return line.Length > 240 ? line[..240] : line;
        }
        return null;
    }
}
