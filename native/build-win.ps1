# Builds native\out\win-<arch>\libmrrdp.dll from the static libraries of build-deps-win.ps1.
param([ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$D = if ($env:DEPS_ROOT) { $env:DEPS_ROOT } else { Join-Path (Split-Path $Root -Parent) 'mRemoteMac-deps-win' }
. "$PSScriptRoot\vcvars-win.ps1" -Arch $Arch
$install = "$D\freerdp\install-$Arch"
$vcpkgLib = "$D\vcpkg\installed\$Arch-windows-static\lib"
$out = "$PSScriptRoot\out\win-$Arch"
$obj = "$PSScriptRoot\out\obj-win-$Arch"
New-Item -ItemType Directory -Force $out, $obj | Out-Null

# every static library FreeRDP produced: the linker only takes what is referenced
$libs = @(Get-ChildItem "$install\lib" -Filter *.lib | ForEach-Object FullName)
$libs += "$vcpkgLib\libssl.lib", "$vcpkgLib\libcrypto.lib", "$vcpkgLib\zs.lib"
$system = 'ws2_32 crypt32 secur32 iphlpapi shlwapi ole32 oleaut32 advapi32 user32 gdi32 dbghelp userenv winmm rpcrt4 shell32 ntdsapi credui bcrypt ncrypt winscard uuid version winspool'.Split(' ') | ForEach-Object { "$_.lib" }

$compile = "$obj\compile.rsp"
$link = "$obj\link.rsp"
@(
    '/nologo', '/c', '/O2', '/MT', '/W3', '/std:c17', '/utf-8', '/D_CRT_SECURE_NO_WARNINGS', '/DWIN32_LEAN_AND_MEAN',
    '/DFREERDP_EXPORTS', '/DWINPR_EXPORTS', # FreeRDP's headers have no static mode; without these they ask for its import libraries
    "/I`"$install\include\freerdp3`"", "/I`"$install\include\winpr3`"",
    "/Fo`"$obj\\`"",
    "`"$PSScriptRoot\mrrdp.c`"", "`"$PSScriptRoot\plat_win.c`""
) | Set-Content -Encoding ascii $compile
@('/nologo', '/DLL', "/OUT:`"$out\libmrrdp.dll`"", '/LTCG', '/OPT:REF', '/OPT:ICF', '/IGNORE:4006', '/IGNORE:4221',
    "`"$obj\mrrdp.obj`"", "`"$obj\plat_win.obj`"") + ($libs | ForEach-Object { "`"$_`"" }) + $system | Set-Content -Encoding ascii $link
& cl "@$compile"
if ($LASTEXITCODE -ne 0) { throw "compiling the shim failed ($LASTEXITCODE)" }
& link "@$link"
if ($LASTEXITCODE -ne 0) { throw "linking libmrrdp.dll failed ($LASTEXITCODE)" }
Remove-Item "$out\libmrrdp.exp", "$out\libmrrdp.lib" -ErrorAction SilentlyContinue
Write-Host ('{0:N0} bytes: {1}' -f (Get-Item "$out\libmrrdp.dll").Length, "$out\libmrrdp.dll")
