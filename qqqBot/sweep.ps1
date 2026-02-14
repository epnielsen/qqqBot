# sweep.ps1 — Phase-by-phase parameter sweep harness for qqqBot trading settings optimization
# Usage examples:
#   .\sweep.ps1 -Action baseline                          # Run baselines (all dates, all phases + full day)
#   .\sweep.ps1 -Action sweep -Phase base -Group signal   # Sweep signal params for base phase
#   .\sweep.ps1 -Action sweep -Phase ov -Group signal     # Sweep signal params for OV phase
#   .\sweep.ps1 -Action sweep -Phase ph -Group signal     # Sweep signal params for power hour
#   .\sweep.ps1 -Action sweep -Phase base -Group stops    # Sweep stop params for base phase
#   .\sweep.ps1 -Action sweep -Phase base -Group exit     # Sweep exit strategy params
#   .\sweep.ps1 -Action sweep -Phase base -Group trim     # Sweep trimming params
#   .\sweep.ps1 -Action sweep -Phase base -Group direction # Sweep BullOnly/Both per phase
#   .\sweep.ps1 -Action sweep -Phase all -Group daily     # Cross-cutting daily params (full day runs)
#   .\sweep.ps1 -Action custom -ConfigFile my.json -Dates "20260209,20260211"  # One-off custom config test

param(
    [ValidateSet("baseline", "sweep", "custom")]
    [string]$Action = "baseline",

    [ValidateSet("ov", "base", "ph", "all")]
    [string]$Phase = "all",

    [ValidateSet("signal", "stops", "exit", "trim", "direction", "daily", "boundaries")]
    [string]$Group = "signal",

    [string]$Dates = "",         # Comma-separated: "20260209,20260211" or empty for all
    [string]$ConfigFile = "",    # For -Action custom
    [switch]$Quick                # Use only 2 dates (Feb 11, 12) for fast iteration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ─── Configuration ───
$ProjectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$BaseConfig = Join-Path $ProjectDir "appsettings.json"
$SweepDir   = Join-Path $ProjectDir "sweep_configs"
$ResultsDir = Join-Path $ProjectDir "sweep_results"
$AllDates   = @("20260209", "20260210", "20260211", "20260212", "20260213")
$QuickDates = @("20260211", "20260212")  # One good day, one good day — fast sanity check
$ValidationDate = "20260206"  # Low-res, non-deterministic — for final validation only

# Phase time windows
$PhaseWindows = @{
    "ov"   = @{ Start = "09:30"; End = "09:50"; Name = "Open Volatility" }
    "base" = @{ Start = "09:50"; End = "14:00"; Name = "Base" }
    "ph"   = @{ Start = "14:00"; End = "16:00"; Name = "Power Hour" }
    "full" = @{ Start = $null;   End = $null;   Name = "Full Day" }
}

# Determine which dates to use
if ($Dates -ne "") {
    $TargetDates = $Dates -split ","
} elseif ($Quick) {
    $TargetDates = $QuickDates
} else {
    $TargetDates = $AllDates
}

# ─── Setup ───
if (-not (Test-Path $SweepDir))   { New-Item -ItemType Directory -Path $SweepDir   -Force | Out-Null }
if (-not (Test-Path $ResultsDir)) { New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null }

# ─── Core Functions ───

function Read-Config {
    # Read appsettings.json as a PowerShell object
    $raw = Get-Content $BaseConfig -Raw
    return ($raw | ConvertFrom-Json)
}

function Write-SweepConfig {
    param([object]$Config, [string]$Name)
    $path = Join-Path $SweepDir "$Name.json"
    $Config | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding UTF8
    return $path
}

function Set-BaseProperty {
    # Set a property on the base TradingBot config (not in TimeRules)
    param([object]$Config, [string]$Property, $Value)
    $Config.TradingBot | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-ExitProperty {
    # Set a property on ExitStrategy
    param([object]$Config, [string]$Property, $Value)
    $Config.TradingBot.ExitStrategy | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-PhaseOverride {
    # Set a property on a specific TimeRules phase override
    param([object]$Config, [string]$PhaseName, [string]$Property, $Value)

    $rule = $Config.TradingBot.TimeRules | Where-Object { $_.Name -eq $PhaseName }
    if ($null -eq $rule) {
        Write-Warning "Phase '$PhaseName' not found in TimeRules"
        return
    }
    $rule.Overrides | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-DynamicStopTiers {
    param([object]$Config, [array]$Tiers)
    $Config.TradingBot.DynamicStopLoss | Add-Member -MemberType NoteProperty -Name "Tiers" -Value $Tiers -Force
}

function Set-PhaseDynamicStopTiers {
    param([object]$Config, [string]$PhaseName, [array]$Tiers)
    $rule = $Config.TradingBot.TimeRules | Where-Object { $_.Name -eq $PhaseName }
    if ($null -eq $rule) { return }
    $rule.Overrides | Add-Member -MemberType NoteProperty -Name "DynamicStopLossTiers" -Value $Tiers -Force
}

function Run-Replay {
    param(
        [string]$ConfigPath,
        [string]$Date,
        [string]$StartTime = $null,
        [string]$EndTime = $null
    )

    $args_list = @("run", "--", "--mode=replay", "--date=$Date", "--speed=0", "-config=$ConfigPath")
    if ($StartTime) { $args_list += "--start-time=$StartTime" }
    if ($EndTime)   { $args_list += "--end-time=$EndTime" }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & dotnet @args_list 2>&1 | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    # Parse results from SimBroker summary
    $result = @{
        PnL      = $null
        ReturnPct = $null
        Trades   = $null
        PeakPnL  = $null
        TroughPnL = $null
        PeakTime = $null
        TroughTime = $null
        Runtime  = [int]$sw.Elapsed.TotalSeconds
        ExitCode = $exitCode
        RawOutput = $output
    }

    foreach ($line in $output) {
        if ($line -match 'Realized P/L:\s+\$?([-\d,.]+)')       { $result.PnL = $matches[1] -replace ',','' }
        if ($line -match 'Net Return:\s+([-\d.]+)\s*%')          { $result.ReturnPct = $matches[1] }
        if ($line -match 'Total Trades:\s+(\d+)')                { $result.Trades = $matches[1] }
        if ($line -match 'Peak P/L:\s+\+?\$?([-\d,.]+).*?at\s+(\d{2}:\d{2}:\d{2})\s*ET') {
            $result.PeakPnL = $matches[1] -replace ',',''
            $result.PeakTime = $matches[2]
        }
        if ($line -match 'Trough P/L:\s+-?\$?([-\d,.]+).*?at\s+(\d{2}:\d{2}:\d{2})\s*ET') {
            $result.TroughPnL = $matches[1] -replace ',',''
            $result.TroughTime = $matches[2]
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
        [string]$PhaseKey    # "ov", "base", "ph", "full"
    )

    $phase = $PhaseWindows[$PhaseKey]
    $totalPnL = 0
    $totalTrades = 0
    $perDay = @()

    foreach ($date in $Dates) {
        $result = Run-Replay -ConfigPath $ConfigPath -Date $date `
                             -StartTime $phase.Start -EndTime $phase.End

        $pnl = if ($result.PnL) { [decimal]$result.PnL } else { 0 }
        $trades = if ($result.Trades) { [int]$result.Trades } else { 0 }
        $totalPnL += $pnl
        $totalTrades += $trades

        $perDay += @{
            Date = $date; PnL = $result.PnL; ReturnPct = $result.ReturnPct
            Trades = $result.Trades; PeakPnL = $result.PeakPnL; TroughPnL = $result.TroughPnL
            Runtime = $result.Runtime
        }
    }

    # Display summary
    $color = Get-Color "$totalPnL"
    $avgPnL = if ($Dates.Count -gt 0) { [math]::Round($totalPnL / $Dates.Count, 2) } else { 0 }

    Write-Host ("  {0,-40} | {1,10} | {2,8} | {3,6} |" -f $ScenarioName, (Format-PnL "$totalPnL"), (Format-PnL "$avgPnL"), $totalTrades) -ForegroundColor $color

    # Per-day breakdown (dimmed)
    foreach ($day in $perDay) {
        $dayColor = Get-Color $day.PnL
        $dpnl = Format-PnL $day.PnL
        Write-Host ("    {0}: {1,10} | {2,6} trades | Peak: {3,8} | Trough: {4,8} | {5}s" -f $day.Date, $dpnl, $day.Trades, (Format-PnL $day.PeakPnL), (Format-PnL $day.TroughPnL), $day.Runtime) -ForegroundColor DarkGray
    }

    return @{
        Scenario   = $ScenarioName
        TotalPnL   = $totalPnL
        AvgPnL     = $avgPnL
        TotalTrades = $totalTrades
        PerDay     = $perDay
    }
}

function Write-ResultsCsv {
    param([array]$Results, [string]$FileName)
    $csvPath = Join-Path $ResultsDir "$FileName.csv"

    $rows = @()
    foreach ($r in $Results) {
        foreach ($day in $r.PerDay) {
            $rows += [PSCustomObject]@{
                Scenario  = $r.Scenario
                Date      = $day.Date
                Phase     = $Phase
                PnL       = $day.PnL
                ReturnPct = $day.ReturnPct
                Trades    = $day.Trades
                PeakPnL   = $day.PeakPnL
                TroughPnL = $day.TroughPnL
                Runtime   = $day.Runtime
            }
        }
        # Add aggregate row
        $rows += [PSCustomObject]@{
            Scenario  = $r.Scenario
            Date      = "TOTAL"
            Phase     = $Phase
            PnL       = $r.TotalPnL
            ReturnPct = ""
            Trades    = $r.TotalTrades
            PeakPnL   = ""
            TroughPnL = ""
            Runtime   = ""
        }
    }
    $rows | Export-Csv -Path $csvPath -NoTypeInformation
    Write-Host "`n  Results saved to: $csvPath" -ForegroundColor DarkGray
}

function Print-Header {
    param([string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
    Write-Host ("  Dates: {0}" -f ($TargetDates -join ", ")) -ForegroundColor DarkGray
    Write-Host ""
    Write-Host ("  {0,-40} | {1,10} | {2,8} | {3,6} |" -f "Scenario", "Total P/L", "Avg P/L", "Trades") -ForegroundColor Yellow
    Write-Host ("  " + ("-" * 76)) -ForegroundColor Yellow
}

# ═════════════════════════════════════════════════════════════════════════════
# ACTION: BASELINE — Run current settings across all phases and all dates
# ═════════════════════════════════════════════════════════════════════════════
function Run-Baselines {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

    # Use original config (no modifications)
    $configPath = $BaseConfig

    $allResults = @()

    foreach ($phaseKey in @("ov", "base", "ph", "full")) {
        $phaseName = $PhaseWindows[$phaseKey].Name
        Print-Header "Baseline — $phaseName"

        $result = Run-Scenario -ScenarioName "Current Settings" -ConfigPath $configPath `
                               -Dates $TargetDates -PhaseKey $phaseKey
        $allResults += $result
    }

    # Save results
    $csvPath = Join-Path $ResultsDir "baseline_$timestamp.csv"
    $rows = @()
    foreach ($r in $allResults) {
        foreach ($day in $r.PerDay) {
            $rows += [PSCustomObject]@{
                Phase     = $r.Scenario
                Date      = $day.Date
                PnL       = $day.PnL
                ReturnPct = $day.ReturnPct
                Trades    = $day.Trades
                PeakPnL   = $day.PeakPnL
                TroughPnL = $day.TroughPnL
                Runtime   = $day.Runtime
            }
        }
    }
    $rows | Export-Csv -Path $csvPath -NoTypeInformation
    Write-Host "`n  Baseline results saved to: $csvPath" -ForegroundColor Green

    Write-Host "`n=== Baseline Complete ===" -ForegroundColor Cyan
}

# ═════════════════════════════════════════════════════════════════════════════
# SWEEP GENERATORS — Define parameter variations for each group
# ═════════════════════════════════════════════════════════════════════════════

function Get-SignalScenarios {
    param([string]$PhaseKey)

    $scenarios = @()

    # MinVelocityThreshold sweep
    $velocities = switch ($PhaseKey) {
        "ov"   { @(0.000015, 0.000020, 0.000025, 0.000030, 0.000040) }
        "base" { @(0.000004, 0.000006, 0.000008, 0.000010, 0.000012, 0.000015) }
        "ph"   { @(0.000008, 0.000010, 0.000015, 0.000020, 0.000025) }
    }
    foreach ($v in $velocities) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "MinVelocityThreshold" $v
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "MinVelocityThreshold" $v
        }
        $path = Write-SweepConfig $cfg "signal_vel_$($PhaseKey)_$v"
        $scenarios += @{ Name = "Velocity=$v"; Config = $path }
    }

    # ChopThresholdPercent sweep
    $chops = switch ($PhaseKey) {
        "ov"   { @(0.001, 0.0015, 0.002, 0.0025, 0.003) }
        "base" { @(0.0006, 0.0008, 0.0011, 0.0014, 0.0018) }
        "ph"   { @(0.001, 0.0012, 0.0015, 0.002) }
    }
    foreach ($c in $chops) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "ChopThresholdPercent" $c
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "ChopThresholdPercent" $c
        }
        $path = Write-SweepConfig $cfg "signal_chop_$($PhaseKey)_$c"
        $scenarios += @{ Name = "Chop=$c"; Config = $path }
    }

    # SMAWindowSeconds sweep
    $smas = switch ($PhaseKey) {
        "ov"   { @(60, 90, 120, 150) }
        "base" { @(120, 150, 180, 240, 300) }
        "ph"   { @(60, 90, 120, 150, 180) }
    }
    foreach ($s in $smas) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "SMAWindowSeconds" $s
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "SMAWindowSeconds" $s
        }
        $path = Write-SweepConfig $cfg "signal_sma_$($PhaseKey)_$s"
        $scenarios += @{ Name = "SMA=$($s)s"; Config = $path }
    }

    # TrendWindowSeconds sweep (base only — OV and PH already have overrides)
    if ($PhaseKey -eq "base") {
        foreach ($t in @(900, 1200, 1800, 2400, 3600)) {
            $cfg = Read-Config
            Set-BaseProperty $cfg "TrendWindowSeconds" $t
            $path = Write-SweepConfig $cfg "signal_trend_$($PhaseKey)_$t"
            $scenarios += @{ Name = "TrendWindow=$($t)s"; Config = $path }
        }
    }

    # MinChopAbsolute sweep
    $minchops = switch ($PhaseKey) {
        "ov"   { @(0.03, 0.05, 0.07, 0.10) }
        "base" { @(0.01, 0.02, 0.03, 0.04, 0.05) }
        "ph"   { @(0.01, 0.02, 0.03, 0.05) }
    }
    foreach ($mc in $minchops) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "MinChopAbsolute" $mc
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "MinChopAbsolute" $mc
        }
        $path = Write-SweepConfig $cfg "signal_minchop_$($PhaseKey)_$mc"
        $scenarios += @{ Name = "MinChop=`$$mc"; Config = $path }
    }

    return $scenarios
}

function Get-StopScenarios {
    param([string]$PhaseKey)

    $scenarios = @()

    # TrailingStopPercent sweep
    $stops = switch ($PhaseKey) {
        "ov"   { @(0.002, 0.003, 0.004, 0.005, 0.006) }
        "base" { @(0.0015, 0.002, 0.0025, 0.003, 0.0035, 0.004) }
        "ph"   { @(0.001, 0.0015, 0.002, 0.0025, 0.003) }
    }
    foreach ($s in $stops) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "TrailingStopPercent" $s
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "TrailingStopPercent" $s
        }
        $path = Write-SweepConfig $cfg "stop_trail_$($PhaseKey)_$s"
        $scenarios += @{ Name = "TrailStop=$($s*100)%"; Config = $path }
    }

    # DynamicStopLoss tier configurations (base only — too many combinations otherwise)
    if ($PhaseKey -eq "base") {
        $tierSets = @(
            @{ Name = "DSL_tight";   Tiers = @(
                @{ TriggerProfitPercent = 0.002; StopPercent = 0.001 },
                @{ TriggerProfitPercent = 0.004; StopPercent = 0.0007 },
                @{ TriggerProfitPercent = 0.007; StopPercent = 0.0005 }
            )},
            @{ Name = "DSL_current"; Tiers = @(
                @{ TriggerProfitPercent = 0.003; StopPercent = 0.0015 },
                @{ TriggerProfitPercent = 0.005; StopPercent = 0.001 },
                @{ TriggerProfitPercent = 0.008; StopPercent = 0.0008 }
            )},
            @{ Name = "DSL_wide";    Tiers = @(
                @{ TriggerProfitPercent = 0.004; StopPercent = 0.002 },
                @{ TriggerProfitPercent = 0.007; StopPercent = 0.0015 },
                @{ TriggerProfitPercent = 0.010; StopPercent = 0.001 }
            )},
            @{ Name = "DSL_off";     Tiers = @() }
        )
        foreach ($ts in $tierSets) {
            $cfg = Read-Config
            if ($ts.Tiers.Count -eq 0) {
                $cfg.TradingBot.DynamicStopLoss | Add-Member -MemberType NoteProperty -Name "Enabled" -Value $false -Force
            } else {
                Set-DynamicStopTiers $cfg $ts.Tiers
            }
            $path = Write-SweepConfig $cfg "stop_$($ts.Name)"
            $scenarios += @{ Name = $ts.Name; Config = $path }
        }
    }

    # StopLossCooldown sweep
    foreach ($cd in @(5, 10, 15, 20, 30)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "StopLossCooldownSeconds" $cd
        $path = Write-SweepConfig $cfg "stop_cooldown_$($PhaseKey)_$cd"
        $scenarios += @{ Name = "StopCD=$($cd)s"; Config = $path }
    }

    return $scenarios
}

function Get-ExitScenarios {
    param([string]$PhaseKey)

    $scenarios = @()

    # ScalpWaitSeconds sweep
    $scalps = switch ($PhaseKey) {
        "ov"   { @(15, 30, 45, 60) }
        "base" { @(15, 20, 30, 45, 60, 90) }
        "ph"   { @(10, 15, 20, 30, 45) }
    }
    foreach ($s in $scalps) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-ExitProperty $cfg "ScalpWaitSeconds" $s
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "ScalpWaitSeconds" $s
        }
        $path = Write-SweepConfig $cfg "exit_scalp_$($PhaseKey)_$s"
        $scenarios += @{ Name = "ScalpWait=$($s)s"; Config = $path }
    }

    # TrendWaitSeconds sweep
    $trends = switch ($PhaseKey) {
        "ov"   { @(60, 120, 180, 240) }
        "base" { @(60, 90, 120, 150, 180, 240) }
        "ph"   { @(30, 45, 60, 90, 120) }
    }
    foreach ($t in $trends) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-ExitProperty $cfg "TrendWaitSeconds" $t
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "TrendWaitSeconds" $t
        }
        $path = Write-SweepConfig $cfg "exit_trend_$($PhaseKey)_$t"
        $scenarios += @{ Name = "TrendWait=$($t)s"; Config = $path }
    }

    # TrendConfidenceThreshold sweep
    foreach ($tc in @(0.00004, 0.00006, 0.00008, 0.0001, 0.00012, 0.00015)) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-ExitProperty $cfg "TrendConfidenceThreshold" $tc
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "TrendConfidenceThreshold" $tc
        }
        $path = Write-SweepConfig $cfg "exit_tconf_$($PhaseKey)_$tc"
        $scenarios += @{ Name = "TrendConf=$tc"; Config = $path }
    }

    # HoldNeutralIfUnderwater toggle
    foreach ($hold in @($true, $false)) {
        $cfg = Read-Config
        Set-ExitProperty $cfg "HoldNeutralIfUnderwater" $hold
        $path = Write-SweepConfig $cfg "exit_hold_$($PhaseKey)_$hold"
        $scenarios += @{ Name = "HoldUnderwater=$hold"; Config = $path }
    }

    return $scenarios
}

function Get-TrimScenarios {
    param([string]$PhaseKey)

    $scenarios = @()

    # EnableTrimming on/off
    foreach ($en in @($true, $false)) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "EnableTrimming" $en
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "EnableTrimming" $en
        }
        $path = Write-SweepConfig $cfg "trim_enable_$($PhaseKey)_$en"
        $scenarios += @{ Name = "Trim=$en"; Config = $path }
    }

    # TrimTriggerPercent sweep (only if trimming enabled in base config)
    foreach ($tp in @(0.0015, 0.002, 0.0025, 0.003, 0.004, 0.005)) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "TrimTriggerPercent" $tp
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "TrimTriggerPercent" $tp
        }
        $path = Write-SweepConfig $cfg "trim_trigger_$($PhaseKey)_$tp"
        $scenarios += @{ Name = "TrimTrigger=$($tp*100)%"; Config = $path }
    }

    # TrimRatio sweep
    foreach ($tr in @(0.25, 0.33, 0.50, 0.67, 0.75)) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "TrimRatio" $tr
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "TrimRatio" $tr
        }
        $path = Write-SweepConfig $cfg "trim_ratio_$($PhaseKey)_$tr"
        $scenarios += @{ Name = "TrimRatio=$($tr*100)%"; Config = $path }
    }

    # TrimCooldownSeconds sweep
    foreach ($cd in @(60, 120, 180, 300, 600)) {
        $cfg = Read-Config
        if ($PhaseKey -eq "base") {
            Set-BaseProperty $cfg "TrimCooldownSeconds" $cd
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "TrimCooldownSeconds" $cd
        }
        $path = Write-SweepConfig $cfg "trim_cd_$($PhaseKey)_$cd"
        $scenarios += @{ Name = "TrimCD=$($cd)s"; Config = $path }
    }

    return $scenarios
}

function Get-DirectionScenarios {
    param([string]$PhaseKey)

    $scenarios = @()

    # BullOnly in the target phase
    foreach ($mode in @("both", "bullonly")) {
        $cfg = Read-Config
        if ($mode -eq "bullonly") {
            if ($PhaseKey -eq "base") {
                Set-BaseProperty $cfg "BullOnlyMode" $true
            } else {
                $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
                Set-PhaseOverride $cfg $phaseName "BullOnlyMode" $true
            }
        }
        # "both" = current config (BullOnly is false by default)
        $path = Write-SweepConfig $cfg "dir_$($PhaseKey)_$mode"
        $scenarios += @{ Name = "Direction=$mode"; Config = $path }
    }

    return $scenarios
}

function Get-DailyScenarios {
    # Cross-cutting: run on full days only
    $scenarios = @()

    # DailyProfitTargetTrailingStopPercent sweep
    foreach ($ts in @(0.1, 0.2, 0.3, 0.5, 0.75, 1.0, 1.5, 2.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DailyProfitTargetTrailingStopPercent" $ts
        $path = Write-SweepConfig $cfg "daily_trail_$ts"
        $scenarios += @{ Name = "DailyTrail=$($ts)%"; Config = $path }
    }

    # DailyProfitTargetPercent sweep
    foreach ($dp in @(0.5, 1.0, 1.5, 2.0, 3.0)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DailyProfitTargetPercent" $dp
        $path = Write-SweepConfig $cfg "daily_target_$dp"
        $scenarios += @{ Name = "DailyTarget=$($dp)%"; Config = $path }
    }

    # DailyProfitTarget disabled
    $cfg = Read-Config
    Set-BaseProperty $cfg "DailyProfitTargetPercent" 0
    Set-BaseProperty $cfg "DailyProfitTargetRealtime" $false
    $path = Write-SweepConfig $cfg "daily_target_off"
    $scenarios += @{ Name = "DailyTarget=OFF"; Config = $path }

    # DailyLossLimitPercent sweep
    foreach ($ll in @(0, 1, 2, 3, 5)) {
        $cfg = Read-Config
        Set-BaseProperty $cfg "DailyLossLimitPercent" $ll
        $path = Write-SweepConfig $cfg "daily_loss_$ll"
        $scenarios += @{ Name = "DailyLoss=$($ll)%"; Config = $path }
    }

    return $scenarios
}

function Get-BoundaryScenarios {
    # Phase boundary exploration
    $scenarios = @()

    # OV end time sweep
    foreach ($end in @("09:40", "09:45", "09:50", "10:00", "10:15", "10:30")) {
        $cfg = Read-Config
        $ovRule = $cfg.TradingBot.TimeRules | Where-Object { $_.Name -eq "Open Volatility" }
        $ovRule | Add-Member -MemberType NoteProperty -Name "EndTime" -Value $end -Force
        $path = Write-SweepConfig $cfg "boundary_ov_end_$($end -replace ':','_')"
        $scenarios += @{ Name = "OV_End=$end"; Config = $path }
    }

    # Power Hour start time sweep
    foreach ($start in @("13:00", "13:30", "14:00", "14:30", "15:00")) {
        $cfg = Read-Config
        $phRule = $cfg.TradingBot.TimeRules | Where-Object { $_.Name -eq "Power Hour" }
        $phRule | Add-Member -MemberType NoteProperty -Name "StartTime" -Value $start -Force
        $path = Write-SweepConfig $cfg "boundary_ph_start_$($start -replace ':','_')"
        $scenarios += @{ Name = "PH_Start=$start"; Config = $path }
    }

    return $scenarios
}

# ═════════════════════════════════════════════════════════════════════════════
# ACTION: SWEEP — Run a parameter sweep for a specific phase and group
# ═════════════════════════════════════════════════════════════════════════════
function Run-Sweep {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

    # Phase for sweep runs
    $phaseKey = $Phase
    if ($Group -eq "daily" -or $Group -eq "boundaries") {
        $phaseKey = "full"
    }

    $phaseName = $PhaseWindows[$phaseKey].Name
    $sweepTitle = "Sweep: $Group — $phaseName"
    Print-Header $sweepTitle

    # First run current settings as baseline for this phase
    Write-Host "  [Baseline with current settings]" -ForegroundColor DarkCyan
    $baseResult = Run-Scenario -ScenarioName "CURRENT" -ConfigPath $BaseConfig `
                               -Dates $TargetDates -PhaseKey $phaseKey
    Write-Host ""

    # Generate and run scenarios
    $scenarios = switch ($Group) {
        "signal"     { Get-SignalScenarios -PhaseKey $Phase }
        "stops"      { Get-StopScenarios -PhaseKey $Phase }
        "exit"       { Get-ExitScenarios -PhaseKey $Phase }
        "trim"       { Get-TrimScenarios -PhaseKey $Phase }
        "direction"  { Get-DirectionScenarios -PhaseKey $Phase }
        "daily"      { Get-DailyScenarios }
        "boundaries" { Get-BoundaryScenarios }
    }

    $allResults = @($baseResult)

    $total = $scenarios.Count
    $i = 0
    foreach ($scenario in $scenarios) {
        $i++
        $pct = [math]::Round(($i / $total) * 100)
        Write-Host "  [$i/$total ($pct%)]" -ForegroundColor DarkCyan -NoNewline
        $result = Run-Scenario -ScenarioName $scenario.Name -ConfigPath $scenario.Config `
                               -Dates $TargetDates -PhaseKey $phaseKey
        $allResults += $result
    }

    # Sort by total P/L and show ranking
    Write-Host ""
    Write-Host "  === RANKING (by Total P/L) ===" -ForegroundColor Cyan
    $ranked = $allResults | Sort-Object { $_.TotalPnL } -Descending
    $rank = 0
    foreach ($r in $ranked) {
        $rank++
        $color = Get-Color "$($r.TotalPnL)"
        $marker = if ($r.Scenario -eq "CURRENT") { " <<<" } else { "" }
        Write-Host ("  #{0,-3} {1,-40} | {2,10} | {3,8} | {4,6}{5}" -f $rank, $r.Scenario, (Format-PnL "$($r.TotalPnL)"), (Format-PnL "$($r.AvgPnL)"), $r.TotalTrades, $marker) -ForegroundColor $color
    }

    # Save results
    Write-ResultsCsv $allResults "$($Group)_$($Phase)_$timestamp"

    Write-Host "`n=== Sweep Complete ===" -ForegroundColor Cyan
}

# ═════════════════════════════════════════════════════════════════════════════
# ACTION: CUSTOM — Test a specific config file
# ═════════════════════════════════════════════════════════════════════════════
function Run-Custom {
    if ($ConfigFile -eq "") {
        Write-Error "Must specify -ConfigFile for custom action"
        return
    }

    if (-not (Test-Path $ConfigFile)) {
        Write-Error "Config file not found: $ConfigFile"
        return
    }

    Print-Header "Custom Config Test — $(Split-Path $ConfigFile -Leaf)"
    foreach ($phaseKey in @("ov", "base", "ph", "full")) {
        $phaseName = $PhaseWindows[$phaseKey].Name
        Write-Host "  [$phaseName]" -ForegroundColor DarkCyan
        Run-Scenario -ScenarioName $phaseName -ConfigPath $ConfigFile `
                     -Dates $TargetDates -PhaseKey $phaseKey
    }
    Write-Host "`n=== Custom Test Complete ===" -ForegroundColor Cyan
}

# ═════════════════════════════════════════════════════════════════════════════
# MAIN DISPATCH
# ═════════════════════════════════════════════════════════════════════════════
Set-Location $ProjectDir

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  qqqBot Parameter Sweep Harness                 ║" -ForegroundColor Cyan
Write-Host "║  Action: $($Action.PadRight(40))║" -ForegroundColor Cyan
if ($Action -eq "sweep") {
Write-Host "║  Phase:  $($Phase.PadRight(40))║" -ForegroundColor Cyan
Write-Host "║  Group:  $($Group.PadRight(40))║" -ForegroundColor Cyan
}
Write-Host "║  Dates:  $((($TargetDates -join ', ')).PadRight(40))║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan

switch ($Action) {
    "baseline" { Run-Baselines }
    "sweep"    { Run-Sweep }
    "custom"   { Run-Custom }
}
