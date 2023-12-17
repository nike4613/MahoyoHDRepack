using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MahoyoHDRepack;

[StructLayout(LayoutKind.Explicit, Size = sizeof(ushort))]
public struct LEUInt16
{
    [FieldOffset(0)]
    private ushort value;

    public ushort Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.LEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToLE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LEUInt16 From(ushort value)
    {
        LEUInt16 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ushort(LEUInt16 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LEUInt16(ushort x) => From(x);
}

[StructLayout(LayoutKind.Explicit, Size = sizeof(uint))]
public struct LEUInt32
{
    [FieldOffset(0)]
    private uint value;

    public uint Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.LEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToLE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LEUInt32 From(uint value)
    {
        LEUInt32 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator uint(LEUInt32 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LEUInt32(uint x) => From(x);
}

[StructLayout(LayoutKind.Explicit, Size = sizeof(ulong))]
public struct LEUInt64
{
    [FieldOffset(0)]
    private ulong value;

    public ulong Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.LEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToLE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LEUInt64 From(ulong value)
    {
        LEUInt64 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(LEUInt64 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LEUInt64(ulong x) => From(x);
}

[StructLayout(LayoutKind.Explicit, Size = sizeof(byte))]
public struct BEInt8
{
    [FieldOffset(0)]
    private byte value;

    public byte Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.BEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToBE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BEInt8 From(byte value)
    {
        BEInt8 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator byte(BEInt8 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator BEInt8(byte x) => From(x);
}

[StructLayout(LayoutKind.Explicit, Size = sizeof(ushort))]
public struct BEInt16
{
    [FieldOffset(0)]
    private ushort value;

    public ushort Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.BEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToBE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BEInt16 From(ushort value)
    {
        BEInt16 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ushort(BEInt16 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator BEInt16(ushort x) => From(x);
}

[StructLayout(LayoutKind.Explicit, Size = sizeof(uint))]
public struct BEInt32
{
    [FieldOffset(0)]
    private uint value;

    public uint Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.BEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToBE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BEInt32 From(uint value)
    {
        BEInt32 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator uint(BEInt32 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator BEInt32(uint x) => From(x);
}

[StructLayout(LayoutKind.Explicit, Size = sizeof(ulong))]
public struct BEInt64
{
    [FieldOffset(0)]
    private ulong value;

    public ulong Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Utils.BEToHost(value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => this.value = Utils.HostToBE(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BEInt64 From(ulong value)
    {
        BEInt64 val = default;
        val.Value = value;
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(BEInt64 x) => x.Value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator BEInt64(ulong x) => From(x);
}
