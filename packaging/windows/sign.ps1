# Signs files with Azure Artifact Signing (Trusted Signing), using signtool and Microsoft's client library from NuGet.
#   sign.ps1 -Path <file or folder>...   folders mean every .exe and .dll in them
# -TokenFile names a file holding an access token for https://codesigning.azure.net (az account get-access-token);
# without it the library uses the Azure login of this machine. On Windows on Arm the library needs the x64 .NET runtime
# in C:\Program Files\dotnet\x64.
param(
    [Parameter(Mandatory)][string[]]$Path,
    [string]$TokenFile,
    [string]$Endpoint = $env:SIGN_ENDPOINT,
    [string]$Account = $env:SIGN_ACCOUNT,
    [string]$CertificateProfile = $env:SIGN_PROFILE
)
if (-not ($Endpoint -and $Account -and $CertificateProfile)) { throw 'give -Endpoint, -Account and -CertificateProfile, or SIGN_ENDPOINT, SIGN_ACCOUNT and SIGN_PROFILE' }
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$D = if ($env:DEPS_ROOT) { $env:DEPS_ROOT } else { Join-Path (Split-Path $Root -Parent) 'mRemoteMac-deps-win' }
$ClientVersion = '1.0.146'

$client = "$D\artifact-signing-$ClientVersion"
if (-not (Test-Path "$client\bin\x64\Azure.CodeSigning.Dlib.dll")) {
    New-Item -ItemType Directory -Force $D | Out-Null
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$ClientVersion/microsoft.artifactsigning.client.$ClientVersion.nupkg" -OutFile "$client.zip"
    Expand-Archive -Force "$client.zip" $client
    Remove-Item "$client.zip"
}
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (-not $signtool) { throw 'signtool from the Windows SDK was not found' }

if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $env:DOTNET_ROOT_X64 = "$env:ProgramFiles\dotnet\x64" } # or the x64 signtool aborts loading the library

# a library its vendor already signed keeps that signature
$files = @(foreach ($item in $Path) {
    if (Test-Path $item -PathType Container) {
        Get-ChildItem $item -File | Where-Object { $_.Extension -in '.exe', '.dll' -and (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' } | ForEach-Object FullName
    }
    else { (Resolve-Path $item).Path }
})
if (-not $files) { throw 'nothing to sign' }

$metadata = [ordered]@{ Endpoint = $Endpoint; CodeSigningAccountName = $Account; CertificateProfileName = $CertificateProfile }
if ($TokenFile) { $metadata.AccessToken = (Get-Content -Raw $TokenFile).Trim() }
$metadataFile = Join-Path ([IO.Path]::GetTempPath()) "signing-$([guid]::NewGuid()).json"
try {
    $metadata | ConvertTo-Json | Set-Content -Encoding ascii $metadataFile
    & $signtool sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /d mRemoteMac /du https://github.com/PollySec/mremotemac `
        /dlib "$client\bin\x64\Azure.CodeSigning.Dlib.dll" /dmdf $metadataFile $files
    if ($LASTEXITCODE) { throw "signtool failed ($LASTEXITCODE)" }
}
finally { Remove-Item $metadataFile -ErrorAction SilentlyContinue }

foreach ($file in $files) {
    & $signtool verify /pa /q $file
    if ($LASTEXITCODE) { throw "the signature of $file does not verify" }
}
$signer = (Get-AuthenticodeSignature $files[0]).SignerCertificate.Subject
Write-Host "signed $($files.Count) file(s), signer: $signer"
