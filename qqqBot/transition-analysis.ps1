$ErrorActionPreference = "Stop"
$projectDir = "c:\dev\TradeEcosystem\qqqBot\qqqBot"
$dates = @("20260209","20260210","20260211","20260212","20260213")

Write-Host "=== OV → Base Transition Analysis ==="
Write-Host "Extracting state at ~09:49-09:52 for each day"
Write-Host ""

foreach ($date in $dates) {
    Write-Host "========================================="
    Write-Host "  DATE: $date"
    Write-Host "========================================="
    
    $output = & dotnet run --project $projectDir -- --mode=replay --date=$date --speed=0 2>&1 | Out-String
    
    # Extract lines around 09:45-09:55 (transition window)
    $lines = $output -split "`n"
    
    Write-Host "--- Transition window (09:45-09:55) ---"
    foreach ($line in $lines) {
        # Match status lines in the transition window
        if ($line -match '\[09:4[5-9]:\d\d\]|\[09:5[0-5]:\d\d\]') {
            $trimmed = $line.Trim()
            if ($trimmed.Length -gt 10) {
                Write-Host $trimmed
            }
        }
        # Also catch PHASE TRANSITION lines
        if ($line -match 'PHASE TRANSITION|TIME RULES|Reconfigur') {
            Write-Host $line.Trim()
        }
    }
    
    # Extract key pre-transition status (last OV status line)
    Write-Host ""
    Write-Host "--- Last OV status line ---"
    $lastOV = ""
    foreach ($line in $lines) {
        if ($line -match '\[Open Volatility\]' -and $line -match '\[09:4[0-9]') {
            $lastOV = $line.Trim()
        }
    }
    if ($lastOV) { Write-Host $lastOV }
    
    # Extract first Base status line
    Write-Host "--- First Base status line ---"  
    $foundBase = $false
    foreach ($line in $lines) {
        if (-not $foundBase -and $line -match '\[09:5[0-9]:\d\d\]' -and $line -notmatch '\[Open Volatility\]' -and $line -match 'QQQ:') {
            Write-Host $line.Trim()
            $foundBase = $true
        }
    }
    
    # Extract trade summary
    Write-Host ""
    Write-Host "--- Summary ---"
    foreach ($line in $lines) {
        if ($line -match 'Realized P/L:|Total Trades:|Peak.*Equity|daily.*target|STOP TRADING') {
            Write-Host $line.Trim()
        }
    }
    
    # Look for any trades between 09:45-10:00
    Write-Host ""
    Write-Host "--- Trades 09:45-10:05 ---"
    foreach ($line in $lines) {
        if ($line -match '\[09:4[5-9]:\d\d\]|\[09:5\d:\d\d\]|\[10:0[0-5]:\d\d\]') {
            if ($line -match 'ENTERED|EXITED|BOUGHT|SOLD|LIQUIDAT|TRIM|Entry|Exit|Trade|SCALP|TREND') {
                Write-Host $line.Trim()
            }
        }
    }
    
    Write-Host ""
}
