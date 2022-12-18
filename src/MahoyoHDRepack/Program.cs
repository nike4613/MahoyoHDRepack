using System;
using System.CommandLine;
using System.IO;
using MahoyoHDRepack;

var rootCmd = new RootCommand();

var ryuBasePath = new Option<string?>(
    "--ryujinx-base", "The Ryujinx base path.")
{
    IsRequired = false,
    Arity = ArgumentArity.ZeroOrOne
};

rootCmd.AddGlobalOption(ryuBasePath);

{
    var xciFile = new Argument<FileInfo>("Game XCI");

    var cmd = new Command("unpack-hd")
    {
        xciFile,
    };

    var exec = UnpackHD.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile);
    rootCmd.Add(cmd);
}

return await rootCmd.InvokeAsync(args);
