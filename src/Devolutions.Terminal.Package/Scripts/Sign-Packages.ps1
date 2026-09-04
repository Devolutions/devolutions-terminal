[CmdletBinding(DefaultParameterSetName = "ArtifactSigning")]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern("^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$")]
    [string] $Version,

    [Parameter(ParameterSetName = "Certificate", Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $CertificatePath,

    [Parameter(ParameterSetName = "Certificate")]
    [securestring] $Password,

    [Parameter(ParameterSetName = "ArtifactSigning", Mandatory)]
    [string] $ArtifactSigningEndpoint,

    [Parameter(ParameterSetName = "ArtifactSigning", Mandatory)]
    [string] $ArtifactSigningAccountName,

    [Parameter(ParameterSetName = "ArtifactSigning", Mandatory)]
    [string] $ArtifactSigningProfileName,

    [Parameter(ParameterSetName = "ArtifactSigning", Mandatory)]
    [string] $AzureTenantId,

    [Parameter(ParameterSetName = "ArtifactSigning", Mandatory)]
    [string] $ClientId,

    [Parameter(ParameterSetName = "ArtifactSigning", Mandatory)]
    [string] $ClientSecret,

    [Parameter(ParameterSetName = "ArtifactSigning")]
    [string] $TimestampServer = "http://timestamp.acs.microsoft.com/",

    [string] $PsignTool
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$useArtifactSigning = $PSCmdlet.ParameterSetName -eq "ArtifactSigning"

function Get-ExpectedPackages {
    param(
        [string[]] $Names
    )

    $matches = @()
    foreach ($name in $Names) {
        $path = Join-Path $PackageDirectory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $matches += Get-Item -LiteralPath $path
        }
    }

    return $matches
}

function Resolve-PsignTool {
    param(
        [string] $PreferredPath
    )

    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        if (-not (Test-Path -LiteralPath $PreferredPath -PathType Leaf)) {
            throw "psign-tool was not found at '$PreferredPath'."
        }

        return (Get-Item -LiteralPath $PreferredPath).FullName
    }

    $command = Get-Command psign-tool -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "psign-tool is required for Azure Artifact Signing. Install Devolutions.Psign.Tool or download psign-tool from Devolutions/psign."
    }

    return $command.Source
}

function Invoke-PsignArtifactSign {
    param(
        [string] $ToolPath,
        [string[]] $Files
    )

    $Files = @($Files | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($Files.Count -eq 0) {
        return
    }

    $metadataPath = Join-Path ([IO.Path]::GetTempPath()) ("artifact-signing-" + [guid]::NewGuid().ToString("N") + ".json")
    $fileListPath = Join-Path ([IO.Path]::GetTempPath()) ("psign-files-" + [guid]::NewGuid().ToString("N") + ".txt")
    try {
        $metadata = [ordered]@{
            Endpoint = $ArtifactSigningEndpoint
            CodeSigningAccountName = $ArtifactSigningAccountName
            CertificateProfileName = $ArtifactSigningProfileName
        }
        $metadata | ConvertTo-Json -Compress | Set-Content -LiteralPath $metadataPath -Encoding utf8
        Set-Content -LiteralPath $fileListPath -Value $Files -Encoding utf8

        & $ToolPath --mode portable --verbose sign `
            --dmdf $metadataPath `
            --artifact-signing-tenant-id $AzureTenantId `
            --artifact-signing-client-id $ClientId `
            --artifact-signing-client-secret $ClientSecret `
            --timestamp-url $TimestampServer `
            --timestamp-digest sha256 `
            --digest sha256 `
            --input-file-list $fileListPath
        if ($LASTEXITCODE -ne 0) {
            throw "psign-tool Artifact Signing failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item -LiteralPath $metadataPath, $fileListPath -ErrorAction SilentlyContinue
    }
}

function Invoke-LocalSign {
    param(
        [System.IO.FileInfo] $Package,
        [string] $PlainTextPassword
    )

    $extension = $Package.Extension.ToLowerInvariant()
    if ($extension -eq ".msi") {
        if (-not (Get-Command signtool -ErrorAction SilentlyContinue)) {
            throw "signtool.exe is required to sign MSI packages with a local certificate."
        }

        & signtool sign /fd SHA256 /td SHA256 /v /tr http://timestamp.digicert.com /n "Devolutions Inc." /a /f $CertificatePath /p $PlainTextPassword $Package.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "Signing '$($Package.Name)' failed with exit code $LASTEXITCODE."
        }

        return
    }

    & winapp sign $Package.FullName $CertificatePath --password $PlainTextPassword
    if ($LASTEXITCODE -ne 0) {
        throw "Signing '$($Package.Name)' failed with exit code $LASTEXITCODE."
    }
}

if ($useArtifactSigning) {
    if ([string]::IsNullOrWhiteSpace($TimestampServer)) {
        $TimestampServer = "http://timestamp.acs.microsoft.com/"
    }

    $PsignTool = Resolve-PsignTool -PreferredPath $PsignTool
}
else {
    if ($null -eq $Password) {
        $Password = Read-Host "Signing certificate password" -AsSecureString
    }

    $CertificatePath = [IO.Path]::GetFullPath($CertificatePath)
    if (-not (Get-Command winapp -ErrorAction SilentlyContinue) -and -not (Get-Command signtool -ErrorAction SilentlyContinue)) {
        throw "Local certificate signing requires either WinApp CLI or signtool on PATH."
    }
}

$msixPackages = Get-ExpectedPackages -Names @(
    "Devolutions.Terminal_${Version}_x64.msix",
    "Devolutions.Terminal_${Version}_arm64.msix"
)
$msiPackages = Get-ExpectedPackages -Names @(
    "Devolutions.Terminal_${Version}_x64.msi",
    "Devolutions.Terminal_${Version}_arm64.msi"
)

if ($msixPackages.Count -eq 0 -and $msiPackages.Count -eq 0) {
    throw "No release packages for version '$Version' were found in '$PackageDirectory'."
}

$pointer = $null
$plainTextPassword = $null
try {
    if ($useArtifactSigning) {
        $unsignedBundle = Join-Path $PackageDirectory "Devolutions.Terminal_${Version}_x64_arm64.msixbundle"
        if (Test-Path -LiteralPath $unsignedBundle -PathType Leaf) {
            Remove-Item -LiteralPath $unsignedBundle -Force
        }

        $filesToSign = @()
        if ($msixPackages.Count -gt 0) {
            $filesToSign += @($msixPackages.FullName)
        }
        if ($msiPackages.Count -gt 0) {
            $filesToSign += @($msiPackages.FullName)
        }

        Invoke-PsignArtifactSign -ToolPath $PsignTool -Files $filesToSign
        return
    }

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Password)
    $plainTextPassword = [Runtime.InteropServices.Marshal]::PtrToStringUni($pointer)

    foreach ($package in $msixPackages) {
        Invoke-LocalSign -Package $package -PlainTextPassword $plainTextPassword
    }

    if ($msixPackages.Count -gt 1) {
        if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
            throw "WinApp CLI is required to rebuild the signed MSIX bundle."
        }

        $bundleInput = Join-Path $PackageDirectory (".bundle-input-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $bundleInput | Out-Null
        try {
            Copy-Item -LiteralPath $msixPackages.FullName -Destination $bundleInput
            $bundlePath = Join-Path $PackageDirectory "Devolutions.Terminal_${Version}_x64_arm64.msixbundle"
            & winapp tool makeappx bundle /d $bundleInput /p $bundlePath /bv $Version /o
            if ($LASTEXITCODE -ne 0) {
                throw "Rebuilding the signed MSIX bundle failed with exit code $LASTEXITCODE."
            }

            Invoke-LocalSign -Package (Get-Item -LiteralPath $bundlePath) -PlainTextPassword $plainTextPassword
        }
        finally {
            if (Test-Path -LiteralPath $bundleInput) {
                Remove-Item -Recurse -Force -LiteralPath $bundleInput
            }
        }
    }

    foreach ($package in $msiPackages) {
        Invoke-LocalSign -Package $package -PlainTextPassword $plainTextPassword
    }
}
finally {
    $plainTextPassword = $null
    if ($null -ne $pointer) {
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($pointer)
    }
}
