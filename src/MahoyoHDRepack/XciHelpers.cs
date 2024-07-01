using System;
using System.Globalization;
using CommunityToolkit.Diagnostics;
using LibHac.Common;
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
        // This is largely copied from https://github.com/Ryujinx/Ryujinx/blob/b994dafe7aa8c49fe8de69b7b81401aaeeed8c59/Ryujinx.Ava/Common/ApplicationHelper.cs#L175
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

            foreach (var entry in pfs.EnumerateEntries("/", "*.nca"))
            {
                using var ncaFile = new UniqueRef<IFile>();
                pfs.OpenFile(ref ncaFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

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
