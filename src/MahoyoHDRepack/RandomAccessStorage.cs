using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibHac;
using LibHac.Fs;
using Microsoft.Win32.SafeHandles;

namespace MahoyoHDRepack
{
    internal class RandomAccessStorage : IStorage
    {
        private readonly SafeFileHandle handle;

        public RandomAccessStorage(SafeFileHandle handle)
        {
            this.handle = handle;
        }

        public override Result Flush()
        {
            return Result.Success;
        }

        public override Result GetSize(out long size)
        {
            size = RandomAccess.GetLength(handle);
            return Result.Success;
        }

        public override Result OperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
            => throw new NotImplementedException();

        public override Result Read(long offset, Span<byte> destination)
        {
            _ = RandomAccess.Read(handle, destination, offset);
            return Result.Success;
        }

        public override Result SetSize(long size)
        {
            RandomAccess.SetLength(handle, size);
            return Result.Success;
        }

        public override Result Write(long offset, ReadOnlySpan<byte> source)
        {
            RandomAccess.Write(handle, source, offset);
            return Result.Success;
        }

        public override void Dispose() => handle.Dispose();
    }
}
