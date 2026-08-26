<#
.SYNOPSIS
Closes a running Master Control Dock, properly.

.DESCRIPTION
taskkill /f would skip ProcessExit, and the AppBar registrations would stay -
the desktop work area would be short until someone noticed and rebooted. The
program listens for a shutdown signal, and starting it with --exit raises that
signal and returns.
#>
$ErrorActionPreference = 'Stop'

$running = @(Get-Process MasterControlDock -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) {
    Write-Host 'nothing running'
    return
}

$exe = $running[0].Path
Start-Process $exe -ArgumentList '--exit' | Out-Null

foreach ($p in $running) {
    if ($p.WaitForExit(10000)) {
        Write-Host "closed pid $($p.Id)"
    }
    else {
        Write-Warning "pid $($p.Id) did not stop. Do NOT kill it: its AppBar would leak and the desktop would stay short."
    }
}
