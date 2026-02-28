param(
    [int]$Round = 0
)

$ErrorActionPreference = "Stop"
$baseCfgPath = "$PSScriptRoot\sweep_configs\baseline_30k.json"
$dates = "20260209-20260213,20260217-20260220,20260223-20260227"

function Run-Sweep {
    param([string]$Name, [hashtable]$Settings, [string]$OutName)
    
    $cfg = Get-Content $baseCfgPath -Raw | ConvertFrom-Json
    foreach ($key in $Settings.Keys) {
        $cfg.TradingBot.$key = $Settings[$key]
    }
    $cfgPath = "$PSScriptRoot\sweep_configs\$OutName.json"
    $cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding UTF8
    
    $outDir = "$PSScriptRoot\sweep_results\$OutName"
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    
    Write-Host "`n=== $Name ===" -ForegroundColor Yellow
    dotnet run --project $PSScriptRoot -- --mode=parallel-replay --dates=$dates --parallelism=8 --speed=0 "-config=$cfgPath" "--output=$outDir\results.csv" "--output-dir=$outDir" 2>&1 | ForEach-Object {
        $line = $_.ToString()
        if ($line -match "Mean P/L|Per-Date|mean=|Win Rate|Median|Std Dev|SUMMARY") {
            Write-Host $line
        }
    }
    
    $csvPath = "$outDir\results.csv"
    if (Test-Path $csvPath) {
        $csv = Import-Csv $csvPath
        $meanPnL = [math]::Round(($csv | Measure-Object -Property RealizedPnL -Average).Average, 2)
        $wins = ($csv | Where-Object { [decimal]$_.RealizedPnL -gt 0 }).Count
        $wr = [math]::Round($wins / $csv.Count * 100, 1)
        Write-Host "  RESULT: Mean P/L = $meanPnL, Win Rate = $wr% ($($csv.Count) runs)" -ForegroundColor Cyan
        return [PSCustomObject]@{ Name = $Name; MeanPnL = $meanPnL; WinRate = $wr; Runs = $csv.Count }
    }
}

# ── Round 1: Daily Loss Limit ──
if ($Round -eq 0 -or $Round -eq 1) {
    Write-Host "`n########################################" -ForegroundColor Green
    Write-Host "# ROUND 1: DAILY LOSS LIMIT SWEEP" -ForegroundColor Green
    Write-Host "########################################" -ForegroundColor Green
    $r1 = @()
    foreach ($val in @(0.75, 1.0, 1.25, 1.5, 2.0)) {
        $r = Run-Sweep -Name "DLL=$val% (`$$([math]::Round($val * 300)))" -Settings @{ DailyLossLimitPercent = $val } -OutName "r1_dll_$($val -replace '\.','p')"
        if ($r) { $r1 += $r }
    }
    Write-Host "`n=== ROUND 1 SUMMARY ===" -ForegroundColor Green
    $r1 | Format-Table -AutoSize
}

# ── Round 2: Daily Profit Target % ──
if ($Round -eq 0 -or $Round -eq 2) {
    Write-Host "`n########################################" -ForegroundColor Green
    Write-Host "# ROUND 2: DAILY PROFIT TARGET % SWEEP" -ForegroundColor Green
    Write-Host "########################################" -ForegroundColor Green
    $r2 = @()
    foreach ($val in @(0.75, 1.0, 1.25, 1.5, 1.75, 2.0)) {
        $r = Run-Sweep -Name "DPT=$val% (`$$([math]::Round($val * 300)))" -Settings @{ DailyProfitTargetPercent = $val } -OutName "r2_dpt_$($val -replace '\.','p')"
        if ($r) { $r2 += $r }
    }
    Write-Host "`n=== ROUND 2 SUMMARY ===" -ForegroundColor Green
    $r2 | Format-Table -AutoSize
}

# ── Round 3: OV Trailing Stop ──
if ($Round -eq 0 -or $Round -eq 3) {
    Write-Host "`n########################################" -ForegroundColor Green
    Write-Host "# ROUND 3: OV TRAILING STOP SWEEP" -ForegroundColor Green
    Write-Host "########################################" -ForegroundColor Green
    $r3 = @()
    foreach ($val in @(0.003, 0.004, 0.005, 0.006, 0.008)) {
        $cfg = Get-Content $baseCfgPath -Raw | ConvertFrom-Json
        $cfg.TradingBot.TimeRules[0].Overrides.TrailingStopPercent = $val
        $outName = "r3_ovts_$($val -replace '\.','p')"
        $cfgPath = "$PSScriptRoot\sweep_configs\$outName.json"
        $cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding UTF8
        
        $outDir = "$PSScriptRoot\sweep_results\$outName"
        if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
        
        Write-Host "`n=== OV TrailingStop=$val ===" -ForegroundColor Yellow
        dotnet run --project $PSScriptRoot -- --mode=parallel-replay --dates=$dates --parallelism=8 --speed=0 "-config=$cfgPath" "--output=$outDir\results.csv" "--output-dir=$outDir" 2>&1 | ForEach-Object {
            $line = $_.ToString()
            if ($line -match "Mean P/L|mean=|Win Rate") { Write-Host $line }
        }
        
        $csvPath = "$outDir\results.csv"
        if (Test-Path $csvPath) {
            $csv = Import-Csv $csvPath
            $meanPnL = [math]::Round(($csv | Measure-Object -Property RealizedPnL -Average).Average, 2)
            $wins = ($csv | Where-Object { [decimal]$_.RealizedPnL -gt 0 }).Count
            $wr = [math]::Round($wins / $csv.Count * 100, 1)
            Write-Host "  RESULT: Mean P/L = $meanPnL, Win Rate = $wr%" -ForegroundColor Cyan
            $r3 += [PSCustomObject]@{ Name = "OV-TS=$val"; MeanPnL = $meanPnL; WinRate = $wr; Runs = $csv.Count }
        }
    }
    Write-Host "`n=== ROUND 3 SUMMARY ===" -ForegroundColor Green
    $r3 | Format-Table -AutoSize
}

Write-Host "`nAll sweeps complete." -ForegroundColor Green
