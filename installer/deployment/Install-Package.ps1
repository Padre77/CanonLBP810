# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PayloadRoot,
      [Parameter(Mandatory=$true)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
$expected = Join-Path $env:ProgramW6432 'Canon LBP-810 Native'
if ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') -ne $expected) { throw 'Invalid installation target.' }
if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt 19045) {
    throw 'Windows 10 22H2 x64 or newer is required.'
}
if (@(Get-PrintJob -PrinterName 'Canon LBP-810 Native' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Finish or cancel jobs in Canon LBP-810 Native before installing.'
}
$dataRoot = Join-Path $env:ProgramData 'Lbp810Native'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
Start-Transcript -Path (Join-Path $dataRoot 'install.log') -Force
try {
    # Resolve Microsoft's inbox Unidrv from this Windows installation, never ship OS DLLs.
    $core = Join-Path $env:windir 'System32\spool\drivers\x64\3'
    New-Item -ItemType Directory -Path $core -Force | Out-Null
    foreach ($name in @('UNIDRV.DLL','UNIDRVUI.DLL','UNIRES.DLL','STDNAMES.GPD')) {
        $source = Join-Path $core $name
        if (-not (Test-Path -LiteralPath $source)) {
            $source = Get-ChildItem -LiteralPath (Join-Path $env:windir 'System32\DriverStore\FileRepository') -Filter $name -Recurse -File |
                Where-Object {$_.FullName -notmatch '_x86_|\\I386\\|_arm64_'} |
                Sort-Object FullName | Select-Object -First 1 -ExpandProperty FullName
        }
        if (-not $source) { throw "Windows inbox print component missing: $name. Enable Windows printing components first." }
        # AddPrinterDriverEx must reference the installed print-driver directory.
        # Only fill missing inbox components; never overwrite a shared Windows DLL.
        $destination = Join-Path $core $name
        if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $source -Destination $destination }
    }
    $marker = Join-Path $InstallRoot 'product-id.txt'
    if (Test-Path -LiteralPath $InstallRoot) {
        if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'LBP810-NATIVE-3D106E28') {
            throw 'Target directory already exists and is not owned by this installer.'
        }
    }
    $service = Get-Service -Name 'Lbp810NativeBridge' -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') {
            try { Stop-Service -Name $service.Name -ErrorAction Stop }
            catch { $service.Refresh(); if ($service.Status -ne 'Stopped') { throw } }
        }
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    # Migrate the development bridge only if it owns our loopback listener.
    $listener = Get-NetTCPConnection -State Listen -LocalPort 18100 -ErrorAction SilentlyContinue
    foreach ($connection in $listener) {
        $owner = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $connection.OwningProcess)
        if ($owner.CommandLine -match 'Lbp810\.dll"?\s+serve\s') { Stop-Process -Id $owner.ProcessId -ErrorAction Stop }
        else { throw 'Port 18100 is used by another process. Installation stopped.' }
    }
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    'LBP810-NATIVE-3D106E28' | Set-Content -LiteralPath $marker -Encoding ASCII
    foreach ($folder in @('app','driver','scripts')) {
        $target = Join-Path $InstallRoot $folder
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $PayloadRoot ($folder + '\*')) -Destination $target -Recurse -Force
    }
    foreach ($file in @('COPYING','README.md','Uninstall.exe','manifest.json','DOTNET-LICENSE.TXT','DOTNET-THIRD-PARTY-NOTICES.TXT')) {
        Copy-Item -LiteralPath (Join-Path $PayloadRoot $file) -Destination (Join-Path $InstallRoot $file) -Force
    }
    & (Join-Path $InstallRoot 'scripts\Install-Queue.ps1') -CoreDirectory $core
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Queue registration failed: $LASTEXITCODE" }
    $binary = '"' + (Join-Path $InstallRoot 'app\Lbp810.exe') + '" service "' + $dataRoot + '"'
    if (-not $service) {
        New-Service -Name 'Lbp810NativeBridge' -BinaryPathName $binary -StartupType Automatic `
            -DisplayName 'Canon LBP-810 Native USB Bridge' -Description 'Native CAPT USB printing for Canon LBP-810.' | Out-Null
    } else {
        $installedService = Get-CimInstance Win32_Service -Filter "Name='Lbp810NativeBridge'"
        if ($installedService.PathName -ne $binary) {
            $changed = Invoke-CimMethod -InputObject $installedService -MethodName Change -Arguments @{PathName=$binary;StartMode='Automatic'}
            if ($changed.ReturnValue -ne 0) { throw ('Could not update bridge service: ' + $changed.ReturnValue) }
        }
        Set-Service -Name 'Lbp810NativeBridge' -StartupType Automatic
    }
    & sc.exe failure Lbp810NativeBridge reset= 86400 actions= restart/5000/restart/15000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
    Start-Service -Name 'Lbp810NativeBridge'
    (Get-Service 'Lbp810NativeBridge').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $listener = Get-NetTCPConnection -State Listen -LocalPort 18100 -ErrorAction Stop
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='Lbp810NativeBridge'"
    if ($listener.OwningProcess -notcontains $serviceInfo.ProcessId) { throw 'Service is not listening on its configured port.' }
    $legacyShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Canon LBP-810 Native.lnk'
    if (Test-Path -LiteralPath $legacyShortcut) { Remove-Item -LiteralPath $legacyShortcut }
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Lbp810Native'
    New-Item -Path $key -Force | Out-Null
    $uninstall = '"' + (Join-Path $InstallRoot 'Uninstall.exe') + '"'
    $properties = @{DisplayName='Canon LBP-810 Native (Community Driver)';DisplayVersion='0.2.0';
        Publisher='LBP-810 Native Community Project';InstallLocation=$InstallRoot;
        UninstallString=$uninstall;QuietUninstallString=($uninstall + ' /quiet');
        DisplayIcon=(Join-Path $InstallRoot 'Uninstall.exe')}
    foreach ($property in $properties.GetEnumerator()) { New-ItemProperty -Path $key -Name $property.Key -Value $property.Value -PropertyType String -Force | Out-Null }
    New-ItemProperty -Path $key -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $key -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    Write-Output 'Installed Canon LBP-810 Native. Runtime included; USB discovery is automatic.'
} catch {
    # Do not remove a pre-existing queue or successful older install on failure.
    $prior = Get-Service -Name 'Lbp810NativeBridge' -ErrorAction SilentlyContinue
    if ($prior -and $prior.Status -eq 'Stopped') { Start-Service $prior.Name -ErrorAction SilentlyContinue }
    throw
} finally { Stop-Transcript }
