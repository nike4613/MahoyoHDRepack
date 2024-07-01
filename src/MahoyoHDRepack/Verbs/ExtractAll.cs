using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using Path = LibHac.Fs.Path;

namespace MahoyoHDRepack.Verbs;

internal static class ExtractAll
{
    private const string ResetLineStr = "\e[2K";
    private const string ClearLineToEol = "\e[0K";
    private const string SaveCursorStr = "\e7";
    private const string RestoreCursorStr = "\e8";

    public static void Run(
        IFileSystem rootFs,
        DirectoryInfo outPath,
        bool noDecompress,
        bool noArchive,
        KnownFileTypes[] doNotProcess
    )
    {
        _ = Directory.CreateDirectory(outPath.FullName);
        using var targetFs = new LocalFileSystem(outPath.FullName);

        // initialize console stuff
        Console.Write("depth " + SaveCursorStr);

        var targetPath = GetRootPath();
        RecursiveExtractAll(rootFs, targetPath, targetFs, targetPath, noDecompress, noArchive, doNotProcess);

        Console.WriteLine(ResetLineStr + "\rDone!");
    }

    private static Path GetRootPath()
    {
        Path targetPath = default;
        targetPath.InitializeWithNormalization("/"u8).ThrowIfFailure();
        return targetPath;
    }

    private static void ForceCreateDirInFs(IFileSystem targetFs, in Path dstPath)
    {
        do
        {
            var result = targetFs.CreateDirectory(dstPath);
            if (result == ResultFs.PathAlreadyExists.Value)
            {
                targetFs.GetEntryType(out var kind, dstPath).ThrowIfFailure();
                if (kind != DirectoryEntryType.Directory)
                {
                    // if the path is a file (not a dir) we want to delete and retry
                    targetFs.DeleteFile(dstPath).ThrowIfFailure();
                    continue;
                }
                else
                {
                    // path is a directoy, we're good
                    break;
                }
            }
            else if (!result.IsSuccess())
            {
                result.ThrowIfFailure();
            }

            break;
        }
        while (true);
    }

    private static void ForceCreateFileInFs(IFileSystem targetFs, in Path dstPath, long size, CreateFileOptions options)
    {
        do
        {
            var result = targetFs.CreateFile(dstPath, size, options);
            if (result == ResultFs.PathAlreadyExists.Value)
            {
                targetFs.GetEntryType(out var kind, dstPath).ThrowIfFailure();
                if (kind != DirectoryEntryType.File)
                {
                    // if the path is a dir (not a file), delete it
                    targetFs.DeleteDirectoryRecursively(dstPath).ThrowIfFailure();
                    continue;
                }
                else
                {
                    // path is a file, we're good
                    break;
                }
            }
            else if (!result.IsSuccess())
            {
                result.ThrowIfFailure();
            }

            break;
        }
        while (true);
    }

    private static void RecursiveExtractAll(IFileSystem rootFs, Path rootFsPath, IFileSystem targetFs, Path targetFsPath, bool noDecompress, bool noArchive, KnownFileTypes[] doNotProcess)
    {
        static Path.Stored ToStored(Path path)
        {
            Path.Stored result = default;
            result.Initialize(path).ThrowIfFailure();
            return result;
        }

        // Add dot to depth display
        Console.WriteLine($"{RestoreCursorStr}. {SaveCursorStr}");

        var dirQueue = new Queue<(Path.Stored src, Path.Stored dst)>();
        dirQueue.Enqueue((ToStored(rootFsPath), ToStored(targetFsPath)));

        while (dirQueue.TryDequeue(out var tuple))
        {
            using Path srcPath = default;
            using Path dstPath = default;
            srcPath.Initialize(tuple.src).ThrowIfFailure();
            dstPath.Initialize(tuple.dst).ThrowIfFailure();

            using UniqueRef<IDirectory> srcDir = default;
            rootFs.OpenDirectory(ref srcDir.Ref, srcPath, OpenDirectoryMode.All).ThrowIfFailure();

            srcDir.Get.GetEntryCount(out var entryCount).ThrowIfFailure();
            long enumerated = 0;
            var entryBuffer = ArrayPool<DirectoryEntry>.Shared.Rent((int)long.Min(entryCount, 128));

            while (enumerated < entryCount)
            {
                srcDir.Get.Read(out var numRead, entryBuffer).ThrowIfFailure();
                for (var i = 0; i < numRead; i++, enumerated++)
                {
                    var entry = entryBuffer[i];
                    var nameSpan = entry.Name.ItemsRo.SliceToFirstNull();

                    srcPath.AppendChild(nameSpan).ThrowIfFailure();
                    dstPath.AppendChild(nameSpan).ThrowIfFailure();

                    if (entry.Type == DirectoryEntryType.Directory)
                    {
                        // for a directory, create the dir in the target fs and enqueue
                        ForceCreateDirInFs(targetFs, dstPath);
                        dirQueue.Enqueue((ToStored(srcPath), ToStored(dstPath)));
                    }
                    else
                    {
                        // for a file, we want to either
                        // 1. copy the file from the src fs into the target fs (respecting decompression), or
                        // 2. open it as an archive and extract

                        // first, we check for the archive case
                        // check for the MRG archive case explicitly, because MRG files are truly bizarre
                        if (!noArchive && !doNotProcess.Contains(KnownFileTypes.Mrg) && (nameSpan.EndsWith(".mrg"u8) || nameSpan.EndsWith(".MRG"u8)))
                        {
                            // we need to strip the MRG suffix from the path we pass to MrgFileSystem, because it needs to look for
                            // sidecar files. I don't want to bother trying to filter those sidecar files from the output, so they get
                            // to stay.
                            srcPath.RemoveChild().ThrowIfFailure();
                            srcPath.AppendChild(nameSpan[0..^4]).ThrowIfFailure();

                            var result = MrgFileSystem.Read(rootFs, srcPath, out var mrgFs);
                            if (result.IsSuccess())
                            {
                                Helpers.DAssert(mrgFs is not null);
                                using var fs = mrgFs;
                                ForceCreateDirInFs(targetFs, dstPath);
                                RecursiveExtractAll(fs, GetRootPath(), targetFs, dstPath, noDecompress, noArchive, doNotProcess);
                                goto doneProcessingFile;
                            }
                            // if result wasn't a success, then this wasn't a valid MRG archive, so we're done
                            // but we should fix up srcPath for later code
                            srcPath.RemoveChild().ThrowIfFailure();
                            srcPath.AppendChild(nameSpan).ThrowIfFailure();
                        }

                        using (UniqueRef<IFile> openFile = default)
                        {
                            var openResult = rootFs.OpenFile(ref openFile.Ref, srcPath, OpenMode.Read);
                            if (!openResult.IsSuccess())
                            {
                                Console.WriteLine($"Could not open file '{Encoding.UTF8.GetString(dstPath.AsSpan())}': {openResult}");
                                goto doneProcessingFile;
                            }

                            var storageToCopy = openFile.Get.AsStorage();

                            if (!noArchive || !noDecompress)
                            {
                                var kind = FileScanner.ProbeForFileType(openFile.Get);
                                using UniqueRef<IFileSystem> archiveFileSystem = default;

                                if (!doNotProcess.Contains(kind))
                                {
                                    switch (kind)
                                    {
                                        case KnownFileTypes.Mzp when !noArchive:
                                            MzpFileSystem.Read(ref archiveFileSystem.Ref, storageToCopy).ThrowIfFailure();
                                            break;
                                        case KnownFileTypes.Hfa when !noArchive:
                                            HfaFileSystem.Read(ref archiveFileSystem.Ref, storageToCopy).ThrowIfFailure();
                                            break;

                                        case KnownFileTypes.Nxx when !noDecompress:
                                            storageToCopy = NxxFile.TryCreate(openFile.Get).AsStorage();
                                            break;
                                        case KnownFileTypes.LenZuCompressor when !noDecompress:
                                            storageToCopy = LenZuCompressorFile.ReadCompressed(storageToCopy);
                                            break;
                                        case KnownFileTypes.Mzx when !noDecompress:
                                            storageToCopy = MzxFile.ReadCompressed(storageToCopy);
                                            break;

                                        case KnownFileTypes.Unknown:
                                        default:
                                            // we don't know what this file is, (or rather, how to process it), or we shouldn't process it
                                            break;
                                    }
                                }

                                if (archiveFileSystem.Get is { } fs)
                                {
                                    Helpers.DAssert(!noArchive);
                                    ForceCreateDirInFs(targetFs, dstPath);
                                    RecursiveExtractAll(fs, GetRootPath(), targetFs, dstPath, noDecompress, noArchive, doNotProcess);
                                    goto doneProcessingFile;
                                }
                            }

                            Console.Write(ResetLineStr + $"{enumerated:D10} " + Encoding.UTF8.GetString(dstPath.AsSpan()) + "\r");

                            // storageToCopy now contains the IStorage to copy into the target fs
                            storageToCopy.GetSize(out var totalSize).ThrowIfFailure();
                            ForceCreateFileInFs(targetFs, dstPath, totalSize, CreateFileOptions.None);
                            using (UniqueRef<IFile> dstFile = default)
                            {
                                targetFs.OpenFile(ref dstFile.Ref, dstPath, OpenMode.Write).ThrowIfFailure();
                                dstFile.Get.SetSize(totalSize).ThrowIfFailure();
                                storageToCopy.CopyTo(dstFile.Get.AsStorage());
                            }
                        }
                    }

                doneProcessingFile:
                    srcPath.RemoveChild().ThrowIfFailure();
                    dstPath.RemoveChild().ThrowIfFailure();
                }
            }

            ArrayPool<DirectoryEntry>.Shared.Return(entryBuffer);
        }

        // Clear dot from depth display
        Console.WriteLine($"{RestoreCursorStr}\b\b{ClearLineToEol}{SaveCursorStr}");
    }
}

