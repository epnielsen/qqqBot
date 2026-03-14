namespace qqqBot;

/// <summary>
/// Identifies which trading strategy the analyst engine should use for signal generation.
/// Phase-gated: each market phase (OV, Base, PH) can default to a different strategy,
/// and the Choppiness Index can dynamically override the default in either direction.
/// </summary>
public enum StrategyMode
{
    /// <summary>
    /// Trend/momentum strategy (default). Uses SMA + slope + hysteresis bands.
    /// Signals: BULL, BEAR, NEUTRAL, MARKET_CLOSE.
    /// </summary>
    Trend = 0,
    
    /// <summary>
    /// Mean-reversion strategy. Uses Bollinger Bands + Choppiness Index.
    /// Buys at lower band, sells at upper band, exits at midline.
    /// Signals: MR_LONG, MR_SHORT, MR_FLAT, MARKET_CLOSE.
    /// </summary>
    MeanReversion = 1
}
