using System;
using System.Collections.Immutable;
using System.Linq;

namespace MahoyoHDRepack.ScriptText.DeepLuna;

internal sealed record DeepLunaLine(
    string SourceFile,
    int SourceLineStart, int SourceLineEnd,
    ImmutableArray<byte> Hash,
    int? Offset,
    string? Translated,
    string? Comments) : IEquatable<DeepLunaLine>
{
    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(SourceFile);
        hc.Add(SourceLineStart);
        hc.Add(SourceLineEnd);
        hc.AddBytes(Hash.AsSpan());
        hc.Add(Offset);
        hc.Add(Translated);
        hc.Add(Comments);
        return hc.ToHashCode();
    }

    public bool Equals(DeepLunaLine? other)
        => other is not null
        && SourceFile == other.SourceFile
        && SourceLineStart == other.SourceLineStart
        && SourceLineEnd == other.SourceLineEnd
        && Hash.AsSpan().SequenceEqual(other.Hash.AsSpan())
        && Offset == other.Offset
        && Translated == other.Translated
        && Comments == other.Comments;
}
