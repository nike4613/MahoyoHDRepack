using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;

namespace MahoyoHDRepack
{
    public sealed class WriteOverlayFileSystem : IFileSystem
    {
        private SharedRef<IFileSystem> readFs;
        private SharedRef<IFileSystem> writeFs;

        public WriteOverlayFileSystem(in SharedRef<IFileSystem> readFs, in SharedRef<IFileSystem> writeFs)
        {
            this.readFs = SharedRef<IFileSystem>.CreateCopy(readFs);
            this.writeFs = SharedRef<IFileSystem>.CreateCopy(writeFs);
        }

        public override void Dispose()
        {
            readFs.Destroy();
            writeFs.Destroy();
        }

        protected override Result DoCleanDirectoryRecursively(in Path path) => writeFs.Get.CleanDirectoryRecursively(path);
        protected override Result DoCommit() => writeFs.Get.Commit();
        protected override Result DoFlush() => writeFs.Get.Flush();

        private Result RecursiveCopyParentDirectory(in Path path)
        {
            Path copy = default;
            var result = copy.Initialize(path);
            if (result.IsFailure()) return result.Miss();

            // trim the last element off of path
            result = copy.RemoveChild();
            if (result.IsFailure()) return result.Miss();

            return RecursiveCopyDirectory(copy);
        }

        private Result RecursiveCopyDirectory(in Path path)
        {
            // we only want to copy the dirs if readFs has the dir
            var result = readFs.Get.GetEntryType(out _, path);
            if (result.IsFailure())
            {
                // path does not exist in read fs, don't attempt to copy to write fs
                return Result.Success; // we didn't fail
            }

            // the dir exists in the readFs, now lets create all of its parents in writeFs as needed
            return DoCopyDirs(path);
        }

        private Result DoCopyDirs(in Path path)
        {
            // if path exists in writeFs, we're done
            var result = writeFs.Get.GetEntryType(out _, path);
            if (result.IsSuccess()) return Result.Success;

            // otherwise we want to ensure parents are created, then create the child
            Path copy = default;
            result = copy.Initialize(path);
            if (result.IsFailure()) return result.Miss();

            // trim the last element off of path
            result = copy.RemoveChild();
            if (result.IsFailure()) return result.Miss();

            // create parents as needed
            result = DoCopyDirs(copy);
            if (result.IsFailure()) return result.Miss();

            // create our actual path
            result = writeFs.Get.CreateDirectory(path);
            return result;
        }

        protected override Result DoCreateDirectory(in Path path)
        {
            var result = RecursiveCopyParentDirectory(path);
            if (result.IsFailure()) return result.Miss();
            return writeFs.Get.CreateDirectory(path);
        }

        protected override Result DoCreateFile(in Path path, long size, CreateFileOptions option)
        {
            var result = RecursiveCopyParentDirectory(path);
            if (result.IsFailure()) return result.Miss();
            return writeFs.Get.CreateFile(path, size, option);
        }

        protected override Result DoGetEntryType(out DirectoryEntryType entryType, in Path path)
        {
            // always check writeFs first
            var result = writeFs.Get.GetEntryType(out entryType, path);
            if (result.IsSuccess()) return result;

            // then, if writeFs failed, fall back to readFs
            return readFs.Get.GetEntryType(out entryType, path);
        }

        // TODO: implement OpenDirectory
        protected override Result DoOpenDirectory(ref UniqueRef<IDirectory> outDirectory, in Path path, OpenDirectoryMode mode) => ResultFs.NotImplemented.Value;

        private sealed class WriteOverlayFile : IFile
        {
            private readonly Path.Stored path;
            private readonly WriteOverlayFileSystem fs;
            private readonly OpenMode mode;
            private UniqueRef<IFile> readFile;
            private UniqueRef<IFile> writeFile;

            public WriteOverlayFile(Path.Stored path, WriteOverlayFileSystem fs, OpenMode mode, ref UniqueRef<IFile> read, ref UniqueRef<IFile> write)
            {
                this.path = path;
                this.fs = fs;
                this.mode = mode;
                readFile = UniqueRef<IFile>.Create(ref read);
                writeFile = UniqueRef<IFile>.Create(ref write);
            }

            public override void Dispose()
            {
                path.Dispose();
                readFile.Destroy();
                writeFile.Destroy();
            }

            // if writeFile doesn't exist, that means that we haven't made any changes
            protected override Result DoFlush() => writeFile.Get?.Flush() ?? Result.Success;

            protected override Result DoGetSize(out long size)
            {
                // if we have the write file, get its size
                if (writeFile.HasValue)
                {
                    return writeFile.Get.GetSize(out size);
                }

                // otherwise, get the read file's size
                if (readFile.HasValue)
                {
                    return readFile.Get.GetSize(out size);
                }

                // it *should* never be the case that they're both null, but each can individually for various reasons
                Unsafe.SkipInit(out size);
                return ResultFs.FileNotFound.Value;
            }
            protected override Result DoRead(out long bytesRead, long offset, Span<byte> destination, in ReadOption option)
            {
                // if we have the write file, read from it
                if (writeFile.HasValue)
                {
                    return writeFile.Get.Read(out bytesRead, offset, destination, in option);
                }

                // otherwise, if we have the read file, read from that
                if (readFile.HasValue)
                {
                    return readFile.Get.Read(out bytesRead, offset, destination, in option);
                }

                // it *should* never be the case that they're both null, but each can individually for various reasons
                Unsafe.SkipInit(out bytesRead);
                return ResultFs.FileNotFound.Value;
            }

            private Result CopyFileIfNeeded()
            {
                // if we already have a writeFile, we don't need to do anything
                if (writeFile.HasValue)
                    return Result.Success;

                // if we don't have our read file, we can't copy it
                if (!readFile.HasValue)
                    return ResultFs.FileNotFound.Value;

                var path = this.path.DangerousGetPath();

                // create necessary directories
                var result = fs.RecursiveCopyParentDirectory(path);
                if (result.IsFailure()) return result.Miss();

                // get the read file size
                result = readFile.Get.GetSize(out var fileSize);
                if (result.IsFailure()) return result.Miss();

                // then create the actual file in the write fs
                result = fs.writeFs.Get.CreateFile(path, fileSize);
                if (result.IsFailure()) return result.Miss();

                // once we've created that file, we'll open the file
                result = fs.writeFs.Get.OpenFile(ref writeFile.Ref, path, mode | OpenMode.Write);
                if (result.IsFailure()) return result.Miss();

                // and now we go and copy from the read file to the write file
                const int BufSize = 0x8000;
                var offset = 0L;
                var buf = ArrayPool<byte>.Shared.Rent(BufSize);
                try
                {
                    while (offset < fileSize)
                    {
                        result = readFile.Get.Read(out var read, offset, buf, ReadOption.None);
                        if (result.IsFailure()) return result.Miss();

                        result = writeFile.Get.Write(offset, buf.AsSpan().Slice(0, (int)read), WriteOption.None);
                        if (result.IsFailure()) return result.Miss();

                        offset += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                return Result.Success;
            }

            protected override Result DoOperateRange(Span<byte> outBuffer, OperationId operationId, long offset, long size, ReadOnlySpan<byte> inBuffer)
            {
                var result = CopyFileIfNeeded();
                if (result.IsFailure()) return result.Miss();
                return writeFile.Get.OperateRange(outBuffer, operationId, offset, size, inBuffer);
            }

            protected override Result DoSetSize(long size)
            {
                var result = CopyFileIfNeeded();
                if (result.IsFailure()) return result.Miss();
                return writeFile.Get.SetSize(size);
            }
            protected override Result DoWrite(long offset, ReadOnlySpan<byte> source, in WriteOption option)
            {
                var result = CopyFileIfNeeded();
                if (result.IsFailure()) return result.Miss();
                return writeFile.Get.Write(offset, source, option);
            }
        }

        protected override Result DoOpenFile(ref UniqueRef<IFile> outFile, in Path path, OpenMode mode)
        {
            // try to open from both the read and write filesystems
            using var uniqRead = new UniqueRef<IFile>();
            using var uniqWrite = new UniqueRef<IFile>();

            // always open the read file with Read
            var readResult = readFs.Get.OpenFile(ref uniqRead.Ref, path, OpenMode.Read);
            // open the write file with the mode provided
            var writeResult = writeFs.Get.OpenFile(ref uniqWrite.Ref, path, mode);

            Helpers.Assert(readResult.IsFailure() ^ uniqRead.HasValue);
            Helpers.Assert(writeResult.IsFailure() ^ uniqWrite.HasValue);

            // if both are failure, then return the readResult
            if (readResult.IsFailure() && writeResult.IsFailure())
            {
                return readResult;
            }

            // otherwise, we can pass them into our file
            var stored = new Path.Stored();
            var result = stored.Initialize(path);
            if (result.IsFailure()) return result.Miss();

            outFile.Reset(new WriteOverlayFile(stored, this, mode, ref uniqRead.Ref, ref uniqWrite.Ref));
            return Result.Success;
        }

        // TODO: how can we represent deletion?
        protected override Result DoDeleteDirectory(in Path path) => ResultFs.NotImplemented.Value;

        protected override Result DoDeleteDirectoryRecursively(in Path path) => ResultFs.NotImplemented.Value;
        protected override Result DoDeleteFile(in Path path) => ResultFs.NotImplemented.Value;

        // TODO: how can we represent renaming?
        protected override Result DoRenameDirectory(in Path currentPath, in Path newPath) => ResultFs.NotImplemented.Value;
        protected override Result DoRenameFile(in Path currentPath, in Path newPath) => ResultFs.NotImplemented.Value;
    }
}
