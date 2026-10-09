#Requires -Version 7
[CmdletBinding()]
param(
    [string]$DestinationRoot = (Join-Path $PSScriptRoot '..\artifacts\test-powershell'),
    [ValidateSet('7.4.6', '7.4.20', '7.5.11', '7.6.6')]
    [string]$Version = '7.6.6'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { throw 'Unsupported test platform.' }
$rid = "$platform-$architecture"
$releases = @{
    '7.4.6' = @{
        'win-x64' = 'ed49ce5adb2162cc4a835d740486be729ba904627cca71fcb6c2b95be11b993d'
        'win-arm64' = '875af8ae039abb583976129b8508c7cc39f0371ae790db096561e44019da0165'
        'linux-x64' = '6f6015203c47806c5cc444c19d8ed019695e610fbd948154264bf9ca8e157561'
        'linux-arm64' = 'c0159b03e85f44ae1e7697818a011558da6c813d0aae848bf5ac13bf435d8624'
        'osx-x64' = '7a18daed105b7cfc80bf8cc00762fe7990105dd23f951cc32ceb744651650e3d'
        'osx-arm64' = 'a482d668787ef98c37f0a5a7696107dffdb3dc340c5be3d1c153ec9d239072a8'
    }
    '7.4.20' = @{
        'win-x64' = 'fb88cd3731847006b157978d6c0a426390d346696d91d1ed85b5e5eb88cb2d40'
        'win-arm64' = '67eff749d5dae357501c95a3eec1dcd93f669824e3ca2b1d2adcdecdebf04f35'
        'linux-x64' = 'ed8008345c7f5337f5e293145f5e243ce132e5ee4dfc24ac551c45a4b3092181'
        'linux-arm64' = '4d1a7b468eae627809eac5fae79c4e645cd9362fdf9f626298686b4489eb4724'
        'osx-x64' = '634d04e806b37a441b140c875e96662a5eb14efd0e38060a730b991069602138'
        'osx-arm64' = '7f9a2503304ee239330f5c4ccc7eef11894b095d7ccb8f9fa78a16c6ee508edc'
    }
    '7.5.11' = @{
        'win-x64' = '75cdab18db9c8ac32f02e82149698166551e42d2a904da4b3a60fb5fcb3ad021'
        'win-arm64' = '990734d5e116d3709791f261a80925aa8c4f38d51f60656c7e36ae1866a4279c'
        'linux-x64' = '82a8b13d92b0f3ae48e56cf2f3f7961679371736ca90145ca71617c2913ba9d8'
        'linux-arm64' = '830ebda118c731ece3fa7e6b7e8573a21346387cbbca5b2f5e3b9bfe24f96672'
        'osx-x64' = '44828c7173cddb335570a76089f2831b9082997eabafd599b115aaa944f90e69'
        'osx-arm64' = '93c5d5b71a3937e3dbb68934886d318689f57ecda466443764a9d7101f196e6f'
    }
    '7.6.6' = @{
        'win-x64' = '02fe458be20493fbdf43f61ea20610b811ee6c738ab1676c61b9cfcd1a33c860'
        'win-arm64' = 'bbde9dda31d148415eccb5fbe1638e6400a144187b006e5b3fd8ec2f39d781be'
        'linux-x64' = 'ddbc4a2d113bbd46d283cfedcbcd117a70caefd7673f41f2b4e0000badf103bc'
        'linux-arm64' = '924829e54c983648f6f1419a2dc7f9433c861b2fb5bd57736ff096c24f133729'
        'osx-x64' = 'e325ed9f666894eb39a5ea52800b602da2fb4242bbe9747ceddb39cdc66de805'
        'osx-arm64' = '6df833d094ebac1c1a74340d7b3437f4aaf5e03ce640484a1c4359f3ce8b3db1'
    }
}
$hashes = $releases[$Version]
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
