using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NexZen.Core.NexShare.Ble;

/// <summary>GATT layout shared with Android NexShareBleDiscovery.kt / NexShareBleHandshake.kt.</summary>
public static class NexShareBleUuids
{
    public const string Service = "0000aabb-0000-1000-8000-00805f9b34fb";
    /// <summary>READ: "id|wsPort|caps".</summary>
    public const string CharInfoRead = "0000bbcc-0000-1000-8000-00805f9b34fb";
    /// <summary>WRITE: peer writes its own "id|wsPort|caps".</summary>
    public const string CharInfoWrite = "0000ccdd-0000-1000-8000-00805f9b34fb";
    /// <summary>WRITE + NOTIFY: framed handshake messages.</summary>
    public const string CharHandshake = "0000ddee-0000-1000-8000-00805f9b34fb";
    public const string Cccd = "00002902-0000-1000-8000-00805f9b34fb";
    /// <summary>Service data value under <see cref="Service"/> in the advertisement.</summary>
    public const string MagicWord = "NEXSHARE";
    public const ushort ManufacturerId = 0xFFFF;
}

/// <summary>
/// Framing on 0xDDEE: each ATT value = 1 header byte + payload. 0x01 FIRST, 0x02 LAST (single fragment = 0x03).
/// Payload per fragment &lt;= ATT_MTU - 4. Max reassembled message 65536 bytes.
/// </summary>
public static class NexShareBleFraming
{
    public const byte FlagFirst = 0x01;
    public const byte FlagLast = 0x02;
    public const int MaxMessage = 65536;

    public const int MaxAttValue = 512;

    public static List<byte[]> Split(byte[] message, int attMtu)
    {
        // ATT values are limited to 512 bytes regardless of MTU (MTU 517 would give 514-byte frames).
        var maxPayload = Math.Max(1, Math.Min(attMtu - 3, MaxAttValue) - 1);
        var output = new List<byte[]>();
        var pos = 0;
        do
        {
            var len = Math.Min(maxPayload, message.Length - pos);
            byte flags = 0;
            if (pos == 0) flags |= FlagFirst;
            if (pos + len >= message.Length) flags |= FlagLast;
            var frame = new byte[len + 1];
            frame[0] = flags;
            Buffer.BlockCopy(message, pos, frame, 1, len);
            output.Add(frame);
            pos += len;
        } while (pos < message.Length);
        return output;
    }

    /// <summary>Same semantics as Android Reassembler: FIRST resets, frames without a started message are dropped.</summary>
    public sealed class Reassembler
    {
        private MemoryStream? _buf;

        public byte[]? Feed(byte[] frame)
        {
            if (frame.Length == 0) return null;
            var flags = frame[0];
            if ((flags & FlagFirst) != 0) _buf = new MemoryStream();
            var b = _buf;
            if (b == null) return null;
            b.Write(frame, 1, frame.Length - 1);
            if (b.Length > MaxMessage) { _buf = null; return null; }
            if ((flags & FlagLast) != 0)
            {
                _buf = null;
                return b.ToArray();
            }
            return null;
        }
    }
}

public sealed record NexShareBleInfo(string DeviceId, int WsPort, IReadOnlySet<string> Caps);

public static class NexShareBleInfoCodec
{
    /// <summary>Capabilities this PC advertises. No "wfd_go": the PC never acts as Wi-Fi Direct group owner.</summary>
    public static readonly IReadOnlyList<string> LocalCaps = new[] { "wfd_legacy", "wfd_e2e", "ble_link", "ble_link_pull" };

    public static string Build(string deviceId, int wsPort, IEnumerable<string> caps) =>
        $"{deviceId}|{wsPort}|{string.Join(",", caps)}";

    /// <summary>Parses "id|wsPort" (legacy) or "id|wsPort|caps". Returns null when fewer than 2 fields.</summary>
    public static NexShareBleInfo? Parse(string? payload)
    {
        if (payload == null) return null;
        var parts = payload.Split('|');
        if (parts.Length < 2) return null;
        var port = int.TryParse(parts[1], out var p) ? p : 0;
        var caps = parts.Length >= 3
            ? parts[2].Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToHashSet()
            : new HashSet<string>();
        return new NexShareBleInfo(parts[0], port, caps);
    }

    /// <summary>Java/Kotlin String.hashCode() over UTF-16 code units.</summary>
    public static int JavaHashCode(string s)
    {
        unchecked
        {
            var h = 0;
            foreach (var c in s) h = 31 * h + c;
            return h;
        }
    }

    /// <summary>Manufacturer data (company 0xFFFF): 4-byte big-endian Java hashCode of the device id.</summary>
    public static byte[] ManufacturerBytes(string deviceId)
    {
        var h = JavaHashCode(deviceId);
        return new[] { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h };
    }
}
