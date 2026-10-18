using System.Collections.Generic;
using NexZen.Core.NexShare;
using NexZen.Core.NexShare.Models;
using Xunit;

namespace NexZen.Tests;

public class RoutingTests
{
    private static DiscoveryMessage Lan(string id, string host = "192.168.1.20") =>
        new() { DeviceId = id, DeviceName = "OPPO", Host = host, TcpPort = 43000 };
    private static DiscoveryMessage Ble(string id, params string[] caps) =>
        new() { DeviceId = id, DeviceName = "OPPO", Host = "", TcpPort = 43000, ViaBle = true, BleAddress = "AA:BB:CC:DD:EE:FF", Caps = new List<string>(caps) };

    [Fact]
    public void Lan_and_ble_sightings_merge_into_one_entry_with_both_badges()
    {
        var reg = new NexShareDeviceRegistry();
        reg.Upsert(Lan("p1"), 1_000);
        var e = reg.Upsert(Ble("p1", NexShareCaps.WfdLegacy), 2_000);
        Assert.Single(reg.Snapshot());
        Assert.Equal("192.168.1.20", e.Device.Host);
        Assert.Equal("AA:BB:CC:DD:EE:FF", e.Device.BleAddress);
        Assert.True(e.LanFresh(3_000));
        Assert.True(e.BleFresh(3_000));
    }

    [Fact]
    public void Lan_goes_stale_after_15s_but_ble_stays()
    {
        var e = new NexShareDeviceEntry(Lan("p1"), 0);
        e.Touch(Ble("p1", NexShareCaps.WfdLegacy), 0);
        e.Touch(Ble("p1", NexShareCaps.WfdLegacy), 20_000);
        Assert.False(e.LanFresh(20_000));
        Assert.True(e.BleFresh(20_000));
        var view = e.RoutableView(20_000);
        Assert.Equal("", view.Host);
        Assert.Equal(NexShareRoute.BleWifiDirect, NexShareRouting.Choose(view, false, false, bleAvailable: true));
    }

    [Fact]
    public void Prune_removes_unseen_entries_but_keeps_connected()
    {
        var reg = new NexShareDeviceRegistry();
        reg.Upsert(Lan("old"), 0);
        reg.Upsert(Lan("conn"), 0);
        reg.Upsert(Lan("new"), 50_000);
        var removed = reg.Prune(NexShareDeviceEntry.RemoveAfterMs + 1, id => id == "conn");
        Assert.Equal(new[] { "old" }, removed);
        Assert.Equal(2, reg.Snapshot().Count);
    }

    [Fact]
    public void Lan_preferred_when_reachable()
    {
        var d = DiscoveryMessage.Merge(Lan("p1"), Ble("p1", NexShareCaps.WfdLegacy));
        Assert.Equal(NexShareRoute.Lan, NexShareRouting.Choose(d, lanFailed: false, trusted: false, bleAvailable: true));
    }

    [Fact]
    public void Lan_failure_falls_back_to_ble_wfd()
    {
        var d = DiscoveryMessage.Merge(Lan("p1"), Ble("p1", NexShareCaps.WfdLegacy));
        Assert.Equal(NexShareRoute.BleWifiDirect, NexShareRouting.Choose(d, lanFailed: true, trusted: false, bleAvailable: true));
        Assert.Equal(NexShareRoute.BleWifiDirect, NexShareRouting.FallbackAfterLanFailure(d, true));
        Assert.Equal(NexShareRoute.None, NexShareRouting.FallbackAfterLanFailure(d, false));
        // no BLE path: LAN is retried
        Assert.Equal(NexShareRoute.Lan, NexShareRouting.Choose(Lan("p1"), lanFailed: true, trusted: false, bleAvailable: true));
    }

    [Fact]
    public void Untrusted_wfd_subnet_host_is_not_used_for_lan()
    {
        var d = DiscoveryMessage.Merge(Lan("p1", "192.168.49.1"), Ble("p1", NexShareCaps.WfdLegacy));
        Assert.Equal(NexShareRoute.BleWifiDirect, NexShareRouting.Choose(d, false, trusted: false, bleAvailable: true));
        Assert.Equal(NexShareRoute.Lan, NexShareRouting.Choose(d, false, trusted: true, bleAvailable: true));
        Assert.Equal(NexShareRoute.None, NexShareRouting.Choose(Lan("p2", "192.168.49.1"), false, trusted: false, bleAvailable: true));
    }

    [Fact]
    public void Ble_without_wfd_legacy_or_without_adapter_has_no_route()
    {
        Assert.Equal(NexShareRoute.None, NexShareRouting.Choose(Ble("p1"), false, false, true));
        Assert.Equal(NexShareRoute.None, NexShareRouting.Choose(Ble("p1", NexShareCaps.WfdLegacy), false, false, bleAvailable: false));
    }
}
