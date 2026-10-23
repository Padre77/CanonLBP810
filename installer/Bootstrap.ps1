# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param([ValidateSet('Install','Verify','Uninstall')][string]$Action,
      [Parameter(Mandatory=$true)][string]$Stage,
      [Parameter(Mandatory=$true)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
try {
    if ($Action -eq 'Uninstall') {
        & (Join-Path $InstallRoot 'scripts\Uninstall-Package.ps1') -InstallRoot $InstallRoot
    } else {
        $payload = Join-Path $Stage 'payload'
        Expand-Archive -LiteralPath (Join-Path $Stage 'payload.zip') -DestinationPath $payload
        $manifest = Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw | ConvertFrom-Json
        foreach ($file in $manifest.files) {
            $full = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
            if (-not $full.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
            if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $file.sha256) { throw ('Payload hash mismatch: ' + $file.path) }
        }
        & (Join-Path $payload 'app\Lbp810.exe') selftest
        if ($LASTEXITCODE -ne 0) { throw 'Bundled runtime/encoder self-test failed.' }
        if ($Action -eq 'Install') {
            & (Join-Path $payload 'scripts\Install-Package.ps1') -PayloadRoot $payload -InstallRoot $InstallRoot
        }
    }
    'SUCCESS' | Set-Content -LiteralPath (Join-Path $Stage 'result.txt') -Encoding ASCII
    exit 0
} catch {
    ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $Stage 'error.txt') -Encoding UTF8
    exit 1
}
