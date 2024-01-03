using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using LibHac;
using LibHac.Common;
using LibHac.Fs;

namespace MahoyoHDRepack
{
    internal class MzxFile
    {
        public static IStorage ReadCompressed(IStorage compressed)
        {
            using UniqueRef<IStorage> result = default;
            ReadCompressed(ref result.Ref, compressed).ThrowIfFailure();
            return result.Release();
        }

        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct MzxHeader
        {
            public unsafe Span<byte> Magic => MemoryMarshal.CreateSpan(ref Unsafe.As<MzxHeader, byte>(ref this), 4);
            [FieldOffset(4)]
            public LEUInt32 UncompressedSize;

            public MzxHeader(uint uncompressedSize)
            {
                FileScanner.Mzx.CopyTo(Magic);
                UncompressedSize.Value = uncompressedSize;
            }
        }

        public static unsafe Result ReadCompressed(ref UniqueRef<IStorage> uncompressed, IStorage compressedStorage)
        {
            var result = compressedStorage.GetSize(out var size);
            if (result.IsFailure()) return result.Miss();

            if (size < 8) return ResultFs.InvalidFileSize.Value;

            MzxHeader header = default;
            result = compressedStorage.Read(0, MemoryMarshal.Cast<MzxHeader, byte>(new(ref header)));
            if (result.IsFailure()) return result.Miss();

            if (!header.Magic.SequenceEqual(FileScanner.Mzx)) return ResultFs.DataCorrupted.Value;

            var compressed = new byte[size - 8];
            result = compressedStorage.Read(8, compressed);
            if (result.IsFailure()) return result.Miss();

            uncompressed.Reset(MemoryStorage.Adopt(Decompress(header.UncompressedSize, compressed)));
            return Result.Success;
        }

        // based on https://github.com/Hintay/PS-HuneX_Tools/blob/master/Specifications/mzp_format.md
        // and https://github.com/Hintay/PS-HuneX_Tools/blob/master/tools/mzx/decomp_mzx0.py
        private static byte[] Decompress(uint finalSize, ReadOnlySpan<byte> compressedData)
        {
            var decompressedBytes = new byte[(finalSize + 1) & ~1]; // possibly overallocate a byte, and get 1 byte of garbage
            var decompressed = MemoryMarshal.Cast<byte, ushort>(decompressedBytes);
            var outPos = 0;

            var ringbuf = new ushort[64];
            var ringbufPos = 0;
            ushort last = 0;

            var count = 0;

            for (var i = 0; i < compressedData.Length && outPos < decompressed.Length; i++)
            {
                var b = compressedData[i];
                var cmd = b & 3;
                var len = b >> 2;
                count -= cmd == 2 ? 1 : len + 1;

                switch (cmd)
                {
                    case 0: // RLE
                        for (var j = 0; j < len + 1 && outPos < decompressed.Length; j++)
                        {
                            decompressed[outPos++] = last;
                        }
                        break;
                    case 1: // backref
                        var k = compressedData[++i] + 1;
                        for (var j = 0; j < len + 1 && outPos < decompressed.Length; j++)
                        {
                            decompressed[outPos] = last = decompressed[outPos - k];
                            outPos++;
                        }
                        break;
                    case 2: // ringbuf
                        decompressed[outPos++] = last = ringbuf[len];
                        break;
                    case 3: // literal
                    default:
                        for (var j = 0; j < len + 1 && outPos < decompressed.Length; j++)
                        {
                            ringbuf[ringbufPos] = last = MemoryMarshal.Read<ushort>(compressedData.Slice(i));
                            i += 2;
                            decompressed[outPos++] = last;

                            ringbufPos = (ringbufPos + 1) % 64;
                        }
                        break;
                }
            }

            if (decompressedBytes.Length != finalSize)
            {
                decompressedBytes = decompressedBytes.AsSpan(0, (int)finalSize).ToArray();
            }

            return decompressedBytes;
        }
    }
}
