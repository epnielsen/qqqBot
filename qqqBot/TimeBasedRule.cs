namespace qqqBot;

/// <summary>
/// Defines a time window during the trading day when specific settings overrides apply.
/// The bot automatically switches parameters based on the current Eastern Time.
/// 
/// Example phases:
///   - Open Volatility (09:30-10:30): Wide stops, fast slope, aggressive thresholds
///   - Mid-Day Lull   (10:30-14:00): Tight stops, patient exits, IOC orders
///   - Power Hour     (14:00-16:00): Tight stops, fast exits, momentum trimming
/// </summary>
public class TimeBasedRule
{
    /// <summary>Display name for logging (e.g., "Open Volatility", "Mid-Day Lull").</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Start time in Eastern Time (inclusive). Format: "HH:mm" → parsed to TimeSpan.</summary>
    public TimeSpan StartTime { get; set; }
    
    /// <summary>End time in Eastern Time (exclusive). Format: "HH:mm" → parsed to TimeSpan.</summary>
    public TimeSpan EndTime { get; set; }
    
    /// <summary>Settings overrides to apply during this window. Only non-null values are applied.</summary>
    public TradingSettingsOverrides Overrides { get; set; } = new();
    
    /// <summary>Checks if the given Eastern time-of-day falls within this rule's window.</summary>
    public bool IsActive(TimeSpan easternTimeOfDay)
        => easternTimeOfDay >= StartTime && easternTimeOfDay < EndTime;
}

/// <summary>
/// Nullable overlay for TradingSettings. Only non-null properties are applied on phase transition.
/// This allows each phase to override only the parameters that differ from the base config.
/// 
/// DESIGN: Flat structure with flattened nested objects (ExitStrategy, DynamicStopLoss).
/// The TimeRuleApplier maps these back to the nested TradingSettings properties.
/// </summary>
public class TradingSettingsOverrides
{
    // --- Signal Generation (AnalystEngine) ---
    public decimal? MinVelocityThreshold { get; set; }
    public decimal? EntryVelocityMultiplier { get; set; }
    public int? SMAWindowSeconds { get; set; }
    public int? SlopeWindowSize { get; set; }
    public decimal? ChopThresholdPercent { get; set; }
    public decimal? MinChopAbsolute { get; set; }
    public int? TrendWindowSeconds { get; set; }
    public int? EntryConfirmationTicks { get; set; }
    public int? BearEntryConfirmationTicks { get; set; }
    public bool? BullOnlyMode { get; set; }
    
    // --- Adaptive Trend Window ---
    public bool? EnableAdaptiveTrendWindow { get; set; }
    public int? ShortTrendSlopeWindow { get; set; }
    public decimal? ShortTrendSlopeThreshold { get; set; }
    
    // --- End-of-Day Entry Cutoff ---
    public decimal? LastEntryMinutesBeforeClose { get; set; }
    
    // --- Drift Mode ---
    public bool? DriftModeEnabled { get; set; }
    public int? DriftModeConsecutiveTicks { get; set; }
    public decimal? DriftModeMinDisplacementPercent { get; set; }
    public decimal? DriftModeAtrMultiplier { get; set; }
    public decimal? DriftTrailingStopPercent { get; set; }
    
    // --- Displacement Re-Entry ---
    public bool? DisplacementReentryEnabled { get; set; }
    public decimal? DisplacementReentryPercent { get; set; }
    public decimal? DisplacementAtrMultiplier { get; set; }
    public decimal? DisplacementChopThreshold { get; set; }
    public int? DisplacementBbwLookback { get; set; }
    public int? DisplacementSlopeWindow { get; set; }
    public decimal? DisplacementMinSlope { get; set; }
    
    // --- Exit Strategy (flattened from DynamicExitConfig) ---
    public int? ScalpWaitSeconds { get; set; }
    public int? TrendWaitSeconds { get; set; }
    public double? TrendConfidenceThreshold { get; set; }
    public bool? HoldNeutralIfUnderwater { get; set; }
    
    // --- Trade Execution (TraderEngine) ---
    public decimal? TrailingStopPercent { get; set; }
    public decimal? TrendRescueTrailingStopPercent { get; set; }
    public bool? UseMarketableLimits { get; set; }
    public bool? UseIocOrders { get; set; }
    public decimal? IocLimitOffsetCents { get; set; }
    public decimal? IocRetryStepCents { get; set; }
    public decimal? MaxSlippagePercent { get; set; }
    public decimal? MaxChaseDeviationPercent { get; set; }
    
    // --- Dynamic Stop Loss (flattened from DynamicStopConfig) ---
    public bool? DynamicStopLossEnabled { get; set; }
    public List<StopTier>? DynamicStopLossTiers { get; set; }
    
    // --- Trimming ---
    public bool? EnableTrimming { get; set; }
    public decimal? TrimTriggerPercent { get; set; }
    public decimal? TrimRatio { get; set; }
    public decimal? TrimSlopeThreshold { get; set; }
    public int? TrimCooldownSeconds { get; set; }
    
    // --- Profit ---
    public decimal? ProfitReinvestmentPercent { get; set; }
    
    // --- Direction Switch Cooldown ---
    public int? DirectionSwitchCooldownSeconds { get; set; }
    
    // --- Mean Reversion Strategy ---
    public StrategyMode? BaseDefaultStrategy { get; set; }
    public StrategyMode? PhDefaultStrategy { get; set; }
    public bool? ChopOverrideEnabled { get; set; }
    public decimal? ChopUpperThreshold { get; set; }
    public decimal? ChopLowerThreshold { get; set; }
    public decimal? ChopTrendExitThreshold { get; set; }
    public int? BollingerWindow { get; set; }
    public decimal? BollingerMultiplier { get; set; }
    public int? ChopPeriod { get; set; }
    public int? ChopCandleSeconds { get; set; }
    public decimal? MrEntryLowPctB { get; set; }
    public decimal? MrEntryHighPctB { get; set; }
    public decimal? MrExitPctB { get; set; }
    public decimal? MeanRevStopPercent { get; set; }
    public decimal? MrAtrStopMultiplier { get; set; }
    public bool? MrRequireRsi { get; set; }
    public int? MrRsiPeriod { get; set; }
    public decimal? MrRsiOversold { get; set; }
    public decimal? MrRsiOverbought { get; set; }
}
