using System;
using LibHac;
using LibHac.Fs;

namespace MahoyoHDRepack
{
    internal class MemoryStorage : IStorage
    {
        private byte[] memory = Array.Empty<byte>();

        public static MemoryStorage Adopt(byte[] data) => new() { memory = data };

        public long Size => memory.LongLength;
        public ReadOnlyMemory<byte> AllData => memory;

        public override Result Flush() => Result.Success;
        public override Result GetSize(out long size)
        {
            size = Size;
            return Result.Success;
        }

        // TODO: the fuck do I even do with this
        public override Result OperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => Result.Success;
        public override Result Read(long offset, Span<byte> destination)
        {
            var result = CheckAccessRange(offset, destination.Length, Size);
            if (result.IsFailure()) return result.Miss();
            memory.AsSpan().Slice((int)offset, destination.Length).CopyTo(destination);
            return Result.Success;
        }

        public override Result SetSize(long size)
        {
            var newArr = GC.AllocateUninitializedArray<byte>((int)size);
            Array.Copy(memory, 0, newArr, 0, Math.Min(size, Size));
            memory = newArr;
            return Result.Success;
        }

        public override Result Write(long offset, ReadOnlySpan<byte> source)
        {
            var result = CheckAccessRange(offset, source.Length, Size);
            if (result.IsFailure()) return result.Miss();
            source.CopyTo(memory.AsSpan().Slice((int)offset));
            return Result.Success;
        }
    }
}
