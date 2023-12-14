using System;
using System.IO;
using System.Threading.Tasks;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack.Verbs;

internal static class UnpackHD
{
    public static Task<int> Run(
        string? ryuBase,
        FileInfo xciFile
    )
    {
        Common.InitRyujinx(ryuBase, out _, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        var romfs = XciHelpers.MountXci(xciStorage, vfs);

        // we've now mounted the ROMFS and have access to the files inside

        /*foreach (var file in romfs.EnumerateEntries())
        {
            Console.WriteLine(file.FullPath);
        }*/

        LibHac.Fs.Path path = default;
        path.InitializeWithNormalization("/allui"u8).ThrowIfFailure();

        MrgFileSystem.Read(romfs, path, out var fs).ThrowIfFailure();
        Helpers.Assert(fs is not null);

        /*foreach (var file in fs.EnumerateEntries())
        {
            Console.WriteLine(file.FullPath);
        }*/

        using var scriptTextFile = new UniqueRef<IFile>();
        romfs.OpenFile(ref scriptTextFile.Ref, "/script_text.mrg".ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();
        using var uniqZpfs = new UniqueRef<IFileSystem>();
        MzpFileSystem.Read(ref uniqZpfs.Ref, scriptTextFile.Release().AsStorage()).ThrowIfFailure();
        using var zpfs = uniqZpfs.Release();

        foreach (var file in zpfs.EnumerateEntries())
        {
            Console.WriteLine(file.FullPath);
        }

        using var dat0file = new UniqueRef<IFile>();
        zpfs.OpenFile(ref dat0file.Ref, "/0".ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

        using (var outFile = File.OpenWrite("script_text.mrg.0"))
        {
            dat0file.Get.AsStream(LibHac.Fs.OpenMode.Read, true).CopyTo(outFile);
        }

        using var cgPartsFile = new UniqueRef<IFile>();
        fs.OpenFile(ref cgPartsFile.Ref, "/CG_PARTS.NXZ".ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

        using (var outFile = File.OpenWrite("CG_PARTS"))
        {
            cgPartsFile.Get.AsStream(LibHac.Fs.OpenMode.Read, true).CopyTo(outFile);
        }

        if (Directory.Exists("romfs"))
            Directory.Delete("romfs", true);
        using var localRomfs = new SharedRef<IFileSystem>(new LocalFileSystem("romfs"));
        using var shromfs = new SharedRef<IFileSystem>(romfs);
        using var woromfs = new WriteOverlayFileSystem(shromfs, localRomfs);

        using var corpTgaFile = new UniqueRef<IFile>();
        woromfs.OpenFile(ref corpTgaFile.Ref, "/corp.tga".ToU8Span(), LibHac.Fs.OpenMode.ReadWrite).ThrowIfFailure();

        corpTgaFile.Get.GetSize(out var corpTgaSize).ThrowIfFailure();
        corpTgaFile.Get.SetSize(corpTgaSize).ThrowIfFailure();

        return Task.FromResult(0);
    }
}
