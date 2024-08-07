using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack
{
    internal class MrgFileSystem : CopyOnWriteFileSystem
    {
        private struct HedEntry(long Offset, long Size)
        {
            public CowEntry Entry = new() { Size = Size, Offset = Offset };

            public long Offset => Entry.Offset;
            public long Size => Entry.Size;
        }

        [StructLayout(LayoutKind.Sequential, Size = Size)]
        private readonly struct Name : IComparable<Name>, IEquatable<Name>
        {
            private readonly ulong w0;
            private readonly ulong w1;
            private readonly ulong w2;
            private readonly ulong w3;

            public const int Size = sizeof(ulong) * 4;

            public Span<byte> AsSpan()
                => MemoryMarshal.CreateSpan(ref Unsafe.As<Name, byte>(ref Unsafe.AsRef(in this)), Size);

            public ReadOnlySpan<byte> AsU8()
            {
                var span = AsSpan();
                var idx = span.IndexOf("\0"u8);
                return span.Slice(0, idx);
            }

            public int CompareTo(Name other)
            {
                var res = Utils.BEToHost(w0).CompareTo(Utils.BEToHost(other.w0));
                if (res is not 0) return res;
                res = Utils.BEToHost(w1).CompareTo(Utils.BEToHost(other.w1));
                if (res is not 0) return res;
                res = Utils.BEToHost(w2).CompareTo(Utils.BEToHost(other.w2));
                if (res is not 0) return res;
                return Utils.BEToHost(w3).CompareTo(Utils.BEToHost(other.w3));
            }

            public static Name Create(ReadOnlySpan<byte> name)
            {
                Name n = default;
                Create(ref n, name);
                return n;
            }

            public static void Create(ref Name n, ReadOnlySpan<byte> name)
            {
                var span = n.AsSpan();
                span[30] = 0x0d;
                span[31] = 0x0a;
                name.CopyTo(span);
            }

            public bool Equals(Name other) => AsU8().SequenceEqual(other.AsU8());
            public override bool Equals([NotNullWhen(true)] object? obj) => obj is Name n && Equals(n);
            public override int GetHashCode() => HashCode.Combine(w0, w1, w2, w3);
        }

        private readonly SharedRef<IFileSystem> sharedFsRef;
        private readonly IFile mrg;
        private readonly IFile hed;
        private readonly IFile? nam;
        private readonly Memory<HedEntry> files;
        private readonly ReadOnlyMemory<Name> names;

        private MrgFileSystem(SharedRef<IFileSystem> sharedFsRef,
            IFile mrg, IFile hed, IFile? nam,
            SharedRef<IStorage> storageRef, IStorage storage,
            Memory<HedEntry> files, ReadOnlyMemory<Name> names)
            : base(storageRef, storage)
        {
            this.sharedFsRef = sharedFsRef;
            this.mrg = mrg;
            this.hed = hed;
            this.nam = nam;
            this.files = files;
            this.names = names;
        }

        public override void Dispose()
        {
            mrg.Dispose();
            hed.Dispose();
            nam?.Dispose();
            sharedFsRef.Destroy();
        }

        public static Result Read(IFileSystem fs, in Path path, out MrgFileSystem? mrgFs)
        {
            return ReadCore(fs, default, path, out mrgFs);
        }

        public static Result Read(SharedRef<IFileSystem> fs, in Path path, out MrgFileSystem? mrgFs)
        {
            return ReadCore(fs.Get, fs, path, out mrgFs);
        }

        private const int HedEntrySize = 8;
        private const long SectorSize = 0x800;

        private static Result ReadCore(IFileSystem fs, SharedRef<IFileSystem> fsSharedRef, in Path path, out MrgFileSystem? mrgFs)
        {
            mrgFs = null;

            var pathBase = path.GetString()[0..path.GetLength()];
            Span<byte> pathbuf = stackalloc byte[pathBase.Length + 4];
            pathBase.CopyTo(pathbuf);

            using scoped var curPath = new Path();

            // load MRG file
            ".mrg"u8.CopyTo(pathbuf.Slice(pathBase.Length));
            var result = curPath.Initialize(pathbuf);
            if (result.IsFailure()) return result.Miss();

            using var uniqMrgFile = new UniqueRef<IFile>();
            result = fs.OpenFile(ref uniqMrgFile.Ref, in curPath, OpenMode.Read);
            if (result.IsFailure()) return result.Miss();

            // load the HED file
            ".hed"u8.CopyTo(pathbuf.Slice(pathBase.Length));
            result = curPath.Initialize(pathbuf);
            if (result.IsFailure()) return result.Miss();

            using var uniqHedFile = new UniqueRef<IFile>();
            result = fs.OpenFile(ref uniqHedFile.Ref, in curPath, OpenMode.Read);
            if (result.IsFailure()) return result.Miss();

            // try to load the NAM file, if present
            ".nam"u8.CopyTo(pathbuf.Slice(pathBase.Length));
            result = curPath.Initialize(pathbuf);
            if (result.IsFailure()) return result.Miss();

            using var uniqNamFile = new UniqueRef<IFile?>();
            result = fs.OpenFile(ref uniqNamFile.Ref, in curPath, OpenMode.Read);
            if (result.IsFailure()) uniqNamFile.Reset(null);

            Span<byte> readBuf = stackalloc byte[Math.Max(Name.Size, HedEntrySize)];

            // now lets read the the HED file
            Memory<HedEntry> hedEntries;
            {
                result = uniqHedFile.Get.GetSize(out var length);
                if (result.IsFailure()) return result.Miss();
                if (length % HedEntrySize != 0)
                {
                    // HED file is probably wrong
                    return ResultFs.InvalidFileSize.Value;
                }

                var numEntries = length / HedEntrySize;

                var entries = new HedEntry[numEntries];
                var i = 0;
                for (; i < numEntries; i++)
                {
                    result = uniqHedFile.Get.Read(out var bytesRead, i * HedEntrySize, readBuf);
                    if (result.IsFailure()) return result.Miss();
                    if (bytesRead < HedEntrySize)
                    {
                        // could not read hed
                        return new Result(256, 10);
                    }

                    var offset = (long)MemoryMarshal.Read<LEUInt32>(readBuf).Value;
                    if (offset == uint.MaxValue)
                    {
                        // end of file
                        break;
                    }

                    offset *= SectorSize;

                    var size = SectorSize * (long)MemoryMarshal.Read<LEUInt16>(readBuf[4..]).Value;

                    // note: the last u16 is the size of the file, uncompressed

                    entries[i] = new(offset, size);
                }

                hedEntries = entries.AsMemory().Slice(0, i);
            }

            // and lets read the NAM file
            Memory<Name> names = default;
            if (uniqNamFile.Get is { } namFile)
            {
                result = namFile.GetSize(out var length);
                if (result.IsFailure()) return result.Miss();
                if (length % Name.Size != 0)
                {
                    // NAM file is the wrong size
                    return ResultFs.InvalidFileSize.Value;
                }

                var numEntries = length / Name.Size;

                if (numEntries < hedEntries.Length)
                {
                    // too few NAM entries
                    return ResultFs.InvalidFileSize.Value;
                }

                var entries = new Name[hedEntries.Length];
                for (var i = 0; i < hedEntries.Length; i++)
                {
                    result = namFile.Read(out var bytesRead, i * Name.Size, readBuf);
                    if (result.IsFailure()) return result.Miss();

                    entries[i] = MemoryMarshal.Read<Name>(readBuf);
                }

                names = entries;

                // sort them by name so we can look them up fairly quickly
                // this breaks writeout, because we don't (currently) rewrite the NAM file
                //names.Span.Sort(hedEntries.Span);
            }

            var mrgFile = uniqMrgFile.Release();

            mrgFs = new(
                SharedRef<IFileSystem>.CreateCopy(fsSharedRef),
                mrgFile, uniqHedFile.Release(), uniqNamFile.Release(),
                default, mrgFile.AsStorage(),
                hedEntries, names);
            return Result.Success;
        }

        protected override int GetEntryCount() => files.Length;
        protected override long GetDataOffset() => 0;
        protected override long AlignOffset(long offset) => (offset + (SectorSize - 1)) & ~(SectorSize - 1);
        protected override ref CowEntry GetEntry(int i) => ref files.Span[i].Entry;
        protected override Result WriteHeader(IStorage storage, long dataOffset)
        {
            // note: while the main file doesn't have a header, we still want to write the hed

            // also note: we don't support adding or removing files, so the HED length 
            Result result;
            var entrySpan = files.Span;
            Span<byte> data = stackalloc byte[HedEntrySize];
            for (var i = 0; i < entrySpan.Length; i++)
            {
                var hedOffset = HedEntrySize * i;

                ref var entry = ref GetEntry(i);

                var offset = entry.NewOffset;
                var size = entry.NewSize;

                var uncompressedSize = FileScanner.GetUncompressedSize(GetStorageForEntry(ref entry).AsFile(OpenMode.Read));

                var offsetInSectors = offset / SectorSize;
                var sizeInSectors = (size + (SectorSize - 1)) / SectorSize;
                var uncompressedSizeInSectors = (uncompressedSize + (SectorSize - 1)) / SectorSize;
                MemoryMarshal.Write<LEUInt32>(data, (uint)offsetInSectors);
                MemoryMarshal.Write<LEUInt16>(data[4..], (ushort)sizeInSectors);
                MemoryMarshal.Write<LEUInt16>(data[6..], (ushort)uncompressedSizeInSectors);

                result = hed.Write(hedOffset, data);
                if (result.IsFailure()) return result.Miss();
            }

            data.Fill(0xff);
            result = hed.Write(HedEntrySize * entrySpan.Length, data);
            if (result.IsFailure()) return result.Miss();
            result = hed.Write(HedEntrySize * (entrySpan.Length + 1), data);
            if (result.IsFailure()) return result.Miss();

            return Result.Success; // note: the main file does not have a header
        }

        private static Result PathToName(ReadOnlySpan<byte> pathStr, out Name name)
        {
            Unsafe.SkipInit(out name);

            if (pathStr.Length > Name.Size)
            {
                // path too long
                return ResultFs.InvalidPath.Value;
            }

            Name.Create(ref name, pathStr);
            return Result.Success;
        }


        private Result GetIndex(in Path path, out int res)
        {
            Unsafe.SkipInit(out res);
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

            if (names.IsEmpty)
            {
                // no names, parse an integer
                result = Utils.ParseHexFromU8(pathStr, out res);
                if (result.IsFailure()) return result.Miss();

                if (res < 0 || res >= files.Length)
                {
                    // invalid name
                    return ResultFs.FileNotFound.Value;
                }

                return Result.Success;
            }
            else
            {
                // turn it into name
                result = PathToName(pathStr, out var name);
                if (result.IsFailure()) return result.Miss();

                var idx = names.Span.IndexOf(name);
                if (idx < 0)
                {
                    // invalid name
                    return ResultFs.FileNotFound.Value;
                }

                res = idx;
                return Result.Success;
            }
        }

        protected override Result DoGetEntryType(out DirectoryEntryType entryType, in Path path)
        {
            entryType = DirectoryEntryType.File;
            return GetIndex(path, out _);
        }

        protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
        {
            var result = GetIndex(path, out var idx);
            if (result.IsFailure()) return result.Miss();

            outFile.Reset(GetStorageForEntry(ref GetEntry(idx)).AsFile(mode));
            return Result.Success;
        }

        private sealed class MrgDirectory : IDirectory
        {
            private readonly MrgFileSystem fs;
            private readonly OpenDirectoryMode mode;

            public MrgDirectory(MrgFileSystem fs, OpenDirectoryMode mode)
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

                entryCount = fs.files.Length;
                return Result.Success;
            }

            private int baseIdx = 0;

            protected override Result DoRead(out long entriesRead, Span<DirectoryEntry> entryBuffer)
            {
                var i = 0;
                for (; baseIdx + i < fs.files.Length && i < entryBuffer.Length; i++)
                {
                    ref var entry = ref entryBuffer[i];
                    entry.Attributes = NxFileAttributes.None;
                    entry.Type = DirectoryEntryType.File;
                    entry.Size = fs.files.Span[baseIdx + i].Size;

                    var nameSpan = entry.Name.Items;
                    nameSpan.Clear();

                    if (fs.names.IsEmpty)
                    {
                        var name = (baseIdx + i).ToString("x16", CultureInfo.InvariantCulture);
                        _ = Encoding.UTF8.GetBytes(name, nameSpan);
                    }
                    else
                    {
                        fs.names.Span[baseIdx + i].AsU8().CopyTo(nameSpan);
                    }
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

            outDirectory.Reset(new MrgDirectory(this, mode));
            return Result.Success;
        }


        protected override Result DoCleanDirectoryRecursively(in Path path) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoCommit() => ResultFs.UnsupportedOperation.Value;
        protected override Result DoCreateDirectory(in Path path) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoCreateFile(in Path path, long size, CreateFileOptions option) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoDeleteDirectory(in Path path) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoDeleteDirectoryRecursively(in Path path) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoDeleteFile(in Path path) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoRenameDirectory(in Path currentPath, in Path newPath) => ResultFs.UnsupportedOperation.Value;
        protected override Result DoRenameFile(in Path currentPath, in Path newPath) => ResultFs.UnsupportedOperation.Value;
    }
}
