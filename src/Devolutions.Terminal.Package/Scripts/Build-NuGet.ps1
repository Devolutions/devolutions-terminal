[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string[]] $RuntimeIdentifiers = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),

    [ValidatePattern("^\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    [string] $Version = "2026.3.0",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $OutputDirectory,

    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $packageRoot "..\.."))
$hostProject = Join-Path $repoRoot "src\Devolutions.Terminal\Devolutions.Terminal.csproj"
$distributionProject = Join-Path $repoRoot "src\Devolutions.Terminal.Distribution\Devolutions.Terminal.Distribution.csproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\nuget"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$layoutRoot = Join-Path $OutputDirectory "layout"
$packageOutput = Join-Path $OutputDirectory "packages"
New-Item -ItemType Directory -Force -Path $layoutRoot, $packageOutput | Out-Null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

foreach ($runtimeIdentifier in $RuntimeIdentifiers) {
    $layout = Join-Path $layoutRoot $runtimeIdentifier
    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $layout) {
            Remove-Item -Recurse -Force -LiteralPath $layout
        }

        New-Item -ItemType Directory -Force -Path $layout | Out-Null
        Invoke-Checked dotnet @(
            "publish", $hostProject,
            "-c", $Configuration,
            "-r", $runtimeIdentifier,
            "--self-contained",
            "-p:VersionPrefix=$Version",
            "-o", $layout
        )
    }
    else {
        $executableName = if ($runtimeIdentifier.StartsWith("win-", [StringComparison]::Ordinal)) { "dt.exe" } else { "dt" }
        if (-not (Test-Path -LiteralPath (Join-Path $layout $executableName))) {
            throw "Published output for '$runtimeIdentifier' was not found at '$layout'."
        }
    }
}

Invoke-Checked dotnet @(
    "pack", $distributionProject,
    "-c", $Configuration,
    "-p:PackageVersion=$Version",
    "-p:PackageOutputPath=$packageOutput\"
)

Get-ChildItem -LiteralPath $packageOutput -File -Filter "*.nupkg" |
    Sort-Object Name
