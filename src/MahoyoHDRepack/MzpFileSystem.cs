using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack;

public sealed class MzpFileSystem : IFileSystem
{
    private const uint SectorSize = 0x800;
    private const int HeaderSize = 8;
    private const int EntrySize = 8;

    [StructLayout(LayoutKind.Explicit, Pack = 2, Size = HeaderSize)]
    private struct MzpHeader
    {
        public unsafe ReadOnlySpan<byte> Magic => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<MzpHeader, byte>(ref this), 6);
        [FieldOffset(6)]
        public readonly LEInt16 EntryCount;

        public MzpHeader(ushort entryCount)
        {
            FileScanner.Mzp.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<MzpHeader, byte>(ref this), 6));
            EntryCount.Value = entryCount;
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 2, Size = EntrySize)]
    private readonly struct MzpEntry
    {
        [FieldOffset(0)]
        public readonly LEInt16 SectorOffset;
        [FieldOffset(2)]
        public readonly LEInt16 ByteOffset;
        [FieldOffset(4)]
        public readonly LEInt16 SizeSectors;
        [FieldOffset(6)]
        public readonly LEInt16 SizeBytes;

        public MzpEntry(uint size, uint offset)
        {
            var (sectors, bytes) = Math.DivRem(offset, SectorSize);
            SectorOffset.Value = (ushort)sectors;
            ByteOffset.Value = (ushort)bytes;

            SizeSectors.Value = (ushort)((size & ~0xffffu) / SectorSize);
            SizeBytes.Value = (ushort)(size & 0xffffu);
        }

        public uint Size => ((SizeSectors * SectorSize) & ~0xffffu) | SizeBytes;
        public uint Offset => (SectorOffset * SectorSize) + ByteOffset;

        public Entry ToEntry(uint baseOffs) => new()
        {
            Size = Size,
            Offset = Offset + baseOffs
        };
    }

    private record struct Entry
    {
        public uint Size;
        public uint Offset;
        public uint NewSize;
        public uint NewOffset;
        public MemoryStorage? CowStorage;
        public CopyOnWriteStorage? Cow;
        public FwStorage? Fw;
    }

    private readonly IStorage storage;
    private long storageSize;
    private readonly Entry[] entries;

    private MzpFileSystem(IStorage storage, long storageSize, Entry[] entries)
    {
        this.storage = storage;
        this.storageSize = storageSize;
        this.entries = entries;
    }

    public static Result Read(ref UniqueRef<IFileSystem> fs, IStorage storage)
        => Read(ref Unsafe.As<UniqueRef<IFileSystem>, UniqueRef<MzpFileSystem>>(ref fs), storage);
    public static Result Read(ref UniqueRef<MzpFileSystem> mzpFs, IStorage storage)
    {
        var result = storage.GetSize(out var size);
        if (result.IsFailure()) return result.Miss();

        if (size < HeaderSize)
        {
            // too small for header
            return ResultFs.InvalidFileSize.Value;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        result = storage.Read(0, header);
        if (result.IsFailure()) return result.Miss();

        var hdr = MemoryMarshal.Read<MzpHeader>(header);
        if (!hdr.Magic.SequenceEqual(FileScanner.Mzp))
        {
            // file header is invalid
            return ResultFs.InvalidFatFormat.Value;
        }

        var numEntries = hdr.EntryCount.Value;

        // full file must be at least EntrySize * entries + HeaderSize bytes long
        if (size < (EntrySize * numEntries) + HeaderSize)
        {
            // file is toko short to be valid
            return ResultFs.InvalidFileSize.Value;
        }

        var dataOffset = HeaderSize + (numEntries * EntrySize);
        var entries = new Entry[numEntries];
        for (var i = 0; i < numEntries; i++)
        {
            result = ReadEntry(storage, i, out var mzpEntry);
            if (result.IsFailure()) return result.Miss();
            entries[i] = mzpEntry.ToEntry((uint)dataOffset);
        }

        mzpFs.Get = new MzpFileSystem(storage, size, entries);
        return Result.Success;
    }

    private static Result ReadEntry(IStorage storage, int index, out MzpEntry entry)
    {
        Unsafe.SkipInit(out entry);

        var entryOffset = HeaderSize + (index * EntrySize);
        Span<byte> entryData = stackalloc byte[EntrySize];
        var result = storage.Read(entryOffset, entryData);
        if (result.IsFailure()) return result.Miss();

        entry = MemoryMarshal.Read<MzpEntry>(entryData);
        return Result.Success;
    }

    private static Result GetPathIndex(in Path path, out int index)
    {
        Unsafe.SkipInit(out index);
        var result = Utils.Normalize(path, out var copy);
        if (result.IsFailure()) return result.Miss();

        var pathStr = copy.AsSpan();
        if (pathStr[0] == '/')
            pathStr = pathStr[1..];

        if (pathStr.Contains((byte)'/'))
        {
            // path has directories
            return ResultFs.InvalidPath.Value;
        }

        result = Utils.ParseHexFromU8(pathStr, out index);
        if (result.IsFailure()) return result.Miss();

        return Result.Success;
    }

    private sealed class FwStorage : IStorage
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

    public Result OpenFile(ref UniqueRef<IFile> outFile, int index, OpenMode mode)
    {
        if (index < 0 || index >= entries.Length)
        {
            // bad index
            return ResultFs.FileNotFound.Value;
        }

        // read the entry corresponding to the file
        ref var entry = ref entries[index];
        var realDataOffset = entry.Offset;
        var realDataLength = entry.Size;

        var result = IStorage.CheckAccessRange(realDataOffset, realDataLength, storageSize);
        if (result.IsFailure()) return result.Miss();

        outFile.Get = GetStorageForEntry(ref entry).AsFile(mode);
        return Result.Success;
    }

    private IStorage GetStorageForEntry(ref Entry entry)
    {
        var cowStor = entry.CowStorage ??= new();
        var cow = entry.Cow ??= new CopyOnWriteStorage(new(new SubStorage(storage, entry.Offset, entry.Size)), new(cowStor));
        // the extra layer of indirection is so that a flush (and thus rebuild of the COW machinery) can be persisted sanely
        var fw = entry.Fw ??= new(cow);
        fw.Storage = cow;

        return fw;
    }

    protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
    {
        var result = GetPathIndex(path, out var index);
        if (result.IsFailure()) return result.Miss();
        return OpenFile(ref outFile, index, mode);
    }

    protected override Result DoFlush()
    {
        // during a flush, we want to rebuild the entry list, update the final size of the file, and write the whole thing out

        // first is the entry list rebuild
        var numEntries = entries.Length;
        var fileDataOffset = (uint)(numEntries * EntrySize) + HeaderSize;

        var offset = fileDataOffset;
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];

            // first, we want to update the size to the real size in CowStorage
            if (entry.CowStorage is not null)
            {
                entry.NewSize = (uint)entry.CowStorage.Size;
            }
            else
            {
                entry.NewSize = entry.Size;
            }

            // then, we want to set the offset
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
            result = storage.SetSize(finalFileSize);
            if (result.IsFailure()) return result.Miss();

            for (var i = entries.Length - 1; i >= 0; i--)
            {
                ref var entry = ref entries[i];
                if (entry.CowStorage is null or { Size: 0 })
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
            for (var i = 0; i < entries.Length; i++)
            {
                ref var entry = ref entries[i];
                if (entry.CowStorage is null or { Size: 0 })
                {
                    Helpers.Assert(entry.Size == entry.NewSize);
                    // this entry hasn't changed, do the copy
                    result = MoveBlock(entry.Offset, entry.NewOffset, entry.Size);
                    if (result.IsFailure()) return result.Miss();
                }
            }

            result = storage.SetSize(finalFileSize);
            if (result.IsFailure()) return result.Miss();
        }

        storageSize = finalFileSize;

        // now we can go through our entries from the front and copy in new data
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            if (entry.CowStorage is null or { Size: 0 })
            {
                // data hasn't changed, so just move on
                continue;
            }

            // TODO: use Result based CopyTo
            entry.CowStorage.CopyTo(storage.Slice(entry.NewOffset, entry.NewSize));
        }

        // next, we need to write the archive header info
        // first we write the MZP header
        Span<byte> hdrSpan = stackalloc byte[HeaderSize];
        var mzpHeader = new MzpHeader((ushort)numEntries);
        MemoryMarshal.Write(hdrSpan, ref mzpHeader);

        result = storage.Write(0, hdrSpan);
        if (result.IsFailure()) return result.Miss();

        // then the entry descriptors
        Span<byte> entrySpan = stackalloc byte[EntrySize];
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            var mzpEntry = new MzpEntry(entry.NewSize, entry.NewOffset - fileDataOffset);
            MemoryMarshal.Write(entrySpan, ref mzpEntry);

            result = storage.Write(HeaderSize + (EntrySize * i), hdrSpan);
            if (result.IsFailure()) return result.Miss();
        }

        // and we finally clean up all of the entries, updating them with new COW spans into the underlying file
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            var cowStor = entry.CowStorage;
            var cow = entry.Cow;
            entry.CowStorage = null;
            entry.Cow = null;
            cowStor?.Dispose();
            cow?.Dispose();

            entry.Size = entry.NewSize;
            entry.Offset = entry.NewOffset;
            // GetStorageForEntry reinitializes COW and updates fw
            _ = GetStorageForEntry(ref entry);
        }

        return Result.Success;
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
                var result = storage.Read(readHead, span);
                if (result.IsFailure()) return result.Miss();
                result = storage.Write(writeHead, span);
                if (result.IsFailure()) return result.Miss();

                readSize = BlockSize;
                readHead += BlockSize * direction;
                writeHead += BlockSize * direction;
            }
            while (readHead >= fromOffs && readHead < fromOffs + length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return Result.Success;
    }

    private sealed class MzpDirectory : IDirectory
    {
        private readonly MzpFileSystem fs;
        private readonly OpenDirectoryMode mode;

        public MzpDirectory(MzpFileSystem fs, OpenDirectoryMode mode)
        {
            this.fs = fs;
            this.mode = mode;
        }

        protected override Result DoGetEntryCount(out long entryCount)
        {
            if (!mode.Has(OpenDirectoryMode.File))
            {
                entryCount = 0;
                return Result.Success;
            }

            entryCount = fs.entries.LongLength;
            return Result.Success;
        }

        private int baseIdx = 0;

        protected override Result DoRead(out long entriesRead, Span<DirectoryEntry> entryBuffer)
        {
            var i = 0;
            for (; baseIdx + i < fs.entries.LongLength && i < entryBuffer.Length; i++)
            {
                ref var entry = ref entryBuffer[i];
                entry.Attributes = NxFileAttributes.None;
                entry.Type = DirectoryEntryType.File;

                entry.Size = fs.entries[i].Size;

                var nameSpan = entry.Name.Items;
                nameSpan.Clear();

                var name = (baseIdx + i).ToString("x16");
                _ = Encoding.UTF8.GetBytes(name, nameSpan);
            }
            entriesRead = i;
            baseIdx += i;
            return Result.Success;
        }
    }

    protected override Result DoOpenDirectory(ref UniqueRef<IDirectory> outDirectory, in Path path, OpenDirectoryMode mode)
    {
        var result = Utils.Normalize(path, out var copy);
        if (result.IsFailure()) return result.Miss();

        var pathStr = copy.AsSpan();
        if (pathStr.Length is not 1 || pathStr[0] is not (byte)'/')
        {
            return ResultFs.FileNotFound.Value;
        }

        outDirectory.Get = new MzpDirectory(this, mode);
        return Result.Success;
    }

    protected override Result DoGetEntryType(out DirectoryEntryType entryType, in Path path)
    {
        Unsafe.SkipInit(out entryType);

        var result = GetPathIndex(path, out var index);
        if (result.IsFailure()) return result.Miss();

        if (index < 0 || index >= entries.Length)
        {
            // bad index
            return ResultFs.FileNotFound.Value;
        }

        entryType = DirectoryEntryType.File;
        return Result.Success;
    }

    protected override Result DoCreateDirectory(in Path path) => throw new NotImplementedException();
    protected override Result DoCreateFile(in Path path, long size, CreateFileOptions option) => throw new NotImplementedException();
    protected override Result DoDeleteDirectory(in Path path) => throw new NotImplementedException();
    protected override Result DoDeleteDirectoryRecursively(in Path path) => throw new NotImplementedException();
    protected override Result DoDeleteFile(in Path path) => throw new NotImplementedException();
    protected override Result DoRenameDirectory(in Path currentPath, in Path newPath) => throw new NotImplementedException();
    protected override Result DoRenameFile(in Path currentPath, in Path newPath) => throw new NotImplementedException();

    protected override Result DoCleanDirectoryRecursively(in Path path) => ResultFs.NotImplemented.Value;
    protected override Result DoCommit() => ResultFs.NotImplemented.Value;
}
