using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Csv;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using MahoyoHDRepack.ScriptText;

namespace MahoyoHDRepack.Verbs;

internal static class RepackScript
{
    public static void Run(
        string? ryuBase,
        FileInfo xciFile,
        GameLanguage secondLang,
        FileInfo csvFile,
        int replaceAboveScore,
        DirectoryInfo outDir
    )
    {
        Common.InitRyujinx(ryuBase, out var horizon, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        if (outDir.Exists)
        {
            outDir.Delete(true);
        }

        using var rawRomfs = new SharedRef<IFileSystem>(XciHelpers.MountXci(xciStorage, vfs));
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
        LineBoundaryParser.ReadLines(mzpFs, GameLanguage.JP, out var jpLines).ThrowIfFailure();
        LineBoundaryParser.ReadLines(mzpFs, secondLang, out var langLines).ThrowIfFailure();

        Helpers.Assert(jpLines.Length == langLines.Length);

        // read the CSV
        string[][] csvLines;
        using (var inStream = csvFile.OpenRead())
        using (var reader = new StreamReader(inStream))
        {
            csvLines = CsvReader.Read(reader, new()
            {
                AllowNewLineInEnclosedFieldValues = true
            }).Select(line => line.Values).ToArray();
        }

        var useIndex = true;
        if (csvLines.Length != langLines.Length)
        {
            Console.WriteLine($"WARNING: CSV file has fewer lines than game script! {csvLines.Length} < {langLines.Length}");
            //Console.WriteLine("This means that the line inserter will use fuzzy matching to find all replacement candidates.");
            //useIndex = false;
        }

        var updatedLang = new string[langLines.Length];
        Array.Copy(langLines, updatedLang, langLines.Length);

        string[]? matcherLines = null;

        for (var i = 0; i < csvLines.Length; i++)
        {
            var line = csvLines[i];
            if (line.Length < 3)
            {
                Console.WriteLine($"WARNING: CSV line {i + 2} has too few elements. Skipping.");
                continue;
            }

            if (string.IsNullOrEmpty(line[2]))
            {
                Console.WriteLine($"WARNING: CSV line {i + 2} has empty 3rd column. Skipping.");
                continue;
            }

            if (useIndex)
            {
                // we're matching by index. JP is our ground truth, and if it doesn't match, we fall back to fuzzy matching
                if (line[0] == jpLines[i])
                {
                    // we're good to use index matching
                    // still issue a warning if the orig line doesn't match
                    if (line[1] != langLines[i])
                    {
                        Console.WriteLine($"WARNING: CSV file original line does not match actual original line (row {i + 2})");
                    }
                    updatedLang[i] = line[2];
                    continue;
                }

                Console.WriteLine($"WARNING: JP line does not match CSV file! (row {i + 2})");
                useIndex = false;
            }

            // we want to first search for the JP line, and if we can't find it, fuzzy match for it
            var jpIdx = jpLines.AsSpan().IndexOf(line[0]);
            if (jpIdx > 0)
            {
                // we found the JP line, replace in the lang
                updatedLang[jpIdx] = line[2];
                continue;
            }
            else
            {
                static string JoinStringsForFuzzyMatch(string s1, string s2) => s1 + "////////" + s2;
                Console.WriteLine($"WARNING: JP line in row {i + 2} was not found in game script! Doing fuzzy match on JP and orig lang...");
                matcherLines ??= jpLines.Zip(langLines).Select(t => JoinStringsForFuzzyMatch(t.First, t.Second)).ToArray();
                var result = FuzzySharp.Process.ExtractOne(JoinStringsForFuzzyMatch(line[0], line[1]), matcherLines, static s => s, cutoff: replaceAboveScore);

                if (result is null)
                {
                    Console.WriteLine($"ERROR: Could not find fuzzy match of JP/Lang at row {i + 2}!");
                }
                else
                {
                    updatedLang[result.Index] = line[2];
                }
            }
        }

        // updatedLang now contains our language lines

        ScriptLineWriter.WriteLines(mzpFs, secondLang, updatedLang).ThrowIfFailure();
        mzpFs.Flush().ThrowIfFailure();
    }
}
