<#
.SYNOPSIS
Builds Master Control Dock and installs it for the current user, with a shortcut
on the desktop.

.DESCRIPTION
A stand-in for the real installer, which arrives with the MSIX. It exists so the
program can be used and tested the way it will actually be used - started from a
shortcut, not from a path pasted into a terminal.

Everything goes under the user's own profile. Nothing is written outside it, and
no administrator rights are needed.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\MasterControlDock'
$exeName = 'MasterControlDock.exe'

# A running copy holds its own files open, and killing it would leave the AppBar
# registered and the desktop work area short.
& (Join-Path $PSScriptRoot 'close-mcd.ps1')

if (-not $SkipBuild) {
    Write-Host "publishing ($Configuration)..."
    & dotnet publish (Join-Path $root 'src\MCD.App\MCD.App.csproj') `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --nologo `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE" }
}

$published = Get-ChildItem -Path (Join-Path $root 'src\MCD.App\bin') -Filter $exeName -Recurse |
    Where-Object { $_.FullName -like '*publish*' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $published) { throw 'no publish output was found' }
$source = $published.Directory.FullName

Write-Host "installing to $target"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Copy-Item (Join-Path $source '*') $target -Recurse -Force

$exe = Join-Path $target $exeName
$icon = Join-Path $target 'Assets\icon.ico'

$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Master Control Dock.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$icon,0"
$link.Description = 'A dock for the edge of your screen'
$link.Save()

# Also in the Start menu, so it can be found by typing its name.
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Master Control Dock.lnk'
$link = $shell.CreateShortcut($startMenu)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$icon,0"
$link.Description = 'A dock for the edge of your screen'
$link.Save()

$size = [math]::Round(((Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)

Write-Host ''
Write-Host "installed:  $exe"
Write-Host "size:       $size MB"
Write-Host "desktop:    $shortcut"
Write-Host "start menu: $startMenu"
Write-Host ''
Write-Host 'Start it from the desktop shortcut. A bar appears on the edge of'
Write-Host 'every screen. Right-click an empty part of one to open the settings,'
Write-Host 'where the Exit button is. Starting the shortcut again while it is'
Write-Host 'already running opens the settings too.'
