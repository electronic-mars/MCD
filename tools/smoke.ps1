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
    [int]$Seconds = 12,

    # Which language to check in. A word that fits its key in English can be
    # half again as long in Russian and be cut at both ends, and the check that
    # measures it only ever sees the language it was run in.
    [string]$Lang = ''
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
$env:MCD_LANG = $Lang

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

    # The settings window is built partly from XAML, and a name XAML does not
    # know is not a compile error - it is a parse failure at run time that takes
    # the whole process with it. Nothing but opening the window finds those.
    'the settings window'     = 'settings\.shown'

    # And built a second time, the way a right-click on a dock builds it. The
    # pages are made in code, and a page made in code goes wrong on the second
    # build, not the first: something it holds still belongs to the page before
    # it. Opening the window once never reaches that.
    'the settings page rebuilt' = 'selftest\.reopened'

    # And every path that takes a bar apart and puts it back: refresh, refresh
    # contents, add, move, remove, and the drag overlay - twice each. Those are
    # the paths that re-parent elements, and re-parenting is what ended the
    # program the last time it went wrong.
    'the bars rebuilt'          = 'selftest\.rehearsed'

    # And each page looked at with the window as small as it is allowed to be.
    'the pages squeezed'        = 'selftest\.squeezed narrowest='

    # And every word on every page measured against the box it was given.
    'the pages measured'        = 'selftest\.clipped worst='

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

# Not required, because a build agent has neither a graphics card nor a drive
# that will report its temperature, and a check that fails on hardware rather
# than on code is a check people learn to ignore.
if ($text -notmatch 'sensors\.provider id=(nvml|disk|hwinfo|lhm) state=available') {
    Write-Host 'note: no temperature source on this machine'
}

if ($text -match 'LEVEL=(ERROR|FATAL)') { $failures += 'the log contains an error' }

# A wrapping explanation squeezed under this is a page that has collapsed into
# a column of single words. Nothing throws when that happens, which is why it
# has to be measured rather than watched for.
$narrow = ([regex]::Matches($text, 'selftest\.squeezed page=(\S+) narrowest=(\d+)'))
foreach ($m in $narrow) {
    if ([int]$m.Groups[2].Value -lt 200) {
        $failures += "the $($m.Groups[1].Value) page collapsed to $($m.Groups[2].Value) points of text"
    }
}

# A word wider than the box it was drawn in loses letters off both ends and
# still looks like a word, so nobody reports it and nobody widens the window.
$cut = ([regex]::Matches($text, 'selftest\.clipped page=(\S+) over=(\d+) text=(.*)'))
foreach ($m in $cut) {
    $failures += "the $($m.Groups[1].Value) page cut '$($m.Groups[3].Value.Trim())' by $($m.Groups[2].Value) points"
}

# Failures that announce themselves at INFO and would otherwise be read by
# nobody. Each of these means the program carried on with something missing,
# which is worse than stopping: a widget that silently does not appear looks
# like a design decision.
$forbidden = @{
    'a widget had no template'   = 'widget\.template missing'
    'a widget threw while ticking' = 'widget\.tick .* failed'
    'a widget threw while attaching' = 'widget\.attach .* failed'
    'a widget type was unknown'  = 'widget\.unknown'
    'the settings could not be saved' = 'settings\.(save|commit) failed'
    'the settings window did not build' = 'settings\.failed'
}

foreach ($name in $forbidden.Keys) {
    if ($text -match $forbidden[$name]) { $failures += $name }
}
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

# On a fresh profile the monitors are new, so the registry is written once; and
# each bar settles its widgets onto slots for the first time and records where
# it put them, which is one write per dock and never again. Anything beyond that
# is the program writing in response to its own side effects - the shape of the
# bug this whole program was built against.
# Counted over the ordinary run only. The rehearsal that follows deliberately
# adds, moves and removes a widget on every bar, and those are real changes a
# person could have made - they are supposed to be written down. Truncating
# here keeps the rule this check exists for ("a bar that has just appeared
# writes once and then stops") instead of weakening it to fit.
$ordinary = $text
$rehearsal = $text.IndexOf('selftest.rehearsing')
if ($rehearsal -ge 0) { $ordinary = $text.Substring(0, $rehearsal) }

$reconciles = ([regex]::Matches($ordinary, 'settings\.commit reason=TopologyReconcile')).Count
$settles = ([regex]::Matches($ordinary, 'settings\.commit reason=WidgetConfig')).Count
$docks = ([regex]::Matches($text, 'dock\.manager docks=(\d+)') | Select-Object -Last 1)
$expected = if ($docks) { [int]$docks.Groups[1].Value } else { 0 }

if ($reconciles -ne 1) {
    $failures += "expected exactly one monitor-registry write on a fresh profile, saw $reconciles"
}

if ($settles -gt $expected) {
    $failures += "the docks wrote their layout $settles times for $expected bars"
}

if ($text -match 'settings\.commit reason=UserAction') {
    $failures += 'something wrote settings as though a person had asked'
}

Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    throw "the smoke check found $($failures.Count) problem(s)"
}

Write-Host 'smoke check passed'
