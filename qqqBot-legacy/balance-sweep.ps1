# Balance Sweep: Run compounding replay at various starting balances
# Compares performance scaling from $10K to $100K

param(
    [int[]]$Balances = @(10000, 20000, 30000, 40000, 50000, 60000, 70000, 80000, 90000, 100000),
    [string]$BaseConfig = "appsettings.json"
)

$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot

# Ordered trading dates (Feb 9 - Feb 27, excluding weekends)
$dates = @(
    "20260209", "20260210", "20260211", "20260212", "20260213",
    "20260217", "20260218", "20260219", "20260220",
    "20260223", "20260224", "20260225", "20260226", "20260227"
)

# Load base config
$baseConfigPath = Join-Path $PSScriptRoot $BaseConfig
if (-not (Test-Path $baseConfigPath)) {
    Write-Error "Base config not found: $baseConfigPath"
    exit 1
}
$baseJson = Get-Content $baseConfigPath -Raw | ConvertFrom-Json

# Create temp config directory
$tempDir = Join-Path $PSScriptRoot "sweep_configs"
if (-not (Test-Path $tempDir)) { New-Item -ItemType Directory -Path $tempDir | Out-Null }
$tempConfigPath = Join-Path $tempDir "balance_sweep_run.json"

# Master results collection
$masterResults = @()

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "   BALANCE SWEEP: COMPOUNDING REPLAY AT VARIOUS SIZES" -ForegroundColor Cyan
Write-Host "   Balances: $($Balances[0].ToString('C0')) - $($Balances[-1].ToString('C0'))" -ForegroundColor Cyan
Write-Host "   Dates: $($dates[0]) - $($dates[-1]) ($($dates.Count) days)" -ForegroundColor Cyan
Write-Host "   Config: $BaseConfig" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

foreach ($initialBalance in $Balances) {
    $balance = [decimal]$initialBalance
    $dailyResults = @()
    
    Write-Host "--- Starting sweep at $($balance.ToString('C0')) ---" -ForegroundColor Yellow
    
    foreach ($date in $dates) {
        # Deep clone config and set StartingAmount
        $cfg = $baseJson | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $cfg.TradingBot.StartingAmount = [double]$balance
        $cfg | ConvertTo-Json -Depth 10 | Set-Content $tempConfigPath -Encoding UTF8

        Write-Host "  [$date] $($balance.ToString('C2')) ..." -NoNewline

        # Run replay
        $output = dotnet run -- --mode=replay --date=$date --speed=0 "-config=$tempConfigPath" 2>&1 | Out-String

        # Parse ending equity
        $endingEquityMatch = [regex]::Match($output, '\[SIM-BROKER\]\s+Ending Equity:\s+\$([\d,]+\.?\d*)')
        $tradeCountMatch = [regex]::Match($output, 'Total trades:\s*(\d+)')

        if ($endingEquityMatch.Success) {
            $newBalance = [decimal]($endingEquityMatch.Groups[1].Value -replace ',', '')
            $dailyPnl = $newBalance - $balance
            $dailyPct = if ($balance -ne 0) { ($dailyPnl / $balance) * 100 } else { 0 }
            $trades = if ($tradeCountMatch.Success) { [int]$tradeCountMatch.Groups[1].Value } else { 0 }

            $color = if ($dailyPnl -ge 0) { "Green" } else { "Red" }
            Write-Host (" P/L: {0} ({1:F2}%)" -f $dailyPnl.ToString('C2'), $dailyPct) -ForegroundColor $color

            $dailyResults += [PSCustomObject]@{
                Date     = $date
                StartBal = $balance
                EndBal   = $newBalance
                DailyPnL = $dailyPnl
                DailyPct = [math]::Round($dailyPct, 4)
                Trades   = $trades
            }
            $balance = $newBalance
        }
        else {
            Write-Host " PARSE FAILED" -ForegroundColor Red
            $dailyResults += [PSCustomObject]@{
                Date     = $date
                StartBal = $balance
                EndBal   = $balance
                DailyPnL = 0
                DailyPct = 0
                Trades   = 0
            }
        }
    }

    # Compute summary for this balance level
    $totalPnL = $balance - [decimal]$initialBalance
    $totalPct = ($totalPnL / [decimal]$initialBalance) * 100
    $winDays = ($dailyResults | Where-Object { $_.DailyPnL -gt 0 }).Count
    $lossDays = ($dailyResults | Where-Object { $_.DailyPnL -lt 0 }).Count
    $avgDailyPnl = ($dailyResults | Measure-Object -Property DailyPnL -Average).Average
    $avgDailyPct = ($dailyResults | Measure-Object -Property DailyPct -Average).Average
    $maxWin = ($dailyResults | Measure-Object -Property DailyPnL -Maximum).Maximum
    $maxLoss = ($dailyResults | Measure-Object -Property DailyPnL -Minimum).Minimum
    $maxWinPct = ($dailyResults | Measure-Object -Property DailyPct -Maximum).Maximum
    $maxLossPct = ($dailyResults | Measure-Object -Property DailyPct -Minimum).Minimum
    $totalTrades = ($dailyResults | Measure-Object -Property Trades -Sum).Sum

    $color = if ($totalPnL -ge 0) { "Green" } else { "Red" }
    Write-Host ("  >> {0}: {1} -> {2} | P/L: {3} ({4:F2}%) | W/L: {5}/{6}" -f $initialBalance.ToString('C0'), $initialBalance.ToString('C0'), $balance.ToString('C2'), $totalPnL.ToString('C2'), $totalPct, $winDays, $lossDays) -ForegroundColor $color
    Write-Host ""

    $masterResults += [PSCustomObject]@{
        InitialBalance = $initialBalance
        FinalBalance   = $balance
        TotalPnL       = $totalPnL
        TotalPct       = [math]::Round($totalPct, 2)
        WinDays        = $winDays
        LossDays       = $lossDays
        WinRate        = [math]::Round(($winDays / $dates.Count) * 100, 1)
        AvgDailyPnL    = [math]::Round($avgDailyPnl, 2)
        AvgDailyPct    = [math]::Round($avgDailyPct, 2)
        MaxWinPnL      = [math]::Round($maxWin, 2)
        MaxLossPnL     = [math]::Round($maxLoss, 2)
        MaxWinPct      = [math]::Round($maxWinPct, 2)
        MaxLossPct     = [math]::Round($maxLossPct, 2)
        TotalTrades    = $totalTrades
    }

    # Save per-balance daily CSV
    $perBalDir = Join-Path $PSScriptRoot "balance_sweep_results"
    if (-not (Test-Path $perBalDir)) { New-Item -ItemType Directory -Path $perBalDir | Out-Null }
    $dailyResults | Export-Csv -Path (Join-Path $perBalDir "compound_${initialBalance}.csv") -NoTypeInformation
}

# Final comparison table
Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "            BALANCE SWEEP COMPARISON" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$header = "{0,12} {1,14} {2,12} {3,8} {4,6} {5,8} {6,12} {7,10} {8,10} {9,10}" -f "Initial", "Final", "Total P/L", "Ret %", "W/L", "Win%", "Avg Daily", "Avg D%", "Best D%", "Worst D%"
Write-Host $header -ForegroundColor White
Write-Host ("{0}" -f ("-" * 110)) -ForegroundColor DarkGray

foreach ($r in $masterResults) {
    $color = if ($r.TotalPnL -ge 0) { "Green" } else { "Red" }
    $line = "{0,12} {1,14} {2,12} {3,7:F2}% {4,2}/{5,-2} {6,7:F1}% {7,12} {8,9:F2}% {9,9:F2}%" -f `
        $r.InitialBalance.ToString('C0'), `
        $r.FinalBalance.ToString('C2'), `
        $r.TotalPnL.ToString('C2'), `
        $r.TotalPct, `
        $r.WinDays, $r.LossDays, `
        $r.WinRate, `
        ([decimal]$r.AvgDailyPnL).ToString('C2'), `
        $r.AvgDailyPct, `
        $r.MaxLossPct
    Write-Host $line -ForegroundColor $color
}

# Scaling analysis
Write-Host ""
Write-Host "--- Scaling Analysis ---" -ForegroundColor Cyan
$baseResult = $masterResults | Where-Object { $_.InitialBalance -eq $Balances[0] }
if ($baseResult) {
    $basePct = $baseResult.TotalPct
    foreach ($r in $masterResults) {
        $scaleFactor = $r.InitialBalance / $Balances[0]
        $expectedPnl = $baseResult.TotalPnL * $scaleFactor
        $efficiency = if ($expectedPnl -ne 0) { ($r.TotalPnL / $expectedPnl) * 100 } else { 0 }
        Write-Host ("{0,12}: Return {1,7:F2}% | Expected PnL (linear): {2,12} | Actual: {3,12} | Efficiency: {4:F1}%" -f `
            $r.InitialBalance.ToString('C0'), $r.TotalPct, ([decimal]$expectedPnl).ToString('C2'), $r.TotalPnL.ToString('C2'), $efficiency)
    }
}

# Save master summary CSV
$csvPath = Join-Path $PSScriptRoot "balance_sweep_summary.csv"
$masterResults | Export-Csv -Path $csvPath -NoTypeInformation
Write-Host ""
Write-Host "Summary saved to: $csvPath" -ForegroundColor DarkGray

# Cleanup
if (Test-Path $tempConfigPath) { Remove-Item $tempConfigPath -Force }
# Remove sweep_configs dir if empty
$sweepDir = Join-Path $PSScriptRoot "sweep_configs"
if ((Test-Path $sweepDir) -and ((Get-ChildItem $sweepDir).Count -eq 0)) {
    Remove-Item $sweepDir -Force
}

Pop-Location
