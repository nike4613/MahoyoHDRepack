using System;
using System.IO;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;
using FsPath = LibHac.Fs.Path;
using Path = System.IO.Path;

namespace MahoyoHDRepack.Verbs;

internal static class ExtractFile
{
    public static void Run(
        IFileSystem rootFs,
        string arcPath,
        string outLoc,
        bool raw,
        bool noArc
    )
    {
        // TODO: support extracting full directories

        using var path = new FsPath();
        path.InitializeWithNormalization(arcPath.ToU8Span()).ThrowIfFailure();

        using var uniqFile = OpenFile(rootFs, path, raw, noArc);

        FileInfo outFile;
        if (outLoc.EndsWith('/') || Directory.Exists(outLoc))
        {
            outFile = new(Path.Combine(outLoc, Path.GetFileName(arcPath)));
        }
        else
        {
            outFile = new(outLoc);
        }

        using var fstream = outFile.OpenWrite();
        using var instream = uniqFile.Get.AsStream();
        instream.CopyTo(fstream);
    }

    private static UniqueRef<IFile> OpenFile(IFileSystem romfs, in FsPath path, bool raw, bool noArchive)
    {
        Utils.Normalize(path, out var normalized).ThrowIfFailure();
        using var uniqFile = new UniqueRef<IFile>();
        if (noArchive)
        {
            // we don't need to search in archives
            romfs.OpenFile(ref uniqFile.Ref, normalized, OpenMode.Read).ThrowIfFailure();
        }
        else
        {
            // we want to scan child archives
            OpenFileInArchive(ref uniqFile.Ref, romfs, normalized).ThrowIfFailure();
        }

        if (!raw)
        {
            uniqFile.Reset(FileScanner.TryGetDecompressedFile(uniqFile.Get));
        }

        return UniqueRef<IFile>.Create(ref uniqFile.Ref);
    }

    private static Result OpenFileInArchive(ref UniqueRef<IFile> outFile, IFileSystem fs, FsPath path)
    {
        // first thing we try is opening the file at the path directly
        Console.WriteLine("OpenFileInArchive(" + fs.GetType() + "," + System.Text.Encoding.UTF8.GetString(path.AsSpan()) + ")");
        var result = fs.OpenFile(ref outFile, path, OpenMode.Read);
        if (result.IsSuccess()) return result;
        Console.WriteLine($"OpenFile failed: " + result.ToStringWithName());

        scoped FsPath normalized;

        // walk up the path, probing for an extant file
        scoped var inArcPath = new FsPath();
        inArcPath.InitializeAsEmpty().ThrowIfFailure();

        while (true)
        {
            Console.WriteLine("scanning");
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(path.AsSpan()));
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(inArcPath.AsSpan()));
            // test for the existence of the provided path
            result = fs.GetEntryType(out _, path);
            if (result.IsSuccess()) break; // the path exists

            // we also want to try for an MRG filesystem because that's funky
            result = MrgFileSystem.Read(fs, path, out var mrgfs);
            if (result.IsSuccess())
            {
                Helpers.Assert(mrgfs is not null);
                Utils.Normalize(inArcPath, out normalized).ThrowIfFailure();
                // we have our archive fs, pass that forward
                return OpenFileInArchive(ref outFile, mrgfs, normalized);
            }

            // pop the child element from path, and insert it as a parent for inArcPath
            var pspan = path.AsSpan().ToArray().AsSpan();
            path.RemoveChild().ThrowIfFailure();
            pspan = pspan.Slice(path.GetLength());
            inArcPath.InsertParent(pspan).ThrowIfFailure();
        }

        // at this point, we should have a functional file
        using var uniqFile = new UniqueRef<IFile>();
        fs.OpenFile(ref uniqFile.Ref, path, OpenMode.Read).ThrowIfFailure();

        // now, we check the archive type
        var ftype = FileScanner.ProbeForFileType(uniqFile.Get);
        switch (ftype)
        {
            case KnownFileTypes.Mzp:
                {
                    using var uniqFs = new UniqueRef<IFileSystem>();
                    MzpFileSystem.Read(ref uniqFs.Ref, uniqFile.Release().AsStorage()).ThrowIfFailure();
                    var mzpFs = uniqFs.Release(); // we have to release it because we need it to persist past the end of the invocation due to the scheme here
                    // oh well!
                    Utils.Normalize(inArcPath, out normalized).ThrowIfFailure();
                    return OpenFileInArchive(ref outFile, mzpFs, normalized);
                }

            case KnownFileTypes.Hfa:
                {
                    using var uniqFs = new UniqueRef<IFileSystem>();
                    HfaFileSystem.Read(ref uniqFs.Ref, uniqFile.Release().AsStorage()).ThrowIfFailure();
                    var hfaFs = uniqFs.Release();
                    Utils.Normalize(inArcPath, out normalized).ThrowIfFailure();
                    return OpenFileInArchive(ref outFile, hfaFs, normalized);
                }

            case KnownFileTypes.Unknown:
            case KnownFileTypes.Mzx:
            case KnownFileTypes.Nxx:
            default:
                return ResultFs.FileNotFound.Miss();
        }
    }
}
