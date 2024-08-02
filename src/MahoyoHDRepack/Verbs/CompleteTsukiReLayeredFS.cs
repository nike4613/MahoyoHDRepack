using System.IO;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using System;

namespace MahoyoHDRepack.Verbs;

internal sealed class CompleteTsukiReLayeredFS
{
    public static void Run(
        string? ryuBase,
        FileInfo xciFile,
        GameLanguage secondLang,
        DirectoryInfo tsukihimatesDir,
        FileInfo? tsukihimeNsp,
        FileInfo? tsukihimatesNsp,
        DirectoryInfo outDir,
        bool invertMzx
    )
    {
        MzxFile.DefaultInvert = invertMzx;
        Common.InitRyujinx(ryuBase, out var horizon, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        using var rawRomfs = new SharedRef<IFileSystem>(XciHelpers.MountXci(xciStorage, vfs, xciFile.Name));
        using var outRomfs = new SharedRef<IFileSystem>(new LocalFileSystem(outDir.FullName));
        using var romfs = new WriteOverlayFileSystem(rawRomfs, outRomfs);

        if (tsukihimeNsp is not null && tsukihimatesNsp is not null)
        {
            using var tsukiHandle = File.OpenHandle(tsukihimeNsp.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            using var tsukiStorage = new RandomAccessStorage(tsukiHandle);
            using var nspHandle = File.OpenHandle(tsukihimatesNsp.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            using var nspStorage = new RandomAccessStorage(nspHandle);

            using var tsukihimatesRomfs = XciHelpers.MountXci(tsukiStorage, vfs, tsukihimeNsp.Name, nspStorage);

            CopyMovies(tsukihimatesRomfs, romfs, secondLang);
        }
        else
        {
            Console.WriteLine($"WARNING: Cannot fully build the LayeredFS patch; no Tsukihimates patch found!");
        }
    }

    private static void CopyMovies(IFileSystem srcFs, IFileSystem dstFs, GameLanguage targetLanguage)
    {
        ReadOnlySpan<string> copyRename = [
            "op1_arc",
            "op2_ciel",
            "phasetitle_*",
            "prologue",
        ];

        ReadOnlySpan<string> copyRaw = [
            // note: we want to copy the endings over as well, because they contain subtitles
            "ed01_arc",
            "ed02_ciel",
            "ed03_ciel",

            // TM_CI to include the Tsukihimates logo at startup
            "TM_CI",
        ];

        // First, lets do the raw copies
        foreach (var name in copyRaw)
        {
            var fullPath = $"/movie/{name}.mp4";

            using UniqueRef<IFile> srcFile = default;
            using UniqueRef<IFile> dstFile = default;

            Console.WriteLine(fullPath);

            srcFs.OpenFile(ref srcFile.Ref, fullPath.ToU8String(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();
            dstFs.OpenFile(ref dstFile.Ref, fullPath.ToU8String(), LibHac.Fs.OpenMode.Write).ThrowIfFailure();

            srcFile.Get.GetSize(out var srcSize).ThrowIfFailure();
            dstFile.Get.SetSize(srcSize).ThrowIfFailure();
            srcFile.Get.CopyTo(dstFile.Get);
        }

        var langSuffix = targetLanguage switch
        {
            GameLanguage.JP => "",
            GameLanguage.EN => "_en",
            GameLanguage.ZC => "_zc",
            GameLanguage.ZT => "_zt",
            GameLanguage.KO => "_ko",
            _ => "_unk"
        };

        // Next, copyRename
        foreach (var name in copyRename)
        {
            foreach (var file in srcFs.EnumerateEntries("/movie/", $"{name}.mp4", SearchOptions.Default))
            {
                if (file.Type is not LibHac.Fs.DirectoryEntryType.File) continue;

                var dstName = $"{Path.GetFileNameWithoutExtension(file.Name)}{langSuffix}{Path.GetExtension(file.Name)}";
                var dstPath = $"/movie/{dstName}";

                using UniqueRef<IFile> srcFile = default;
                using UniqueRef<IFile> dstFile = default;

                Console.WriteLine($"{file.FullPath} -> {dstPath}");

                srcFs.OpenFile(ref srcFile.Ref, file.FullPath.ToU8String(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();
                dstFs.OpenFile(ref dstFile.Ref, dstPath.ToU8String(), LibHac.Fs.OpenMode.Write).ThrowIfFailure();

                srcFile.Get.GetSize(out var srcSize).ThrowIfFailure();
                dstFile.Get.SetSize(srcSize).ThrowIfFailure();
                srcFile.Get.CopyTo(dstFile.Get);
            }
        }
    }
}
