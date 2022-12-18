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

        var programIndex = 0;

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        // This is largely copied from https://github.com/Ryujinx/Ryujinx/blob/b994dafe7aa8c49fe8de69b7b81401aaeeed8c59/Ryujinx.Ava/Common/ApplicationHelper.cs#L175
        Nca? mainNca = null;
        Nca? patchNca = null;

        var pfs = new Xci(vfs.KeySet, xciStorage).OpenPartition(XciPartitionType.Secure);

        foreach (var entry in pfs.EnumerateEntries("/", "*.nca"))
        {
            using var ncaFile = new UniqueRef<IFile>();
            pfs.OpenFile(ref ncaFile.Ref(), entry.FullPath.ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

            var nca = new Nca(vfs.KeySet, ncaFile.Get.AsStorage());
            if (nca.Header.ContentType == NcaContentType.Program)
            {
                var dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);
                if (nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                {
                    patchNca = nca;
                }
                else
                {
                    mainNca = nca;
                }
            }
        }

        Helpers.Assert(mainNca is not null);

        var (updatePatchNca, _) = ApplicationLoader.GetGameUpdateData(vfs, mainNca.Header.TitleId.ToString("x16"), programIndex, out _);
        if (updatePatchNca is not null)
        {
            patchNca = updatePatchNca;
        }

        var index = Nca.GetSectionIndexFromType(NcaSectionType.Data, mainNca.Header.ContentType);

        var romfs = patchNca is not null
            ? mainNca.OpenFileSystemWithPatch(patchNca, index, IntegrityCheckLevel.ErrorOnInvalid)
            : mainNca.OpenFileSystem(index, IntegrityCheckLevel.ErrorOnInvalid);

        // we've now mounted the ROMFS and have access to the files inside

        foreach (var file in romfs.EnumerateEntries())
        {
            Console.WriteLine(file.FullPath);
        }


        return 0;
    }
}
