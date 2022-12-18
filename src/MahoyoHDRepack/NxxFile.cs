using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Zip.Compression;
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack
{
    internal sealed class NxxFile : IFile
    {
        public static ReadOnlySpan<byte> NXCX => "NXCX"u8;
        public static ReadOnlySpan<byte> NXGX => "NXGX"u8;

        public static IFile TryCreate(IFile file)
        {
            var result = file.GetSize(out var size);
            if (result.IsFailure()) return file;

            const int HdrSize = 16;
            if (size < HdrSize) return file;

            Span<byte> hdr = stackalloc byte[HdrSize];
            result = file.Read(out var bytesRead, 0, hdr);
            if (result.IsFailure() || bytesRead < HdrSize) return file;

            if (hdr[0..4].SequenceEqual(NXCX))
            {
                var len = MemoryMarshal.Read<uint>(hdr[4..]);
                var zlen = MemoryMarshal.Read<uint>(hdr[8..]);

                var zdata = new byte[zlen];
                var data = new byte[len];

                file.Read(out var read, HdrSize, zdata).ThrowIfFailure();

                var inflater = new Inflater(false);
                inflater.SetInput(zdata);
                var num = inflater.Inflate(data);
                Helpers.Assert(num == len);

                return new NxxFile(data);
            }

            if (hdr[0..4].SequenceEqual(NXGX))
            {
                var len = MemoryMarshal.Read<uint>(hdr[4..]);
                var zlen = MemoryMarshal.Read<uint>(hdr[8..]);

                var zdata = new byte[zlen];
                var data = new byte[len];

                file.Read(out var read, HdrSize, zdata).ThrowIfFailure();

                GZip.Decompress(new MemoryStream(zdata), new MemoryStream(data), true);

                return new NxxFile(data);
            }

            return file;
        }

        private readonly ReadOnlyMemory<byte> uncompressedData;

        private NxxFile(ReadOnlyMemory<byte> uncompressedData) => this.uncompressedData = uncompressedData;

        protected override Result DoGetSize(out long size)
        {
            size = uncompressedData.Length;
            return Result.Success;
        }

        protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
        {
            Unsafe.SkipInit(out bytesRead);

            if (offset < 0)
            {
                return new Result(257, 0);
            }

            var endOffs = offset + destination.Length;
            endOffs = Math.Clamp(endOffs, 0, uncompressedData.Length);
            bytesRead = endOffs - offset;
            destination = destination.Slice(0, (int)(endOffs - offset));

            if (destination.Length is 0)
            {
                return new Result(257, 0);
            }

            uncompressedData.Span.Slice((int)offset).CopyTo(destination);
            return Result.Success;
        }

        protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
        protected override Result DoSetSize(long size) => throw new NotSupportedException();
        protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();
        protected override Result DoFlush() => throw new NotSupportedException();
    }
}
