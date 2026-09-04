[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDir,

    [Parameter(Mandatory = $true)]
    [string] $OutputFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$publishRoot = [IO.Path]::GetFullPath($PublishDir)
if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "Publish directory '$publishRoot' does not exist."
}

$directoryIds = @{}
$files = Get-ChildItem -LiteralPath $publishRoot -File -Recurse |
    Where-Object { $_.Extension -ne ".pdb" } |
    Sort-Object FullName

function Get-ParentPath {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -eq '.') {
        return ''
    }

    $normalized = $Path.Replace('\\', '/')
    $index = $normalized.LastIndexOf('/')
    if ($index -lt 0) {
        return ''
    }

    return $normalized.Substring(0, $index)
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $BasePath,

        [Parameter(Mandatory = $true)]
        [string] $TargetPath
    )

    $baseFull = [IO.Path]::GetFullPath($BasePath).TrimEnd('\')
    $targetFull = [IO.Path]::GetFullPath($TargetPath)

    if ($targetFull.StartsWith($baseFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $targetFull.Substring($baseFull.Length).TrimStart('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative)) {
            return '.'
        }

        return $relative.Replace('\\', '/')
    }

    $baseUri = [Uri]::new(($baseFull + [IO.Path]::DirectorySeparatorChar))
    $targetUri = [Uri]::new($targetFull)
    $relative = [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString())
    return $relative.Replace('\\', '/')
}

foreach ($file in $files) {
    $relativeDirectory = Get-RelativePath -BasePath $publishRoot -TargetPath ([IO.Path]::GetDirectoryName($file.FullName))
    if ([string]::IsNullOrWhiteSpace($relativeDirectory) -or $relativeDirectory -eq '.' -or $relativeDirectory -eq './') {
        continue
    }

    $path = $relativeDirectory
    while (-not [string]::IsNullOrWhiteSpace($path)) {
        if (-not $directoryIds.ContainsKey($path)) {
            $directoryIds[$path] = 'DIR_' + (($path -replace '[^A-Za-z0-9_]', '_').Trim('_'))
        }

        $parent = Get-ParentPath $path
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $path) {
            break
        }

        $path = $parent
    }
}

function EmitDirectories {
    param(
        [string] $ParentPath,
        [System.Text.StringBuilder] $Builder
    )

    $children = @($directoryIds.Keys | Where-Object {
        $parent = Get-ParentPath $_
        $parent -eq $ParentPath
    } | Sort-Object)

    foreach ($child in $children) {
        $directoryId = $directoryIds[$child]
        $directoryName = [IO.Path]::GetFileName($child)
        [void]$Builder.AppendLine("      <Directory Id='$directoryId' Name='$directoryName'>")
        EmitDirectories -ParentPath $child -Builder $Builder
        [void]$Builder.AppendLine("      </Directory>")
    }
}

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$builder.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$builder.AppendLine('  <Fragment>')
[void]$builder.AppendLine('    <DirectoryRef Id="INSTALLLOCATION">')
EmitDirectories -ParentPath '' -Builder $builder
[void]$builder.AppendLine('    </DirectoryRef>')
[void]$builder.AppendLine('    <ComponentGroup Id="ProductComponents">')

foreach ($file in $files) {
    $relativePath = Get-RelativePath -BasePath $publishRoot -TargetPath $file.FullName
    $relativeDirectory = [IO.Path]::GetDirectoryName($relativePath)
    if ([string]::IsNullOrWhiteSpace($relativeDirectory) -or $relativeDirectory -eq '.' -or $relativeDirectory -eq './') {
        $directoryId = 'INSTALLLOCATION'
    }
    else {
        $directoryId = $directoryIds[$relativeDirectory.Replace('\\', '/')]
    }

    $componentId = 'cmp_' + (($relativePath -replace '[^A-Za-z0-9_]', '_').Trim('_'))
    if ([string]::IsNullOrWhiteSpace($componentId)) {
        $componentId = 'cmp_' + [guid]::NewGuid().ToString('N')
    }

    [void]$builder.AppendLine("      <Component Directory='$directoryId'>")
    [void]$builder.AppendLine("        <File Id='$componentId' Source='$([System.Security.SecurityElement]::Escape($file.FullName))' KeyPath='yes' />")
    [void]$builder.AppendLine('      </Component>')
}

[void]$builder.AppendLine('    </ComponentGroup>')
[void]$builder.AppendLine('  </Fragment>')
[void]$builder.AppendLine('</Wix>')

$directory = Split-Path -Parent $OutputFile
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

[IO.File]::WriteAllText($OutputFile, $builder.ToString(), [Text.UTF8Encoding]::new($false))
