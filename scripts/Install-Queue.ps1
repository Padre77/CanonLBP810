# SPDX-License-Identifier: GPL-2.0-or-later
# Run as Administrator. Installs only this experimental queue/driver/loopback port.
[CmdletBinding()]
param([string]$CoreDirectory)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run Install-Queue.ps1 in an elevated PowerShell window.'
}
$projectRoot = Split-Path $PSScriptRoot -Parent
$artifactDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
Start-Transcript -Path (Join-Path $artifactDirectory 'install-queue.log') -Force
trap {
    Write-Output ($_ | Out-String)
    Stop-Transcript
    throw
}
$gpd = Join-Path $projectRoot 'driver\LBP810.GPD'
$core = Join-Path $env:windir 'System32\spool\drivers\x64\3'
if ($CoreDirectory) { $core = $CoreDirectory }
foreach ($file in @('UNIDRV.DLL','UNIDRVUI.DLL','UNIRES.DLL')) {
    if (-not (Test-Path -LiteralPath (Join-Path $core $file))) { throw "Missing Windows component: $file" }
}
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class Lbp810Installer {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct DriverInfo {
        public uint Version;
        public string Name, Environment, DriverPath, DataFile, ConfigFile;
        public string HelpFile;
        public IntPtr DependentFiles;
        public string MonitorName, DefaultDataType;
    }
    [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool AddPrinterDriverEx(string server, uint level, ref DriverInfo info, uint flags);
    public static void Install(string core, string gpd) {
        var dependent = Marshal.StringToHGlobalUni(core + "\\UNIRES.DLL\0" + core + "\\STDNAMES.GPD\0\0");
        try {
            var d = new DriverInfo {
                Version=3, Name="Canon LBP-810 Native", Environment="Windows x64",
                DriverPath=core+"\\UNIDRV.DLL", DataFile=gpd,
                ConfigFile=core+"\\UNIDRVUI.DLL", DependentFiles=dependent,
                DefaultDataType="RAW"
            };
            if (!AddPrinterDriverEx(null, 3, ref d, 0x18)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { Marshal.FreeHGlobal(dependent); }
    }
}
'@
[Lbp810Installer]::Install($core, $gpd)
$port = 'LBP810_NATIVE_LOOPBACK'
if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $port -PrinterHostAddress '127.0.0.1' -PortNumber 18100
}
if (-not (Get-Printer -Name 'Canon LBP-810 Native' -ErrorAction SilentlyContinue)) {
    Add-Printer -Name 'Canon LBP-810 Native' -DriverName 'Canon LBP-810 Native' -PortName $port -PrintProcessor 'winprint'
}
Write-Output 'Installed experimental Canon LBP-810 Native queue. Start capture/serve bridge before printing.'
Stop-Transcript
