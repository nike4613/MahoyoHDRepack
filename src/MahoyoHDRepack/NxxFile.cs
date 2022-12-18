using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack
{
    internal sealed class NxxFile : IFile
    {
        public static IFile TryCreate(IFile file)
        {
            var result = file.GetSize(out var size);
            if (result.IsFailure()) return file;

            const int HdrSize = 16;
            if (size < HdrSize) return file;

            Span<byte> hdr = stackalloc byte[HdrSize];
            result = file.Read(out var bytesRead, 0, hdr);
            if (result.IsFailure() || bytesRead < HdrSize) return file;

            if (hdr[0..4].SequenceEqual(FileScanner.NxCx))
            {
                var len = MemoryMarshal.Read<uint>(hdr[4..]);
                var zlen = MemoryMarshal.Read<uint>(hdr[8..]);

                var dataStream = new PartialFile(file, HdrSize, zlen).AsStream(OpenMode.Read, false);
                var inflater = new Inflater(false);
                var inflaterStream = new InflaterInputStream(dataStream, inflater, 16384);
                return new NxxFile(inflaterStream, new(), len);
            }

            if (hdr[0..4].SequenceEqual(FileScanner.NxGx))
            {
                var len = MemoryMarshal.Read<uint>(hdr[4..]);
                var zlen = MemoryMarshal.Read<uint>(hdr[8..]);

                var dataStream = new PartialFile(file, HdrSize, zlen).AsStream(OpenMode.Read, false);
                var gzipStream = new GZipInputStream(dataStream, 16384);
                return new NxxFile(gzipStream, new(), len);
            }

            return file;
        }

        private readonly Stream decompStream;
        private readonly MemoryStream buffer;
        private readonly long length;

        private NxxFile(Stream stream, MemoryStream buf, long len)
        {
            decompStream = stream;
            buffer = buf;
            length = len;
        }

        public override void Dispose()
        {
            decompStream.Dispose();
            buffer.Dispose();
        }

        protected override Result DoGetSize(out long size)
        {
            size = length;
            return Result.Success;
        }

        private void EnsureOffsetInBuffer(long offset)
        {
            if (offset < buffer.Position)
            {
                // it's already available in the buffer
                return;
            }

            var copyBuffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                var fullSizeThreshold = length / 4 * 3;

                while (buffer.Position < offset)
                {
                    // once the buffer is about at 3/4 of the full size, set the capacity to the final size
                    if (buffer.Capacity < length && buffer.Position >= fullSizeThreshold)
                    {
                        buffer.Capacity = (int)length;
                    }

                    var read = decompStream.Read(copyBuffer);
                    if (read is 0)
                        break;
                    buffer.Write(copyBuffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(copyBuffer);
            }
        }

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            Unsafe.SkipInit(out bytesRead);

            var result = IStorage.CheckAccessRange(offset, destination.Length, length);
            if (result.IsFailure()) return result;

            EnsureOffsetInBuffer(offset + destination.Length - 1);
            buffer.GetBuffer().AsSpan().Slice((int)offset, destination.Length).CopyTo(destination);

            return Result.Success;
        }

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
        protected override Result DoSetSize(long size) => throw new NotSupportedException();
        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();
        protected override Result DoFlush() => throw new NotSupportedException();
    }
}
