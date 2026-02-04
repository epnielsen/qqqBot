namespace qqqBot;

/// <summary>
/// Represents shares that couldn't be fully liquidated and need cleanup.
/// </summary>
public class OrphanedPosition
{
    public string Symbol { get; set; } = string.Empty;
    public long Shares { get; set; }
    public string? CreatedAt { get; set; }
}

/// <summary>
/// Trading state for persistence across restarts.
/// </summary>
public class TradingState
{
    public decimal AvailableCash { get; set; }
    public decimal AccumulatedLeftover { get; set; }
    public bool IsInitialized { get; set; }
    public string? LastTradeTimestamp { get; set; }
    public string? CurrentPosition { get; set; }
    public long CurrentShares { get; set; }
    public decimal StartingAmount { get; set; }
    public decimal DayStartBalance { get; set; }
    public string? DayStartDate { get; set; }
    public TradingStateMetadata? Metadata { get; set; }
    
    // ORPHANED SHARES (partial fills that need cleanup after position switch)
    public OrphanedPosition? OrphanedShares { get; set; }
    
    // TRAILING STOP PERSISTENCE (survives restarts)
    public decimal? HighWaterMark { get; set; }
    public decimal? LowWaterMark { get; set; }
    public decimal? TrailingStopValue { get; set; }
    public bool IsStoppedOut { get; set; }
    public string? StoppedOutDirection { get; set; }
    public decimal? WashoutLevel { get; set; }
    public string? StopoutTimestamp { get; set; }
}
