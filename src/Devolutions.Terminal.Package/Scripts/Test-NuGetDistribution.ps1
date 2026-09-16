[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath,

    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string] $Version = "2026.3.0",

    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string] $RuntimeIdentifier = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$packagePath = [IO.Path]::GetFullPath($PackagePath)
$packageSource = Split-Path -Parent $packagePath
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("devolutions-terminal-nuget-" + [guid]::NewGuid())
$projectPath = Join-Path $testRoot "Consumer.csproj"
$nuGetConfigPath = Join-Path $testRoot "NuGet.Config"
$outputDirectory = Join-Path $testRoot "bin\Release\net10.0\$RuntimeIdentifier"
$executableName = if ($RuntimeIdentifier.StartsWith("win-", [StringComparison]::Ordinal)) { "dt.exe" } else { "dt" }
$payloadPath = Join-Path $outputDirectory "runtimes\$RuntimeIdentifier\native\payload\$executableName"

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>$RuntimeIdentifier</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Devolutions.Terminal.App" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $projectPath -Encoding utf8NoBOM
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageSource" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nuGetConfigPath -Encoding utf8NoBOM

    & dotnet restore $projectPath --configfile $nuGetConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed with exit code $LASTEXITCODE."
    }

    & dotnet build $projectPath -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        throw "Package payload '$payloadPath' was not copied to the consumer output."
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
