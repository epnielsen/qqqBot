$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

function Run-FullDay {
    param([string]$Name, [string]$ConfigPath)

    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 "-config=$ConfigPath" 2>&1 | Out-String
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

# Build combined optimized config
$config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json

# --- Base settings optimizations ---
$config.TradingBot.MinVelocityThreshold = 0.000015
$config.TradingBot.TrendWindowSeconds = 5400
$config.TradingBot.TrailingStopPercent = 0.002
$config.TradingBot.ExitStrategy.TrendWaitSeconds = 180
$config.TradingBot.TrimRatio = 0.75

# --- OV phase optimizations ---
$ovRule = $config.TradingBot.TimeRules[0]
$ovRule.Overrides | Add-Member -NotePropertyName "MinVelocityThreshold" -NotePropertyValue 0.000015 -Force
$ovRule.Overrides | Add-Member -NotePropertyName "TrailingStopPercent" -NotePropertyValue 0.005 -Force

# --- PH phase optimizations (minor: tighter trail) ---
$phRule = $config.TradingBot.TimeRules[1]
$phRule.Overrides | Add-Member -NotePropertyName "TrailingStopPercent" -NotePropertyValue 0.001 -Force

$optimizedPath = "$configDir\fullday_optimized.json"
$config | ConvertTo-Json -Depth 10 | Set-Content $optimizedPath

# Also create a version without PH trail change for comparison
$config2 = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
$config2.TradingBot.MinVelocityThreshold = 0.000015
$config2.TradingBot.TrendWindowSeconds = 5400
$config2.TradingBot.TrailingStopPercent = 0.002
$config2.TradingBot.ExitStrategy.TrendWaitSeconds = 180
$config2.TradingBot.TrimRatio = 0.75
$ovRule2 = $config2.TradingBot.TimeRules[0]
$ovRule2.Overrides | Add-Member -NotePropertyName "MinVelocityThreshold" -NotePropertyValue 0.000015 -Force
$ovRule2.Overrides | Add-Member -NotePropertyName "TrailingStopPercent" -NotePropertyValue 0.005 -Force
$baseOvOnlyPath = "$configDir\fullday_base_ov_only.json"
$config2 | ConvertTo-Json -Depth 10 | Set-Content $baseOvOnlyPath

Write-Host ""
Write-Host "=== FULL-DAY VALIDATION ==="
Write-Host "Baseline full-day: -`$436.23 (77 trades)"
Write-Host ("{0,-40} | {1,10} | {2,5} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 120)

# 1. Current baseline
Run-FullDay "BASELINE" $projectDir\appsettings.json

# 2. Base + OV optimized (no PH change)
Run-FullDay "BASE+OV_OPTIMIZED" $baseOvOnlyPath

# 3. All phases optimized
Run-FullDay "ALL_PHASES_OPTIMIZED" $optimizedPath

Write-Host ""
Write-Host "=== Full-Day Validation Complete ==="
