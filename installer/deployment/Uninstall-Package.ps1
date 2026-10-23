# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
$expected = Join-Path $env:ProgramW6432 'Canon LBP-810 Native'
if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') -ne $expected) { throw 'Invalid removal target.' }
$marker = Join-Path $InstallRoot 'product-id.txt'
if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'LBP810-NATIVE-3D106E28') {
    throw 'Product ownership marker missing; refusing removal.'
}
if (@(Get-PrintJob -PrinterName 'Canon LBP-810 Native' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Finish or cancel jobs in Canon LBP-810 Native before uninstalling.'
}
$service = Get-Service -Name 'Lbp810NativeBridge' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        try { Stop-Service $service.Name -ErrorAction Stop }
        catch { $service.Refresh(); if ($service.Status -ne 'Stopped') { throw } }
    }
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
if (Get-Printer -Name 'Canon LBP-810 Native' -ErrorAction SilentlyContinue) {
    # Detach first: spooler may retain a deleted queue briefly, keeping its TCP port busy.
    Set-Printer -Name 'Canon LBP-810 Native' -PortName 'FILE:'
    Remove-Printer -Name 'Canon LBP-810 Native'
}
for ($attempt = 0; $attempt -lt 15; $attempt++) {
    if (-not (Get-PrinterPort -Name 'LBP810_NATIVE_LOOPBACK' -ErrorAction SilentlyContinue)) { break }
    try { Remove-PrinterPort -Name 'LBP810_NATIVE_LOOPBACK'; break }
    catch { if ($attempt -eq 14) { throw }; Start-Sleep -Milliseconds 500 }
}
if (Get-PrinterDriver -Name 'Canon LBP-810 Native' -ErrorAction SilentlyContinue) {
    try { Remove-PrinterDriver -Name 'Canon LBP-810 Native' }
    catch {
        # Windows may retain a driver configuration handle after its queue is gone.
        # Preserve that inert inbox-Unidrv registration instead of restarting the shared spooler.
        if ($_.FullyQualifiedErrorId -notmatch '0x80070bb9') { throw }
        $dataRoot = Join-Path $env:ProgramData 'Lbp810Native'
        'Queue removed; Windows retained the cached Unidrv/GPD registration because it is still in use.' |
            Add-Content -LiteralPath (Join-Path $dataRoot 'uninstall.log') -Encoding UTF8
    }
}
if ($service) { & sc.exe delete Lbp810NativeBridge | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' } }
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Lbp810Native'
if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse }
# Exact fixed Program Files target and ownership marker have both been checked above.
Remove-Item -LiteralPath $InstallRoot -Recurse -Force
Write-Output 'Removed LBP-810 Native. Diagnostic logs in ProgramData\Lbp810Native were preserved.'
