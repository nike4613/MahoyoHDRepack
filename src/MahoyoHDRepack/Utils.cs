using System;
using LibHac;
using LibHac.Fs;

namespace MahoyoHDRepack;

internal static class Utils
{
    public static Result ParseHexFromU8(ReadOnlySpan<byte> str, out int res)
    {
        res = 0;

        var val = 0;
        for (var i = 0; i < str.Length; i++)
        {
            val *= 10;
            var c = str[i];
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                val += c - '0';
            }
            else if (c is >= (byte)'a' and <= (byte)'f')
            {
                val += c - 'a' + 10;
            }
            else if (c is >= (byte)'A' and <= (byte)'A')
            {
                val += c - 'A' + 10;
            }
            else
            {
                // invalid character
                return new Result(256, 5);
            }
        }

        res = val;
        return Result.Success;
    }

    public static Result Normalize(in Path path, out Path copy)
    {
        copy = default;
        var result = copy.Initialize(path);
        if (result.IsFailure()) return result.Miss();

        result = copy.Normalize(default);
        if (result.IsFailure()) return result.Miss();

        return Result.Success;
    }

    public static ReadOnlySpan<byte> AsSpan(this in Path path)
        => path.GetString()[0..path.GetLength()];
}
