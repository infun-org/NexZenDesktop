using System.Diagnostics;
using System.Text.Json;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public sealed class LocalLlamaServer : ILocalLlamaServer, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly string _modelsDirectory;
    private readonly string _runtimeDirectory;
    private readonly string? _wrapperPath;
    private readonly bool _allowBootstrap;
    private readonly LocalLlamaBootstrap _bootstrap;
    private readonly bool _ownsBootstrap;
    private string? _llamaServerPath;
    private readonly string _logPath;
    private Process? _process;
    private StreamWriter? _log;

    public LocalLlamaServer(
        string? modelsDirectory = null,
        string? wrapperPath = null,
        string? llamaServerPath = null,
        LocalLlamaBootstrap? bootstrap = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(data, "NexZenDesktop");
        Directory.CreateDirectory(root);
        _modelsDirectory = modelsDirectory
            ?? Path.Combine(root, "models");
        _runtimeDirectory = Path.Combine(root, "runtime");
        _wrapperPath = wrapperPath ?? ResolveWrapper();
        _allowBootstrap = llamaServerPath == null;
        if (bootstrap != null)
        {
            _bootstrap = bootstrap;
            _ownsBootstrap = false;
        }
        else
        {
            _bootstrap = new LocalLlamaBootstrap();
            _ownsBootstrap = true;
        }
        _llamaServerPath = llamaServerPath ?? LocalLlamaBootstrap.FindInstalledServer(
            _runtimeDirectory, AppContext.BaseDirectory);
        _logPath = Path.Combine(root, "llama-server.log");
    }

    public IReadOnlyList<LocalLlamaModel> ListInstalled()
    {
        var list = new List<LocalLlamaModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirExists = Directory.Exists(_modelsDirectory);

        foreach (var known in LocalLlamaCatalog.Known)
        {
            var installed = dirExists && File.Exists(Path.Combine(_modelsDirectory, known.FileName));
            list.Add(known with { IsInstalled = installed });
            seen.Add(known.FileName);
        }

        if (!dirExists)
            return list;

        foreach (var file in Directory.EnumerateFiles(_modelsDirectory, "*.gguf"))
        {
            var name = Path.GetFileName(file);
            if (IsMmprojFile(name))
                continue;
            if (!seen.Add(name))
                continue;
            var stem = Path.GetFileNameWithoutExtension(name);
            var alias = SanitizeAlias(stem);
            list.Add(new LocalLlamaModel("gguf:" + name, alias, stem, name));
        }

        return list;
    }

    public async Task<string?> ProbeRunningAliasAsync(string? baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var (host, port) = LocalLlamaCatalog.LoopbackEndpoint(baseUrl);
            using var resp = await Http.GetAsync($"http://{host}:{port}/v1/models", cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return null;
    }

    public async Task<LocalLlamaSwitchResult> SwitchAsync(
        LocalLlamaModel model,
        string? baseUrl,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var (host, port) = LocalLlamaCatalog.LoopbackEndpoint(baseUrl);
        var current = await ProbeRunningAliasAsync(baseUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(current)
            && (string.Equals(current, model.Alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, model.Id, StringComparison.OrdinalIgnoreCase)))
            return new LocalLlamaSwitchResult(true, $"Already running {model.Label}.", model.Alias);

        try
        {
            await EnsureRuntimeAsync(progress, cancellationToken);
            await EnsureModelAsync(model, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalLlamaSwitchResult(false, ex.Message, null);
        }

        var gguf = Path.Combine(_modelsDirectory, model.FileName);
        if (!File.Exists(gguf))
            return new LocalLlamaSwitchResult(false, $"Missing GGUF: {model.FileName}", null);

        try
        {
            progress?.Report($"Starting {model.Label}…");
            StopServer();
            await WaitPortFreeAsync(host, port, cancellationToken);
            if (model.GeneratesImages)
            {
                return new LocalLlamaSwitchResult(
                    true,
                    $"Ready for text-to-image · {model.Label}. Chat prompts generate 384×384 images.",
                    model.Alias);
            }

            StartServer(model, gguf, host, port);
            var ready = await WaitReadyAsync(model.Alias, baseUrl, TimeSpan.FromSeconds(90), cancellationToken);
            if (ready == null)
                return new LocalLlamaSwitchResult(false, $"Timed out starting {model.Label}.", null);
            return new LocalLlamaSwitchResult(true, $"Local model · {model.Label}", ready);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LocalLlamaSwitchResult(false, $"Could not start {model.Label}: {ex.Message}", null);
        }
    }

    public Task StopAsync()
    {
        StopServer();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _log?.Dispose(); }
        catch { /* ignore */ }
        _log = null;
        _process?.Dispose();
        _process = null;
        if (_ownsBootstrap)
            _bootstrap.Dispose();
    }

    private async Task EnsureRuntimeAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_wrapperPath) && File.Exists(_wrapperPath))
            return;
        if (!string.IsNullOrWhiteSpace(_llamaServerPath) && File.Exists(_llamaServerPath))
            return;
        var found = LocalLlamaBootstrap.FindInstalledServer(_runtimeDirectory, AppContext.BaseDirectory);
        if (found != null)
        {
            _llamaServerPath = found;
            return;
        }

        if (!_allowBootstrap)
            return;

        progress?.Report("Installing local AI runtime…");
        _llamaServerPath = await _bootstrap.EnsureRuntimeAsync(
            _runtimeDirectory, AppContext.BaseDirectory, progress, cancellationToken);
    }

    private async Task EnsureModelAsync(
        LocalLlamaModel model, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var gguf = Path.Combine(_modelsDirectory, model.FileName);
        var needsDownload = !File.Exists(gguf)
            || LocalLlamaCatalog.FilesToDownload(model).Any(file =>
                !LocalLlamaBootstrap.FileLooksComplete(Path.Combine(_modelsDirectory, file.FileName), file.SizeBytes));
        if (!needsDownload)
            return;
        if (model.Download == null || !_allowBootstrap)
        {
            if (!File.Exists(gguf))
                throw new InvalidOperationException($"Missing GGUF: {model.FileName}");
            return;
        }

        await _bootstrap.EnsureModelFilesAsync(model, _modelsDirectory, progress, cancellationToken);
    }

    private void StartServer(LocalLlamaModel model, string ggufPath, string host, int port)
    {
        Directory.CreateDirectory(_modelsDirectory);
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _modelsDirectory
        };
        psi.Environment["NEXZEN_LLAMA_HOST"] = host;
        psi.Environment["NEXZEN_LLAMA_PORT"] = port.ToString();

        var known = LocalLlamaCatalog.FindByIdOrAlias(model.Id)
            ?? LocalLlamaCatalog.FindByIdOrAlias(model.Alias);
        if (known != null && !string.IsNullOrWhiteSpace(_wrapperPath) && File.Exists(_wrapperPath))
        {
            psi.FileName = _wrapperPath;
            psi.ArgumentList.Add(known.Id);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_llamaServerPath) || !File.Exists(_llamaServerPath))
                throw new InvalidOperationException("llama-server binary was not found.");
            psi.FileName = _llamaServerPath;
            AddLibrarySearchPath(psi, _llamaServerPath);
            AddDirectArgs(psi, ggufPath, host, port, model.Alias, known, _modelsDirectory);
        }

        EnsureLog();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => WriteLog(e.Data);
        proc.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        if (!proc.Start())
            throw new InvalidOperationException("llama-server did not start.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        _process = proc;
    }

    private static void AddDirectArgs(
        ProcessStartInfo psi,
        string ggufPath,
        string host,
        int port,
        string alias,
        LocalLlamaModel? known,
        string modelsDirectory)
    {
        var knownId = known?.Id;
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(ggufPath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("16384");
        psi.ArgumentList.Add("--parallel");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("--alias");
        psi.ArgumentList.Add(alias);
        psi.ArgumentList.Add("-ngl");
        psi.ArgumentList.Add(GpuLayers());
        psi.ArgumentList.Add("--jinja");
        if (NeedsReasoningOff(knownId))
        {
            psi.ArgumentList.Add("--reasoning");
            psi.ArgumentList.Add("off");
        }
        if (!string.IsNullOrWhiteSpace(known?.MmprojFileName))
        {
            var mmproj = Path.Combine(modelsDirectory, known.MmprojFileName);
            if (File.Exists(mmproj))
            {
                psi.ArgumentList.Add("--mmproj");
                psi.ArgumentList.Add(mmproj);
            }
        }
    }

    private static string GpuLayers()
    {
        var ngl = Environment.GetEnvironmentVariable("NEXZEN_LLAMA_NGL");
        return string.IsNullOrWhiteSpace(ngl) ? "99" : ngl.Trim();
    }

    private static bool NeedsReasoningOff(string? knownId) =>
        knownId is "qwen35-2b" or "bonsai2-4b" or "bonsai2-9b";

    private void StopServer()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(4000);
            }
        }
        catch { /* ignore */ }

        foreach (var proc in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(4000);
            }
            catch { /* ignore */ }
            finally { proc.Dispose(); }
        }

        _process?.Dispose();
        _process = null;
    }

    private async Task WaitPortFreeAsync(string host, int port, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var resp = await Http.GetAsync($"http://{host}:{port}/v1/models", cancellationToken);
            }
            catch
            {
                return;
            }
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task<string?> WaitReadyAsync(
        string alias, string? baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
                return null;
            var id = await ProbeRunningAliasAsync(baseUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(id)
                && (string.Equals(id, alias, StringComparison.OrdinalIgnoreCase)
                    || LocalLlamaCatalog.FindByIdOrAlias(id)?.Alias == alias))
                return id;
            await Task.Delay(400, cancellationToken);
        }
        return null;
    }

    private void EnsureLog()
    {
        if (_log != null)
            return;
        var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _log = new StreamWriter(stream) { AutoFlush = true };
    }

    private void WriteLog(string? line)
    {
        if (string.IsNullOrEmpty(line) || _log == null)
            return;
        try { _log.WriteLine(line); }
        catch { /* ignore */ }
    }

    private static string? ResolveWrapper()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, ".local", "bin", "nexzen-gemma-local");
        if (File.Exists(local))
            return local;
        return FindOnPath("nexzen-gemma-local");
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

    private static void AddLibrarySearchPath(ProcessStartInfo psi, string binaryPath)
    {
        var binDir = Path.GetDirectoryName(binaryPath);
        if (string.IsNullOrEmpty(binDir))
            return;
        var parts = new List<string> { binDir };
        var parent = Path.GetDirectoryName(binDir);
        if (!string.IsNullOrEmpty(parent))
        {
            var lib = Path.Combine(parent, "lib");
            if (Directory.Exists(lib))
                parts.Add(lib);
        }

        var extra = string.Join(Path.PathSeparator, parts);
        if (OperatingSystem.IsWindows())
        {
            var path = psi.Environment.TryGetValue("PATH", out var existingPath) ? existingPath : "";
            psi.Environment["PATH"] = string.IsNullOrEmpty(path) ? extra : extra + Path.PathSeparator + path;
            return;
        }

        var ld = psi.Environment.TryGetValue("LD_LIBRARY_PATH", out var existing) ? existing : "";
        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(ld) ? extra : extra + ":" + ld;
    }

    private static bool IsMmprojFile(string fileName)
        => fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
           || LocalLlamaCatalog.Known.Any(m =>
               !string.IsNullOrWhiteSpace(m.MmprojFileName)
               && string.Equals(m.MmprojFileName, fileName, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeAlias(string stem)
    {
        var chars = stem.Trim().ToLowerInvariant().ToCharArray();
        var builder = new System.Text.StringBuilder(chars.Length);
        foreach (var ch in chars)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var alias = builder.ToString().Trim('-');
        return alias.Length == 0 ? "local-gguf" : alias;
    }
}
