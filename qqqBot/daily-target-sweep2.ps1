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

    $configPath = "$configDir\dt2_$Name.json"
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
    $nameFormatted = "{0,-32}" -f $Name
    $perDayStr = $perDay -join " | "
    Write-Host "$nameFormatted | `$$plFormatted | $("{0,3}" -f $totalTrades) | $perDayStr"
}

Write-Host ""
Write-Host "=== LOW TARGET + WIDE TRAIL SWEEP ==="
Write-Host "Goal: Low target arms on Feb 9 peak `$81, wide trail lets Feb 11-13 run"
Write-Host "Peaks: Feb9=`$81(0.81%) Feb10=`$42(0.42%) Feb11=`$182(1.82%) Feb12=`$155-190(~1.8%) Feb13=`$182(1.82%)"
Write-Host ""
Write-Host ("{0,-32} | {1,10} | {2,3} | Per-day" -f "Config","Total P/L","Tr")
Write-Host ("-" * 130)

# Reference: current settings
Run-Config "CURRENT_T1.75_Tr0.3" 1.75 0.3

# --- Target at 0.5% ($50) — would arm on Feb 9 peak $81 ---
Run-Config "T0.5_Tr1" 0.5 1
Run-Config "T0.5_Tr2" 0.5 2
Run-Config "T0.5_Tr3" 0.5 3
Run-Config "T0.5_Tr5" 0.5 5
Run-Config "T0.5_Tr10" 0.5 10
Run-Config "T0.5_Tr15" 0.5 15
Run-Config "T0.5_Tr20" 0.5 20

# --- Target at 0.6% ($60) ---
Run-Config "T0.6_Tr5" 0.6 5
Run-Config "T0.6_Tr10" 0.6 10
Run-Config "T0.6_Tr15" 0.6 15
Run-Config "T0.6_Tr20" 0.6 20

# --- Target at 0.75% ($75) — just below Feb 9 peak ---
Run-Config "T0.75_Tr5" 0.75 5
Run-Config "T0.75_Tr10" 0.75 10
Run-Config "T0.75_Tr15" 0.75 15
Run-Config "T0.75_Tr20" 0.75 20

# --- Target at 0.4% ($40) — would arm on Feb 10 peak $42 too ---
Run-Config "T0.4_Tr5" 0.4 5
Run-Config "T0.4_Tr10" 0.4 10
Run-Config "T0.4_Tr15" 0.4 15
Run-Config "T0.4_Tr20" 0.4 20

Write-Host ""
Write-Host "--- Best targets from above with finer trail ---"

# (These will be filled in based on initial results)
Run-Config "T0.5_Tr7" 0.5 7
Run-Config "T0.5_Tr8" 0.5 8
Run-Config "T0.5_Tr12" 0.5 12
Run-Config "T0.6_Tr7" 0.6 7
Run-Config "T0.6_Tr8" 0.6 8
Run-Config "T0.6_Tr12" 0.6 12

Write-Host ""
Write-Host "=== Sweep Complete ==="
