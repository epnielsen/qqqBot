$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

function Build-WinnerConfig {
    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    return $config
}

function Run-Config {
    param([string]$Name, [scriptblock]$Modifier)

    $config = Build-WinnerConfig
    & $Modifier $config

    $configPath = "$configDir\daily_$Name.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath

    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 "-config=$configPath" 2>&1 | Out-String
        $pl = 0.0; $trades = 0; $peak = ""; $trough = ""
        if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
        if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
        if ($output -match 'Peak P/L:\s*\$?([-\d,.]+)\s*at\s*(\S+)') { $peak = "$($Matches[1])@$($Matches[2])" }
        if ($output -match 'Trough P/L:\s*\$?([-\d,.]+)\s*at\s*(\S+)') { $trough = "$($Matches[1])@$($Matches[2])" }
        $totalPL += $pl; $totalTrades += $trades
        $dayLabel = $date.Substring(6,2)
        $perDay += "${dayLabel}:$([math]::Round($pl,0))($trades)pk=$peak"
    }

    $plFormatted = "{0,10}" -f ([math]::Round($totalPL,2).ToString('F2'))
    $nameFormatted = "{0,-36}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,3}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== DAILY PROFIT TARGET SWEEP (with peak P/L info) ==="
Write-Host "Current: DailyTarget=1.5%, TrailStop=0.3% => +`$502.95 (32 trades)"
Write-Host ("{0,-36} | {1,10} | {2,3} | Per-day with peaks" -f "Config","Total P/L","Tr")
Write-Host ("-" * 160)

# --- First: show current with peak info ---
Run-Config "CURRENT_1.5pct_0.3trail" { param($c) }

# --- DailyTarget OFF (no trailing stop matters) ---
Run-Config "TARGET_OFF" { param($c) $c.TradingBot.DailyProfitTargetPercent = 0 }

# --- DailyTarget variations with current 0.3% trail ---
Run-Config "Target0.8_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 0.8 }
Run-Config "Target1.0_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 1.0 }
Run-Config "Target1.25_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 1.25 }
Run-Config "Target1.5_Trail0.3" { param($c) } # same as current
Run-Config "Target1.75_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 1.75 }
Run-Config "Target2.0_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.0 }
Run-Config "Target2.5_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.5 }
Run-Config "Target3.0_Trail0.3" { param($c) $c.TradingBot.DailyProfitTargetPercent = 3.0 }

Write-Host ""
Write-Host "--- Varying TrailingStopPercent with Target=1.5% ---"

# --- Trail variations with 1.5% target ---
Run-Config "Target1.5_Trail0.1" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.1 }
Run-Config "Target1.5_Trail0.2" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.2 }
Run-Config "Target1.5_Trail0.3" { param($c) } # current
Run-Config "Target1.5_Trail0.5" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.5 }
Run-Config "Target1.5_Trail0.75" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.75 }
Run-Config "Target1.5_Trail1.0" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 1.0 }

Write-Host ""
Write-Host "--- Higher targets with wider trails ---"

# --- Combo: higher targets with various trails ---
Run-Config "Target2.0_Trail0.5" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.0; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.5 }
Run-Config "Target2.0_Trail0.75" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.0; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.75 }
Run-Config "Target2.0_Trail1.0" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.0; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 1.0 }
Run-Config "Target2.5_Trail0.5" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.5; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.5 }
Run-Config "Target2.5_Trail0.75" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.5; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.75 }
Run-Config "Target2.5_Trail1.0" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.5; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 1.0 }
Run-Config "Target3.0_Trail0.5" { param($c) $c.TradingBot.DailyProfitTargetPercent = 3.0; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0.5 }
Run-Config "Target3.0_Trail1.0" { param($c) $c.TradingBot.DailyProfitTargetPercent = 3.0; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 1.0 }

Write-Host ""
Write-Host "--- No trail (hard cap at target) ---"
Run-Config "Target1.5_NoTrail" { param($c) $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0 }
Run-Config "Target2.0_NoTrail" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.0; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0 }
Run-Config "Target2.5_NoTrail" { param($c) $c.TradingBot.DailyProfitTargetPercent = 2.5; $c.TradingBot.DailyProfitTargetTrailingStopPercent = 0 }

Write-Host ""
Write-Host "=== Daily Target Sweep Complete ==="
