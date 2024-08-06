using System;
using System.Diagnostics.CodeAnalysis;
using Ryujinx.Common;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.Texture;

namespace MahoyoHDRepack.Utility;

internal static class BC7Encoder
{
    [Flags]
    private enum EncodeMode
    {
        Fast,
        Exhaustive,
        ModeMask = 0xff,
        Multithreaded = 1 << 8,
    }

    private const string Bc7EncoderTypeName = "Ryujinx.Graphics.Texture.Encoders.BC7Encoder";

    private delegate void Bc7EncoderEncode(Memory<byte> outputStorage, ReadOnlyMemory<byte> data, int width, int height, EncodeMode mode);

    private static readonly Bc7EncoderEncode Encode = GetEncodeMethod();

    [DynamicDependency("Encode", Bc7EncoderTypeName, "Ryujinx.Graphics.Texture")]
    private static Bc7EncoderEncode GetEncodeMethod()
    {
        var assembly = typeof(BCnEncoder).Assembly;
        var type = assembly.GetType(Bc7EncoderTypeName, throwOnError: true)!;
        var method = type.GetMethod("Encode") ?? throw new MissingMethodException("Encode");
        return method.CreateDelegate<Bc7EncoderEncode>();
    }

    public const int BlockWidth = 4;
    public const int BlockHeight = 4;
    public const int BlockSizeBytes = 16;

    public static MemoryOwner<byte> EncodeBC7(Memory<byte> data, int width, int height, int depth, int levels, int layers, bool fastMode = true, bool multithreaded = true)
    {
        var flags = (fastMode ? EncodeMode.Fast : EncodeMode.Exhaustive) | (multithreaded ? EncodeMode.Multithreaded : 0);

        var size = 0;

        for (var l = 0; l < levels; l++)
        {
            var w = BitUtils.DivRoundUp(Math.Max(1, width >> l), BlockWidth);
            var h = BitUtils.DivRoundUp(Math.Max(1, height >> l), BlockHeight);

            size += w * h * 16 * Math.Max(1, depth >> l) * layers;
        }

        var output = MemoryOwner<byte>.Rent(size);
        var outputMemory = output.Memory;

        var imageBaseIOffs = 0;
        var imageBaseOOffs = 0;

        for (var l = 0; l < levels; l++)
        {
            var w = BitUtils.DivRoundUp(width, BlockWidth);
            var h = BitUtils.DivRoundUp(height, BlockHeight);

            for (var l2 = 0; l2 < layers; l2++)
            {
                for (var z = 0; z < depth; z++)
                {
                    Encode(
                        outputMemory[imageBaseOOffs..],
                        data[imageBaseIOffs..],
                        width,
                        height,
                        flags);

                    imageBaseIOffs += width * height * 4;
                    imageBaseOOffs += w * h * 16;
                }
            }

            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            depth = Math.Max(1, depth >> 1);
        }

        return output;
    }
}
