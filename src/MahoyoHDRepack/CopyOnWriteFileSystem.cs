using System;
using System.Buffers;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack;

public abstract class CopyOnWriteFileSystem : IFileSystem
{
    private readonly SharedRef<IStorage> storageRef;
    protected readonly IStorage Storage;
    private long storageSize;

    protected long StorageSize => storageSize;

    protected CopyOnWriteFileSystem(SharedRef<IStorage> storageRef, IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        this.storageRef = SharedRef<IStorage>.CreateCopy(storageRef);
        Storage = storage;
        storage.GetSize(out storageSize).ThrowIfFailure();
    }

    protected struct CowEntry
    {
        public uint Size;
        public uint Offset;
        internal uint NewSize;
        internal uint NewOffset;
        internal MemoryStorage? CowStorage;
        internal CopyOnWriteStorage? Cow;
        internal FwStorage? Fw;
    }

    public override void Dispose()
    {
        storageRef.Destroy();
        base.Dispose();
    }

    protected IStorage GetStorageForEntry(ref CowEntry entry)
    {
        var cowStor = entry.CowStorage ??= new();
        var cow = entry.Cow ??= new CopyOnWriteStorage(new(new SubStorage(Storage, entry.Offset, entry.Size)), new(cowStor));
        // the extra layer of indirection is so that a flush (and thus rebuild of the COW machinery) can be persisted sanely
        var fw = entry.Fw ??= new(cow);
        fw.Storage = cow;

        return fw;
    }

    protected abstract int GetEntryCount();
    protected abstract uint GetDataOffset();
    protected abstract uint AlignOffset(uint offset);
    protected abstract ref CowEntry GetEntry(int i);

    protected abstract Result WriteHeader(IStorage storage, uint dataOffset);

    protected override Result DoFlush()
    {
        // during a flush, we want to rebuild the entry list, update the final size of the file, and write the whole thing out

        // first is the entry list rebuild
        var numEntries = GetEntryCount();
        var fileDataOffset = GetDataOffset();

        var offset = fileDataOffset;
        for (var i = 0; i < numEntries; i++)
        {
            ref var entry = ref GetEntry(i);

            // first, we want to update the size to the real size in CowStorage
            if (entry.Cow is { HasWritten: true })
            {
                entry.NewSize = (uint)entry.CowStorage!.Size;
            }
            else
            {
                entry.NewSize = entry.Size;
            }

            // then, we want to set the offset
            offset = AlignOffset(offset);
            entry.NewOffset = offset;
            // then update the offset correctly
            offset += entry.NewSize;
        }

        var finalFileSize = offset;

        Result result;

        // in order to not move EVERYTHING in-memory, we change the order that we do things based on whether the file is growing or shrinking
        if (finalFileSize > storageSize)
        {
            // if the file is growing, we want to set the new storage size *first*, then copy all unchanged files into place from the end
            result = Storage.SetSize(finalFileSize);
            if (result.IsFailure()) return result.Miss();

            for (var i = numEntries - 1; i >= 0; i--)
            {
                ref var entry = ref GetEntry(i);
                if (entry.Cow is null or { HasWritten: false })
                {
                    Helpers.Assert(entry.Size == entry.NewSize);
                    // this entry hasn't changed, do the copy
                    result = MoveBlock(entry.Offset, entry.NewOffset, entry.Size);
                    if (result.IsFailure()) return result.Miss();
                }
            }
        }
        else
        {
            // if the file is shrinking, we want to copy all unchanged files into place from the start, then set the storage size
            for (var i = 0; i < numEntries; i++)
            {
                ref var entry = ref GetEntry(i);
                if (entry.Cow is null or { HasWritten: false })
                {
                    Helpers.Assert(entry.Size == entry.NewSize);
                    // this entry hasn't changed, do the copy
                    result = MoveBlock(entry.Offset, entry.NewOffset, entry.Size);
                    if (result.IsFailure()) return result.Miss();
                }
            }

            result = Storage.SetSize(finalFileSize);
            if (result.IsFailure()) return result.Miss();
        }

        storageSize = finalFileSize;

        // now we can go through our entries from the front and copy in new data
        for (var i = 0; i < numEntries; i++)
        {
            ref var entry = ref GetEntry(i);
            if (entry.Cow is null or { HasWritten: false })
            {
                // data hasn't changed, so just move on
                continue;
            }

            // TODO: use Result based CopyTo
            entry.CowStorage.CopyTo(Storage.Slice(entry.NewOffset, entry.NewSize));
        }

        // next, we need to write the archive header info
        result = WriteHeader(Storage, fileDataOffset);
        if (result.IsFailure()) return result.Miss();

        // and we finally clean up all of the entries, updating them with new COW spans into the underlying file
        for (var i = 0; i < numEntries; i++)
        {
            ref var entry = ref GetEntry(i);
            var cowStor = entry.CowStorage;
            var cow = entry.Cow;
            entry.CowStorage = null;
            entry.Cow = null;
            cowStor?.Dispose();
            cow?.Dispose();

            entry.Size = entry.NewSize;
            entry.Offset = entry.NewOffset;
            if (cow is not null)
            {
                // GetStorageForEntry reinitializes COW and updates fw
                _ = GetStorageForEntry(ref entry);
            }
        }

        return Storage.Flush();
    }

    private Result MoveBlock(long fromOffs, long toOffs, long length)
    {
        if (fromOffs == toOffs || length <= 0)
            return Result.Success;

        int direction;
        if (toOffs < fromOffs && toOffs + length > fromOffs)
        {
            // to overlaps from from the high end
            // this means that we need to copy blocks forward
            direction = 1;
        }
        else if (fromOffs < toOffs && fromOffs + length > toOffs)
        {
            // from overlaps to from the high end
            // this means that we need to copy blocks backward
            direction = -1;
        }
        else
        {
            // there is no overlap, it doesn't matter which direction we take
            direction = 1;
        }

        const int BlockSize = 0x4000;

        long readHead;
        long writeHead;
        var readSize = (int)(length % BlockSize);
        if (readSize == 0) readSize = BlockSize;

        if (direction > 0)
        {
            readHead = fromOffs;
            writeHead = toOffs;
        }
        else
        {
            readHead = fromOffs + length - readSize;
            writeHead = toOffs + length - readSize;
        }

        var buf = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            // we always want to do one iteration
            do
            {
                var span = buf.AsSpan().Slice(0, readSize);
                var result = Storage.Read(readHead, span);
                if (result.IsFailure()) return result.Miss();
                result = Storage.Write(writeHead, span);
                if (result.IsFailure()) return result.Miss();

                if (direction < 0)
                {
                    readSize = BlockSize;
                }
                readHead += readSize * direction;
                writeHead += readSize * direction;
                if (direction > 0)
                {
                    readSize = BlockSize;
                }
            }
            while (readHead >= fromOffs && readHead < fromOffs + length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Result.Success;
    }
}
