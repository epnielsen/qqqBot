<#
.SYNOPSIS
    Parallel parameter sweep using --mode=parallel-replay.
    
.DESCRIPTION
    Generates config variants for a parameter sweep and runs each variant across
    multiple dates in parallel using the in-process parallel replay engine.
    
    This is a drop-in replacement for sequential sweep scripts — instead of running
    N dates × M parameter values as N×M sequential `dotnet run` invocations, it runs
    M invocations of parallel-replay (each processing N dates concurrently).

.EXAMPLE
    # Sweep TrailingStopPercent across 5 values, 3 dates, 8-way parallel
    .\parallel-sweep.ps1 -Param "TradingBot:TrailingStopPercent" `
        -Values @(0.001, 0.002, 0.003, 0.004, 0.005) `
        -Dates "20260210,20260211,20260212" `
        -Parallelism 8

.EXAMPLE
    # Monte Carlo: 50 seeds across 5 dates
    .\parallel-sweep.ps1 -MonteCarloSeeds "1-50" `
        -Dates "20260210-20260214" `
        -Parallelism 8

.EXAMPLE
    # Sweep with a custom base config
    .\parallel-sweep.ps1 -Param "TradingBot:MinVelocityThreshold" `
        -Values @(0.000001, 0.000005, 0.00001) `
        -Dates "20260210,20260211" `
        -BaseConfig "sweep_configs/my_base.json"
#>
param(
    [string]$Param = "",                    # Config path to sweep (e.g., "TradingBot:TrailingStopPercent")
    [array]$Values = @(),                   # Values to sweep
    [string]$Dates = "",                    # Dates: comma-separated or range (20260210-20260214)
    [int]$Parallelism = 8,                  # Max concurrent replays per variant
    [string]$BaseConfig = "appsettings.json",  # Base config file
    [string]$OutputDir = "sweep_results",   # Output directory for results
    [string]$MonteCarloSeeds = "",          # Seed range for Monte Carlo (e.g., "1-50")
    [int]$BaseSeed = 0,                     # Base seed for Monte Carlo (0 = date-based default)
    [double]$Speed = 0                      # Replay speed (0 = max)
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── Helper: Set a nested property on a JSON object ──
function Set-NestedProperty {
    param($obj, [string]$path, $value)
    $parts = $path -split ":"
    $current = $obj
    for ($i = 0; $i -lt $parts.Length - 1; $i++) {
        $current = $current.($parts[$i])
    }
    $leaf = $parts[-1]
    if ($current.PSObject.Properties[$leaf]) {
        $current.$leaf = $value
    } else {
        $current | Add-Member -MemberType NoteProperty -Name $leaf -Value $value -Force
    }
}

# ── Ensure output directory exists ──
$sweepDir = Join-Path $projectDir $OutputDir
if (-not (Test-Path $sweepDir)) { New-Item -ItemType Directory -Path $sweepDir -Force | Out-Null }
$configDir = Join-Path $sweepDir "configs"
if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }

# ── Load base config ──
$baseConfigPath = Join-Path $projectDir $BaseConfig
$baseJson = Get-Content $baseConfigPath -Raw | ConvertFrom-Json

# ── Build sweep variants ──
if ($MonteCarloSeeds -ne "") {
    # Monte Carlo mode: single config, multiple seeds
    Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║  MONTE CARLO SIMULATION                             ║" -ForegroundColor Cyan
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host "  Seeds: $MonteCarloSeeds"
    Write-Host "  Dates: $Dates"
    Write-Host "  Parallelism: $Parallelism"
    Write-Host ""

    $seedArg = if ($BaseSeed -gt 0) { "--seed=$BaseSeed" } else { "" }
    $runOutputDir = Join-Path $sweepDir "montecarlo"
    $csvPath = Join-Path $runOutputDir "results.csv"

    $cmd = "dotnet run --project `"$projectDir`" -- --mode=parallel-replay --dates=$Dates --seeds=$MonteCarloSeeds --parallelism=$Parallelism --speed=$Speed --output=`"$csvPath`" --output-dir=`"$runOutputDir`" -config=`"$BaseConfig`" $seedArg"
    Write-Host "Running: $cmd" -ForegroundColor DarkGray
    Invoke-Expression $cmd

    Write-Host ""
    Write-Host "Results: $csvPath" -ForegroundColor Green
}
elseif ($Param -ne "" -and $Values.Length -gt 0) {
    # Parameter sweep mode
    Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "║  PARALLEL PARAMETER SWEEP                           ║" -ForegroundColor Cyan
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    Write-Host "  Parameter: $Param"
    Write-Host "  Values: $($Values -join ', ')"
    Write-Host "  Dates: $Dates"
    Write-Host "  Parallelism: $Parallelism"
    Write-Host ""

    $allResults = @()

    foreach ($value in $Values) {
        Write-Host "────────────────────────────────────────────────" -ForegroundColor DarkGray
        Write-Host "  $Param = $value" -ForegroundColor Yellow

        # Deep-clone and modify config
        $cfg = $baseJson | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        Set-NestedProperty -obj $cfg -path $Param -value $value

        # Write variant config
        $safeValue = "$value" -replace "[^a-zA-Z0-9._-]", ""
        $variantName = "$($Param -replace ':', '_')_$safeValue"
        $cfgPath = Join-Path $configDir "$variantName.json"
        $cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding UTF8

        # Run parallel replay
        $runOutputDir = Join-Path $sweepDir $variantName
        $csvPath = Join-Path $runOutputDir "results.csv"

        $cmd = "dotnet run --project `"$projectDir`" -- --mode=parallel-replay --dates=$Dates --parallelism=$Parallelism --speed=$Speed --output=`"$csvPath`" --output-dir=`"$runOutputDir`" -config=`"$cfgPath`""
        Write-Host "  Running: $cmd" -ForegroundColor DarkGray
        Invoke-Expression $cmd

        # Parse CSV results
        if (Test-Path $csvPath) {
            $csv = Import-Csv $csvPath
            $meanPnL = ($csv | Measure-Object -Property RealizedPnL -Average).Average
            $winRate = ($csv | Where-Object { [decimal]$_.RealizedPnL -gt 0 }).Count / $csv.Count
            $allResults += [PSCustomObject]@{
                Parameter = $Param
                Value = $value
                MeanPnL = [math]::Round($meanPnL, 2)
                WinRate = [math]::Round($winRate * 100, 1)
                Runs = $csv.Count
            }
        }
    }

    # Summary table
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║  SWEEP SUMMARY                                      ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Green
    $allResults | Format-Table -AutoSize
    
    # Save summary
    $summaryPath = Join-Path $sweepDir "sweep_summary.csv"
    $allResults | Export-Csv $summaryPath -NoTypeInformation
    Write-Host "Summary saved to: $summaryPath" -ForegroundColor Green
}
else {
    Write-Host "Error: specify -Param/-Values for parameter sweep, or -MonteCarloSeeds for Monte Carlo." -ForegroundColor Red
}
