$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

function Run-Config {
    param([string]$Name, [string]$OVEndTime)

    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    $config.TradingBot.TimeRules[0].EndTime = $OVEndTime

    $configPath = "$configDir\ov_fine_$Name.json"
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
    $nameFormatted = "{0,-20}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,3}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== OV WINDOW FINE-TUNE (10:05 - 10:25) ==="
Write-Host ("{0,-20} | {1,10} | {2,3} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 110)

Run-Config "CURRENT_0950" "09:50"
Run-Config "OV_to_1005" "10:05"
Run-Config "OV_to_1007" "10:07"
Run-Config "OV_to_1010" "10:10"
Run-Config "OV_to_1012" "10:12"
Run-Config "OV_to_1013" "10:13"
Run-Config "OV_to_1014" "10:14"
Run-Config "OV_to_1015" "10:15"
Run-Config "OV_to_1016" "10:16"
Run-Config "OV_to_1017" "10:17"
Run-Config "OV_to_1018" "10:18"
Run-Config "OV_to_1020" "10:20"
Run-Config "OV_to_1025" "10:25"

Write-Host ""
Write-Host "=== Sweep Complete ==="
