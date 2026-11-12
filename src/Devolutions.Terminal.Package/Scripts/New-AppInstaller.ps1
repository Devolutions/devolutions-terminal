#Requires -Version 7
<#
.SYNOPSIS
Generates a Windows App Installer feed that keeps an MSIX installation updated from GitHub Releases.

.DESCRIPTION
The feed URI uses GitHub's stable `releases/latest/download/<asset>` redirect, so every
release publishes a feed at the same URL. Each feed points at that release's immutable
MSIX download, and Windows App Installer checks the stable URL on launch and in the
background. The same file is embedded in the package as `Update.appinstaller` and
referenced through `uap13:AutoUpdate`, so packages installed directly from a downloaded
`.msix` also enroll in automatic updates on Windows versions that support it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,

    [Parameter(Mandatory)]
    [ValidateSet("x64", "arm64")]
    [string] $Architecture,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_./-]+$')]
    [string] $ReleaseTag,

    [Parameter(Mandatory)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

[xml] $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8
$identity = $manifest.Package.Identity
if ([string]::IsNullOrWhiteSpace($identity.Name) -or [string]::IsNullOrWhiteSpace($identity.Publisher)) {
    throw "The package manifest must contain an identity name and publisher."
}
if ($identity.Version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
    @(([version] $identity.Version).ToString().Split('.') | Where-Object { [int] $_ -gt 65535 }).Count -ne 0) {
    throw "The package manifest version '$($identity.Version)' is not a valid four-part MSIX version."
}

$releases = "https://github.com/$Repository/releases"
$feedName = "$($identity.Name)_$Architecture.appinstaller"
$packageName = "$($identity.Name)_$($identity.Version)_$Architecture.msix"
$escapedTag = [uri]::EscapeDataString($ReleaseTag)
$namespace = "http://schemas.microsoft.com/appx/appinstaller/2021"

$document = [xml]::new()
[void] $document.AppendChild($document.CreateXmlDeclaration("1.0", "utf-8", $null))
$installer = $document.CreateElement("AppInstaller", $namespace)
$installer.SetAttribute("Version", $identity.Version)
$installer.SetAttribute("Uri", "$releases/latest/download/$feedName")
[void] $document.AppendChild($installer)

$package = $document.CreateElement("MainPackage", $namespace)
$package.SetAttribute("Name", $identity.Name)
$package.SetAttribute("Publisher", $identity.Publisher)
$package.SetAttribute("Version", $identity.Version)
$package.SetAttribute("ProcessorArchitecture", $Architecture)
$package.SetAttribute("Uri", "$releases/download/$escapedTag/$packageName")
[void] $installer.AppendChild($package)

$settings = $document.CreateElement("UpdateSettings", $namespace)
$onLaunch = $document.CreateElement("OnLaunch", $namespace)
$onLaunch.SetAttribute("HoursBetweenUpdateChecks", "0")
$onLaunch.SetAttribute("ShowPrompt", "false")
$onLaunch.SetAttribute("UpdateBlocksActivation", "false")
[void] $settings.AppendChild($onLaunch)
[void] $settings.AppendChild($document.CreateElement("AutomaticBackgroundTask", $namespace))
[void] $installer.AppendChild($settings)

$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$writerSettings = [Xml.XmlWriterSettings]::new()
$writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
$writerSettings.Indent = $true
$writer = [Xml.XmlWriter]::Create($output, $writerSettings)
try {
    $document.Save($writer)
}
finally {
    $writer.Dispose()
}

Get-Item -LiteralPath $output
