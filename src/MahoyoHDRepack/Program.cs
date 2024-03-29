﻿using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.IO;
using System.Security.Principal;
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

var doNotProcessKinds = new Option<KnownFileTypes[]>(
    new[] { "--no-process", "-e" }, "File types to not process")
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

        using var romfs = XciHelpers.MountXci(xciStorage, vfs);

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
        xciFile, path, outLoc, raw, noArchive, gameDir
    };

    cmd.SetHandler(Exec);
    rootCmd.Add(cmd);

    void Exec(InvocationContext context) => ExecWithRootFs(context, rootfs =>
    {
        ExtractFile.Run(rootfs,
            context.ParseResult.GetValueForArgument(path), context.ParseResult.GetValueForArgument(outLoc),
            context.ParseResult.GetValueForOption(raw), context.ParseResult.GetValueForOption(noArchive));
    });
}

{
    var targetDir = new Argument<DirectoryInfo>("targetDir", "The location to extract everything to");

    var cmd = new Command("extract-all", "Extracts the entire virtual filesystem to a target directory")
    {
        xciFile, targetDir, raw, noArchive, gameDir, doNotProcessKinds,
    };

    cmd.SetHandler(Exec);
    rootCmd.Add(cmd);

    void Exec(InvocationContext context) => ExecWithRootFs(context, rootfs =>
    {
        ExtractAll.Run(rootfs,
            context.ParseResult.GetValueForArgument(targetDir),
            context.ParseResult.GetValueForOption(raw),
            context.ParseResult.GetValueForOption(noArchive),
            context.ParseResult.GetValueForOption(doNotProcessKinds) ?? Array.Empty<KnownFileTypes>());
    });
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
