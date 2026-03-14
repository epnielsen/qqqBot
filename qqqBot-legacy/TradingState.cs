using MarketBlocks.Bots.Domain;

namespace qqqBot;

/// <summary>
/// qqqBot-specific trading state. Extends <see cref="BaseTradingState"/> with
/// trailing stop, daily target, sliding band, and analyst signal persistence.
/// </summary>
public class TradingState : BaseTradingState
{
    public DateTime? LastTrimTime { get; set; }    // Trim cooldown tracking
    
    // TRAILING STOP PERSISTENCE (survives restarts)
    public decimal? HighWaterMark { get; set; }
    public decimal? LowWaterMark { get; set; }
    public decimal? TrailingStopValue { get; set; }
    public bool IsStoppedOut { get; set; }
    public string? StoppedOutDirection { get; set; }
    public decimal? WashoutLevel { get; set; }
    public string? StopoutTimestamp { get; set; }
    
    // DAILY PROFIT TARGET TRAILING STOP PERSISTENCE
    public bool DailyTargetArmed { get; set; } // True when profit target reached, trailing stop active
    public decimal? DailyTargetPeakPnL { get; set; } // High water mark for daily P/L trailing stop
    public decimal? DailyTargetStopLevel { get; set; } // Current trailing stop level for daily P/L
    
    // HALT REASON (persisted — replaces volatile _dailyTargetReached bool)
    // Tracks WHY trading was halted so PH Resume can distinguish profit-target vs loss-limit halts.
    public HaltReason HaltReason { get; set; } = HaltReason.None;
    
    // PH RESUME MODE PERSISTENCE
    // True when daily profit target fired before Power Hour and ResumeInPowerHour is enabled.
    // Cleared when PH starts (resume) or on day boundary (reset).
    public bool PhResumeArmed { get; set; }
    
    // SLIDING BAND PERSISTENCE
    public decimal? SlidingBandPositionHighWaterMark { get; set; }
    public decimal? SlidingBandPositionLowWaterMark { get; set; }
    
    // ANALYST STATE PERSISTENCE (survives restarts)
    // This allows the Analyst to know its last emitted signal without
    // needing to infer from share counts (separation of concerns)
    public string? LastAnalystSignal { get; set; }
}
