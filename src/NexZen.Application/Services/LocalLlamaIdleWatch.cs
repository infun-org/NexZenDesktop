namespace NexZen.Application.Services;

public sealed class LocalLlamaIdleWatch
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _timeout;
    private bool _visible = true;
    private bool _busy;
    private bool _stopped;
    private DateTime? _idleSinceUtc;

    public LocalLlamaIdleWatch(TimeSpan? timeout = null)
    {
        _timeout = timeout is { } value && value > TimeSpan.Zero
            ? value
            : DefaultTimeout;
    }

    public void SetVisible(bool visible, DateTime utcNow)
    {
        _visible = visible;
        Recalc(utcNow);
    }

    public void SetBusy(bool busy, DateTime utcNow)
    {
        _busy = busy;
        Recalc(utcNow);
    }

    public bool TryConsumeStop(DateTime utcNow)
    {
        Recalc(utcNow);
        if (_stopped || _idleSinceUtc is not { } start)
            return false;
        if (utcNow - start < _timeout)
            return false;
        _stopped = true;
        return true;
    }

    private void Recalc(DateTime utcNow)
    {
        if (_visible || _busy)
        {
            _idleSinceUtc = null;
            _stopped = false;
            return;
        }

        _idleSinceUtc ??= utcNow;
    }
}
