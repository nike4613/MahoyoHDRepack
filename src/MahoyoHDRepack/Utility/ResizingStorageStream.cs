using System;
using System.IO;
using CommunityToolkit.Diagnostics;
using LibHac.Fs;

namespace MahoyoHDRepack.Utility;

internal sealed class ResizingStorageStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;

    private readonly IStorage storage;
    private long storageSize;
    private long endOfStream;
    private long position;
    private readonly bool skipFlush;

    // note: non-owning
    public ResizingStorageStream(IStorage storage, bool skipFlush = false)
    {
        this.storage = storage;
        storage.GetSize(out storageSize).ThrowIfFailure();
        endOfStream = storageSize;
        this.skipFlush = skipFlush;
        position = 0;
    }

    public override long Length => endOfStream;

    public override long Position
    {
        get => position;
        set
        {
            if (value > endOfStream)
            {
                // a seek past the end of stream is OK, just need to make sure that we resize
                GrowToAtLeastLength(value);
                endOfStream = value;
            }
            position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => endOfStream + offset,
            _ => ThrowHelper.ThrowArgumentOutOfRangeException<long>(nameof(origin)),
        };
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (position + buffer.Length > endOfStream)
        {
            buffer = buffer.Slice(0, (int)(endOfStream - position));
        }

        storage.Read(position, buffer).ThrowIfFailure();
        position += buffer.Length;
        return buffer.Length;
    }

    public override void SetLength(long value)
    {
        storage.SetSize(value).ThrowIfFailure();
        storage.GetSize(out storageSize).ThrowIfFailure();
        endOfStream = storageSize;
        position = long.Min(position, endOfStream);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (position + buffer.Length > storageSize)
        {
            // need to increase the size of the backing storage
            GrowToAtLeastLength(position + buffer.Length);
        }

        storage.Write(position, buffer).ThrowIfFailure();
        position += buffer.Length;
        endOfStream = long.Max(endOfStream, position);
    }

    private void GrowToAtLeastLength(long targetLength)
    {
        if (targetLength > storageSize)
        {
            var newSize = Helpers.NextPow2(targetLength);
            storage.SetSize(newSize).ThrowIfFailure();
            storage.GetSize(out storageSize).ThrowIfFailure();
        }
    }

    public override void Flush()
    {
        if (skipFlush) { return; }
        ForceFlush();
    }

    public void ForceFlush()
    {
        if (storageSize != endOfStream)
        {
            // we overallocated somewhat, shrink down before the final flush
            storage.SetSize(endOfStream).ThrowIfFailure();
            storage.GetSize(out storageSize).ThrowIfFailure();
        }
        storage.Flush().ThrowIfFailure();
    }
}
