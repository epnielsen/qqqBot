$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

function Run-PHConfig {
    param([string]$Name, [hashtable]$PHOverrides, [hashtable]$PHRemove = @{})

    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    $phRule = $config.TradingBot.TimeRules[1]
    foreach ($key in $PHOverrides.Keys) {
        $phRule.Overrides | Add-Member -NotePropertyName $key -NotePropertyValue $PHOverrides[$key] -Force
    }
    foreach ($key in $PHRemove.Keys) {
        $phRule.Overrides.PSObject.Properties.Remove($key)
    }

    $configPath = "$configDir\ph_$Name.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath

    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=14:00 --end-time=16:00 "-config=$configPath" 2>&1 | Out-String
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
Write-Host "=== PH PHASE SWEEP ==="
Write-Host "PH baseline: -`$26.22 (2 trades, only Feb 12 active)"
Write-Host "Current PH: Vel=0.000015, Trail=0.15%, SMA=120, Chop=0.0015, TrendWait=60"
Write-Host ("{0,-40} | {1,10} | {2,5} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 100)

# Current baseline
Run-PHConfig "CURRENT_PH" @{}

# --- VELOCITY SWEEP (lower to get more trades) ---
Run-PHConfig "PH_Vel=8E-06" @{ "MinVelocityThreshold" = 0.000008 }
Run-PHConfig "PH_Vel=1E-05" @{ "MinVelocityThreshold" = 0.00001 }
Run-PHConfig "PH_Vel=1.2E-05" @{ "MinVelocityThreshold" = 0.000012 }
Run-PHConfig "PH_Vel=2E-05" @{ "MinVelocityThreshold" = 0.00002 }
Run-PHConfig "PH_Vel=2.5E-05" @{ "MinVelocityThreshold" = 0.000025 }

# --- TRAILING STOP ---
Run-PHConfig "PH_Trail=0.1%" @{ "TrailingStopPercent" = 0.001 }
Run-PHConfig "PH_Trail=0.2%" @{ "TrailingStopPercent" = 0.002 }
Run-PHConfig "PH_Trail=0.25%" @{ "TrailingStopPercent" = 0.0025 }
Run-PHConfig "PH_Trail=0.3%" @{ "TrailingStopPercent" = 0.003 }

# --- CHOP THRESHOLD ---
Run-PHConfig "PH_Chop=0.001" @{ "ChopThresholdPercent" = 0.001 }
Run-PHConfig "PH_Chop=0.002" @{ "ChopThresholdPercent" = 0.002 }

# --- TREND WAIT ---
Run-PHConfig "PH_TrendWait=120" @{ "TrendWaitSeconds" = 120 }
Run-PHConfig "PH_TrendWait=180" @{ "TrendWaitSeconds" = 180 }

# --- SMA WINDOW ---
Run-PHConfig "PH_SMA=60" @{ "SMAWindowSeconds" = 60 }
Run-PHConfig "PH_SMA=180" @{ "SMAWindowSeconds" = 180 }

# --- TREND WINDOW ---
Run-PHConfig "PH_TrendWin=1800" @{ "TrendWindowSeconds" = 1800 }
Run-PHConfig "PH_TrendWin=3600" @{ "TrendWindowSeconds" = 3600 }
Run-PHConfig "PH_TrendWin=5400" @{ "TrendWindowSeconds" = 5400 }

# --- DSL OFF ---
Run-PHConfig "PH_DSL=OFF" @{ "DynamicStopLossEnabled" = $false }

# --- BULL ONLY ---
Run-PHConfig "PH_BullOnly" @{ "AllowedDirection" = "BullOnly" }

# --- PH DISABLED (use base settings) ---
# Set velocity impossibly high to block entries
Run-PHConfig "PH_DISABLED" @{ "MinVelocityThreshold" = 1.0 }

Write-Host ""
Write-Host "=== PH Sweep Complete ==="
