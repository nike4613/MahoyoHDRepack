using System.IO;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;

namespace MahoyoHDRepack.Verbs
{
    internal class RepackScriptPc
    {
        public static void Run(
            IFileSystem gameFs,
            FileInfo newTextPath,
            DirectoryInfo outDir
        )
        {
            using var sharedGameFs = new SharedRef<IFileSystem>(gameFs);
            using var outRomfs = new SharedRef<IFileSystem>(new LocalFileSystem(outDir.FullName));
            using var romfs = new WriteOverlayFileSystem(sharedGameFs, outRomfs);

            using var uniqScriptTextFile = new UniqueRef<IFile>();
            romfs.OpenFile(ref uniqScriptTextFile.Ref, "/data00200.hfa".ToU8Span(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();

            using UniqueRef<HfaFileSystem> hfaFs = default;
            HfaFileSystem.Read(ref hfaFs.Ref, uniqScriptTextFile.Get.AsStorage()).ThrowIfFailure();

            using UniqueRef<IFile> scriptTextFile = default;
            hfaFs.Get.OpenFile(ref scriptTextFile.Ref, "/script_text_en.ctd".ToU8Span(), LibHac.Fs.OpenMode.Write).ThrowIfFailure();

            var newText = File.ReadAllBytes(newTextPath.FullName);
            using var newTextStorage = MemoryStorage.Adopt(newText);

            LenZuCompressorFile.CompressTo(newTextStorage, scriptTextFile.Get.AsStorage()).ThrowIfFailure();

            scriptTextFile.Get.Flush().ThrowIfFailure();
            hfaFs.Get.Flush().ThrowIfFailure();
            uniqScriptTextFile.Get.Flush().ThrowIfFailure();
            romfs.Flush();
        }
    }
}
