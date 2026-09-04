# Publish Windows-mcp as ONE self-contained exe: <repo>/bundle/WindowsMcp.exe.
# Works from any working directory. bundle/ is gitignored - binaries are never committed.
#
# IncludeNativeLibrariesForSelfExtract embeds libSkiaSharp.dll and aspnetcorev2_inprocess.dll
# (otherwise they are left loose next to the exe and the exe alone is not portable); they
# self-extract on first launch to %TEMP%\.net\WindowsMcp\<hash>\. DebugType=none drops our
# own .pdb files.
$ErrorActionPreference = 'Stop'
$repo   = Split-Path -Parent $PSScriptRoot
$bundle = Join-Path $repo 'bundle'

# Start clean so files from an earlier publish (different flags) cannot linger.
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }

dotnet publish (Join-Path $repo 'src/WindowsMcp') -c Release -o $bundle -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# libSkiaSharp.pdb is a native asset of the SkiaSharp package and survives every publish flag
# (DebugType / PublishReferencesSymbols do not touch it). Drop it so bundle/ holds exactly one file.
Remove-Item -LiteralPath (Join-Path $bundle 'libSkiaSharp.pdb') -Force -ErrorAction SilentlyContinue

Get-ChildItem -LiteralPath $bundle | Format-Table Name, Length, LastWriteTime -AutoSize
