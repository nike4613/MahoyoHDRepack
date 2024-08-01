using System;
using System.Buffers;
using System.Runtime.InteropServices;
using LibHac;
using LibHac.Fs;

namespace MahoyoHDRepack
{
    internal partial class LenZuCompressorFile
    {
        private static ReadOnlySpan<int> PrecomputedHuffTable => [
            4,
            6,
            10,
            16,
            26,
            42
        ];

        private const int PrecomputedLeftoverEntryValue = 2;
        private const int PrecomputedBlockEntryValue = 3;
        private const int TargetBlockSize = 0x80;

        private static ReadOnlySpan<byte> PrecomputedCompressorOptions => [
            7, // unused
            7,
            7,
            7,
            0,
            2,
            TargetBlockSize,
        ];

        private const byte FullBlockLit = 0;
        private const byte EndBlockLit = 1;

        public static Result CompressTo(IStorage uncompressed, IStorage dest)
        {
            var result = uncompressed.GetSize(out var size);
            if (result.IsFailure()) return result;

            var curOutSize = 0x37L + (4 * TargetBlockSize) + size + (size / TargetBlockSize) + 1;
            result = dest.SetSize(curOutSize);
            if (result.IsFailure()) return result;

            ulong checksum = 0;

            result = dest.Write(0, ExpectHeader);
            if (result.IsFailure()) return result;

            LEUInt32 outSize = (uint)size;
            result = dest.Write(0x20, MemoryMarshal.Cast<LEUInt32, byte>(new(ref outSize)));
            if (result.IsFailure()) return result;

            // next 8 are checksum, which we don't have yet, then 4 which are unused, so we don't care about their value

            // then, the compressor options
            result = dest.Write(0x30, PrecomputedCompressorOptions);
            if (result.IsFailure()) return result;

            var offset = 0x37L;

            // then, we need to compute and write out our table
            var arr = ArrayPool<int>.Shared.Rent(TargetBlockSize);
            arr.AsSpan().Clear();
            PrecomputedHuffTable.CopyTo(arr);
            var leftover = (int)(size % TargetBlockSize);
            if (leftover != 0)
            {
                var sp = arr.AsSpan();
                sp.Slice(leftover - 1, sp.Length - leftover).CopyTo(sp.Slice(leftover));
                sp[leftover - 1] = PrecomputedLeftoverEntryValue;
            }
            else
            {
                arr[PrecomputedHuffTable.Length] = PrecomputedLeftoverEntryValue;
            }
            arr[TargetBlockSize - 1] = PrecomputedBlockEntryValue;

            {
                var huffSpan = MemoryMarshal.Cast<int, byte>(arr.AsSpan(0, TargetBlockSize));
                result = dest.Write(offset, huffSpan);
                if (result.IsFailure()) return result;
                offset += huffSpan.Length;
            }

            ArrayPool<int>.Shared.Return(arr);

            // now we can start writing our data
            var buf = ArrayPool<byte>.Shared.Rent(TargetBlockSize + 1);
            var span = buf.AsSpan(0, TargetBlockSize + 1);

            // TODO: maybe we should overallocate here a little bit to be safe?
            result = dest.SetSize(curOutSize);
            if (result.IsFailure()) return result;

            for (var readOffs = 0; readOffs < size; readOffs += TargetBlockSize)
            {
                var blockSize = (int)long.Min(TargetBlockSize, size - readOffs);

                result = uncompressed.Read(readOffs, span.Slice(1, blockSize));
                if (result.IsFailure()) return result;

                checksum = ComputeChecksumWithSeed(checksum, readOffs, span.Slice(1, blockSize));

                if (blockSize == TargetBlockSize)
                {
                    span[0] = FullBlockLit;
                }
                else
                {
                    Helpers.DAssert(blockSize == leftover);
                    span[0] = EndBlockLit;
                }
                if (result.IsFailure()) return result;

                result = dest.Write(offset, span.Slice(0, blockSize + 1));
                if (result.IsFailure()) return result;
                offset += blockSize + 1;
            }

            ArrayPool<byte>.Shared.Return(buf);

            // set the final size
            result = dest.SetSize(offset);
            if (result.IsFailure()) return result;

            // now we have our checksum, lets write it out
            LEUInt32 checksumHi = (uint)(checksum >> 32);
            LEUInt32 checksumLo = (uint)checksum;

            result = dest.Write(0x24, MemoryMarshal.Cast<LEUInt32, byte>(new(ref checksumHi)));
            if (result.IsFailure()) return result;
            result = dest.Write(0x28, MemoryMarshal.Cast<LEUInt32, byte>(new(ref checksumLo)));
            if (result.IsFailure()) return result;

            // and we're done
            return Result.Success;
        }
    }
}
