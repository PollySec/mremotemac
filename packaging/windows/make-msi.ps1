# Builds mRemoteMac for Windows in two steps, because the files inside an MSI must be signed before they go in:
#   make-msi.ps1 -Arch x64 -Step Publish   NativeAOT build into <Dist>\win-x64\app; sign those files now (sign.ps1)
#   make-msi.ps1 -Arch x64 -Step Package   wraps the app folder into <Dist>\mRemoteMac-<version>-windows-x64.msi; sign that too
# Needs native\build-deps-win.ps1 (once) and .NET 10. -Version overrides the version MinVer reads from the git tag.
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [ValidateSet('Publish', 'Package')][string]$Step = 'Publish',
    [string]$Dist,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Dist) { $Dist = "$Root\dist" }
$Rid = "win-$Arch"
$App = "$Dist\$Rid\app"
$Proj = "$Root\src\MRemoteMac\MRemoteMac.csproj"
$versionProps = @(if ($Version) { "-p:MinVerVersionOverride=$Version" })

if ($Step -eq 'Publish') {
    & "$Root\native\build-win.ps1" -Arch $Arch
    if ($LASTEXITCODE) { throw 'native shim failed' }
    . "$Root\native\vcvars-win.ps1" -Arch $Arch # the NativeAOT linker
    Write-Host "==> dotnet publish ($Rid, NativeAOT)"
    $publish = "$Dist\$Rid\publish"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    & dotnet publish $Proj -c Release -r $Rid --self-contained true -p:PublishAot=true -p:PublishReadyToRun=false -p:DebugType=none @versionProps -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }

    if (Test-Path $App) { Remove-Item -Recurse -Force $App }
    New-Item -ItemType Directory -Force $App | Out-Null
    Get-ChildItem $publish -File | Where-Object Extension -In '.exe', '.dll' | Copy-Item -Destination $App

    # Every library must come from the bundle or from Windows. Anything else (the Visual C++ runtime in particular, which
    # this machine has because it builds) loads here and nowhere else.
    $bundled = (Get-ChildItem $App -File).Name
    $leaks = foreach ($file in Get-ChildItem $App -File) {
        foreach ($line in & dumpbin /nologo /dependents $file.FullName) {
            if ($line -match '^\s+(\S+\.(dll|DLL))\s*$') {
                $dep = $Matches[1]
                if ($bundled -contains $dep) { continue }
                if ($dep -match '^(vcruntime|msvcp|concrt|vccorlib|ucrtbased)' -or
                    (-not ($dep -like 'api-ms-*' -or $dep -like 'ext-ms-*') -and -not (Test-Path "$env:SystemRoot\System32\$dep"))) {
                    "  $($file.Name) -> $dep"
                }
            }
        }
    }
    if ($leaks) { throw "libraries outside the app and Windows:`n$($leaks -join "`n")" }
    Get-ChildItem $App -File | ForEach-Object { Write-Host ('{0,12:N0}  {1}' -f $_.Length, $_.Name) }
    Write-Host "Done: $App. Sign these files, then run -Step Package."
    return
}

if (-not $Version) {
    $Version = (& dotnet msbuild $Proj -t:MinVer -getProperty:MinVerVersion -p:Configuration=Release | Select-Object -Last 1).Trim()
}
$msiVersion = ($Version -split '[-+]')[0] # MSI versions are numeric
$msi = "$Dist\mRemoteMac-$Version-windows-$Arch.msi"

$tools = "$Dist\wix-tools"
$env:DOTNET_ROLL_FORWARD = 'LatestMajor' # WiX 5 is built for an older runtime
$env:WIX_EXTENSIONS = "$tools\extensions"
if (-not (Test-Path "$tools\wix.exe")) {
    & dotnet tool install wix --version 5.0.2 --tool-path $tools
    if ($LASTEXITCODE) { throw 'installing WiX failed' }
}
& "$tools\wix.exe" extension add WixToolset.UI.wixext/5.0.2
if ($LASTEXITCODE) { throw 'adding the WiX UI extension failed' }

# the license page shows the GPL as it stands in the repository
$rtf = "$Dist\$Rid\license.rtf"
$text = (Get-Content -Raw "$Root\LICENSE") -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}' -replace "`r?`n", "\par`n"
Set-Content -Encoding ascii $rtf "{\rtf1\ansi\deff0{\fonttbl{\f0\fmodern Consolas;}}\f0\fs16 $text}"

Write-Host "==> wix build ($msiVersion, $Arch)"
& "$tools\wix.exe" build "$PSScriptRoot\mRemoteMac.wxs" -arch $Arch -ext WixToolset.UI.wixext -d "Version=$msiVersion" -d "LicenseRtf=$rtf" `
    -bindpath "app=$App" -o $msi
if ($LASTEXITCODE) { throw 'wix build failed' }
Remove-Item "$($msi -replace '\.msi$', '.wixpdb')" -ErrorAction SilentlyContinue
Write-Host ('{0:N0} bytes: {1}' -f (Get-Item $msi).Length, $msi)
