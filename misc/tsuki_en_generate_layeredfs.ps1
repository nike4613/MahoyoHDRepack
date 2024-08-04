#Requires -PSEdition Core
#Requires -Version 7

<#

#>

param (
    [Parameter(Mandatory=$true)]
    [string]
    # The XCI or NSP of the Tsukihime EN base game. Ryujinx should be configured with updates for the game.
    $Xci,
    
    [Parameter(Mandatory=$true)]
    [string]
    # Tsukihimates translation repo directory.
    $Tsukihimates,

    [string]
    # An XCI or NSP of the base JP release of Tsukihime
    $TsukiJP = $null,

    [string]
    # The NSP of the official Tsukihimates patch
    $TsukiPatch = $null,
    
    [Parameter(Mandatory=$true)]
    [string]
    # The directory to output the LayeredFS patch to.
    $PatchDir
)

Set-StrictMode -Version 3.0;
$ErrorActionPreference = "Stop";
$ConfirmPreference = "None";
trap { Write-Error $_ -ErrorAction Continue; exit 1 }

$TFM = "net8.0";

function Materialize-Path {
    param(
        [Parameter(Mandatory=$true)]
        $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path;
    } else {
        return Join-Path (Get-Location) $Path;
    }
}

# resolve paths relative to the current directory, before moving ourselves to the repo root
$Xci = Materialize-Path $Xci;
$Tsukihimates = Materialize-Path $Tsukihimates;
$PatchDir = Materialize-Path $PatchDir;

# look for the commands we need
$dotnet = Get-Command dotnet -ErrorAction Continue;
$fontforge = Get-Command fontforge -ErrorAction Continue;

if ($null -eq $dotnet) {
    Write-Error "dotnet not found. Please make sure it is on your PATH. .NET 8 is required.";
}
if ($null -eq $fontforge) {
    Write-Error "fontforge not found. Please make sure it is on your PATH.";
}

# now move into the repo root to do our thing
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..");
Push-Location $RepoRoot

try {

Remove-Item -Recurse -Force $PatchDir | Out-Null;
New-Item -ItemType Directory $PatchDir | Out-Null;

$tmp = Join-Path $PatchDir ".tmp";
New-Item -ItemType Directory $tmp | Out-Null;

$fontinfo = Join-Path $tmp "fontinfo.json";

$romfs = (Join-Path $PatchDir "romfs");

# First, run MahoyoHDRepack to generate most of the RomFS
$romfsCompleteArgs = @();
if ($null -ne $TsukiJP) {
    $romfsCompleteArgs += @("--tsukihime", $TsukiJP);
}
if ($null -ne $TsukiPatch) {
    $romfsCompleteArgs += @("--tsukihimates-nsp", $TsukiPatch);
}
&$dotnet run --project "src/MahoyoHDRepack/MahoyoHDRepack.csproj" -c Release -f $TFM -- `
    rebuild-tsukire-en --xci $Xci `
    -l EN -o $romfs `
    --font-info $fontinfo `
    --tsukihimates-dir $Tsukihimates `
    @romfsCompleteArgs;
if (-not $?) { Write-Error "Failed to generate RomFS!"; }

# Then, lets extract and fix our fonts
$fontPaths = @(
    @("en/HelveticaNeueLTGEO-55Roman.otf", 0),
    @("en2/DemosNextPro-Regular.otf", 1)
    #@("en3/FOT-SeuratPro-M.otf", 2)
);
$antiquaInsert = "misc/tex-gyre-chorus.regular.otf";
$antiquaScale = 1.5;
$antiquaWeightAdjust = 0;

foreach ($fonta in $fontPaths) {
    $font = $fonta[0];
    $mode = $fonta[1];
    # make sure the directory is present in the tmp dir
    New-Item -ItemType Directory -Force (Join-Path $tmp (Split-Path -Parent $font)) | Out-Null;
    # extract the font
    &$dotnet run --project "src/MahoyoHDRepack/MahoyoHDRepack.csproj" -c Release -f $TFM --no-build -- `
        extract --xci $Xci "/$font" (Join-Path $tmp $font);
    if (-not $?) { Write-Error "Failed to extract font $font (mode $mode)!"; }
    # make sure the directory is present in the overlayfs
    New-Item -ItemType Directory -Force (Join-Path $romfs (Split-Path -Parent $font)) | Out-Null;
    # use fontforge to update the fonts, and write them into the romfs
    &$fontforge -quiet -lang=py -script "misc/generate_font_variations.py" `
        (Join-Path $tmp $font) `
        $fontinfo `
        (Join-Path $romfs $font) `
        $mode `
        $antiquaInsert $antiquaScale $antiquaWeightAdjust;
    # Note about these last 
    if (-not $?) { Write-Error "Failed to patch font $font!"; }
}

# Finally, copy in the exefs patches
$exefs = (Join-Path $PatchDir "exefs");
New-Item -ItemType Directory $exefs | Out-Null;

Get-ChildItem "misc/" -Filter "*.pchtxt" | Copy-Item -Destination $exefs | Out-Null;

# Clean up temp dir
Remove-Item -Recurse -Force $tmp;

Write-Host "Done!";

} finally {
    Pop-Location
}