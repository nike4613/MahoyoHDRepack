using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommunityToolkit.Diagnostics;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;
using MahoyoHDRepack.Utility;

namespace MahoyoHDRepack;

internal sealed class NxxFile : IFile
{
    private const int HdrSize = 16;
    private const int BufferSize = 16384;

    public static IFile TryCreate(IFile file)
    {
        var result = file.GetSize(out var size);
        if (result.IsFailure()) return file;

        if (size < HdrSize) return file;

        Span<byte> hdr = stackalloc byte[HdrSize];
        result = file.Read(out var bytesRead, 0, hdr);
        if (result.IsFailure() || bytesRead < HdrSize) return file;

        if (hdr[0..4].SequenceEqual(FileScanner.NxCx))
        {
            var len = MemoryMarshal.Read<LEUInt32>(hdr[4..]);
            var zlen = MemoryMarshal.Read<LEUInt32>(hdr[8..]);

            var dataStream = new PartialFile(file, HdrSize, zlen).AsStream(OpenMode.Read, false);
            var inflater = new Inflater(false);
            var inflaterStream = new InflaterInputStream(dataStream, inflater, BufferSize);
            return new NxxFile(file, inflaterStream, len, usesGzip: false);
        }

        if (hdr[0..4].SequenceEqual(FileScanner.NxGx))
        {
            var len = MemoryMarshal.Read<LEUInt32>(hdr[4..]);
            var zlen = MemoryMarshal.Read<LEUInt32>(hdr[8..]);

            var dataStream = new PartialFile(file, HdrSize, zlen).AsStream(OpenMode.Read, false);
            var gzipStream = new GZipInputStream(dataStream, BufferSize);
            return new NxxFile(file, gzipStream, len, usesGzip: true);
        }

        return file;
    }

    public static uint GetUncompressedSize(IFile file)
    {
        file.GetSize(out var size).ThrowIfFailure();
        if (size < HdrSize) ThrowHelper.ThrowInvalidOperationException("File too small to be compressed");

        Span<byte> hdr = stackalloc byte[HdrSize];
        file.Read(out var bytesRead, 0, hdr).ThrowIfFailure();
        if (bytesRead < HdrSize) ThrowHelper.ThrowInvalidOperationException("Partial read");

        if (hdr[0..4].SequenceEqual(FileScanner.NxCx) || hdr[0..4].SequenceEqual(FileScanner.NxGx))
        {
            return MemoryMarshal.Read<LEUInt32>(hdr[4..]);
        }
        else
        {
            ThrowHelper.ThrowInvalidOperationException("Not an Nxx compressed file");
            return 0;
        }
    }

    private readonly IFile underlyingFile;
    private readonly Stream decompStream;
    private readonly MemoryStorage buffer;
    private long length;
    private long decompressToLength;
    private long nextBlockToDecompress;
    private readonly bool usesGzip;
    private bool didWrite;

    private NxxFile(IFile underlyingFile, Stream stream, long len, bool usesGzip)
    {
        this.underlyingFile = underlyingFile;
        decompStream = stream;
        buffer = new();
        length = len;
        decompressToLength = len;
        this.usesGzip = usesGzip;
    }

    public override void Dispose()
    {
        decompStream.Dispose();
        buffer.Dispose();
        base.Dispose();
    }

    protected override Result DoGetSize(out long size)
    {
        size = length;
        return Result.Success;
    }

    private Result EnsureOffsetInBuffer(long offset)
    {
        if (offset < nextBlockToDecompress)
        {
            // it's already available in the buffer
            return Result.Success;
        }

        if (nextBlockToDecompress >= decompressToLength)
        {
            // we've already decompressed as much as we're supposed to
            return Result.Success;
        }

        Result result;

        // resize the underlying buffer appropriately
        if (buffer.Size <= offset)
        {
            // need to grow the stream
            // grow to a power of two, maxing out at our final length
            var newSize = Helpers.NextPow2(offset);
            newSize = long.Min(newSize, length);
            result = buffer.SetSize(newSize);
            if (result.IsFailure()) return result;
        }

        var copyBuffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // don't decompress past decompressToLength, but we don't care about being very strict about it
            while (nextBlockToDecompress < offset && nextBlockToDecompress < decompressToLength)
            {
                var copySpan = copyBuffer.AsSpan();
                if (nextBlockToDecompress + copySpan.Length > buffer.Size)
                {
                    copySpan = copySpan.Slice(0, (int)(buffer.Size - nextBlockToDecompress));
                }

                var read = decompStream.Read(copySpan);
                if (read is 0)
                    break;

                result = buffer.Write(nextBlockToDecompress, copySpan.Slice(0, read));
                if (result.IsFailure()) return result;

                nextBlockToDecompress += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }

        return Result.Success;
    }

    protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
    {
        Unsafe.SkipInit(out bytesRead);

        var result = IStorage.CheckAccessRange(offset, destination.Length, length);
        if (result.IsFailure()) return result;

        result = EnsureOffsetInBuffer(offset + destination.Length - 1);
        if (result.IsFailure()) return result;
        result = buffer.Read(offset, destination);
        if (result.IsFailure()) return result;

        bytesRead = destination.Length;
        return Result.Success;
    }

    protected override Result DoSetSize(long size)
    {
        Result result;

        // set the size of the underlying buffer
        result = buffer.SetSize(size);
        if (result.IsFailure()) return result;

        // and update our stored length
        length = size;
        // also make sure that our readToLength is the minimum of the existing value and the new size, so we don't try to populate later bits with data
        decompressToLength = long.Min(decompressToLength, size);

        didWrite = true;

        return Result.Success;
    }

    protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option)
    {
        var result = IStorage.CheckAccessRange(offset, source.Length, length);
        if (result.IsFailure()) return result;

        // before writing, we want to make sure we've populated past the write so we don't accidentally overwrite written segments when reading later
        result = EnsureOffsetInBuffer(offset + source.Length - 1);
        if (result.IsFailure()) return result;
        // then we can write
        result = buffer.Write(offset, source);
        if (result.IsFailure()) return result;

        didWrite = true;

        if (option.HasFlushFlag())
        {
            result = Flush();
            if (result.IsFailure()) return result;
        }

        return Result.Success;
    }

    protected override Result DoFlush()
    {
        if (!didWrite)
        {
            // if we never wrote, there's no work to do
            return Result.Success;
        }
        // in a flush, we want to re-compress the data, and inject the appropriate headers

        // first, we reset the underlying file to just the header
        var result = underlyingFile.SetSize(HdrSize);
        if (result.IsFailure()) return result;

        using var fileOutRawStream = new ResizingStorageStream(underlyingFile.AsStorage());
        // make sure the stream write starts after the header
        fileOutRawStream.Position = HdrSize;

        ReadOnlySpan<byte> header;
        Span<byte> headerData = stackalloc byte[8];
        Stream compressOutputStream;

        if (usesGzip)
        {
            header = FileScanner.NxGx;

            compressOutputStream = new GZipOutputStream(fileOutRawStream, BufferSize);
        }
        else
        {
            // use deflate
            header = FileScanner.NxCx;

            var deflater = new Deflater(Deflater.BEST_COMPRESSION, false);
            compressOutputStream = new DeflaterOutputStream(fileOutRawStream, deflater);
        }

        try
        {
            // write out what header we know of 
            result = underlyingFile.Write(0, header);
            if (result.IsFailure()) return result;

            headerData.Clear();
            // first is the decompressed length
            MemoryMarshal.Write<LEUInt32>(headerData, (uint)length);
            result = underlyingFile.Write(4, headerData.Slice(0, 4));
            if (result.IsFailure()) return result;
            // second, is the compressed length, but we don't know that just yet, so defer writing that

            // now lets start copying data
            var buf = ArrayPool<byte>.Shared.Rent(BufferSize);
            var pos = 0L;
            while (pos < length)
            {
                // read data from the buffer
                var readSpan = buf.AsSpan();
                if (pos + readSpan.Length > length)
                {
                    readSpan = readSpan.Slice(0, (int)(length - pos));
                }

                result = buffer.Read(pos, readSpan);
                if (result.IsFailure()) return result;
                pos += readSpan.Length;

                // write it to the compression stream
                compressOutputStream.Write(buf, 0, readSpan.Length);
            }

            // we're done compressing, flush both
            //compressOutputStream.Flush();
            compressOutputStream.Close();
            //fileOutRawStream.Flush();
            fileOutRawStream.Close();
        }
        finally
        {
            compressOutputStream.Dispose();
            fileOutRawStream.Dispose();
        }

        // we now know the length of the compressed data, write it
        MemoryMarshal.Write<LEUInt32>(headerData, (uint)(fileOutRawStream.Position - HdrSize));
        result = underlyingFile.Write(8, headerData.Slice(0, 4));
        if (result.IsFailure()) return result;

        // we're done writing out, flush the underlying file
        result = underlyingFile.Flush();
        if (result.IsFailure()) return result;

        didWrite = false;
        return Result.Success;
    }

    protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => ResultFs.NotImplemented.Value;
}
