using System;
using System.Runtime.CompilerServices;
using LibHac;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack;

public class PartialFile : IFile
{
    private readonly IStorage file;
    private readonly long offset;
    private readonly long length;

    public PartialFile(IFile file, long offset, long length)
    {
        if (file.GetType() == typeof(PartialFile))
        {
            var partial = (PartialFile)file;
            this.file = partial.file;
            this.offset = partial.offset + offset;
            this.length = length;
        }
        else
        {
            this.file = file.AsStorage();
            this.offset = offset;
            this.length = length;
        }
    }

    public PartialFile(IStorage storage, long offset, long length)
    {
        file = storage;
        this.offset = offset;
        this.length = length;
    }

    protected override Result DoGetSize(out long size)
    {
        size = length;
        return Result.Success;
    }

    protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
    {
        Unsafe.SkipInit(out bytesRead);

        var endOffs = offset + destination.Length;
        endOffs = Math.Clamp(endOffs, 0, length);
        destination = destination.Slice(0, (int)(endOffs - offset));

        if (destination.Length is 0)
        {
            return ResultFs.OutOfRange.Value;
        }


        bytesRead = destination.Length;
        return file.Read(this.offset + offset, destination);
    }

    protected override Result DoFlush() => file.Flush();
    protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => ResultFs.NotImplemented.Miss();
    protected override Result DoSetSize(long size) => ResultFs.NotImplemented.Miss();
    protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => ResultFs.NotImplemented.Miss();
}
