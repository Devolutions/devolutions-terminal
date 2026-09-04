[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string[]] $Architectures = @("x64", "arm64"),

    [ValidatePattern("^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    [string] $Version = "0.1.0.0",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $OutputDirectory,

    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageRoot = Split-Path -Parent $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $packageRoot "..\.."))
$hostProject = Join-Path $dotnetRoot "src\Devolutions.Terminal\Devolutions.Terminal.csproj"
$installerProject = Join-Path $dotnetRoot "src\Devolutions.Terminal.Installer\Devolutions.Terminal.Installer.wixproj"

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $dotnetRoot "artifacts\msi"
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

foreach ($architecture in $Architectures) {
    $runtimeIdentifier = "win-$architecture"
    $layout = Join-Path $layoutRoot $runtimeIdentifier
    if (-not $SkipPublish) {
        if (Test-Path -LiteralPath $layout) {
            Remove-Item -Recurse -Force -LiteralPath $layout
        }

        New-Item -ItemType Directory -Force -Path $layout | Out-Null
        Invoke-Checked -FilePath dotnet -ArgumentList @(
            "publish",
            $hostProject,
            "-c", $Configuration,
            "-r", $runtimeIdentifier,
            "--self-contained",
            "-o", $layout
        )
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $layout "Devolutions.Terminal.exe"))) {
        throw "Published output for '$runtimeIdentifier' was not found at '$layout'."
    }

    $buildDirectory = [IO.Path]::GetFullPath((Join-Path $packageOutput $architecture))
    New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null

    $generatedComponents = Join-Path $dotnetRoot "src\Devolutions.Terminal.Installer\GeneratedProductComponents.wxs"
    & (Join-Path $PSScriptRoot "Write-MsiComponents.ps1") -PublishDir $layout -OutputFile $generatedComponents
    if ($LASTEXITCODE -ne 0) {
        throw "Generating MSI component list failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $generatedComponents -PathType Leaf)) {
        throw "MSI component list was not written to '$generatedComponents'."
    }

    $platform = if ($architecture -eq "arm64") { "ARM64" } else { "x64" }
    Invoke-Checked -FilePath dotnet -ArgumentList @(
        "build",
        $installerProject,
        "-c", $Configuration,
        "-p:Platform=$platform",
        "-p:ProductVersion=$Version",
        "-p:OutputPath=$buildDirectory\\",
        "-p:OutputName=Devolutions.Terminal_${Version}_${architecture}"
    )

    $generatedMsi = Join-Path $buildDirectory "Devolutions.Terminal_${Version}_${architecture}.msi"
    if (-not (Test-Path -LiteralPath $generatedMsi -PathType Leaf)) {
        $generatedMsi = (Get-ChildItem -LiteralPath $buildDirectory -Filter "*.msi" -File | Sort-Object Name | Select-Object -First 1).FullName
    }

    if (-not (Test-Path -LiteralPath $generatedMsi -PathType Leaf)) {
        throw "No MSI artifact was created for '$runtimeIdentifier'."
    }

    Copy-Item -Force -LiteralPath $generatedMsi -Destination (Join-Path $packageOutput ([IO.Path]::GetFileName($generatedMsi)))
}

Get-ChildItem -LiteralPath $packageOutput -File -Filter "*.msi" |
    Sort-Object Name
