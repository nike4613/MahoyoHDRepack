using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using Buffer = System.Buffer;

namespace MahoyoHDRepack
{
    internal static partial class LenZuCompressorFile
    {
        private const int HeaderSize = 0x20;

        public static ReadOnlySpan<byte> ExpectHeader
            => "LenZuCompressor\0"u8 +
               "1\0\0\00\0\0\0\0\0\0\0\0\0\0\0"u8;

        public static IStorage ReadCompressed(IStorage compressed)
        {
            using UniqueRef<IStorage> result = default;
            ReadCompressed(ref result.Ref, compressed).ThrowIfFailure();
            return result.Release();
        }

        public static unsafe Result ReadCompressed(ref UniqueRef<IStorage> uncompressed, IStorage compressedStorage)
        {
            var result = compressedStorage.GetSize(out var size);
            if (result.IsFailure()) return result.Miss();

            if (size < 0x36) return ResultFs.InvalidFileSize.Value;

            Span<byte> headerData = stackalloc byte[HeaderSize];
            result = compressedStorage.Read(0, headerData);
            if (result.IsFailure()) return result.Miss();

            if (!headerData.SequenceEqual(ExpectHeader)) return ResultFs.InvalidFileSize.Value;

            // now we read the rest of the file into memory
            var compressedData = new byte[size].AsSpan();
            result = compressedStorage.Read(0, compressedData);
            if (result.IsFailure()) return result.Miss();

#if USE_DECOMP
            Attempt2.NativeSpan outSpan = default, compressedSpan = default;

            byte[] decompressedData;
            try
            {
                int finalLen;
                fixed (byte* pCompressed = compressedData)
                {
                    compressedSpan.Data = pCompressed;
                    compressedSpan.Length = compressedData.Length;

                    finalLen = Attempt2.lz_decompress(&outSpan, &compressedSpan);
                }

                if (finalLen < 0)
                {
                    throw new InvalidOperationException($"Result code: {finalLen} (decompression failed?)");
                }

                decompressedData = new byte[finalLen];
                new Span<byte>(outSpan.Data, outSpan.Length).Slice(0, finalLen).CopyTo(decompressedData);
            }
            finally
            {
                if (outSpan.Data != null)
                {
                    NativeMemory.Free(outSpan.Data);
                }
            }
#else
            var decompressedData = DecompressFile(compressedData);
#endif

            uncompressed.Reset(MemoryStorage.Adopt(decompressedData));
            return Result.Success;
        }

    }
}
