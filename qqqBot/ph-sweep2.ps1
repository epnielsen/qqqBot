$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

function Run-PHConfig {
    param([string]$Name, [hashtable]$Overrides)
    
    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    
    # Turn daily target OFF so PH actually gets a chance to trade
    $config.TradingBot.DailyProfitTargetPercent = 0
    
    # Apply PH overrides
    foreach ($k in $Overrides.Keys) {
        $config.TradingBot.TimeRules[1].Overrides | Add-Member -NotePropertyName $k -NotePropertyValue $Overrides[$k] -Force
    }
    
    $configPath = "$configDir\ph2_$Name.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath
    
    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        # Run PH segment only (14:00-16:00) in isolation to see what PH could do
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=14:00 --end-time=16:00 "-config=$configPath" 2>&1 | Out-String
        $pl = 0.0; $trades = 0
        if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
        if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
        $totalPL += $pl; $totalTrades += $trades
        $dayLabel = $date.Substring(6,2)
        $perDay += "${dayLabel}:`$$([math]::Round($pl,0))($trades)"
    }
    
    $plFormatted = "{0,10}" -f ([math]::Round($totalPL,2).ToString('F2'))
    $nameFormatted = "{0,-28}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,3}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== POWER HOUR SETTINGS SWEEP (isolated 14:00-16:00) ==="
Write-Host "Daily target OFF so PH can trade freely"
Write-Host ""
Write-Host ("{0,-28} | {1,10} | {2,3} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 110)

# Current PH settings
Run-PHConfig "CURRENT_PH" @{}

# Disable PH entirely (use base settings)
Run-PHConfig "BASE_SETTINGS_IN_PH" @{
    "MinVelocityThreshold" = 0.000015
    "SMAWindowSeconds" = 180
    "SlopeWindowSize" = 20
    "ChopThresholdPercent" = 0.0011
    "TrendWaitSeconds" = 180
    "TrendConfidenceThreshold" = 0.00008
    "TrailingStopPercent" = 0.002
}

# Use OV-style aggressive settings in PH
Run-PHConfig "OV_SETTINGS_IN_PH" @{
    "MinVelocityThreshold" = 0.000015
    "SMAWindowSeconds" = 120
    "ChopThresholdPercent" = 0.0015
    "MinChopAbsolute" = 0.05
    "TrendWindowSeconds" = 900
    "TrendWaitSeconds" = 180
    "TrendConfidenceThreshold" = 0.00012
    "TrailingStopPercent" = 0.005
}

# Try wider velocity thresholds (more selective)
Run-PHConfig "PH_Vel25" @{ "MinVelocityThreshold" = 0.000025 }
Run-PHConfig "PH_Vel30" @{ "MinVelocityThreshold" = 0.00003 }
Run-PHConfig "PH_Vel50" @{ "MinVelocityThreshold" = 0.00005 }

# Try tighter trailing stops
Run-PHConfig "PH_Trail0.1pct" @{ "TrailingStopPercent" = 0.001 }
Run-PHConfig "PH_Trail0.25pct" @{ "TrailingStopPercent" = 0.0025 }
Run-PHConfig "PH_Trail0.4pct" @{ "TrailingStopPercent" = 0.004 }

# Try wider chop filter
Run-PHConfig "PH_Chop0.002" @{ "ChopThresholdPercent" = 0.002 }
Run-PHConfig "PH_Chop0.003" @{ "ChopThresholdPercent" = 0.003 }

# Try longer trend window
Run-PHConfig "PH_TrendWin3600" @{ "TrendWindowSeconds" = 3600 }
Run-PHConfig "PH_TrendWin5400" @{ "TrendWindowSeconds" = 5400 }

# Combo: wider velocity + wider trail
Run-PHConfig "PH_Vel25_Trail0.3" @{ "MinVelocityThreshold" = 0.000025; "TrailingStopPercent" = 0.003 }
Run-PHConfig "PH_Vel30_Trail0.4" @{ "MinVelocityThreshold" = 0.00003; "TrailingStopPercent" = 0.004 }

# Combo: wider velocity + wider chop 
Run-PHConfig "PH_Vel25_Chop0.002" @{ "MinVelocityThreshold" = 0.000025; "ChopThresholdPercent" = 0.002 }

Write-Host ""
Write-Host "=== Sweep Complete ==="
