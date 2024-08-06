using System.IO;
using LibHac.Common;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.FsSystem;
using System;
using MahoyoHDRepack.ScriptText.DeepLuna;
using System.Text.Json;
using MahoyoHDRepack.ScriptText;
using CommunityToolkit.Diagnostics;
using System.Text;
using System.Collections.Generic;
using Syroot.NintenTools.NSW.Bntx;
using Ryujinx.Graphics.Texture;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Advanced;
using MahoyoHDRepack.Utility;
using System.Buffers;

namespace MahoyoHDRepack.Verbs;

internal sealed class CompleteTsukiReLayeredFS
{
    public static void Run(
        string? ryuBase,
        FileInfo xciFile,
        GameLanguage secondLang,
        DirectoryInfo tsukihimatesDir,
        FileInfo fontInfoJson,
        FileInfo? tsukihimeNsp,
        FileInfo? tsukihimatesNsp,
        DirectoryInfo outDir,
        bool invertMzx
    )
    {
        MzxFile.DefaultInvert = invertMzx;
        Common.InitRyujinx(ryuBase, out var horizon, out var vfs);

        // attempt to mount the XCI file
        using var xciHandle = File.OpenHandle(xciFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        using var xciStorage = new RandomAccessStorage(xciHandle);

        using var rawRomfs = new SharedRef<IFileSystem>(XciHelpers.MountXci(xciStorage, vfs, xciFile.Name));
        using var outRomfs = new SharedRef<IFileSystem>(new LocalFileSystem(outDir.FullName));
        using var romfs = new WriteOverlayFileSystem(rawRomfs, outRomfs);

        var textProcessor = new DeepLunaTextProcessor();

        // first pass: inject main script
        InjectMainScript(outRomfs.Get, romfs, tsukihimatesDir, secondLang, textProcessor);

        // second pass: inject SYSMES_TEXT (and the rest of allui)
        InjectAllui(outRomfs.Get, romfs, tsukihimatesDir, secondLang, textProcessor);

        // once we've loaded all text, dump the fontinfo json
        Console.WriteLine($"Writing font info to {fontInfoJson}");
        using (var stream = File.Create(fontInfoJson.FullName))
        {
            JsonSerializer.Serialize(stream, textProcessor.GetFontInfoModel(), FontInfoJsonContext.Default.FontInfo);
        }

        // the last thing that needs to be done is to extract the movies that are used by Tsukihimates
        if (tsukihimeNsp is not null && tsukihimatesNsp is not null)
        {
            using var tsukiHandle = File.OpenHandle(tsukihimeNsp.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            using var tsukiStorage = new RandomAccessStorage(tsukiHandle);
            using var nspHandle = File.OpenHandle(tsukihimatesNsp.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            using var nspStorage = new RandomAccessStorage(nspHandle);

            using var tsukihimatesRomfs = XciHelpers.MountXci(tsukiStorage, vfs, tsukihimeNsp.Name, nspStorage);

            CopyMovies(tsukihimatesRomfs, romfs, secondLang);
        }
        else
        {
            Console.WriteLine($"WARNING: Cannot fully build the LayeredFS patch; no Tsukihimates patch found!");
        }
    }

    private static void InjectMainScript(IFileSystem outRomfs, IFileSystem romfs, DirectoryInfo tsukihimatesDir, GameLanguage targetLang, DeepLunaTextProcessor processor)
    {
        // first, delete any existing files from the out overlay, to ensure that we're reading from the game correctly
        using var scriptTextPath = new LibHac.Fs.Path();
        scriptTextPath.Initialize("/script_text.mrg".ToU8Span()).ThrowIfFailure();
        _ = outRomfs.DeleteFile(scriptTextPath); // note: Failures are OK, if it doesn't exist, all is well

        // we use separate databases for system text and main script
        var db = new DeepLunaDatabase();

        // load database from the script/ subfolder
        var scriptDir = Path.Combine(tsukihimatesDir.FullName, "script");
        foreach (var file in Directory.EnumerateFiles(scriptDir, "*.txt", SearchOption.AllDirectories))
        {
            DeepLunaParser.Parse(
                db, processor,
                Path.GetRelativePath(scriptDir, file),
                File.ReadAllText(file));
        }

        // we will also load the system strings, because there *does* seem to be a bit of overlap, but we won't warn if they're unused
        DeepLunaParser.Parse(
            db, processor,
            "system_strings/sysmes_text.en",
            File.ReadAllText(Path.Combine(tsukihimatesDir.FullName, "system_strings", "sysmes_text.en")),
            doNotWarnIfUnused: true);

        Console.WriteLine($"Loaded deepLuna script with {db.Count} lines");

        RepackScriptDeepLuna.RepackScriptText(targetLang, romfs, db, out var inserted, out var failed);

        // print unused
        var unused = 0;
        foreach (var line in db.UnusedLines)
        {
            RepackScriptDeepLuna.PrintUnusedLine(line);
            unused++;
        }

        Console.WriteLine($"Injected {inserted} lines into the main script text, with {failed} failures, not using {unused}.");
    }

    private static void InjectAllui(IFileSystem outRomfs, IFileSystem romfs, DirectoryInfo tsukihimatesDir, GameLanguage targetLanguage, DeepLunaTextProcessor textProcessor)
    {
        // first, delete any existing files from the out overlay, to ensure that we're reading from the game correctly
        using var alluiPath = new LibHac.Fs.Path();
        alluiPath.Initialize("/w_allui.mrg".ToU8Span()).ThrowIfFailure();
        _ = outRomfs.DeleteFile(alluiPath); // note: Failures are OK, if it doesn't exist, all is well
        alluiPath.Initialize("/w_allui.hed".ToU8Span()).ThrowIfFailure();
        _ = outRomfs.DeleteFile(alluiPath); // note: Failures are OK, if it doesn't exist, all is well
        alluiPath.Initialize("/w_allui.nam".ToU8Span()).ThrowIfFailure();
        _ = outRomfs.DeleteFile(alluiPath); // note: Failures are OK, if it doesn't exist, all is well
        alluiPath.InitializeWithNormalization("/w_allui".ToU8Span().Value).ThrowIfFailure();
        alluiPath.Normalize(default).ThrowIfFailure();

        // open allui
        MrgFileSystem.Read(romfs, alluiPath, out var outAllui).ThrowIfFailure();
        Helpers.Assert(outAllui is not null);
        using var allui = outAllui;

        // inject SYSMES_TEXT_ML.DAT
        InjectSysmesText(targetLanguage, tsukihimatesDir, textProcessor, allui);

        // inject the allui images
        InjectAlluiImages(targetLanguage, tsukihimatesDir, allui);

        // flush the filesystem
        allui.Flush().ThrowIfFailure();
    }

    private static void InjectSysmesText(GameLanguage targetLanguage, DirectoryInfo tsukihimatesDir, DeepLunaTextProcessor textProcessor, MrgFileSystem allui)
    {
        // now reload system strings
        var db = new DeepLunaDatabase();
        DeepLunaParser.Parse(
            db, textProcessor,
            "system_strings/sysmes_text.en",
            File.ReadAllText(Path.Combine(tsukihimatesDir.FullName, "system_strings", "sysmes_text.en")));

        using UniqueRef<IFile> uniqSysmesTxt = default;
        allui.OpenFile(ref uniqSysmesTxt.Ref, "/SYSMES_TEXT_ML.DAT".ToU8Span(), LibHac.Fs.OpenMode.ReadWrite).ThrowIfFailure();

        var smText = SysmesText.ReadFromFile(uniqSysmesTxt.Get);

        // insert the text
        var jpLang = smText.GetForLanguage(GameLanguage.JP);
        Helpers.Assert(jpLang is not null);
        var targetLang = smText.GetForLanguage(targetLanguage);
        if (targetLang is null) ThrowHelper.ThrowInvalidOperationException("Target language is not supported");

        var inserted = 0;
        var failed = 0;
        for (var i = 0; i < jpLang.Length; i++)
        {
            // note: we never want to match against offset
            var jpSpan = jpLang[i].AsSpan();
            if (jpSpan is [(byte)'j', (byte)'a'])
            {
                // string 0 in SYSMES_TEXT is used for suffixes of various files.
                // By forcing it from `jp` to `en`, we prevent the engine from loading many English-languag assets, including fonts.
                continue;
            }

            if (db.TryLookupLine(jpSpan, -1, out var line))
            {
                if (line.Translated is not null)
                {
                    targetLang[i] = [.. Encoding.UTF8.GetBytes(line.Translated)];
                }

                line.Used = true;
                inserted++;
            }
            else
            {
                RepackScriptDeepLuna.PrintMissingLine(Encoding.UTF8.GetString(targetLang[i].AsSpan()), jpSpan);
                failed++;
            }
        }

        var unused = 0;
        foreach (var line in db.UnusedLines)
        {
            RepackScriptDeepLuna.PrintUnusedLine(line);
            unused++;
        }

        smText.WriteToFile(uniqSysmesTxt.Get);
        uniqSysmesTxt.Get.Flush().ThrowIfFailure();

        Console.WriteLine($"Injected {inserted} lines into SYSMES_TEXT_ML.DAT, with {failed} failures, not using {unused}.");
    }

    private static void InjectAlluiImages(GameLanguage targetLanguage, DirectoryInfo tsukihimatesDir, MrgFileSystem allui)
    {
        const string NameReplaceUpper = "_JA";
        var targetLangUpper = targetLanguage switch
        {
            GameLanguage.JP => "_JA",
            GameLanguage.EN => "_EN",
            GameLanguage.ZC => "_ZC",
            GameLanguage.ZT => "_ZT",
            GameLanguage.KO => ThrowHelper.ThrowInvalidOperationException<string>(),
            _ => ThrowHelper.ThrowInvalidOperationException<string>(),
        };

        var basedir = Path.Combine(tsukihimatesDir.FullName, "images", "en_user_interface");
        foreach (var candidateDir in Directory.EnumerateDirectories(basedir, "*", SearchOption.TopDirectoryOnly))
        {
            var candidateName = Path.GetFileName(candidateDir);
            var targetName = candidateName;
            var targetNameClean = targetName;

            if (targetName.EndsWith(NameReplaceUpper, StringComparison.Ordinal))
            {
                targetNameClean = targetName[..^NameReplaceUpper.Length];
                targetName = targetNameClean + targetLangUpper;
            }

            targetName = $"/{targetName}.NXGZ";

            // open the target file
            using UniqueRef<IFile> targetFile = default;
            allui.OpenFile(ref targetFile.Ref, targetName.ToU8Span(), LibHac.Fs.OpenMode.ReadWrite).ThrowIfFailure();

            // get the decompressed version of the file
            var decompressedTarget = FileScanner.TryGetDecompressedFile(targetFile.Get);

            // make sure this is the BNTX we expect
            Helpers.Assert(FileScanner.ProbeForFileType(decompressedTarget) is KnownFileTypes.Bntx);

            // pass off to the inner function
            InjectIntoAlluiBntx(targetLanguage, decompressedTarget, candidateDir, targetNameClean);

            // then flush out the data
            decompressedTarget.Flush().ThrowIfFailure();
            targetFile.Get.Flush().ThrowIfFailure();
        }
    }

    private static readonly Dictionary<string, (string FromFile, string IntoName)[]> injectAlluiFileListOverride = new()
    {
        // TODO: dynamically select inject target suffix based on target language
        ["CONF_PARTS"] = [
            ("btn_default", "btn_default"), // for CONF_PARTS, we only want to inject btn_default. Well, we'd like to be able to inject conf_name, but the layout is different now...
        ],
        ["MENU_PARTS"] = [
            ("flow_phasetitle", "flow_phasetitle"),
            ("system_phasetitle_ja", "system_phasetitle"),
            ("flow_thumb_all_nx", "flow_thumb_all"),
        ],
        ["SAVE_PARTS"] = [
            ("savetitle", "savetitle")
        ],
        // TODO: fix the title to not be the dumb EN title
        ["TITLE_PARTS"] = [
            ("caution(UpRGB)(scale)(x1.500000)", "caution"),
            ("caution1_1920x1080", "caution1"),
        ]
    };

    private static unsafe void InjectIntoAlluiBntx(GameLanguage targetLanguage, IFile targetFile, string fromPath, string nameClean)
    {
        Console.WriteLine($"{fromPath} -> allui/{nameClean}");

        var shiftJis = Encoding.GetEncoding("shift-jis");

        // load the bntx
        var bntx = new BntxFile(targetFile.AsStream(), shiftJis);

#if DEBUG
        for (var i = 0; i < bntx.TextureDict.Count; i++)
        {
            var texName = bntx.TextureDict[i];
            Console.WriteLine($"    {i}: {texName}");
        }
#endif

        const string RemoveLangSuffix = "_ja";
        var langSuffix = targetLanguage switch
        {
            GameLanguage.JP => "_ja",
            GameLanguage.EN => "_en",
            GameLanguage.ZC => "_zc",
            GameLanguage.ZT => "_zt",
            GameLanguage.KO => "_ko",
            _ => throw new InvalidOperationException()
        };

        // TODO: automate more of this, instead of *just* deferring to the hardcoded lists
        if (injectAlluiFileListOverride.TryGetValue(nameClean, out var filesList))
        {
            foreach (var (fromFile, intoName) in filesList)
            {
                var fromFilePath = Path.Combine(fromPath, fromFile + ".png");

                var texId = bntx.TextureDict.IndexOf(intoName);
                if (texId < 0)
                {
                    texId = bntx.TextureDict.IndexOf(intoName + langSuffix);
                }

                if (texId < 0)
                {
                    Console.WriteLine($"WRN: Cannot find texture item {intoName} or {intoName + langSuffix}");
                    continue;
                }

                var tex = bntx.Textures[texId];
                Console.WriteLine($"  {fromFile}->{tex.Name}");
                InjectIntoTex(tex, fromFilePath);
            }
        }
        else
        {
            // scan for candidates ourselves
            foreach (var filename in Directory.EnumerateFiles(fromPath, "*.png", SearchOption.TopDirectoryOnly))
            {
                var basename = Path.GetFileNameWithoutExtension(filename);
                var injectName = basename;

                // try scan for exact match
                var texId = bntx.TextureDict.IndexOf(injectName);
                if (texId < 0)
                {
                    // if no match, try basename without _ja suffix
                    if (basename.EndsWith(RemoveLangSuffix, StringComparison.Ordinal))
                    {
                        injectName = injectName[..^RemoveLangSuffix.Length];
                        texId = bntx.TextureDict.IndexOf(injectName);
                    }
                }

                if (texId < 0)
                {
                    // if still no match, try with the language suffix
                    injectName += langSuffix;
                    texId = bntx.TextureDict.IndexOf(injectName);
                }

                if (texId < 0)
                {
                    // if STILL no match, report an error
                    Console.WriteLine($"WRN: Could not find injection match for {basename}");
                    continue;
                }

                var tex = bntx.Textures[texId];
                Console.WriteLine($"  {basename}->{tex.Name}");
                InjectIntoTex(tex, filename);
            }
        }

        // note: we use skipFlush because the BNTX writier does a *lot* of flushing, which in turn goes through our compression logic
        // over and over again
        targetFile.SetSize(0).ThrowIfFailure(); // clear the file first
        using var bntxSaveStream = new ResizingStorageStream(targetFile.AsStorage(), skipFlush: true);

        // saving for some ungodly reason writes console output, so suppress that
        var origOut = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);
            bntx.Save(bntxSaveStream, shiftJis);
        }
        finally
        {
            Console.SetOut(origOut);
        }

        bntxSaveStream.ForceFlush(); // once we've finished, actually force a flush through

#if DEBUG
        using var dbgFile = File.Create($"{nameClean}.bntx");
        targetFile.GetSize(out var finalFileSize).ThrowIfFailure();
        dbgFile.SetLength(finalFileSize);
        using var dbgIFile = dbgFile.AsStorage();
        targetFile.AsStorage().CopyTo(dbgIFile);
#endif
    }

    private static unsafe void InjectIntoTex(Texture tex, string fromFilePath)
    {
        var format = tex.Format;
        Helpers.Assert(format is Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC7_SRGB);
        Helpers.Assert(tex.Depth == 1);
        Helpers.Assert(tex.Dim is Syroot.NintenTools.NSW.Bntx.GFX.Dim.Dim2D);
        Helpers.Assert(tex is
        {
            ChannelAlpha: Syroot.NintenTools.NSW.Bntx.GFX.ChannelType.Alpha,
            ChannelBlue: Syroot.NintenTools.NSW.Bntx.GFX.ChannelType.Blue,
            ChannelGreen: Syroot.NintenTools.NSW.Bntx.GFX.ChannelType.Green,
            ChannelRed: Syroot.NintenTools.NSW.Bntx.GFX.ChannelType.Red,
        });

        var tileMode = tex.TileMode;
        Helpers.Assert(tileMode is Syroot.NintenTools.NSW.Bntx.GFX.TileMode.Default);

        /*
        var rawData = tex.TextureData.Single().Single();

        var sizeInfo = SizeCalculator.GetBlockLinearTextureSize(
            (int)tex.Width, (int)tex.Height, (int)tex.Depth,
            levels: 1,
            layers: 1,
            blockWidth: 4,
            blockHeight: 4,
            bytesPerPixel: 16,
            gobBlocksInY: 1 << (int)tex.BlockHeightLog2,
            gobBlocksInZ: 1,
            gobBlocksInTileX: 1);

        using var linearData = LayoutConverter.ConvertBlockLinearToLinear(
            (int)tex.Width, (int)tex.Height, (int)tex.Depth,
            sliceDepth: (int)tex.Depth, // ?
            levels: 1,
            layers: 1,
            blockWidth: 4,
            blockHeight: 4,
            bytesPerPixel: 16,
            gobBlocksInY: 1 << (int)tex.BlockHeightLog2,
            gobBlocksInZ: 1,
            gobBlocksInTileX: 1,
            sizeInfo: sizeInfo,
            rawData);

        using var decodedData = BCnDecoder.DecodeBC7(linearData.Memory.Span,
            (int)tex.Width, (int)tex.Height, (int)tex.Depth,
            1, 1);

        var img = Image.WrapMemory<Rgba32>(decodedData, (int)tex.Width, (int)tex.Height);
        img.SaveAsPng($"{intoName}.png");
        */

        var imgToImport = Image.Load(fromFilePath).CloneAs<Rgba32>();
        var pixelMem = imgToImport.GetPixelMemoryGroup();
        var pixelData = new byte[pixelMem.TotalLength * sizeof(Rgba32)];
        imgToImport.CopyPixelDataTo(pixelData);
        var bc7Encoded = BCnEncoder.EncodeBC7(pixelData,
                        imgToImport.Width, imgToImport.Height, 1, levels: 1, layers: 1);
        var replacedSizeInfo = SizeCalculator.GetBlockLinearTextureSize(
            imgToImport.Width, imgToImport.Height, 1,
            levels: 1,
            layers: 1,
            blockWidth: 4,
            blockHeight: 4,
            bytesPerPixel: 16, // actually bytes per block
            gobBlocksInY: 1 << (int)tex.BlockHeightLog2, // TODO: should this be changed, or left?
            gobBlocksInZ: 1,
            gobBlocksInTileX: 1);
        // de-linearize
        var resultData = new byte[replacedSizeInfo.TotalSize];
        _ = LayoutConverter.ConvertLinearToBlockLinear(resultData,
            imgToImport.Width, imgToImport.Height, 1,
            sliceDepth: 1, levels: 1, layers: 1,
            blockWidth: 4,
            blockHeight: 4,
            bytesPerPixel: 16, // actually bytes per block
            gobBlocksInY: 1 << (int)tex.BlockHeightLog2, // TODO: should this be changed?
            gobBlocksInZ: 1,
            gobBlocksInTileX: 1,
            replacedSizeInfo,
            bc7Encoded.Memory.Span);
        // now we can inject the data
        tex.Width = (uint)imgToImport.Width;
        tex.Height = (uint)imgToImport.Height;
        tex.Dim = Syroot.NintenTools.NSW.Bntx.GFX.Dim.Dim2D;
        tex.TextureData[0][0] = resultData;
        tex.ImageSize = (uint)resultData.Length;
    }

    private static void CopyMovies(IFileSystem srcFs, IFileSystem dstFs, GameLanguage targetLanguage)
    {
        ReadOnlySpan<string> copyRename = [
            "op1_arc",
            "op2_ciel",
            "phasetitle_*",
            "prologue",
        ];

        ReadOnlySpan<string> copyRaw = [
            // note: we want to copy the endings over as well, because they contain subtitles
            "ed01_arc",
            "ed02_ciel",
            "ed03_ciel",

            // TM_CI to include the Tsukihimates logo at startup
            "TM_CI",
        ];

        // First, lets do the raw copies
        foreach (var name in copyRaw)
        {
            var fullPath = $"/movie/{name}.mp4";

            using UniqueRef<IFile> srcFile = default;
            using UniqueRef<IFile> dstFile = default;

            Console.WriteLine(fullPath);

            srcFs.OpenFile(ref srcFile.Ref, fullPath.ToU8String(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();
            dstFs.OpenFile(ref dstFile.Ref, fullPath.ToU8String(), LibHac.Fs.OpenMode.Write).ThrowIfFailure();

            srcFile.Get.GetSize(out var srcSize).ThrowIfFailure();
            dstFile.Get.SetSize(srcSize).ThrowIfFailure();
            srcFile.Get.CopyTo(dstFile.Get);
        }

        var langSuffix = targetLanguage switch
        {
            GameLanguage.JP => "",
            GameLanguage.EN => "_en",
            GameLanguage.ZC => "_zc",
            GameLanguage.ZT => "_zt",
            GameLanguage.KO => "_ko",
            _ => "_unk"
        };

        // Next, copyRename
        foreach (var name in copyRename)
        {
            foreach (var file in srcFs.EnumerateEntries("/movie/", $"{name}.mp4", SearchOptions.Default))
            {
                if (file.Type is not LibHac.Fs.DirectoryEntryType.File) continue;

                var dstName = $"{Path.GetFileNameWithoutExtension(file.Name)}{langSuffix}{Path.GetExtension(file.Name)}";
                var dstPath = $"/movie/{dstName}";

                using UniqueRef<IFile> srcFile = default;
                using UniqueRef<IFile> dstFile = default;

                Console.WriteLine($"{file.FullPath} -> {dstPath}");

                srcFs.OpenFile(ref srcFile.Ref, file.FullPath.ToU8String(), LibHac.Fs.OpenMode.Read).ThrowIfFailure();
                dstFs.OpenFile(ref dstFile.Ref, dstPath.ToU8String(), LibHac.Fs.OpenMode.Write).ThrowIfFailure();

                srcFile.Get.GetSize(out var srcSize).ThrowIfFailure();
                dstFile.Get.SetSize(srcSize).ThrowIfFailure();
                srcFile.Get.CopyTo(dstFile.Get);
            }
        }
    }
}
