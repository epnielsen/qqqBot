# macd-sweep.ps1 — MACD Momentum Layer parameter sweep harness for qqqBot
# Built on sweep.ps1 infrastructure. Uses appsettings.macd.json as base config.
# Corrected phase windows: OV=09:30-10:13, Base=10:13-14:00, PH=14:00-16:00
#
# Usage examples:
#   .\macd-sweep.ps1 -Action baseline                                # Compare no-MACD vs MACD-default across all phases
#   .\macd-sweep.ps1 -Action sweep -Phase full -Group periods        # MACD period grid search (Fast×Slow×Signal)
#   .\macd-sweep.ps1 -Action sweep -Phase full -Group roles          # Role isolation (7 combos)
#   .\macd-sweep.ps1 -Action sweep -Phase full -Group deadzone       # EntryGate DeadZone sweep
#   .\macd-sweep.ps1 -Action sweep -Phase full -Group boost          # TrendBoost threshold sweep
#   .\macd-sweep.ps1 -Action sweep -Phase full -Group accelerator    # ExitAccelerator wait sweep
#   .\macd-sweep.ps1 -Action sweep -Phase base -Group thresholds     # Combined threshold sweep for a phase
#   .\macd-sweep.ps1 -Action sweep -Phase ov -Group phase-tune       # Per-phase MACD override tuning
#   .\macd-sweep.ps1 -Action sweep -Phase base -Group rebase         # Re-optimize velocity/trend with MACD active
#   .\macd-sweep.ps1 -Action sweep -Phase base -Group gate-handoff    # Velocity×DeadZone cross-sweep (Gate replaces velocity)
#   .\macd-sweep.ps1 -Action custom -ConfigFile my.json              # One-off custom config test
#   .\macd-sweep.ps1 -Action sweep -Phase full -Group periods -Quick # Use only 2 dates for fast iteration

param(
    [ValidateSet("baseline", "sweep", "custom")]
    [string]$Action = "baseline",

    [ValidateSet("ov", "base", "ph", "full", "all")]
    [string]$Phase = "full",

    [ValidateSet("periods", "roles", "deadzone", "boost", "accelerator", "thresholds", "phase-tune", "rebase", "gate-handoff")]
    [string]$Group = "periods",

    [string]$Dates = "",         # Comma-separated: "20260209,20260211" or empty for all
    [string]$ConfigFile = "",    # For -Action custom
    [switch]$Quick,              # Use only 2 dates (Feb 11, 13) for fast iteration
    [switch]$Verbose             # Show raw replay output on errors
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ─── Configuration ───
$ProjectDir   = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$MacdConfigSrc  = Join-Path $ProjectDir "appsettings.macd.json"    # Source copy (for reading/parsing)
$NoMacdConfigSrc = Join-Path $ProjectDir "appsettings.json"        # Source copy (for reading/parsing)
$MacdConfigBin = "appsettings.macd.json"                           # Relative path in bin dir (for -config=)
$NoMacdConfigBin = "appsettings.json"                              # Relative path in bin dir (for -config=)
$BinDir       = Join-Path $ProjectDir "bin\Debug\net10.0"
$SweepDir     = Join-Path $BinDir "sweep_configs"                  # Sweep configs go directly in bin subdir
$ResultsDir   = Join-Path $ProjectDir "sweep_results"
$AllDates     = @("20260209", "20260210", "20260211", "20260212", "20260213")
$QuickDates   = @("20260211", "20260213")  # One clean day + barcoding day
$BarcodeDate  = "20260213"                  # Key barcoding test day

# Phase time windows — CORRECTED to match actual OV window (10:13, not 09:50)
$PhaseWindows = @{
    "ov"   = @{ Start = "09:30"; End = "10:13"; Name = "Open Volatility" }
    "base" = @{ Start = "10:13"; End = "14:00"; Name = "Base" }
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

function Read-MacdConfig {
    $raw = Get-Content $MacdConfigSrc -Raw
    return ($raw | ConvertFrom-Json)
}

function Read-NoMacdConfig {
    $raw = Get-Content $NoMacdConfigSrc -Raw
    return ($raw | ConvertFrom-Json)
}

function Write-SweepConfig {
    param([object]$Config, [string]$Name)
    $path = Join-Path $SweepDir "macd_$Name.json"
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
    if ($null -eq $rule) {
        Write-Warning "Phase '$PhaseName' not found in TimeRules"
        return
    }
    $rule.Overrides | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-MacdProperty {
    # Set a property on the base Macd config section
    param([object]$Config, [string]$Property, $Value)
    if ($null -eq $Config.TradingBot.Macd) {
        $Config.TradingBot | Add-Member -MemberType NoteProperty -Name "Macd" -Value ([PSCustomObject]@{}) -Force
    }
    $Config.TradingBot.Macd | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Set-MacdPhaseOverride {
    # Set a MACD-related override on a specific TimeRules phase
    # Uses the flattened property names: MacdEnabled, MacdTrendBoostEnabled, etc.
    param([object]$Config, [string]$PhaseName, [string]$Property, $Value)
    $rule = $Config.TradingBot.TimeRules | Where-Object { $_.Name -eq $PhaseName }
    if ($null -eq $rule) {
        Write-Warning "Phase '$PhaseName' not found in TimeRules"
        return
    }
    $rule.Overrides | Add-Member -MemberType NoteProperty -Name $Property -Value $Value -Force
}

function Run-Replay {
    param(
        [string]$ConfigPath,
        [string]$Date,
        [string]$StartTime = $null,
        [string]$EndTime = $null
    )

    # Build the config argument — use relative path from bin dir for sweep configs,
    # or just the filename for base configs (already in bin dir)
    $args_list = @("run", "--", "--mode=replay", "--date=$Date", "--speed=0")
    if ($StartTime) { $args_list += "--start-time=$StartTime" }
    if ($EndTime)   { $args_list += "--end-time=$EndTime" }

    # For config path: if it's in the sweep dir under bin, make it relative to bin
    # Otherwise, use the filename directly (appsettings.json, appsettings.macd.json are in bin root)
    if ($ConfigPath.StartsWith($BinDir)) {
        $relConfig = $ConfigPath.Substring($BinDir.Length + 1)
    } else {
        $relConfig = Split-Path $ConfigPath -Leaf
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # Use Invoke-Expression with quoted -config to handle dotted filenames
    $argsString = ($args_list -join " ") + " `"-config=$relConfig`""
    $output = Invoke-Expression "dotnet $argsString 2>&1" | ForEach-Object { "$_" }
    $exitCode = $LASTEXITCODE
    $sw.Stop()

    $result = @{
        PnL       = $null
        ReturnPct = $null
        Trades    = $null
        PeakPnL   = $null
        TroughPnL = $null
        PeakTime  = $null
        TroughTime = $null
        Runtime   = [int]$sw.Elapsed.TotalSeconds
        ExitCode  = $exitCode
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

    # Show raw output on errors if verbose mode
    if ($exitCode -ne 0 -and $Verbose) {
        Write-Host "    [ERROR] Exit code: $exitCode" -ForegroundColor Red
        $output | Select-Object -Last 20 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkRed }
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
        [string]$PhaseKey
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

    Write-Host ("  {0,-45} | {1,10} | {2,8} | {3,6} |" -f $ScenarioName, (Format-PnL "$totalPnL"), (Format-PnL "$avgPnL"), $totalTrades) -ForegroundColor $color

    # Per-day breakdown
    foreach ($day in $perDay) {
        $dayColor = Get-Color $day.PnL
        $dpnl = Format-PnL $day.PnL
        Write-Host ("    {0}: {1,10} | {2,6} trades | Peak: {3,8} | Trough: {4,8} | {5}s" -f $day.Date, $dpnl, $day.Trades, (Format-PnL $day.PeakPnL), (Format-PnL $day.TroughPnL), $day.Runtime) -ForegroundColor DarkGray
    }

    return @{
        Scenario    = $ScenarioName
        TotalPnL    = $totalPnL
        AvgPnL      = $avgPnL
        TotalTrades = $totalTrades
        PerDay      = $perDay
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
    Write-Host ("  {0,-45} | {1,10} | {2,8} | {3,6} |" -f "Scenario", "Total P/L", "Avg P/L", "Trades") -ForegroundColor Yellow
    Write-Host ("  " + ("-" * 80)) -ForegroundColor Yellow
}

function Print-Ranking {
    param([array]$Results, [string]$BaselineLabel = "CURRENT")
    Write-Host ""
    Write-Host "  === RANKING (by Total P/L) ===" -ForegroundColor Cyan
    $ranked = $Results | Sort-Object { $_.TotalPnL } -Descending
    $rank = 0
    foreach ($r in $ranked) {
        $rank++
        $color = Get-Color "$($r.TotalPnL)"
        $marker = if ($r.Scenario -eq $BaselineLabel) { " <<<" } else { "" }
        Write-Host ("  #{0,-3} {1,-45} | {2,10} | {3,8} | {4,6}{5}" -f $rank, $r.Scenario, (Format-PnL "$($r.TotalPnL)"), (Format-PnL "$($r.AvgPnL)"), $r.TotalTrades, $marker) -ForegroundColor $color
    }
}

# ═════════════════════════════════════════════════════════════════════════════
# ACTION: BASELINE — Compare no-MACD vs MACD-default across all phases
# ═════════════════════════════════════════════════════════════════════════════
function Run-Baselines {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

    $allResults = @()

    foreach ($phaseKey in @("ov", "base", "ph", "full")) {
        $phaseName = $PhaseWindows[$phaseKey].Name
        Print-Header "Baseline — $phaseName"

        # No-MACD baseline (appsettings.json)
        $result1 = Run-Scenario -ScenarioName "NO-MACD (appsettings.json)" -ConfigPath $NoMacdConfigBin `
                                -Dates $TargetDates -PhaseKey $phaseKey
        $allResults += $result1

        # MACD-default baseline (appsettings.macd.json)
        $result2 = Run-Scenario -ScenarioName "MACD-DEFAULT (all roles on)" -ConfigPath $MacdConfigBin `
                                -Dates $TargetDates -PhaseKey $phaseKey
        $allResults += $result2

        # Delta
        $delta = $result2.TotalPnL - $result1.TotalPnL
        $deltaColor = if ($delta -ge 0) { "Green" } else { "Red" }
        Write-Host ("  DELTA: {0}" -f (Format-PnL "$([math]::Round($delta, 2))")) -ForegroundColor $deltaColor
    }

    # Save results
    $csvPath = Join-Path $ResultsDir "macd_baseline_$timestamp.csv"
    $rows = @()
    foreach ($r in $allResults) {
        foreach ($day in $r.PerDay) {
            $rows += [PSCustomObject]@{
                Scenario  = $r.Scenario
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
# SWEEP GENERATORS — MACD-specific parameter variations
# ═════════════════════════════════════════════════════════════════════════════

function Get-PeriodScenarios {
    # Full grid search: Fast × Slow × Signal where Fast < Slow
    $scenarios = @()

    $fastValues   = @(180, 300, 600, 900)
    $slowValues   = @(480, 720, 1200, 1800)
    $signalValues = @(60, 120, 180, 300)

    foreach ($fast in $fastValues) {
        foreach ($slow in $slowValues) {
            if ($fast -ge $slow) { continue }  # Fast must be < Slow
            foreach ($signal in $signalValues) {
                $cfg = Read-MacdConfig
                Set-MacdProperty $cfg "FastPeriod" $fast
                Set-MacdProperty $cfg "SlowPeriod" $slow
                Set-MacdProperty $cfg "SignalPeriod" $signal
                $name = "F${fast}_S${slow}_Sig${signal}"
                $path = Write-SweepConfig $cfg "period_$name"
                $warmup = $slow + $signal
                $scenarios += @{ Name = "F=$fast S=$slow Sig=$signal (warm=${warmup}s)"; Config = $path }
            }
        }
    }

    Write-Host "  Period grid: $($scenarios.Count) valid combos (Fast<Slow constraint)" -ForegroundColor DarkGray
    return $scenarios
}

function Get-RoleScenarios {
    # 7 combinations of 3 independent roles
    $scenarios = @()

    $roleCombos = @(
        @{ Name = "Boost-ONLY";        Boost = $true;  Accel = $false; Gate = $false },
        @{ Name = "Accel-ONLY";        Boost = $false; Accel = $true;  Gate = $false },
        @{ Name = "Gate-ONLY";         Boost = $false; Accel = $false; Gate = $true  },
        @{ Name = "Boost+Accel";       Boost = $true;  Accel = $true;  Gate = $false },
        @{ Name = "Boost+Gate";        Boost = $true;  Accel = $false; Gate = $true  },
        @{ Name = "Accel+Gate";        Boost = $false; Accel = $true;  Gate = $true  },
        @{ Name = "ALL-ON";            Boost = $true;  Accel = $true;  Gate = $true  }
    )

    foreach ($combo in $roleCombos) {
        $cfg = Read-MacdConfig
        Set-MacdProperty $cfg "TrendBoostEnabled" $combo.Boost
        Set-MacdProperty $cfg "ExitAcceleratorEnabled" $combo.Accel
        Set-MacdProperty $cfg "EntryGateEnabled" $combo.Gate
        $path = Write-SweepConfig $cfg "role_$($combo.Name)"
        $scenarios += @{ Name = $combo.Name; Config = $path }
    }

    return $scenarios
}

function Get-DeadzoneScenarios {
    $scenarios = @()
    foreach ($dz in @(0.005, 0.01, 0.015, 0.02, 0.03, 0.04, 0.05, 0.08)) {
        # Gate-ONLY: disable Boost and Accel to isolate the gate effect
        $cfg = Read-MacdConfig
        Set-MacdProperty $cfg "TrendBoostEnabled" $false
        Set-MacdProperty $cfg "ExitAcceleratorEnabled" $false
        Set-MacdProperty $cfg "EntryGateDeadZone" $dz
        $path = Write-SweepConfig $cfg "deadzone_gateonly_$dz"
        $scenarios += @{ Name = "Gate-Only DZ=$dz"; Config = $path }
    }
    # Also test with Boost+Gate at various dead zones to see interaction
    foreach ($dz in @(0.03, 0.05, 0.08, 0.12, 0.15, 0.20)) {
        $cfg = Read-MacdConfig
        Set-MacdProperty $cfg "ExitAcceleratorEnabled" $false
        Set-MacdProperty $cfg "EntryGateDeadZone" $dz
        $path = Write-SweepConfig $cfg "deadzone_boostgate_$dz"
        $scenarios += @{ Name = "Boost+Gate DZ=$dz"; Config = $path }
    }
    return $scenarios
}

function Get-BoostScenarios {
    $scenarios = @()
    foreach ($bt in @(0.005, 0.01, 0.02, 0.03, 0.05, 0.08, 0.12)) {
        $cfg = Read-MacdConfig
        Set-MacdProperty $cfg "TrendBoostThreshold" $bt
        $path = Write-SweepConfig $cfg "boost_$bt"
        $scenarios += @{ Name = "BoostThresh=$bt"; Config = $path }
    }
    return $scenarios
}

function Get-AcceleratorScenarios {
    $scenarios = @()
    # Accel-ONLY (no boost, no gate) to isolate acceleration effect
    foreach ($ws in @(5, 10, 15, 20, 30, 45, 60, 90)) {
        $cfg = Read-MacdConfig
        Set-MacdProperty $cfg "TrendBoostEnabled" $false
        Set-MacdProperty $cfg "EntryGateEnabled" $false
        Set-MacdProperty $cfg "ExitAcceleratorWaitSeconds" $ws
        $path = Write-SweepConfig $cfg "accel_only_${ws}s"
        $scenarios += @{ Name = "Accel-Only Wait=${ws}s"; Config = $path }
    }
    return $scenarios
}

function Get-GateHandoffScenarios {
    # Cross-sweep: Lower velocity × EntryGate dead zone
    # Hypothesis: if we relax velocity (allow more entries), MACD EntryGate can handle barcoding filtering
    # This tests whether the MACD gate can be a BETTER barcoding filter than raw velocity
    # Gate-ONLY mode (no Boost/Accel) to isolate the handoff effect
    param([string]$PhaseKey)

    $scenarios = @()

    # First: no-MACD at various velocities (baseline for comparison)
    $velocities = @(0.000004, 0.000006, 0.000008, 0.000010, 0.000012, 0.000015)
    foreach ($v in $velocities) {
        $cfg = Read-NoMacdConfig
        if ($PhaseKey -eq "base" -or $PhaseKey -eq "full") {
            Set-BaseProperty $cfg "MinVelocityThreshold" $v
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "MinVelocityThreshold" $v
        }
        $path = Write-SweepConfig $cfg "handoff_nomacd_vel_${v}"
        $scenarios += @{ Name = "NoMACD Vel=$v"; Config = $path }
    }

    # Then: Gate-ONLY at each velocity × dead zone combination
    $deadZones = @(0.01, 0.02, 0.03, 0.05, 0.08, 0.12)
    foreach ($v in $velocities) {
        foreach ($dz in $deadZones) {
            $cfg = Read-MacdConfig
            Set-MacdProperty $cfg "TrendBoostEnabled" $false
            Set-MacdProperty $cfg "ExitAcceleratorEnabled" $false
            Set-MacdProperty $cfg "EntryGateEnabled" $true
            Set-MacdProperty $cfg "EntryGateDeadZone" $dz
            if ($PhaseKey -eq "base" -or $PhaseKey -eq "full") {
                Set-BaseProperty $cfg "MinVelocityThreshold" $v
            } else {
                $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
                Set-PhaseOverride $cfg $phaseName "MinVelocityThreshold" $v
            }
            $name = "vel${v}_dz${dz}"
            $path = Write-SweepConfig $cfg "handoff_gate_$name"
            $scenarios += @{ Name = "Gate V=$v DZ=$dz"; Config = $path }
        }
    }

    Write-Host "  Gate Handoff grid: $($scenarios.Count) configs ($($velocities.Count) vel × (1 nomacd + $($deadZones.Count) deadzone))" -ForegroundColor DarkGray
    return $scenarios
}

function Get-ThresholdScenarios {
    # Combined sweep: DeadZone × BoostThreshold (important combos)
    $scenarios = @()

    $deadZones = @(0.01, 0.02, 0.03, 0.05)
    $boosts    = @(0.01, 0.03, 0.05, 0.08)
    $accels    = @(10, 15, 30)

    foreach ($dz in $deadZones) {
        foreach ($bt in $boosts) {
            foreach ($aw in $accels) {
                $cfg = Read-MacdConfig
                Set-MacdProperty $cfg "EntryGateDeadZone" $dz
                Set-MacdProperty $cfg "TrendBoostThreshold" $bt
                Set-MacdProperty $cfg "ExitAcceleratorWaitSeconds" $aw
                $name = "DZ${dz}_BT${bt}_AW${aw}"
                $path = Write-SweepConfig $cfg "thresh_$name"
                $scenarios += @{ Name = "DZ=$dz BT=$bt AW=${aw}s"; Config = $path }
            }
        }
    }

    Write-Host "  Threshold grid: $($scenarios.Count) combos" -ForegroundColor DarkGray
    return $scenarios
}

function Get-PhaseTuneScenarios {
    param([string]$PhaseKey)

    $scenarios = @()
    $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } elseif ($PhaseKey -eq "ph") { "Power Hour" } else { $null }

    if ($PhaseKey -eq "ov") {
        # OV: Test enabling/disabling entry gate (currently disabled due to warm-up)
        foreach ($gate in @($true, $false)) {
            $cfg = Read-MacdConfig
            Set-MacdPhaseOverride $cfg "Open Volatility" "MacdEntryGateEnabled" $gate
            $path = Write-SweepConfig $cfg "phase_ov_gate_$gate"
            $scenarios += @{ Name = "OV_EntryGate=$gate"; Config = $path }
        }

        # OV: Boost threshold variations (may want aggressive rescue during high-vol)
        foreach ($bt in @(0.01, 0.02, 0.03, 0.05, 0.08)) {
            $cfg = Read-MacdConfig
            Set-MacdPhaseOverride $cfg "Open Volatility" "MacdTrendBoostThreshold" $bt
            $path = Write-SweepConfig $cfg "phase_ov_boost_$bt"
            $scenarios += @{ Name = "OV_BoostThresh=$bt"; Config = $path }
        }

        # OV: Exit accelerator wait (faster exits during volatile opens)
        foreach ($aw in @(5, 10, 15, 30)) {
            $cfg = Read-MacdConfig
            Set-MacdPhaseOverride $cfg "Open Volatility" "MacdExitAcceleratorWaitSeconds" $aw
            $path = Write-SweepConfig $cfg "phase_ov_accel_${aw}s"
            $scenarios += @{ Name = "OV_AccelWait=${aw}s"; Config = $path }
        }

        # OV: Disable all MACD for OV phase (keep for Base/PH only)
        $cfg = Read-MacdConfig
        Set-MacdPhaseOverride $cfg "Open Volatility" "MacdEnabled" $false
        $path = Write-SweepConfig $cfg "phase_ov_macd_off"
        $scenarios += @{ Name = "OV_MACD=OFF"; Config = $path }

    } elseif ($PhaseKey -eq "base") {
        # Base: DeadZone tuning (primary barcoding defense)
        foreach ($dz in @(0.005, 0.01, 0.015, 0.02, 0.03, 0.05)) {
            $cfg = Read-MacdConfig
            Set-MacdPhaseOverride $cfg $null "MacdEntryGateDeadZone" $null  # This sets on base
            # Actually for base phase, we set the base Macd property since base=default
            Set-MacdProperty $cfg "EntryGateDeadZone" $dz
            $path = Write-SweepConfig $cfg "phase_base_dz_$dz"
            $scenarios += @{ Name = "Base_DZ=$dz"; Config = $path }
        }

        # Base: Different accelerator waits (base has 180s TrendWait — lots of room)
        foreach ($aw in @(5, 10, 15, 30, 45, 60)) {
            $cfg = Read-MacdConfig
            Set-MacdProperty $cfg "ExitAcceleratorWaitSeconds" $aw
            $path = Write-SweepConfig $cfg "phase_base_accel_${aw}s"
            $scenarios += @{ Name = "Base_AccelWait=${aw}s"; Config = $path }
        }

        # Base: Boost threshold (conservative OK since base has high velocity filter)
        foreach ($bt in @(0.01, 0.02, 0.03, 0.05, 0.08)) {
            $cfg = Read-MacdConfig
            Set-MacdProperty $cfg "TrendBoostThreshold" $bt
            $path = Write-SweepConfig $cfg "phase_base_boost_$bt"
            $scenarios += @{ Name = "Base_BoostThresh=$bt"; Config = $path }
        }

    } elseif ($PhaseKey -eq "ph") {
        # PH: Dead zone (afternoon chop filtering)
        foreach ($dz in @(0.01, 0.02, 0.03, 0.05, 0.08)) {
            $cfg = Read-MacdConfig
            Set-MacdPhaseOverride $cfg "Power Hour" "MacdEntryGateDeadZone" $dz
            $path = Write-SweepConfig $cfg "phase_ph_dz_$dz"
            $scenarios += @{ Name = "PH_DZ=$dz"; Config = $path }
        }

        # PH: Enable/disable individual roles
        foreach ($role in @("Boost", "Accel", "Gate")) {
            foreach ($val in @($true, $false)) {
                $cfg = Read-MacdConfig
                $propName = switch ($role) {
                    "Boost" { "MacdTrendBoostEnabled" }
                    "Accel" { "MacdExitAcceleratorEnabled" }
                    "Gate"  { "MacdEntryGateEnabled" }
                }
                Set-MacdPhaseOverride $cfg "Power Hour" $propName $val
                $path = Write-SweepConfig $cfg "phase_ph_${role}_$val"
                $scenarios += @{ Name = "PH_${role}=$val"; Config = $path }
            }
        }

        # PH: Disable all MACD for PH phase 
        $cfg = Read-MacdConfig
        Set-MacdPhaseOverride $cfg "Power Hour" "MacdEnabled" $false
        $path = Write-SweepConfig $cfg "phase_ph_macd_off"
        $scenarios += @{ Name = "PH_MACD=OFF"; Config = $path }
    }

    return $scenarios
}

function Get-RebaseScenarios {
    # Re-optimize base trading settings with MACD active
    param([string]$PhaseKey)

    $scenarios = @()

    # MinVelocityThreshold — with MACD gate, may be able to lower
    $velocities = switch ($PhaseKey) {
        "ov"   { @(0.000008, 0.000010, 0.000012, 0.000015, 0.000020, 0.000025) }
        "base" { @(0.000004, 0.000006, 0.000008, 0.000010, 0.000012, 0.000015, 0.000020) }
        "ph"   { @(0.000008, 0.000010, 0.000015, 0.000020) }
        default { @(0.000008, 0.000010, 0.000012, 0.000015, 0.000020) }
    }
    foreach ($v in $velocities) {
        $cfg = Read-MacdConfig
        if ($PhaseKey -eq "base" -or $PhaseKey -eq "full") {
            Set-BaseProperty $cfg "MinVelocityThreshold" $v
        } else {
            $phaseName = if ($PhaseKey -eq "ov") { "Open Volatility" } else { "Power Hour" }
            Set-PhaseOverride $cfg $phaseName "MinVelocityThreshold" $v
        }
        $path = Write-SweepConfig $cfg "rebase_vel_$($PhaseKey)_$v"
        $scenarios += @{ Name = "Velocity=$v"; Config = $path }
    }

    # TrendWindowSeconds — with MACD, may be able to shorten
    if ($PhaseKey -eq "base" -or $PhaseKey -eq "full") {
        foreach ($t in @(1200, 1800, 2400, 3600, 5400, 7200)) {
            $cfg = Read-MacdConfig
            Set-BaseProperty $cfg "TrendWindowSeconds" $t
            $path = Write-SweepConfig $cfg "rebase_trend_$($PhaseKey)_$t"
            $scenarios += @{ Name = "TrendWindow=$($t)s"; Config = $path }
        }
    }

    # SMAWindowSeconds
    if ($PhaseKey -eq "base" -or $PhaseKey -eq "full") {
        foreach ($s in @(120, 150, 180, 240, 300)) {
            $cfg = Read-MacdConfig
            Set-BaseProperty $cfg "SMAWindowSeconds" $s
            $path = Write-SweepConfig $cfg "rebase_sma_$($PhaseKey)_$s"
            $scenarios += @{ Name = "SMA=$($s)s"; Config = $path }
        }
    }

    # Combined velocity + trend (top combos)
    if ($PhaseKey -eq "base" -or $PhaseKey -eq "full") {
        foreach ($v in @(0.000008, 0.000010, 0.000015)) {
            foreach ($t in @(1800, 3600, 5400)) {
                $cfg = Read-MacdConfig
                Set-BaseProperty $cfg "MinVelocityThreshold" $v
                Set-BaseProperty $cfg "TrendWindowSeconds" $t
                $path = Write-SweepConfig $cfg "rebase_vt_$($PhaseKey)_${v}_${t}"
                $scenarios += @{ Name = "Vel=$v Trend=$($t)s"; Config = $path }
            }
        }
    }

    return $scenarios
}

# ═════════════════════════════════════════════════════════════════════════════
# ACTION: SWEEP — Run a parameter sweep for a specific phase and group
# ═════════════════════════════════════════════════════════════════════════════
function Run-Sweep {
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

    # Determine phase for sweep runs
    $phaseKey = $Phase
    if ($phaseKey -eq "all") { $phaseKey = "full" }

    $phaseName = $PhaseWindows[$phaseKey].Name
    $sweepTitle = "MACD Sweep: $Group — $phaseName"
    Print-Header $sweepTitle

    # Run MACD-default as baseline
    Write-Host "  [Baseline: MACD-default config]" -ForegroundColor DarkCyan
    $baseResult = Run-Scenario -ScenarioName "MACD-DEFAULT" -ConfigPath $MacdConfigBin `
                               -Dates $TargetDates -PhaseKey $phaseKey

    # Also run no-MACD for reference
    Write-Host "  [Reference: no-MACD config]" -ForegroundColor DarkCyan
    $refResult = Run-Scenario -ScenarioName "NO-MACD-REF" -ConfigPath $NoMacdConfigBin `
                              -Dates $TargetDates -PhaseKey $phaseKey
    Write-Host ""

    # Generate scenarios
    $scenarios = switch ($Group) {
        "periods"     { Get-PeriodScenarios }
        "roles"       { Get-RoleScenarios }
        "deadzone"    { Get-DeadzoneScenarios }
        "boost"       { Get-BoostScenarios }
        "accelerator" { Get-AcceleratorScenarios }
        "thresholds"  { Get-ThresholdScenarios }
        "phase-tune"  { Get-PhaseTuneScenarios -PhaseKey $Phase }
        "rebase"      { Get-RebaseScenarios -PhaseKey $Phase }
        "gate-handoff" { Get-GateHandoffScenarios -PhaseKey $Phase }
    }

    $allResults = @($baseResult, $refResult)

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

    # Ranking
    Print-Ranking $allResults "MACD-DEFAULT"

    # Save results
    Write-ResultsCsv $allResults "macd_$($Group)_$($Phase)_$timestamp"

    Write-Host "`n=== MACD Sweep Complete ===" -ForegroundColor Cyan
}

# ═════════════════════════════════════════════════════════════════════════════
# ACTION: CUSTOM — Test a specific config file across all phases
# ═════════════════════════════════════════════════════════════════════════════
function Run-Custom {
    if ($ConfigFile -eq "") {
        Write-Error "Must specify -ConfigFile for custom action. Example: -ConfigFile .\sweep_configs\macd_best.json"
        return
    }

    if (-not (Test-Path $ConfigFile)) {
        Write-Error "Config file not found: $ConfigFile"
        return
    }

    Print-Header "Custom Config Test — $(Split-Path $ConfigFile -Leaf)"

    $allResults = @()

    # Run no-MACD reference
    foreach ($phaseKey in @("ov", "base", "ph", "full")) {
        $phaseName = $PhaseWindows[$phaseKey].Name
        Write-Host "  [$phaseName]" -ForegroundColor DarkCyan

        $ref = Run-Scenario -ScenarioName "NO-MACD ($phaseName)" -ConfigPath $NoMacdConfigBin `
                            -Dates $TargetDates -PhaseKey $phaseKey
        $allResults += $ref

        $custom = Run-Scenario -ScenarioName "CUSTOM ($phaseName)" -ConfigPath $ConfigFile `
                               -Dates $TargetDates -PhaseKey $phaseKey
        $allResults += $custom

        $delta = $custom.TotalPnL - $ref.TotalPnL
        $deltaColor = if ($delta -ge 0) { "Green" } else { "Red" }
        Write-Host ("  DELTA: {0}" -f (Format-PnL "$([math]::Round($delta, 2))")) -ForegroundColor $deltaColor
        Write-Host ""
    }

    Write-Host "`n=== Custom Test Complete ===" -ForegroundColor Cyan
}

# ═════════════════════════════════════════════════════════════════════════════
# MAIN DISPATCH
# ═════════════════════════════════════════════════════════════════════════════
Set-Location $ProjectDir

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  qqqBot MACD Parameter Sweep Harness                    ║" -ForegroundColor Cyan
Write-Host "║  Base Config: appsettings.macd.json                     ║" -ForegroundColor Cyan
Write-Host "║  Action: $($Action.PadRight(47))║" -ForegroundColor Cyan
if ($Action -eq "sweep") {
Write-Host "║  Phase:  $($Phase.PadRight(47))║" -ForegroundColor Cyan
Write-Host "║  Group:  $($Group.PadRight(47))║" -ForegroundColor Cyan
}
Write-Host "║  Dates:  $((($TargetDates -join ', ')).PadRight(47))║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

switch ($Action) {
    "baseline" { Run-Baselines }
    "sweep"    { Run-Sweep }
    "custom"   { Run-Custom }
}
