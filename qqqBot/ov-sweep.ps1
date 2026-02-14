# ov-sweep.ps1 — Open Volatility phase optimization
Set-Location "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$sweepDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot\sweep_configs"

function Make-BaseConfig {
    # Start from combined winner for base phase  
    $c = Get-Content "c:\dev\TradeEcosystem\qqqBot\qqqBot\appsettings.json" -Raw | ConvertFrom-Json
    # Apply base phase winners (these won't affect OV since OV has overrides)
    $c.TradingBot.MinVelocityThreshold = 0.000015
    $c.TradingBot.TrendWindowSeconds = 5400
    $c.TradingBot.TrailingStopPercent = 0.002
    $c.TradingBot.ExitStrategy.TrendWaitSeconds = 180
    $c.TradingBot.TrimRatio = 0.75
    return $c
}

function Run-OV {
    param([string]$ConfigPath, [string]$Label)
    $total = 0; $trades = 0; $days = @()
    foreach ($d in @("20260209","20260210","20260211","20260212","20260213")) {
        $out = dotnet run -- --mode=replay --date=$d --speed=0 --start-time=09:30 --end-time=09:50 "-config=$ConfigPath" 2>&1 | ForEach-Object {"$_"}
        $p = 0; $t = 0
        foreach ($l in $out) {
            if ($l -match 'Realized P/L:\s+\$?([-\d,.]+)') { $p = [decimal]($matches[1] -replace ',','') }
            if ($l -match 'Total Trades:\s+(\d+)') { $t = [int]$matches[1] }
        }
        $total += $p; $trades += $t
        $days += "$($d.Substring(6)):$([math]::Round($p,0))"
    }
    $color = if ($total -gt 50) {"Green"} elseif ($total -gt 0) {"Yellow"} else {"Red"}
    Write-Host ("{0,-35} | {1,10} | {2,5} | {3}" -f $Label, "`$$([math]::Round($total,2))", $trades, ($days -join " | ")) -ForegroundColor $color
}

Write-Host "`n=== OV PHASE SIGNAL SWEEP ===" -ForegroundColor Cyan
Write-Host "OV baseline: +`$75.91 (14 trades)" -ForegroundColor DarkGray
Write-Host ("{0,-35} | {1,10} | {2,5} | {3}" -f "Config", "Total P/L", "Tr", "Per-day") -ForegroundColor Yellow
Write-Host ("-" * 100) -ForegroundColor Yellow

# Current OV settings baseline
$c = Make-BaseConfig; $p = "$sweepDir\ov_baseline.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-OV $p "CURRENT_OV"

# OV MinVelocityThreshold sweep (override level)
foreach ($v in @(0.000015, 0.000020, 0.000025, 0.000030, 0.000040, 0.000050, 0.000075, 0.0001)) {
    $c = Make-BaseConfig
    $ov = $c.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
    $ov.Overrides | Add-Member -MemberType NoteProperty -Name "MinVelocityThreshold" -Value $v -Force
    $p = "$sweepDir\ov_vel_$v.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-OV $p "OV_Vel=$v"
}

# OV TrailingStopPercent sweep
foreach ($ts in @(0.002, 0.003, 0.004, 0.005, 0.006, 0.008)) {
    $c = Make-BaseConfig
    $ov = $c.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
    $ov.Overrides | Add-Member -MemberType NoteProperty -Name "TrailingStopPercent" -Value $ts -Force
    $p = "$sweepDir\ov_trail_$ts.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-OV $p "OV_Trail=$($ts*100)%"
}

# OV SMAWindowSeconds sweep
foreach ($sma in @(60, 90, 120, 150, 180)) {
    $c = Make-BaseConfig
    $ov = $c.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
    $ov.Overrides | Add-Member -MemberType NoteProperty -Name "SMAWindowSeconds" -Value $sma -Force
    $p = "$sweepDir\ov_sma_$sma.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-OV $p "OV_SMA=$($sma)s"
}

# OV BullOnlyMode
$c = Make-BaseConfig
$ov = $c.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
$ov.Overrides | Add-Member -MemberType NoteProperty -Name "BullOnlyMode" -Value $true -Force
$p = "$sweepDir\ov_bullonly.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-OV $p "OV_BullOnly=true"

# OV ChopThresholdPercent sweep
foreach ($chop in @(0.001, 0.0015, 0.002, 0.0025, 0.003)) {
    $c = Make-BaseConfig
    $ov = $c.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
    $ov.Overrides | Add-Member -MemberType NoteProperty -Name "ChopThresholdPercent" -Value $chop -Force
    $p = "$sweepDir\ov_chop_$chop.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-OV $p "OV_Chop=$chop"
}

# Disable OV entirely (set velocity impossibly high)
$c = Make-BaseConfig
$ov = $c.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
$ov.Overrides | Add-Member -MemberType NoteProperty -Name "MinVelocityThreshold" -Value 999.0 -Force
$p = "$sweepDir\ov_disabled.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-OV $p "OV_DISABLED"

Write-Host "`n=== OV Sweep Complete ===" -ForegroundColor Cyan
