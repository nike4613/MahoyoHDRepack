using System.IO;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack.Verbs;

internal static class ExtractFile
{
    public static void Run(
        string? ryuBase,
        FileInfo xciFile,
        string arcPath,
        string outLoc
    )
    {
        Common.InitRyujinx(ryuBase, out var horizon, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        var romfs = XciHelpers.MountXci(xciStorage, vfs);

        // TODO: implement archive child extraction

        using var uniqFile = new UniqueRef<IFile>();
        romfs.OpenFile(ref uniqFile.Ref(), arcPath.ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

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
}
