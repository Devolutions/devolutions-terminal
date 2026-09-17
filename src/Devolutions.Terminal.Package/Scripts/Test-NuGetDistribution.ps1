[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackageDirectory,

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string] $Version = "2026.3.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packageSource = [IO.Path]::GetFullPath($PackageDirectory)
$expectedPackageNames = @(
    "Devolutions.Terminal.App.$Version.nupkg"
    "Devolutions.Terminal.App.win-x64.$Version.nupkg"
    "Devolutions.Terminal.App.win-arm64.$Version.nupkg"
    "Devolutions.Terminal.App.linux-x64.$Version.nupkg"
    "Devolutions.Terminal.App.linux-arm64.$Version.nupkg"
    "Devolutions.Terminal.App.osx-x64.$Version.nupkg"
    "Devolutions.Terminal.App.osx-arm64.$Version.nupkg"
)
foreach ($packageName in $expectedPackageNames) {
    $packagePath = Join-Path $packageSource $packageName
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected NuGet package '$packagePath' was not found."
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("devolutions-terminal-nuget-" + [guid]::NewGuid())
$projectPath = Join-Path $testRoot "Consumer.csproj"
$nuGetConfigPath = Join-Path $testRoot "NuGet.Config"
$originalNugetPackages = $env:NUGET_PACKAGES

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    $env:NUGET_PACKAGES = Join-Path $testRoot "packages"
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageSource" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nuGetConfigPath -Encoding utf8NoBOM

    $runtimeIdentifiers = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
    foreach ($runtimeIdentifier in $runtimeIdentifiers) {
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>$runtimeIdentifier</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Devolutions.Terminal.App" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8NoBOM

        & dotnet restore $projectPath --configfile $nuGetConfigPath --no-cache --force
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed for '$runtimeIdentifier' with exit code $LASTEXITCODE."
        }

        & dotnet build $projectPath -c Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed for '$runtimeIdentifier' with exit code $LASTEXITCODE."
        }

        $outputDirectory = Join-Path $testRoot "bin\Release\net10.0\$runtimeIdentifier"
        $executableName = if ($runtimeIdentifier.StartsWith("win-", [StringComparison]::Ordinal)) { "dt.exe" } else { "dt" }
        $payloadPath = Join-Path $outputDirectory "runtimes\$runtimeIdentifier\native\payload\$executableName"
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Package payload '$payloadPath' was not copied to the consumer output."
        }

        $otherPayloads = @(Get-ChildItem -LiteralPath (Join-Path $outputDirectory "runtimes") -Directory |
            Where-Object Name -ne $runtimeIdentifier)
        if ($otherPayloads.Count -ne 0) {
            throw "Unexpected runtime payloads were copied for '$runtimeIdentifier': $($otherPayloads.Name -join ', ')."
        }
    }

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Devolutions.Terminal.App" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8NoBOM

    & dotnet restore $projectPath --configfile $nuGetConfigPath --no-cache --force
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for the default runtime with exit code $LASTEXITCODE."
    }

    & dotnet build $projectPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for the default runtime with exit code $LASTEXITCODE."
    }

    $defaultPayloadPath = Join-Path $testRoot "bin\Release\net10.0\runtimes\win-x64\native\payload\dt.exe"
    if (-not (Test-Path -LiteralPath $defaultPayloadPath -PathType Leaf)) {
        throw "Default win-x64 package payload '$defaultPayloadPath' was not copied to the consumer output."
    }
}
finally {
    $env:NUGET_PACKAGES = $originalNugetPackages
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
