# ph-resume-test.ps1
# Test: When daily target fires, pause until Power Hour, then resume with BASE settings (no target)

$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$configDir = "$projectDir\sweep_configs"
$dates = @("20260209", "20260210", "20260211", "20260212", "20260213")
$logDir = "C:\dev\TradeEcosystem\logs\qqqbot"

if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }

# Build a PH-only config: base settings everywhere (no PH overrides), daily target OFF
function Build-PHBaseConfig {
    $config = Get-Content "$projectDir\appsettings.json" -Raw | ConvertFrom-Json
    # Disable daily target for PH portion
    $config.TradingBot.DailyProfitTargetPercent = 0
    # Set PH overrides to BASE settings so PH uses base params
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
    $configPath = "$configDir\ph_resume_base.json"
    $config | ConvertTo-Json -Depth 10 | Set-Content $configPath
    return $configPath
}

function Parse-Output {
    param([string]$Output)
    $pl = 0.0; $trades = 0
    if ($Output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $pl = [double]($Matches[1] -replace ",","") }
    if ($Output -match 'Total Trades:\s*(\d+)') { $trades = [int]$Matches[1] }
    return @{ PL = $pl; Trades = $trades }
}

function Check-TargetFired {
    param([string]$Date)
    # Find the most recent log for this date
    $log = Get-ChildItem "$logDir\qqqbot_replay_${Date}_*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return @{ Fired = $false; Time = "N/A" } }
    
    $hit = Select-String -Path $log.FullName -Pattern "HALT|DAILY PROFIT TARGET|daily.*target.*trail|target.*armed|STOPPED.*profit" | Select-Object -First 1
    $fired = $false; $time = "N/A"
    if ($hit) {
        $fired = $true
        if ($hit.Line -match '(\d{2}:\d{2}:\d{2})') {
            $time = $matches[1]
        }
    }
    return @{ Fired = $fired; Time = $time }
}

# Build the PH config once
$phConfigPath = Build-PHBaseConfig

Write-Host "=" * 80
Write-Host "PH RESUME TEST: Stop on target, resume in Power Hour with BASE settings"
Write-Host "=" * 80
Write-Host ""

$totalCurrent = 0.0
$totalResume = 0.0
$results = @()

foreach ($date in $dates) {
    Write-Host "-" * 60
    Write-Host "DATE: $date"
    Write-Host "-" * 60
    
    # --- Step 1: Full day with current settings (target ON) ---
    Write-Host "  [1/2] Full day replay (current settings, target ON)..."
    $fullOutput = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $fullResult = Parse-Output $fullOutput
    
    # Check if target fired from the log
    $targetInfo = Check-TargetFired $date
    
    Write-Host "    P/L: `$$($fullResult.PL) ($($fullResult.Trades) trades)"
    Write-Host "    Target fired: $($targetInfo.Fired) (time: $($targetInfo.Time))"
    
    # --- Step 2: If target fired, run isolated PH with base settings, no target ---
    $phPL = 0.0; $phTrades = 0
    
    if ($targetInfo.Fired) {
        Write-Host "  [2/2] Isolated PH (14:00-16:00, BASE settings, NO target)..."
        $phOutput = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 --start-time=14:00 --end-time=16:00 "-config=$phConfigPath" 2>&1 | Out-String
        $phResult = Parse-Output $phOutput
        $phPL = $phResult.PL
        $phTrades = $phResult.Trades
        Write-Host "    PH P/L: `$$phPL ($phTrades trades)"
    } else {
        Write-Host "  [2/2] Target didn't fire - PH already in full day run (no additional PH)"
    }
    
    $combinedPL = [math]::Round($fullResult.PL + $phPL, 2)
    $delta = [math]::Round($phPL, 2)
    
    Write-Host ""
    Write-Host "    CURRENT:  `$$($fullResult.PL)"
    if ($targetInfo.Fired) {
        Write-Host "    + PH:     `$$phPL ($phTrades trades)"
    }
    Write-Host "    COMBINED: `$$combinedPL  (delta: `$$delta)"
    
    $totalCurrent += $fullResult.PL
    $totalResume += $combinedPL
    
    $results += [PSCustomObject]@{
        Date = $date
        Current = $fullResult.PL
        CurTrades = $fullResult.Trades
        TargetFired = $targetInfo.Fired
        TargetTime = $targetInfo.Time
        PHPL = $phPL
        PHTrades = $phTrades
        Combined = $combinedPL
        Delta = $delta
    }
}

Write-Host ""
Write-Host "=" * 80
Write-Host "SUMMARY"
Write-Host "=" * 80
Write-Host ""
Write-Host ("{0,-10} {1,10} {2,5} {3,8} {4,10} {5,10} {6,5} {7,10} {8,8}" -f "Date", "Current", "Trd", "Target?", "FireTime", "PH P/L", "PHTr", "Combined", "Delta")
Write-Host ("-" * 90)

foreach ($r in $results) {
    $tgt = if ($r.TargetFired) { "YES" } else { "no" }
    Write-Host ("{0,-10} {1,10:N2} {2,5} {3,8} {4,10} {5,10:N2} {6,5} {7,10:N2} {8,8:N2}" -f $r.Date, $r.Current, $r.CurTrades, $tgt, $r.TargetTime, $r.PHPL, $r.PHTrades, $r.Combined, $r.Delta)
}

Write-Host ""
$totalCurrent = [math]::Round($totalCurrent, 2)
$totalResume = [math]::Round($totalResume, 2)
$totalDelta = [math]::Round($totalResume - $totalCurrent, 2)

Write-Host ("TOTAL CURRENT:   `${0:N2}  (stop on target, done for day)" -f $totalCurrent)
Write-Host ("TOTAL RESUME:    `${0:N2}  (stop on target, resume PH with base)" -f $totalResume)
if ($totalCurrent -ne 0) {
    Write-Host ("TOTAL DELTA:     `${0:N2}  ({1:N1}%)" -f $totalDelta, ($totalDelta / [math]::Abs($totalCurrent) * 100))
} else {
    Write-Host ("TOTAL DELTA:     `${0:N2}" -f $totalDelta)
}
Write-Host ""
Write-Host "Strategy: Morning session with daily target -> if target fires, pause -> resume at 14:00"
Write-Host "PH config: BASE settings (SMA=180, Chop=0.0011, Trend=5400, Trail=0.002, Trim=0.75)"
Write-Host "PH has NO daily target (fresh start, can trade freely)"
