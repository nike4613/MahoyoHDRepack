using System;
using LibHac;
using LibHac.Fs;

namespace MahoyoHDRepack;

internal sealed class FwStorage : IStorage
{
    public IStorage Storage;
    public FwStorage(IStorage stor) => Storage = stor;

    public override Result Flush() => Storage.Flush();
    public override Result GetSize(out long size) => Storage.GetSize(out size);
    public override Result OperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
        => Storage.OperateRange(outBuffer, operationId, offset, size, inBuffer);
    public override Result Read(long offset, Span<byte> destination) => Storage.Read(offset, destination);
    public override Result SetSize(long size) => Storage.SetSize(size);
    public override Result Write(long offset, ReadOnlySpan<byte> source) => Storage.Write(offset, source);
}
