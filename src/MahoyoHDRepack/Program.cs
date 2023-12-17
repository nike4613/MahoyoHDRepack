using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.IO;
using LibHac.FsSystem;
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
    IsRequired = false,
};

var gameDir = new Option<DirectoryInfo>(
    new[] { "--game-dir", "-d" }, "Game directory")
{
    IsRequired = false,
};

var language = new Option<GameLanguage>(
    new[] { "--lang", "-l" }, "Language to operate on")
{
    IsRequired = true,
};

var outFile = new Option<FileInfo>(
    new[] { "--out", "-o" }, "Output file")
{
    IsRequired = true,
};

var outDir = new Option<DirectoryInfo>(
    new[] { "--out", "-o" }, "Output directory")
{
    IsRequired = true,
};

rootCmd.AddGlobalOption(ryuBasePath);

{
    var path = new Argument<string>("path", "The path of the file in the archive to extract");
    var outLoc = new Argument<string>("to", "The location to write the file");
    var raw = new Option<bool>("--raw", "Do not uncompress the file if it compressed");
    var noArchive = new Option<bool>("--no-arc", "Do not treat archives as directories");

    var cmd = new Command("extract", "Extracts a single file from the RomFS.")
    {
        xciFile, path, outLoc, raw, noArchive, gameDir
    };

    cmd.SetHandler(Exec);
    rootCmd.Add(cmd);

    void Exec(InvocationContext context)
    {
        var xciFileInfo = context.ParseResult.GetValueForOption(xciFile);

        if (xciFileInfo is not null)
        {
            var ryuBase = context.ParseResult.GetValueForOption(ryuBasePath);

            Common.InitRyujinx(ryuBase, out _, out var vfs);

            // attempt to mount the XCI file
            using var xciHandle = File.OpenHandle(xciFileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            using var xciStorage = new RandomAccessStorage(xciHandle);

            using var romfs = XciHelpers.MountXci(xciStorage, vfs);

            ExtractFile.Run(romfs,
                context.ParseResult.GetValueForArgument(path), context.ParseResult.GetValueForArgument(outLoc),
                context.ParseResult.GetValueForOption(raw), context.ParseResult.GetValueForOption(noArchive));
        }
        else
        {
            var dir = context.ParseResult.GetValueForOption(gameDir);
            if (dir is null)
            {
                context.ExitCode = -1;
                context.Console.Error.WriteLine("If --xci is not specified, --game-dir must be");
                return;
            }

            using var rootfs = new LocalFileSystem(dir.FullName);

            ExtractFile.Run(rootfs,
                context.ParseResult.GetValueForArgument(path), context.ParseResult.GetValueForArgument(outLoc),
                context.ParseResult.GetValueForOption(raw), context.ParseResult.GetValueForOption(noArchive));
        }
    }
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

return await rootCmd.InvokeAsync(args).ConfigureAwait(false);
