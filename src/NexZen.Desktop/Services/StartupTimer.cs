using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NexZen.Desktop.Services;

/// <summary>
/// Startup milestones as "[Startup] +1234ms step", measured from process start
/// (so .NET runtime boot is included). Written to stdout and startup.log.
/// </summary>
internal static class StartupTimer
{
    private static readonly DateTime ProcessStart = ReadProcessStart();
    private static readonly HashSet<string> Seen = new();
    private static readonly object Gate = new();

    public static void Mark(string step)
    {
        var ms = (long)(DateTime.Now - ProcessStart).TotalMilliseconds;
        var line = $"[Startup] +{ms}ms {step}";
        Console.WriteLine(line);
        Program.StartupLog.Write(line);
    }

    /// <summary>Marks a step that can fire many times (per tab, per frame) only the first time.</summary>
    public static void MarkOnce(string step)
    {
        lock (Gate)
        {
            if (!Seen.Add(step))
                return;
        }
        Mark(step);
    }

    private static DateTime ReadProcessStart()
    {
        try { return Process.GetCurrentProcess().StartTime; }
        catch { return DateTime.Now; }
    }
}
