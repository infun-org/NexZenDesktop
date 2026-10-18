using System;

namespace NexZen.Core.Entities;

public class DownloadRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
