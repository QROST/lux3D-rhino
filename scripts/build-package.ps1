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
    $checks = 'skipped'
    if (-not $SkipTests) {
        Invoke-LuxNative $dotnet @('test', 'Lux3D.Rhino.sln', '--configuration', 'Release', '--no-build', '--no-restore')
        $python = Get-Command py.exe -ErrorAction SilentlyContinue
        if ($null -ne $python) { Invoke-LuxNative $python.Source @('-3', 'scripts\create-fixtures.py', '--check') }
        else {
            $python = Get-Command python.exe -ErrorAction SilentlyContinue
            if ($null -eq $python) { throw 'Python 3 is needed for fixture checks. Install Python, or explicitly use -SkipTests for an untested candidate.' }
            Invoke-LuxNative $python.Source @('scripts\create-fixtures.py', '--check')
        }
        $checks = 'passed'
    }
    $artifacts = Join-Path $script:LuxRepository 'artifacts'
    [void][IO.Directory]::CreateDirectory($artifacts)
    $stage = Join-Path $artifacts ('package-win.' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    Get-ChildItem -LiteralPath 'src\Lux3D.Rhino\bin\Release\net7.0' -Force | Copy-Item -Destination $stage -Recurse
    $sidecar = Join-Path $stage 'sidecar'
    [void][IO.Directory]::CreateDirectory($sidecar)
    Get-ChildItem -LiteralPath 'src\Lux3D.Sidecar\bin\Release\net8.0' -Force | Copy-Item -Destination $sidecar -Recurse
    # Remove only apphost copies in this newly-created staging directory, never build outputs.
    foreach ($name in @('Lux3D.Sidecar', 'Lux3D.Sidecar.exe')) {
        $staleHost = Join-Path $sidecar $name
        if (Test-Path -LiteralPath $staleHost -PathType Leaf) { Remove-Item -LiteralPath $staleHost }
    }
    Copy-Item -LiteralPath 'packaging\manifest.yml' -Destination $stage
    Copy-Item -LiteralPath @('LICENSE', 'NOTICE', 'readme.md', 'readme.zh-CN.md') -Destination $stage
    Copy-Item -LiteralPath 'docs' -Destination $stage -Recurse
    $fixtureStage = Join-Path $stage 'fixtures'
    [void][IO.Directory]::CreateDirectory($fixtureStage)
    Get-ChildItem -Path 'fixtures\*.glb' -File | Copy-Item -Destination $fixtureStage
    Copy-Item -LiteralPath @('fixtures\manifest.json', 'fixtures\README.md') -Destination $fixtureStage
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
