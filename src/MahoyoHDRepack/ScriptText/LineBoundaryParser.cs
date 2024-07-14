using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack.ScriptText
{
    internal static class LineBoundaryParser
    {
        public static Result ReadLineBoundaries(MzpFileSystem fs, GameLanguage lang, out ReadOnlyMemory<uint> boundaries)
        {
            Unsafe.SkipInit(out boundaries);

            using var uniqBoundaryFile = new UniqueRef<IFile>();
            var result = OpenLangBoundaryFile(fs, ref uniqBoundaryFile.Ref, lang);
            if (result.IsFailure()) return result.Miss();

            using var boundaryFile = new SharedRef<IFile>();
            boundaryFile.Ref.Set(ref uniqBoundaryFile.Ref);

            using var fileStorage = new FileStorage(ref boundaryFile.Ref);

            return ParseLineBoundaries(fileStorage, out boundaries);
        }

        public static Result OpenLangBoundaryFile(MzpFileSystem fs, ref UniqueRef<IFile> outFile, GameLanguage lang, OpenMode mode = OpenMode.Read)
            => fs.OpenFile(ref outFile, ((int)lang) * 2, mode);

        public static Result OpenLangLineDataFile(MzpFileSystem fs, ref UniqueRef<IFile> outFile, GameLanguage lang, OpenMode mode = OpenMode.Read)
            => fs.OpenFile(ref outFile, (((int)lang) * 2) + 1, mode);

        public static Result ReadLines(MzpFileSystem fs, GameLanguage lang, out string[] lines)
        {
            Unsafe.SkipInit(out lines);

            ReadLinesAction action = default;
            var result = ReadLinesCore(fs, lang, ref action);
            if (result.IsSuccess())
            {
                Helpers.Assert(action.Lines is not null);
                lines = action.Lines;
            }
            return result;
        }

        private struct ReadLinesAction : ILineReadAction
        {
            public string[]? Lines;

            public Result OnReadBoundaries(ReadOnlySpan<uint> boundaries)
            {
                Lines = new string[boundaries.Length];
                return Result.Success;
            }
            public Result ReadLine(int index, ReadOnlySpan<byte> data)
            {
                Helpers.Assert(Lines is not null);
                Lines[index] = Encoding.UTF8.GetString(data);
                return Result.Success;
            }
        }

        public static Result ReadLinesU8(MzpFileSystem fs, GameLanguage lang, out ImmutableArray<byte>[] lines)
        {
            Unsafe.SkipInit(out lines);

            ReadLinesU8Action action = default;
            var result = ReadLinesCore(fs, lang, ref action);
            if (result.IsSuccess())
            {
                Helpers.Assert(action.Lines is not null);
                lines = action.Lines;
            }
            return result;
        }

        private struct ReadLinesU8Action : ILineReadAction
        {
            public ImmutableArray<byte>[]? Lines;

            public Result OnReadBoundaries(ReadOnlySpan<uint> boundaries)
            {
                Lines = new ImmutableArray<byte>[boundaries.Length];
                return Result.Success;
            }
            public Result ReadLine(int index, ReadOnlySpan<byte> data)
            {
                Helpers.Assert(Lines is not null);
                Lines[index] = [.. data];
                return Result.Success;
            }
        }

        private interface ILineReadAction
        {
            Result OnReadBoundaries(ReadOnlySpan<uint> boundaries);
            Result ReadLine(int index, ReadOnlySpan<byte> data);
        }

        private static Result ReadLinesCore<TLineReader>(MzpFileSystem fs, GameLanguage lang, ref TLineReader lineReader)
            where TLineReader : ILineReadAction
        {
            var result = ReadLineBoundaries(fs, lang, out var boundaries);
            if (result.IsFailure()) return result.Miss();

            result = lineReader.OnReadBoundaries(boundaries.Span);
            if (result.IsFailure()) return result.Miss();

            using var lineDataFile = new UniqueRef<IFile>();
            result = OpenLangLineDataFile(fs, ref lineDataFile.Ref, lang);
            if (result.IsFailure()) return result.Miss();

            result = lineDataFile.Get.GetSize(out var lineDataSize);
            if (result.IsFailure()) return result.Miss();

            for (var i = 0; i < boundaries.Length; i++)
            {
                var start = boundaries.Span[i];
                var end = i + 1 < boundaries.Length ? boundaries.Span[i + 1] : lineDataSize;
                var len = (int)end - (int)start;

                var buf = ArrayPool<byte>.Shared.Rent(len);
                try
                {
                    result = lineDataFile.Get.Read(out var read, start, buf);
                    if (result.IsFailure()) return result.Miss();

                    var data = buf.AsSpan()[..Math.Min((int)read, len)];
                    result = lineReader.ReadLine(i, data);
                    if (result.IsFailure()) return result.Miss();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }

            return Result.Success;
        }

        private const int EntrySize = 4;

        public static Result ParseLineBoundaries(IStorage storage, out ReadOnlyMemory<uint> boundaries)
        {
            Unsafe.SkipInit(out boundaries);

            var result = storage.GetSize(out var size);
            if (result.IsFailure()) return result.Miss();

            if (size % EntrySize != 0)
            {
                // size is wrong
                return ResultFs.InvalidFileSize.Value;
            }

            var entryCount = size / EntrySize;

            Span<byte> entrySpan = stackalloc byte[EntrySize];

            var entries = new uint[entryCount];
            var i = 0;
            for (; i < entryCount; i++)
            {
                result = storage.Read(i * EntrySize, entrySpan);
                if (result.IsFailure()) return result.Miss();

                var start = MemoryMarshal.Read<BEInt32>(entrySpan).Value;
                if (start == uint.MaxValue)
                    break;

                entries[i] = start;
            }

            boundaries = entries.AsMemory().Slice(0, i);
            return Result.Success;
        }
    }
}
