Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-HostWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Test-HostLinux {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Linux)
}

function Test-HostMacOS {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)
}

function Get-HostRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archRid = switch ($arch) {
        "X64" { "x64" }
        "Arm64" { "arm64" }
        default { throw "Unsupported host architecture '$arch'." }
    }

    if (Test-HostWindows) {
        return "win-$archRid"
    }

    if (Test-HostLinux) {
        return "linux-$archRid"
    }

    if (Test-HostMacOS) {
        return "osx-$archRid"
    }

    throw "Unsupported host OS."
}

function Get-HostZigTriple {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archName = switch ($arch) {
        "X64" { "x86_64" }
        "Arm64" { "aarch64" }
        default { throw "Unsupported host architecture '$arch'." }
    }

    if (Test-HostWindows) {
        return "$archName-windows"
    }

    if (Test-HostLinux) {
        return "$archName-linux"
    }

    if (Test-HostMacOS) {
        return "$archName-macos"
    }

    throw "Unsupported host OS."
}

function Install-Zig {
    param(
        [Parameter(Mandatory)]
        [string] $Version,
        [Parameter(Mandatory)]
        [string] $DotnetRoot
    )

    $triple = Get-HostZigTriple
    $extension = if (Test-HostWindows) { "zip" } else { "tar.xz" }
    $archiveName = "zig-$triple-$Version.$extension"
    $url = "https://ziglang.org/download/$Version/$archiveName"
    $toolRoot = Join-Path $DotnetRoot "artifacts\tools"
    $extractRoot = Join-Path $toolRoot "zig-$Version"
    $marker = Join-Path $extractRoot ".downloaded"
    New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null

    if (-not (Test-Path -LiteralPath $marker)) {
        $archivePath = Join-Path $toolRoot $archiveName
        Write-Host "Downloading $url"
        # Windows PowerShell (used by MSBuild) downloads much slower while rendering progress.
        $previousProgress = $ProgressPreference
        $ProgressPreference = "SilentlyContinue"
        try {
            Invoke-WebRequest -Uri $url -OutFile $archivePath
        }
        finally {
            $ProgressPreference = $previousProgress
        }
        if (Test-Path -LiteralPath $extractRoot) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }

        if ($extension -eq "zip") {
            # Expand-Archive takes many minutes on Zig's thousands of small files under Windows PowerShell.
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extractRoot)
        }
        else {
            New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
            & tar -xJf $archivePath -C $extractRoot
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to extract $archivePath."
            }
        }

        Set-Content -LiteralPath $marker -Value $url
    }

    $zigName = if (Test-HostWindows) { "zig.exe" } else { "zig" }
    $found = Get-ChildItem -LiteralPath $extractRoot -Filter $zigName -Recurse -File |
        Select-Object -First 1
    if ($null -eq $found) {
        throw "Zig $Version was extracted but '$zigName' was not found under '$extractRoot'."
    }

    return $found.FullName
}

function Get-ZigVersion {
    param(
        [Parameter(Mandatory)]
        [string] $ZigPath
    )

    try {
        $output = & $ZigPath version 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        return ([string]$output).Trim()
    }
    catch {
        return $null
    }
}

# Zig's build API changes between minor releases, so only the pinned major.minor can build the
# pinned sources; patch releases (and dev builds of that minor) are accepted.
function Test-ZigVersionCompatible {
    param(
        [string] $Actual,
        [Parameter(Mandatory)]
        [string] $Required
    )

    if ([string]::IsNullOrWhiteSpace($Actual)) {
        return $false
    }

    $actualCore = ($Actual -split '[-+]', 2)[0]
    $requiredCore = ($Required -split '[-+]', 2)[0]
    $actualVersion = $null
    $requiredVersion = $null
    if (-not [version]::TryParse($actualCore, [ref]$actualVersion) -or
        -not [version]::TryParse($requiredCore, [ref]$requiredVersion)) {
        return $false
    }

    return $actualVersion.Major -eq $requiredVersion.Major -and $actualVersion.Minor -eq $requiredVersion.Minor
}

function Resolve-ZigPath {
    param(
        [string] $ZigPath,
        [Parameter(Mandatory)]
        [string] $Version,
        [Parameter(Mandatory)]
        [string] $DotnetRoot,
        [switch] $Install
    )

    if (-not [string]::IsNullOrWhiteSpace($ZigPath) -and (Test-Path -LiteralPath $ZigPath)) {
        $explicitVersion = Get-ZigVersion -ZigPath $ZigPath
        if (-not (Test-ZigVersionCompatible -Actual $explicitVersion -Required $Version)) {
            Write-Warning "Using explicit Zig '$ZigPath' ($explicitVersion); the pinned sources expect Zig $Version."
        }

        return $ZigPath
    }

    # A different system Zig (for example a newer release on PATH) is ignored rather than failing
    # the build: the pinned version is used from, or downloaded into, artifacts/tools instead.
    $command = Get-Command zig -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        $pathVersion = Get-ZigVersion -ZigPath $command.Source
        if (Test-ZigVersionCompatible -Actual $pathVersion -Required $Version) {
            return $command.Source
        }

        Write-Host "Ignoring Zig $pathVersion at '$($command.Source)'; native libraries require Zig $Version."
    }

    $zigName = if (Test-HostWindows) { "zig.exe" } else { "zig" }
    $fallback = Join-Path $DotnetRoot "artifacts\tools\zig-$Version"
    $found = Get-ChildItem -LiteralPath $fallback -Filter $zigName -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $found) {
        return $found.FullName
    }

    if ($Install -or [string]::IsNullOrWhiteSpace($ZigPath)) {
        return Install-Zig -Version $Version -DotnetRoot $DotnetRoot
    }

    throw "Zig $Version was not found. Pass -ZigPath or allow automatic install."
}
