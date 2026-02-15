$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$dates = @("20260209","20260210","20260211","20260212","20260213")

Write-Host ""
Write-Host "=== CONTINUOUS DAY: DETAILED PHASE P/L (OV=10:13, PH=14:00) ==="
Write-Host ""

foreach ($date in $dates) {
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    $lines = $output -split "`n"
    
    $dayLabel = $date.Substring(4,2) + "/" + $date.Substring(6,2)
    Write-Host "=== $dayLabel ==="
    
    # Find last OV status line, first Base line, last Base line before PH, first PH line, last line
    $lastOVLine = ""; $firstBaseLine = ""; $lastBaseLine = ""; $firstPHLine = ""
    $seenOVEnd = $false; $seenPHStart = $false
    $targetLines = @()
    
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        
        if ($trimmed -match '\[Open Volatility\].*QQQ:') {
            $lastOVLine = $trimmed
        }
        
        if ($trimmed -match 'PHASE TRANSITION: Open Volatility') {
            $seenOVEnd = $true
        }
        
        if ($seenOVEnd -and -not $seenPHStart -and $trimmed -match '^\[\d{2}:\d{2}:\d{2}\]' -and $trimmed -match 'QQQ:' -and $trimmed -notmatch '\[Open Volatility\]') {
            if (-not $firstBaseLine) { $firstBaseLine = $trimmed }
            $lastBaseLine = $trimmed
        }
        
        if ($trimmed -match 'PHASE TRANSITION.*Power Hour') {
            $seenPHStart = $true
        }
        
        if ($seenPHStart -and $trimmed -match '\[Power Hour\].*QQQ:') {
            if (-not $firstPHLine) { $firstPHLine = $trimmed }
        }
        
        if ($trimmed -match 'DAILY TARGET|daily.*target|STOP TRADING') {
            $targetLines += $trimmed
        }
    }
    
    # Extract short summary from each line
    function Get-Summary($line) {
        if (-not $line) { return "(no data)" }
        $sig = ""; if ($line -match '\|\s*(BULL|BEAR|NEUTRAL)\s*\|') { $sig = $Matches[1] }
        $pos = ""; if ($line -match '\|\s*(CASH|[A-Z]{3,4}\s*x\d+)\s*\|') { $pos = $Matches[1] }
        $day = ""; if ($line -match 'Day:\s*([+-]\$[\d,.]+)') { $day = $Matches[1] }
        $run = ""; if ($line -match 'Run:\s*([+-]\$[\d,.]+)') { $run = $Matches[1] }
        $time = ""; if ($line -match '^\[(\d{2}:\d{2}:\d{2})\]') { $time = $Matches[1] }
        return "$time $sig $pos Day=$day Unreal=$run"
    }
    
    Write-Host "  Last OV:      $(Get-Summary $lastOVLine)"
    Write-Host "  First Base:   $(Get-Summary $firstBaseLine)"
    Write-Host "  Last Base:    $(Get-Summary $lastBaseLine)"
    Write-Host "  First PH:     $(Get-Summary $firstPHLine)"
    
    foreach ($tl in $targetLines) {
        Write-Host "  TARGET: $tl"
    }
    
    # Compute phase P/L from Day values
    $ovPL = 0.0; $basePL = 0.0; $phPL = 0.0; $finalPL = 0.0
    
    function Get-DayPL($line) {
        if (-not $line) { return $null }
        if ($line -match 'Day:\s*([+-])\$?([\d,.]+)') {
            $val = [double]($Matches[2] -replace ",","")
            if ($Matches[1] -eq '-') { return (-$val) } else { return $val }
        }
        return $null
    }
    
    $plEndOV = Get-DayPL $lastOVLine
    $plStartBase = Get-DayPL $firstBaseLine
    $plEndBase = Get-DayPL $lastBaseLine
    $plStartPH = Get-DayPL $firstPHLine
    
    if ($output -match 'Realized P/L:\s*\$?([-\d,.]+)') { $finalPL = [double]($Matches[1] -replace ",","") }
    
    if ($null -eq $plEndOV) { $plEndOV = 0.0 }
    if ($null -eq $plEndBase) { $plEndBase = $plEndOV }
    
    $baseDelta = [math]::Round($plEndBase - $plEndOV, 2)
    $phDelta = [math]::Round($finalPL - $plEndBase, 2)
    
    Write-Host ""
    Write-Host "  OV contrib:   +`$$([math]::Round($plEndOV,2))"
    Write-Host "  Base contrib: $( if($baseDelta -ge 0){'+'}else{''} )`$$baseDelta"
    Write-Host "  PH contrib:   $( if($phDelta -ge 0){'+'}else{''} )`$$phDelta"
    Write-Host "  TOTAL:        +`$$([math]::Round($finalPL,2))"
    Write-Host ""
}
