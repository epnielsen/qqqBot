$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

function Run-Config {
    param([string]$Name, [double]$Target, [double]$Trail)

    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    $config.TradingBot.DailyProfitTargetPercent = $Target
    $config.TradingBot.DailyProfitTargetTrailingStopPercent = $Trail

    $configPath = "$configDir\dt_$Name.json"
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
Write-Host "=== FINE-TUNE AROUND TARGET=1.75% ==="
Write-Host ("{0,-30} | {1,10} | {2,3} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 130)

# Reference points
Run-Config "T1.5_Tr0.3_CURRENT" 1.5 0.3

# Fine-tune target around 1.75%
Run-Config "T1.6_Tr0.3" 1.6 0.3
Run-Config "T1.65_Tr0.3" 1.65 0.3
Run-Config "T1.7_Tr0.3" 1.7 0.3
Run-Config "T1.75_Tr0.3" 1.75 0.3
Run-Config "T1.8_Tr0.3" 1.8 0.3
Run-Config "T1.85_Tr0.3" 1.85 0.3
Run-Config "T1.9_Tr0.3" 1.9 0.3
Run-Config "T1.95_Tr0.3" 1.95 0.3

Write-Host ""
Write-Host "--- Target=1.75% with varying trails ---"
Run-Config "T1.75_Tr0.1" 1.75 0.1
Run-Config "T1.75_Tr0.2" 1.75 0.2
Run-Config "T1.75_Tr0.3" 1.75 0.3
Run-Config "T1.75_Tr0.4" 1.75 0.4
Run-Config "T1.75_Tr0.5" 1.75 0.5
Run-Config "T1.75_Tr0.75" 1.75 0.75
Run-Config "T1.75_Tr1.0" 1.75 1.0
Run-Config "T1.75_Tr1.25" 1.75 1.25
Run-Config "T1.75_NoTrail" 1.75 0

Write-Host ""
Write-Host "--- Best target with wider trails ---"
Run-Config "T1.5_Tr1.0" 1.5 1.0
Run-Config "T1.75_Tr1.0" 1.75 1.0
Run-Config "T1.85_Tr0.5" 1.85 0.5
Run-Config "T1.85_Tr1.0" 1.85 1.0

Write-Host ""
Write-Host "=== Fine-Tune Complete ==="
