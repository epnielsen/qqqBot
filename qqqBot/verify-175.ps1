$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209","20260210","20260211","20260212","20260213")

$config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
$config.TradingBot.DailyProfitTargetPercent = 1.75
$configPath = "$configDir\verify_175.json"
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath

$total = 0
foreach ($d in $dates) {
    $o = & dotnet run --project $projectDir -- --mode=replay --date=$d --speed=0 "-config=$configPath" 2>&1 | Out-String
    $pl = 0; $pk = "?"
    if ($o -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    if ($o -match 'Peak P/L:\s*[+\-]?\$?([\d,.]+)') { $pk = $Matches[1] }
    $total += $pl
    Write-Host "Feb $($d.Substring(6,2)): `$$([math]::Round($pl,2))  (Peak `$$pk)"
}
Write-Host ""
Write-Host "TOTAL: `$$([math]::Round($total,2))  (vs current `$502.95)"
