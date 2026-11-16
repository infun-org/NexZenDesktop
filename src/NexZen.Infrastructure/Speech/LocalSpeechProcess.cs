using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NexZen.Infrastructure.Speech;

/// <summary>
/// A local speech server process (zonos3-server, omnivoice tts-server): started on a free
/// localhost port when first needed, probed until it answers, logged to a file, and stopped after
/// a while without requests (freeing its memory) or when NexZen exits.
/// </summary>
public sealed class LocalSpeechProcess : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

    private readonly string _name;
    private readonly string _logPath;
    private readonly string _healthPath;
    private readonly TimeSpan _idleStop;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Timer _idleTimer;
    private Process? _process;
    private Uri? _baseUri;
    private int _activeRequests;
    private DateTime _lastUse = DateTime.UtcNow;

    public LocalSpeechProcess(string name, string logPath, string healthPath, TimeSpan idleStop)
    {
        _name = name;
        _logPath = logPath;
        _healthPath = healthPath;
        _idleStop = idleStop;
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public HttpClient Http { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>A value describing the running process (e.g. which optional models it loaded).</summary>
    public object? StartedWith { get; private set; }

    public bool IsRunning => _baseUri != null && _process is { HasExited: false };

    /// <summary>
    /// Base address of the running server, starting it when needed. <paramref name="arguments"/> gets the
    /// port; <paramref name="startedWith"/> records what this start loaded, and a running server whose
    /// record fails <paramref name="usable"/> is restarted.
    /// </summary>
    public async Task<Uri> EnsureStartedAsync(string executable, string workingDirectory, Func<int, IEnumerable<string>> arguments,
        object? startedWith, Func<object?, bool>? usable, CancellationToken cancellationToken)
    {
        _lastUse = DateTime.UtcNow;
        bool Usable() => IsRunning && (usable == null || usable(StartedWith));
        if (Usable())
            return _baseUri!;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Usable())
                return _baseUri!;
            StopProcess();
            var port = FreePort();
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                // A pipe NexZen closes right away: tools the server runs (ffmpeg reads stdin for
                // commands) see end of input instead of a terminal, where a background process
                // reading from it would be stopped (SIGTTIN) and hang forever.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory,
            };
            foreach (var arg in arguments(port))
                psi.ArgumentList.Add(arg);
            var process = Process.Start(psi) ?? throw new InvalidOperationException(_name + " did not start");
            var log = new StreamWriter(_logPath, append: false) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            process.Exited += (_, _) => { lock (log) log.Dispose(); };
            process.EnableRaisingEvents = true;
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"{_name} exited ({process.ExitCode}); see {_logPath}");
                try
                {
                    using var probe = await Http.GetAsync(new Uri(baseUri, _healthPath), cancellationToken).ConfigureAwait(false);
                    if (probe.IsSuccessStatusCode)
                    {
                        _baseUri = baseUri;
                        StartedWith = startedWith;
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
            throw new TimeoutException(_name + " did not answer within " + StartTimeout.TotalSeconds + " s");
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Marks a request as running, so the idle timer does not stop the server under it.</summary>
    public IDisposable Use()
    {
        Interlocked.Increment(ref _activeRequests);
        _lastUse = DateTime.UtcNow;
        return new Release(this);
    }

    private sealed class Release(LocalSpeechProcess owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            owner._lastUse = DateTime.UtcNow;
            Interlocked.Decrement(ref owner._activeRequests);
        }
    }

    private void StopIfIdle()
    {
        if (_process == null || Volatile.Read(ref _activeRequests) > 0 || DateTime.UtcNow - _lastUse < _idleStop)
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

    /// <summary>Stops the server unless a request is running on it (another window may be using it).</summary>
    public bool StopIfUnused()
    {
        if (Volatile.Read(ref _activeRequests) > 0 || !_startGate.Wait(0))
            return false;
        try
        {
            if (Volatile.Read(ref _activeRequests) > 0)
                return false;
            StopProcess();
            return true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Stop()
    {
        _startGate.Wait();
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
        StartedWith = null;
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

    public void Dispose()
    {
        _idleTimer.Dispose();
        StopProcess();
        Http.Dispose();
    }
}
