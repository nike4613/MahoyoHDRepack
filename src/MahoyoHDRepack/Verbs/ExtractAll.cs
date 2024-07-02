using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using MahoyoHDRepack.Utility;
using Path = LibHac.Fs.Path;

namespace MahoyoHDRepack.Verbs;

internal static class ExtractAll
{
    public static void Run(
        IFileSystem rootFs,
        DirectoryInfo outPath,
        bool noDecompress,
        bool noArchive,
        KnownFileTypes[] doNotProcess,
        bool invertMzx,
        bool useParallel
    )
    {
        MzxFile.DefaultInvert = invertMzx;

        _ = Directory.CreateDirectory(outPath.FullName);
        using var targetFs = new LocalFileSystem(outPath.FullName);

        var options = new ExtractionOptions()
        {
            NoDecompress = noDecompress,
            NoArchive = noArchive,
            DoNotProcess = ImmutableArray.Create(doNotProcess),
        };

        if (!useParallel)
        {
            var targetPath = GetRootPath();
            RecursiveExtractAll(rootFs, targetPath, targetFs, targetPath, in options);
        }
        else
        {
            var channel = Channel.CreateUnbounded<RecursiveExtractChannelItem>(new()
            {
                AllowSynchronousContinuations = false,
                SingleWriter = true,
            });

            // first do a scan
            var scanPctProgressReporter = new PercentageConsoleProgressReporter("Scan", 1, false);
            using var srcFs = new SharedRef<IFileSystem>(rootFs);
            using var dstFs = new SharedRef<IFileSystem>(targetFs);
            ExtractAllToChannel(channel.Writer, scanPctProgressReporter, srcFs, dstFs, in options);
            Console.WriteLine();

            // then perform extraction
            var extractPctProgressReporter = new PercentageConsoleProgressReporter("Extract", 1, true);
            DoExtractFromChannel(channel.Reader, extractPctProgressReporter);
        }
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

    private ref struct ExtractionOptions
    {
        public bool NoDecompress;
        public bool NoArchive;
        public ImmutableArray<KnownFileTypes> DoNotProcess;
    }

    private static Path.Stored ToStored(Path path)
    {
        Path.Stored result = default;
        result.Initialize(path).ThrowIfFailure();
        return result;
    }

    private static void RecursiveExtractAll(IFileSystem rootFs, Path rootFsPath, IFileSystem targetFs, Path targetFsPath, in ExtractionOptions options)
    {
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
                        if (!options.NoArchive && !options.DoNotProcess.Contains(KnownFileTypes.Mrg) && (nameSpan.EndsWith(".mrg"u8) || nameSpan.EndsWith(".MRG"u8)))
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
                                RecursiveExtractAll(fs, GetRootPath(), targetFs, dstPath, options);
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

                            if (!options.NoArchive || !options.NoDecompress)
                            {
                                var kind = FileScanner.ProbeForFileType(openFile.Get);
                                using UniqueRef<IFileSystem> archiveFileSystem = default;

                                if (!options.DoNotProcess.Contains(kind))
                                {
                                    switch (kind)
                                    {
                                        case KnownFileTypes.Mzp when !options.NoArchive:
                                            MzpFileSystem.Read(ref archiveFileSystem.Ref, storageToCopy).ThrowIfFailure();
                                            break;
                                        case KnownFileTypes.Hfa when !options.NoArchive:
                                            HfaFileSystem.Read(ref archiveFileSystem.Ref, storageToCopy).ThrowIfFailure();
                                            break;

                                        case KnownFileTypes.Nxx when !options.NoDecompress:
                                            storageToCopy = NxxFile.TryCreate(openFile.Get).AsStorage();
                                            break;
                                        case KnownFileTypes.LenZuCompressor when !options.NoDecompress:
                                            storageToCopy = LenZuCompressorFile.ReadCompressed(storageToCopy);
                                            break;
                                        case KnownFileTypes.Mzx when !options.NoDecompress:
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
                                    Helpers.DAssert(!options.NoDecompress);
                                    ForceCreateDirInFs(targetFs, dstPath);
                                    RecursiveExtractAll(fs, GetRootPath(), targetFs, dstPath, in options);
                                    goto doneProcessingFile;
                                }
                            }

                            Console.Write(Helpers.ResetLineStr + $"{enumerated:D10} " + Encoding.UTF8.GetString(dstPath.AsSpan()) + "\r");

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
    }


    private readonly record struct RecursiveExtractChannelItem(
        SharedRef<IStorage> SrcStorage, KnownFileTypes? DecompressAs,
        SharedRef<IFileSystem> DstFs, Path.Stored ToPath,
        long EnumId);

    private static void ExtractAllToChannel(
        ChannelWriter<RecursiveExtractChannelItem> outChannel, IProgressWithTotal<double> scanProgress,
        SharedRef<IFileSystem> srcFs, SharedRef<IFileSystem> targetFs, in ExtractionOptions options)
    {
        try
        {
            scanProgress.Total = 1;
            var root = GetRootPath();
            RecursiveExtractAllQueueToChannel(outChannel, scanProgress, 1, srcFs, root, targetFs, root, in options);
            outChannel.Complete();
            scanProgress.Complete();
        }
        catch (Exception e)
        {
            outChannel.Complete(e);
        }
    }

    private static void RecursiveExtractAllQueueToChannel(
        ChannelWriter<RecursiveExtractChannelItem> outChannel, IProgressWithTotal<double> scanProgress, double progressSlice,
        SharedRef<IFileSystem> rootFs, Path rootFsPath, SharedRef<IFileSystem> targetFs, Path targetFsPath,
        in ExtractionOptions options)
    {
        var dirQueue = new Queue<(Path.Stored src, Path.Stored dst, double localSlice)>();
        dirQueue.Enqueue((ToStored(rootFsPath), ToStored(targetFsPath), progressSlice));

        while (dirQueue.TryDequeue(out var tuple))
        {
            using Path srcPath = default;
            using Path dstPath = default;
            srcPath.Initialize(tuple.src).ThrowIfFailure();
            dstPath.Initialize(tuple.dst).ThrowIfFailure();

            using UniqueRef<IDirectory> srcDir = default;
            rootFs.Get.OpenDirectory(ref srcDir.Ref, srcPath, OpenDirectoryMode.All).ThrowIfFailure();

            srcDir.Get.GetEntryCount(out var entryCount).ThrowIfFailure();

            var localProgressSlice = tuple.localSlice / entryCount;

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
                        ForceCreateDirInFs(targetFs.Get, dstPath);
                        dirQueue.Enqueue((ToStored(srcPath), ToStored(dstPath), localProgressSlice));
                    }
                    else
                    {
                        // for a file, we want to either
                        // 1. copy the file from the src fs into the target fs (respecting decompression), or
                        // 2. open it as an archive and extract

                        // first, we check for the archive case
                        // check for the MRG archive case explicitly, because MRG files are truly bizarre
                        if (!options.NoArchive && !options.DoNotProcess.Contains(KnownFileTypes.Mrg) && (nameSpan.EndsWith(".mrg"u8) || nameSpan.EndsWith(".MRG"u8)))
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
                                using var fs = new SharedRef<IFileSystem>(mrgFs);
                                ForceCreateDirInFs(targetFs.Get, dstPath);
                                //RecursiveExtractAll(fs, GetRootPath(), targetFs, dstPath, options);
                                RecursiveExtractAllQueueToChannel(outChannel, scanProgress, localProgressSlice, fs, GetRootPath(), targetFs, dstPath, in options);
                                goto doneProcessingFile;
                            }
                            // if result wasn't a success, then this wasn't a valid MRG archive, so we're done
                            // but we should fix up srcPath for later code
                            srcPath.RemoveChild().ThrowIfFailure();
                            srcPath.AppendChild(nameSpan).ThrowIfFailure();
                        }

                        using (UniqueRef<IFile> openFileUnique = default)
                        {
                            var openResult = rootFs.Get.OpenFile(ref openFileUnique.Ref, srcPath, OpenMode.Read);
                            if (!openResult.IsSuccess())
                            {
                                Console.WriteLine($"Could not open file '{Encoding.UTF8.GetString(dstPath.AsSpan())}': {openResult}");
                                goto doneProcessingFile;
                            }

                            using (var openFileShared = new SharedRef<IFile>(openFileUnique.Release()))
                            {
                                using var openFileSharedCopy = SharedRef<IFile>.CreateCopy(openFileShared);
                                var storageToCopy = new FileStorage(ref openFileSharedCopy.Ref);
                                using var storageToCopyRef = new SharedRef<IStorage>(storageToCopy);
                                KnownFileTypes? decompressAs = null;

                                if (!options.NoArchive || !options.NoDecompress)
                                {
                                    var kind = FileScanner.ProbeForFileType(openFileShared.Get);
                                    using UniqueRef<IFileSystem> archiveFileSystem = default;

                                    if (!options.DoNotProcess.Contains(kind))
                                    {
                                        switch (kind)
                                        {
                                            case KnownFileTypes.Mzp when !options.NoArchive:
                                                MzpFileSystem.Read(ref archiveFileSystem.Ref, storageToCopyRef).ThrowIfFailure();
                                                break;
                                            case KnownFileTypes.Hfa when !options.NoArchive:
                                                HfaFileSystem.Read(ref archiveFileSystem.Ref, storageToCopyRef).ThrowIfFailure();
                                                break;

                                            case KnownFileTypes.Nxx when !options.NoDecompress:
                                                //storageToCopy = NxxFile.TryCreate(openFile.Get).AsStorage();
                                                decompressAs = KnownFileTypes.Nxx;
                                                break;
                                            case KnownFileTypes.LenZuCompressor when !options.NoDecompress:
                                                //storageToCopy = LenZuCompressorFile.ReadCompressed(storageToCopy);
                                                decompressAs = KnownFileTypes.LenZuCompressor;
                                                break;
                                            case KnownFileTypes.Mzx when !options.NoDecompress:
                                                //storageToCopy = MzxFile.ReadCompressed(storageToCopy);
                                                decompressAs = KnownFileTypes.Mzx;
                                                break;

                                            case KnownFileTypes.Unknown:
                                            default:
                                                // we don't know what this file is, (or rather, how to process it), or we shouldn't process it
                                                break;
                                        }
                                    }

                                    if (archiveFileSystem.Get is { } fs)
                                    {
                                        Helpers.DAssert(!options.NoArchive);
                                        ForceCreateDirInFs(targetFs.Get, dstPath);
                                        using var fsShared = new SharedRef<IFileSystem>(archiveFileSystem.Release());
                                        RecursiveExtractAllQueueToChannel(outChannel, scanProgress, localProgressSlice, fsShared, GetRootPath(), targetFs, dstPath, in options);
                                        goto doneProcessingFile;
                                    }
                                }

                                Helpers.Assert(outChannel.TryWrite(new(
                                    SharedRef<IStorage>.CreateCopy(storageToCopyRef), decompressAs,
                                    SharedRef<IFileSystem>.CreateCopy(targetFs), ToStored(dstPath),
                                    enumerated)));

                                scanProgress.AddProgress(localProgressSlice);
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
    }

    private static void DoExtractFromChannel(ChannelReader<RecursiveExtractChannelItem> channel, IProgressWithTotal<double> progress)
    {
        progress.Total = channel.Count;

        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount * 4,
#if DEBUG
            //MaxDegreeOfParallelism = 1
#endif
        };

        var messagesChannel = Channel.CreateUnbounded<string>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var messagePrinterThread = new Thread(() =>
        {
            while (messagesChannel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (messagesChannel.Reader.TryRead(out var msg))
                {
                    Console.Write($"\r{Helpers.ResetLineStr}{msg}{Environment.NewLine}");
                }
            }
        });

        messagePrinterThread.Start();

        Parallel.ForEachAsync(channel.ReadAllAsync(), opts, (it, ct) =>
        {
            using var srcStorageRef = it.SrcStorage;
            using var targetFs = it.DstFs;
            using var dstPath = it.ToPath.DangerousGetPath();

            var srcStorage = srcStorageRef.Get;

            switch (it.DecompressAs)
            {
                case KnownFileTypes.Nxx:
                    srcStorage = NxxFile.TryCreate(srcStorage.AsFile(OpenMode.Read)).AsStorage();
                    break;
                case KnownFileTypes.LenZuCompressor:
                    srcStorage = LenZuCompressorFile.ReadCompressed(srcStorage);
                    break;
                case KnownFileTypes.Mzx:
                    srcStorage = MzxFile.ReadCompressed(srcStorage);
                    break;

                case KnownFileTypes.Unknown:
                case KnownFileTypes.Mrg:
                case KnownFileTypes.Mzp:
                case KnownFileTypes.Hfa:
                case null:
                default:
                    // don't need to change the storage
                    break;
            }

            // report the progress item
            progress.SetStatusString($"{Encoding.UTF8.GetString(dstPath.AsSpan())} (id {it.EnumId})");

            // copy the file
            srcStorage.GetSize(out var totalSize).ThrowIfFailure();
            ForceCreateFileInFs(targetFs.Get, dstPath, totalSize, CreateFileOptions.None);
            using (UniqueRef<IFile> dstFile = default)
            {
                var openResult = targetFs.Get.OpenFile(ref dstFile.Ref, dstPath, OpenMode.Write);
                if (openResult == ResultFs.TargetLocked.Value)
                {
                    Helpers.Assert(messagesChannel.Writer.TryWrite(
                        $"WARNING: Could not open {Encoding.UTF8.GetString(dstPath.AsSpan())} (id {it.EnumId}) for writing. " +
                        $"This likely means that there are multiple files with that name."));
                    goto Done;
                }
                openResult.ThrowIfFailure();
                dstFile.Get.SetSize(totalSize).ThrowIfFailure();
                srcStorage.CopyTo(dstFile.Get.AsStorage());
            }

        Done:
            // report it as done
            progress.AddProgress(1);

            return default;

        }).Wait();

        messagesChannel.Writer.Complete();
        progress.Complete();

        messagePrinterThread.Join();
    }
}

