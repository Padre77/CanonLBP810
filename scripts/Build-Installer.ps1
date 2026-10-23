# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $artifactRoot = Join-Path $projectRoot 'artifacts'
    $package = Join-Path $artifactRoot 'package'
    $app = Join-Path $package 'app'
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    & dotnet publish src/Lbp810/Lbp810.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Autonomous application build failed.' }
    $stalePdb = Join-Path $app 'Lbp810.pdb'
    if (Test-Path -LiteralPath $stalePdb) { Remove-Item -LiteralPath $stalePdb }
    & (Join-Path $app 'Lbp810.exe') selftest
    if ($LASTEXITCODE -ne 0) { throw 'Encoder tests failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $visualStudio) { throw 'Visual C++ x64 compiler is required.' }
    $vc = Get-ChildItem (Join-Path $visualStudio 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
    $sdk = Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'um\Windows.h')} |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
    $compiler = Join-Path $vc 'bin\Hostx64\x64\cl.exe'
    $rc = Join-Path $sdkRoot ('bin\' + $sdk + '\x64\rc.exe')
    $includes = @('/I' + (Join-Path $vc 'include'))
    foreach ($part in @('shared','um','ucrt')) { $includes += '/I' + (Join-Path $sdkRoot ('Include\' + $sdk + '\' + $part)) }
    $libs = @('/LIBPATH:' + (Join-Path $vc 'lib\x64'))
    foreach ($part in @('um','ucrt')) { $libs += '/LIBPATH:' + (Join-Path $sdkRoot ('Lib\' + $sdk + '\' + $part + '\x64')) }
    & $rc /nologo /i $projectRoot /fo (Join-Path $artifactRoot 'uninstall.res') installer/Uninstall.rc
    if ($LASTEXITCODE -ne 0) { throw 'Uninstaller resources failed.' }
    $options = @('/nologo','/std:c++17','/utf-8','/EHsc','/O2','/MT','/DUNICODE','/D_UNICODE') + $includes
    & $compiler @options /DUNINSTALLER installer/Setup.cpp (Join-Path $artifactRoot 'uninstall.res') `
        ('/Fo' + (Join-Path $artifactRoot 'uninstall.obj')) ('/Fe' + (Join-Path $package 'Uninstall.exe')) `
        /link /SUBSYSTEM:WINDOWS /MACHINE:X64 /MANIFEST:NO @libs user32.lib shell32.lib ole32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native uninstaller build failed.' }
    foreach ($folder in @('driver','scripts')) { New-Item -ItemType Directory -Path (Join-Path $package $folder) -Force | Out-Null }
    Copy-Item driver/LBP810.GPD (Join-Path $package 'driver\LBP810.GPD') -Force
    Copy-Item scripts/Install-Queue.ps1 (Join-Path $package 'scripts\Install-Queue.ps1') -Force
    Copy-Item installer/deployment/*.ps1 (Join-Path $package 'scripts') -Force
    Copy-Item COPYING,README.md -Destination $package -Force
    $runtimeLicense = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netcore.app.runtime.win-x64'
    $runtimeVersion = Get-ChildItem $runtimeLicense -Directory | Where-Object {$_.Name -like '10.0.*'} |
        Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1 -ExpandProperty FullName
    foreach ($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
        Copy-Item -LiteralPath (Join-Path $runtimeVersion $notice) -Destination (Join-Path $package ('DOTNET-' + $notice)) -Force
    }
    $manifestFiles = @(Get-ChildItem $package -Recurse -File | Where-Object {$_.Name -ne 'manifest.json'} | ForEach-Object {
        @{path=$_.FullName.Substring($package.Length + 1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    @{product='LBP810-NATIVE-3D106E28';version='0.2.0';files=$manifestFiles} | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $artifactRoot 'payload.zip') -Force
    & $rc /nologo /i $projectRoot /fo (Join-Path $artifactRoot 'setup.res') installer/Setup.rc
    if ($LASTEXITCODE -ne 0) { throw 'Installer resources failed.' }
    $output = Join-Path $projectRoot 'dist'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $setup = Join-Path $output 'LBP810-Native-Setup-0.2.0-x64.exe'
    & $compiler @options installer/Setup.cpp (Join-Path $artifactRoot 'setup.res') `
        ('/Fo' + (Join-Path $artifactRoot 'setup.obj')) ('/Fe' + $setup) `
        /link /SUBSYSTEM:WINDOWS /MACHINE:X64 /MANIFEST:NO @libs user32.lib shell32.lib ole32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native installer build failed.' }
    # Corresponding source archive: only product sources, never old proprietary Canon files or job logs.
    $sourceRoot = Join-Path $artifactRoot 'source-package'
    foreach ($folder in @('src','driver','scripts','installer')) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Recurse -File |
            Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'} | ForEach-Object {
                $relative = $_.FullName.Substring($projectRoot.Length + 1)
                $target = Join-Path $sourceRoot $relative
                New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            }
    }
    Copy-Item COPYING,README.md -Destination $sourceRoot -Force
    $reference = Join-Path $sourceRoot 'third_party\capt-reference\capt-0.1'
    New-Item -ItemType Directory -Path $reference -Force | Out-Null
    foreach ($file in @('capt.c','capt.h','SPECS','README','COPYING')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('third_party\capt-reference\capt-0.1\' + $file)) -Destination $reference -Force
    }
    Compress-Archive -Path (Join-Path $sourceRoot '*') -DestinationPath (Join-Path $output 'LBP810-Native-Source-0.2.0.zip') -Force
    Get-FileHash -LiteralPath $setup -Algorithm SHA256 | ForEach-Object {
        ($_.Hash + '  ' + (Split-Path $_.Path -Leaf)) | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
    }
    Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Format-List
    Get-Item -LiteralPath $setup | Select-Object FullName,Length
} finally { Pop-Location }
