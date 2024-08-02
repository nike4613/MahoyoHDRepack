using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using MahoyoHDRepack.ScriptText;
using MahoyoHDRepack.ScriptText.DeepLuna;

namespace MahoyoHDRepack.Verbs;

internal static class RepackScriptDeepLuna
{
    public static void Run(
        string? ryuBase,
        FileInfo xciFile,
        GameLanguage secondLang,
        string[] deepLunaFilesAndDirs,
        DirectoryInfo outDir,
        FileInfo fontInfoJson,
        bool invertMzx
    )
    {
        MzxFile.DefaultInvert = invertMzx;
        Common.InitRyujinx(ryuBase, out var horizon, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        if (outDir.Exists)
        {
            outDir.Delete(true);
        }

        using var rawRomfs = new SharedRef<IFileSystem>(XciHelpers.MountXci(xciStorage, vfs, xciFile.Name));
        using var outRomfs = new SharedRef<IFileSystem>(new LocalFileSystem(outDir.FullName));
        using var romfs = new WriteOverlayFileSystem(rawRomfs, outRomfs);

        using var uniqScriptTextFile = new UniqueRef<IFile>();
        romfs.OpenFile(ref uniqScriptTextFile.Ref, "/script_text.mrg".ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

        Helpers.Assert(FileScanner.ProbeForFileType(uniqScriptTextFile.Get) is KnownFileTypes.Mzp, "script_text.mrg is not an mzp???");

        using var uniqMzpFs = new UniqueRef<MzpFileSystem>();
        MzpFileSystem.Read(ref uniqMzpFs.Ref, uniqScriptTextFile.Get.AsStorage()).ThrowIfFailure();

        using var mzpFs = uniqMzpFs.Release();

        // we now have access to script_text's filesystem

        // we always want to read the JP and the target language
        LineBoundaryParser.ReadLinesU8(mzpFs, GameLanguage.JP, out var jpLines).ThrowIfFailure();
        LineBoundaryParser.ReadLines(mzpFs, secondLang, out var langLines).ThrowIfFailure();

        Console.WriteLine($"Loaded JP with {jpLines.Length} strings");
        Console.WriteLine($"Loaded {secondLang} with {langLines.Length} strings");

        // now, let's load the deepLuna database
        var deepLunaDb = new DeepLunaDatabase();
        var textProcessor = new DeepLunaTextProcessor();

        foreach (var fileOrDir in deepLunaFilesAndDirs)
        {
            if (File.Exists(fileOrDir))
            {
                DeepLunaParser.Parse(deepLunaDb, textProcessor,
                    Path.GetFileName(fileOrDir),
                    File.ReadAllText(fileOrDir));
            }
            else if (Directory.Exists(fileOrDir))
            {
                // this is a dir, enumerate all *.txt files
                foreach (var file in Directory.EnumerateFiles(fileOrDir, "*.txt", SearchOption.AllDirectories))
                {
                    DeepLunaParser.Parse(deepLunaDb, textProcessor,
                        Path.GetFileName(file),
                        File.ReadAllText(file));
                }
            }
        }

        Console.WriteLine($"Loaded deepLuna with {deepLunaDb.Count} lines");

        Console.WriteLine($"Writing font info to {fontInfoJson}");
        using (var stream = File.Create(fontInfoJson.FullName))
        {
            JsonSerializer.Serialize(stream, textProcessor.GetFontInfoModel(), FontInfoJsonContext.Default.FontInfo);
        }

        const string Sep = "-------------------------------------------------------------------------------------------------------";

        Helpers.Assert(langLines.Length == jpLines.Length);
        var inserted = 0;
        var failed = 0;
        for (var i = 0; i < langLines.Length; i++)
        {
            // look up a translation for this line
            if (deepLunaDb.TryLookupLine(jpLines[i].AsSpan(), i, out var line))
            {
                // found a match, insert it
                // TODO: process ruby text
                if (line.Translated is not null)
                {
                    langLines[i] = line.Translated + "\r\n";
                }

                line.Used = true;
                inserted++;
            }
            else
            {
                // did not find a match, print
                Console.WriteLine($"ERROR: Could not find match for JP line: sha:{Convert.ToHexString(SHA1.HashData(jpLines[i].AsSpan())).ToLowerInvariant()}");
                Console.WriteLine(Sep);
                Console.WriteLine(Encoding.UTF8.GetString(jpLines[i].AsSpan()));
                Console.WriteLine(Sep);
                Console.WriteLine(langLines[i]);
                Console.WriteLine(Sep);
                failed++;
            }
        }

        var unused = 0;
        foreach (var line in deepLunaDb.UnusedLines)
        {
            Console.Write($"WARNING: Unused deepLuna line: ");
            if (!line.Hash.IsDefault)
            {
                Console.WriteLine($"sha:{Convert.ToHexString(line.Hash.AsSpan()).ToLowerInvariant()}");
            }
            else
            {
                Console.WriteLine($"offset:{line.Offset}");
            }
            Console.WriteLine(Sep);
            Console.WriteLine(line.Translated);
            Console.WriteLine(Sep);
            Console.WriteLine($"From {line.SourceFile} @ {line.SourceLineStart}");
            Console.WriteLine($"Comment: {line.Comments}");
            Console.WriteLine(Sep);
            unused++;
        }

        Console.WriteLine($"Script insert completed. Inserted {inserted} lines, failed on {failed} lines, not using {unused} lines.");

        // langLines now contains our language lines

        ScriptLineWriter.WriteLines(mzpFs, secondLang, langLines).ThrowIfFailure();
        mzpFs.Flush().ThrowIfFailure();
    }
}
