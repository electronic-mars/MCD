<#
.SYNOPSIS
Publishes Master Control Dock and packs it into an MSIX for the Store.

.DESCRIPTION
The package is built with makeappx from the self-contained publish output -
no packaging project, no Visual Studio workload. The result is unsigned: the
Store signs what it publishes, and a local install of an unsigned package is
done with Add-AppxPackage after enabling developer mode, or not at all.

Identity comes from Partner Center once the app name is reserved there; until
then the defaults below produce a package that packs and uploads but will be
renamed by the Store submission.

.EXAMPLE
tools/pack-msix.ps1
tools/pack-msix.ps1 -IdentityName "12345ElectronicMars.MasterControlDock" -Publisher "CN=ABCDEF01-2345-..."
#>
[CmdletBinding()]
param(
    [string]$IdentityName = 'ElectronicMars.MasterControlDock',
    [string]$Publisher = 'CN=00000000-0000-0000-0000-000000000000',
    [string]$Architecture = 'x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---- the one version number
[xml]$props = Get-Content (Join-Path $root 'Directory.Version.props')
$version = ($props.Project.PropertyGroup.McdVersion | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'McdVersion not found in Directory.Version.props' }
Write-Host "packing version $version ($Architecture)"

# ---- publish
if (-not $SkipPublish) {
    & dotnet publish (Join-Path $root 'src\MCD.App\MCD.App.csproj') `
        --configuration Release `
        --runtime "win-$Architecture" `
        --self-contained true `
        --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE" }
}

$published = Get-ChildItem -Path (Join-Path $root 'src\MCD.App\bin') -Filter 'MasterControlDock.exe' -Recurse |
    Where-Object { $_.FullName -like '*Release*' -and $_.FullName -like "*win-$Architecture*" -and $_.FullName -like '*publish*' } |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $published) { throw 'no publish output found' }
$publishDir = $published.DirectoryName
Write-Host "publish output: $publishDir"

# ---- stage
$staging = Join-Path $root "dist\staging-$Architecture"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Copy-Item -Path (Join-Path $publishDir '*') -Destination $staging -Recurse
Get-ChildItem $staging -Filter *.pdb | Remove-Item

# The manifest, with the real identity and version patched in.
$manifest = Get-Content (Join-Path $root 'packaging\Package.appxmanifest') -Raw
$manifest = $manifest -replace 'Name="ElectronicMars\.MasterControlDock"', "Name=`"$IdentityName`""
$manifest = $manifest -replace 'Publisher="CN=00000000-0000-0000-0000-000000000000"', "Publisher=`"$Publisher`""
$manifest = $manifest -replace 'Version="[\d\.]+"', "Version=`"$version.0`""
$manifest = $manifest -replace 'ProcessorArchitecture="x64"', "ProcessorArchitecture=`"$Architecture`""
Set-Content -Path (Join-Path $staging 'AppxManifest.xml') -Value $manifest -Encoding utf8

# ---- makeappx, from the newest installed Windows SDK
$kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeappx = Get-ChildItem -Path $kits -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\x64\*' } |
    Sort-Object FullName | Select-Object -Last 1
if (-not $makeappx) { throw "makeappx.exe not found under $kits - install a Windows 10/11 SDK" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$package = Join-Path $dist "MasterControlDock_${version}_$Architecture.msix"
if (Test-Path $package) { Remove-Item $package -Force }

& $makeappx.FullName pack /o /d $staging /p $package | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE" }

Remove-Item $staging -Recurse -Force
Write-Host "packed: $package"
Write-Host 'Unsigned - upload it to Partner Center, which signs on publish.'
