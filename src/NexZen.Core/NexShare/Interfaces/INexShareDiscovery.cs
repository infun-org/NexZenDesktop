using System;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Interfaces;

public interface INexShareDiscovery
{
    /// <summary>
    /// Fired when a new device broadcasts its presence.
    /// </summary>
    event EventHandler<DiscoveryMessage> OnDeviceDiscovered;

    /// <summary>
    /// Starts listening for incoming discovery UDP broadcasts.
    /// </summary>
    Task StartListeningAsync();

    /// <summary>
    /// Stops listening for discovery broadcasts.
    /// </summary>
    Task StopListeningAsync();

    /// <summary>
    /// Broadcasts our own presence to the local network.
    /// </summary>
    Task BroadcastPresenceAsync(DiscoveryMessage message);
}
