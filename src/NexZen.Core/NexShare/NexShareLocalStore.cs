using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NexZen.Core.NexShare;

/// <summary>
/// Persistent NexShare state in app data (~/.local/share/NexZen on Linux):
/// a stable device id (so phones recognise this PC across launches) and the trusted-device list
/// (peers that completed a mutual SAS confirm, like Android's NexShareTrustedDeviceStore).
/// </summary>
public sealed class NexShareLocalStore
{
    private static readonly Lazy<NexShareLocalStore> _shared = new(() => new NexShareLocalStore(DefaultDirectory()));
    public static NexShareLocalStore Shared => _shared.Value;

    private readonly object _gate = new();
    private readonly string _idPath;
    private readonly string _trustPath;
    private HashSet<string>? _trusted;
    private string? _deviceId;

    public NexShareLocalStore(string directory)
    {
        Directory = directory;
        _idPath = Path.Combine(directory, "nexshare-device-id");
        _trustPath = Path.Combine(directory, "nexshare-trusted.json");
    }

    public string Directory { get; }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZen");

    public string DeviceId
    {
        get
        {
            lock (_gate)
            {
                if (_deviceId != null) return _deviceId;
                try
                {
                    if (File.Exists(_idPath))
                    {
                        var s = File.ReadAllText(_idPath).Trim();
                        if (s.Length >= 8 && s.Length <= 64) return _deviceId = s;
                    }
                }
                catch { }
                _deviceId = Guid.NewGuid().ToString("N");
                try { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(_idPath, _deviceId); }
                catch (Exception ex) { Console.WriteLine($"[NexShare] Could not persist device id: {ex.Message}"); }
                return _deviceId;
            }
        }
    }

    private HashSet<string> Trusted()
    {
        if (_trusted != null) return _trusted;
        _trusted = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(_trustPath))
                foreach (var id in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_trustPath)) ?? new())
                    if (!string.IsNullOrWhiteSpace(id)) _trusted.Add(id);
        }
        catch { }
        return _trusted;
    }

    public bool IsTrusted(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        lock (_gate) return Trusted().Contains(deviceId);
    }

    public void AddTrusted(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        lock (_gate)
        {
            if (!Trusted().Add(deviceId)) return;
            Save();
        }
    }

    public void RemoveTrusted(string deviceId)
    {
        lock (_gate) { if (Trusted().Remove(deviceId)) Save(); }
    }

    public void ClearTrusted()
    {
        lock (_gate) { Trusted().Clear(); Save(); }
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(_trustPath, JsonSerializer.Serialize(new List<string>(_trusted!)));
        }
        catch (Exception ex) { Console.WriteLine($"[NexShare] Could not save trusted devices: {ex.Message}"); }
    }
}
