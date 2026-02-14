# combined-test.ps1 — Test combined Base phase winner
Set-Location "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$sweepDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot\sweep_configs"

$c = Get-Content "c:\dev\TradeEcosystem\qqqBot\qqqBot\appsettings.json" -Raw | ConvertFrom-Json
$c.TradingBot.MinVelocityThreshold = 0.000015
$c.TradingBot.TrendWindowSeconds = 5400
$c.TradingBot.TrailingStopPercent = 0.002
$c.TradingBot.ExitStrategy.TrendWaitSeconds = 180
$c.TradingBot.TrimRatio = 0.75
$c | ConvertTo-Json -Depth 10 | Set-Content "$sweepDir\base_combined_winner.json"

Write-Host "`n=== Combined Base Phase Winner ===" -ForegroundColor Cyan
Write-Host "Settings: Vel=0.000015, TrendWindow=5400, TrailStop=0.20%, TrendWait=180s, TrimRatio=75%"

$total = 0; $trades = 0
foreach ($d in @("20260209","20260210","20260211","20260212","20260213")) {
    $out = dotnet run -- --mode=replay --date=$d --speed=0 --start-time=09:50 --end-time=14:00 "-config=$sweepDir\base_combined_winner.json" 2>&1 | ForEach-Object {"$_"}
    $p = 0; $t = 0
    foreach ($l in $out) {
        if ($l -match 'Realized P/L:\s+\$?([-\d,.]+)') { $p = [decimal]($matches[1] -replace ',','') }
        if ($l -match 'Total Trades:\s+(\d+)') { $t = [int]$matches[1] }
    }
    $total += $p; $trades += $t
    $color = if ($p -gt 0) {"Green"} elseif ($p -gt -50) {"Yellow"} else {"Red"}
    Write-Host "  $d : `$$([math]::Round($p,2)) ($t trades)" -ForegroundColor $color
}
Write-Host "`n  COMBINED TOTAL: `$$([math]::Round($total,2)) ($trades trades)" -ForegroundColor $(if ($total -gt 0) {"Green"} else {"Red"})
Write-Host "  vs Baseline:    `$-415.43 (55 trades)" -ForegroundColor DarkGray
Write-Host "  Improvement:    `$$([math]::Round($total - (-415.43), 2))" -ForegroundColor Cyan
