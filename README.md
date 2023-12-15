# MahoyoHDRepack

This is a tool intended to be able to extract, modify, and repack the Switch release
of Mahoyo HD.

This tool uses [Ryujinx](https://github.com/Ryujinx/Ryujinx) and
[LibHac](https://github.com/Thealexbarney/LibHac) to be able to read the game's XCI file directly, avoiding the need to fully dump the game before working on it. 

Much of the code here to extract the archives was derived from
[PS HuneX Tools](https://github.com/Hintay/PS-HuneX_Tools/) and the Tsukihime Remake
translation team's tools [deepLuna](https://github.com/Hakanaou/deepLuna) and
[Mangetsu](https://github.com/rschlaikjer/mangetsu).

## Building

First, ensure you have the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) installed.

```sh
git clone ... && pushd MahoyoHDRepack
git submodule update --init --recursive
dotnet build -c Release
```

This will place the resulting executable at `artifacts/bin/MahoyoHDRepack/Release/net8.0/`.

If you want a single self-contained native executable, you can use
```sh
dotnet publish src/MahoyoHDRepack/MahoyoHDRepack.csproj -c Release -f net8.0 -p:InvariantGlobalization=true --self-contained -r <RID>
```
replacing `<RID>` with the
[Runtime Identifier](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog) for
your current platform. For instance, you may use `win-x64` or `linux-x64`. This will place the resulting binary at `artifacts/bin/MahoyoHDRepack/Release/net8.0/<RID>/publish/`.

# Setup for use

Before this can be used, you must:

1. Set up Ryujinx with your Switch's key files ([see here](https://github.com/Ryujinx/Ryujinx/wiki/Keys))
2. Load your copy of Mahoyo HD into Ryujinx
3. Configure update files for the game, making sure the most recent update is 
   selected.

At this point, Ryujinx is configured enough for `MahoyoHDRepack` to function.

## Commands

All commands take an optional argument `--ryujinx-base` which specifies the data
directory for your Ryujinx installation. If you are not using a portable Ryujinx,
then this option can be omitted, and `MahoyoHDRepack` will use the default directory.

### `extract`

Extracts a single file out of the RomFS of the game. This will also extract files
out of (known) archives within the RomFS, and automatically decompress files which
are compressed using a known scheme.

#### Usage

```
MahoyoHDRepack extract <RomFS path> <output path>
```

#### Options

- `-x <file>`, `--xci <file>` - Specifies the XCI file of the game.
- `--raw` - Prevents the automatic decompression of compressed files.
- `--no-arc` - Prevents following paths into archives.

#### Notes

The RomFS path may specify a file within an archive in the RomFS. For self-describing
archives like MZP archives, this just allows you to write the archive (with its full
name, including extension!) as a directory. For MRG archives, which consist of a
triple of `.HED`, `.MRG`, and (optionally) `.NAM`, write only the name of the
archive *without* any extensions. It will automatically find the `HED` and `MRG`
files.

For any archive which does not include filenames (i.e. MZP and `NAM`-less MRG), the
files have names which are simply their (zero-based) index in the archive in
hexadecimal. The name may have any amount of zero-padding, and may include either
capital and lowercase letters. There are (theoretically) infinite names for each file
in the archive. When enumerated, however, the files' names are their hexadecimal
indicies, in lowercase, padded with zeros to be 16 characters long.

Example paths:
- `/allscr.mrg/3` - Extracts the 4th file from the `allscr.mrg` MZP archive
- `/allscr.mrg/2/0` - Extracts the first file from the MZP archive which is the 
  third file in `allscr.mrg`. (Note that, at the moment, this will fail without
  `--raw` because MXZ decompression is not yet implemented.)

### `extract-script`

Extracts the game script for a particular language, alongside its JP equivalent,
into a CSV file.

#### Options

- `-x <file>`, `--xci <file>` - Specifies the XCI file of the game.
- `-l <lang>`, `--lang <lang>` - Specifies the language to extract.
    - This must be one of `JP`, `EN`, `ZH1`, `ZH2`, or `KO`.
      
      `ZH1` and `ZH2` correspond to the two Chinese translations in Mahoyo HD.
      As I am not faimiliar with Chinese, I do not actually know which is which, so
      the index is the order they appear in the game files.

      `KO` refers to the Korean translation of the game, which you may note *does
      not exist*. In `script_text.mrg`, there is an extra set of files for another
      language after `ZH2`. Everywhere in the game files, the order of the languages
      is always `JP`, `EN`, `ZH1`, `ZH2`. Then, I noticed elsewhere in the files
      (though I admit, I do not remember where) that after `ZH2` came one labeled 
      `ko`, which is the ISO code for Korean. As it stands, however, translation file
      contains only U+3000 CJK Unified Ideograph Space, followed by a carriage return-newline pair for each and every line of the translation.

      `JP` and `EN` are self-explanatory.
- `-o <file>`, `--out <file>` - Specifies the output file to write the script CSV to.

#### Notes

The output CSV file has 3 columns: the original JP, the target language, and a
target language replacement. The first two columns are populated by the extractor,
and the third is expected to be populated by a translator or editor, for use with
`repack-script`.

### `repack-script`

Reads an updated script CSV and writes a Ryujinx/Atmosphere mod directory which
replaces the game script with the updated one in the CSV.

#### Options

- `-x <file>`, `--xci <file>` - Specifies the XCI file of the game.
- `-l <lang>`, `--lang <lang>` - Specifies the language to extract.
    - This must be one of `JP`, `EN`, `ZH1`, `ZH2`, or `KO`.
      Refer to the same option in `extract-script`.
- `-o <dir>`, `--out <dir>` - Specifies the output RomFS folder to write the mod
  to. This directory will be deleted *completely* before any work is done.
- `--csv <file>` - Specifies the CSV file containing the updated script to replace.
- `--replace-above <score>` (optional) - Specifies the fuzzy match score above which
  a replacement line is still considered to match, from 0 to 100. Default 95.

  This is only actually relevant if the CSV file has different JP from the game
  being patched. If a line is found which doesn't exactly match any JP lines in the
  game, a fuzzy match is performed on the pair of JP/original lang listed in the CSV
  to try to find the line to replace. This specifies the threshold for a match to be accepted.

#### Notes

The replacement first tries to replace by-index. If the CSV is kept in the same order
as it was originally dumped (and thus the same order as in the game files), then fast
comparisons will be made with each JP line to ensure it matches, and then the line
will be replaced. If a JP line *doesn't* match, it switches away from using index
matching.

This fallback first searches the entire JP script for the JP line found in the CSV.
If it finds it, then it uses that. If it does not, however, it falls back to a fuzzy
match on the original JP line and the original specified language line. 
`--replace-above` specifies the match score required to accept such a fuzzy match as 
a replacement.

# Game Format

## Languages

As discussed above, everywhere that variants based on language exist (in titlecards,
cutscenes, images, text, etc) the languages are always in the following order:
1. `JP` - Japanese
2. `EN` - English
3. `ZH1` - The first of 2 Chinese translations.
4. `ZH2` - The second of 2 Chinese translations.
5. `KO` - The (non-existant) Korean translation.
   For more detail on why I believe this to be a Korean translation, see the
   discussion of the `-l` option of `extract-script`.

## Image formats

In all of the MRG archives, images are stored in one of two forms: either as
a `.JPG` (a standard JFIF file, openable by most image viewers), or as a `.NXZ`
file. This is compressed using either GZip or Deflate, depending on whether the
header is `NXCX` or `NXGX`. See
[`src/MahoyoHDRepack/NxxFile.cs`](src/MahoyoHDRepack/NxxFile.cs) for the extractor 
implementation. THe file which is compressed seems to always be a BNTX image
container. There exist a handful of tools able to process this format, including
[@gdkchan](https://github.com/gdkchan/)'s [BnTxx](https://github.com/gdkchan/BnTxx)
and [Switch Toolbox](https://github.com/KillzXGaming/Switch-Toolbox). At some point,
I intend to implement decoding of this format in this project (likely based on or
using gdkchan's implementation) to translate it to a more commonly usable format.

## RomFS

- `romfs/` -  The RomFS directory from the XCI. This will be notated the root, a
  preceding `/` from here out.

- `/script_text.mrg` - An MZP archive containing the text for the script.

  This archive contains pairs of files. The first in each pair is a binary file
  containing 32-bit big-endian integers containing the offset from the start of
  the second file to the start of the script line. The final entry is always -1.

  The second file in each pair is a UTF-8 encoded text file. Each line is selected
  by offset according to the first file. Each line starts at the specified offset, 
  and goes until the start of the next line.

- `/allscr.mrg` - An MZP archive containing the game script.
  
  This does not contain any actual text; only the instructions used by the engine
  to display the game.

  This archive contains the following files:
  - `0` - The name of each of the script files, as 32-byte zero-padded strings.
    The names are in the same order they appear later in this archive. The format
    is notably similar to a `.NAM` file.
  - `1` - An MZP archive containing text files which appear to define the offsets
    in each script file that its labels appear. Each file corresponds to one script.
  - `2` - This is an MZP archive which seems to be garbage, or otherwise empty and
    meaningless. Perhaps this is an obfuscation technique which makes the format
    behave differently for this index?
  - `3` and onward - These are MXZ compressed scripts. `3` is the first script
    (index zero in `/allscr.mrg/0` and `/allscr.mrg/1`).

    This script language is not well understood. Mangetsu has some documentation,
    though it is poor and incomplete. (It claims that `_WKST` is a wait, though it
    seems to me to be far more likely to be a variable operation.)

    My cursory look over a few of the scripts has revealed the following (to be 
    updated as more is learned):
    - `_RTM_()` returns to a parent script. Seems to be used for the archive, which
      is handled by checking a variable and either using `_RTM_()` or a jump
      depending on its value.
    - `_IF__(var, op, val, label)` conditionally jumps to a label. Relatively
      self-explanatory.
    - `_ZYxxxxx(label)` seems to mark a label. The `xxxxx` is a (seemingly) unique
      hexadecimal number, possibly used to help with saves.
    - `_SCH2(?,img,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)` displays an image somehow.
    - `_END_()` marks the end of a script. This seems to always be present.
    - `_JUMP(number)` seems to jump to a different script.
      - I do not understand the encoding of its argument. The jump from script 0 to
        script 1 appears to be encoded as `201`.
    - `_WKST(var,op,val)` performs an operation on a variable.
      - The `eq` operation sets the value of the variable.
    - `_ZZxxxxx()` seems to mark the start (and start label) of the script? 
      Each script seems to start with an invocation with no arguments, and is always
      followed by another with an argument.

      Like `_ZY...`, the `xxxxx` is a seemingly unique hexadecimal number.
    - `_ZMxxxxx(textspec)` displays a piece of text. deepLuna searches for these,
      and seems to have some understanding of the `textspec`'s format. At the very
      least, `$xxxxxx`, where `xxxxxx` is a hexadecimal number, refers to a line in
      `script_text` by index. 

- `/allui` (`.HED`, `.MRG`, `.NAM`) - An MRG archive containing UI (and some menu)
  graphics.

- `/allpacml`, `/allpachdml` (`.HED`, `.MRG`, `.NAM`) - An MRG archive containing
  what appear to be mostly chapter title images, as well as some MZP archives with
  unknown (possibly empty) content.
  
  The `hd` variant contains HD textures, while the other archive contains files
  which seem to be nearly identical to those found in the orignal PC release.

- `/jallpac`, `/jallpachd` (`.HED`, `.MRG`, `.NAM`) - An MRG archive containing
  the majority of the images used by the game.

  The two variants are the same as `allpacml`, with `hd` containing the HD textures,
  and the non-`hd` variant containing images very close to those used in the original
  PC release.

  I do not know if the game ever actually uses the non-HD textures. If I had to
  guess, it would use them when in handheld mode, but I have not done anything to
  check this.

- `/parts` (`.HED`, `.MRG`, `.NAM`) - An MRG archive containing startup messages.

   Notably, this includes the Type-Moon logo, as well as the opening warning text in
   Japanese, but *not* the same in English, or any other language. I suspect that
   this isn't actually used in the Switch release.

- `/witch.bfsar` - A [BFSAR](https://mk8.tockdom.com/wiki/BFSAR_(File_Format))
  archive containing the audio for the game. Seemingly has the same contents as
  `/stream/`.

- `/stream/` - A folder containing Nintendo OPUS audio files. Use
  [vgmstream](https://github.com/vgmstream/vgmstream) to decode them.

  I intend on implementing decoding of these files, just as I do for BNTX images.
