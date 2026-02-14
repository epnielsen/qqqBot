$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

function Build-WinnerConfig {
    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    # Base optimizations
    $config.TradingBot.MinVelocityThreshold = 0.000015
    $config.TradingBot.TrendWindowSeconds = 5400
    $config.TradingBot.TrailingStopPercent = 0.002
    $config.TradingBot.ExitStrategy.TrendWaitSeconds = 180
    $config.TradingBot.TrimRatio = 0.75
    # OV optimizations
    $ovRule = $config.TradingBot.TimeRules[0]
    $ovRule.Overrides | Add-Member -NotePropertyName "MinVelocityThreshold" -NotePropertyValue 0.000015 -Force
    $ovRule.Overrides | Add-Member -NotePropertyName "TrailingStopPercent" -NotePropertyValue 0.005 -Force
    return $config
}

function Run-CrossCut {
    param([string]$Name, [scriptblock]$Modifier)

    $config = Build-WinnerConfig
    & $Modifier $config

    $configPath = "$configDir\crosscut_$Name.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath

    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 "-config=$configPath" 2>&1 | Out-String
        $pl = 0.0; $trades = 0
        if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
        if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
        $totalPL += $pl; $totalTrades += $trades
        $dayLabel = $date.Substring(6,2)
        $perDay += "${dayLabel}:$([math]::Round($pl,0))($trades)"
    }

    $plFormatted = "{0,10}" -f ([math]::Round($totalPL,2).ToString('F2'))
    $nameFormatted = "{0,-40}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,5}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== CROSS-CUTTING PARAMETER SWEEP ==="
Write-Host "Winner baseline: +`$502.95 (32 trades, all 5 days profitable)"
Write-Host ("{0,-40} | {1,10} | {2,5} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 120)

# Baseline (winner config unchanged)
Run-CrossCut "WINNER_BASELINE" { param($c) }

# --- Daily Profit Target Trailing Stop ---
Run-CrossCut "DailyTrail=0.1%" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.1 }
Run-CrossCut "DailyTrail=0.2%" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.2 }
Run-CrossCut "DailyTrail=0.3%" { param($c) } # current = 0.3, same as baseline
Run-CrossCut "DailyTrail=0.4%" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.4 }
Run-CrossCut "DailyTrail=0.5%" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.5 }

# --- Daily Profit Target Percent ---
Run-CrossCut "DailyTarget=0.5%" { param($c) $c.TradingBot.DailyProfitTargetPercent = 0.5 }
Run-CrossCut "DailyTarget=1.0%" { param($c) $c.TradingBot.DailyProfitTargetPercent = 1.0 }
Run-CrossCut "DailyTarget=1.5%" { param($c) } # current
Run-CrossCut "DailyTarget=2.0%" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.0 }
Run-CrossCut "DailyTarget=OFF" { param($c) $c.TradingBot.DailyProfitTargetPercent = 0 }

# --- Daily Loss Limit ---
Run-CrossCut "DailyLoss=-0.5%" { param($c) $c.TradingBot.DailyLossLimitPercent = 0.5 }
Run-CrossCut "DailyLoss=-1.0%" { param($c) $c.TradingBot.DailyLossLimitPercent = 1.0 }
Run-CrossCut "DailyLoss=-1.5%" { param($c) $c.TradingBot.DailyLossLimitPercent = 1.5 }

# --- StopLoss Cooldown ---
Run-CrossCut "Cooldown=5s" { param($c) $c.TradingBot.StopLossCooldownSeconds = 5 }
Run-CrossCut "Cooldown=10s" { param($c) } # current
Run-CrossCut "Cooldown=30s" { param($c) $c.TradingBot.StopLossCooldownSeconds = 30 }
Run-CrossCut "Cooldown=60s" { param($c) $c.TradingBot.StopLossCooldownSeconds = 60 }

# --- Profit Reinvestment ---
Run-CrossCut "Reinvest=0%" { param($c) $c.TradingBot.ProfitReinvestmentPercent = 0.0 }
Run-CrossCut "Reinvest=25%" { param($c) $c.TradingBot.ProfitReinvestmentPercent = 0.25 }
Run-CrossCut "Reinvest=50%" { param($c) } # current
Run-CrossCut "Reinvest=75%" { param($c) $c.TradingBot.ProfitReinvestmentPercent = 0.75 }
Run-CrossCut "Reinvest=100%" { param($c) $c.TradingBot.ProfitReinvestmentPercent = 1.0 }

Write-Host ""
Write-Host "=== Cross-Cutting Sweep Complete ==="
