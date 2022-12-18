using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using LibHac.Fs;
using LibHac;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack;

public sealed class PartialFile : IFile
{
    private readonly IFile file;
    private readonly long offset;
    private readonly long length;

    public PartialFile(IFile file, long offset, long length)
    {
        if (file is PartialFile partial)
        {
            this.file = partial.file;
            this.offset = partial.offset + offset;
            this.length = length;
        }
        else
        {
            this.file = file;
            this.offset = offset;
            this.length = length;
        }
    }

    protected override Result DoGetSize(out long size)
    {
        size = length;
        return Result.Success;
    }

    protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
    {
        Unsafe.SkipInit(out bytesRead);

        var result = IStorage.CheckAccessRange(offset, destination.Length, length);
        if (result.IsFailure()) return result;

        return file.Read(out bytesRead, this.offset + offset, destination, in option);
    }

    protected override Result DoFlush() => file.Flush();
    protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
    protected override Result DoSetSize(long size) => throw new NotSupportedException();
    protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();
}
