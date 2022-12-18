using System;
using System.IO;
using System.Threading.Tasks;
using LibHac;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;

namespace MahoyoHDRepack;

internal static class UnpackHD
{
    public static async Task<int> Run(
        string? ryuBase,
        FileInfo xciFile)
    {
        // init Ryujinx
        Ryujinx.Common.Configuration.AppDataManager.Initialize(ryuBase);

        var horizonConfig = new HorizonConfiguration();
        var horizon = new LibHac.Horizon(horizonConfig);
        var horizonClient = horizon.CreateHorizonClient();
        var vfs = VirtualFileSystem.CreateInstance();

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        var romfs = XciHelpers.MountXci(xciStorage, vfs);

        // we've now mounted the ROMFS and have access to the files inside

        foreach (var file in romfs.EnumerateEntries())
        {
            Console.WriteLine(file.FullPath);
        }


        return 0;
    }
}
