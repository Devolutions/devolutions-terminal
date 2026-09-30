#Requires -Version 7
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $IsWindows) {
    throw "MSIX build and local signing regression tests require Windows."
}
if (-not (Get-Command winapp -ErrorAction SilentlyContinue)) {
    throw "MSIX build regression tests require WinApp CLI."
}

$packageRoot = Split-Path -Parent $PSScriptRoot
$sourceManifest = Join-Path $packageRoot "Package.appxmanifest"
$sourceHash = (Get-FileHash -LiteralPath $sourceManifest).Hash
$work = Join-Path ([IO.Path]::GetTempPath()) "terminal-msix-test-$([guid]::NewGuid().ToString('N'))"
$productionPublisher = "CN=Devolutions Inc, O=Devolutions Inc, L=Lavaltrie, S=Qu$([char]0xE9)bec, C=CA"
$password = ConvertTo-SecureString "ephemeral-fixture-password" -AsPlainText -Force
$wrongPublisherPackage = Join-Path $work "wrong-publisher.msix"

function Assert-ExpectedFailure {
    param(
        [scriptblock] $Action,
        [string] $MessagePattern,
        [string] $Name
    )

    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
        Write-Host "$Name passed."
        return
    }
    throw "$Name did not reject the invalid input."
}

function New-PackageFixture {
    param([string] $OutputDirectory)

    foreach ($architecture in @("x64", "arm64")) {
        $layout = Join-Path $OutputDirectory "layout\win-$architecture"
        $native = Join-Path $OutputDirectory "native-shell\$architecture"
        New-Item -ItemType Directory -Path $layout, $native -Force | Out-Null
        # Non-runnable PE headers are sufficient for architecture detection and packing.
        $image = [byte[]]::new(1024)
        $image[0] = 0x4D
        $image[1] = 0x5A
        $image[0x3C] = 0x80
        $image[0x80] = 0x50
        $image[0x81] = 0x45
        $image[0x86] = 1
        $image[0x94] = 0xF0
        $image[0x96] = 0x22
        $image[0x98] = 0x0B
        $image[0x99] = 0x02
        [BitConverter]::GetBytes([uint32]512).CopyTo($image, 0x9C)
        [BitConverter]::GetBytes([uint32]4096).CopyTo($image, 0xA8)
        [BitConverter]::GetBytes([uint32]4096).CopyTo($image, 0xAC)
        [BitConverter]::GetBytes([uint64]0x140000000).CopyTo($image, 0xB0)
        [BitConverter]::GetBytes([uint32]4096).CopyTo($image, 0xB8)
        [BitConverter]::GetBytes([uint32]512).CopyTo($image, 0xBC)
        $image[0xC0] = 6
        $image[0xC8] = 6
        [BitConverter]::GetBytes([uint32]8192).CopyTo($image, 0xD0)
        [BitConverter]::GetBytes([uint32]512).CopyTo($image, 0xD4)
        $image[0xDC] = 3
        [BitConverter]::GetBytes([uint64]1MB).CopyTo($image, 0xE0)
        [BitConverter]::GetBytes([uint64]4096).CopyTo($image, 0xE8)
        [BitConverter]::GetBytes([uint64]1MB).CopyTo($image, 0xF0)
        [BitConverter]::GetBytes([uint64]4096).CopyTo($image, 0xF8)
        $image[0x104] = 16
        [Text.Encoding]::ASCII.GetBytes(".text").CopyTo($image, 0x188)
        [BitConverter]::GetBytes([uint32]4).CopyTo($image, 0x190)
        [BitConverter]::GetBytes([uint32]4096).CopyTo($image, 0x194)
        [BitConverter]::GetBytes([uint32]512).CopyTo($image, 0x198)
        [BitConverter]::GetBytes([uint32]512).CopyTo($image, 0x19C)
        [BitConverter]::GetBytes([uint32]0x60000020).CopyTo($image, 0x1AC)
        $machine = if ($architecture -eq "arm64") { [uint16]0xAA64 } else { [uint16]0x8664 }
        [BitConverter]::GetBytes($machine).CopyTo($image, 0x84)
        foreach ($name in @("Devolutions.Terminal.exe", "dt.exe", "ghostty-vt.dll")) {
            [IO.File]::WriteAllBytes((Join-Path $layout $name), $image)
        }
        foreach ($name in @("Devolutions.Terminal.ShellExt.dll", "dt-shell-integration.exe")) {
            [IO.File]::WriteAllBytes((Join-Path $native $name), $image)
        }
        [IO.File]::WriteAllText((Join-Path $layout "THIRD-PARTY-NOTICES-GHOSTTY.txt"), "Fixture license")
    }
}

try {
    foreach ($case in @(
        @{ Name = "Development"; Publisher = "CN=Devolutions Inc."; Override = $false },
        @{ Name = "Production"; Publisher = $productionPublisher; Override = $true },
        @{ Name = "XmlEscaping"; Publisher = "CN=Fixture & Company"; Override = $true }
    )) {
        $output = Join-Path $work $case.Name
        New-PackageFixture -OutputDirectory $output
        $buildArguments = @{
            OutputDirectory = $output
            SkipPublish = $true
            SkipNativeBuild = $true
            Version = "2026.3.0.0"
        }
        if ($case.Override) {
            $buildArguments.Publisher = $case.Publisher
        }
        & (Join-Path $PSScriptRoot "Build-Packages.ps1") @buildArguments | Out-Host
        # First-run WinApp setup can write status messages to the success stream.
        $packages = @(Get-ChildItem -LiteralPath (Join-Path $output "packages") -File -Filter "*.msix")
        if ($packages.Count -ne 2) {
            throw "$($case.Name) build did not produce both architecture packages."
        }
        $generatedManifest = Join-Path $output "metadata\Package.appxmanifest"
        [xml] $manifest = Get-Content -LiteralPath $generatedManifest -Raw -Encoding utf8
        if ($manifest.Package.Identity.Publisher -cne $case.Publisher -or
            $manifest.Package.Identity.Version -cne "2026.3.0.0") {
            throw "$($case.Name) build did not write the exact publisher and version."
        }
        $testArguments = @{ PackagePath = @($packages.FullName) }
        if ($case.Override) {
            $testArguments.ExpectedPublisher = $case.Publisher
        }
        $results = @(& (Join-Path $PSScriptRoot "Test-Packages.ps1") @testArguments)
        if ($results.Count -ne 2 -or
            ($results.Architecture | Sort-Object) -join "|" -cne "arm64|x64" -or
            @($results | Where-Object { $_.Version -cne "2026.3.0.0" -or $_.Signed }).Count -ne 0) {
            throw "$($case.Name) packages did not pass exact unsigned architecture/version validation."
        }
        Write-Host "Build-Packages_$($case.Name)PublisherRoundTripsBothArchitectures passed."

        if ($case.Override) {
            Assert-ExpectedFailure -Name "Test-Packages_$($case.Name)RejectsDevelopmentPublisher" `
                -MessagePattern "Unexpected package publisher" -Action {
                    & (Join-Path $PSScriptRoot "Test-Packages.ps1") -PackagePath $packages[0].FullName
                }
        }
        if ($case.Name -eq "XmlEscaping") {
            continue
        }

        $certificateDirectory = Join-Path $output "certificates"
        $certificatePath = Join-Path $certificateDirectory "Devolutions.Terminal.pfx"
        if ($case.Name -eq "Development") {
            Copy-Item -LiteralPath $packages[0].FullName -Destination $wrongPublisherPackage
            & (Join-Path $PSScriptRoot "New-DevelopmentCertificate.ps1") `
                -OutputDirectory $certificateDirectory -Password $password | Out-Null
        }
        else {
            New-Item -ItemType Directory -Path $certificateDirectory | Out-Null
            winapp cert generate --manifest $generatedManifest --output $certificatePath `
                --password "ephemeral-fixture-password" --valid-days 1 --quiet
            if ($LASTEXITCODE -ne 0) {
                throw "Production-subject fixture certificate generation failed."
            }
            $mismatchOutput = & winapp sign $wrongPublisherPackage $certificatePath `
                --password "ephemeral-fixture-password" --quiet 2>&1
            if ($LASTEXITCODE -eq 0 -or ($mismatchOutput -join "`n") -notmatch '0x8007000B') {
                throw "Native signing did not reject the development publisher with the production-subject fixture certificate: $mismatchOutput"
            }
            Write-Host "Build-Packages_OldPublisherReproducesNativeSigningFailure passed."
        }
        $buildArguments.CertificatePath = $certificatePath
        $buildArguments.CertificatePassword = $password
        & (Join-Path $PSScriptRoot "Build-Packages.ps1") @buildArguments | Out-Host
        $signedPackages = @(Get-ChildItem -LiteralPath (Join-Path $output "packages") -File -Filter "*.msix")
        $testArguments.PackagePath = @($signedPackages.FullName)
        $testArguments.RequireSignature = $true
        $testArguments.AllowUntrustedRoot = $true
        $signedResults = @(& (Join-Path $PSScriptRoot "Test-Packages.ps1") @testArguments)
        if ($signedResults.Count -ne 2 -or @($signedResults | Where-Object { -not $_.Signed }).Count -ne 0) {
            throw "$($case.Name) packages did not pass local signature and publisher validation."
        }
        Write-Host "Build-Packages_$($case.Name)LocalCertificateMatchesPublisher passed."
    }

    foreach ($invalid in @("", " ", "CN=Fixture`nO=Company")) {
        Assert-ExpectedFailure -Name "Build-Packages_RejectsInvalidPublisher" -MessagePattern "Publisher" -Action {
            & (Join-Path $PSScriptRoot "Build-Packages.ps1") -Publisher $invalid `
                -OutputDirectory (Join-Path $work "invalid") -SkipPublish -SkipNativeBuild
        }
    }
    if ((Get-FileHash -LiteralPath $sourceManifest).Hash -cne $sourceHash) {
        throw "Package build tests changed the checked-in development manifest."
    }
    Write-Host "Build-Packages_SourceDevelopmentIdentityUnchanged passed."
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
