$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

function Run-Segment {
    param([string]$Date, [string]$Start, [string]$End, [string]$Label)
    
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$Date --speed=0 --start-time=$Start --end-time=$End 2>&1 | Out-String
    $pl = 0.0; $trades = 0
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
    return @{ PL = [math]::Round($pl,2); Trades = $trades }
}

Write-Host ""
Write-Host "=== PER-PHASE P/L BREAKDOWN (OV=09:30-10:13 settings) ==="
Write-Host ""
Write-Host ("{0,-8} | {1,12} | {2,12} | {3,12} | {4,12} | {5,12}" -f "Date", "OV (9:30-10:13)", "Base (10:13-14)", "PH (14:00-16)", "Full Day", "OV % of Total")
Write-Host ("-" * 90)

foreach ($date in $dates) {
    $ov = Run-Segment $date "09:30" "10:13" "OV"
    $base = Run-Segment $date "10:13" "14:00" "Base"
    $ph = Run-Segment $date "14:00" "16:00" "PH"
    $full = Run-Segment $date "09:30" "16:00" "Full"
    
    $ovPct = if ($full.PL -ne 0) { [math]::Round(($ov.PL / $full.PL) * 100, 0) } else { "N/A" }
    
    $dayLabel = $date.Substring(4,2) + "/" + $date.Substring(6,2)
    Write-Host ("{0,-8} | {1,7:F2} ({2,2}t) | {3,7:F2} ({4,2}t) | {5,7:F2} ({6,2}t) | {7,7:F2} ({8,2}t) | {9,5}" -f `
        $dayLabel, $ov.PL, $ov.Trades, $base.PL, $base.Trades, $ph.PL, $ph.Trades, $full.PL, $full.Trades, "$ovPct%")
}

Write-Host ""
