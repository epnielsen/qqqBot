$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"

# Create config with daily target OFF
$config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
$config.TradingBot.DailyProfitTargetPercent = 0
$configPath = "$configDir\notarget_ov1013.json"
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath

Write-Host "=== FEB 11 + 13: DAILY TARGET ON vs OFF ==="
Write-Host ""

foreach ($date in @("20260211","20260213")) {
    $dayLabel = $date.Substring(6,2)
    
    # With target (current settings)
    $out1 = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $pl1 = 0.0; if ($out1 -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl1 = [double]($Matches[1] -replace ",","") }
    $tr1 = 0; if ($out1 -match 'Total Trades:\s*(\d+)') { $tr1 = [int]$Matches[1] }
    
    # Without target
    $out2 = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 "-config=$configPath" 2>&1 | Out-String
    $pl2 = 0.0; if ($out2 -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl2 = [double]($Matches[1] -replace ",","") }
    $tr2 = 0; if ($out2 -match 'Total Trades:\s*(\d+)') { $tr2 = [int]$Matches[1] }
    
    # Extract peak from no-target run
    $peak = ""; if ($out2 -match 'Peak.*?:\s*\+?\$?([\d,.]+)') { $peak = $Matches[1] }
    
    # Extract per-phase P/L from no-target run
    $lines2 = $out2 -split "`n"
    $lastOV = ""; $lastBase = ""; $seenPH = $false
    foreach ($l in $lines2) {
        $t = $l.Trim()
        if ($t -match '\[Open Volatility\].*Day:\s*([+-])\$?([\d,.]+)') {
            $v = [double]($Matches[2] -replace ",",""); if ($Matches[1] -eq '-') { $v = -$v }
            $lastOV = $v
        }
        if ($t -match 'PHASE TRANSITION.*Power Hour') { $seenPH = $true }
        if (-not $seenPH -and $t -match '^\[\d{2}:\d{2}:\d{2}\]' -and $t -notmatch '\[Open Volatility\]' -and $t -match 'Day:\s*([+-])\$?([\d,.]+)') {
            $v = [double]($Matches[2] -replace ",",""); if ($Matches[1] -eq '-') { $v = -$v }
            $lastBase = $v
        }
    }
    $baseDelta = if ($lastBase) { [math]::Round($lastBase - $lastOV, 2) } else { 0 }
    $phDelta = [math]::Round($pl2 - $lastBase, 2)
    
    Write-Host "Feb $dayLabel`:"
    Write-Host "  Target ON:  `$$([math]::Round($pl1,2)) ($tr1 trades)"
    Write-Host "  Target OFF: `$$([math]::Round($pl2,2)) ($tr2 trades)"
    $baseSign = if ($baseDelta -ge 0) { "+" } else { "" }
    $phSign = if ($phDelta -ge 0) { "+" } else { "" }
    $diffVal = [math]::Round($pl2 - $pl1, 2)
    $diffSign = if ($diffVal -ge 0) { "+" } else { "" }
    Write-Host "    No-target breakdown: OV=`$$lastOV  Base=${baseSign}`$$baseDelta  PH=${phSign}`$$phDelta"
    Write-Host "  Difference: ${diffSign}`$$diffVal"
    Write-Host ""
}
