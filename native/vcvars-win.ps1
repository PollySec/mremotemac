# Dot-source with -Arch x64|arm64: loads the MSVC environment that builds for that architecture on this machine.
param([Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Arch)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the C++ tools was not found' }
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'amd64' }
$target = if ($Arch -eq 'x64') { 'amd64' } else { 'arm64' }
$spec = if ($hostArch -eq $target) { $target } else { "${hostArch}_$target" }
cmd /c "`"$vs\VC\Auxiliary\Build\vcvarsall.bat`" $spec >nul && set" | ForEach-Object {
    if ($_ -match '^([^=]+)=(.*)$') { Set-Item "env:$($Matches[1])" $Matches[2] }
}
Remove-Item env:Platform -ErrorAction SilentlyContinue # MSBuild would take it for the project's platform and build elsewhere
