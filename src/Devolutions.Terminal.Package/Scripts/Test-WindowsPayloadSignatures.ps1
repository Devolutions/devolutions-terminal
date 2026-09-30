[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PayloadDirectory,

    # Exact certificate subject for our app executables only. Third-party binaries
    # may legitimately have another signer; all binaries must still be trusted.
    [ValidateNotNullOrEmpty()]
    [ValidateScript({ -not [string]::IsNullOrWhiteSpace($_) -and $_ -notmatch '[\r\n]' })]
    [string] $ExpectedPublisher
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
# Inspect the native exit code ourselves, including when the caller enables this preference.
$PSNativeCommandUseErrorActionPreference = $false

if (-not $IsWindows) {
    throw "Windows payload signature validation requires Windows."
}

$payloadPath = (Get-Item -LiteralPath $PayloadDirectory).FullName
$appExecutableNames = @("Devolutions.Terminal.exe", "dt.exe")
foreach ($name in $appExecutableNames) {
    $appPath = Join-Path $payloadPath $name
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw "Required Windows app executable '$appPath' was not found."
    }
}

Get-Command winapp -ErrorAction Stop | Out-Null

# Include nested host layouts, not just the executables at the payload root.
$binaries = @(Get-ChildItem -LiteralPath $payloadPath -Recurse -File -Force |
    Where-Object Extension -in @(".exe", ".dll") |
    Sort-Object @{ Expression = { $_.Name -notin $appExecutableNames } }, FullName)

foreach ($binary in $binaries) {
    $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
    if ($signature.Status -ne "Valid" -or $null -eq $signature.SignerCertificate) {
        throw "Windows payload signature validation failed for '$($binary.FullName)': Authenticode status '$($signature.Status)' (expected 'Valid'). $($signature.StatusMessage)"
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Windows payload signature validation failed for '$($binary.FullName)': timestamp certificate is missing."
    }
    if ($PSBoundParameters.ContainsKey("ExpectedPublisher") -and $binary.Name -in $appExecutableNames -and
        $signature.SignerCertificate.Subject -cne $ExpectedPublisher) {
        throw "Windows app signer for '$($binary.FullName)' is '$($signature.SignerCertificate.Subject)'; expected exact publisher '$ExpectedPublisher'."
    }

    $signatureOutput = & winapp tool signtool verify /pa /all /v /tw $binary.FullName 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $signatureOutput | Out-Host
        throw "SignTool signature validation failed for '$($binary.FullName)' with exit code $exitCode."
    }

    Write-Host "Verified trusted, timestamped Windows payload signature: $($binary.FullName)"
}

Write-Host "Validated $($binaries.Count) Windows payload binary signatures in '$payloadPath'."
