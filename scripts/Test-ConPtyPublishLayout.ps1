#!/usr/bin/env pwsh
#Requires -Version 7
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$targets = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'Directory.Build.targets'))
$work = Join-Path ([IO.Path]::GetTempPath()) "conpty-publish-test-$([guid]::NewGuid().ToString('N'))"

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $project = Join-Path $work 'PublishLayout.proj'
    @"
<Project>
  <Import Project="$targets" />
  <ItemGroup>
    <ResolvedFileToPublish Include="x86\OpenConsole.exe;x64\OpenConsole.exe;arm64\OpenConsole.exe;runtimes\win-x86\native\x86\OpenConsole.exe;runtimes\win-x86\native\x64\OpenConsole.exe;runtimes\win-x86\native\arm64\OpenConsole.exe;runtimes\win-x64\native\x64\OpenConsole.exe;runtimes\win-x64\native\arm64\OpenConsole.exe;runtimes\win-arm64\native\arm64\OpenConsole.exe;THIRD-PARTY-NOTICES-CONPTY.txt;dt;libghostty-vt.dylib" />
  </ItemGroup>
  <Target Name="ComputeFilesToPublish" />
</Project>
"@ | Set-Content -LiteralPath $project -Encoding utf8

    foreach ($rid in @('', 'win-x86', 'win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')) {
        $output = & dotnet msbuild $project -t:ComputeFilesToPublish "-p:RuntimeIdentifier=$rid" `
            -getItem:ResolvedFileToPublish -verbosity:quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Publish layout evaluation failed for '$rid': $output"
        }
        $items = ($output -join "`n" | ConvertFrom-Json).Items.ResolvedFileToPublish
        $hosts = @($items | Where-Object { "$($_.Filename)$($_.Extension)" -eq 'OpenConsole.exe' })
        $expectedHosts = if ($rid -match '^(osx|linux)-') { 0 } else { 9 }
        if ($hosts.Count -ne $expectedHosts -or $items.Count -ne $expectedHosts + 3) {
            throw "Unexpected publish layout for '$rid': expected $expectedHosts Windows hosts and 3 retained files, got $($hosts.Count) hosts and $($items.Count) files."
        }
        foreach ($retained in @('THIRD-PARTY-NOTICES-CONPTY.txt', 'dt', 'libghostty-vt.dylib')) {
            if (@($items | Where-Object { "$($_.Filename)$($_.Extension)" -ceq $retained }).Count -ne 1) {
                throw "Publish layout for '$rid' lost $retained."
            }
        }
        Write-Host "ConPtyPublishLayout ($rid): $expectedHosts Windows hosts; legal notice and native payload preserved."
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
