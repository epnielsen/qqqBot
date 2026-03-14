<#
.SYNOPSIS
    Downloads historical 1-minute bar data for QQQ, TQQQ, SQQQ from Alpaca.

.DESCRIPTION
    Iterates over all trading days in the specified date range, calling
    "dotnet run -- --fetch-history --date=YYYYMMDD --symbols=QQQ,TQQQ,SQQQ"
    for each day. Skips weekends, known US market holidays, and dates whose
    CSV files already exist in the market data directory.

    Designed for the Alpaca Free (paper) API tier (200 req/min).
    Pacing: 200ms between dates, 10s pause every 100 dates.

.PARAMETER StartDate
    First calendar date to fetch (inclusive). Default: 2025-01-02.

.PARAMETER EndDate
    Last calendar date to fetch (inclusive). Default: 2026-02-05.

.PARAMETER DataDir
    Directory where market data CSVs are stored.
    Default: C:\dev\TradeEcosystem\data\market

.PARAMETER Symbols
    Comma-separated list of symbols. Default: QQQ,TQQQ,SQQQ

.PARAMETER PacingMs
    Milliseconds to sleep between each date fetch. Default: 200.

.PARAMETER BatchSize
    Number of dates between longer pauses. Default: 100.

.PARAMETER BatchPauseSeconds
    Seconds to pause every BatchSize dates. Default: 10.

.PARAMETER DryRun
    If set, prints what would be fetched without actually downloading.

.EXAMPLE
    # Full backfill (2025 + early 2026)
    .\backfill-history.ps1

    # Custom range
    .\backfill-history.ps1 -StartDate 2025-06-01 -EndDate 2025-06-30

    # Dry run to see what would be fetched
    .\backfill-history.ps1 -DryRun
#>
param(
    [datetime]$StartDate = [datetime]"2025-01-02",
    [datetime]$EndDate   = [datetime]"2026-02-05",
    [string]$DataDir     = "C:\dev\TradeEcosystem\data\market",
    [string]$Symbols     = "QQQ,TQQQ,SQQQ",
    [int]$PacingMs       = 200,
    [int]$BatchSize      = 100,
    [int]$BatchPauseSeconds = 10,
    [switch]$DryRun
)

$ErrorActionPreference = "Continue"

# --- US Market Holidays (NYSE/NASDAQ closed) ---
# 2025 holidays
$holidays = @(
    [datetime]"2025-01-01"   # New Year's Day
    [datetime]"2025-01-20"   # MLK Jr. Day
    [datetime]"2025-02-17"   # Presidents' Day
    [datetime]"2025-04-18"   # Good Friday
    [datetime]"2025-05-26"   # Memorial Day
    [datetime]"2025-06-19"   # Juneteenth
    [datetime]"2025-07-04"   # Independence Day
    [datetime]"2025-09-01"   # Labor Day
    [datetime]"2025-11-27"   # Thanksgiving
    [datetime]"2025-12-25"   # Christmas
    # 2026 holidays (only those before our end date matter)
    [datetime]"2026-01-01"   # New Year's Day
    [datetime]"2026-01-19"   # MLK Jr. Day
)
$holidaySet = [System.Collections.Generic.HashSet[string]]::new()
foreach ($h in $holidays) {
    [void]$holidaySet.Add($h.ToString("yyyyMMdd"))
}

$symbolList = $Symbols -split ","

# --- Build list of trading days ---
$tradingDays = @()
$current = $StartDate
while ($current -le $EndDate) {
    $dow = $current.DayOfWeek
    $dateStr = $current.ToString("yyyyMMdd")

    if ($dow -ne 'Saturday' -and $dow -ne 'Sunday' -and -not $holidaySet.Contains($dateStr)) {
        $tradingDays += $current
    }
    $current = $current.AddDays(1)
}

Write-Host "=========================================="
Write-Host " Historical Data Backfill"
Write-Host "=========================================="
Write-Host "Date range : $($StartDate.ToString('yyyy-MM-dd')) to $($EndDate.ToString('yyyy-MM-dd'))"
Write-Host "Trading days: $($tradingDays.Count)"
Write-Host "Symbols    : $Symbols"
Write-Host "Data dir   : $DataDir"
Write-Host "Pacing     : ${PacingMs}ms between dates, ${BatchPauseSeconds}s every $BatchSize dates"
if ($DryRun) { Write-Host "MODE       : DRY RUN (no downloads)" }
Write-Host "=========================================="
Write-Host ""

# --- Pre-check: how many dates already have all files? ---
$alreadyComplete = 0
$toFetch = @()
foreach ($day in $tradingDays) {
    $dateStr = $day.ToString("yyyyMMdd")
    $allExist = $true
    foreach ($sym in $symbolList) {
        $csvPath = Join-Path $DataDir "${dateStr}_market_data_${sym}.csv"
        if (-not (Test-Path $csvPath)) {
            $allExist = $false
            break
        }
    }
    if ($allExist) {
        $alreadyComplete++
    } else {
        $toFetch += $day
    }
}

Write-Host "Already have data : $alreadyComplete dates (will skip)"
Write-Host "Dates to fetch    : $($toFetch.Count)"
Write-Host ""

if ($toFetch.Count -eq 0) {
    Write-Host "Nothing to download -- all dates already have data files."
    exit 0
}

if ($DryRun) {
    Write-Host "Would fetch these dates:"
    foreach ($day in $toFetch) {
        Write-Host "  $($day.ToString('yyyy-MM-dd')) ($($day.DayOfWeek))"
    }
    Write-Host ""
    Write-Host "Total: $($toFetch.Count) dates x $($symbolList.Count) symbols = $($toFetch.Count * $symbolList.Count) API calls"
    exit 0
}

# --- Determine script directory for dotnet run ---
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $scriptDir

$fetchedCount = 0
$skippedCount = 0
$failedDates  = @()
$stopwatch    = [System.Diagnostics.Stopwatch]::StartNew()

try {
    for ($i = 0; $i -lt $toFetch.Count; $i++) {
        $day = $toFetch[$i]
        $dateStr = $day.ToString("yyyyMMdd")
        $pct = [math]::Round(($i / $toFetch.Count) * 100, 1)
        $elapsed = $stopwatch.Elapsed
        if ($i -gt 0) {
            $avgMs = $elapsed.TotalMilliseconds / $i
            $remainMs = $avgMs * ($toFetch.Count - $i)
            $eta = [timespan]::FromMilliseconds($remainMs).ToString("mm\:ss")
        } else {
            $eta = "calculating..."
        }

        $progressMsg = '[{0}/{1}] {2}% Fetching {3} ({4})  ETA: {5}' -f ($i+1), $toFetch.Count, $pct, $dateStr, $day.DayOfWeek, $eta
        Write-Host $progressMsg

        $output = dotnet run -- --fetch-history --date=$dateStr --symbols=$Symbols 2>&1 | Out-String
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            Write-Host "  WARNING: dotnet run exited with code $exitCode for $dateStr" -ForegroundColor Yellow
            $failedDates += $dateStr
        } else {
            # Verify at least one file was created
            $anyCreated = $false
            foreach ($sym in $symbolList) {
                $csvPath = Join-Path $DataDir "${dateStr}_market_data_${sym}.csv"
                if (Test-Path $csvPath) { $anyCreated = $true; break }
            }
            if ($anyCreated) {
                $fetchedCount++
            } else {
                Write-Host "  WARNING: No files created for $dateStr (holiday or no data?)" -ForegroundColor Yellow
                $failedDates += $dateStr
            }
        }

        # Pacing: small sleep between dates
        if ($i -lt ($toFetch.Count - 1)) {
            Start-Sleep -Milliseconds $PacingMs

            # Longer pause every BatchSize dates
            if ((($i + 1) % $BatchSize) -eq 0) {
                Write-Host ""
                $pauseMsg = '  -- Batch pause ({0}s) after {1} dates to be kind to the API --' -f $BatchPauseSeconds, ($i+1)
                Write-Host $pauseMsg -ForegroundColor Cyan
                Start-Sleep -Seconds $BatchPauseSeconds
                Write-Host ""
            }
        }
    }
} finally {
    Pop-Location
}

$stopwatch.Stop()
$totalTime = $stopwatch.Elapsed.ToString("hh\:mm\:ss")

Write-Host ""
Write-Host "=========================================="
Write-Host " Backfill Complete"
Write-Host "=========================================="
Write-Host "Fetched     : $fetchedCount dates"
Write-Host "Skipped     : $alreadyComplete dates (pre-existing)"
Write-Host "Failed      : $($failedDates.Count) dates"
Write-Host "Total time  : $totalTime"
Write-Host ""

if ($failedDates.Count -gt 0) {
    Write-Host "Failed dates (retry these manually):" -ForegroundColor Yellow
    foreach ($fd in $failedDates) {
        Write-Host "  $fd" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "Retry command:" -ForegroundColor Yellow
    $retryDates = $failedDates -join ','
    $retryCmd = 'foreach ($d in @(' + $retryDates + ')) { dotnet run -- --fetch-history --date=$d --symbols=' + $Symbols + ' }'
    Write-Host ('  ' + $retryCmd) -ForegroundColor Yellow
}
