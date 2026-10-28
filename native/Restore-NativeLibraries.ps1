[CmdletBinding()]
param(
    [string] $Rid,
    [string] $ZigPath,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Zig.ps1")

$nativeRoot = $PSScriptRoot
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $nativeRoot ".."))
$zigVersion = Get-PinnedZigVersion

if ([string]::IsNullOrWhiteSpace($Rid)) {
    $Rid = Get-HostRid
}

# dt-pty-host is the only native helper built from source, and only Unix RIDs use it.
$isUnixRid = $Rid.StartsWith("linux-", [StringComparison]::Ordinal) -or
    $Rid.StartsWith("osx-", [StringComparison]::Ordinal)
if (-not $isUnixRid) {
    Write-Host "No native libraries required for $Rid."
    return
}

$mutex = [System.Threading.Mutex]::new($false, "Devolutions.Terminal.NativeRestore")
$acquired = $false
try {
    $acquired = $mutex.WaitOne()
    $output = Join-Path $nativeRoot "linux-pty\$Rid\dt-pty-host"
    if ($Force -or -not (Test-Path -LiteralPath $output)) {
        $ptyArgs = @{
            Rid = $Rid
            ZigPath = Resolve-ZigPath -ZigPath $ZigPath -Version $zigVersion -DotnetRoot $dotnetRoot -Install
        }
        if ($Force) {
            $ptyArgs.Force = $true
        }

        & (Join-Path (Join-Path $nativeRoot "linux-pty") "Build-LinuxPtyHost.ps1") @ptyArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Build-LinuxPtyHost.ps1 failed for $Rid."
        }
    }
    else {
        Write-Host "Using existing $output"
    }
}
finally {
    if ($acquired) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}
