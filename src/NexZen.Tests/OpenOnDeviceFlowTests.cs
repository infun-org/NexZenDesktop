using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NexZen.Application.Services;
using NexZen.Core.NexShare.Models;
using Xunit;

namespace NexZen.Tests;

public class OpenOnDeviceFlowTests
{
    private sealed class Fake
    {
        public long Now;
        public List<DiscoveryMessage> Devices = new();
        public List<(string Msg, bool Err)> Notes = new();
        public List<string> Sent = new();
        public bool SendResult = true;
        public DiscoveryMessage? PickResult;
        public int Picks;

        public OpenOnDeviceFlow Flow(System.Func<bool>? superseded = null) => new()
        {
            GetReachable = () => Task.FromResult<IReadOnlyList<DiscoveryMessage>>(Devices.ToList()),
            Send = (d, u, t) => { Sent.Add(d.DeviceId + "|" + u); return Task.FromResult(SendResult); },
            Notify = (m, e) => Notes.Add((m, e)),
            Pick = l => { Picks++; return Task.FromResult(PickResult); },
            Delay = ms => { Now += ms; return Task.CompletedTask; },
            Clock = () => Now,
            Log = _ => { },
            IsSuperseded = superseded ?? (() => false),
        };
    }

    private static DiscoveryMessage LanOnly(string id) =>
        new() { DeviceId = id, DeviceName = "CPH2481", Host = "192.168.88.54", TcpPort = 43000 };

    [Fact]
    public async Task Single_lan_only_device_is_sent_and_reported()
    {
        var f = new Fake(); f.Devices.Add(LanOnly("phone1"));
        var r = await f.Flow().RunAsync("https://example.com", "t");
        Assert.Equal(OpenOnDeviceOutcome.Sent, r);
        Assert.Equal(new[] { "phone1|https://example.com" }, f.Sent);
        Assert.Contains(f.Notes, n => n.Msg.StartsWith("Sent the link to CPH2481"));
        Assert.InRange(f.Now, 2500, 8000);
    }

    [Fact]
    public async Task Nothing_found_reports_not_found()
    {
        var f = new Fake();
        var r = await f.Flow().RunAsync("https://example.com", "t");
        Assert.Equal(OpenOnDeviceOutcome.NotFound, r);
        Assert.Equal("No nearby NexShare devices found", f.Notes.Last().Msg);
        Assert.True(f.Now >= 8000);
    }

    [Fact]
    public async Task Send_failure_is_reported()
    {
        var f = new Fake { SendResult = false }; f.Devices.Add(LanOnly("p"));
        Assert.Equal(OpenOnDeviceOutcome.SendFailed, await f.Flow().RunAsync("http://a.b", "t"));
        Assert.True(f.Notes.Last().Err);
    }

    [Fact]
    public async Task Two_devices_open_picker_and_send_choice_or_report_cancel()
    {
        var f = new Fake(); f.Devices.Add(LanOnly("a")); f.Devices.Add(LanOnly("b"));
        f.PickResult = f.Devices[1];
        Assert.Equal(OpenOnDeviceOutcome.Sent, await f.Flow().RunAsync("http://x.y", "t"));
        Assert.Equal("b|http://x.y", f.Sent.Single());
        f.PickResult = null;
        Assert.Equal(OpenOnDeviceOutcome.PickCancelled, await f.Flow().RunAsync("http://x.y", "t"));
        Assert.Equal("Link send cancelled.", f.Notes.Last().Msg);
    }

    [Fact]
    public async Task Invalid_url_is_reported()
    {
        var f = new Fake();
        Assert.Equal(OpenOnDeviceOutcome.InvalidUrl, await f.Flow().RunAsync("about:blank", "t"));
        Assert.True(f.Notes.Single().Err);
    }

    [Fact]
    public async Task Exception_is_reported_not_swallowed()
    {
        var notes = new List<string>();
        var flow = new OpenOnDeviceFlow
        {
            GetReachable = () => throw new System.InvalidOperationException("boom"),
            Send = (_, _, _) => Task.FromResult(true),
            Notify = (m, _) => notes.Add(m),
            Pick = _ => Task.FromResult<DiscoveryMessage?>(null),
            Log = _ => { },
        };
        Assert.Equal(OpenOnDeviceOutcome.Error, await flow.RunAsync("https://a.b", "t"));
        Assert.Contains("boom", notes.Last());
    }
}
