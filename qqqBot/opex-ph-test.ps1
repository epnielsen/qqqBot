# opex-ph-test.ps1
# Compare Power Hour behavior on two Fridays (Feb 6 low-res, Feb 13 high-res)
# Tests: (A) Isolated PH with current PH settings, (B) Isolated PH with Base settings,
#        (C) Full PH-resume strategy (morning w/target → if fired, resume PH w/Base, no target)
#
# Hypothesis: Weekly OpEx pinning creates exploitable PH trends.
# Feb 6 uses Brownian bridge interpolation (60s bar data). Feb 13 uses raw tick data.

$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$logDir = "C:\dev\TradeEcosystem\logs\qqqbot"

# Two Fridays to compare
$dates = @("20260206", "20260213")

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

# --- Config builders ---

# PH with Base settings, daily target OFF (for isolated PH and for PH-resume step 2)
function Build-PHBaseConfig {
    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    $config.TradingBot.DailyProfitTargetPercent = 0
    # Override PH TimeRule to use Base settings
    $config.TradingBot.TimeRules[1].Overrides = [PSCustomObject]@{
        MinVelocityThreshold = 0.000015
        SMAWindowSeconds = 180
        SlopeWindowSize = 20
        ChopThresholdPercent = 0.0011
        TrendWindowSeconds = 5400
        TrendWaitSeconds = 180
        TrendConfidenceThreshold = 0.00008
        TrailingStopPercent = 0.002
        EnableTrimming = $true
        TrimRatio = 0.75
        TrimTriggerPercent = 0.0025
        TrimSlopeThreshold = 0.000001
        TrimCooldownSeconds = 300
    }
    $configPath = "$configDir\opex_ph_base.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath
    return $configPath
}

# PH with current PH settings, daily target OFF (for isolated PH segment test)
function Build-PHCurrentConfig {
    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    $config.TradingBot.DailyProfitTargetPercent = 0
    # Keep PH TimeRule overrides as-is (from appsettings.json)
    $configPath = "$configDir\opex_ph_current.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath
    return $configPath
}

# --- Output parsers ---

function Parse-Output {
    param([string]$Output)
    $pl = 0.0; $trades = 0; $peakPL = "N/A"; $troughPL = "N/A"
    if ($Output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    if ($Output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
    if ($Output -match 'Peak P/L:\s*\$?([-\d,.]+)') { $peakPL = $Matches[1] -replace ",","" }
    if ($Output -match 'Trough P/L:\s*\$?([-\d,.]+)') { $troughPL = $Matches[1] -replace ",","" }
    return @{ PL = $pl; Trades = $trades; Peak = $peakPL; Trough = $troughPL }
}

function Check-TargetFired {
    param([string]$Date)
    $log = Get-ChildItem "$logDir\qqqbot_replay_${Date}_*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return @{ Fired = $false; Time = "N/A" } }
    
    $hit = Select-String -Path $log.FullName -Pattern "HALT|DAILY PROFIT TARGET|daily.*target.*trail|target.*armed|STOPPED.*profit" | Select-Object -First 1
    $fired = $false; $time = "N/A"
    if ($hit) {
        $fired = $true
        if ($hit.Line -match '(\d{2}:\d{2}:\d{2})') { $time = $matches[1] }
    }
    return @{ Fired = $fired; Time = $time }
}

function Get-DataResolution {
    param([string]$Date)
    $qqqFile = "C:\dev\TradeEcosystem\data\market\${Date}_market_data_QQQ.csv"
    if (-not (Test-Path $qqqFile)) { return "MISSING" }
    $lines = (Get-Content $qqqFile).Count - 1  # subtract header
    if ($lines -lt 500) { return "LOW-RES (~${lines} bars, Brownian bridge)" }
    return "HIGH-RES (~${lines} ticks, raw)"
}

# --- Build configs ---
$phBaseConfig = Build-PHBaseConfig
$phCurrentConfig = Build-PHCurrentConfig

Write-Host "=" * 90
Write-Host "OPEX FRIDAY PH INVESTIGATION"
Write-Host "Hypothesis: Weekly OpEx pinning creates exploitable PH trends"
Write-Host "Comparing: Feb 6 (Friday, low-res) vs Feb 13 (Friday, high-res)"
Write-Host "=" * 90
Write-Host ""

$allResults = @()

foreach ($date in $dates) {
    $resolution = Get-DataResolution $date
    $dayOfWeek = [datetime]::ParseExact($date, "yyyyMMdd", $null).DayOfWeek
    
    Write-Host ""
    Write-Host ("=" * 80)
    Write-Host "DATE: $date ($dayOfWeek) -- $resolution"
    Write-Host ("=" * 80)
    
    # --- Test A: Isolated PH segment with CURRENT PH settings ---
    Write-Host ""
    Write-Host "  [A] Isolated PH (14:00-16:00) with CURRENT PH settings..."
    $aOutput = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=14:00 --end-time=16:00 "-config=$phCurrentConfig" 2>&1 | Out-String
    $aResult = Parse-Output $aOutput
    Write-Host "      P/L: `$$($aResult.PL)  Trades: $($aResult.Trades)  Peak: `$$($aResult.Peak)  Trough: `$$($aResult.Trough)"
    
    # --- Test B: Isolated PH segment with BASE settings ---
    Write-Host "  [B] Isolated PH (14:00-16:00) with BASE settings..."
    $bOutput = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=14:00 --end-time=16:00 "-config=$phBaseConfig" 2>&1 | Out-String
    $bResult = Parse-Output $bOutput
    Write-Host "      P/L: `$$($bResult.PL)  Trades: $($bResult.Trades)  Peak: `$$($bResult.Peak)  Trough: `$$($bResult.Trough)"
    
    # --- Test C: Full PH-resume strategy ---
    Write-Host "  [C] Full-day replay (target ON)..."
    $cFullOutput = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $cFullResult = Parse-Output $cFullOutput
    $targetInfo = Check-TargetFired $date
    Write-Host "      Full-day P/L: `$$($cFullResult.PL)  Trades: $($cFullResult.Trades)  Target: $($targetInfo.Fired) ($($targetInfo.Time))"
    
    $cPhPL = 0.0; $cPhTrades = 0
    if ($targetInfo.Fired) {
        Write-Host "  [C+] PH-Resume: Isolated PH (14:00-16:00, BASE settings, NO target)..."
        $cPhOutput = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=14:00 --end-time=16:00 "-config=$phBaseConfig" 2>&1 | Out-String
        $cPhResult = Parse-Output $cPhOutput
        $cPhPL = $cPhResult.PL
        $cPhTrades = $cPhResult.Trades
        Write-Host "      PH-Resume P/L: `$$cPhPL  Trades: $cPhTrades"
    } else {
        Write-Host "      Target didn't fire -- no PH resume (PH already in full-day run)"
    }
    
    $cCombined = [math]::Round($cFullResult.PL + $cPhPL, 2)
    $cDelta = [math]::Round($cPhPL, 2)
    Write-Host ""
    Write-Host "      COMBINED: `$$cCombined  (full-day `$$($cFullResult.PL) + PH-resume `$$cPhPL)"
    
    $allResults += [PSCustomObject]@{
        Date = $date
        DayOfWeek = $dayOfWeek
        Resolution = $resolution
        # Test A: PH with current settings
        PH_Current_PL = $aResult.PL
        PH_Current_Trades = $aResult.Trades
        PH_Current_Peak = $aResult.Peak
        PH_Current_Trough = $aResult.Trough
        # Test B: PH with base settings
        PH_Base_PL = $bResult.PL
        PH_Base_Trades = $bResult.Trades
        PH_Base_Peak = $bResult.Peak
        PH_Base_Trough = $bResult.Trough
        # Test C: Full day + PH-resume
        FullDay_PL = $cFullResult.PL
        FullDay_Trades = $cFullResult.Trades
        TargetFired = $targetInfo.Fired
        TargetTime = $targetInfo.Time
        PHResume_PL = $cPhPL
        PHResume_Trades = $cPhTrades
        Combined_PL = $cCombined
        Resume_Delta = $cDelta
    }
}

# --- Determinism check: run Feb 6 PH Base one more time to confirm bridge is deterministic ---
Write-Host ""
Write-Host ("=" * 80)
Write-Host "DETERMINISM CHECK: Feb 6 PH (Base settings) -- 2nd run"
Write-Host ("=" * 80)
$detOutput = & dotnet run --project $projectDir -- --mode=replay --date=20260206 --speed=0 --start-time=14:00 --end-time=16:00 "-config=$phBaseConfig" 2>&1 | Out-String
$detResult = Parse-Output $detOutput
$origFeb6 = ($allResults | Where-Object { $_.Date -eq "20260206" }).PH_Base_PL
Write-Host "  Run 1 P/L: `$$origFeb6   Run 2 P/L: `$$($detResult.PL)"
if ($origFeb6 -eq $detResult.PL) {
    Write-Host "  [OK] DETERMINISTIC -- Brownian bridge produces identical results"
} else {
    Write-Host "  [FAIL] NON-DETERMINISTIC -- Brownian bridge results differ! ($origFeb6 vs $($detResult.PL))"
}

# --- Summary ---
Write-Host ""
Write-Host ("=" * 90)
Write-Host "SUMMARY: OPEX FRIDAY PH COMPARISON"
Write-Host ("=" * 90)
Write-Host ""

Write-Host "Test A -- Isolated PH (14:00-16:00) with CURRENT PH settings (OV-lite):"
Write-Host ("{0,-12} {1,12} {2,10} {3,8} {4,12} {5,12}" -f "Date", "Resolution", "P/L", "Trades", "Peak", "Trough")
Write-Host ("-" * 70)
foreach ($r in $allResults) {
    $res = if ($r.Resolution -match "LOW") { "LOW-RES" } else { "HIGH-RES" }
    $plStr = "{0:N2}" -f $r.PH_Current_PL
    Write-Host ("{0,-12} {1,12} {2,10} {3,8} {4,12} {5,12}" -f $r.Date, $res, $plStr, $r.PH_Current_Trades, $r.PH_Current_Peak, $r.PH_Current_Trough)
}

Write-Host ""
Write-Host "Test B -- Isolated PH (14:00-16:00) with BASE settings:"
Write-Host ("{0,-12} {1,12} {2,10} {3,8} {4,12} {5,12}" -f "Date", "Resolution", "P/L", "Trades", "Peak", "Trough")
Write-Host ("-" * 70)
foreach ($r in $allResults) {
    $res = if ($r.Resolution -match "LOW") { "LOW-RES" } else { "HIGH-RES" }
    $plStr = "{0:N2}" -f $r.PH_Base_PL
    Write-Host ("{0,-12} {1,12} {2,10} {3,8} {4,12} {5,12}" -f $r.Date, $res, $plStr, $r.PH_Base_Trades, $r.PH_Base_Peak, $r.PH_Base_Trough)
}

Write-Host ""
Write-Host "Test C -- PH-Resume strategy (morning w/target -> resume PH w/Base, no target):"
Write-Host ("{0,-12} {1,12} {2,10} {3,8} {4,10} {5,10} {6,8} {7,10} {8,8}" -f "Date", "Resolution", "FullDay", "FDTrd", "Target?", "PH P/L", "PHTrd", "Combined", "Delta")
Write-Host ("-" * 95)
foreach ($r in $allResults) {
    $res = if ($r.Resolution -match "LOW") { "LOW-RES" } else { "HIGH-RES" }
    $tgt = if ($r.TargetFired) { "YES $($r.TargetTime)" } else { "no" }
    $fdStr = "{0:N2}" -f $r.FullDay_PL
    $phStr = "{0:N2}" -f $r.PHResume_PL
    $combStr = "{0:N2}" -f $r.Combined_PL
    $deltaStr = "{0:N2}" -f $r.Resume_Delta
    Write-Host ("{0,-12} {1,12} {2,10} {3,8} {4,10} {5,10} {6,8} {7,10} {8,8}" -f $r.Date, $res, $fdStr, $r.FullDay_Trades, $tgt, $phStr, $r.PHResume_Trades, $combStr, $deltaStr)
}

Write-Host ""
Write-Host ("=" * 90)
Write-Host "KEY QUESTIONS:"
Write-Host "  1. Does Feb 6 (Friday, low-res) show similar PH trending behavior to Feb 13?"
Write-Host "  2. Does PH with Base settings outperform current PH settings on both Fridays?"
Write-Host "  3. Does the PH-resume strategy add value on Feb 6 as well as Feb 13?"
Write-Host "  4. Is the Brownian bridge deterministic and producing realistic trade patterns?"
Write-Host "  NOTE: Feb 6 uses Brownian bridge (60s bars -> ~1 tick/sec synthetic data)"
Write-Host "        Feb 13 uses raw recorded tick data (sub-second resolution)"
Write-Host ("=" * 90)
