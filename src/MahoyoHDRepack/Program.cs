using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.IO;
using System.Text;
using LibHac.Fs.Fsa;
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
    ["--xci", "-x"], "Game XCI file")
{
    IsRequired = false,
};

var invertMzx = new Option<bool>(
    ["--invert-mzx", "-m"], "Use MZX decompressor \"invert\" mode")
{
    IsRequired = false,
};

var gameDir = new Option<DirectoryInfo>(
    ["--game-dir", "-d"], "Game directory")
{
    IsRequired = false,
};

var language = new Option<GameLanguage>(
    ["--lang", "-l"], "Language to operate on")
{
    IsRequired = true,
};

var outFile = new Option<FileInfo>(
    ["--out", "-o"], "Output file")
{
    IsRequired = true,
};

var outDir = new Option<DirectoryInfo>(
    ["--out", "-o"], "Output directory")
{
    IsRequired = true,
};

var doNotProcessKinds = new Option<KnownFileTypes[]>(
    ["--no-process", "-e"], "File types to not process")
{

};

var inParallel = new Option<bool>(
    ["--parallel", "-p"], "Extract in parallel")
{

};

rootCmd.AddGlobalOption(ryuBasePath);

void ExecWithRootFs(InvocationContext context, Action<IFileSystem> action)
{
    var xciFileInfo = context.ParseResult.GetValueForOption(xciFile);

    if (xciFileInfo is not null)
    {
        var ryuBase = context.ParseResult.GetValueForOption(ryuBasePath);

        Common.InitRyujinx(ryuBase, out _, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        using var romfs = XciHelpers.MountXci(xciStorage, vfs, xciFileInfo.Name);

        action(romfs);
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

        action(rootfs);
    }
}

var raw = new Option<bool>("--raw", "Do not uncompress the file if it compressed");
var noArchive = new Option<bool>("--no-arc", "Do not treat archives as directories");
{
    var path = new Argument<string>("path", "The path of the file in the archive to extract");
    var outLoc = new Argument<string>("to", "The location to write the file");

    var cmd = new Command("extract", "Extracts a single file from the RomFS.")
    {
        xciFile, path, outLoc, raw, noArchive, gameDir, invertMzx
    };

    cmd.SetHandler(Exec);
    rootCmd.Add(cmd);

    void Exec(InvocationContext context) => ExecWithRootFs(context, rootfs =>
    {
        ExtractFile.Run(rootfs,
            context.ParseResult.GetValueForArgument(path), context.ParseResult.GetValueForArgument(outLoc),
            context.ParseResult.GetValueForOption(raw), context.ParseResult.GetValueForOption(noArchive),
            context.ParseResult.GetValueForOption(invertMzx));
    });
}

{
    var targetDir = new Argument<DirectoryInfo>("targetDir", "The location to extract everything to");

    var cmd = new Command("extract-all", "Extracts the entire virtual filesystem to a target directory")
    {
        xciFile, targetDir, raw, noArchive, gameDir, doNotProcessKinds, invertMzx, inParallel,
    };

    cmd.SetHandler(Exec);
    rootCmd.Add(cmd);

    void Exec(InvocationContext context) => ExecWithRootFs(context, rootfs =>
    {
        ExtractAll.Run(rootfs,
            context.ParseResult.GetValueForArgument(targetDir),
            context.ParseResult.GetValueForOption(raw),
            context.ParseResult.GetValueForOption(noArchive),
            context.ParseResult.GetValueForOption(doNotProcessKinds) ?? Array.Empty<KnownFileTypes>(),
            context.ParseResult.GetValueForOption(invertMzx),
            context.ParseResult.GetValueForOption(inParallel));
    });
}

{
    var cmd = new Command("extract-script")
    {
        xciFile, language, outFile, invertMzx
    };

    var exec = ExtractScript.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile, language, outFile, invertMzx);
    rootCmd.Add(cmd);
}

{
    var csv = new Option<FileInfo>(
        ["--csv"], "Script CSV")
    {
        IsRequired = true,
        Arity = ArgumentArity.ExactlyOne
    };

    var autoReplaceAboveScore = new Option<int>(
        ["--replace-above"], () => 95, "Fuzzy match score to accept as a match");

    var cmd = new Command("repack-script")
    {
        xciFile, language, outDir, csv, autoReplaceAboveScore, invertMzx
    };

    var exec = RepackScript.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile, language, csv, autoReplaceAboveScore, outDir, invertMzx);
    rootCmd.Add(cmd);
}
{
    var lunaFiles = new Option<string[]>(
        ["-d", "--luna"], "deepLuna translation files")
    {
        IsRequired = true,
        Arity = ArgumentArity.OneOrMore
    };


    var cmd = new Command("repack-script-deepluna")
    {
        xciFile, language, outDir, lunaFiles, invertMzx,
    };

    var exec = RepackScriptDeepLuna.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile, language, lunaFiles, outDir, invertMzx);
    rootCmd.Add(cmd);
}

{
    var replacementText = new Option<FileInfo>(
        ["--replacement-text", "-r"], "Replacement text")
    {
        IsRequired = true,
        Arity = ArgumentArity.ExactlyOne
    };

    var cmd = new Command("repack-script-pc")
    {
        xciFile, ryuBasePath, gameDir, outDir, replacementText
    };

    cmd.SetHandler(Exec);
    rootCmd.Add(cmd);

    void Exec(InvocationContext context) => ExecWithRootFs(context, rootfs =>
    {
        RepackScriptPc.Run(rootfs,
            context.ParseResult.GetValueForOption(replacementText)!,
            context.ParseResult.GetValueForOption(outDir)!);
    });
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

Console.OutputEncoding = Encoding.UTF8;
return await rootCmd.InvokeAsync(args).ConfigureAwait(false);
