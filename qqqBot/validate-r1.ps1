# validate-r1.ps1 — Validate R1 winner candidates on full 9-day dataset
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$SweepDir    = Join-Path $ProjectDir "sweep_configs"
$Dates = @("20260209","20260210","20260211","20260212","20260213","20260217","20260218","20260219","20260220")

function Run-SingleReplay {
    param([string]$Config, [string]$Date)
    $args2 = @("run", "--project", $ProjectDir, "--", "--mode=replay", "--date=$Date", "--speed=0", "-config=$Config")
    $output = & dotnet @args2 2>&1 | ForEach-Object { "$_" }
    $pnl = 0; $trades = 0
    foreach ($line in $output) {
        if ($line -match 'Realized P/L:\s+\$?([-\d,.]+)') { $pnl = [decimal]($matches[1] -replace ',','') }
        if ($line -match 'Total Trades:\s+(\d+)') { $trades = [int]$matches[1] }
    }
    return @{ PnL = $pnl; Trades = $trades }
}

function Validate-Config {
    param([string]$Name, [string]$ConfigPath)
    $totalPnL = 0; $totalTrades = 0
    $perDay = @()
    foreach ($d in $Dates) {
        $r = Run-SingleReplay -Config $ConfigPath -Date $d
        $totalPnL += $r.PnL
        $totalTrades += $r.Trades
        $perDay += "$d`: $($r.PnL)"
    }
    $color = if ($totalPnL -gt 0) { "Green" } else { "Red" }
    Write-Host ("{0,-30}: 9-day P/L = {1,10} | {2,4} trades" -f $Name, "`$$([math]::Round($totalPnL,2))", $totalTrades) -ForegroundColor $color
    Write-Host "  Per-day: $($perDay -join ' | ')" -ForegroundColor DarkGray
    return @{ Name = $Name; PnL = $totalPnL; Trades = $totalTrades }
}

Write-Host ""
Write-Host "=== R1 WINNER VALIDATION (9 dates) ===" -ForegroundColor Cyan
Write-Host ""

# CURRENT baseline
Validate-Config "CURRENT" "$ProjectDir\appsettings.json"

# SMA=210
$cfg = Get-Content "$ProjectDir\appsettings.json" -Raw | ConvertFrom-Json
$cfg.TradingBot | Add-Member -MemberType NoteProperty -Name "SMAWindowSeconds" -Value 210 -Force
$cfg | ConvertTo-Json -Depth 10 | Set-Content "$SweepDir\r1_val_sma210.json" -Encoding UTF8
Validate-Config "SMA=210" "$SweepDir\r1_val_sma210.json"

# SMA=240
$cfg = Get-Content "$ProjectDir\appsettings.json" -Raw | ConvertFrom-Json
$cfg.TradingBot | Add-Member -MemberType NoteProperty -Name "SMAWindowSeconds" -Value 240 -Force
$cfg | ConvertTo-Json -Depth 10 | Set-Content "$SweepDir\r1_val_sma240.json" -Encoding UTF8
Validate-Config "SMA=240" "$SweepDir\r1_val_sma240.json"

# SMA=210 + Chop=0.0008
$cfg = Get-Content "$ProjectDir\appsettings.json" -Raw | ConvertFrom-Json
$cfg.TradingBot | Add-Member -MemberType NoteProperty -Name "SMAWindowSeconds" -Value 210 -Force
$cfg.TradingBot | Add-Member -MemberType NoteProperty -Name "ChopThresholdPercent" -Value 0.0008 -Force
$cfg | ConvertTo-Json -Depth 10 | Set-Content "$SweepDir\r1_val_combo.json" -Encoding UTF8
Validate-Config "SMA=210+Chop=0.0008" "$SweepDir\r1_val_combo.json"

Write-Host ""
Write-Host "=== VALIDATION COMPLETE ===" -ForegroundColor Green
