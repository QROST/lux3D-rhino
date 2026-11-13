#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$NoRestore,
    [string]$YakPath,
    [string]$DotNetPath
)
. (Join-Path $PSScriptRoot 'package-common.ps1')
Assert-LuxWindows
$yak = Get-LuxYakPath $YakPath
$dotnet = Get-LuxDotNetPath $DotNetPath
Push-Location $script:LuxRepository
try {
    $sourceIdentity = Get-LuxManifestIdentity (Get-Content -LiteralPath 'packaging\manifest.yml' -Raw -Encoding UTF8)
    [xml]$properties = Get-Content -LiteralPath 'Directory.Build.props' -Raw -Encoding UTF8
    if ([string]$properties.Project.PropertyGroup.Version -ne $sourceIdentity.version) { throw 'Manifest and Directory.Build.props versions must agree.' }
    $buildArguments = @('build', 'Lux3D.Rhino.sln', '--configuration', 'Release', '-p:UseAppHost=false')
    if ($NoRestore) { $buildArguments += '--no-restore' }
    Invoke-LuxNative $dotnet $buildArguments
    # Python is required for the shared, allowlisted staging path on both platforms.
    $python = Get-Command py.exe -ErrorAction SilentlyContinue
    $pythonPrefix = @('-3')
    if ($null -eq $python) {
        $python = Get-Command python.exe -ErrorAction SilentlyContinue
        $pythonPrefix = @()
    }
    if ($null -eq $python) { throw 'Python 3.9+ is required for package staging and validation.' }
    $checks = 'skipped'
    if (-not $SkipTests) {
        Invoke-LuxNative $dotnet @('test', 'Lux3D.Rhino.sln', '--configuration', 'Release', '--no-build', '--no-restore')
        Invoke-LuxNative $python.Source ($pythonPrefix + @('scripts\create-fixtures.py', '--check'))
        Invoke-LuxNative $python.Source ($pythonPrefix + @('-m', 'unittest', 'discover', '-s', 'scripts', '-p', 'test_*.py'))
        $checks = 'passed'
    }
    $artifacts = Join-Path $script:LuxRepository 'artifacts'
    [void][IO.Directory]::CreateDirectory($artifacts)
    $stage = Join-Path $artifacts ('package-win.' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    Invoke-LuxNative $python.Source ($pythonPrefix + @('scripts\stage-package.py', $stage))
    Push-Location $stage
    try { Invoke-LuxNative $yak @('build', '--platform', 'win') }
    finally { Pop-Location }
    $packages = @(Get-ChildItem -LiteralPath $stage -Filter '*.yak' -File)
    if ($packages.Count -ne 1) { throw 'Expected exactly one Yak package in the new staging directory.' }
    $metadata = Get-LuxPackageIdentity $packages[0].FullName 'win'
    if ($metadata.name -cne $sourceIdentity.name -or $metadata.version -cne $sourceIdentity.version) { throw 'Archive and source manifest identities differ.' }
    $metadata | Add-Member -NotePropertyName checks -NotePropertyValue $checks
    $metadata | Add-Member -NotePropertyName createdUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('o'))
    [IO.File]::WriteAllText(($metadata.path + '.sha256'), ($metadata.sha256 + '  ' + $packages[0].Name + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-LuxAtomicJson (Join-Path $artifacts 'latest-package-win.json') $metadata
    Write-LuxAtomicJson (Join-Path $artifacts 'latest-package.json') $metadata
    Write-Host ('Package: ' + $metadata.path)
    Write-Host ('SHA256: ' + $metadata.sha256)
    Write-Host ('Checks: ' + $checks)
    Write-Host 'Candidate only. Host/account acceptance remains separate. No install or publication was performed.'
}
finally { Pop-Location }
