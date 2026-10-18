using System;

namespace NexZen.Application.Services;

public sealed class DataClearRequest
{
    public bool History { get; set; }
    public bool Cookies { get; set; }
    public bool Cache { get; set; }
    public bool DownloadHistory { get; set; }
    public bool Autofill { get; set; }
    public bool SiteSettings { get; set; }
    public bool HostedAppData { get; set; }

    /// <summary>Null clears every stored time.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>Null clears every site.</summary>
    public string? Host { get; set; }

    /// <summary>Absolute Chromium user-data directory, captured while CEF is running.</summary>
    public string? UserDataDir { get; set; }
}
