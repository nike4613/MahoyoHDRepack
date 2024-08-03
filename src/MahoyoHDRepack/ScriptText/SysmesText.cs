using System;
using System.Collections.Immutable;
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
        // TODO:
    }

}
