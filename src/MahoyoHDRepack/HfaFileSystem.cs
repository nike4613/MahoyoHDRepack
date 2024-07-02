using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Kernel;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack
{
    // .hfa filesystem (mahoyo steam)
    internal class HfaFileSystem : CopyOnWriteFileSystem
    {
        private const int HeaderSize = 16;
        private const int EntrySize = 0x80;

        private static ReadOnlySpan<byte> ExpectMagic => FileScanner.Hfa;

        [StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
        private struct HfaHeader
        {
            public unsafe ReadOnlySpan<byte> Magic => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<HfaHeader, byte>(ref this), 12);
            [FieldOffset(12)]
            public readonly LEUInt32 EntryCount;

            public HfaHeader(uint entryCount)
            {
                FileScanner.Hfa.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<HfaHeader, byte>(ref this), 12));
                EntryCount.Value = entryCount;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = EntrySize)]
        private struct HfaEntry
        {
            public unsafe Span<byte> FileNameBuffer => MemoryMarshal.CreateSpan(ref Unsafe.As<HfaEntry, byte>(ref this), 0x60);
            [FieldOffset(0x60)]
            public LEUInt32 Offset;
            [FieldOffset(0x64)]
            public LEUInt32 Length;

            public ReadOnlySpan<byte> FileName => FileNameBuffer.Slice(0, FileNameBuffer.IndexOf((byte)0) is { } i && i < 0 ? 0x60 : i);

            public HfaEntry(ReadOnlySpan<byte> name, uint offset, uint length)
            {
                FileNameBuffer.Clear();
                name.Slice(0, int.Min(name.Length, FileNameBuffer.Length)).CopyTo(FileNameBuffer);
                Offset.Value = offset;
                Length.Value = length;
            }

            internal Entry ToEntry(uint dataOffset) => new(Encoding.UTF8.GetString(FileName), Offset.Value + dataOffset, Length.Value);
        }

        private readonly Entry[] entries;
        private readonly Dictionary<string, int> nameToIdx = new();

        private struct Entry
        {
            public CowEntry CowEntry;
            public string FileName;

            public Entry(string filename, uint offset, uint length)
            {
                FileName = filename;
                CowEntry.Offset = offset;
                CowEntry.Size = length;
            }
        }

        private HfaFileSystem(SharedRef<IStorage> storageRef, IStorage storage, Entry[] entries) : base(storageRef, storage)
        {
            this.entries = entries;

            for (var i = 0; i < entries.Length; i++)
            {
                // some archives have the same file in multiple times...
                while (!nameToIdx.TryAdd(entries[i].FileName, i))
                {
                    Console.WriteLine($"rewriting {entries[i].FileName} at index {i}");
                    entries[i].FileName += "_";
                }
            }
        }

        public static Result Read(ref UniqueRef<IFileSystem> fs, IStorage storage)
            => ReadCore(ref Unsafe.As<UniqueRef<IFileSystem>, UniqueRef<HfaFileSystem>>(ref fs), default, storage);
        public static Result Read(ref UniqueRef<IFileSystem> fs, SharedRef<IStorage> storage)
            => ReadCore(ref Unsafe.As<UniqueRef<IFileSystem>, UniqueRef<HfaFileSystem>>(ref fs), storage, storage.Get);
        public static Result Read(ref UniqueRef<HfaFileSystem> fs, IStorage storage)
            => ReadCore(ref fs, default, storage);
        public static Result Read(ref UniqueRef<HfaFileSystem> fs, SharedRef<IStorage> storage)
            => ReadCore(ref fs, storage, storage.Get);
        private static Result ReadCore(ref UniqueRef<HfaFileSystem> hfaFs, SharedRef<IStorage> storageRef, IStorage storage)
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

            var hdr = MemoryMarshal.Read<HfaHeader>(header);
            if (!hdr.Magic.SequenceEqual(FileScanner.Hfa))
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
                result = ReadEntry(storage, i, out var hfaEntry);
                if (result.IsFailure()) return result.Miss();
                entries[i] = hfaEntry.ToEntry(dataOffset);
            }

            hfaFs.Reset(new HfaFileSystem(storageRef, storage, entries));
            return Result.Success;
        }

        private static Result ReadEntry(IStorage storage, int index, out HfaEntry entry)
        {
            Unsafe.SkipInit(out entry);

            var entryOffset = HeaderSize + (index * EntrySize);
            Span<byte> entryData = stackalloc byte[EntrySize];
            var result = storage.Read(entryOffset, entryData);
            if (result.IsFailure()) return result.Miss();

            entry = MemoryMarshal.Read<HfaEntry>(entryData);
            return Result.Success;
        }

        // files do not seem to need to be aligned
        protected override uint AlignOffset(uint offset) => offset;
        protected override uint GetDataOffset() => (uint)(HeaderSize + (EntrySize * entries.Length));
        protected override ref CowEntry GetEntry(int i) => ref entries[i].CowEntry;
        protected override int GetEntryCount() => entries.Length;
        protected override Result WriteHeader(IStorage storage, uint dataOffset)
        {
            // first we write the HFA header
            Span<byte> hdrSpan = stackalloc byte[HeaderSize];
            var hfaHeader = new HfaHeader((uint)entries.Length);
            MemoryMarshal.Write(hdrSpan, hfaHeader);

            var result = Storage.Write(0, hdrSpan);
            if (result.IsFailure()) return result.Miss();

            // then the entry descriptors
            Span<byte> entrySpan = stackalloc byte[EntrySize];
            for (var i = 0; i < entries.Length; i++)
            {
                ref var entry = ref entries[i];

                // note: this does a bunch of extra copies, but eh
                _ = Encoding.UTF8.GetBytes(entry.FileName, entrySpan);
                var mzpEntry = new HfaEntry(entrySpan, entry.CowEntry.NewSize, entry.CowEntry.NewOffset - dataOffset);
                MemoryMarshal.Write(entrySpan, mzpEntry);

                result = Storage.Write(HeaderSize + (EntrySize * i), entrySpan);
                if (result.IsFailure()) return result.Miss();
            }

            return Result.Success;
        }

        private Result GetPathIndex(in Path path, out int index)
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

            var str = Encoding.UTF8.GetString(pathStr);
            copy.Dispose();
            if (nameToIdx.TryGetValue(str, out index))
            {
                return Result.Success;
            }
            else
            {
                return ResultFs.FileNotFound.Value;
            }
        }

        protected override Result DoGetEntryType(out DirectoryEntryType entryType, in Path path)
        {
            Unsafe.SkipInit(out entryType);

            var result = GetPathIndex(path, out var index);
            if (result.IsFailure()) return result.Miss();
            Helpers.DAssert(index >= 0 && index < entries.Length);

            entryType = DirectoryEntryType.File;
            return Result.Success;
        }

        protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
        {
            var result = GetPathIndex(path, out var index);
            if (result.IsFailure()) return result.Miss();

            ref var entry = ref entries[index];
            var realOffset = entry.CowEntry.Offset;
            var realSize = entry.CowEntry.Size;

            result = IStorage.CheckAccessRange(realOffset, realSize, StorageSize);
            if (result.IsFailure()) return result.Miss();

            outFile.Reset(GetStorageForEntry(ref entry.CowEntry).AsFile(mode));
            return Result.Success;
        }

        private sealed class HfaDirectory : IDirectory
        {
            private readonly HfaFileSystem fs;
            private readonly OpenDirectoryMode mode;

            public HfaDirectory(HfaFileSystem fs, OpenDirectoryMode mode)
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

                    entry.Size = fs.entries[baseIdx + i].CowEntry.Size;

                    var nameSpan = entry.Name.Items;
                    nameSpan.Clear();
                    _ = Encoding.UTF8.GetBytes(fs.entries[baseIdx + i].FileName, nameSpan);
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

            outDirectory.Reset(new HfaDirectory(this, mode));
            return Result.Success;
        }


        protected override Result DoCommit() => ResultFs.NotImplemented.Value;
        protected override Result DoCreateDirectory(in Path path) => ResultFs.NotImplemented.Value;
        protected override Result DoCreateFile(in Path path, long size, CreateFileOptions option) => ResultFs.NotImplemented.Value;
        protected override Result DoDeleteDirectory(in Path path) => ResultFs.NotImplemented.Value;
        protected override Result DoDeleteDirectoryRecursively(in Path path) => ResultFs.NotImplemented.Value;
        protected override Result DoCleanDirectoryRecursively(in Path path) => ResultFs.NotImplemented.Value;
        protected override Result DoDeleteFile(in Path path) => ResultFs.NotImplemented.Value;
        protected override Result DoRenameDirectory(in Path currentPath, in Path newPath) => ResultFs.NotImplemented.Value;
        protected override Result DoRenameFile(in Path currentPath, in Path newPath) => ResultFs.NotImplemented.Value;
    }
}
