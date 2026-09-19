<#
.SYNOPSIS
    Validates that an MSIX package's physical zip entry order matches the strict
    ordering that Windows' native AppX package reader (IAppxFactory / Add-AppxPackage)
    requires, independently of any tool-specific "unpack" validation.

.DESCRIPTION
    Windows' AppX/MSIX block map validation is stricter than "do the per-block SHA256
    hashes match the file contents": the physical file order inside the package's zip
    archive must also match the order listed in AppxBlockMap.xml, and the payload
    portion of that order must be a case-insensitive ordinal ascending sort. A package
    that violates this ordering is rejected with HRESULT 0x80080205 ("The Appx
    package's block map is invalid") even though every individual block hash is
    correct - this is not caught by hash verification alone, and (depending on the
    tool) may not be caught by an "unpack" round trip either.

    This check is pure .NET/PowerShell (System.IO.Compression + XML), so it runs
    identically on Windows, Linux, and macOS runners without depending on the
    Windows-only AppX packaging APIs (winapp/makeappx, Add-AppxPackage, etc.). This
    makes it suitable for validating packages *after* they have been re-signed on a
    non-Windows runner, which is precisely when this class of bug was discovered:
    the build step produced a correctly validated package, but the shipped release
    asset still had a corrupt block map.

.PARAMETER PackagePath
    Path(s) to one or more .msix files to validate.

.EXAMPLE
    ./Test-MsixBlockMapOrder.ps1 -PackagePath ./artifacts/msix-packages/Devolutions.Terminal_2026.3.2.0_x64.msix
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string[]] $PackagePath
)

begin {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = "Stop"
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Files that are appended after the sorted payload and are not part of the
    # ordinal sort themselves. AppxManifest.xml is listed inside AppxBlockMap.xml
    # (always last), while AppxBlockMap.xml/[Content_Types].xml/AppxSignature.p7x
    # are not part of the block map's own File list at all.
    $script:TrailingEntryOrder = @(
        "AppxManifest.xml",
        "AppxBlockMap.xml",
        "[Content_Types].xml",
        "AppxSignature.p7x"
    )

    function Test-OneMsix {
        param([string] $Path)

        $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $physicalOrder = @($zip.Entries | ForEach-Object { $_.FullName })

            $blockMapEntry = $zip.GetEntry("AppxBlockMap.xml")
            if ($null -eq $blockMapEntry) {
                throw "'$Path' does not contain an AppxBlockMap.xml entry."
            }

            $reader = [IO.StreamReader]::new($blockMapEntry.Open())
            try {
                [xml] $blockMap = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }

            $ns = [Xml.XmlNamespaceManager]::new($blockMap.NameTable)
            $ns.AddNamespace("b", "http://schemas.microsoft.com/appx/2010/blockmap")
            # AppxBlockMap.xml stores Name attributes with backslash path separators,
            # while the physical zip entries always use forward slashes; normalize both
            # to forward slashes so the ordering comparison below is separator-agnostic.
            $blockMapOrder = @($blockMap.SelectNodes("//b:File", $ns) | ForEach-Object { $_.Name -replace '\\', '/' })
            if ($blockMapOrder.Count -eq 0) {
                throw "AppxBlockMap.xml in '$Path' does not list any files."
            }

            # AppxManifest.xml is expected to be the last entry inside the block map's
            # own file list; everything before it is sortable payload.
            $payloadOrder = $blockMapOrder
            if ($blockMapOrder[-1] -eq "AppxManifest.xml") {
                $payloadOrder = $blockMapOrder[0..($blockMapOrder.Count - 2)]
            }

            # 1) The physical zip order must exactly match the block map's file order
            #    (plus the fixed trailing metadata entries not already listed inside the
            #    block map itself, i.e. AppxBlockMap.xml/[Content_Types].xml/
            #    AppxSignature.p7x). A mismatch here means the block map does not
            #    describe the package's actual physical layout.
            $expectedTrailing = @(
                $script:TrailingEntryOrder |
                    Where-Object { $blockMapOrder -notcontains $_ } |
                    Where-Object { $physicalOrder -contains $_ }
            )
            $expectedOrder = @($blockMapOrder) + $expectedTrailing
            if (($physicalOrder.Count -ne $expectedOrder.Count) -or (Compare-Object $physicalOrder $expectedOrder -SyncWindow 0)) {
                throw (
                    "'$Path' has a physical zip entry order that does not match its " +
                    "AppxBlockMap.xml file order. Expected:`n$($expectedOrder -join "`n")`n`n" +
                    "Actual:`n$($physicalOrder -join "`n")"
                )
            }

            # 2) The sortable payload portion must be in strict ascending
            #    case-insensitive ordinal order, matching the order the native
            #    Windows AppX package reader (IAppxFactory) requires. Windows
            #    rejects a package with HRESULT 0x80080205 ("The Appx package's
            #    block map is invalid") if this is violated, even when every
            #    individual block hash is correct.
            for ($i = 1; $i -lt $payloadOrder.Count; $i++) {
                $previous = $payloadOrder[$i - 1]
                $current = $payloadOrder[$i]
                if ([string]::Compare($previous, $current, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw (
                        "'$Path' has an invalid payload file order: '$previous' must sort " +
                        "strictly before '$current' (case-insensitive ordinal comparison) for " +
                        "the package's block map to be considered valid by Windows. This " +
                        "package will fail to install with 0x80080205 ('The Appx package's " +
                        "block map is invalid')."
                    )
                }
            }

            Write-Host "OK: '$Path' has a valid block map / payload ordering ($($payloadOrder.Count) payload files)."
        }
        finally {
            $zip.Dispose()
        }
    }
}

process {
    foreach ($path in $PackagePath) {
        Test-OneMsix -Path ([IO.Path]::GetFullPath($path))
    }
}
