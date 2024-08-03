using System;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.InteropServices;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack.ScriptText;

internal sealed class SysmesText
{
    private readonly ImmutableArray<byte>[][] languageLists;

    private SysmesText(ImmutableArray<byte>[][] languageLists)
    {
        this.languageLists = languageLists;
    }

    public ImmutableArray<byte>[]? GetForLanguage(GameLanguage lang)
    {
        if ((uint)(int)lang >= languageLists.Length)
        {
            // language isn't supported
            return null;
        }
        return languageLists[(int)lang];
    }

    public static SysmesText ReadFromFile(IFile file)
    {
        file.GetSize(out var fileSize).ThrowIfFailure();

        var bytes = new byte[fileSize];
        var remainingSpan = bytes.AsSpan();

        while (remainingSpan.Length > 0)
        {
            file.Read(out var bytesRead, fileSize - remainingSpan.Length, remainingSpan).ThrowIfFailure();

            remainingSpan = remainingSpan.Slice((int)bytesRead);
        }

        var byteSpan = bytes.AsSpan();

        var langCount = MemoryMarshal.Read<LEUInt32>(byteSpan).Value;
        var numStrings = MemoryMarshal.Read<LEUInt32>(byteSpan[4..]).Value;

        var langLists = new ImmutableArray<byte>[langCount][];
        for (var i = 0; i < langCount; i++)
        {
            var strings = new ImmutableArray<byte>[numStrings];
            langLists[i] = strings;

            var firstStringOffsetOffset = (int)MemoryMarshal.Read<LEUInt64>(byteSpan[(16 + (i * 8))..]).Value;

            for (var j = 0; j < numStrings; j++)
            {
                var stringOffset = (int)MemoryMarshal.Read<LEUInt64>(byteSpan[(firstStringOffsetOffset + (j * 8))..]).Value;

                var stringBytes = byteSpan[stringOffset..];
                var strLen = ((ReadOnlySpan<byte>)stringBytes).IndexOf((byte)0);
                var str = strLen >= 0 ? stringBytes.Slice(0, strLen) : stringBytes;

                strings[j] = [.. str];
            }
        }

        return new(langLists);
    }

    public void WriteToFile(IFile file)
    {
        // first, compute the full size
        var fullSize = 0;
        // base header size
        fullSize += 16;
        // one entry for each language
        fullSize += 8 * languageLists.Length;
        // one entry per language for each string
        var numStrs = languageLists[0].Length;
        foreach (var lang in languageLists)
        {
            // all language arrays should be the same length
            Helpers.Assert(lang.Length == numStrs);
        }
        fullSize += 8 * languageLists.Length * numStrs;
        var firstString = fullSize;

        // then the length of all strings plus 1
        foreach (var lang in languageLists)
        {
            foreach (var str in lang)
            {
                fullSize += str.Length + 1; // for null terminator
            }
        }
        // but the final string doesn't have a null terminator
        fullSize -= 1;

        file.SetSize(fullSize).ThrowIfFailure();

        // header
        Span<byte> u8data = stackalloc byte[8];
        MemoryMarshal.Write<LEUInt32>(u8data, (uint)languageLists.Length);
        MemoryMarshal.Write<LEUInt32>(u8data[4..], (uint)numStrs);
        file.Write(0, u8data, default).ThrowIfFailure();
        u8data.Clear();
        file.Write(8, u8data, default).ThrowIfFailure();

        // offsets to first of each language string
        for (var i = 0; i < languageLists.Length; i++)
        {
            MemoryMarshal.Write<LEUInt64>(u8data, (ulong)(16 + (8 * languageLists.Length) + (8 * i * numStrs)));
            file.Write(16 + (8 * i), u8data, default).ThrowIfFailure();
        }

        // offsets to strings and strings
        var stringOffset = firstString;
        var indexOffset = 16 + (8 * languageLists.Length);
        foreach (var lang in languageLists)
        {
            foreach (var str in lang)
            {
                // write the index
                MemoryMarshal.Write<LEUInt64>(u8data, (ulong)stringOffset);
                file.Write(indexOffset, u8data, default).ThrowIfFailure();
                indexOffset += 8;

                // write the string
                file.Write(stringOffset, str.AsSpan(), default).ThrowIfFailure();
                if (stringOffset + str.Length < fullSize)
                {
                    file.Write(stringOffset + str.Length, [0], default).ThrowIfFailure();
                }
                stringOffset += str.Length + 1;
            }
        }
    }

}
