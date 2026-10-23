#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Validates the native and build assets of a Devolutions.Terminal.Control package.

.DESCRIPTION
Checks that the package carries the bundled assemblies, the buildTransitive targets,
the ConPTY hosts, and a dt-pty-host of the right format and architecture for every
Unix RID, so local shells can start on Windows, Linux and macOS.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $PackagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ELF e_machine and Mach-O cputype for each Unix RID.
$ptyHosts = [ordered]@{
    'linux-x64'   = @{ Format = 'ELF'; Machine = 0x3E }
    'linux-arm64' = @{ Format = 'ELF'; Machine = 0xB7 }
    'osx-x64'     = @{ Format = 'Mach-O'; Machine = 0x01000007 }
    'osx-arm64'   = @{ Format = 'Mach-O'; Machine = 0x0100000C }
}
$requiredEntries = @(
    'lib/net10.0/Devolutions.Terminal.Control.dll',
    'lib/net10.0/Devolutions.Terminal.Connection.dll',
    'buildTransitive/Devolutions.Terminal.Control.targets',
    'buildTransitive/ConPtyHost.targets',
    'buildTransitive/UnixPtyHost.targets',
    'buildTransitive/THIRD-PARTY-NOTICES-CONPTY.txt',
    'tools/conpty/x86/OpenConsole.exe',
    'tools/conpty/x64/OpenConsole.exe',
    'tools/conpty/arm64/OpenConsole.exe'
)

function Read-Header([IO.Compression.ZipArchiveEntry] $Entry) {
    $buffer = [byte[]]::new(20)
    $stream = $Entry.Open()
    try {
        $read = 0
        while ($read -lt $buffer.Length) {
            $count = $stream.Read($buffer, $read, $buffer.Length - $read)
            if ($count -eq 0) { break }
            $read += $count
        }
        if ($read -lt $buffer.Length) {
            throw "$($Entry.FullName) is too small to be a native executable ($read bytes)."
        }
    }
    finally {
        $stream.Dispose()
    }
    return $buffer
}

$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) {
        $entries[$entry.FullName] = $entry
    }

    foreach ($name in $requiredEntries) {
        if (-not $entries.ContainsKey($name)) {
            throw "Package is missing $name."
        }
    }

    foreach ($rid in $ptyHosts.Keys) {
        $name = "runtimes/$rid/native/dt-pty-host"
        if (-not $entries.ContainsKey($name)) {
            throw "Package is missing $name; local shells cannot start on $rid."
        }

        $header = Read-Header $entries[$name]
        $expected = $ptyHosts[$rid]
        if ($expected.Format -eq 'ELF') {
            $isElf64 = $header[0] -eq 0x7F -and $header[1] -eq 0x45 -and $header[2] -eq 0x4C -and
                $header[3] -eq 0x46 -and $header[4] -eq 2
            $machine = [BitConverter]::ToUInt16($header, 18)
            if (-not $isElf64 -or $machine -ne $expected.Machine) {
                throw "$name is not a 64-bit ELF executable for $rid (e_machine 0x$($machine.ToString('X')))."
            }
        }
        else {
            $magic = [BitConverter]::ToUInt32($header, 0)
            $cpuType = [BitConverter]::ToUInt32($header, 4)
            if ($magic -ne 0xFEEDFACFu -or $cpuType -ne $expected.Machine) {
                throw "$name is not a 64-bit Mach-O executable for $rid (cputype 0x$($cpuType.ToString('X')))."
            }
        }
        Write-Host "ControlNuGetPackage: $name is a $($expected.Format) $rid executable ($($entries[$name].Length) bytes)."
    }

    $expectedHostEntries = @($ptyHosts.Keys | ForEach-Object { "runtimes/$_/native/dt-pty-host" })
    $unexpected = @($archive.Entries | Where-Object {
        $_.Name -eq 'dt-pty-host' -and $_.FullName -notin $expectedHostEntries
    })
    if ($unexpected.Count -gt 0) {
        throw "Package has unexpected dt-pty-host entries: $($unexpected.FullName -join ', ')."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "ControlNuGetPackage: $PackagePath carries assemblies, build targets, ConPTY hosts and all Unix PTY hosts."
