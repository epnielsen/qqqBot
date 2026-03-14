using MarketBlocks.Trade.Interfaces;

namespace qqqBot;

/// <summary>
/// Represents a snapshot of the market's regime at a specific moment.
/// This is the message contract between the Analyst (Producer) and Trader (Consumer).
/// Implements <see cref="ISignalMessage"/> so it can flow through the generic
/// <see cref="Services.PipelineHost{TSignal}"/> framework.
/// </summary>
/// <param name="Signal">The trading signal: "BULL", "BEAR", "NEUTRAL", or "MARKET_CLOSE"</param>
/// <param name="BenchmarkPrice">Current benchmark price (e.g., QQQ)</param>
/// <param name="SmaValue">Current SMA value for context/logging</param>
/// <param name="Slope">Velocity (Linear Regression Slope) of the SMA - used for brake system</param>
/// <param name="UpperBand">Upper hysteresis band for charting/debugging</param>
/// <param name="LowerBand">Lower hysteresis band for charting/debugging</param>
/// <param name="TimestampUtc">Data freshness timestamp</param>
/// <param name="Reason">Human-readable reason for the signal (e.g., "Price > UpperBand")</param>
/// <param name="CandleHigh">Rolling candle high (highest price in volatility window)</param>
/// <param name="CandleLow">Rolling candle low (lowest price in volatility window)</param>
/// <param name="VolatilityPercent">Rolling volatility as a percentage ("Storm Factor": 0 = calm, >0.001 = stormy)</param>
public record MarketRegime(
    string Signal,
    decimal BenchmarkPrice,
    decimal SmaValue,
    decimal Slope,
    decimal UpperBand,
    decimal LowerBand,
    DateTime TimestampUtc,
    string Reason,
    decimal? BullPrice = null,
    decimal? BearPrice = null,
    decimal CandleHigh = 0,
    decimal CandleLow = 0,
    decimal VolatilityPercent = 0,
    double CyclePeriodSeconds = 0,
    double CycleStability = 0,
    // Mean-reversion strategy fields (populated when StrategyMode = MeanReversion)
    StrategyMode ActiveStrategy = StrategyMode.Trend,
    decimal? PercentB = null,
    decimal? BollingerUpper = null,
    decimal? BollingerMiddle = null,
    decimal? BollingerLower = null,
    decimal? ChopIndex = null,
    // RSI and ATR for mean-reversion confirmation and dynamic stops
    decimal? Rsi = null,
    decimal? Atr = null,
    // Trend Rescue entry flag: true when BULL was entered via trendRescue (slope < entryVelocity)
    // Used by TraderEngine to apply wider trailing stop for gradual trend holdings
    bool IsTrendRescueEntry = false,
    // Drift Mode entry flag: true when entered via sustained SMA displacement (velocity-independent)
    // Used by TraderEngine to apply wider trailing stop for drift holdings
    bool IsDriftEntry = false,
    // Displacement Re-Entry flag: true when re-entered after stop-out via regime-validated displacement
    bool IsDisplacementReentry = false
) : ISignalMessage
{
    /// <summary>
    /// Check if this regime data is stale (older than threshold).
    /// </summary>
    public bool IsStale(TimeSpan maxAge) => DateTime.UtcNow - TimestampUtc > maxAge;
    
    /// <summary>
    /// Default stale threshold (10 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(10);
    
    /// <summary>
    /// Creates a NEUTRAL regime for stale data protection.
    /// </summary>
    public static MarketRegime StaleDataNeutral(decimal lastPrice, decimal sma) =>
        new("NEUTRAL", lastPrice, sma, 0m, 0m, 0m, DateTime.UtcNow, "Stale Data Protection");
}
