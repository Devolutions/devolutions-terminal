# Shared helpers for the macOS packaging PowerShell scripts:
# Build-MacOsPackage.ps1, Stage-MacOsApp.ps1, Test-MacOsPackage.ps1,
# Test-MacOsRuntime.ps1, Test-MacOsPackagingMetadata.ps1,
# Sign-MacOsPackage.ps1, Build-MacOsDmg.ps1, Notarize-MacOsPackage.ps1, and
# Release-MacOsPackage.ps1.

Set-StrictMode -Version Latest

function Import-MacOsPackageEnv {
    <#
        .SYNOPSIS
        Parses macos/package.env (simple KEY=VALUE / KEY="VALUE" lines) into
        an ordered hashtable, mirroring `source macos/package.env` in Bash.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "macOS package metadata not found: $Path"
    }

    $values = [ordered]@{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }
        if ($trimmed -notmatch '^(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<value>.*)$') {
            continue
        }
        $key = $Matches['key']
        $value = $Matches['value']
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$key] = $value
    }
    return $values
}

function Get-MacOsSourceDateEpoch {
    <#
        .SYNOPSIS
        Resolves SOURCE_DATE_EPOCH: the environment variable if set, else the
        latest commit timestamp, else the current time. Also re-exports it as
        an environment variable, matching the Bash scripts.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $epoch = $env:SOURCE_DATE_EPOCH
    if ([string]::IsNullOrWhiteSpace($epoch)) {
        $epoch = (& git -C $RepoRoot log -1 --format=%ct 2>$null)
        if ([string]::IsNullOrWhiteSpace($epoch)) {
            $epoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
        }
    }
    if ($epoch -notmatch '^[0-9]+$') {
        throw "SOURCE_DATE_EPOCH must be a non-negative integer."
    }
    $env:SOURCE_DATE_EPOCH = $epoch
    return [long]$epoch
}

function Assert-Darwin {
    <#
        .SYNOPSIS
        Throws unless running on Darwin (macOS).
    #>
    param(
        [string]$Message = "This operation requires Darwin."
    )

    if ((& uname -s) -ne 'Darwin') {
        throw $Message
    }
}

function Assert-Command {
    <#
        .SYNOPSIS
        Throws if a required external command is not on PATH.
    #>
    param(
        [Parameter(Mandatory)]
        [string[]]$Name
    )

    foreach ($command in $Name) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "$command is required but was not found on PATH."
        }
    }
}

function Assert-MacOsVersion {
    <#
        .SYNOPSIS
        Throws unless the version string looks like a valid package version.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    if ($Version -notmatch '^[0-9][0-9A-Za-z.+~_-]*$') {
        throw "Invalid macOS package version: $Version"
    }
}

function Get-MacOsExpectedArch {
    <#
        .SYNOPSIS
        Maps a .NET macOS RID to the `lipo -archs` architecture name.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet('osx-arm64', 'osx-x64')]
        [string]$Rid
    )

    switch ($Rid) {
        'osx-arm64' { return 'arm64' }
        'osx-x64' { return 'x86_64' }
    }
}

function Invoke-Native {
    <#
        .SYNOPSIS
        Runs an external command and throws if it exits non-zero, since
        PowerShell does not treat native command failures as terminating
        errors on its own (the Bash scripts rely on `set -e` for this).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($ArgumentList -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256Manifest {
    <#
        .SYNOPSIS
        Runs `shasum -a 256` over the given paths (relative to WorkingDirectory)
        and returns the lines sorted the same way as the Bash scripts' `sort`
        (ordinal byte order, matching `LC_ALL=C`).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$WorkingDirectory,
        [Parameter(Mandatory)]
        [string[]]$Path
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = & shasum -a 256 @Path
        if ($LASTEXITCODE -ne 0) {
            throw "shasum failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]$output)
    $lines.Sort([StringComparer]::Ordinal)
    return $lines
}

function Set-MacOsReproducibleTimestamps {
    <#
        .SYNOPSIS
        Sets every file/directory under Path to SOURCE_DATE_EPOCH, deepest
        entries first, matching the Bash scripts' Python `os.utime` walk.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [long]$Epoch
    )

    $time = [DateTimeOffset]::FromUnixTimeSeconds($Epoch).UtcDateTime
    $entries = Get-ChildItem -LiteralPath $Path -Recurse -Force |
        Sort-Object -Property @{ Expression = { ($_.FullName -split '/').Count } } -Descending
    foreach ($entry in $entries) {
        $entry.LastWriteTimeUtc = $time
        $entry.LastAccessTimeUtc = $time
    }
    $root = Get-Item -LiteralPath $Path -Force
    $root.LastWriteTimeUtc = $time
    $root.LastAccessTimeUtc = $time
}

Export-ModuleMember -Function `
    Import-MacOsPackageEnv, `
    Get-MacOsSourceDateEpoch, `
    Assert-Darwin, `
    Assert-Command, `
    Assert-MacOsVersion, `
    Get-MacOsExpectedArch, `
    Invoke-Native, `
    Get-Sha256Manifest, `
    Set-MacOsReproducibleTimestamps
