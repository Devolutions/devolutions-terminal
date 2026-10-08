#Requires -Version 7
[CmdletBinding()]
param([string]$DestinationRoot = (Join-Path $PSScriptRoot '..\artifacts\test-powershell'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$version = '7.6.6'
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { throw 'Unsupported test platform.' }
$rid = "$platform-$architecture"
$hashes = @{
    'win-x64' = '02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860'
    'win-arm64' = 'bbde9dda31d148415eccb5fbe1638e6400a144187b006e5b3fd8ec2f39d781be'
    'linux-x64' = 'ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc'
    'linux-arm64' = '924829e54c983648f6f1419a2dc7f9433c861b2fb5bd57736ff096c24f133729'
    'osx-x64' = 'e325ed9f666894eb39a5ea52800b602da2fb4242bbe9747ceddb39cdc66de805'
    'osx-arm64' = '6df833d094ebac1c1a74340d7b3437f4aaf5e03ce640484a1c4359f3ce8b3db1'
}
if (-not $hashes.ContainsKey($rid)) { throw "No pinned PowerShell test runtime for $rid." }
$root = [IO.Path]::GetFullPath($DestinationRoot)
$engineDirectory = Join-Path $root "$version-$rid"
$executable = Join-Path $engineDirectory $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    New-Item -ItemType Directory -Path $engineDirectory -Force | Out-Null
    $asset = if ($IsWindows) { "PowerShell-$version-$rid.zip" } else { "powershell-$version-$rid.tar.gz" }
    $archive = Join-Path $root $asset
    Invoke-WebRequest "https://github.com/PowerShell/PowerShell/releases/download/v$version/$asset" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hashes[$rid]) {
        throw "PowerShell test runtime checksum mismatch: $archive"
    }
    if ($IsWindows) {
        Expand-Archive -LiteralPath $archive -DestinationPath $engineDirectory -Force
    } else {
        & tar -xzf $archive -C $engineDirectory
        & chmod +x $executable
    }
    Remove-Item -LiteralPath $archive
}
$actual = & $executable -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()'
if ($actual -ne $version) { throw "Expected PowerShell $version at $engineDirectory; found $actual." }
$env:DT_ISEBERG_PSHOME = $engineDirectory
if ($env:GITHUB_ENV) { "DT_ISEBERG_PSHOME=$engineDirectory" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append }
Write-Host "Test-only PowerShell ${version}: $engineDirectory (never include this directory in DT packages)."
