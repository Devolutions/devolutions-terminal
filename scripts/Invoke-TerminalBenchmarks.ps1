[CmdletBinding()]
param(
    # vt: builtin ingest vs an in-run clock control; render: frame prep + CPU raster; control: PTY->UI coalescing.
    [string[]] $Suite = @("vt", "render"),

    # Smaller corpora and fewer runs for a fast local signal (not for published numbers).
    [switch] $Quick,

    [string] $OutputDirectory,

    # Comma-separated vt workloads; "all" runs every workload.
    [string] $Workloads = "all",

    # Read a frame (snapshot / render-state update) every N KiB fed; 0 measures pure ingest.
    [ValidateRange(0, 1048576)]
    [int] $FrameKb = 0,

    # A previous run's vt.json; medians that drop by more than -RegressionPercent fail the run.
    [string] $Baseline,

    [ValidateRange(1, 100)]
    [int] $RegressionPercent = 15,

    # Minimum length of each vt sample in milliseconds (the bench's default is 500).
    # Laptop CPUs drop out of turbo a few seconds into sustained load; samples of
    # ~3000 ms all run at the sustained clock, so medians and spreads stay stable.
    [ValidateRange(0, 600000)]
    [int] $MinMs = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# `pwsh -File` passes "vt,render" as one string; accept both forms.
$Suite = @($Suite | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($name in $Suite) {
    if ($name -notin @("vt", "render", "control")) {
        throw "Unknown suite '$name' (expected vt, render or control)."
    }
}

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "tools\Devolutions.Terminal.Bench"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo ("artifacts\bench\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path

$megabytes = if ($Quick) { 2 } else { 8 }
$runs = if ($Quick) { 3 } else { 7 }

Write-Host "Building $project (Release)..."
dotnet build $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    throw "Benchmark build failed."
}

$bench = Get-ChildItem -Path (Join-Path $project "bin\Release") -Recurse -File |
    Where-Object { $_.BaseName -eq "Devolutions.Terminal.Bench" -and $_.Extension -in @(".exe", "") } |
    Select-Object -First 1
if (-not $bench) {
    $bench = Get-ChildItem -Path (Join-Path $project "bin\Release") -Recurse -Filter "Devolutions.Terminal.Bench.dll" |
        Select-Object -First 1
}

function Invoke-Bench {
    param([string] $Name, [string[]] $Arguments)

    $log = Join-Path $OutputDirectory "$Name.txt"
    Write-Host ""
    Write-Host "== $Name =="
    # Out-Host keeps the transcript out of the function's return value.
    if ($bench.Extension -eq ".dll") {
        & dotnet $bench.FullName @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
    }
    else {
        & $bench.FullName @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
    }

    return $LASTEXITCODE
}

$failures = [System.Collections.Generic.List[string]]::new()

if ($Suite -contains "vt") {
    $vtJson = Join-Path $OutputDirectory "vt.json"
    $names = if ($Workloads -eq "all") {
        if ($bench.Extension -eq ".dll") { & dotnet $bench.FullName vt --list } else { & $bench.FullName vt --list }
    }
    else {
        $Workloads -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    }

    # One process per workload: GC heap and JIT state left behind by an earlier
    # workload shifted later ratios by up to ~50% in a shared process, which made
    # runs with different -Workloads subsets incomparable.
    $merged = $null
    foreach ($name in $names) {
        $partial = Join-Path $OutputDirectory "vt-$name.json"
        $vtArgs = @("vt", "--mb", $megabytes, "--runs", $runs, "--frame-kb", $FrameKb, "--workloads", $name, "--json", $partial)
        if ($MinMs -gt 0) {
            $vtArgs += @("--min-ms", $MinMs)
        }

        if ((Invoke-Bench -Name "vt-$name" -Arguments $vtArgs) -ne 0) {
            $failures.Add("vt benchmark failed on workload '$name'")
        }

        $report = Get-Content -LiteralPath $partial -Raw | ConvertFrom-Json
        if ($merged) {
            $merged.workloads = @($merged.workloads) + @($report.workloads)
        }
        else {
            $merged = $report
        }
    }

    $merged | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $vtJson


    if ($Baseline) {
        $previous = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
        $current = Get-Content -LiteralPath $vtJson -Raw | ConvertFrom-Json
        Write-Host ""
        Write-Host "== regression check vs $Baseline (threshold $RegressionPercent%) =="
        foreach ($workload in $current.workloads) {
            $before = $previous.workloads | Where-Object { $_.name -eq $workload.name } | Select-Object -First 1
            if (-not $before) {
                continue
            }

            if ($before.sha256 -ne $workload.sha256) {
                Write-Host ("{0,-14} skipped: input differs from the baseline (use the same -Quick/-Workloads settings)" -f $workload.name)
                continue
            }

            foreach ($target in $workload.targets) {
                $old = $before.targets | Where-Object { $_.target -eq $target.target } | Select-Object -First 1
                if ($old) {
                    $change = ($target.medianMBps - $old.medianMBps) / $old.medianMBps * 100
                    Write-Host ("{0,-14} {1,-10} {2,9:F1} -> {3,9:F1} MB/s ({4:+0.0;-0.0}%)" -f $workload.name, $target.target, $old.medianMBps, $target.medianMBps, $change)
                }
            }

            # Absolute MB/s drifts with clocks and background load between sessions, so
            # gate on builtin relative to the clock control measured in the same run.
            $oldRatio = $before.PSObject.Properties["builtinVsControl"]
            $newRatio = $workload.PSObject.Properties["builtinVsControl"]
            if ($oldRatio -and $newRatio -and $oldRatio.Value -gt 0) {
                $change = ($newRatio.Value - $oldRatio.Value) / $oldRatio.Value * 100
                $line = "{0,-14} builtin/control {1:F4} -> {2:F4} ({3:+0.0;-0.0}%)" -f $workload.name, $oldRatio.Value, $newRatio.Value, $change
                if ($change -lt -$RegressionPercent) {
                    $failures.Add("regression: $line")
                    Write-Host "$line  REGRESSION"
                }
                else {
                    Write-Host $line
                }
            }
        }
    }
}

if ($Suite -contains "render") {
    $renderArgs = if ($Quick) {
        @("render", "--mb", $megabytes, "--runs", $runs, "--samples", "60", "--warmup", "2", "--frames", "64")
    }
    else {
        @("render", "--mb", $megabytes, "--runs", "5", "--samples", "128", "--warmup", "2", "--frames", "256")
    }

    if ((Invoke-Bench -Name "render" -Arguments $renderArgs) -ne 0) {
        $failures.Add("render benchmark failed")
    }
}

if ($Suite -contains "control") {
    if ((Invoke-Bench -Name "control" -Arguments @("control", "--mb", $megabytes, "--runs", $runs)) -ne 0) {
        $failures.Add("control benchmark failed")
    }
}

Write-Host ""
Write-Host "Results: $OutputDirectory"
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Warning $_ }
    exit 1
}
