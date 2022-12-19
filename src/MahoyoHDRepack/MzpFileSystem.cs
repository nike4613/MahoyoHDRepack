using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;

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

        public uint Size => ((SizeSectors * SectorSize) & ~0xffffu) | SizeBytes;
        public uint Offset => (SectorOffset * SectorSize) + ByteOffset;
    }

    private readonly IStorage storage;
    private readonly long storageSize;
    private readonly uint numEntries;

    private MzpFileSystem(IStorage storage, long storageSize, uint numEntries)
    {
        this.storage = storage;
        this.storageSize = storageSize;
        this.numEntries = numEntries;
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

        var entries = hdr.EntryCount.Value;

        // full file must be at least EntrySize * entries + HeaderSize bytes long
        if (size < (EntrySize * entries) + HeaderSize)
        {
            // file is toko short to be valid
            return ResultFs.InvalidFileSize.Value;
        }

        mzpFs.Get = new MzpFileSystem(storage, size, entries);
        return Result.Success;
    }

    private Result ReadEntry(int index, out MzpEntry entry)
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

    public Result OpenFile(ref UniqueRef<IFile> outFile, int index, OpenMode mode)
    {
        if (mode is not OpenMode.Read)
        {
            return ResultFs.InvalidOperationForOpenMode.Value;
        }

        if (index < 0 || index >= numEntries)
        {
            // bad index
            return ResultFs.FileNotFound.Value;
        }

        // read the entry corresponding to the file
        var result = ReadEntry(index, out var entry);
        if (result.IsFailure()) return result.Miss();
        var realDataOffset = HeaderSize + (numEntries * EntrySize) + entry.Offset;
        var realDataLength = entry.Size;

        result = IStorage.CheckAccessRange(realDataOffset, realDataLength, storageSize);
        if (result.IsFailure()) return result.Miss();

        outFile.Get = FileScanner.TryGetDecompressedFile(new PartialFile(storage, realDataOffset, realDataLength));
        return Result.Success;
    }

    protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
    {
        var result = GetPathIndex(path, out var index);
        if (result.IsFailure()) return result.Miss();
        return OpenFile(ref outFile, index, mode);
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

            entryCount = fs.numEntries;
            return Result.Success;
        }

        private int baseIdx = 0;

        protected override Result DoRead(out long entriesRead, Span<DirectoryEntry> entryBuffer)
        {
            Unsafe.SkipInit(out entriesRead);
            var i = 0;
            for (; baseIdx + i < fs.numEntries && i < entryBuffer.Length; i++)
            {
                ref var entry = ref entryBuffer[i];
                entry.Attributes = NxFileAttributes.None;
                entry.Type = DirectoryEntryType.File;

                var result = fs.ReadEntry(baseIdx + i, out var mzpEntry);
                if (result.IsFailure()) return result.Miss();

                entry.Size = mzpEntry.Size;

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

        if (index < 0 || index >= numEntries)
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
