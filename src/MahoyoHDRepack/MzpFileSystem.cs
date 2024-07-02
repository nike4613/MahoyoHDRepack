using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack;

public sealed class MzpFileSystem : CopyOnWriteFileSystem
{
    private const uint SectorSize = 0x800;
    private const int HeaderSize = 8;
    private const int EntrySize = 8;

    [StructLayout(LayoutKind.Explicit, Pack = 2, Size = HeaderSize)]
    private struct MzpHeader
    {
        public unsafe ReadOnlySpan<byte> Magic => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<MzpHeader, byte>(ref this), 6);
        [FieldOffset(6)]
        public readonly LEUInt16 EntryCount;

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
        public readonly LEUInt16 SectorOffset;
        [FieldOffset(2)]
        public readonly LEUInt16 ByteOffset;
        [FieldOffset(4)]
        public readonly LEUInt16 SizeSectors;
        [FieldOffset(6)]
        public readonly LEUInt16 SizeBytes;

        public MzpEntry(uint size, uint offset)
        {
            var (sectors, bytes) = Math.DivRem(offset, SectorSize);
            bytes |= (sectors >> 4) & 0xf000u;
            SectorOffset.Value = (ushort)sectors;
            ByteOffset.Value = (ushort)bytes;

            SizeSectors.Value = (ushort)(((size & ~0xffffu) / SectorSize) + 2u);
            SizeBytes.Value = (ushort)(size & 0xffffu);
        }

        public uint Size => (uint)((((SizeSectors - 1) * SectorSize) & ~0xffffu) + SizeBytes.Value);
        public uint Offset => ((SectorOffset + ((ByteOffset & 0xf000u) * 16)) * SectorSize) + (ByteOffset & 0xfffu);

        public CowEntry ToEntry(uint baseOffs) => new()
        {
            Size = Size,
            Offset = Offset + baseOffs
        };
    }

    private readonly CowEntry[] entries;

    private MzpFileSystem(SharedRef<IStorage> storageRef, IStorage storage, CowEntry[] entries) : base(storageRef, storage)
    {
        this.entries = entries;
    }

    public static Result Read(ref UniqueRef<IFileSystem> fs, IStorage storage)
        => ReadCore(ref Unsafe.As<UniqueRef<IFileSystem>, UniqueRef<MzpFileSystem>>(ref fs), default, storage);
    public static Result Read(ref UniqueRef<IFileSystem> fs, SharedRef<IStorage> storage)
        => ReadCore(ref Unsafe.As<UniqueRef<IFileSystem>, UniqueRef<MzpFileSystem>>(ref fs), storage, storage.Get);
    public static Result Read(ref UniqueRef<MzpFileSystem> fs, IStorage storage)
        => ReadCore(ref fs, default, storage);
    public static Result Read(ref UniqueRef<MzpFileSystem> fs, SharedRef<IStorage> storage)
        => ReadCore(ref fs, storage, storage.Get);
    private static Result ReadCore(ref UniqueRef<MzpFileSystem> mzpFs, SharedRef<IStorage> storageRef, IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

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
        var entries = new CowEntry[numEntries];
        for (var i = 0; i < numEntries; i++)
        {
            result = ReadEntry(storage, i, out var mzpEntry);
            if (result.IsFailure()) return result.Miss();
            entries[i] = mzpEntry.ToEntry((uint)dataOffset);
        }

        mzpFs.Reset(new MzpFileSystem(storageRef, storage, entries));
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
            copy.Dispose();
            // path has directories
            return ResultFs.InvalidPath.Value;
        }

        result = Utils.ParseHexFromU8(pathStr, out index);
        copy.Dispose();
        if (result.IsFailure()) return result.Miss();

        return Result.Success;
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

        var result = IStorage.CheckAccessRange(realDataOffset, realDataLength, StorageSize);
        if (result.IsFailure()) return result.Miss();

        outFile.Reset(GetStorageForEntry(ref entry).AsFile(mode));
        return Result.Success;
    }

    protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
    {
        var result = GetPathIndex(path, out var index);
        if (result.IsFailure()) return result.Miss();
        return OpenFile(ref outFile, index, mode);
    }

    protected override int GetEntryCount() => entries.Length;
    protected override ref CowEntry GetEntry(int i) => ref entries[i];
    protected override uint GetDataOffset() => (uint)((entries.Length * EntrySize) + HeaderSize);
    protected override uint AlignOffset(uint offset) => (offset + 15u) & ~0xfu; // each file is aligned to 16 bytes

    protected override Result WriteHeader(IStorage storage, uint dataOffset)
    {
        // first we write the MZP header
        Span<byte> hdrSpan = stackalloc byte[HeaderSize];
        var mzpHeader = new MzpHeader((ushort)entries.Length);
        MemoryMarshal.Write(hdrSpan, mzpHeader);

        var result = Storage.Write(0, hdrSpan);
        if (result.IsFailure()) return result.Miss();

        // then the entry descriptors
        Span<byte> entrySpan = stackalloc byte[EntrySize];
        for (var i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            var mzpEntry = new MzpEntry(entry.NewSize, entry.NewOffset - dataOffset);
            MemoryMarshal.Write(entrySpan, mzpEntry);

            result = Storage.Write(HeaderSize + (EntrySize * i), entrySpan);
            if (result.IsFailure()) return result.Miss();
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

        private int baseIdx;

        protected override Result DoRead(out long entriesRead, Span<DirectoryEntry> entryBuffer)
        {
            var i = 0;
            for (; baseIdx + i < fs.entries.LongLength && i < entryBuffer.Length; i++)
            {
                ref var entry = ref entryBuffer[i];
                entry.Attributes = NxFileAttributes.None;
                entry.Type = DirectoryEntryType.File;

                entry.Size = fs.entries[baseIdx + i].Size;

                var nameSpan = entry.Name.Items;
                nameSpan.Clear();

                var name = (baseIdx + i).ToString("x16", CultureInfo.InvariantCulture);
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
        copy.Dispose();
        if (pathStr.Length is not 1 || pathStr[0] is not (byte)'/')
        {
            return ResultFs.FileNotFound.Value;
        }

        outDirectory.Reset(new MzpDirectory(this, mode));
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
