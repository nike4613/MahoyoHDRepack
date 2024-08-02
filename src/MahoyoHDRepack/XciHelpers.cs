using System.Globalization;
using System.Linq;
using CommunityToolkit.Diagnostics;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Processes.Extensions;

namespace MahoyoHDRepack;

public static class XciHelpers
{
    public static IFileSystem MountXci(IStorage xciStorage, VirtualFileSystem vfs, string filename, IStorage? explicitUpdatePatch = null, int programIndex = 0)
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

            var appCmn = pfs.GetContentData(LibHac.Ncm.ContentMetaType.Application, vfs, IntegrityCheckLevel.ErrorOnInvalid).Values.Single();
            mainNca = appCmn.GetNcaByType(vfs.KeySet, LibHac.Ncm.ContentType.Program);

            var patchCmn = pfs.GetContentData(LibHac.Ncm.ContentMetaType.Patch, vfs, IntegrityCheckLevel.ErrorOnInvalid).Values.SingleOrDefault();
            patchNca = patchCmn?.GetNcaByType(vfs.KeySet, LibHac.Ncm.ContentType.Program);
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

        Nca? updatePatchNca;

        if (explicitUpdatePatch is null)
        {
            (updatePatchNca, _) = mainNca.GetUpdateData(vfs, IntegrityCheckLevel.ErrorOnInvalid, 0, out _);
        }
        else
        {
            var pfs = new PartitionFileSystem();
            pfs.Initialize(explicitUpdatePatch).ThrowIfFailure();

            var patchCmn = pfs.GetContentData(LibHac.Ncm.ContentMetaType.Patch, vfs, IntegrityCheckLevel.ErrorOnInvalid).Values.SingleOrDefault();
            updatePatchNca = patchCmn?.GetNcaByType(vfs.KeySet, LibHac.Ncm.ContentType.Program);
        }

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
