# Photographs every page of the settings window from a run of its own.
#
# A visual change is verified by looking at the window it changed. The window
# that gets looked at must not be the one on somebody's desk while they are
# working, so this starts one copy against a scratch settings root, walks it
# through the pages, photographs each and lets it stop itself.
#
# The program announces every page it reaches in its log; this waits for that
# line rather than guessing at a cadence, so a slow machine cannot hand back a
# set of photographs of the wrong pages.
param(
    [string]$Out = "$env:TEMP\mcd-pages",
    [string]$Pages = 'docks,icons,appearance,sensors,about',
    [int]$Seconds = 16
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\MCD.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MasterControlDock.exe'
if (-not (Test-Path $exe)) { throw "not built: $exe" }

New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // The biggest visible window this process owns. The docks are its other
  // windows and they are a few dozen pixels tall; the settings window is the
  // only one that could be mistaken for a page.
  public static IntPtr Biggest(uint want) {
    IntPtr best = IntPtr.Zero; long area = 0;
    EnumWindows(delegate(IntPtr h, IntPtr _) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want || !IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      long a = (long)(r.R - r.L) * (r.B - r.T);
      if (a > area) { area = a; best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
"@
[Win]::SetProcessDPIAware() | Out-Null

$scratch = Join-Path $env:TEMP ("mcd-pages-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
$logDir = Join-Path $scratch 'MCD\logs'

$env:MCD_DATA_DIR = Join-Path $scratch 'MCD'
$env:MCD_SELFTEST = '1'
$env:MCD_SELFTEST_SECONDS = "$Seconds"
$env:MCD_SELFTEST_PAGE = $Pages

$p = Start-Process -FilePath $exe -PassThru
$wanted = $Pages.Split(',')
$seen = 0
$deadline = (Get-Date).AddSeconds($Seconds + 4)

while ($seen -lt $wanted.Count -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { continue }

    $lines = @(Select-String -Path $log.FullName -Pattern 'selftest\.page (\S+)' -ErrorAction SilentlyContinue)
    while ($seen -lt $lines.Count) {
        $page = $lines[$seen].Matches[0].Groups[1].Value
        Start-Sleep -Milliseconds 450   # let the page finish laying itself out

        $h = [Win]::Biggest($p.Id)
        if ($h -ne [IntPtr]::Zero) {
            $r = New-Object Win+RECT
            [Win]::GetWindowRect($h, [ref]$r) | Out-Null
            $w = $r.R - $r.L; $ht = $r.B - $r.T
            if ($w -gt 0 -and $ht -gt 0) {
                $bmp = New-Object System.Drawing.Bitmap($w, $ht)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
                $file = Join-Path $Out "$page.png"
                $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
                "saved $file  ($w x $ht)"
            }
        } else { Write-Warning "no window when $page was shown" }

        $seen++
    }
}

try { $p.WaitForExit(($Seconds + 8) * 1000) | Out-Null } catch {}
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

$errors = @(Select-String -Path (Join-Path $logDir '*.log') -Pattern 'LEVEL=(ERROR|FATAL)' -ErrorAction SilentlyContinue)
if ($errors) { $errors | ForEach-Object { "LOG " + $_.Line } }

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
Remove-Item Env:MCD_DATA_DIR, Env:MCD_SELFTEST, Env:MCD_SELFTEST_SECONDS, Env:MCD_SELFTEST_PAGE -ErrorAction SilentlyContinue
