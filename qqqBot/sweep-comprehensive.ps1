# sweep-comprehensive.ps1 — Full cascading parameter sweep for the new realistic SimulatedBroker
#
# 6-round cascading sweep: each round bakes the previous winner into the baseline.
# Runs on 9-day primary dataset (Feb 9-13, 17-20), validates on Feb 6 out-of-sample.
#
# Usage:
#   .\sweep-comprehensive.ps1                       # Run all rounds sequentially
#   .\sweep-comprehensive.ps1 -Round 1              # Run only Round 1 (Signal)
#   .\sweep-comprehensive.ps1 -Round 2              # Run only Round 2 (Stops)
#   .\sweep-comprehensive.ps1 -Round 1 -Quick       # Fast iteration: 3 dates only
#   .\sweep-comprehensive.ps1 -BaselineOnly          # Just run the 10-day baseline
#   .\sweep-comprehensive.ps1 -DiagnoseDate 20260220 # Segment-by-segment diagnosis

param(
    [int]$Round = 0,              # 0 = all rounds, 1-6 = specific round
    [switch]$Quick,               # Use 3 dates for fast iteration
    [switch]$BaselineOnly,        # Only run baseline, no sweeps
    [string]$DiagnoseDate = "",   # Run phase-by-phase diagnosis for a specific date
    [string]$WinnerConfig = ""    # Path to a winner config to use as the base (for resuming)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ─── Configuration ───
$ProjectDir  = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$BaseConfig  = Join-Path $ProjectDir "appsettings.json"
$SweepDir    = Join-Path $ProjectDir "sweep_configs"
$ResultsDir  = Join-Path $ProjectDir "sweep_results"

# 9-day primary dataset (consistent recorded tick data)
$PrimaryDates = @("20260209", "20260210", "20260211", "20260212", "20260213",
                   "20260217", "20260218", "20260219", "20260220")
$QuickDates   = @("20260211", "20260212", "20260220")
$OosDates     = @("20260206")  # Out-of-sample: low-res historical API data

$PhaseWindows = @{
    "ov"   = @{ Start = "09:30"; End = "10:13"; Name = "Open Volatility" }
    "base" = @{ Start = "10:13"; End = "14:00"; Name = "Base" }
    "ph"   = @{ Start = "14:00"; End = "16:00"; Name = "Power Hour" }
    "full" = @{ Start = $null;   End = $null;   Name = "Full Day" }
}

$TargetDates = if ($Quick) { $QuickDates } else { $PrimaryDates }
$Timestamp   = Get-Date -Format "yyyyMMdd_HHmmss"

# ─── Setup ───
if (-not (Test-Path $SweepDir))   { New-Item -ItemType Directory -Path $SweepDir   -Force | Out-Null }
if (-not (Test-Path $ResultsDir)) { New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null }

# Track the evolving baseline config path (starts as appsettings.json, gets replaced by each round's winner)
$script:CurrentBaseline = if ($WinnerConfig -ne "" -and (Test-Path $WinnerConfig)) {
    Write-Host "  Resuming from winner config: $WinnerConfig" -ForegroundColor Yellow
    $WinnerConfig
} else {
    $BaseConfig
}

# ═══════════════════════════════════════════════════════════════════════════
# CORE FUNCTIONS (adapted from sweep.ps1)
# ═══════════════════════════════════════════════════════════════════════════

function Read-Config {
    param([string]$Path = $script:CurrentBaseline)
    return (Get-Content $Path -Raw | ConvertFrom-Json)
}

function Write-SweepConfig {
    param([object]$Config, [string]$Name)
    $path = Join-Path $SweepDir "$Name.json"
    $Config | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding UTF8
    return $path
}

function Set-BaseProperty {
    param([object]$Config, [string]$Property, $Value)
    $Config.TradingBot | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-ExitProperty {
    param([object]$Config, [string]$Property, $Value)
    $Config.TradingBot.ExitStrategy | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-PhaseOverride {
    param([object]$Config, [string]$PhaseName, [string]$Property, $Value)
    $rule = $Config.TradingBot.TimeRules | Where-Object { $_.Name -eq $PhaseName }
    if ($null -eq $rule) { Write-Warning "Phase '$PhaseName' not found"; return }
    $rule.Overrides | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-DynamicStopTiers {
    param([object]$Config, [array]$Tiers)
    $Config.TradingBot.DynamicStopLoss | Add-Member -MemberType NoteProperty -Name "Tiers" -Value $Tiers -Force
}

function Run-Replay {
    param(
        [string]$ConfigPath,
        [string]$Date,
        [string]$StartTime = $null,
        [string]$EndTime = $null
    )

    $argsList = @("run", "--project", $ProjectDir, "--", "--mode=replay", "--date=$Date", "--speed=0", "-config=$ConfigPath")
    if ($StartTime) { $argsList += "--start-time=$StartTime" }
    if ($EndTime)   { $argsList += "--end-time=$EndTime" }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & dotnet @argsList 2>&1 | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    $result = @{
        PnL = $null; Trades = $null; SpreadCost = $null; SlipCost = $null
        PeakPnL = $null; TroughPnL = $null; PeakTime = $null; TroughTime = $null
        Runtime = [int]$sw.Elapsed.TotalSeconds; ExitCode = $exitCode
    }

    foreach ($line in $output) {
        if ($line -match 'Realized P/L:\s+\$?([-\d,.]+)')       { $result.PnL = $matches[1] -replace ',','' }
        if ($line -match 'Total Trades:\s+(\d+)')                { $result.Trades = $matches[1] }
        if ($line -match 'Spread Cost:\s+\$?([\d,.]+)')          { $result.SpreadCost = $matches[1] -replace ',','' }
        if ($line -match 'Slippage Cost:\s+\$?([\d,.]+)')        { $result.SlipCost = $matches[1] -replace ',','' }
        if ($line -match 'Peak P/L:\s+[+]?\$?([-\d,.]+).*?(\d{2}:\d{2}:\d{2})\s*ET') {
            $result.PeakPnL = $matches[1] -replace ',',''; $result.PeakTime = $matches[2]
        }
        if ($line -match 'Trough P/L:\s+-?\$?([-\d,.]+).*?(\d{2}:\d{2}:\d{2})\s*ET') {
            $result.TroughPnL = $matches[1] -replace ',',''; $result.TroughTime = $matches[2]
        }
    }

    return $result
}

function Format-PnL {
    param([string]$Value)
    if ($null -eq $Value -or $Value -eq "") { return "ERR" }
    $num = [decimal]$Value
    if ($num -ge 0) { return "+`$$Value" } else { return "`$$Value" }
}

function Get-Color {
    param([string]$Value)
    if ($null -eq $Value -or $Value -eq "") { return "Red" }
    $num = [decimal]$Value
    if ($num -gt 50)  { return "Green" }
    if ($num -gt 0)   { return "Yellow" }
    return "Red"
}

function Run-Scenario {
    param(
        [string]$ScenarioName,
        [string]$ConfigPath,
        [string[]]$Dates,
        [string]$PhaseKey = "full"
    )

    $phase = $PhaseWindows[$PhaseKey]
    $totalPnL = 0; $totalTrades = 0; $totalSpread = 0; $totalSlip = 0
    $perDay = @()

    foreach ($date in $Dates) {
        $r = Run-Replay -ConfigPath $ConfigPath -Date $date -StartTime $phase.Start -EndTime $phase.End
        $pnl    = if ($r.PnL)    { [decimal]$r.PnL }    else { 0 }
        $trades = if ($r.Trades) { [int]$r.Trades }     else { 0 }
        $spread = if ($r.SpreadCost) { [decimal]$r.SpreadCost } else { 0 }
        $slip   = if ($r.SlipCost)   { [decimal]$r.SlipCost }   else { 0 }
        $totalPnL += $pnl; $totalTrades += $trades; $totalSpread += $spread; $totalSlip += $slip
        $perDay += @{ Date=$date; PnL=$r.PnL; Trades=$r.Trades; SpreadCost=$r.SpreadCost; SlipCost=$r.SlipCost;
                      PeakPnL=$r.PeakPnL; TroughPnL=$r.TroughPnL; Runtime=$r.Runtime }
    }

    $color = Get-Color "$totalPnL"
    $avgPnL = if ($Dates.Count -gt 0) { [math]::Round($totalPnL / $Dates.Count, 2) } else { 0 }
    $txnCost = [math]::Round($totalSpread + $totalSlip, 2)

    Write-Host ("  {0,-45} | {1,10} | {2,8} | {3,5} | TXN:{4,7}" -f $ScenarioName, (Format-PnL "$([math]::Round($totalPnL,2))"), (Format-PnL "$avgPnL"), $totalTrades, "`$$txnCost") -ForegroundColor $color

    foreach ($day in $perDay) {
        $dayColor = Get-Color $day.PnL
        Write-Host ("    {0}: {1,10} | {2,4}t | Spd:{3,6} Slp:{4,6}" -f $day.Date, (Format-PnL $day.PnL), $day.Trades, "`$$($day.SpreadCost)", "`$$($day.SlipCost)") -ForegroundColor DarkGray
    }

    return @{
        Scenario=$ScenarioName; TotalPnL=$totalPnL; AvgPnL=$avgPnL;
        TotalTrades=$totalTrades; TxnCost=$txnCost; PerDay=$perDay
    }
}

function Print-Header {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 90) -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ("  Dates: {0}" -f ($TargetDates -join ", ")) -ForegroundColor DarkGray
    Write-Host ("  Baseline: {0}" -f (Split-Path $script:CurrentBaseline -Leaf)) -ForegroundColor DarkGray
    Write-Host ("=" * 90) -ForegroundColor Cyan
    Write-Host ("  {0,-45} | {1,10} | {2,8} | {3,5} | {4,10}" -f "Scenario", "Total P/L", "Avg P/L", "Trd", "Txn Cost") -ForegroundColor Yellow
    Write-Host ("  " + ("-" * 86)) -ForegroundColor Yellow
}

function Run-SweepRound {
    param(
        [string]$RoundName,
        [array]$Scenarios,       # Array of @{ Name; Config }
        [string]$PhaseKey = "full"
    )

    Print-Header $RoundName

    # Always run current baseline first
    Write-Host "  [Baseline]" -ForegroundColor DarkCyan
    $baseResult = Run-Scenario -ScenarioName "CURRENT" -ConfigPath $script:CurrentBaseline -Dates $TargetDates -PhaseKey $PhaseKey
    Write-Host ""

    $allResults = @($baseResult)
    $total = $Scenarios.Count
    $i = 0

    foreach ($scenario in $Scenarios) {
        $i++
        $pct = [math]::Round(($i / $total) * 100)
        Write-Host "  [$i/$total ($pct%)]" -ForegroundColor DarkCyan -NoNewline
        $result = Run-Scenario -ScenarioName $scenario.Name -ConfigPath $scenario.Config -Dates $TargetDates -PhaseKey $PhaseKey
        $allResults += $result
    }

    # Ranking
    Write-Host ""
    Write-Host "  === RANKING ===" -ForegroundColor Cyan
    $ranked = $allResults | Sort-Object { $_.TotalPnL } -Descending
    $rank = 0
    foreach ($r in $ranked) {
        $rank++
        $color = Get-Color "$($r.TotalPnL)"
        $marker = if ($r.Scenario -eq "CURRENT") { " <<<" } else { "" }
        Write-Host ("  #{0,-3} {1,-45} | {2,10} | {3,5} trades{4}" -f $rank, $r.Scenario, (Format-PnL "$([math]::Round($r.TotalPnL,2))"), $r.TotalTrades, $marker) -ForegroundColor $color
    }

    # CSV export
    $csvPath = Join-Path $ResultsDir "${RoundName}_${Timestamp}.csv"
    $rows = @()
    foreach ($r in $allResults) {
        foreach ($day in $r.PerDay) {
            $rows += [PSCustomObject]@{
                Scenario=$r.Scenario; Date=$day.Date; PnL=$day.PnL; Trades=$day.Trades;
                SpreadCost=$day.SpreadCost; SlipCost=$day.SlipCost;
                PeakPnL=$day.PeakPnL; TroughPnL=$day.TroughPnL; Runtime=$day.Runtime
            }
        }
        $rows += [PSCustomObject]@{
            Scenario=$r.Scenario; Date="TOTAL"; PnL=$r.TotalPnL; Trades=$r.TotalTrades;
            SpreadCost=""; SlipCost=""; PeakPnL=""; TroughPnL=""; Runtime=""
        }
    }
    $rows | Export-Csv -Path $csvPath -NoTypeInformation
    Write-Host "  Results: $csvPath" -ForegroundColor DarkGray

    # Return the winner for cascading
    $winner = $ranked[0]
    return $winner
}

# ═══════════════════════════════════════════════════════════════════════════
# BASELINE
# ═══════════════════════════════════════════════════════════════════════════

function Run-FullBaseline {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║           10-DAY BASELINE ESTABLISHMENT             ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Green

    # Full day on all 9 primary dates
    Print-Header "Primary Dataset — Full Day"
    $totalPL = 0; $totalT = 0
    foreach ($d in $PrimaryDates) {
        $r = Run-Replay -ConfigPath $script:CurrentBaseline -Date $d
        $pl = if ($r.PnL) { [decimal]$r.PnL } else { 0 }
        $t = if ($r.Trades) { [int]$r.Trades } else { 0 }
        $totalPL += $pl; $totalT += $t
        $color = Get-Color $r.PnL
        Write-Host ("  {0}: P/L={1,10} | {2,4} trades | Spd:{3,7} Slp:{4,7} | Peak:{5,8} Trough:{6,8}" -f $d, (Format-PnL $r.PnL), $r.Trades, "`$$($r.SpreadCost)", "`$$($r.SlipCost)", (Format-PnL $r.PeakPnL), (Format-PnL $r.TroughPnL)) -ForegroundColor $color
    }
    Write-Host ("  {0}  TOTAL: P/L={1,10} | {2,4} trades" -f ("-" * 10), (Format-PnL "$([math]::Round($totalPL,2))"), $totalT) -ForegroundColor Cyan

    # Out-of-sample validation (Feb 6)
    Write-Host ""
    Write-Host "  --- Out-of-Sample: Feb 6 (low-res) ---" -ForegroundColor DarkCyan
    foreach ($d in $OosDates) {
        $r = Run-Replay -ConfigPath $script:CurrentBaseline -Date $d
        $color = Get-Color $r.PnL
        Write-Host ("  {0}: P/L={1,10} | {2,4} trades | Spd:{3,7} Slp:{4,7}" -f $d, (Format-PnL $r.PnL), $r.Trades, "`$$($r.SpreadCost)", "`$$($r.SlipCost)") -ForegroundColor $color
    }

    Write-Host ""
    Write-Host "  Baseline complete." -ForegroundColor Green
}

# ═══════════════════════════════════════════════════════════════════════════
# DIAGNOSIS
# ═══════════════════════════════════════════════════════════════════════════

function Run-Diagnosis {
    param([string]$Date)

    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Yellow
    Write-Host "║        WHIPSAW DIAGNOSIS — $Date                   ║" -ForegroundColor Yellow
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Yellow

    foreach ($phaseKey in @("ov", "base", "ph", "full")) {
        $phase = $PhaseWindows[$phaseKey]
        $r = Run-Replay -ConfigPath $script:CurrentBaseline -Date $Date -StartTime $phase.Start -EndTime $phase.End
        $color = Get-Color $r.PnL
        Write-Host ("  {0,-20}: P/L={1,10} | {2,4} trades | Spd:{3,7} Slp:{4,7} | Peak:{5,8}@{6} Trough:{7,8}@{8}" -f `
            $phase.Name, (Format-PnL $r.PnL), $r.Trades, "`$$($r.SpreadCost)", "`$$($r.SlipCost)", `
            (Format-PnL $r.PeakPnL), $r.PeakTime, (Format-PnL $r.TroughPnL), $r.TroughTime) -ForegroundColor $color
    }
}

# ═══════════════════════════════════════════════════════════════════════════
# ROUND 1: SIGNAL GENERATION
# ═══════════════════════════════════════════════════════════════════════════

function Get-Round1Scenarios {
    $scenarios = @()

    # MinVelocityThreshold (base)
    foreach ($v in @(0.000008, 0.000010, 0.000012, 0.000015, 0.000018, 0.000020, 0.000025)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "MinVelocityThreshold" $v
        $path = Write-SweepConfig $cfg "r1_vel_$v"
        $scenarios += @{ Name = "Vel=$v"; Config = $path }
    }

    # ChopThresholdPercent (base)
    foreach ($c in @(0.0006, 0.0008, 0.0010, 0.0011, 0.0013, 0.0015, 0.0018)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "ChopThresholdPercent" $c
        $path = Write-SweepConfig $cfg "r1_chop_$c"
        $scenarios += @{ Name = "Chop=$c"; Config = $path }
    }

    # SMAWindowSeconds (base)
    foreach ($s in @(90, 120, 150, 180, 210, 240, 300)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "SMAWindowSeconds" $s
        $path = Write-SweepConfig $cfg "r1_sma_$s"
        $scenarios += @{ Name = "SMA=${s}s"; Config = $path }
    }

    # TrendWindowSeconds (base)
    foreach ($t in @(1800, 2400, 3600, 4200, 4800, 5400, 6000, 7200)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "TrendWindowSeconds" $t
        $path = Write-SweepConfig $cfg "r1_trend_$t"
        $scenarios += @{ Name = "TrendWin=${t}s"; Config = $path }
    }

    # MinChopAbsolute (base)
    foreach ($mc in @(0.01, 0.015, 0.02, 0.03, 0.04, 0.05)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "MinChopAbsolute" $mc
        $path = Write-SweepConfig $cfg "r1_minchop_$mc"
        $scenarios += @{ Name = "MinChop=$mc"; Config = $path }
    }

    # SlopeWindowSize (base) — rarely swept but important for whipsaw detection
    foreach ($sw in @(10, 15, 20, 25, 30, 40)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "SlopeWindowSize" $sw
        $path = Write-SweepConfig $cfg "r1_slope_$sw"
        $scenarios += @{ Name = "SlopeWin=$sw"; Config = $path }
    }

    # EntryVelocityMultiplier — controls how much stronger velocity must be vs threshold
    foreach ($ev in @(0.5, 0.75, 1.0, 1.25, 1.5, 2.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "EntryVelocityMultiplier" $ev
        $path = Write-SweepConfig $cfg "r1_entrymult_$ev"
        $scenarios += @{ Name = "EntryMult=$ev"; Config = $path }
    }

    # EntryConfirmationTicks — requires N consecutive confirming ticks before entry
    foreach ($ec in @(0, 1, 2, 3, 5)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "EntryConfirmationTicks" $ec
        $path = Write-SweepConfig $cfg "r1_confirm_$ec"
        $scenarios += @{ Name = "ConfirmTicks=$ec"; Config = $path }
    }

    return $scenarios
}

# ═══════════════════════════════════════════════════════════════════════════
# ROUND 2: STOPS & DYNAMIC STOP-LOSS
# ═══════════════════════════════════════════════════════════════════════════

function Get-Round2Scenarios {
    $scenarios = @()

    # TrailingStopPercent (base)
    foreach ($ts in @(0.0010, 0.0015, 0.0020, 0.0025, 0.0030, 0.0040, 0.0050)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "TrailingStopPercent" $ts
        $path = Write-SweepConfig $cfg "r2_trail_$ts"
        $scenarios += @{ Name = "Trail=$($ts*100)%"; Config = $path }
    }

    # TrendRescueTrailingStopPercent
    foreach ($tr in @(0.003, 0.004, 0.005, 0.006, 0.007, 0.008, 0.010)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "TrendRescueTrailingStopPercent" $tr
        $path = Write-SweepConfig $cfg "r2_trendrescue_$tr"
        $scenarios += @{ Name = "TRescueStop=$($tr*100)%"; Config = $path }
    }

    # DynamicStopLoss: current vs wider vs tighter vs off
    $tierSets = @{
        "DSL_Off" = $null  # Will disable
        "DSL_Tight" = @(
            @{ TriggerProfitPercent=0.002; StopPercent=0.001 },
            @{ TriggerProfitPercent=0.004; StopPercent=0.0008 },
            @{ TriggerProfitPercent=0.006; StopPercent=0.0005 }
        )
        "DSL_Wide" = @(
            @{ TriggerProfitPercent=0.005; StopPercent=0.003 },
            @{ TriggerProfitPercent=0.008; StopPercent=0.002 },
            @{ TriggerProfitPercent=0.012; StopPercent=0.001 }
        )
        "DSL_Aggressive" = @(
            @{ TriggerProfitPercent=0.002; StopPercent=0.0015 },
            @{ TriggerProfitPercent=0.003; StopPercent=0.001 },
            @{ TriggerProfitPercent=0.005; StopPercent=0.0005 }
        )
    }
    foreach ($name in $tierSets.Keys) {
        $cfg = Read-Config
        if ($name -eq "DSL_Off") {
            $cfg.TradingBot.DynamicStopLoss | Add-Member -MemberType NoteProperty -Name "Enabled" -Value $false -Force
        } else {
            Set-DynamicStopTiers $cfg $tierSets[$name]
        }
        $path = Write-SweepConfig $cfg "r2_$name"
        $scenarios += @{ Name = $name; Config = $path }
    }

    # StopLossCooldownSeconds
    foreach ($cd in @(3, 5, 10, 15, 20, 30, 60)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "StopLossCooldownSeconds" $cd
        $path = Write-SweepConfig $cfg "r2_slcooldown_$cd"
        $scenarios += @{ Name = "SLCooldown=${cd}s"; Config = $path }
    }

    return $scenarios
}

# ═══════════════════════════════════════════════════════════════════════════
# ROUND 3: EXIT STRATEGY
# ═══════════════════════════════════════════════════════════════════════════

function Get-Round3Scenarios {
    $scenarios = @()

    # ScalpWaitSeconds
    foreach ($sw in @(-1, 10, 15, 20, 30, 45, 60, 90)) {
        $cfg = Read-Config
        Set-ExitProperty $cfg "ScalpWaitSeconds" $sw
        $label = if ($sw -eq -1) { "OFF" } else { "${sw}s" }
        $path = Write-SweepConfig $cfg "r3_scalp_$sw"
        $scenarios += @{ Name = "ScalpWait=$label"; Config = $path }
    }

    # TrendWaitSeconds
    foreach ($tw in @(-1, 60, 90, 120, 180, 240, 300, 600)) {
        $cfg = Read-Config
        Set-ExitProperty $cfg "TrendWaitSeconds" $tw
        $label = if ($tw -eq -1) { "OFF" } else { "${tw}s" }
        $path = Write-SweepConfig $cfg "r3_trend_$tw"
        $scenarios += @{ Name = "TrendWait=$label"; Config = $path }
    }

    # TrendConfidenceThreshold
    foreach ($tc in @(0.00003, 0.00005, 0.00006, 0.00008, 0.00010, 0.00012, 0.00015, 0.00020)) {
        $cfg = Read-Config
        Set-ExitProperty $cfg "TrendConfidenceThreshold" $tc
        $path = Write-SweepConfig $cfg "r3_tconf_$tc"
        $scenarios += @{ Name = "TrendConf=$tc"; Config = $path }
    }

    # HoldNeutralIfUnderwater
    foreach ($h in @($true, $false)) {
        $cfg = Read-Config
        Set-ExitProperty $cfg "HoldNeutralIfUnderwater" $h
        $path = Write-SweepConfig $cfg "r3_holdneutral_$h"
        $scenarios += @{ Name = "HoldNeutral=$h"; Config = $path }
    }

    return $scenarios
}

# ═══════════════════════════════════════════════════════════════════════════
# ROUND 4: TRIMMING & DRIFT MODE
# ═══════════════════════════════════════════════════════════════════════════

function Get-Round4Scenarios {
    $scenarios = @()

    # EnableTrimming on/off
    foreach ($et in @($true, $false)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "EnableTrimming" $et
        $path = Write-SweepConfig $cfg "r4_trim_$et"
        $scenarios += @{ Name = "Trimming=$et"; Config = $path }
    }

    # TrimRatio
    foreach ($tr in @(0.25, 0.33, 0.50, 0.67, 0.75, 1.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "TrimRatio" $tr
        $path = Write-SweepConfig $cfg "r4_trimratio_$tr"
        $scenarios += @{ Name = "TrimRatio=$tr"; Config = $path }
    }

    # TrimTriggerPercent
    foreach ($tt in @(0.0010, 0.0015, 0.0020, 0.0025, 0.0030, 0.0040)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "TrimTriggerPercent" $tt
        $path = Write-SweepConfig $cfg "r4_trimtrig_$tt"
        $scenarios += @{ Name = "TrimTrig=$($tt*100)%"; Config = $path }
    }

    # TrimCooldownSeconds
    foreach ($tc in @(60, 120, 180, 300, 600)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "TrimCooldownSeconds" $tc
        $path = Write-SweepConfig $cfg "r4_trimcool_$tc"
        $scenarios += @{ Name = "TrimCool=${tc}s"; Config = $path }
    }

    # DriftModeConsecutiveTicks
    foreach ($dt in @(20, 30, 45, 60, 90, 120)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DriftModeConsecutiveTicks" $dt
        $path = Write-SweepConfig $cfg "r4_driftticks_$dt"
        $scenarios += @{ Name = "DriftTicks=$dt"; Config = $path }
    }

    # DriftModeMinDisplacementPercent
    foreach ($dd in @(0.001, 0.0015, 0.002, 0.0025, 0.003, 0.004)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DriftModeMinDisplacementPercent" $dd
        $path = Write-SweepConfig $cfg "r4_driftdisp_$dd"
        $scenarios += @{ Name = "DriftDisp=$($dd*100)%"; Config = $path }
    }

    # DriftTrailingStopPercent
    foreach ($ds in @(0.0020, 0.0025, 0.0030, 0.0035, 0.0040, 0.0050, 0.0060)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DriftTrailingStopPercent" $ds
        $path = Write-SweepConfig $cfg "r4_driftstop_$ds"
        $scenarios += @{ Name = "DriftStop=$($ds*100)%"; Config = $path }
    }

    # DriftMode on/off
    foreach ($dm in @($true, $false)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DriftModeEnabled" $dm
        $path = Write-SweepConfig $cfg "r4_driftmode_$dm"
        $scenarios += @{ Name = "DriftMode=$dm"; Config = $path }
    }

    return $scenarios
}

# ═══════════════════════════════════════════════════════════════════════════
# ROUND 5: DAILY TARGETS & CROSS-CUTTING
# ═══════════════════════════════════════════════════════════════════════════

function Get-Round5Scenarios {
    $scenarios = @()

    # DailyProfitTargetPercent
    foreach ($dp in @(0, 0.5, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DailyProfitTargetPercent" $dp
        $label = if ($dp -eq 0) { "OFF" } else { "$($dp)%" }
        $path = Write-SweepConfig $cfg "r5_dailytarget_$dp"
        $scenarios += @{ Name = "DailyTarget=$label"; Config = $path }
    }

    # DailyProfitTargetTrailingStopPercent
    foreach ($dt in @(0.10, 0.15, 0.20, 0.25, 0.30, 0.40, 0.50)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DailyProfitTargetTrailingStopPercent" $dt
        $path = Write-SweepConfig $cfg "r5_trailstop_$dt"
        $scenarios += @{ Name = "TargetTrail=$($dt*100)%"; Config = $path }
    }

    # ProfitReinvestmentPercent
    foreach ($pr in @(0, 0.25, 0.5, 0.75, 1.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "ProfitReinvestmentPercent" $pr
        $path = Write-SweepConfig $cfg "r5_reinvest_$pr"
        $scenarios += @{ Name = "Reinvest=$($pr*100)%"; Config = $path }
    }

    # DailyLossLimitPercent
    foreach ($dl in @(0, 0.5, 1.0, 1.5, 2.0, 3.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DailyLossLimitPercent" $dl
        $label = if ($dl -eq 0) { "OFF" } else { "$($dl)%" }
        $path = Write-SweepConfig $cfg "r5_losslimit_$dl"
        $scenarios += @{ Name = "LossLimit=$label"; Config = $path }
    }

    # ResumeInPowerHour
    foreach ($rp in @($true, $false)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "ResumeInPowerHour" $rp
        $path = Write-SweepConfig $cfg "r5_phresume_$rp"
        $scenarios += @{ Name = "PHResume=$rp"; Config = $path }
    }

    return $scenarios
}

# ═══════════════════════════════════════════════════════════════════════════
# ROUND 6: PHASE BOUNDARIES & PHASE-SPECIFIC OVERRIDES
# ═══════════════════════════════════════════════════════════════════════════

function Get-Round6Scenarios {
    $scenarios = @()

    # OV EndTime
    foreach ($et in @("09:45", "09:50", "10:00", "10:05", "10:10", "10:13", "10:15", "10:20")) {
        $cfg = Read-Config
        $rule = $cfg.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
        $rule | Add-Member -MemberType NoteProperty -Name "EndTime" -Value $et -Force
        $path = Write-SweepConfig $cfg "r6_ovend_$($et -replace ':','')"
        $scenarios += @{ Name = "OV_End=$et"; Config = $path }
    }

    # PH StartTime
    foreach ($st in @("13:30", "13:45", "14:00", "14:15", "14:30", "15:00")) {
        $cfg = Read-Config
        $rule = $cfg.TradingBot.TimeRules | Where-Object { $_.Name -eq "Power Hour" }
        $rule | Add-Member -MemberType NoteProperty -Name "StartTime" -Value $st -Force
        $path = Write-SweepConfig $cfg "r6_phstart_$($st -replace ':','')"
        $scenarios += @{ Name = "PH_Start=$st"; Config = $path }
    }

    # OV TrailingStopPercent
    foreach ($ots in @(0.003, 0.004, 0.005, 0.006, 0.008, 0.010)) {
        $cfg = Read-Config
        Set-PhaseOverride $cfg "Open Volatility" "TrailingStopPercent" $ots
        $path = Write-SweepConfig $cfg "r6_ovtrail_$ots"
        $scenarios += @{ Name = "OV_Trail=$($ots*100)%"; Config = $path }
    }

    # OV MinVelocityThreshold
    foreach ($ov in @(0.000010, 0.000015, 0.000020, 0.000025, 0.000030, 0.000040)) {
        $cfg = Read-Config
        Set-PhaseOverride $cfg "Open Volatility" "MinVelocityThreshold" $ov
        $path = Write-SweepConfig $cfg "r6_ovvel_$ov"
        $scenarios += @{ Name = "OV_Vel=$ov"; Config = $path }
    }

    # OV ChopThresholdPercent
    foreach ($oc in @(0.0010, 0.0012, 0.0015, 0.0020, 0.0025)) {
        $cfg = Read-Config
        Set-PhaseOverride $cfg "Open Volatility" "ChopThresholdPercent" $oc
        $path = Write-SweepConfig $cfg "r6_ovchop_$oc"
        $scenarios += @{ Name = "OV_Chop=$oc"; Config = $path }
    }

    # PH MinVelocityThreshold
    foreach ($pv in @(0.000015, 0.000020, 0.000025, 0.000030, 0.000040, 0.000050)) {
        $cfg = Read-Config
        Set-PhaseOverride $cfg "Power Hour" "MinVelocityThreshold" $pv
        $path = Write-SweepConfig $cfg "r6_phvel_$pv"
        $scenarios += @{ Name = "PH_Vel=$pv"; Config = $path }
    }

    # PH TrendWindowSeconds
    foreach ($ptw in @(600, 900, 1200, 1800, 2400, 3600)) {
        $cfg = Read-Config
        Set-PhaseOverride $cfg "Power Hour" "TrendWindowSeconds" $ptw
        $path = Write-SweepConfig $cfg "r6_phtrend_$ptw"
        $scenarios += @{ Name = "PH_TrendWin=${ptw}s"; Config = $path }
    }

    # OV SMAWindowSeconds
    foreach ($os in @(60, 90, 120, 150, 180)) {
        $cfg = Read-Config
        Set-PhaseOverride $cfg "Open Volatility" "SMAWindowSeconds" $os
        $path = Write-SweepConfig $cfg "r6_ovsma_$os"
        $scenarios += @{ Name = "OV_SMA=${os}s"; Config = $path }
    }

    return $scenarios
}

# ═══════════════════════════════════════════════════════════════════════════
# WINNER BAKING
# ═══════════════════════════════════════════════════════════════════════════

function Bake-Winner {
    param([string]$WinnerScenario, [string]$RoundLabel)

    # The winner scenario has its config file already written. If it beat CURRENT, update the baseline;
    # otherwise, keep the current baseline (CURRENT won).
    if ($WinnerScenario -eq "CURRENT") {
        Write-Host "  >> CURRENT baseline wins Round $RoundLabel. No changes baked." -ForegroundColor Yellow
        return
    }

    # Find the winner's config file in sweep_configs
    $winnerFile = Get-ChildItem $SweepDir -Filter "*.json" | Sort-Object LastWriteTime -Descending |
        Where-Object { $_.Name -like "*$RoundLabel*" -or $_.Name -like "r*" } | Select-Object -First 1

    # Safer: since the scenario ran with a specific config, we can reconstruct it.
    # For now, just keep using the current baseline — the user should manually bake winners
    # between rounds by passing -WinnerConfig.
    Write-Host "  >> Winner: $WinnerScenario" -ForegroundColor Green
    Write-Host "  >> To cascade into next round, re-run with: -WinnerConfig <path_to_winner_config>" -ForegroundColor Yellow
    Write-Host "  >> Or modify appsettings.json with the winning values." -ForegroundColor Yellow
}

# ═══════════════════════════════════════════════════════════════════════════
# MAIN DISPATCH
# ═══════════════════════════════════════════════════════════════════════════

Set-Location $ProjectDir

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  qqqBot COMPREHENSIVE PARAMETER SWEEP                          ║" -ForegroundColor Cyan
Write-Host "║  Branch: tuning/small-dataset-v1                               ║" -ForegroundColor Cyan
Write-Host "║  SimBroker: Spread + Vol-Slippage + Phase-Aware                ║" -ForegroundColor Cyan
Write-Host "║  Dates: $($TargetDates.Count) primary, $($OosDates.Count) OOS                                      ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  Primary: $($TargetDates -join ', ')" -ForegroundColor DarkGray
Write-Host "  OOS:     $($OosDates -join ', ')" -ForegroundColor DarkGray
Write-Host "  Quick:   $Quick" -ForegroundColor DarkGray
Write-Host ""

# Diagnose mode
if ($DiagnoseDate -ne "") {
    Run-Diagnosis -Date $DiagnoseDate
    exit 0
}

# Baseline only
if ($BaselineOnly) {
    Run-FullBaseline
    exit 0
}

# ─── ROUND EXECUTION ───

$rounds = @{
    1 = @{ Name = "Round 1 — Signal Generation"; Generator = { Get-Round1Scenarios } }
    2 = @{ Name = "Round 2 — Stops & DSL";       Generator = { Get-Round2Scenarios } }
    3 = @{ Name = "Round 3 — Exit Strategy";      Generator = { Get-Round3Scenarios } }
    4 = @{ Name = "Round 4 — Trimming & Drift";   Generator = { Get-Round4Scenarios } }
    5 = @{ Name = "Round 5 — Daily Targets";      Generator = { Get-Round5Scenarios } }
    6 = @{ Name = "Round 6 — Phase Boundaries";   Generator = { Get-Round6Scenarios } }
}

# Determine which rounds to run
$roundsToRun = if ($Round -gt 0) { @($Round) } else { 1..6 }

# Always start with baseline
Run-FullBaseline

foreach ($r in $roundsToRun) {
    $roundInfo = $rounds[$r]
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║  $($roundInfo.Name.PadRight(50))║" -ForegroundColor Magenta
    Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Magenta

    $scenarios = & $roundInfo.Generator
    Write-Host "  Scenarios to test: $($scenarios.Count)" -ForegroundColor DarkGray

    $winner = Run-SweepRound -RoundName $roundInfo.Name -Scenarios $scenarios
    Bake-Winner -WinnerScenario $winner.Scenario -RoundLabel "R$r"
}

# Final OOS validation
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  OUT-OF-SAMPLE VALIDATION (Feb 6)                   ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════╝" -ForegroundColor Green
foreach ($d in $OosDates) {
    $r = Run-Replay -ConfigPath $script:CurrentBaseline -Date $d
    $color = Get-Color $r.PnL
    Write-Host ("  {0}: P/L={1,10} | {2,4} trades | Spd:{3,7} Slp:{4,7}" -f $d, (Format-PnL $r.PnL), $r.Trades, "`$$($r.SpreadCost)", "`$$($r.SlipCost)") -ForegroundColor $color
}

Write-Host ""
Write-Host "=== COMPREHENSIVE SWEEP COMPLETE ===" -ForegroundColor Green
Write-Host "  Results in: $ResultsDir" -ForegroundColor DarkGray
Write-Host "  To apply winners, update appsettings.json with the winning parameter values." -ForegroundColor Yellow
Write-Host ""
