# SPDX-License-Identifier: GPL-2.0-or-later
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'Start-BridgeBackground.ps1'
$startupDirectory = [Environment]::GetFolderPath('Startup')
$shellObject = New-Object -ComObject WScript.Shell
$shortcut = $shellObject.CreateShortcut((Join-Path $startupDirectory 'Canon LBP-810 Native.lnk'))
$shortcut.TargetPath = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcher + '"'
$shortcut.WorkingDirectory = Split-Path $PSScriptRoot -Parent
$shortcut.WindowStyle = 7
$shortcut.Description = 'Native USB CAPT print bridge for Canon LBP-810'
$shortcut.Save()
Write-Output 'LBP-810 bridge enabled at user logon.'
