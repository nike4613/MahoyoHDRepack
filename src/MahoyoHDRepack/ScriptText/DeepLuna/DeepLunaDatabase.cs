using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MahoyoHDRepack.Utility;

namespace MahoyoHDRepack.ScriptText.DeepLuna;

internal class DeepLunaDatabase
{
    private readonly struct LineWithListIndex
    {
        public required DeepLunaLine Line { get; init; }
        public required int Index { get; init; }
    }

    public int Count => allLines.Count;

    private readonly List<DeepLunaLine> allLines = new();
    private readonly Dictionary<ImmutableArray<byte>, LineWithListIndex> byHash = new(ImmutableArrayEqualityComparer<byte>.Instance);
    private readonly Dictionary<int, LineWithListIndex> byOffset = new();

    public void InsertLine(DeepLunaLine line)
    {
        var lineWithIndex = new LineWithListIndex
        {
            Line = line,
            Index = allLines.Count
        };

        var addedToAny = false;

        if (!line.Hash.IsDefault)
        {
            addedToAny |= byHash.TryAdd(line.Hash, lineWithIndex);
        }

        if (line.Offset is { } offset)
        {
            addedToAny |= byOffset.TryAdd(offset, lineWithIndex);
        }

        // if it wasn't added to anything, it's not a "new" line, so we can ignore it
        if (addedToAny)
        {
            allLines.Add(line);
        }
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "deepLuna uses SHA1, so there's nothing I can do")]
    public bool TryLookupLine(ReadOnlySpan<byte> jpLine, int offset, [MaybeNullWhen(false)] out DeepLunaLine line)
    {
        // start with a by-offset lookup, because that's cheaper than computing a hash
        if (byOffset.TryGetValue(offset, out var result))
        {
            line = result.Line;
            return true;
        }

        // then compute a hash and try to use that
        var hash = ImmutableCollectionsMarshal.AsImmutableArray(SHA1.HashData(jpLine));
        if (byHash.TryGetValue(hash, out result))
        {
            line = result.Line;
            return true;
        }

        line = null;
        return false;
    }

    public IEnumerable<DeepLunaLine> UnusedLines => allLines.Where(l => !l.Used);
}
