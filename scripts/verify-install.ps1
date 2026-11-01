#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$MetadataPath,
    [string]$PackagesRoot,
    [switch]$PackageOnly,
    [switch]$PrintPath,
    [switch]$NoReport
)
. (Join-Path $PSScriptRoot 'package-common.ps1')
Assert-LuxWindows
if ($PrintPath -and -not $PackageOnly) { throw '-PrintPath requires -PackageOnly.' }
$metadata = Read-LuxMetadata $MetadataPath 'win'
if ($PackageOnly) {
    if ($PrintPath) { Write-Output $metadata.path }
    else { Write-Host "Verified $($metadata.name) $($metadata.version) (win): $($metadata.path)" }
    return
}
if (-not $PackagesRoot) {
    if (-not $env:APPDATA) { throw 'APPDATA is unavailable; supply -PackagesRoot.' }
    $PackagesRoot = Join-Path $env:APPDATA 'McNeel\Rhinoceros\packages\8.0'
}
$installed = Join-Path (Join-Path $PackagesRoot $metadata.name) $metadata.version
$checks = [Collections.Generic.List[object]]::new()
$archive = [IO.Compression.ZipFile]::OpenRead($metadata.path)
try {
    foreach ($entry in @(Get-LuxSafeEntries $archive)) {
        $candidate = Join-Path $installed ($entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar))
        Assert-LuxNoReparsePoint $installed $candidate
        $expected = Get-LuxEntryHash $entry
        $actual = $null
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() }
        $checks.Add([pscustomobject]@{ file = $entry.FullName; matches = $actual -ceq $expected; sha256 = $actual })
    }
}
finally { $archive.Dispose() }
$allMatch = @($checks | Where-Object { -not $_.matches }).Count -eq 0
$report = [pscustomobject][ordered]@{
    installedPath = $installed; package = $metadata.path; name = $metadata.name
    version = $metadata.version; platform = 'win'; allFilesMatch = $allMatch; files = $checks.ToArray()
}
if (-not $NoReport) {
    $artifacts = Join-Path $script:LuxRepository 'artifacts'
    [void][IO.Directory]::CreateDirectory($artifacts)
    Write-LuxAtomicJson (Join-Path $artifacts 'install-verification-win.json') $report
    Write-LuxAtomicJson (Join-Path $artifacts 'install-verification.json') $report
}
if (-not $allMatch) { throw 'Installed files differ from the reviewed candidate; installation is not verified.' }
Write-Host "Verified $($checks.Count) installed files: $installed"
