$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

function Run-OVCombo {
    param([string]$Name, [hashtable]$OVOverrides)

    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    $ovRule = $config.TradingBot.TimeRules[0]
    foreach ($key in $OVOverrides.Keys) {
        $ovRule.Overrides | Add-Member -NotePropertyName $key -NotePropertyValue $OVOverrides[$key] -Force
    }

    $configPath = "$configDir\ov_combo_$Name.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath

    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=09:30 --end-time=09:50 "-config=$configPath" 2>&1 | Out-String
        $pl = 0.0; $trades = 0
        if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
        if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
        $totalPL += $pl; $totalTrades += $trades
        $dayLabel = $date.Substring(6,2)
        $perDay += "${dayLabel}:$([math]::Round($pl,0))"
    }

    $plFormatted = "{0,10}" -f ([math]::Round($totalPL,2).ToString('F2'))
    $nameFormatted = "{0,-40}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,5}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== OV PHASE COMBINATION TESTING ==="
Write-Host "OV baseline: +`$75.91 (14 trades)"
Write-Host "Top individuals: Vel15=+`$279, Chop001=+`$250, SMA60=+`$174, Trail05=+`$167"
Write-Host ("{0,-40} | {1,10} | {2,5} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 100)

Run-OVCombo "Vel15+Chop001" @{
    "MinVelocityThreshold" = 0.000015
    "ChopThresholdPercent" = 0.001
}

Run-OVCombo "Vel15+Trail05" @{
    "MinVelocityThreshold" = 0.000015
    "TrailingStopPercent" = 0.005
}

Run-OVCombo "Chop001+Trail05" @{
    "ChopThresholdPercent" = 0.001
    "TrailingStopPercent" = 0.005
}

Run-OVCombo "Vel15+Chop001+Trail05" @{
    "MinVelocityThreshold" = 0.000015
    "ChopThresholdPercent" = 0.001
    "TrailingStopPercent" = 0.005
}

Run-OVCombo "Vel15+Chop001+SMA60" @{
    "MinVelocityThreshold" = 0.000015
    "ChopThresholdPercent" = 0.001
    "SMAWindowSeconds" = 60
}

Run-OVCombo "ALL_WINNERS" @{
    "MinVelocityThreshold" = 0.000015
    "ChopThresholdPercent" = 0.001
    "TrailingStopPercent" = 0.005
    "SMAWindowSeconds" = 60
}

Run-OVCombo "Vel20+Chop001" @{
    "MinVelocityThreshold" = 0.00002
    "ChopThresholdPercent" = 0.001
}

Write-Host ""
Write-Host "=== OV Combo Testing Complete ==="
