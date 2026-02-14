$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

Write-Host ""
Write-Host "=== PHASE-LEVEL PROFIT ANALYSIS ==="
Write-Host "What if we stopped trading after each phase when profitable?"
Write-Host ""

# For each day, run OV alone, then Base alone, then Full Day
# to understand per-phase contributions

Write-Host "=== Per-Phase P/L Breakdown (current optimized settings) ==="
Write-Host ""
Write-Host ("{0,-8} | {1,8} | {2,8} | {3,8} | {4,8} | {5,8} | {6,8}" -f "Day","OV P/L","OV Peak","Base P/L","Base Pk","FullDay","FD Peak")
Write-Host ("-" * 80)

foreach ($date in $dates) {
    $dayLabel = "Feb " + $date.Substring(6,2)
    
    # OV only
    $ovOut = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=09:30 --end-time=09:50 2>&1 | Out-String
    $ovPL = 0; $ovPeak = 0
    if ($ovOut -match 'Realized P/L:\s*\$?([-\d,.]+)') { $ovPL = [math]::Round([double]($Matches[1] -replace ",",""), 0) }
    if ($ovOut -match 'Peak P/L:\s*[+\-]?\$?([\d,.]+)') { $ovPeak = [math]::Round([double]($Matches[1] -replace ",",""), 0) }
    
    # Base only
    $baseOut = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=09:50 --end-time=14:00 2>&1 | Out-String
    $basePL = 0; $basePeak = 0
    if ($baseOut -match 'Realized P/L:\s*\$?([-\d,.]+)') { $basePL = [math]::Round([double]($Matches[1] -replace ",",""), 0) }
    if ($baseOut -match 'Peak P/L:\s*[+\-]?\$?([\d,.]+)') { $basePeak = [math]::Round([double]($Matches[1] -replace ",",""), 0) }
    
    # Full day
    $fdOut = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $fdPL = 0; $fdPeak = 0
    if ($fdOut -match 'Realized P/L:\s*\$?([-\d,.]+)') { $fdPL = [math]::Round([double]($Matches[1] -replace ",",""), 0) }
    if ($fdOut -match 'Peak P/L:\s*[+\-]?\$?([\d,.]+)') { $fdPeak = [math]::Round([double]($Matches[1] -replace ",",""), 0) }
    
    Write-Host ("{0,-8} | {1,8} | {2,8} | {3,8} | {4,8} | {5,8} | {6,8}" -f $dayLabel,"`$$ovPL","`$$ovPeak","`$$basePL","`$$basePeak","`$$fdPL","`$$fdPeak")
}

Write-Host ""
Write-Host "=== Simulated 'Stop After OV If Profitable' ==="
Write-Host "(If OV P/L >= threshold, skip Base+PH. Otherwise, full day.)"
Write-Host ""

$thresholds = @(0, 10, 20, 30, 50, 75, 100)

Write-Host ("{0,-20} | {1,10} | Per-day" -f "Strategy","Total P/L")
Write-Host ("-" * 100)

# Full day baseline
$fdTotal = 0; $fdDays = @()
foreach ($date in $dates) {
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $pl = 0
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    $fdTotal += $pl
    $fdDays += "$($date.Substring(6,2)):`$$([math]::Round($pl,0))"
}
Write-Host ("{0,-20} | `${1,9} | {2}" -f "FullDay",[math]::Round($fdTotal,2),($fdDays -join " | "))

# For each threshold, simulate stop-after-OV
foreach ($thresh in $thresholds) {
    $total = 0; $days = @()
    foreach ($date in $dates) {
        # Run OV
        $ovOut = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=09:30 --end-time=09:50 2>&1 | Out-String
        $ovPL = 0
        if ($ovOut -match 'Realized P/L:\s*\$?([-\d,.]+)') { $ovPL = [double]($Matches[1] -replace ",","") }
        
        if ($ovPL -ge $thresh) {
            # OV was profitable enough — stop here
            $total += $ovPL
            $days += "$($date.Substring(6,2)):`$$([math]::Round($ovPL,0))*"
        } else {
            # Run full day
            $fdOut = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
            $fdPL = 0
            if ($fdOut -match 'Realized P/L:\s*\$?([-\d,.]+)') { $fdPL = [double]($Matches[1] -replace ",","") }
            $total += $fdPL
            $days += "$($date.Substring(6,2)):`$$([math]::Round($fdPL,0))"
        }
    }
    $label = "StopAfterOV>=$thresh"
    Write-Host ("{0,-20} | `${1,9} | {2}" -f $label,[math]::Round($total,2),($days -join " | "))
}

Write-Host ""
Write-Host "(* = stopped after OV phase)"
Write-Host ""
Write-Host "=== Phase Analysis Complete ==="
