using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Interfaces;

public sealed class WfdJoinResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    /// <summary>True when the failure was a polkit / NetworkManager permission denial.</summary>
    public bool PermissionDenied { get; init; }
    public string? LocalIp { get; init; }
    public string? InterfaceName { get; init; }
    public static WfdJoinResult Fail(string error, bool denied = false) => new() { Success = false, Error = error, PermissionDenied = denied };
}

/// <summary>
/// Joins an Android Wi-Fi Direct "legacy" group (DIRECT-NX-xxxx, WPA2-PSK) as a normal Wi-Fi client,
/// and leaves it again (restoring whatever Wi-Fi connection was active before).
/// </summary>
public interface IWifiDirectJoiner
{
    bool IsSupported { get; }
    Task<WfdJoinResult> JoinAsync(WireP2pCredentials credentials, CancellationToken ct = default);
    /// <summary>Idempotent. Deactivates/deletes the temporary connection and restores the previous one.</summary>
    Task LeaveAsync();
}
