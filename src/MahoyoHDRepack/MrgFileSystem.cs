using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack
{
    internal class MrgFileSystem : IFileSystem
    {
        private readonly record struct HedEntry(long Offset, long Size);

        [StructLayout(LayoutKind.Sequential, Size = Size)]
        private readonly struct Name : IComparable<Name>
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
                var res = Helpers.BEToHost(w0).CompareTo(Helpers.BEToHost(other.w0));
                if (res is not 0) return res;
                res = Helpers.BEToHost(w1).CompareTo(Helpers.BEToHost(other.w1));
                if (res is not 0) return res;
                res = Helpers.BEToHost(w2).CompareTo(Helpers.BEToHost(other.w2));
                if (res is not 0) return res;
                return Helpers.BEToHost(w3).CompareTo(Helpers.BEToHost(other.w3));
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
        }

        private readonly IFile mrg;
        private readonly ReadOnlyMemory<HedEntry> files;
        private readonly ReadOnlyMemory<Name> names;

        private MrgFileSystem(IFile mrg, ReadOnlyMemory<HedEntry> files, ReadOnlyMemory<Name> names)
        {
            this.mrg = mrg;
            this.files = files;
            this.names = names;
        }

        public override void Dispose() => mrg.Dispose();

        public static Result Read(IFileSystem fs, in Path path, out MrgFileSystem? mrgFs)
        {
            mrgFs = null;

            var pathBase = path.GetString()[0..path.GetLength()];
            Span<byte> pathbuf = stackalloc byte[pathBase.Length + 4];
            pathBase.CopyTo(pathbuf);

            scoped var curPath = new Path();

            // load MRG file
            ".mrg"u8.CopyTo(pathbuf.Slice(pathBase.Length));
            var result = curPath.Initialize(pathbuf);
            if (result.IsFailure()) return result.Miss();

            using var uniqMrgFile = new UniqueRef<IFile>();
            result = fs.OpenFile(ref uniqMrgFile.Ref(), in curPath, OpenMode.Read);
            if (result.IsFailure()) return result.Miss();

            // load the HED file
            ".hed"u8.CopyTo(pathbuf.Slice(pathBase.Length));
            result = curPath.Initialize(pathbuf);
            if (result.IsFailure()) return result.Miss();

            using var uniqHedFile = new UniqueRef<IFile>();
            result = fs.OpenFile(ref uniqHedFile.Ref(), in curPath, OpenMode.Read);
            if (result.IsFailure()) return result.Miss();

            // try to load the NAM file, if present
            ".nam"u8.CopyTo(pathbuf.Slice(pathBase.Length));
            result = curPath.Initialize(pathbuf);
            if (result.IsFailure()) return result.Miss();

            using var uniqNamFile = new UniqueRef<IFile?>();
            result = fs.OpenFile(ref uniqNamFile.Ref(), in curPath, OpenMode.Read);
            if (result.IsFailure()) uniqNamFile.Get = null;

            const int HedEntrySize = 8;
            Span<byte> readBuf = stackalloc byte[Math.Max(Name.Size, HedEntrySize)];

            // now lets read the the HED file
            Memory<HedEntry> hedEntries;
            {
                const uint SectorSize = 0x800;

                result = uniqHedFile.Get.GetSize(out var length);
                if (result.IsFailure()) return result.Miss();
                if (length % HedEntrySize != 0)
                {
                    // HED file is probably wrong
                    return new Result(256, 0);
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

                    var offset = Helpers.LEToHost(MemoryMarshal.Read<uint>(readBuf));
                    if (offset == uint.MaxValue)
                    {
                        // end of file
                        break;
                    }

                    offset *= SectorSize;

                    var size = SectorSize * Helpers.LEToHost(MemoryMarshal.Read<ushort>(readBuf[4..]));

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
                    return new Result(256, 1);
                }

                var numEntries = length / Name.Size;

                if (numEntries < hedEntries.Length)
                {
                    // too few NAM entries
                    return new Result(256, 2);
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
                names.Span.Sort(hedEntries.Span);
            }

            mrgFs = new(uniqMrgFile.Release(), hedEntries, names);
            return Result.Success;
        }

        private static Result PathToName(ReadOnlySpan<byte> pathStr, out Name name)
        {
            Unsafe.SkipInit(out name);

            if (pathStr.Length > Name.Size)
            {
                // path too long
                return new Result(256, 4);
            }

            Name.Create(ref name, pathStr);
            return Result.Success;
        }

        private static Result ParseU8(ReadOnlySpan<byte> str, out int res)
        {
            res = 0;

            var val = 0;
            for (var i = 0; i < str.Length; i++)
            {
                val *= 10;
                var c = str[i];
                if (c is >= (byte)'0' and <= (byte)'9')
                {
                    val += c - '0';
                }
                else if (c is >= (byte)'a' and <= (byte)'f')
                {
                    val += c - 'a' + 10;
                }
                else if (c is >= (byte)'A' and <= (byte)'A')
                {
                    val += c - 'A' + 10;
                }
                else
                {
                    // invalid character
                    return new Result(256, 5);
                }
            }

            res = val;
            return Result.Success;
        }

        private static Result Normalize(in Path path, out Path copy)
        {
            copy = default;
            var result = copy.Initialize(path);
            if (result.IsFailure()) return result.Miss();

            result = copy.Normalize(default);
            if (result.IsFailure()) return result.Miss();

            return Result.Success;
        }

        private Result GetIndex(in Path path, out int res)
        {
            Unsafe.SkipInit(out res);
            var result = Normalize(path, out var copy);
            if (result.IsFailure()) return result.Miss();

            var pathStr = copy.GetString()[0..copy.GetLength()];
            Helpers.DAssert(pathStr[0] == '/');
            pathStr = pathStr[1..];

            if (pathStr.Contains((byte)'/'))
            {
                // path has directories
                return new Result(256, 3);
            }

            if (names.IsEmpty)
            {
                // no names, parse an integer
                result = ParseU8(pathStr, out res);
                if (result.IsFailure()) return result.Miss();

                if (res < 0 || res >= files.Length)
                {
                    // invalid name
                    return new Result(256, 6);
                }

                return Result.Success;
            }
            else
            {
                // turn it into name
                result = PathToName(pathStr, out var name);
                if (result.IsFailure()) return result.Miss();

                var idx = names.Span.BinarySearch(name);
                if (idx < 0)
                {
                    // invalid name
                    return new Result(256, 6);
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

        private sealed class MrgFile : IFile
        {
            private readonly IFile mrgFile;
            private readonly HedEntry entry;

            public MrgFile(IFile mrg, HedEntry entry)
            {
                mrgFile = mrg;
                this.entry = entry;
            }

            protected override Result DoGetSize(out long size)
            {
                size = entry.Size;
                return Result.Success;
            }

            protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
            {
                Unsafe.SkipInit(out bytesRead);

                if (offset < 0)
                {
                    return new Result(257, 0);
                }

                var endOffs = offset + destination.Length;
                endOffs = Math.Clamp(endOffs, 0, endOffs);
                destination = destination.Slice(0, (int)(endOffs - offset));

                if (destination.Length is 0)
                {
                    return new Result(257, 0);
                }

                return mrgFile.Read(out bytesRead, entry.Offset + offset, destination, in option);
            }

            protected override Result DoFlush() => throw new NotSupportedException();
            protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer) => throw new NotSupportedException();
            protected override Result DoSetSize(long size) => throw new NotSupportedException();
            protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option) => throw new NotSupportedException();
        }

        // TODO: auto-decompress NXCX and NXGX

        protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
        {
            if (mode is not OpenMode.Read)
            {
                return new Result(256, 7);
            }

            var result = GetIndex(path, out var idx);
            if (result.IsFailure()) return result.Miss();

            var entry = files.Span[idx];
            outFile.Get = new MrgFile(mrg, entry);
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
                    nameSpan.Fill(0);

                    if (fs.names.IsEmpty)
                    {
                        var name = (baseIdx + i).ToString("x16");
                        var nameBytes = Encoding.UTF8.GetBytes(name);
                        nameBytes.AsSpan().CopyTo(nameSpan);
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
            var result = Normalize(path, out var copy);
            if (result.IsFailure()) return result.Miss();

            var pathStr = copy.GetString()[0..copy.GetLength()];
            if (pathStr.Length is not 1 || pathStr[0] is not (byte)'/')
            {
                return new Result(256, 7);
            }

            outDirectory.Get = new MrgDirectory(this, mode);
            return Result.Success;
        }


        protected override Result DoCleanDirectoryRecursively(in Path path) => throw new NotSupportedException();
        protected override Result DoCommit() => throw new NotSupportedException();
        protected override Result DoCreateDirectory(in Path path) => throw new NotSupportedException();
        protected override Result DoCreateFile(in Path path, long size, CreateFileOptions option) => throw new NotSupportedException();
        protected override Result DoDeleteDirectory(in Path path) => throw new NotSupportedException();
        protected override Result DoDeleteDirectoryRecursively(in Path path) => throw new NotSupportedException();
        protected override Result DoDeleteFile(in Path path) => throw new NotSupportedException();
        protected override Result DoRenameDirectory(in Path currentPath, in Path newPath) => throw new NotSupportedException();
        protected override Result DoRenameFile(in Path currentPath, in Path newPath) => throw new NotSupportedException();
    }
}
