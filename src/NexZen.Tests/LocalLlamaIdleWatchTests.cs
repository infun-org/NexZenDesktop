using NexZen.Application.Services;
using Xunit;

namespace NexZen.Tests;

public class LocalLlamaIdleWatchTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Stops_after_five_minutes_hidden_and_not_busy()
    {
        var watch = new LocalLlamaIdleWatch(TimeSpan.FromMinutes(5));
        watch.SetVisible(false, T0);
        Assert.False(watch.TryConsumeStop(T0.AddMinutes(4).AddSeconds(59)));
        Assert.True(watch.TryConsumeStop(T0.AddMinutes(5)));
        Assert.False(watch.TryConsumeStop(T0.AddMinutes(10)));
    }

    [Fact]
    public void Visible_window_does_not_stop()
    {
        var watch = new LocalLlamaIdleWatch(TimeSpan.FromMinutes(5));
        watch.SetVisible(true, T0);
        Assert.False(watch.TryConsumeStop(T0.AddMinutes(30)));
    }

    [Fact]
    public void Busy_delays_idle_until_work_finishes()
    {
        var watch = new LocalLlamaIdleWatch(TimeSpan.FromMinutes(5));
        watch.SetVisible(false, T0);
        watch.SetBusy(true, T0.AddMinutes(1));
        Assert.False(watch.TryConsumeStop(T0.AddMinutes(10)));
        watch.SetBusy(false, T0.AddMinutes(10));
        Assert.False(watch.TryConsumeStop(T0.AddMinutes(14)));
        Assert.True(watch.TryConsumeStop(T0.AddMinutes(15)));
    }

    [Fact]
    public void Showing_the_window_resets_the_idle_clock()
    {
        var watch = new LocalLlamaIdleWatch(TimeSpan.FromMinutes(5));
        watch.SetVisible(false, T0);
        watch.SetVisible(true, T0.AddMinutes(4));
        watch.SetVisible(false, T0.AddMinutes(4));
        Assert.False(watch.TryConsumeStop(T0.AddMinutes(8)));
        Assert.True(watch.TryConsumeStop(T0.AddMinutes(9)));
    }
}
