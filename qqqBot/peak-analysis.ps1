$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$dates = @("20260209","20260210","20260211","20260212","20260213")

Write-Host ""
Write-Host "=== INTRADAY EQUITY PEAK/TROUGH ANALYSIS ==="
Write-Host "Looking at unrealized equity peaks vs final realized P/L"
Write-Host ""

foreach ($date in $dates) {
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $dayLabel = $date.Substring(4,2) + "/" + $date.Substring(6,2)
    
    # Extract key metrics
    $pl = "?"; $trades = "?"; $peak = "?"; $peakTime = "?"; $trough = "?"; $troughTime = "?"
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = $Matches[1] }
    if ($output -match 'Total Trades:\s*(\d+)') { $trades = $Matches[1] }
    if ($output -match 'Peak P/L:\s*[+\-]?\$?([\d,.]+)\s*\([^)]+\)\s*at\s*(\S+)') { $peak = $Matches[1]; $peakTime = $Matches[2] }
    if ($output -match 'Trough P/L:\s*[+\-]?\$?([\d,.]+)\s*\([^)]+\)\s*at\s*(\S+)') { $trough = $Matches[1]; $troughTime = $Matches[2] }
    
    # Also look for daily target trigger
    $targetHit = "no"
    if ($output -match 'DAILY.*(TARGET|PROFIT).*REACHED|target.*reached|profit.*target') { $targetHit = "YES" }
    if ($output -match 'Flattening.*daily|daily.*flatten') { $targetHit = "YES" }
    
    Write-Host "=== Feb $dayLabel ==="
    Write-Host "  Final Realized P/L: `$$pl  ($trades trades)"
    Write-Host "  Peak P/L:           `$$peak  at $peakTime"
    Write-Host "  Trough P/L:         `$$trough  at $troughTime"
    Write-Host "  Peak - Final gap:   `$$([math]::Round([double]($peak -replace ',','') - [math]::Abs([double]($pl -replace ',','')), 2)) left on table"
    Write-Host "  Daily target hit:   $targetHit"
    Write-Host ""
}
