# Toggles the bar's density mid-run - the same write the settings page makes -
# and photographs the top edge of the primary screen before, between, and
# after. The check the density bug demanded: switch, shoot, switch back,
# shoot, compare.
param(
    [string]$Out = "$env:TEMP\mcd-flip",
    [string]$Config = "",
    [int]$Band = 70
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\MCD.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MasterControlDock.exe'
if (-not (Test-Path $exe)) { throw "not built: $exe" }

& (Join-Path $PSScriptRoot 'close-mcd.ps1') | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

$scratch = Join-Path $env:TEMP ("mcd-flip-" + [guid]::NewGuid().ToString('N'))
$logDir = Join-Path $scratch 'MCD\logs'
$env:MCD_DATA_DIR = Join-Path $scratch 'MCD'
$env:MCD_SELFTEST = '1'
$env:MCD_SELFTEST_SECONDS = '22'
$env:MCD_SELFTEST_FLIP = 'density'

if ($Config) {
    New-Item -ItemType Directory -Force $env:MCD_DATA_DIR | Out-Null
    Copy-Item $Config (Join-Path $env:MCD_DATA_DIR 'config.json')
}

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[Dpi]::SetProcessDPIAware() | Out-Null

function Shoot([string]$name, [int]$band) {
    # The top band of the primary screen, fixed coordinates: the bar's own
    # thickness is part of what is being photographed.
    $bmp = New-Object System.Drawing.Bitmap(2560, $band)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $file = Join-Path $Out "$name.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "saved $file"
}

$p = Start-Process -FilePath $exe -PassThru

function AwaitFlip([int]$count, [int]$timeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($log -and @(Select-String -Path $log.FullName -Pattern 'selftest\.flipped' -ErrorAction SilentlyContinue).Count -ge $count) {
            return $true
        }
    }
    Write-Warning "flip $count did not announce itself"
    return $false
}

Start-Sleep -Milliseconds 3000
Shoot 'stage1-before' $Band
AwaitFlip 1 12000 | Out-Null
Start-Sleep -Milliseconds 2200   # let the rebuild and the write-back settle
Shoot 'stage2-flipped' $Band
AwaitFlip 2 12000 | Out-Null
Start-Sleep -Milliseconds 2200
Shoot 'stage3-back' $Band

try { $p.WaitForExit(12000) | Out-Null } catch {}
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

Copy-Item (Join-Path $env:MCD_DATA_DIR 'config.json') (Join-Path $Out 'config-after.json') -ErrorAction SilentlyContinue
Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
    Copy-Item -Destination (Join-Path $Out 'run.log') -ErrorAction SilentlyContinue
Select-String -Path (Join-Path $logDir '*.log') -Pattern 'selftest.flipped|dock.refitted|dock.applied|LEVEL=(ERROR|FATAL)' |
    ForEach-Object { $_.Line } | Select-Object -Last 20

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
Remove-Item Env:MCD_DATA_DIR, Env:MCD_SELFTEST, Env:MCD_SELFTEST_SECONDS, Env:MCD_SELFTEST_FLIP -ErrorAction SilentlyContinue
