using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MahoyoHDRepack.Utility;

internal sealed class ImmutableArrayEqualityComparer<T> : IEqualityComparer<ImmutableArray<T>>
{
    public static readonly ImmutableArrayEqualityComparer<T> Instance = new();

    public bool Equals(ImmutableArray<T> x, ImmutableArray<T> y)
        => x.AsSpan().SequenceEqual(y.AsSpan());

    public unsafe int GetHashCode([DisallowNull] ImmutableArray<T> obj)
    {
        var hc = new HashCode();

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            foreach (var it in obj)
            {
                hc.Add(it);
            }
        }
        else
        {
            var objSpan = obj.AsSpan();
            var byteSpan = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(objSpan)),
                objSpan.Length * Unsafe.SizeOf<T>());
            hc.AddBytes(byteSpan);
        }

        return hc.ToHashCode();
    }
}
