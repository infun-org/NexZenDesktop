using System;
using System.IO;

namespace NexZen.Infrastructure.NexShare.Transfer;

public class ChunkFileStore : IDisposable
{
    private readonly FileStream _fileStream;
    public string FilePath { get; }

    public ChunkFileStore(string filePath)
    {
        FilePath = filePath;
        _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public void WriteChunk(byte[] data)
    {
        _fileStream.Write(data, 0, data.Length);
    }

    public void Dispose()
    {
        _fileStream.Dispose();
    }
}
