using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using Xunit.Abstractions;
using Path = System.IO.Path;

namespace MahoyoHDRepack.Tests
{
    public class TestDecompressors(ITestOutputHelper testOut)
    {
        public static IEnumerable<object[]> GetTestDatasets(string compressedPattern, string decompressedExt)
        {
            var dataPath = Path.Combine(Environment.CurrentDirectory, "DecompressorTestFiles");

            using var localFs = new LocalFileSystem(dataPath);

            foreach (var file in localFs.EnumerateEntries(compressedPattern, SearchOptions.RecurseSubdirectories))
            {
                using UniqueRef<IFile> compressedFile = default;
                using UniqueRef<IFile> decompressedFile = default;
                var result = localFs.OpenFile(ref compressedFile.Ref, new(file.Name), OpenMode.Read);
                if (!result.IsSuccess()) continue;
                result = localFs.OpenFile(ref decompressedFile.Ref, new(file.Name + decompressedExt), OpenMode.Read);
                if (!result.IsSuccess()) continue;

                yield return new object[] { file.Name, compressedFile.Release(), decompressedFile.Release() };
            }
        }

        [Theory]
        [MemberData(nameof(GetTestDatasets), "*.ctd", ".de")]
        public void TestLenZuDecompressor(string file, IFile compressed, IFile decompressed)
        {
            _ = file;
            var actualDecompressed = LenZuCompressorFile.ReadCompressed(compressed.AsStorage());
            AssertStorageEqual(decompressed.AsStorage(), actualDecompressed);
        }

        [Theory]
        [MemberData(nameof(GetTestDatasets), "*.de", "")]
        public void TestNopLenZuEncoder(string file, IFile decompressed, IFile unused)
        {
            _ = file;
            _ = unused;

            using var memStor = new MemoryStorage();
            var decompStorage = decompressed.AsStorage();
            LenZuCompressorFile.CompressTo(decompStorage, memStor).ThrowIfFailure();
            var reDecomp = LenZuCompressorFile.ReadCompressed(memStor, assertChecksum: false);
            AssertStorageEqual(decompStorage, reDecomp);
        }

        private void AssertStorageEqual(IStorage expect, IStorage actual)
        {
            expect.GetSize(out var expectSize).ThrowIfFailure();
            actual.GetSize(out var actualSize).ThrowIfFailure();

            Assert.Equal(expectSize, actualSize);

            const int blockSize = 16384;
            const int errorContextSize = 32;

            var expectBuffer = ArrayPool<byte>.Shared.Rent(blockSize);
            var actualBuffer = ArrayPool<byte>.Shared.Rent(blockSize);
            try
            {
                for (var baseOffset = 0L; baseOffset < expectSize; baseOffset += expectBuffer.Length)
                {
                    var expectSpan = expectBuffer.AsSpan(0, (int)long.Min(expectBuffer.Length, expectSize - baseOffset));
                    var actualSpan = actualBuffer.AsSpan(0, expectSpan.Length);

                    expect.Read(baseOffset, expectSpan).ThrowIfFailure();
                    actual.Read(baseOffset, actualSpan).ThrowIfFailure();

                    var commonPrefix = expectSpan.CommonPrefixLength(actualSpan);

                    if (commonPrefix != expectSpan.Length)
                    {
                        var roundedFullOffset = (baseOffset + commonPrefix) & ~0xf;
                        var roundedLocalOffset = (int)long.Clamp(roundedFullOffset - baseOffset, 0, expectSpan.Length);
                        var localUpperBound = int.Clamp(roundedLocalOffset + errorContextSize, 0, expectSpan.Length);

                        static unsafe void PrintValues(ITestOutputHelper testOut, long lowBoundFull, ReadOnlySpan<byte> data, int lowBound, int highBound)
                        {
                            for (var i = lowBound; i < highBound; i += 8)
                            {
                                var dataSpan = data.Slice(i, int.Min(8, data.Length));
#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
                                var hexStr = string.Create(dataSpan.Length * 3, (nint)(&dataSpan), static (span, state) =>
                                {
                                    var data = *(ReadOnlySpan<byte>*)state;
                                    for (var i = 0; i < data.Length; i++)
                                    {
                                        span[(i * 3) + 0] = ' ';
                                        var lo = data[i] & 0xf;
                                        var hi = data[i] >> 4;
                                        span[(i * 3) + 1] = (char)('0' + lo + (lo >= 10 ? 'a' - '0' - 10 : 0));
                                        span[(i * 3) + 2] = (char)('0' + hi + (hi >= 10 ? 'a' - '0' - 10 : 0));
                                    }
                                });
#pragma warning restore CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

                                testOut.WriteLine($"0x{lowBoundFull + i:x8}:{hexStr}");
                            }
                        }

                        testOut.WriteLine($"expect:");
                        PrintValues(testOut, roundedFullOffset, expectSpan, roundedLocalOffset, localUpperBound);
                        testOut.WriteLine($"actual:");
                        PrintValues(testOut, roundedFullOffset, actualSpan, roundedLocalOffset, localUpperBound);

                        Assert.Fail($"Storages were not equal. Index of first difference: 0x{baseOffset + commonPrefix:x}");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(expectBuffer);
                ArrayPool<byte>.Shared.Return(actualBuffer);
            }
        }
    }
}
