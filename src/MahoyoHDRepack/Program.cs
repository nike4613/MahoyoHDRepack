using System.CommandLine;
using System.IO;
using MahoyoHDRepack;
using MahoyoHDRepack.Verbs;

var rootCmd = new RootCommand();

var ryuBasePath = new Option<string?>(
    "--ryujinx-base", "The Ryujinx base path.")
{
    IsRequired = false,
    Arity = ArgumentArity.ZeroOrOne
};

var xciFile = new Option<FileInfo>(
    new[] { "--xci", "-x" }, "Game XCI file")
{
    IsRequired = true,
    Arity = ArgumentArity.ExactlyOne
};

var language = new Option<GameLanguage>(
    new[] { "--lang", "-l" }, "Language to operate on")
{
    IsRequired = true,
    Arity = ArgumentArity.ExactlyOne
};

var outFile = new Option<FileInfo>(
    new[] { "--out", "-o" }, "Output file")
{
    IsRequired = true,
    Arity = ArgumentArity.ExactlyOne
};

var outDir = new Option<DirectoryInfo>(
    new[] { "--out", "-o" }, "Output directory")
{
    IsRequired = true,
    Arity = ArgumentArity.ExactlyOne
};

rootCmd.AddGlobalOption(ryuBasePath);

{
    var path = new Argument<string>("path", "The path of the file in the archive to extract");
    var outLoc = new Argument<string>("to", "The location to write the file");

    var cmd = new Command("extract", "Extracts a single file from the RomFS.")
    {
        xciFile, path, outLoc
    };

    var exec = ExtractFile.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile, path, outLoc);
    rootCmd.Add(cmd);
}

{
    var cmd = new Command("extract-script")
    {
        xciFile, language, outFile
    };

    var exec = ExtractScript.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile, language, outFile);
    rootCmd.Add(cmd);
}

{
    var csv = new Option<FileInfo>(
        new[] { "--csv" }, "Script CSV")
    {
        IsRequired = true,
        Arity = ArgumentArity.ExactlyOne
    };

    var autoReplaceAboveScore = new Option<int>(
        new[] { "--replace-above" }, () => 95, "Fuzzy match score to accept as a match");

    var cmd = new Command("repack-script")
    {
        xciFile, language, outDir, csv, autoReplaceAboveScore
    };

    var exec = RepackScript.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile, language, csv, autoReplaceAboveScore, outDir);
    rootCmd.Add(cmd);
}

{
    var cmd = new Command("unpack-hd")
    {
        xciFile,
    };

    var exec = UnpackHD.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile);
    rootCmd.Add(cmd);
}

return await rootCmd.InvokeAsync(args);
