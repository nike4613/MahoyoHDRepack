using System;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack;

internal static class FileScanner
{
    public static ReadOnlySpan<byte> Mzp => "mrgd00"u8;
    public static ReadOnlySpan<byte> Mzx => "MZX0"u8;
    public static ReadOnlySpan<byte> NxCx => "NXCX"u8;
    public static ReadOnlySpan<byte> NxGx => "NXGX"u8;
    public static ReadOnlySpan<byte> Hfa => "HUNEXGGEFA10"u8;


    private const int MaxMagicBytes = 6;

    public static KnownFileTypes ProbeForFileType(IFile file)
    {
        Span<byte> magic = stackalloc byte[MaxMagicBytes];

        var result = file.Read(out var read, 0, magic);
        if (result.IsFailure()) return KnownFileTypes.Unknown;

        if (Matches(read, magic, Mzp)) return KnownFileTypes.Mzp;
        if (Matches(read, magic, Mzx)) return KnownFileTypes.Mzx;
        if (Matches(read, magic, NxCx)) return KnownFileTypes.Nxx;
        if (Matches(read, magic, NxGx)) return KnownFileTypes.Nxx;

        return KnownFileTypes.Unknown;
    }

    private static bool Matches(long read, ReadOnlySpan<byte> magic, ReadOnlySpan<byte> test)
        => read >= test.Length && magic.StartsWith(test);

    public static IFile TryGetDecompressedFile(IFile file)
    {
        var type = ProbeForFileType(file);
        return type switch
        {
            KnownFileTypes.Unknown => file,
            KnownFileTypes.Mzp => file, // this is an archive format, not a compressed file
            KnownFileTypes.Mzx => throw new NotImplementedException(),
            KnownFileTypes.Nxx => NxxFile.TryCreate(file),
            _ => file
        };
    }
}

public enum KnownFileTypes
{
    Unknown,
    Mzp,
    Mzx,
    Nxx,
}
