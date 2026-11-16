using System.Text;

namespace NexZen.Infrastructure.Speech;

/// <summary>Minimal NumPy .npy writer (format 1.0) for the speaker embeddings ZONOS3 reads.</summary>
public static class Npy
{
    public static byte[] Float32Vector(float[] values)
    {
        var header = $"{{'descr': '<f4', 'fortran_order': False, 'shape': ({values.Length},), }}";
        // magic (6) + version (2) + header length (2) + header, padded with spaces to a multiple of 64, ending in \n.
        var total = 10 + header.Length + 1;
        var padding = (64 - total % 64) % 64;
        header = header + new string(' ', padding) + "\n";
        using var stream = new MemoryStream();
        stream.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
        var length = (ushort)header.Length;
        stream.Write([(byte)(length & 0xFF), (byte)(length >> 8)]);
        stream.Write(Encoding.ASCII.GetBytes(header));
        foreach (var v in values)
            stream.Write(BitConverter.GetBytes(v)); // little-endian on every platform NexZen runs on
        return stream.ToArray();
    }
}
