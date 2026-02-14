# stops-exit-sweep.ps1 — Sweep stops and exit params using winning signal config as base
Set-Location "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$sweepDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot\sweep_configs"

function Make-WinnerConfig {
    $c = Get-Content "c:\dev\TradeEcosystem\qqqBot\qqqBot\appsettings.json" -Raw | ConvertFrom-Json
    $c.TradingBot.MinVelocityThreshold = 0.000015
    $c.TradingBot.TrendWindowSeconds = 5400
    return $c
}

function Run-Config {
    param([string]$ConfigPath, [string]$Label)
    $total = 0; $trades = 0; $days = @()
    foreach ($d in @("20260209","20260210","20260211","20260212","20260213")) {
        $out = dotnet run -- --mode=replay --date=$d --speed=0 --start-time=09:50 --end-time=14:00 "-config=$ConfigPath" 2>&1 | ForEach-Object {"$_"}
        $p = 0; $t = 0
        foreach ($l in $out) {
            if ($l -match 'Realized P/L:\s+\$?([-\d,.]+)') { $p = [decimal]($matches[1] -replace ',','') }
            if ($l -match 'Total Trades:\s+(\d+)') { $t = [int]$matches[1] }
        }
        $total += $p; $trades += $t
        $days += "$($d.Substring(6)):$([math]::Round($p,0))"
    }
    $color = if ($total -gt 80) {"Green"} elseif ($total -gt 0) {"Yellow"} else {"Red"}
    Write-Host ("{0,-30} | {1,10} | {2,5} | {3}" -f $Label, "`$$([math]::Round($total,2))", $trades, ($days -join " | ")) -ForegroundColor $color
}

Write-Host "`n=== STOPS SWEEP (on top of Vel15+T5400 winner) ===" -ForegroundColor Cyan
Write-Host ("{0,-30} | {1,10} | {2,5} | {3}" -f "Config", "Total P/L", "Tr", "Per-day") -ForegroundColor Yellow
Write-Host ("-" * 100) -ForegroundColor Yellow

# Baseline winner
$c = Make-WinnerConfig; $p = "$sweepDir\stops_baseline.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "WINNER_BASELINE"

# TrailingStopPercent sweep
foreach ($ts in @(0.0015, 0.002, 0.0025, 0.003, 0.0035, 0.004, 0.005)) {
    $c = Make-WinnerConfig; $c.TradingBot.TrailingStopPercent = $ts
    $p = "$sweepDir\stops_trail_$ts.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-Config $p "TrailStop=$($ts*100)%"
}

# DynamicStopLoss off
$c = Make-WinnerConfig; $c.TradingBot.DynamicStopLoss.Enabled = $false
$p = "$sweepDir\stops_dsl_off.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "DSL=OFF"

# Wider DSL tiers
$c = Make-WinnerConfig
$c.TradingBot.DynamicStopLoss.Tiers = @(
    @{TriggerProfitPercent=0.005; StopPercent=0.002},
    @{TriggerProfitPercent=0.008; StopPercent=0.0015},
    @{TriggerProfitPercent=0.012; StopPercent=0.001}
)
$p = "$sweepDir\stops_dsl_wide.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "DSL=wide"

# Tighter DSL tiers
$c = Make-WinnerConfig
$c.TradingBot.DynamicStopLoss.Tiers = @(
    @{TriggerProfitPercent=0.002; StopPercent=0.001},
    @{TriggerProfitPercent=0.004; StopPercent=0.0007},
    @{TriggerProfitPercent=0.006; StopPercent=0.0005}
)
$p = "$sweepDir\stops_dsl_tight.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "DSL=tight"

Write-Host "`n=== EXIT STRATEGY SWEEP ===" -ForegroundColor Cyan
Write-Host ("{0,-30} | {1,10} | {2,5} | {3}" -f "Config", "Total P/L", "Tr", "Per-day") -ForegroundColor Yellow
Write-Host ("-" * 100) -ForegroundColor Yellow

# ScalpWaitSeconds sweep
foreach ($sw in @(15, 20, 30, 45, 60, 90, -1)) {
    $c = Make-WinnerConfig; $c.TradingBot.ExitStrategy.ScalpWaitSeconds = $sw
    $p = "$sweepDir\exit_scalp_$sw.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    $label = if ($sw -eq -1) { "ScalpWait=OFF" } else { "ScalpWait=$($sw)s" }
    Run-Config $p $label
}

# TrendWaitSeconds sweep
foreach ($tw in @(30, 60, 90, 120, 180, 240, -1)) {
    $c = Make-WinnerConfig; $c.TradingBot.ExitStrategy.TrendWaitSeconds = $tw
    $p = "$sweepDir\exit_trend_$tw.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    $label = if ($tw -eq -1) { "TrendWait=OFF" } else { "TrendWait=$($tw)s" }
    Run-Config $p $label
}

# TrendConfidenceThreshold sweep
foreach ($tc in @(0.00004, 0.00006, 0.00008, 0.0001, 0.00012, 0.00015)) {
    $c = Make-WinnerConfig; $c.TradingBot.ExitStrategy.TrendConfidenceThreshold = $tc
    $p = "$sweepDir\exit_tconf_$tc.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-Config $p "TrendConf=$tc"
}

# HoldNeutralIfUnderwater toggle
foreach ($hold in @($true, $false)) {
    $c = Make-WinnerConfig; $c.TradingBot.ExitStrategy.HoldNeutralIfUnderwater = $hold
    $p = "$sweepDir\exit_hold_$hold.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-Config $p "HoldUnderwater=$hold"
}

Write-Host "`n=== TRIMMING SWEEP ===" -ForegroundColor Cyan
Write-Host ("{0,-30} | {1,10} | {2,5} | {3}" -f "Config", "Total P/L", "Tr", "Per-day") -ForegroundColor Yellow
Write-Host ("-" * 100) -ForegroundColor Yellow

# Trimming on/off
$c = Make-WinnerConfig; $c.TradingBot.EnableTrimming = $false
$p = "$sweepDir\trim_off.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "Trim=OFF"

$c = Make-WinnerConfig; $c.TradingBot.EnableTrimming = $true
$p = "$sweepDir\trim_on.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "Trim=ON(current)"

# TrimRatio sweep (with trimming on)
foreach ($tr in @(0.25, 0.33, 0.50, 0.67, 0.75)) {
    $c = Make-WinnerConfig; $c.TradingBot.TrimRatio = $tr
    $p = "$sweepDir\trim_ratio_$tr.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
    Run-Config $p "TrimRatio=$($tr*100)%"
}

Write-Host "`n=== DIRECTION MODE ===" -ForegroundColor Cyan
Write-Host ("{0,-30} | {1,10} | {2,5} | {3}" -f "Config", "Total P/L", "Tr", "Per-day") -ForegroundColor Yellow
Write-Host ("-" * 100) -ForegroundColor Yellow

# BullOnlyMode for base phase
$c = Make-WinnerConfig
$c.TradingBot | Add-Member -MemberType NoteProperty -Name "BullOnlyMode" -Value $true -Force
$p = "$sweepDir\dir_bullonly.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "BullOnly=true"

$c = Make-WinnerConfig
$p = "$sweepDir\dir_both.json"; $c | ConvertTo-Json -Depth 10 | Set-Content $p
Run-Config $p "Both(current)"

Write-Host "`n=== All Sweeps Complete ===" -ForegroundColor Cyan
