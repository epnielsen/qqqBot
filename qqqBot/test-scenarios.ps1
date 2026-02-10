# test-scenarios.ps1 — Systematic feature testing for profitability enhancements
# Tests BearEntryConfirmationTicks, DailyProfitTarget, and BullOnlyMode
# in isolation and combination against the Feb 9, 2026 replay.

Set-Location "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$sf = "appsettings.json"
$orig = Get-Content $sf -Raw

Write-Host ""
Write-Host "=== Profitability Enhancement Feature Testing ===" -ForegroundColor Cyan
Write-Host "  Date: 2026-02-09 | Replay: deterministic (Brownian bridge)" -ForegroundColor DarkGray
Write-Host ""

# ─── Create baseline: strip all 3 new features ───
$base = $orig
$base = $base -replace '"DailyProfitTarget": 130', '"DailyProfitTarget": 0'
$base = $base -replace '"BullOnlyMode": true,\r?\n\s*"MinVelocityThreshold": 0\.000010', '"MinVelocityThreshold": 0.000010'
$base = $base -replace '"ChopThresholdPercent": 0\.002,\r?\n\s*"BearEntryConfirmationTicks": \d+', '"ChopThresholdPercent": 0.002'

# Verify baseline was created correctly
$ok = $true
if ($base -notmatch '"DailyProfitTarget": 0') { Write-Host "  WARN: DPT not zeroed" -ForegroundColor Red; $ok = $false }
if ($base -match 'BullOnlyMode')              { Write-Host "  WARN: BullOnlyMode still present" -ForegroundColor Red; $ok = $false }
if ($base -match 'BearEntryConfirmationTicks') { Write-Host "  WARN: BearConfirm still present" -ForegroundColor Red; $ok = $false }
if ($ok) { Write-Host "  Baseline verified: DPT=0, no BullOnly, no BearConfirm" -ForegroundColor DarkGray }
Write-Host ""

# ─── Feature application functions ───
function Add-DPT([string]$c, [int]$v) {
    return $c -replace '"DailyProfitTarget": 0', ('"DailyProfitTarget": ' + $v)
}

function Add-BullOnly([string]$c) {
    return $c -replace '"MinVelocityThreshold": 0\.000010', ("`"BullOnlyMode`": true,`r`n          `"MinVelocityThreshold`": 0.000010")
}

function Add-BearConfirm([string]$c, [int]$v) {
    return $c -replace '"ChopThresholdPercent": 0\.002\r?\n', ("`"ChopThresholdPercent`": 0.002,`r`n          `"BearEntryConfirmationTicks`": $v`r`n")
}

# ─── Run a single scenario ───
function Run-Scenario([string]$name, [string]$content) {
    Set-Content $sf $content -NoNewline
    
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $out = dotnet run -- --mode=replay --date=20260209 --speed=0 2>&1 | ForEach-Object { "$_" }
    $sw.Stop()
    
    $ret = "ERR"; $tr = "?"; $pnl = "?"
    foreach ($l in $out) {
        if ($l -match 'Net Return:\s+([-\d.]+)\s*%')    { $ret = $matches[1] }
        if ($l -match 'Total Trades:\s+(\d+)')           { $tr  = $matches[1] }
        if ($l -match 'Realized P/L:\s+\$?([-\d,.]+)')  { $pnl = $matches[1] }
    }
    
    $sec = [int]$sw.Elapsed.TotalSeconds
    $color = if ([double]$ret -gt 0.89) { "Green" } elseif ([double]$ret -gt 0) { "Yellow" } else { "Red" }
    Write-Host ("  {0,-36} | {1,8} | {2,6} | {3,10} | {4,3}s" -f $name, "$ret%", $tr, "`$$pnl", $sec) -ForegroundColor $color
}

# ─── Header ───
Write-Host ("  {0,-36} | {1,8} | {2,6} | {3,10} | {4,4}" -f "Scenario", "Return", "Trades", "P/L", "Time") -ForegroundColor Yellow
Write-Host ("  " + ("-" * 74)) -ForegroundColor Yellow

# ─── Individual feature tests ───
Run-Scenario "1. Baseline"                    $base
Run-Scenario "2. BullOnly OpenVol"            (Add-BullOnly $base)
Run-Scenario "3. DailyTarget=90"              (Add-DPT $base 90)
Run-Scenario "4. DailyTarget=95"              (Add-DPT $base 95)
Run-Scenario "5. DailyTarget=100"             (Add-DPT $base 100)
Run-Scenario "6. DailyTarget=110"             (Add-DPT $base 110)

# ─── BullOnly + DPT dial ───
$bo = Add-BullOnly $base
Run-Scenario "7. BullOnly + DPT=90"           (Add-DPT $bo 90)
Run-Scenario "8. BullOnly + DPT=95"           (Add-DPT $bo 95)
Run-Scenario "9. BullOnly + DPT=100"          (Add-DPT $bo 100)
Run-Scenario "10. BullOnly + DPT=110"         (Add-DPT $bo 110)

# ─── Restore original ───
Set-Content $sf $orig -NoNewline
Write-Host ""
Write-Host "  appsettings.json restored to original." -ForegroundColor Green
Write-Host "=== Testing Complete ===" -ForegroundColor Cyan
