using System;

namespace NexZen.Application.Services;

internal static class NexShareHost
{
    /// <summary>
    /// The Avalonia previewer loads this app and would otherwise open a second
    /// NexShare identity on the same LAN ports.
    /// </summary>
    public static bool IsPreviewer
    {
        get
        {
            var command = Environment.CommandLine;
            return command.Contains("Previewer.HostApp", StringComparison.OrdinalIgnoreCase)
                || command.Contains("avalonia-remote", StringComparison.OrdinalIgnoreCase);
        }
    }
}
