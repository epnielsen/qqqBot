# fine-tune2.ps1 — Push TrendWindow higher since 5400 was best
Set-Location "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$base = "c:\dev\TradeEcosystem\qqqBot\qqqBot\sweep_configs"

$variants = @(
    @{Name="Vel15_T4800"; Vel=0.000015; Trend=4800},
    @{Name="Vel15_T5400"; Vel=0.000015; Trend=5400},
    @{Name="Vel15_T6000"; Vel=0.000015; Trend=6000},
    @{Name="Vel15_T7200"; Vel=0.000015; Trend=7200},
    @{Name="Vel15_T9000"; Vel=0.000015; Trend=9000},
    @{Name="Vel15_T10800"; Vel=0.000015; Trend=10800},
    @{Name="Vel10_T5400"; Vel=0.000010; Trend=5400},
    @{Name="Vel08_T5400"; Vel=0.000008; Trend=5400}
)

foreach ($v in $variants) {
    $c = Get-Content "c:\dev\TradeEcosystem\qqqBot\qqqBot\appsettings.json" -Raw | ConvertFrom-Json
    $c.TradingBot.MinVelocityThreshold = $v.Vel
    $c.TradingBot.TrendWindowSeconds = $v.Trend
    $c | ConvertTo-Json -Depth 10 | Set-Content "$base\fine2_$($v.Name).json"
}

Write-Host ""
Write-Host "=== Fine-Tuning Round 2: Longer TrendWindow ===" -ForegroundColor Cyan
Write-Host ("{0,-18} | {1,10} | {2,5} | {3}" -f "Config", "Total P/L", "Tr", "Per-day breakdown") -ForegroundColor Yellow
Write-Host ("-" * 100) -ForegroundColor Yellow

foreach ($v in $variants) {
    $total = 0; $trades = 0; $days = @()
    foreach ($d in @("20260209","20260210","20260211","20260212","20260213")) {
        $cfgPath = "$base\fine2_$($v.Name).json"
        $out = dotnet run -- --mode=replay --date=$d --speed=0 --start-time=09:50 --end-time=14:00 "-config=$cfgPath" 2>&1 | ForEach-Object {"$_"}
        $p = 0; $t = 0
        foreach ($l in $out) {
            if ($l -match 'Realized P/L:\s+\$?([-\d,.]+)') { $p = [decimal]($matches[1] -replace ',','') }
            if ($l -match 'Total Trades:\s+(\d+)') { $t = [int]$matches[1] }
        }
        $total += $p; $trades += $t
        $days += "$($d.Substring(6)):$([math]::Round($p,0))"
    }
    $color = if ($total -gt 50) {"Green"} elseif ($total -gt 0) {"Yellow"} else {"Red"}
    Write-Host ("{0,-18} | {1,10} | {2,5} | {3}" -f $v.Name, "`$$([math]::Round($total,2))", $trades, ($days -join " | ")) -ForegroundColor $color
}
Write-Host "`n=== Complete ===" -ForegroundColor Cyan
