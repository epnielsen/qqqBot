$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$dates = @("20260209","20260210","20260211","20260212","20260213")

Write-Host ""
Write-Host "=== VERIFICATION: appsettings.json now reproduces winner ==="
Write-Host "Expected: +`$502.95 (32 trades)"
Write-Host ""

$totalPL = 0.0; $totalTrades = 0
foreach ($date in $dates) {
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $pl = 0.0; $trades = 0
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
    $totalPL += $pl; $totalTrades += $trades
    $dayLabel = $date.Substring(6,2)
    Write-Host "  Feb $dayLabel : `$$([math]::Round($pl,2))  ($trades trades)"
}
Write-Host ""
Write-Host "  TOTAL: `$$([math]::Round($totalPL,2))  ($totalTrades trades)"
Write-Host ""
if ([math]::Abs($totalPL - 502.95) -lt 1.0) {
    Write-Host "  [PASS] Settings verified! Matches expected result."
} else {
    Write-Host "  [WARN] Result differs from expected +`$502.95. Check settings."
}
