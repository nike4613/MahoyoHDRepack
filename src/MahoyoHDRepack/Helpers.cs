using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MahoyoHDRepack;

public static class Helpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Has<T>(this T @enum, T value) where T : struct, Enum
    {
        var flgsVal = NumericValue(value);
        return (NumericValue(@enum) & flgsVal) == flgsVal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong NumericValue<T>(T value) where T : struct, Enum
    {
        ulong result = 0;
        Unsafe.CopyBlock(ref Unsafe.As<ulong, byte>(ref result), ref Unsafe.As<T, byte>(ref value), (uint)Unsafe.SizeOf<T>());
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfArgumentNull<T>([NotNull] T? arg, [CallerArgumentExpression("arg")] string name = "")
    {
        if (arg is null)
            ThrowArgumentNull(name);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T ThrowIfNull<T>([NotNull] T? arg, [CallerArgumentExpression("arg")] string name = "")
    {
        if (arg is null)
            ThrowArgumentNull(name);
        return arg;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowArgumentNull(string argName)
    {
        throw new ArgumentNullException(argName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LEToHost(byte x) => x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort LEToHost(ushort x)
        => BitConverter.IsLittleEndian ? x : (ushort)BitPermuteStepSimple(x, 0x00ff00ff, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LEToHost(uint x)
        => BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff00ff00, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff00ff, 3 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LEToHost(ulong x)
        => BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff000000_ff000000, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff0000_00ff0000, 3 * 8)
            | BitOperations.RotateLeft(x & 0x0000ff00_0000ff00, 5 * 8)
            | BitOperations.RotateLeft(x & 0x000000ff_000000ff, 7 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte BEToHost(byte x) => x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort BEToHost(ushort x)
        => !BitConverter.IsLittleEndian ? x : (ushort)BitPermuteStepSimple(x, 0x00ff00ff, 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint BEToHost(uint x)
        => !BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff00ff00, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff00ff, 3 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BEToHost(ulong x)
        => !BitConverter.IsLittleEndian ? x
            : BitOperations.RotateLeft(x & 0xff000000_ff000000, 1 * 8)
            | BitOperations.RotateLeft(x & 0x00ff0000_00ff0000, 3 * 8)
            | BitOperations.RotateLeft(x & 0x0000ff00_0000ff00, 5 * 8)
            | BitOperations.RotateLeft(x & 0x000000ff_000000ff, 7 * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitPermuteStepSimple(uint x, uint m, int shift)
        => ((x & m) << shift) | ((x >> shift) & m);

    public static T EventAdd<T>(ref T? evt, T del) where T : Delegate
    {
        T? orig;
        T newDel;
        do
        {
            orig = evt;
            newDel = (T)Delegate.Combine(orig, del);
        } while (Interlocked.CompareExchange(ref evt, newDel, orig) != orig);
        return newDel;
    }

    public static T? EventRemove<T>(ref T? evt, T del) where T : Delegate
    {
        T? orig;
        T? newDel;
        do
        {
            orig = evt;
            newDel = (T?)Delegate.Remove(orig, del);
        } while (Interlocked.CompareExchange(ref evt, newDel, orig) != orig);
        return newDel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Assert([DoesNotReturnIf(false)] bool value,
        string? message = null,
        [CallerArgumentExpression("value")] string expr = ""
    )
    {
        if (!value)
            ThrowAssertionFailed(message, expr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Conditional("DEBUG")]
    public static void DAssert([DoesNotReturnIf(false)] bool value,
        string? message = null,
        [CallerArgumentExpression("value")] string expr = ""
    )
    {
        if (!value)
            ThrowAssertionFailed(message, expr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Assert([DoesNotReturnIf(false)] bool value,
        [InterpolatedStringHandlerArgument("value")] ref AssertionInterpolatedStringHandler message,
        [CallerArgumentExpression("value")] string expr = ""
    )
    {
        if (!value)
            ThrowAssertionFailed(message.ToStringAndClear(), expr);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Conditional("DEBUG")]
    public static void DAssert([DoesNotReturnIf(false)] bool value,
        [InterpolatedStringHandlerArgument("value")] ref AssertionInterpolatedStringHandler message,
        [CallerArgumentExpression("value")] string expr = ""
    )
    {
        if (!value)
            ThrowAssertionFailed(message.ToStringAndClear(), expr);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [DoesNotReturn]
    private static void ThrowAssertionFailed(string? msg, string expr)
    {
        throw new AssertionFailedException(msg, expr);
    }
}
public sealed class AssertionFailedException : Exception
{
    private const string AssertFailed = "Assertion failed! ";

    public AssertionFailedException() : base()
    {
        Message = "";
    }

    public AssertionFailedException(string? message) : base(AssertFailed + message)
    {
        Message = message ?? "";
    }

    public AssertionFailedException(string? message, Exception innerException) : base(AssertFailed + message, innerException)
    {
        Message = message ?? "";
    }

    public AssertionFailedException(string? message, string expression) : base($"{AssertFailed}{expression} {message}")
    {
        Message = message ?? "";
        Expression = expression;
    }

    public string Expression { get; } = "";

    public new string Message { get; }
}

[InterpolatedStringHandler]
public ref struct AssertionInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler handler;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AssertionInterpolatedStringHandler(int literalLen, int formattedCount, bool assertValue, out bool isEnabled)
    {
        if (isEnabled = !assertValue)
        {
            handler = new(literalLen, formattedCount);
        }
    }

    public override string ToString() => handler.ToString();
    public string ToStringAndClear() => handler.ToStringAndClear();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(string s)
    {
        handler.AppendLiteral(s);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(string? s)
    {
        handler.AppendFormatted(s);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(string? s, int alignment = 0, string? format = default)
    {
        handler.AppendFormatted(s, alignment, format);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(ReadOnlySpan<char> s)
    {
        handler.AppendFormatted(s);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted(ReadOnlySpan<char> s, int alignment = 0, string? format = default)
    {
        handler.AppendFormatted(s, alignment, format);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value)
    {
        handler.AppendFormatted(value);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, int alignment)
    {
        handler.AppendFormatted(value, alignment);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, string? format)
    {
        handler.AppendFormatted(value, format);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        handler.AppendFormatted(value, alignment, format);
    }
}
