using MarketBlocks.Bots.Domain;

namespace qqqBot;

/// <summary>
/// qqqBot-specific configuration settings. Extends <see cref="BaseTradingSettings"/> with
/// signal generation parameters (SMA, slope, velocity, mean-reversion, drift mode),
/// dynamic exit/stop strategies, trimming, phase reset, and time-based rules.
/// </summary>
public class TradingSettings : BaseTradingSettings
{
    // ─── SMA / SIGNAL GENERATION ─────────────────────────────────────
    public int SMAWindowSeconds { get; set; } = 60; // Total time window for rolling average
    public decimal ChopThresholdPercent { get; set; } = 0.0015m;
    public decimal MinChopAbsolute { get; set; } = 0.02m; // Absolute floor for hysteresis (tick-aware)
    public bool SlidingBand { get; set; } = false; // When true, band slides based on position high/low
    public decimal SlidingBandFactor { get; set; } = 0.5m; // Exit threshold factor
    
    // DYNAMIC EXIT STRATEGY (Hybrid Scalp/Trend Mode)
    public DynamicExitConfig ExitStrategy { get; set; } = new();
    
    public string CryptoBenchmarkSymbol { get; set; } = "BTC/USD";
    public bool UseBtcEarlyTrading { get; set; } = false;
    public bool WatchBtc { get; set; } = false;
    public DynamicStopConfig DynamicStopLoss { get; set; } = new(); // Ratchet stop
    public int StopLossCooldownSeconds { get; set; } = 10; // Washout latch duration
    public int DirectionSwitchCooldownSeconds { get; set; } = 0; // Min seconds before switching BULL↔BEAR
    
    // BRAKE SYSTEM (Velocity/Slope Detection)
    public decimal MinVelocityThreshold { get; set; } = 0.0001m;
    public decimal EntryVelocityMultiplier { get; set; } = 2.0m;
    public decimal TrendRescueTrailingStopPercent { get; set; } = 0m;
    public int SlopeWindowSize { get; set; } = 5;
    public int EntryConfirmationTicks { get; set; } = 2;
    public int BearEntryConfirmationTicks { get; set; } = 0;
    
    // PH RESUME MODE
    public bool ResumeInPowerHour { get; set; } = false;
    
    // ANALYST PHASE RESET
    public AnalystPhaseResetMode AnalystPhaseResetMode { get; set; } = AnalystPhaseResetMode.None;
    public int AnalystPhaseResetSeconds { get; set; } = 120;
    
    // MEAN REVERSION STRATEGY
    public StrategyMode BaseDefaultStrategy { get; set; } = StrategyMode.Trend;
    public StrategyMode PhDefaultStrategy { get; set; } = StrategyMode.Trend;
    public bool ChopOverrideEnabled { get; set; } = false;
    public decimal ChopUpperThreshold { get; set; } = 61.8m;
    public decimal ChopLowerThreshold { get; set; } = 38.2m;
    public decimal ChopTrendExitThreshold { get; set; } = 45m;
    public int BollingerWindow { get; set; } = 20;
    public decimal BollingerMultiplier { get; set; } = 2.0m;
    public int ChopPeriod { get; set; } = 14;
    public int ChopCandleSeconds { get; set; } = 60;
    public decimal MrEntryLowPctB { get; set; } = 0.2m;
    public decimal MrEntryHighPctB { get; set; } = 0.8m;
    public decimal MrExitPctB { get; set; } = 0.5m;
    public decimal MeanRevStopPercent { get; set; } = 0.003m;
    public decimal MrAtrStopMultiplier { get; set; } = 2.0m;
    public bool MrRequireRsi { get; set; } = true;
    public int MrRsiPeriod { get; set; } = 14;
    public decimal MrRsiOversold { get; set; } = 30m;
    public decimal MrRsiOverbought { get; set; } = 70m;
    
    // HYBRID ENGINE SETTINGS (Velocity + Trend)
    public int TrendWindowSeconds { get; set; } = 1800;
    
    // ADAPTIVE TREND WINDOW
    public bool EnableAdaptiveTrendWindow { get; set; } = true;
    public int ShortTrendSlopeWindow { get; set; } = 90;
    public decimal ShortTrendSlopeThreshold { get; set; } = 0.00002m;
    
    // DRIFT MODE
    public bool DriftModeEnabled { get; set; }
    public int DriftModeConsecutiveTicks { get; set; } = 60;
    public decimal DriftModeMinDisplacementPercent { get; set; } = 0.002m;
    public decimal DriftModeAtrMultiplier { get; set; } = 0m;
    public decimal DriftTrailingStopPercent { get; set; } = 0m;
    
    // DISPLACEMENT RE-ENTRY
    public bool DisplacementReentryEnabled { get; set; }
    public decimal DisplacementReentryPercent { get; set; } = 0.005m;
    public decimal DisplacementAtrMultiplier { get; set; } = 2.0m;
    public decimal DisplacementChopThreshold { get; set; } = 50m;
    public int DisplacementBbwLookback { get; set; } = 20;
    public int DisplacementSlopeWindow { get; set; } = 10;
    public decimal DisplacementMinSlope { get; set; } = 0m;

    // TRIMMING SETTINGS
    public bool EnableTrimming { get; set; } = true;
    public decimal TrimTriggerPercent { get; set; } = 0.015m;
    public decimal TrimRatio { get; set; } = 0.33m;
    public decimal TrimSlopeThreshold { get; set; } = 0.000005m;
    public int TrimCooldownSeconds { get; set; } = 120;
    
    // TIME-BASED RULES (Auto Phase Switching)
    public List<TimeBasedRule> TimeRules { get; set; } = new();
    
    // DEPRECATED: Use ProfitReinvestmentPercent instead.
    [Obsolete("Use ProfitReinvestmentPercent instead. Set to 0 to disable legacy behavior.")]
    public decimal TakeProfitAmount { get; set; } = 0m;
    
    // Derived: Calculate queue size dynamically from window and interval
    public int SMALength => System.Math.Max(1, SMAWindowSeconds / PollingIntervalSeconds);
    
    /// <summary>
    /// Creates a validated copy of the settings for use by the trading engine.
    /// </summary>
    public TradingSettings ValidatedCopy()
    {
        Validate();
        return this;
    }
}

/// <summary>
/// Configuration for dynamic exit strategy that switches between Scalp and Trend modes
/// based on the strength of the current trend (absolute slope value).
/// </summary>
public class DynamicExitConfig
{
    /// <summary>
    /// How long to wait in Neutral if the trend is weak (Chop/Scalp mode).
    /// Recommended: 0 seconds (immediate exit in choppy markets).
    /// Set to -1 to hold through neutral (disabled).
    /// </summary>
    public int ScalpWaitSeconds { get; set; } = 0;

    /// <summary>
    /// How long to wait in Neutral if the trend is strong (Trend mode).
    /// Recommended: 120 seconds (patience for momentum to resume).
    /// Set to -1 to hold through neutral (disabled).
    /// </summary>
    public int TrendWaitSeconds { get; set; } = 120;

    /// <summary>
    /// The absolute slope value required to trigger "Trend Mode".
    /// When |slope| >= this threshold, use TrendWaitSeconds; otherwise use ScalpWaitSeconds.
    /// Recommended: 0.00015 (adjust based on your instrument volatility).
    /// </summary>
    public double TrendConfidenceThreshold { get; set; } = 0.00015;
    
    /// <summary>
    /// When true, the Neutral Timeout will NOT liquidate positions that are underwater (unrealized P/L &lt; 0).
    /// Instead, it holds until the Trailing Stop / Ratchet Stop triggers or the signal flips.
    /// This prevents realizing losses on sideways markets that may recover.
    /// Safety: The Hard Stop and Ratchet Stop remain active as downside protection.
    /// </summary>
    public bool HoldNeutralIfUnderwater { get; set; } = true;
}

/// <summary>
/// Configuration for dynamic trailing stop that tightens as unrealized profit grows.
/// Prevents "round trip" scenarios where large gains evaporate because the fixed stop was too loose.
/// 
/// Example tiers:
///   - Profit > 0.3% → tighten stop to 0.15%
///   - Profit > 0.5% → tighten stop to 0.10% (lock the win)
/// </summary>
public class DynamicStopConfig
{
    /// <summary>Enable dynamic profit-based stop tightening.</summary>
    public bool Enabled { get; set; }
    
    /// <summary>Ordered list of profit tiers. Highest matching tier wins.</summary>
    public List<StopTier> Tiers { get; set; } = new();
}

/// <summary>
/// A single ratchet tier: when unrealized profit exceeds TriggerProfitPercent,
/// the trailing stop tightens to StopPercent.
/// </summary>
public class StopTier
{
    /// <summary>Minimum profit % to activate this tier (e.g., 0.003 = 0.3%).</summary>
    public decimal TriggerProfitPercent { get; set; }
    
    /// <summary>Trailing stop distance when this tier is active (e.g., 0.0015 = 0.15%).</summary>
    public decimal StopPercent { get; set; }
}
