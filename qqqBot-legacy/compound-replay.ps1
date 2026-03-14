# Compounding Multi-Week Replay Simulation
# Runs sequential daily replays, carrying forward the ending balance to the next day
# Uses appsettings.json as base config (MR PH strategy, DPT=1.25%, DLL=1.0%)

param(
    [decimal]$InitialBalance = 30000,
    [string]$BaseConfig = "appsettings.json",
    [string[]]$Dates = @()
)

$ErrorActionPreference = "Stop"

# Ensure we're in the script's directory (where dotnet project is)
Push-Location $PSScriptRoot

# Use provided dates or default to Feb 9-27 2026
if ($Dates.Count -gt 0) {
    $dates = $Dates
} else {
    $dates = @(
        "20260209", "20260210", "20260211", "20260212", "20260213",
        "20260217", "20260218", "20260219", "20260220",
        "20260223", "20260224", "20260225", "20260226", "20260227"
    )
}

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
$tempConfigPath = Join-Path $tempDir "compound_run.json"

# Results tracking
$results = @()
$balance = $InitialBalance

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " COMPOUNDING MULTI-WEEK REPLAY" -ForegroundColor Cyan
Write-Host " Starting Balance: $($InitialBalance.ToString('C2'))" -ForegroundColor Cyan
Write-Host " Config: $BaseConfig" -ForegroundColor Cyan
Write-Host " Dates: $($dates[0]) - $($dates[-1]) ($($dates.Count) days)" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

foreach ($date in $dates) {
    # Deep clone config and set StartingAmount
    $cfg = $baseJson | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $cfg.TradingBot.StartingAmount = [double]$balance
    $cfg | ConvertTo-Json -Depth 10 | Set-Content $tempConfigPath -Encoding UTF8

    Write-Host "[$date] Starting: $($balance.ToString('C2')) ..." -NoNewline -ForegroundColor Yellow

    # Run replay and capture ALL output
    $output = dotnet run -- --mode=replay --date=$date --speed=0 "-config=$tempConfigPath" 2>&1 | Out-String

    # Parse ending equity from output
    # Format: [SIM-BROKER]  Ending Equity:  $29,974.43  (extra spaces in log output)
    $endingEquityMatch = [regex]::Match($output, '\[SIM-BROKER\]\s+Ending Equity:\s+\$([\d,]+\.?\d*)')
    # Also parse Realized P/L: [SIM-BROKER]  Realized P/L:   $-25.57
    $realizedPnlMatch = [regex]::Match($output, '\[SIM-BROKER\]\s+Realized P/L:\s+\$(-?[\d,]+\.?\d*)')
    # Parse peak/trough
    $peakMatch = [regex]::Match($output, '\[SIM-BROKER\]\s+Peak P/L:\s+\+?\$([\d,]+\.?\d*)')
    $troughMatch = [regex]::Match($output, '\[SIM-BROKER\]\s+Trough P/L:\s+-?\$([\d,]+\.?\d*)')
    # Parse trade count
    $tradeCountMatch = [regex]::Match($output, 'Total trades:\s*(\d+)')

    if ($endingEquityMatch.Success) {
        $newBalance = [decimal]($endingEquityMatch.Groups[1].Value -replace ',', '')
        $dailyPnl = $newBalance - $balance
        $dailyPct = if ($balance -ne 0) { ($dailyPnl / $balance) * 100 } else { 0 }
        $trades = if ($tradeCountMatch.Success) { [int]$tradeCountMatch.Groups[1].Value } else { 0 }
        $peakPnl = if ($peakMatch.Success) { [decimal]($peakMatch.Groups[1].Value -replace ',', '') } else { 0 }
        $troughPnl = if ($troughMatch.Success) { -[decimal]($troughMatch.Groups[1].Value -replace ',', '') } else { 0 }

        $color = if ($dailyPnl -ge 0) { "Green" } else { "Red" }
        Write-Host (" P/L: {0} ({1:F2}%) | End: {2} | Trades: {3}" -f $dailyPnl.ToString('C2'), $dailyPct, $newBalance.ToString('C2'), $trades) -ForegroundColor $color

        $results += [PSCustomObject]@{
            Date        = $date
            StartBal    = $balance
            EndBal      = $newBalance
            DailyPnL    = $dailyPnl
            DailyPct    = [math]::Round($dailyPct, 4)
            CumPnL      = $newBalance - $InitialBalance
            Trades      = $trades
            PeakPnL     = $peakPnl
            TroughPnL   = $troughPnl
        }

        $balance = $newBalance
    }
    else {
        Write-Host " FAILED TO PARSE OUTPUT" -ForegroundColor Red
        # Try to find any useful info
        $lastLines = ($output -split "`n") | Select-Object -Last 20
        Write-Host "Last 20 lines of output:" -ForegroundColor DarkGray
        $lastLines | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        
        $results += [PSCustomObject]@{
            Date        = $date
            StartBal    = $balance
            EndBal      = $balance  # carry forward unchanged
            DailyPnL    = 0
            DailyPct    = 0
            CumPnL      = $balance - $InitialBalance
            Trades      = 0
            PeakPnL     = 0
            TroughPnL   = 0
        }
    }
}

# Summary
Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " COMPOUNDING RESULTS SUMMARY" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

$totalPnL = $balance - $InitialBalance
$totalPct = ($totalPnL / $InitialBalance) * 100
$winDays = ($results | Where-Object { $_.DailyPnL -gt 0 }).Count
$lossDays = ($results | Where-Object { $_.DailyPnL -lt 0 }).Count
$flatDays = ($results | Where-Object { $_.DailyPnL -eq 0 }).Count
$avgDailyPnl = ($results | Measure-Object -Property DailyPnL -Average).Average
$maxWin = ($results | Measure-Object -Property DailyPnL -Maximum).Maximum
$maxLoss = ($results | Measure-Object -Property DailyPnL -Minimum).Minimum

Write-Host ("Initial Balance:    {0}" -f $InitialBalance.ToString('C2'))
Write-Host ("Final Balance:      {0}" -f $balance.ToString('C2'))
$color = if ($totalPnL -ge 0) { "Green" } else { "Red" }
Write-Host ("Total P/L:          {0} ({1:F2}%)" -f $totalPnL.ToString('C2'), $totalPct) -ForegroundColor $color
Write-Host ("Win/Loss/Flat:      {0}/{1}/{2}" -f $winDays, $lossDays, $flatDays)
Write-Host ("Win Rate:           {0:F1}%" -f (($winDays / $dates.Count) * 100))
Write-Host ("Avg Daily P/L:      {0}" -f ([decimal]$avgDailyPnl).ToString('C2'))
Write-Host ("Best Day:           {0}" -f ([decimal]$maxWin).ToString('C2'))
Write-Host ("Worst Day:          {0}" -f ([decimal]$maxLoss).ToString('C2'))

# Equity curve
Write-Host ""
Write-Host "--- Equity Curve ---" -ForegroundColor Cyan
Write-Host ("{0,-12} {1,14} {2,12} {3,10} {4,14}" -f "Date", "Start", "P/L", "P/L %", "End Balance")
Write-Host ("{0,-12} {1,14} {2,12} {3,10} {4,14}" -f "----", "-----", "---", "-----", "-----------")
foreach ($r in $results) {
    $color = if ($r.DailyPnL -ge 0) { "Green" } elseif ($r.DailyPnL -lt 0) { "Red" } else { "White" }
    $line = "{0,-12} {1,14} {2,12} {3,10} {4,14}" -f $r.Date, $r.StartBal.ToString('C2'), $r.DailyPnL.ToString('C2'), ("{0:F2}%" -f $r.DailyPct), $r.EndBal.ToString('C2')
    Write-Host $line -ForegroundColor $color
}

# Also compare to flat (non-compounding) baseline
Write-Host ""
Write-Host "--- Compounding vs Flat $30k Baseline ---" -ForegroundColor Cyan
$flatTotal = ($results | Measure-Object -Property DailyPnL -Sum).Sum
Write-Host ("Compounding Final:  {0} (P/L: {1})" -f $balance.ToString('C2'), $totalPnL.ToString('C2'))
Write-Host "(Note: In flat mode, DPT/DLL % targets produce different dollar amounts." -ForegroundColor DarkGray
Write-Host " The compounding effect means winning days have higher dollar targets.)" -ForegroundColor DarkGray

# Save results CSV
$csvPath = Join-Path $PSScriptRoot "compound_results.csv"
$results | Export-Csv -Path $csvPath -NoTypeInformation
Write-Host ""
Write-Host "Results saved to: $csvPath" -ForegroundColor DarkGray

# Cleanup temp config
if (Test-Path $tempConfigPath) { Remove-Item $tempConfigPath -Force }

Pop-Location
