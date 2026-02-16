$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$dates = @("20260209","20260210","20260211","20260212","20260213")

Write-Host "=== VERIFY OV EndTime=10:13 (using appsettings.json) ==="
$totalPL = 0.0; $totalTrades = 0
foreach ($date in $dates) {
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $pl = 0.0; $trades = 0
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    if ($output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
    $totalPL += $pl; $totalTrades += $trades
    Write-Host "  $date : P/L = `$$([math]::Round($pl,2))  Trades = $trades"
}
Write-Host ""
Write-Host "  TOTAL: P/L = `$$([math]::Round($totalPL,2))  Trades = $totalTrades"
Write-Host ""
Write-Host "  Expected: `$608.90  Trades = 38"
