using LibHac;
using Ryujinx.HLE.FileSystem;

namespace MahoyoHDRepack.Verbs;

internal static class Common
{
    public static void InitRyujinx(
        string? ryuBase,
        out Horizon horizon,
        out VirtualFileSystem vfs
    )
    {
        Ryujinx.Common.Configuration.AppDataManager.Initialize(ryuBase);

        var horizonConfig = new HorizonConfiguration();
        horizon = new Horizon(horizonConfig);
        vfs = VirtualFileSystem.CreateInstance();
    }
}
