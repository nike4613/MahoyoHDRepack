using System.IO;
using System.Linq;
using Csv;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.Tools.FsSystem;
using MahoyoHDRepack.ScriptText;

namespace MahoyoHDRepack.Verbs;

internal static class ExtractScript
{
    public static void Run(
        string? ryuBase,
        FileInfo xciFile,
        GameLanguage secondLang,
        FileInfo outFile,
        bool invertMzx
    )
    {
        MzxFile.DefaultInvert = invertMzx;

        Common.InitRyujinx(ryuBase, out var horizon, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        var romfs = XciHelpers.MountXci(xciStorage, vfs, xciFile.Name);

        using var uniqScriptTextFile = new UniqueRef<IFile>();
        romfs.OpenFile(ref uniqScriptTextFile.Ref, "/script_text.mrg".ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

        Helpers.Assert(FileScanner.ProbeForFileType(uniqScriptTextFile.Get) is KnownFileTypes.Mzp, "script_text.mrg is not an mzp???");

        using var uniqMzpFs = new UniqueRef<MzpFileSystem>();
        MzpFileSystem.Read(ref uniqMzpFs.Ref, uniqScriptTextFile.Get.AsStorage()).ThrowIfFailure();

        using var mzpFs = uniqMzpFs.Release();

        // we now have access to script_text's filesystem

        // first, we always want to read JP
        LineBoundaryParser.ReadLines(mzpFs, GameLanguage.JP, out var jpLines).ThrowIfFailure();
        // then we read the requested language
        LineBoundaryParser.ReadLines(mzpFs, secondLang, out var langLines).ThrowIfFailure();

        Helpers.Assert(jpLines.Length == langLines.Length);

        // column headers
        var headers = new[] { GameLanguage.JP.ToString(), secondLang.ToString(), $"{secondLang} Replacement" };

        // row enumeration
        var rows = jpLines.Zip(langLines)
            .Select(p => new[] { p.First, p.Second });

        using (var outStream = outFile.OpenWrite())
        using (var writer = new StreamWriter(outStream))
        {
            CsvWriter.Write(writer, headers, rows);
        }
    }
}
