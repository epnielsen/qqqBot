namespace qqqBot;

/// <summary>
/// Reason why trading was halted for the session.
/// Persisted in TradingState to survive restarts.
/// </summary>
public enum HaltReason
{
    /// <summary>Trading is active (not halted).</summary>
    None = 0,
    
    /// <summary>Daily profit target trailing stop triggered — trading halted.
    /// May be resumed in Power Hour if ResumeInPowerHour is enabled.</summary>
    ProfitTarget = 1,
    
    /// <summary>Daily loss limit breached — trading halted for the rest of the day.
    /// Never resumable (PH Resume does not apply to loss halts).</summary>
    LossLimit = 2
}
