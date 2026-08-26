<#
.SYNOPSIS
Runs the built program for a few seconds and checks the log for signs of life.

.DESCRIPTION
Unit tests cannot see the order services are constructed in, a XAML resource
that failed to merge, a missing asset, or a start that hangs. This can, and it
costs twenty seconds.

The program is never killed. It is asked to end itself through MCD_SELFTEST,
because taskkill skips ProcessExit, and skipping ProcessExit leaves the AppBar
registered - the desktop work area of the build agent would stay shrunk for
every job after this one.
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Seconds = 12
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Exe) {
    $found = Get-ChildItem -Path (Join-Path $root 'src\MCD.App\bin') -Filter 'MasterControlDock.exe' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $found) { throw 'no build of MasterControlDock.exe was found; build first' }
    $Exe = $found.FullName
}

# Any copy already running would refuse this one and the check would report ten
# missing markers for one cause. On a build agent there is nothing to close.
& (Join-Path $PSScriptRoot 'close-mcd.ps1')

# A profile of its own, so the check starts from defaults every time and the
# "exactly one settings write" assertion below means something.
$profileDir = Join-Path ([System.IO.Path]::GetTempPath()) ("mcd-smoke-" + [guid]::NewGuid().ToString('n'))
$env:MCD_DATA_DIR = Join-Path $profileDir 'MCD'
$env:MCD_SELFTEST = '1'
$env:MCD_SELFTEST_SECONDS = "$Seconds"

Write-Host "running $Exe for $Seconds s"
$proc = Start-Process $Exe -PassThru
$exited = $proc.WaitForExit(($Seconds + 25) * 1000)

$log = Join-Path $profileDir 'MCD\logs\mcd.log'
if (Test-Path $log) { $text = Get-Content $log -Raw } else { $text = '' }

if (-not $exited) {
    Stop-Process -Id $proc.Id -Force
    Write-Host $text
    throw "it never ended on its own within $($Seconds + 25) s"
}

Write-Host $text

$required = [ordered]@{
    'the start line'          = '=== start'
    'a settled topology'      = 'monitors\.snapshot authoritative'
    'an AppBar registration'  = 'appbar\.registered'
    'a positioned dock'       = 'dock\.applied'
    'the dock manager'        = 'dock\.manager docks='
    'the performance counters' = 'sensors\.provider id=pdh state=available'
    'the memory reading'      = 'sensors\.provider id=mem state=available'
    'a temperature source'    = 'sensors\.provider id=(nvml|disk|hwinfo|lhm) state=available'

    # The settings window is built entirely from XAML, and a name XAML does not
    # know is not a compile error - it is a parse failure at run time that takes
    # the whole process with it. Nothing but opening the window finds those.
    'the settings window'     = 'settings\.shown'
    'the AppBar released'     = 'appbar\.removed'
}

$failures = @()

# Named separately: one refusal explains every missing marker at once, and a
# list of ten failures hides that.
if ($text -match 'start\.refused') {
    Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
    throw 'another copy was running and this one stood down; close it and try again'
}

foreach ($name in $required.Keys) {
    if ($text -notmatch $required[$name]) { $failures += "never got to: $name" }
}

if ($text -match 'LEVEL=(ERROR|FATAL)') { $failures += 'the log contains an error' }
if ($text -match 'Unhandled exception')  { $failures += 'the log contains an unhandled exception' }

# The AppBar counter is printed with every registration and release. Ending on
# anything but zero means a bar is still holding part of the work area.
$lastLive = ([regex]::Matches($text, 'appbar\.(registered|removed) .*live=(\d+)') | Select-Object -Last 1)
if (-not $lastLive) {
    $failures += 'no AppBar registration was ever logged'
}
elseif ($lastLive.Groups[2].Value -ne '0') {
    $failures += "AppBar registrations were left behind (live=$($lastLive.Groups[2].Value))"
}

# One write on a fresh profile: the monitors are new. More than one means the
# program is writing in response to its own side effects.
$commits = ([regex]::Matches($text, 'settings\.commit')).Count
if ($commits -ne 1) { $failures += "expected exactly one settings write on a fresh profile, saw $commits" }

Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    throw "the smoke check found $($failures.Count) problem(s)"
}

Write-Host 'smoke check passed'
