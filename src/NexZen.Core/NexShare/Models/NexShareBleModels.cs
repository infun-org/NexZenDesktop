using System;
using System.Collections.Generic;
using System.Threading;

namespace NexZen.Core.NexShare.Models;

/// <summary>A pending 6-digit SAS comparison. Confirm/Reject are idempotent; the first call wins.</summary>
public sealed class SasPrompt
{
    public const int TimeoutSeconds = 60;
    private readonly Action _confirm;
    private readonly Action _reject;
    private int _done;

    public SasPrompt(string peerId, string? peerName, string code, Action confirm, Action reject)
    {
        PeerId = peerId;
        PeerName = peerName;
        Code = code;
        _confirm = confirm;
        _reject = reject;
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
    }

    public string PeerId { get; }
    public string? PeerName { get; }
    public string Code { get; }
    /// <summary>"233 994" like Android.</summary>
    public string DisplayCode => Code.Length == 6 ? Code[..3] + " " + Code[3..] : Code;
    public DateTimeOffset ExpiresAt { get; }
    public bool IsDone => Volatile.Read(ref _done) != 0;

    public void Confirm() { if (Interlocked.Exchange(ref _done, 1) == 0) _confirm(); }
    public void Reject() { if (Interlocked.Exchange(ref _done, 1) == 0) _reject(); }
}

public sealed class BleVerifiedEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required byte[] SessionKey { get; init; }
    public required string SessionId { get; init; }
    public IReadOnlyList<string> PeerCaps { get; init; } = Array.Empty<string>();
    public bool IsInitiator { get; init; }
    /// <summary>Session ends with ble_url/ble_url_ack; no Wi-Fi Direct credentials will follow.</summary>
    public bool IsLinkOnly { get; init; }
}

public sealed class BleCredentialsEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required WireP2pCredentials Credentials { get; init; }
    public required byte[] SessionKey { get; init; }
    public required string SessionId { get; init; }
}

public enum BleLinkResult
{
    /// <summary>Peer acked ble_url; no Wi-Fi Direct involved.</summary>
    Delivered,
    /// <summary>Peer lacks ble_link: the handshake was verified and continues with Wi-Fi Direct credentials.</summary>
    FellBackToWifiDirect,
    Failed
}

public sealed class BleLinkReceivedEventArgs : EventArgs
{
    public required string PeerId { get; init; }
    public required WireBleUrl Link { get; init; }
}

public sealed class BleHandshakeFailedEventArgs : EventArgs
{
    public string? PeerId { get; init; }
    public string Reason { get; init; } = "";
}
