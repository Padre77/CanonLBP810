# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([switch]$CaptureOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $projectRoot 'src\Lbp810\bin\Release\net10.0\Lbp810.dll'
$logs = Join-Path $projectRoot 'artifacts\jobs'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Build src/Lbp810 first.' }
if ($CaptureOnly) { & dotnet $dll capture $logs; exit $LASTEXITCODE }
$devices = @(& dotnet $dll list | Where-Object { $_ -match 'vid_04a9&pid_260a' })
if ($devices.Count -ne 1) { throw 'Expected exactly one connected Canon LBP-810 USBPRINT interface.' }
& dotnet $dll serve $devices[0] $logs
exit $LASTEXITCODE
