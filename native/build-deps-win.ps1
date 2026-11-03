# Builds OpenSSL and zlib (vcpkg) and FreeRDP 3 as static libraries with the static C runtime, one architecture at a
# time, into ..\mRemoteMac-deps-win. build-win.ps1 links them into a single libmrrdp.dll, so the app needs neither the
# Visual C++ redistributable nor several C runtimes sharing FreeRDP's allocations.
param([ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
$D = if ($env:DEPS_ROOT) { $env:DEPS_ROOT } else { Join-Path (Split-Path $Root -Parent) 'mRemoteMac-deps-win' }
$VcpkgRef = '2026.10.16'
$FreeRdpVer = '3.32.1'
New-Item -ItemType Directory -Force $D | Out-Null
. "$PSScriptRoot\vcvars-win.ps1" -Arch $Arch

function Invoke-Native([string]$Exe) {
    & $Exe @args
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed with exit code $LASTEXITCODE" }
}

$vcpkg = "$D\vcpkg"
if (-not (Test-Path "$vcpkg\vcpkg.exe")) {
    Write-Host "==> vcpkg $VcpkgRef"
    Invoke-Native git -c advice.detachedHead=false clone --quiet --depth 1 --branch $VcpkgRef https://github.com/microsoft/vcpkg.git $vcpkg
    Invoke-Native "$vcpkg\bootstrap-vcpkg.bat" -disableMetrics
}
$triplet = "$Arch-windows-static"
if (-not (Test-Path "$vcpkg\installed\$triplet\lib\libssl.lib")) {
    Write-Host "==> openssl, zlib ($triplet)"
    Invoke-Native "$vcpkg\vcpkg.exe" install openssl zlib --triplet $triplet --disable-metrics
}

$src = "$D\freerdp\src"
if (-not (Test-Path "$src\.git")) {
    Write-Host "==> freerdp $FreeRdpVer"
    Invoke-Native git -c advice.detachedHead=false -c core.autocrlf=false clone --quiet --depth 1 --branch $FreeRdpVer https://github.com/FreeRDP/FreeRDP.git $src
    # upstream's local file list does not work with native Windows paths and folders (see the patch)
    Invoke-Native git -C $src -c core.autocrlf=false apply "$PSScriptRoot\freerdp-windows.patch"
}
$install = "$D\freerdp\install-$Arch"
if (-not (Test-Path "$install\lib\freerdp-client3.lib")) {
    Write-Host "==> build freerdp ($Arch)"
    $build = "$D\freerdp\build-$Arch"
    Invoke-Native cmake -G Ninja -S $src -B $build -DCMAKE_BUILD_TYPE=Release "-DCMAKE_INSTALL_PREFIX=$install" `
        "-DCMAKE_TOOLCHAIN_FILE=$vcpkg\scripts\buildsystems\vcpkg.cmake" "-DVCPKG_TARGET_TRIPLET=$triplet" `
        -DBUILD_SHARED_LIBS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DCMAKE_WINDOWS_VERSION=WIN10 `
        -DOPENSSL_USE_STATIC_LIBS=ON -DZLIB_USE_STATIC_LIBS=ON -DWITH_INTERNAL_RC4=ON -DWITH_INTERNAL_MD4=ON -DWITH_INTERNAL_MD5=ON `
        -DWITH_SERVER=OFF -DWITH_SAMPLE=OFF -DWITH_CLIENT=OFF -DWITH_CLIENT_SDL=OFF -DWITH_X11=OFF -DWITH_MANPAGES=OFF `
        -DBUILD_TESTING=OFF -DWITH_WINMM=ON -DWITH_MEDIA_FOUNDATION=OFF -DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_OPENH264=OFF -DWITH_WEBVIEW=OFF `
        -DCHANNEL_URBDRC=OFF -DWITH_PKCS11=OFF -DWITH_CCACHE=OFF -DWITH_CLANG_FORMAT=OFF -DWITH_PLATFORM_SERVER=OFF `
        -DWITH_WINPR_TOOLS=OFF -DWITH_JSON_DISABLED=ON -DCMAKE_VERBOSE_MAKEFILE=OFF -DWITH_VERBOSE_WINPR_ASSERT=OFF
    Invoke-Native cmake --build $build
    Invoke-Native cmake --install $build
    # two helper libraries FreeRDP does not install; after this the build tree is only weight (CI caches this folder)
    Get-ChildItem $build -Recurse -Filter *-common.lib | Copy-Item -Destination "$install\lib"
    Remove-Item -Recurse -Force $build
    'buildtrees', 'packages', 'downloads' | ForEach-Object { Remove-Item -Recurse -Force "$vcpkg\$_" -ErrorAction SilentlyContinue }
}
Write-Host "deps ready in $D ($Arch)"
