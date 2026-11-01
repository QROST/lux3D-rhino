#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$MetadataPath,
    [string]$YakPath,
    [string]$DotNetPath
)
. (Join-Path $PSScriptRoot 'package-common.ps1')
Assert-LuxWindows
$yak = Get-LuxYakPath $YakPath
$null = Get-LuxDotNetPath $DotNetPath
# Validate hash, archive manifest and Windows distribution tag before any installation.
$metadata = Read-LuxMetadata $MetadataPath 'win'
Write-Host "Installing reviewed candidate $($metadata.name) $($metadata.version) (win)."
Invoke-LuxNative $yak @('install', $metadata.path)
& (Join-Path $PSScriptRoot 'verify-install.ps1') -MetadataPath $MetadataPath
Write-Host 'Lux3D installed. Open or restart Rhino manually, then run Lux3DPanel.'
