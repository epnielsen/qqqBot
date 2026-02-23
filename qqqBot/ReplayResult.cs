namespace qqqBot;

/// <summary>
/// Structured result from a single replay run.
/// Returned by SimulatedBroker.GetResult() and collected by ParallelReplayRunner
/// for aggregation and CSV output.
/// </summary>
public sealed record ReplayResult
{
    /// <summary>Whether the replay completed successfully (no exceptions).</summary>
    public bool Success { get; init; }

    /// <summary>Error message if the replay failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Replay date.</summary>
    public DateOnly ReplayDate { get; init; }

    /// <summary>RNG seed used for this run (broker + interpolation).</summary>
    public int RngSeed { get; init; }

    /// <summary>Optional label describing the config variant (e.g., "TrailingStop=0.3%").</summary>
    public string? ConfigLabel { get; init; }

    // ── Financial results ──

    /// <summary>Starting cash for the run.</summary>
    public decimal StartingCash { get; init; }

    /// <summary>Ending equity (cash + position value).</summary>
    public decimal EndingEquity { get; init; }

    /// <summary>Realized P/L (closed trades only).</summary>
    public decimal RealizedPnL { get; init; }

    /// <summary>Net return as a fraction (e.g., 0.0137 for 1.37%).</summary>
    public decimal NetReturnFraction { get; init; }

    /// <summary>Total number of trades executed.</summary>
    public int TotalTrades { get; init; }

    /// <summary>Total spread cost incurred.</summary>
    public decimal SpreadCost { get; init; }

    /// <summary>Total slippage cost incurred.</summary>
    public decimal SlippageCost { get; init; }

    // ── Watermarks ──

    /// <summary>Peak equity during the run.</summary>
    public decimal PeakEquity { get; init; }

    /// <summary>Time (UTC) of peak equity.</summary>
    public DateTime PeakEquityTimeUtc { get; init; }

    /// <summary>Trough equity during the run.</summary>
    public decimal TroughEquity { get; init; }

    /// <summary>Time (UTC) of trough equity.</summary>
    public DateTime TroughEquityTimeUtc { get; init; }

    // ── Timing ──

    /// <summary>Wall-clock duration of the replay.</summary>
    public TimeSpan WallClockDuration { get; init; }

    // ── CSV header for aggregation ──

    /// <summary>Returns a CSV header line matching ToCsvLine().</summary>
    public static string CsvHeader =>
        "Date,Seed,ConfigLabel,Success,RealizedPnL,NetReturn%,TotalTrades," +
        "SpreadCost,SlippageCost,PeakPnL,TroughPnL,WallClockMs,ErrorMessage";

    /// <summary>Returns a CSV data line matching CsvHeader.</summary>
    public string ToCsvLine()
    {
        var peakPnL = PeakEquity - StartingCash;
        var troughPnL = TroughEquity - StartingCash;
        var escapedLabel = ConfigLabel?.Replace(",", ";") ?? "";
        var escapedError = ErrorMessage?.Replace(",", ";").Replace("\n", " ") ?? "";
        return $"{ReplayDate:yyyy-MM-dd},{RngSeed},{escapedLabel},{Success}," +
               $"{RealizedPnL:F2},{NetReturnFraction * 100:F4},{TotalTrades}," +
               $"{SpreadCost:F2},{SlippageCost:F2},{peakPnL:F2},{troughPnL:F2}," +
               $"{WallClockDuration.TotalMilliseconds:F0},{escapedError}";
    }
}
