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

rootCmd.AddGlobalOption(ryuBasePath);

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
    var cmd = new Command("unpack-hd")
    {
        xciFile,
    };

    var exec = UnpackHD.Run;
    cmd.SetHandler(exec, ryuBasePath, xciFile);
    rootCmd.Add(cmd);
}

return await rootCmd.InvokeAsync(args);
