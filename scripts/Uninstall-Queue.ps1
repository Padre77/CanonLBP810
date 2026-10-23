# SPDX-License-Identifier: GPL-2.0-or-later
# Run as Administrator. Keeps sources, binaries and diagnostic logs.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $projectRoot 'src\Lbp810\bin\Release\net10.0\Lbp810.dll'
$processes = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
    $_.CommandLine -like ('*' + $dll + '*') -and $_.CommandLine -match '\sserve\s'
})
foreach ($process in $processes) { Stop-Process -Id $process.ProcessId }
$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Canon LBP-810 Native.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
if (Get-Printer -Name 'Canon LBP-810 Native' -ErrorAction SilentlyContinue) {
    Remove-Printer -Name 'Canon LBP-810 Native'
}
if (Get-PrinterPort -Name 'LBP810_NATIVE_LOOPBACK' -ErrorAction SilentlyContinue) {
    Remove-PrinterPort -Name 'LBP810_NATIVE_LOOPBACK'
}
if (Get-PrinterDriver -Name 'Canon LBP-810 Native' -ErrorAction SilentlyContinue) {
    Remove-PrinterDriver -Name 'Canon LBP-810 Native'
}
Write-Output 'Removed LBP-810 experimental queue, port, driver registration, bridge and autostart shortcut. Project files/logs were kept.'
