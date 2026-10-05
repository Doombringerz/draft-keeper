# Builds a single self-contained executable and zips it for a release.
#
#   pwsh tools\publish.ps1                 -> dist\DraftKeeper-<version>-win-x64.zip
#   pwsh tools\publish.ps1 -Version 0.2.0  -> stamps that version instead
#
# Self-contained so it runs without the .NET runtime installed. That costs about 70 MB
# and is the right trade for a utility someone downloads once.

param(
    [string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\DraftKeeper\DraftKeeper.csproj"
$out = Join-Path $root "dist\$Runtime"
$dist = Join-Path $root "dist"

if (-not $Version) {
    $xml = [xml](Get-Content $project)
    $Version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { $Version = "0.1.0" }
}

Write-Output "building Draft Keeper $Version for $Runtime"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

# No debug information, and neutral source paths. A debug build records the folder it
# was built in, and that folder names the account that built it.
dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:ContinuousIntegrationBuild=true `
    -p:Version=$Version `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $out "DraftKeeper.exe"
if (-not (Test-Path $exe)) { throw "expected $exe, which is not there" }

Get-ChildItem $out -Filter *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

$bytes = [IO.File]::ReadAllBytes($exe)
$texts = @([Text.Encoding]::ASCII.GetString($bytes),
           [Text.Encoding]::Unicode.GetString($bytes),
           [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1))
foreach ($local in @($root, $env:USERPROFILE, $env:USERNAME) | Where-Object { $_ }) {
    if ($texts | Where-Object { $_.Contains($local) }) { throw "the executable contains a local path or name: $local" }
}

$zip = Join-Path $dist "DraftKeeper-$Version-$Runtime.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $exe -DestinationPath $zip

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Output ""
Write-Output "exe    $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB  $exe"
Write-Output "zip    $([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB  $zip"
Write-Output "sha256 $hash"
Write-Output ""
Write-Output "Publish the hash alongside the download. Until the executable is signed,"
Write-Output "SmartScreen will warn on first run and the hash is the only way anyone can"
Write-Output "check they got what you built."
