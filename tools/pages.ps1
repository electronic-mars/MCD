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
    [string]$Pages = 'docks,widgets,pins,presets,appearance,general,sensors,about',
    [int]$Seconds = 22
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\MCD.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MasterControlDock.exe'
if (-not (Test-Path $exe)) { throw "not built: $exe" }

New-Item -ItemType Directory -Force $Out | Out-Null
Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

# A second copy only signals the first and stands down, so it would have no
# window to photograph. The one already running is asked to stop first.
& (Join-Path $PSScriptRoot 'close-mcd.ps1')

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  // Asks the window to draw itself into a bitmap. Grabbing the screen at the
  // window's coordinates photographs whatever happens to be in front of it -
  // which on somebody's desk is their browser, and the shot is then a picture
  // of their browser. PW_RENDERFULLCONTENT (2) is what makes this work for a
  // composed window rather than returning black.
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
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
$tries = 0
$deadline = (Get-Date).AddSeconds($Seconds + 4)

while ($seen -lt $wanted.Count -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { continue }

    $lines = @(Select-String -Path $log.FullName -Pattern 'selftest\.page (\S+)' -ErrorAction SilentlyContinue)
    while ($seen -lt $lines.Count) {
        $page = $lines[$seen].Matches[0].Groups[1].Value
        if ($tries -eq 0) { Start-Sleep -Milliseconds 450 }  # let it lay itself out

        $h = [Win]::Biggest($p.Id)
        if ($h -ne [IntPtr]::Zero) {
            $r = New-Object Win+RECT
            [Win]::GetWindowRect($h, [ref]$r) | Out-Null
            $w = $r.R - $r.L; $ht = $r.B - $r.T
            if ($w -gt 0 -and $ht -gt 0) {
                $bmp = New-Object System.Drawing.Bitmap($w, $ht)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $dc = $g.GetHdc()
                $ok = [Win]::PrintWindow($h, $dc, 2)
                $g.ReleaseHdc($dc)

                if (-not $ok) {
                    # Some windows refuse; the screen is the fallback, and the
                    # shot is then only as good as what is in front of it.
                    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
                    Write-Warning "$page was photographed off the screen, not out of the window"
                }

                # A window that has been told to change page can be
                # photographed before it has drawn the new one, and hands back
                # a sheet of black that looks exactly like a page that failed
                # to render. Counting the colours in a coarse grid tells the
                # two apart: a real page has dozens, an unpainted one has one.
                $seenColours = @{}
                for ($sx = 20; $sx -lt $w - 20; $sx += 40) {
                    for ($sy = 40; $sy -lt $ht - 20; $sy += 40) {
                        $seenColours[$bmp.GetPixel($sx, $sy).ToArgb()] = $true
                    }
                }

                # Waiting is bounded well inside the 1400 ms the program
                # spends on each page: wait past that and the next page is on
                # screen, and the photograph is filed under the wrong name -
                # which is worse than a black one, because it looks right.
                if ($seenColours.Count -lt 4 -and $tries -lt 2) {
                    $g.Dispose(); $bmp.Dispose()
                    $tries++
                    Write-Warning "$page had not drawn itself yet; waiting"
                    Start-Sleep -Milliseconds 300
                    continue
                }

                if ($seenColours.Count -lt 4) {
                    Write-Warning "$page is blank in the photograph, not just late"
                }

                $file = Join-Path $Out "$page.png"
                $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
                "saved $file  ($w x $ht)"
                $tries = 0
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
