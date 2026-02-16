$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

function Run-Config {
    param([string]$Name, [string]$OVEndTime, [string]$BaseStartTime, [string]$PHStartTime)

    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    
    # Adjust OV end time
    $config.TradingBot.TimeRules[0].EndTime = $OVEndTime
    
    # If we need to adjust PH start (for "no base" configs)
    if ($PHStartTime) {
        $config.TradingBot.TimeRules[1].StartTime = $PHStartTime
    }

    $configPath = "$configDir\ov_ext_$Name.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath

    $totalPL = 0.0; $totalTrades = 0; $perDay = @()
    foreach ($date in $dates) {
        $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 "-config=$configPath" 2>&1 | Out-String
        $pl = 0.0; $trades = 0
        if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
        if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
        $totalPL += $pl; $totalTrades += $trades
        $dayLabel = $date.Substring(6,2)
        $perDay += "${dayLabel}:`$$([math]::Round($pl,0))($trades)"
    }

    $plFormatted = "{0,10}" -f ([math]::Round($totalPL,2).ToString('F2'))
    $nameFormatted = "{0,-30}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,3}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== OV WINDOW EXTENSION SWEEP ==="
Write-Host "Testing: extend OV phase (aggressive settings) past 09:50"
Write-Host ""
Write-Host ("{0,-30} | {1,10} | {2,3} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 120)

# Reference: current settings (OV 09:30-09:50)
Run-Config "CURRENT_OV_0950" "09:50" $null $null

# Extend OV to various end times
Run-Config "OV_to_0955" "09:55" $null $null
Run-Config "OV_to_1000" "10:00" $null $null
Run-Config "OV_to_1005" "10:05" $null $null
Run-Config "OV_to_1010" "10:10" $null $null
Run-Config "OV_to_1015" "10:15" $null $null
Run-Config "OV_to_1020" "10:20" $null $null
Run-Config "OV_to_1030" "10:30" $null $null
Run-Config "OV_to_1045" "10:45" $null $null
Run-Config "OV_to_1100" "11:00" $null $null

# What if OV ran ALL the way to Power Hour?
Run-Config "OV_to_1400_noPH" "14:00" $null "14:00"
Run-Config "OV_to_1600_allday" "16:00" $null "16:00"

Write-Host ""
Write-Host "=== Sweep Complete ==="
