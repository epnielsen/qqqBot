$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$dates = @("20260209","20260210","20260211","20260212","20260213")

Write-Host ""
Write-Host "=== CONTINUOUS DAY: P/L BY PHASE (OV boundary=10:13, PH boundary=14:00) ==="
Write-Host "Reading Day P/L at phase transitions from full-day replays (includes held positions)"
Write-Host ""
Write-Host ("{0,-8} | {1,16} | {2,16} | {3,16} | {4,10}" -f "Date", "OV (->10:13)", "Base (10:13->14)", "PH (14:00->EOD)", "Full Day")
Write-Host ("-" * 85)

foreach ($date in $dates) {
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $lines = $output -split "`n"
    
    # Find Day P/L at key timestamps
    # We want the LAST status line before or at the phase boundary
    $plAtOVEnd = $null      # Last line with [Open Volatility] tag
    $plAtPHStart = $null    # Last line before [Power Hour] tag appears
    $plFinal = $null
    $tradesAtOVEnd = $null
    $tradesAtPHStart = $null
    $tradesFinal = $null
    
    # Track trade count by watching for trade executions
    $lastDayPL = 0.0
    $lastSignalAndPos = ""
    
    # Strategy: find last status line in each phase
    $lastOVLine = ""
    $lastBaseLineBeforePH = ""
    $lastLine = ""
    $inPH = $false
    $seenPHTransition = $false
    
    foreach ($line in $lines) {
        if ($line -match '\[Open Volatility\].*Day:\s*([+-])\$?([\d,.]+)') {
            $sign = $Matches[1]; $val = [double]($Matches[2] -replace ",","")
            $plAtOVEnd = if ($sign -eq '-') { -$val } else { $val }
            $lastOVLine = $line
        }
        
        # Base phase lines: have a timestamp, QQQ:, Day:, but no [Open Volatility] or [Power Hour]
        if ($line -match '^\s*\[\d{2}:\d{2}:\d{2}\]' -and $line -notmatch '\[Open Volatility\]' -and $line -notmatch '\[Power Hour\]' -and $line -match 'Day:\s*([+-])\$?([\d,.]+)' -and -not $seenPHTransition) {
            $sign = $Matches[1]; $val = [double]($Matches[2] -replace ",","")
            $plAtPHStart = if ($sign -eq '-') { -$val } else { $val }
            $lastBaseLineBeforePH = $line
        }
        
        if ($line -match 'PHASE TRANSITION.*Power Hour') {
            $seenPHTransition = $true
        }
        
        # Capture any line with Day P/L as potential final
        if ($line -match 'Day:\s*([+-])\$?([\d,.]+)') {
            $sign = $Matches[1]; $val = [double]($Matches[2] -replace ",","")
            $plFinal = if ($sign -eq '-') { -$val } else { $val }
        }
    }
    
    # Also grab from Realized P/L for final
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') {
        $plFinal = [double]($Matches[1] -replace ",","")
    }
    
    # If no Base lines found (e.g. stopped during OV), plAtPHStart = plAtOVEnd
    if ($null -eq $plAtPHStart) { $plAtPHStart = $plAtOVEnd }
    if ($null -eq $plAtOVEnd) { $plAtOVEnd = 0.0 }
    if ($null -eq $plAtPHStart) { $plAtPHStart = $plAtOVEnd }
    if ($null -eq $plFinal) { $plFinal = $plAtPHStart }
    
    $ovPL = [math]::Round($plAtOVEnd, 2)
    $basePL = [math]::Round($plAtPHStart - $plAtOVEnd, 2)
    $phPL = [math]::Round($plFinal - $plAtPHStart, 2)
    $totalPL = [math]::Round($plFinal, 2)
    
    $dayLabel = $date.Substring(4,2) + "/" + $date.Substring(6,2)
    
    $fmtOV = if ($ovPL -ge 0) { "+`$$ovPL" } else { "-`$$([math]::Abs($ovPL))" }
    $fmtBase = if ($basePL -ge 0) { "+`$$basePL" } else { "-`$$([math]::Abs($basePL))" }
    $fmtPH = if ($phPL -ge 0) { "+`$$phPL" } else { "-`$$([math]::Abs($phPL))" }
    $fmtTotal = if ($totalPL -ge 0) { "+`$$totalPL" } else { "-`$$([math]::Abs($totalPL))" }

    Write-Host ("{0,-8} | {1,16} | {2,16} | {3,16} | {4,10}" -f $dayLabel, $fmtOV, $fmtBase, $fmtPH, $fmtTotal)
}

Write-Host ""
