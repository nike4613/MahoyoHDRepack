using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack.ScriptText
{
    internal static class ScriptLineWriter
    {
        private const int EntrySize = 4;

        public static Result WriteLines(MzpFileSystem fs, GameLanguage lang, ReadOnlySpan<string> lines)
        {
            using var uniqOffsetsFile = new UniqueRef<IFile>();
            var result = LineBoundaryParser.OpenLangBoundaryFile(fs, ref uniqOffsetsFile.Ref, lang, OpenMode.Write);
            if (result.IsFailure()) return result.Miss();

            using var uniqTextFile = new UniqueRef<IFile>();
            result = LineBoundaryParser.OpenLangLineDataFile(fs, ref uniqTextFile.Ref, lang, OpenMode.AllowAppend | OpenMode.Write);
            if (result.IsFailure()) return result.Miss();

            return WriteLines(uniqOffsetsFile.Get, uniqTextFile.Get, lines);
        }

        public static Result WriteLines(IFile offsetsFile, IFile textFile, ReadOnlySpan<string> lines)
        {
            var offsetsFileSize = (lines.Length + 1) * EntrySize;

            var result = offsetsFile.SetSize(offsetsFileSize);
            if (result.IsFailure()) return result.Miss();

            result = textFile.SetSize(2);
            if (result.IsFailure()) return result.Miss();

            Span<byte> entrySpan = stackalloc byte[EntrySize];

            byte[]? buf = null;
            try
            {
                BEInt32 entry;
                var dataOffs = 0u;
                for (var i = 0; i < lines.Length; i++)
                {
                    entry = dataOffs;
                    MemoryMarshal.Write(entrySpan, ref entry);
                    // write the offset of the data to the offsets file
                    result = offsetsFile.Write(i * EntrySize, entrySpan, WriteOption.None);
                    if (result.IsFailure()) return result.Miss();

                    var str = lines[i];
                    var encBytes = Encoding.UTF8.GetByteCount(str);
                    if (buf is null || buf.Length < encBytes)
                    {
                        if (buf is not null)
                            ArrayPool<byte>.Shared.Return(buf);
                        buf = ArrayPool<byte>.Shared.Rent(encBytes);
                    }

                    // write the text bytes
                    var bytes = Encoding.UTF8.GetBytes(str, buf);
                    result = textFile.Write(dataOffs, buf.AsSpan().Slice(0, bytes), WriteOption.None);
                    if (result.IsFailure()) return result.Miss();

                    dataOffs += (uint)bytes;
                }

                // finally, we write a -1
                entry = uint.MaxValue;
                MemoryMarshal.Write(entrySpan, ref entry);
                result = offsetsFile.Write(lines.Length * EntrySize, entrySpan, WriteOption.None);
                if (result.IsFailure()) return result.Miss();
            }
            finally
            {
                if (buf is not null)
                    ArrayPool<byte>.Shared.Return(buf);
            }

            // and we're done!
            result = offsetsFile.Flush();
            if (result.IsFailure()) return result.Miss();
            result = textFile.Flush();
            if (result.IsFailure()) return result.Miss();

            return Result.Success;
        }
    }
}
