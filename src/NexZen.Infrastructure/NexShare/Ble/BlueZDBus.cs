using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace NexZen.Infrastructure.NexShare.Ble;

/// <summary>Minimal hand-written org.bluez client helpers over Tmds.DBus.Protocol.</summary>
[SupportedOSPlatform("linux")]
internal static class BlueZDBus
{
    public const string Bus = "org.bluez";
    public const string Adapter1 = "org.bluez.Adapter1";
    public const string Device1 = "org.bluez.Device1";
    public const string GattChar1 = "org.bluez.GattCharacteristic1";
    public const string GattManager1 = "org.bluez.GattManager1";
    public const string AdvManager1 = "org.bluez.LEAdvertisingManager1";
    public const string Props = "org.freedesktop.DBus.Properties";
    public const string ObjMgr = "org.freedesktop.DBus.ObjectManager";

    public sealed class ManagedObjects : Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> { }

    public static Task<ManagedObjects> GetManagedObjectsAsync(DBusConnection c)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, "/", ObjMgr, "GetManagedObjects", null, MessageFlags.None);
        return c.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var result = new ManagedObjects();
            var r = m.GetBodyReader();
            var outer = r.ReadDictionaryStart();
            while (r.HasNext(outer))
            {
                r.AlignStruct();
                var path = r.ReadObjectPathAsString();
                var ifaces = new Dictionary<string, Dictionary<string, VariantValue>>();
                var mid = r.ReadDictionaryStart();
                while (r.HasNext(mid))
                {
                    r.AlignStruct();
                    var iface = r.ReadString();
                    ifaces[iface] = r.ReadDictionaryOfStringToVariantValue();
                }
                result[path] = ifaces;
            }
            return result;
        }, null);
    }

    public static Task CallAsync(DBusConnection c, string path, string iface, string member)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, path, iface, member, null, MessageFlags.None);
        return c.CallMethodAsync(w.CreateMessage());
    }

    public static Task CallWithDictAsync(DBusConnection c, string path, string iface, string member, Dictionary<string, VariantValue> dict)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, path, iface, member, "a{sv}", MessageFlags.None);
        w.WriteDictionary(dict);
        return c.CallMethodAsync(w.CreateMessage());
    }

    public static Task CallWithPathAndDictAsync(DBusConnection c, string path, string iface, string member, string objPath)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, path, iface, member, "oa{sv}", MessageFlags.None);
        w.WriteObjectPath(objPath);
        w.WriteDictionary(new Dictionary<string, VariantValue>());
        return c.CallMethodAsync(w.CreateMessage());
    }

    public static Task CallWithPathAsync(DBusConnection c, string path, string iface, string member, string objPath)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, path, iface, member, "o", MessageFlags.None);
        w.WriteObjectPath(objPath);
        return c.CallMethodAsync(w.CreateMessage());
    }

    public static Task<VariantValue> GetPropertyAsync(DBusConnection c, string path, string iface, string name)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, path, Props, "Get", "ss", MessageFlags.None);
        w.WriteString(iface);
        w.WriteString(name);
        return c.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            return Unwrap(r.ReadVariantValue());
        }, null);
    }

    /// <summary>
    /// BlueZ drops Device1 (and GATT children) after a failed Connect / Disconnect of an unpaired LE
    /// random address. Properties.Get then fails with UnknownObject until the next advert recreates the path.
    /// </summary>
    public static bool IsUnknownObject(Exception ex)
    {
        if (ex is DBusErrorReplyException d)
        {
            if (d.ErrorName.Contains("UnknownObject", StringComparison.OrdinalIgnoreCase)) return true;
            if (d.Message.Contains("UnknownObject", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return ex.Message.Contains("UnknownObject", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<byte[]> ReadValueAsync(DBusConnection c, string charPath)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, charPath, GattChar1, "ReadValue", "a{sv}", MessageFlags.None);
        w.WriteDictionary(new Dictionary<string, VariantValue>());
        return c.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            return r.ReadArrayOfByte();
        }, null);
    }

    public static Task WriteValueAsync(DBusConnection c, string charPath, byte[] value)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Bus, charPath, GattChar1, "WriteValue", "aya{sv}", MessageFlags.None);
        w.WriteArray(value);
        w.WriteDictionary(new Dictionary<string, VariantValue> { ["type"] = VariantValue.String("request") });
        return c.CallMethodAsync(w.CreateMessage());
    }

    /// <summary>Unique bus name currently owning org.bluez (changes when bluetoothd restarts), or null.</summary>
    public static async Task<string?> GetBluezOwnerAsync(DBusConnection c)
    {
        try { return await GetNameOwnerCore(c); }
        catch (DBusErrorReplyException) { return null; }
    }

    private static Task<string> GetNameOwnerCore(DBusConnection c)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s", MessageFlags.None);
        w.WriteString(Bus);
        return c.CallMethodAsync(w.CreateMessage(), (Message m, object? _) => m.GetBodyReader().ReadString(), null);
    }

    public static VariantValue Unwrap(VariantValue v)
    {
        while (v.Type == VariantValueType.Variant) v = v.GetVariantValue();
        return v;
    }

    public static string? GetString(Dictionary<string, VariantValue> p, string key) =>
        p.TryGetValue(key, out var v) && Unwrap(v).Type == VariantValueType.String ? Unwrap(v).GetString() : null;

    public static bool GetBool(Dictionary<string, VariantValue> p, string key) =>
        p.TryGetValue(key, out var v) && Unwrap(v).Type == VariantValueType.Bool && Unwrap(v).GetBool();

    public static short GetInt16(Dictionary<string, VariantValue> p, string key)
    {
        if (!p.TryGetValue(key, out var v)) return 0;
        v = Unwrap(v);
        return v.Type switch
        {
            VariantValueType.Int16 => v.GetInt16(),
            VariantValueType.Int32 => (short)v.GetInt32(),
            VariantValueType.Byte => v.GetByte(),
            _ => 0
        };
    }

    public static bool TryGetServiceData(Dictionary<string, VariantValue> p, string uuid, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (!p.TryGetValue("ServiceData", out var sd)) return false;
        sd = Unwrap(sd);
        if (sd.Type != VariantValueType.Dictionary) return false;
        for (var i = 0; i < sd.Count; i++)
        {
            var e = sd.GetDictionaryEntry(i);
            var k = Unwrap(e.Key);
            if (k.Type != VariantValueType.String || !string.Equals(k.GetString(), uuid, StringComparison.OrdinalIgnoreCase)) continue;
            var val = Unwrap(e.Value);
            if (val.Type != VariantValueType.Array) return false;
            data = val.GetArray<byte>();
            return true;
        }
        return false;
    }

    public static string DevicePath(string adapterPath, string address) =>
        adapterPath + "/dev_" + address.Replace(':', '_').ToUpperInvariant();
}
