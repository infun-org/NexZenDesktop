using System;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Infrastructure.NexShare.Ble;

/// <summary>Used on non-Linux platforms until a WinRT BLE implementation exists.</summary>
public sealed class NoOpBleService : INexShareBleService
{
    public bool IsAvailable => false;
    public Func<string, bool>? TrustPredicate { get; set; }
#pragma warning disable CS0067 // events are part of the contract but never raised here
    public event EventHandler<DiscoveryMessage>? OnDeviceDiscovered;
    public event EventHandler<SasPrompt>? OnSasRequested;
    public event EventHandler<BleVerifiedEventArgs>? OnVerified;
    public event EventHandler<BleCredentialsEventArgs>? OnCredentials;
    public event EventHandler<BleHandshakeFailedEventArgs>? OnHandshakeFailed;
    public event EventHandler<string>? OnStatus;
    public event EventHandler<BleLinkReceivedEventArgs>? OnLinkReceived;
#pragma warning restore CS0067
    public void Configure(string myDeviceId, string myDeviceName, int wsPort) { }
    public Task<bool> StartHandshakeAsync(string bleAddress, string peerId) => Task.FromResult(false);
    public Task<BleLinkResult> SendLinkAsync(string bleAddress, string peerId, WireBleUrl link) => Task.FromResult(BleLinkResult.Failed);
    public Task StartListeningAsync() => Task.CompletedTask;
    public Task StopListeningAsync() => Task.CompletedTask;
    public Task BroadcastPresenceAsync(DiscoveryMessage message) => Task.CompletedTask;
}
