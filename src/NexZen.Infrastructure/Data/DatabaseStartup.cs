namespace NexZen.Infrastructure.Data;

/// <summary>
/// Lets the database be created and upgraded on a background thread while the window and
/// browser start. Every <see cref="NexZenDbContext"/> made elsewhere waits until that is done,
/// so nothing reads or writes a table before it exists.
/// </summary>
public static class DatabaseStartup
{
    private static Task _ready = Task.CompletedTask;

    [ThreadStatic]
    private static bool _initializingThread;

    public static Task Ready => _ready;

    /// <summary>Runs <paramref name="initialize"/> on a worker thread. Failures are passed to <paramref name="onError"/>.</summary>
    public static Task Begin(Action initialize, Action<Exception> onError)
    {
        _ready = Task.Run(() =>
        {
            _initializingThread = true;
            try
            {
                initialize();
            }
            catch (Exception ex)
            {
                onError(ex);
            }
            finally
            {
                _initializingThread = false;
            }
        });
        return _ready;
    }

    /// <summary>Blocks until the background setup finished; returns at once on the setup thread itself.</summary>
    public static void WaitUntilReady()
    {
        if (_initializingThread || _ready.IsCompleted)
            return;
        _ready.GetAwaiter().GetResult();
    }
}
