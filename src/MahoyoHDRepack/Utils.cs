using System;
using System.Numerics;
using System.Runtime.CompilerServices;
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
        var result = copy.InitializeWithNormalization(path.AsSpan());
        if (result.IsFailure()) return result.Miss();

        return Result.Success;
    }

    public static ReadOnlySpan<byte> AsSpan(this in Path path)
        => path.GetString()[0..path.GetLength()];


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LEToHost(byte x) => x;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte HostToLE(byte x) => x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort LEToHost(ushort x)
        => BitConverter.IsLittleEndian ? x : (ushort)BitPermuteStepSimple(x, 0x00ff00ff, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort HostToLE(ushort x) => LEToHost(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LEToHost(uint x)
        => BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff00ff00, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff00ff, 3 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HostToLE(uint x) => LEToHost(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LEToHost(ulong x)
        => BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff000000_ff000000, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff0000_00ff0000, 3 * 8)
            | BitOperations.RotateLeft(x & 0x0000ff00_0000ff00, 5 * 8)
            | BitOperations.RotateLeft(x & 0x000000ff_000000ff, 7 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong HostToLE(ulong x) => LEToHost(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte BEToHost(byte x) => x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte HostToBE(byte x) => x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort BEToHost(ushort x)
        => !BitConverter.IsLittleEndian ? x : (ushort)BitPermuteStepSimple(x, 0x00ff00ff, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort HostToBE(ushort x) => BEToHost(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BEToHost(uint x)
        => !BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff00ff00, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff00ff, 3 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HostToBE(uint x) => BEToHost(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BEToHost(ulong x)
        => !BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff000000_ff000000, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff0000_00ff0000, 3 * 8)
            | BitOperations.RotateLeft(x & 0x0000ff00_0000ff00, 5 * 8)
            | BitOperations.RotateLeft(x & 0x000000ff_000000ff, 7 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong HostToBE(ulong x) => BEToHost(x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitPermuteStepSimple(uint x, uint m, int shift)
        => ((x & m) << shift) | ((x >> shift) & m);
}
