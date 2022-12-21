using System;
using System.Buffers;
using LibHac;
using LibHac.Common;
using LibHac.Fs;

namespace MahoyoHDRepack
{
    internal class CopyOnWriteStorage : IStorage
    {
        private SharedRef<IStorage> readStorage;
        private SharedRef<IStorage> writeStorage;

        public bool HasWritten { get; private set; }

        public CopyOnWriteStorage(in SharedRef<IStorage> readStorage, in SharedRef<IStorage> writeStorage)
        {
            this.readStorage = SharedRef<IStorage>.CreateCopy(readStorage);
            this.writeStorage = SharedRef<IStorage>.CreateCopy(writeStorage);
        }

        public override void Dispose()
        {
            readStorage.Destroy();
            writeStorage.Destroy();
        }

        public override Result Flush()
        {
            if (HasWritten)
            {
                return writeStorage.Get.Flush();
            }

            return Result.Success;
        }

        public override Result GetSize(out long size)
        {
            if (HasWritten)
            {
                return writeStorage.Get.GetSize(out size);
            }
            else
            {
                return readStorage.Get.GetSize(out size);
            }
        }

        public override Result Read(long offset, Span<byte> destination)
        {
            if (HasWritten)
            {
                return writeStorage.Get.Read(offset, destination);
            }
            else
            {
                return readStorage.Get.Read(offset, destination);
            }
        }

        private Result CopyIfNeeded()
        {
            if (HasWritten)
            {
                return Result.Success;
            }

            // otherwise, we need to copy
            var result = readStorage.Get.GetSize(out var size);
            if (result.IsFailure()) return result.Miss();
            result = writeStorage.Get.SetSize(size);
            if (result.IsFailure()) return result.Miss();

            const int BufSize = 0x4000;
            var offset = 0L;
            var buf = ArrayPool<byte>.Shared.Rent(BufSize);
            try
            {
                while (offset < size)
                {
                    var realBuf = buf.AsSpan().Slice(0, (int)Math.Min(BufSize, size - offset));

                    result = readStorage.Get.Read(offset, realBuf);
                    if (result.IsFailure()) return result.Miss();

                    result = writeStorage.Get.Write(offset, realBuf);
                    if (result.IsFailure()) return result.Miss();

                    offset += realBuf.Length;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            HasWritten = true;

            return Result.Success;
        }

        public override Result OperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
        {
            var result = CopyIfNeeded();
            if (result.IsFailure()) return result.Miss();
            return writeStorage.Get.OperateRange(outBuffer, operationId, offset, size, inBuffer);
        }
        public override Result SetSize(long size)
        {
            var result = CopyIfNeeded();
            if (result.IsFailure()) return result.Miss();
            return writeStorage.Get.SetSize(size);
        }
        public override Result Write(long offset, ReadOnlySpan<byte> source)
        {
            var result = CopyIfNeeded();
            if (result.IsFailure()) return result.Miss();
            return writeStorage.Get.Write(offset, source);
        }
    }
}
