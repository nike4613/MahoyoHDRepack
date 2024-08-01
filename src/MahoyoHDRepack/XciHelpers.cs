using System.Globalization;
using CommunityToolkit.Diagnostics;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.HLE.FileSystem;
using Ryujinx.UI.App.Common;

namespace MahoyoHDRepack;

public static class XciHelpers
{
    public static IFileSystem MountXci(IStorage xciStorage, VirtualFileSystem vfs, string filename, int programIndex = 0)
    {
        Nca? mainNca = null;
        Nca? patchNca = null;

        var extension = System.IO.Path.GetExtension(filename).ToUpperInvariant();

        if (extension is ".XCI" or ".NSP" or ".PFS0")
        {
            IFileSystem pfs;

            if (extension is ".XCI")
            {
                pfs = new Xci(vfs.KeySet, xciStorage).OpenPartition(XciPartitionType.Secure);
            }
            else
            {
                var tmp = new PartitionFileSystem();
                tmp.Initialize(xciStorage).ThrowIfFailure();
                pfs = tmp;
            }

            (mainNca, patchNca, _) = ApplicationLibrary.GetGameData(vfs, pfs, programIndex);
        }
        else if (extension is ".NCA")
        {
            mainNca = new Nca(vfs.KeySet, xciStorage);
        }
        else
        {
            ThrowHelper.ThrowInvalidOperationException($"Unrecognized Switch archive extension {extension}");
        }

        Helpers.Assert(mainNca is not null);

        var (updatePatchNca, _) = ApplicationLibrary.GetGameUpdateData(vfs,
            mainNca.Header.TitleId.ToString("x16", CultureInfo.InvariantCulture), programIndex, out _);
        if (updatePatchNca is not null)
        {
            patchNca = updatePatchNca;
        }

        var index = Nca.GetSectionIndexFromType(NcaSectionType.Data, mainNca.Header.ContentType);

        return patchNca is not null
            ? mainNca.OpenFileSystemWithPatch(patchNca, index, IntegrityCheckLevel.ErrorOnInvalid)
            : mainNca.OpenFileSystem(index, IntegrityCheckLevel.ErrorOnInvalid);
    }
}
