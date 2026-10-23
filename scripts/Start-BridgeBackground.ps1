# SPDX-License-Identifier: GPL-2.0-or-later
$ErrorActionPreference = 'Stop'
if (Get-Service -Name Lbp810NativeBridge -ErrorAction SilentlyContinue) {
    Write-Output 'Installed Windows service manages the bridge. Use Services to start/stop it.'
    exit 0
}
$projectRoot = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $projectRoot 'src\Lbp810\bin\Release\net10.0\Lbp810.dll'
$logs = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
# A named mutex in the launcher prevents two simultaneous logon/manual starts.
$launchMutex = [Threading.Mutex]::new($false, 'Local\Lbp810NativeBridgeLauncher')
if (-not $launchMutex.WaitOne(0)) { $launchMutex.Dispose(); exit 0 }
try {
    $existing = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
        $_.CommandLine -like ('*' + $dll + '*') -and $_.CommandLine -match '\sserve\s'
    })
    if ($existing.Count -gt 0) { exit 0 }
    $device = 'auto'
    # The saved device path identifies this user's tested printer. The server opens it per job,
    # so the printer does not have to be powered on when the user logs in.
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $arguments = @(('"' + $dll + '"'), 'serve', ('"' + $device + '"'), ('"' + (Join-Path $logs 'jobs') + '"'))
    $process = Start-Process -FilePath $dotnet -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'bridge.stdout.log') `
        -RedirectStandardError (Join-Path $logs 'bridge.stderr.log')
    Write-Output ('LBP-810 bridge PID: ' + $process.Id)
} finally {
    $launchMutex.ReleaseMutex()
    $launchMutex.Dispose()
}
