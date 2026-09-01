# Photographs every bar of a scratch run, straight off the screen, and names
# each file by its edge and position. For looking at the widgets as they are
# actually drawn - the settings window's pages are photographed by pages.ps1,
# but a widget lives on the bar, and the bar is what people look at.
param(
    [string]$Out = "$env:TEMP\mcd-bars",
    [string]$Config = "",
    [int]$Seconds = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\MCD.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MasterControlDock.exe'
if (-not (Test-Path $exe)) { throw "not built: $exe" }

& (Join-Path $PSScriptRoot 'close-mcd.ps1') | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

$scratch = Join-Path $env:TEMP ("mcd-bars-" + [guid]::NewGuid().ToString('N'))
$logDir = Join-Path $scratch 'MCD\logs'
$env:MCD_DATA_DIR = Join-Path $scratch 'MCD'
$env:MCD_SELFTEST = '1'
$env:MCD_SELFTEST_SECONDS = "$Seconds"
if ($env:MCD_BAR_PAINT) { $env:MCD_PAINT = $env:MCD_BAR_PAINT }

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

$p = Start-Process -FilePath $exe -PassThru
$deadline = (Get-Date).AddSeconds(8)
$rects = @()

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
    $log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $log) { continue }
    $rects = @(Select-String -Path $log.FullName -Pattern 'appbar\.positioned edge=(\S+) rect=(\S+)' |
        ForEach-Object { $_.Matches[0].Groups[1].Value + '|' + $_.Matches[0].Groups[2].Value } |
        Select-Object -Unique)
}

Start-Sleep -Milliseconds 1200

foreach ($r in $rects) {
    $edge, $box = $r.Split('|')
    $n = $box.Split(',') | ForEach-Object { [int]$_ }
    $w = $n[2] - $n[0]; $h = $n[3] - $n[1]
    if ($w -le 0 -or $h -le 0) { continue }

    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($n[0], $n[1], 0, 0, $bmp.Size)
    $file = Join-Path $Out "$edge-$($n[0])x$($n[1]).png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "saved $file ($w x $h)"
}

try { $p.WaitForExit(($Seconds + 6) * 1000) | Out-Null } catch {}
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

# What the run wrote back - so a chain of runs can hand one run's settled
# arrangement to the next, the way a person's real sessions do.
Copy-Item (Join-Path $env:MCD_DATA_DIR 'config.json') (Join-Path $Out 'config-after.json') -ErrorAction SilentlyContinue
Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
    Copy-Item -Destination (Join-Path $Out 'run.log') -ErrorAction SilentlyContinue

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
Remove-Item Env:MCD_DATA_DIR, Env:MCD_SELFTEST, Env:MCD_SELFTEST_SECONDS, Env:MCD_PAINT -ErrorAction SilentlyContinue
