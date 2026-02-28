$ErrorActionPreference = "Stop"
$baseCfgPath = "$PSScriptRoot\sweep_configs\baseline_30k.json"
$dates = "20260209-20260213,20260217-20260220,20260223-20260227"

function Run-Combined {
    param([string]$Name, [string]$OutName, [scriptblock]$ConfigMod)
    
    $cfg = Get-Content $baseCfgPath -Raw | ConvertFrom-Json
    & $ConfigMod $cfg
    $cfgPath = "$PSScriptRoot\sweep_configs\$OutName.json"
    $cfg | ConvertTo-Json -Depth 10 | Set-Content $cfgPath -Encoding UTF8
    
    $outDir = "$PSScriptRoot\sweep_results\$OutName"
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    
    Write-Host "`n=== $Name ===" -ForegroundColor Yellow
    dotnet run --project $PSScriptRoot -- --mode=parallel-replay --dates=$dates --parallelism=8 --speed=0 "-config=$cfgPath" "--output=$outDir\results.csv" "--output-dir=$outDir" 2>&1 | ForEach-Object {
        $line = $_.ToString()
        if ($line -match "Mean P/L|Median|Std Dev|Win Rate|mean=|SUMMARY") { Write-Host $line }
    }
    
    $csvPath = "$outDir\results.csv"
    if (Test-Path $csvPath) {
        $csv = Import-Csv $csvPath
        $meanPnL = [math]::Round(($csv | Measure-Object -Property RealizedPnL -Average).Average, 2)
        $wins = ($csv | Where-Object { [decimal]$_.RealizedPnL -gt 0 }).Count
        $wr = [math]::Round($wins / $csv.Count * 100, 1)
        Write-Host "  RESULT: Mean P/L = $meanPnL, Win Rate = $wr%" -ForegroundColor Cyan
        return [PSCustomObject]@{ Name = $Name; MeanPnL = $meanPnL; WinRate = $wr; Runs = $csv.Count }
    }
}

Write-Host "########################################" -ForegroundColor Green
Write-Host "# COMBINED BEST VALUES VALIDATION" -ForegroundColor Green
Write-Host "########################################" -ForegroundColor Green

$results = @()

# Combo 1: DPT=1.25% + DLL=0.75%
$r = Run-Combined -Name "DPT=1.25% + DLL=0.75%" -OutName "combo_dpt125_dll075" -ConfigMod {
    param($c) $c.TradingBot.DailyProfitTargetPercent = 1.25; $c.TradingBot.DailyLossLimitPercent = 0.75
}
if ($r) { $results += $r }

# Combo 2: DPT=1.25% + DLL=1.0%
$r = Run-Combined -Name "DPT=1.25% + DLL=1.0%" -OutName "combo_dpt125_dll100" -ConfigMod {
    param($c) $c.TradingBot.DailyProfitTargetPercent = 1.25; $c.TradingBot.DailyLossLimitPercent = 1.0
}
if ($r) { $results += $r }

# Combo 3: DPT=1.0% + DLL=0.75%
$r = Run-Combined -Name "DPT=1.0% + DLL=0.75%" -OutName "combo_dpt100_dll075" -ConfigMod {
    param($c) $c.TradingBot.DailyProfitTargetPercent = 1.0; $c.TradingBot.DailyLossLimitPercent = 0.75
}
if ($r) { $results += $r }

# Combo 4: DPT=1.0% + DLL=1.0%
$r = Run-Combined -Name "DPT=1.0% + DLL=1.0%" -OutName "combo_dpt100_dll100" -ConfigMod {
    param($c) $c.TradingBot.DailyProfitTargetPercent = 1.0; $c.TradingBot.DailyLossLimitPercent = 1.0
}
if ($r) { $results += $r }

# Combo 5: DPT=1.25% + DLL=1.25%
$r = Run-Combined -Name "DPT=1.25% + DLL=1.25%" -OutName "combo_dpt125_dll125" -ConfigMod {
    param($c) $c.TradingBot.DailyProfitTargetPercent = 1.25; $c.TradingBot.DailyLossLimitPercent = 1.25
}
if ($r) { $results += $r }

Write-Host "`n=== COMBINED VALIDATION SUMMARY ===" -ForegroundColor Green
$results | Format-Table -AutoSize
