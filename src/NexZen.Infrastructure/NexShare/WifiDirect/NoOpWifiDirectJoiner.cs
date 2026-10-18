using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Infrastructure.NexShare.WifiDirect;

public sealed class NoOpWifiDirectJoiner : IWifiDirectJoiner
{
    public bool IsSupported => false;
    public Task<WfdJoinResult> JoinAsync(WireP2pCredentials credentials, CancellationToken ct = default) =>
        Task.FromResult(WfdJoinResult.Fail("Wi-Fi Direct join is only implemented on Linux"));
    public Task LeaveAsync() => Task.CompletedTask;
}
