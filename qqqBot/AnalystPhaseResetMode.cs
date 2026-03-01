namespace qqqBot;

/// <summary>
/// Controls how the analyst engine resets its indicators at phase boundaries.
/// Used to configure whether indicators carry forward, start cold, or retain recent history.
/// </summary>
public enum AnalystPhaseResetMode
{
    /// <summary>
    /// Default. Indicators carry forward all history across phase transitions.
    /// SMA/slope/trend retain full session data — no warmup needed.
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Clear all indicator history at the phase boundary.
    /// SMA, slope, and trend start empty — equivalent to a fresh bot startup.
    /// Warmup required: SMA fills in ~SMAWindowSeconds, slope in ~SlopeWindowSize ticks after SMA.
    /// Trend SMA may never fill during a short phase (e.g., 2-hour PH needs 90 min for 5400s trend).
    /// </summary>
    Cold = 1,
    
    /// <summary>
    /// Retain only the last N seconds of indicator history (configured via AnalystPhaseResetSeconds).
    /// Provides a compromise between full history (stale morning data) and cold start (long warmup).
    /// </summary>
    Partial = 2
}
