#requires -Version 5.1
# Shared helpers; dot-sourcing this file does not build, install, or open Rhino.
# Rhino 8 CLI: https://developer.rhino3d.com/guides/yak/package-sources/
# Package root: https://discourse.mcneel.com/t/yak8-cant-find-but-packagemanager8-can/172000/4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:LuxRepository = Split-Path -Parent $PSScriptRoot

function Assert-LuxWindows {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Use the .sh scripts on macOS. These scripts target Windows PowerShell 5.1 or later.'
    }
    if (-not [Environment]::Is64BitProcess) {
        throw 'Run the 64-bit PowerShell host for Rhino 8 and its .NET 8 sidecar.'
    }
}

function Get-LuxYakPath([string]$Override) {
    if ($Override) { $candidate = $Override }
    elseif ($env:LUX3D_YAK_PATH) { $candidate = $env:LUX3D_YAK_PATH }
    else { $candidate = Join-Path $env:ProgramFiles 'Rhino 8\System\Yak.exe' }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Rhino 8 Yak CLI is missing: $candidate" }
    return (Resolve-Path -LiteralPath $candidate).Path
}

function Get-LuxDotNetPath([string]$Override) {
    $candidates = [Collections.Generic.List[string]]::new()
    if ($Override) {
        if (-not (Test-Path -LiteralPath $Override -PathType Leaf)) { throw '.NET host override was not found.' }
        $candidates.Add((Resolve-Path -LiteralPath $Override).Path)
    }
    else {
        foreach ($root in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
            if ($root) { $candidates.Add((Join-Path $root 'dotnet.exe')) }
        }
        $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))
        foreach ($command in @(Get-Command dotnet.exe -All -CommandType Application -ErrorAction SilentlyContinue)) {
            if ($null -ne $command) { $candidates.Add($command.Source) }
        }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        if (-not $seen.Add($resolved)) { continue }
        try {
            $runtimes = @(& $resolved --list-runtimes 2>$null)
            if ($LASTEXITCODE -eq 0 -and ($runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App 8\.' })) {
                return $resolved
            }
        }
        catch { continue }
    }
    throw 'No usable .NET 8 host was found. Install Microsoft.NETCore.App 8.x, or set DOTNET_ROOT_X64 / DOTNET_ROOT / -DotNetPath. A .NET 9/10 runtime alone is insufficient.'
}

function Invoke-LuxNative([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $LASTEXITCODE." }
}

function Get-LuxManifestIdentity([string]$Text) {
    $values = @{}
    foreach ($key in @('name', 'version')) {
        $manifestMatches = [regex]::Matches($Text, ('(?m)^' + $key + ':\s*([^\r\n]+?)\s*$'))
        if ($manifestMatches.Count -ne 1) { throw "Expected exactly one manifest $key." }
        $values[$key] = $manifestMatches[0].Groups[1].Value.Trim().Trim([char[]]@([char]34, [char]39))
    }
    if ($values.name -notmatch '^[A-Za-z0-9_-]+$') { throw 'Invalid package name.' }
    if ($values.version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?$') { throw 'Invalid package version.' }
    return [pscustomobject]$values
}

function Get-LuxSafeEntries($Archive) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $files = [Collections.Generic.List[object]]::new()
    foreach ($entry in $Archive.Entries) {
        $raw = $entry.FullName
        $clean = $raw.TrimEnd([char]'/')
        $badSegments = @($clean.Split([char]'/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':') })
        $isLink = (([int64]$entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000
        if (-not $raw -or $raw.StartsWith('/') -or $raw.Contains('\') -or $badSegments.Count -gt 0 -or $isLink) {
            throw "Unsafe package entry: $raw"
        }
        if (-not $seen.Add($clean)) { throw "Duplicate package entry: $raw" }
        if (-not $raw.EndsWith('/')) { $files.Add($entry) }
    }
    if ($files.Count -eq 0) { throw 'Package is empty.' }
    return $files.ToArray()
}

function Read-LuxZipText($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) { throw "Package is missing $Name." }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Get-LuxPackageIdentity([string]$PackagePath, [string]$Platform = 'win') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = (Resolve-Path -LiteralPath $PackagePath).Path
    $filename = [IO.Path]::GetFileName($package)
    $tag = [regex]::Match($filename, '-rh(\d+)(?:_\d+)?-(mac|win)\.yak$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $tag.Success -or $tag.Groups[1].Value -ne '8' -or $tag.Groups[2].Value.ToLowerInvariant() -ne $Platform) {
        throw "Expected a Rhino 8 $Platform distribution, got $filename."
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entries = @(Get-LuxSafeEntries $archive)
        $identity = Get-LuxManifestIdentity (Read-LuxZipText $archive 'manifest.yml')
        if ($identity.name -cne 'lux3d-rhino') { throw 'This installer accepts only the lux3d-rhino package.' }
        $prefix = $identity.name + '-' + $identity.version + '-rh8'
        if (-not $filename.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package filename does not match its manifest identity.' }
        foreach ($required in @('Lux3D.Rhino.rhp', 'Lux3D.Core.dll', 'sidecar/Lux3D.Sidecar.dll', 'sidecar/Lux3D.Sidecar.deps.json', 'sidecar/Lux3D.Sidecar.runtimeconfig.json')) {
            if ($null -eq $archive.GetEntry($required)) { throw "Package is missing $required." }
        }
        if ($null -ne $archive.GetEntry('sidecar/Lux3D.Sidecar') -or $null -ne $archive.GetEntry('sidecar/Lux3D.Sidecar.exe')) {
            throw 'Expected a framework-dependent sidecar without a platform apphost.'
        }
        $runtime = (Read-LuxZipText $archive 'sidecar/Lux3D.Sidecar.runtimeconfig.json' | ConvertFrom-Json).runtimeOptions.framework
        if ($runtime.name -ne 'Microsoft.NETCore.App' -or $runtime.version -notmatch '^8\.') { throw 'Sidecar must require Microsoft.NETCore.App 8.x.' }
    }
    finally { $archive.Dispose() }
    return [pscustomobject][ordered]@{
        schemaVersion = 2; path = $package; sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        name = $identity.name; version = $identity.version; platform = $Platform; rhinoMajor = 8
        deploymentMode = 'framework-dependent'; sidecarRuntime = 'net8.0'
    }
}

function Get-LuxMetadataPath([string]$Override, [string]$Platform = 'win') {
    if ($Override) { return (Resolve-Path -LiteralPath $Override).Path }
    $preferred = Join-Path $script:LuxRepository "artifacts\latest-package-$Platform.json"
    if (Test-Path -LiteralPath $preferred -PathType Leaf) { return $preferred }
    return Join-Path $script:LuxRepository 'artifacts\latest-package.json'
}

function Read-LuxMetadata([string]$MetadataPath, [string]$Platform = 'win') {
    $selected = Get-LuxMetadataPath $MetadataPath $Platform
    $data = Get-Content -LiteralPath $selected -Raw -Encoding UTF8 | ConvertFrom-Json
    $package = [string]$data.path
    if (-not [IO.Path]::IsPathRooted($package)) { $package = Join-Path (Split-Path -Parent $selected) $package }
    $actual = Get-LuxPackageIdentity $package $Platform
    if ([string]$data.sha256 -ine $actual.sha256) { throw 'Package checksum mismatch.' }
    # Legacy metadata is accepted only after checking the archive manifest and platform tag.
    foreach ($field in @('name', 'version', 'platform', 'rhinoMajor', 'deploymentMode', 'sidecarRuntime')) {
        if ($null -ne $data.PSObject.Properties[$field] -and $data.$field -cne $actual.$field) {
            throw "Package metadata $field does not match the archive."
        }
    }
    return $actual
}

function Write-LuxAtomicJson([string]$Path, $Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, (($Value | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, $null) }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Get-LuxEntryHash($Entry) {
    $stream = $Entry.Open()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Assert-LuxNoReparsePoint([string]$Root, [string]$Candidate) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $current = [IO.Path]::GetFullPath($Candidate)
    if (-not $current.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installed package path escapes its directory.' }
    while ($current.Length -ge $rootFull.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Installed package contains a reparse point: $current" }
        }
        if ($current -eq $rootFull) { break }
        $current = Split-Path -Parent $current
    }
}
