using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Automatically applies time-based settings overrides to the shared TradingSettings singleton.
/// Called from both AnalystEngine and TraderEngine on every tick/regime to check for phase transitions.
/// 
/// Architecture:
///   1. On construction, snapshots all overrideable base values from the TradingSettings singleton.
///   2. On each CheckAndApply() call, determines which TimeBasedRule (if any) is active.
///   3. On phase change: resets ALL overrideable properties to base values, then applies the new rule's overrides.
///   4. Returns true on phase change so the caller can trigger indicator reconfiguration.
///   
/// Thread safety: Both engines call CheckAndApply() but they run on different async threads.
/// We use a simple lock to ensure atomic phase transitions. The mutex is very fast (no I/O).
/// </summary>
public class TimeRuleApplier
{
    private readonly TradingSettings _settings;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    
    // Snapshot of base (config-file) values — never mutated after construction
    private readonly SettingsSnapshot _baseValues;
    
    // Current phase tracking (shared — reflects the most recent caller's phase)
    private string? _activePhaseName;
    private TimeBasedRule? _activeRule;
    
    // Per-caller monotonic time guards: prevents a single caller from processing
    // timestamps that go backwards within its own stream.
    private readonly Dictionary<string, TimeSpan> _highWaterTimes = new();
    
    // Global max time: the highest timestamp seen from ANY caller.
    // Phase determination uses this instead of the individual caller's time.
    // This prevents cross-caller bouncing: once the analyst races past a phase
    // boundary (e.g., 10:31 past OV end at 10:30), the trader's earlier timestamps
    // (e.g., 09:45 still in OV) won't flip phase back because the global max
    // stays at 10:31. Settings stay consistent for both callers.
    private TimeSpan _globalMaxTime = TimeSpan.Zero;
    
    /// <summary>Name of the currently active phase, or null if in base config.</summary>
    public string? ActivePhaseName
    {
        get { lock (_lock) return _activePhaseName; }
    }
    
    /// <summary>True if the settings were changed since the last call to CheckAndApply().</summary>
    public bool SettingsChangedSinceLastCheck { get; private set; }

    public TimeRuleApplier(TradingSettings settings, ILogger<TimeRuleApplier> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Snapshot ALL overrideable values from config-file defaults
        _baseValues = SnapshotCurrentSettings();
        
        if (_settings.TimeRules.Count > 0)
        {
            _logger.LogInformation("[TIME RULES] Loaded {Count} phase rule(s):", _settings.TimeRules.Count);
            foreach (var rule in _settings.TimeRules)
            {
                _logger.LogInformation("[TIME RULES]   {Name}: {Start} -> {End}", 
                    rule.Name, rule.StartTime.ToString(@"hh\:mm"), rule.EndTime.ToString(@"hh\:mm"));
            }
        }
    }
    
    /// <summary>
    /// Checks the current time against all rules and applies overrides if the phase has changed.
    /// Returns true if a phase transition occurred (caller should reconfigure indicators).
    /// 
    /// IMPORTANT: This is idempotent within the same phase — calling multiple times per tick is safe.
    /// </summary>
    /// <param name="easternTimeOfDay">Current Eastern Time as TimeSpan (e.g., 10:30 = new TimeSpan(10,30,0)).</param>
    /// <param name="callerId">Identifies the caller (e.g., "analyst" or "trader") for per-caller monotonic guards.</param>
    /// <returns>True if settings were changed (phase transition occurred).</returns>
    public bool CheckAndApply(TimeSpan easternTimeOfDay, string callerId = "default")
    {
        if (_settings.TimeRules.Count == 0) return false;
        
        lock (_lock)
        {
            // Per-caller monotonic time guard: ignore timestamps that go backwards
            // for this specific caller.
            if (_highWaterTimes.TryGetValue(callerId, out var hwm) && easternTimeOfDay < hwm)
            {
                return false;
            }
            _highWaterTimes[callerId] = easternTimeOfDay;
            
            // Advance the global max time. Phase determination uses this so that
            // once ANY caller crosses a boundary, it's crossed for everyone.
            if (easternTimeOfDay > _globalMaxTime)
                _globalMaxTime = easternTimeOfDay;
            
            // Find the matching rule using the global max time (not this caller's time).
            // This is the key anti-bounce mechanism: the trader at 09:45 won't flip
            // back to OV if the analyst has already advanced global time to 10:31.
            TimeBasedRule? matchingRule = null;
            foreach (var rule in _settings.TimeRules)
            {
                if (rule.IsActive(_globalMaxTime))
                {
                    matchingRule = rule;
                    break;
                }
            }
            
            // Determine new phase name
            var newPhaseName = matchingRule?.Name;
            
            // Compare against the shared active phase. With global max time,
            // there's no cross-caller bouncing, so shared tracking is safe.
            if (newPhaseName == _activePhaseName)
            {
                SettingsChangedSinceLastCheck = false;
                return false;
            }
            
            // --- PHASE TRANSITION ---
            var previousPhase = _activePhaseName ?? "Base Config";
            var nextPhase = newPhaseName ?? "Base Config";
            
            // Step 1: Reset ALL overrideable properties to base (config-file) values
            RestoreBaseValues();
            
            // Step 2: Apply the new rule's overrides (if any)
            if (matchingRule != null)
            {
                ApplyOverrides(matchingRule.Overrides);
            }
            
            // Step 3: Update tracking
            _activePhaseName = newPhaseName;
            _activeRule = matchingRule;
            SettingsChangedSinceLastCheck = true;
            
            // Step 4: Log the transition with a visible banner
            _logger.LogInformation(
                "╔══════════════════════════════════════════════════════╗");
            _logger.LogInformation(
                "║  PHASE TRANSITION: {Previous} -> {Next}", previousPhase, nextPhase);
            _logger.LogInformation(
                "╚══════════════════════════════════════════════════════╝");
            
            LogChangedSettings(matchingRule);
            
            return true;
        }
    }
    
    /// <summary>
    /// Checks if indicator-affecting settings changed (SMA window, slope window, trend window).
    /// The AnalystEngine uses this to decide whether to rebuild calculators.
    /// </summary>
    public bool IndicatorSettingsChanged(SettingsSnapshot before)
    {
        return before.SMAWindowSeconds != _settings.SMAWindowSeconds
            || before.SlopeWindowSize != _settings.SlopeWindowSize
            || before.TrendWindowSeconds != _settings.TrendWindowSeconds
            || before.ShortTrendSlopeWindow != _settings.ShortTrendSlopeWindow
            || before.BollingerWindow != _settings.BollingerWindow
            || before.BollingerMultiplier != _settings.BollingerMultiplier
            || before.ChopPeriod != _settings.ChopPeriod
            || before.ChopCandleSeconds != _settings.ChopCandleSeconds
            || before.MrRsiPeriod != _settings.MrRsiPeriod;
    }
    
    /// <summary>
    /// Takes a snapshot of the current indicator-affecting settings for comparison after phase change.
    /// </summary>
    public SettingsSnapshot SnapshotCurrentSettings()
    {
        return new SettingsSnapshot
        {
            // Signal generation
            MinVelocityThreshold = _settings.MinVelocityThreshold,
            EntryVelocityMultiplier = _settings.EntryVelocityMultiplier,
            SMAWindowSeconds = _settings.SMAWindowSeconds,
            SlopeWindowSize = _settings.SlopeWindowSize,
            ChopThresholdPercent = _settings.ChopThresholdPercent,
            MinChopAbsolute = _settings.MinChopAbsolute,
            TrendWindowSeconds = _settings.TrendWindowSeconds,
            EnableAdaptiveTrendWindow = _settings.EnableAdaptiveTrendWindow,
            ShortTrendSlopeWindow = _settings.ShortTrendSlopeWindow,
            ShortTrendSlopeThreshold = _settings.ShortTrendSlopeThreshold,
            DriftModeEnabled = _settings.DriftModeEnabled,
            DriftModeConsecutiveTicks = _settings.DriftModeConsecutiveTicks,
            DriftModeMinDisplacementPercent = _settings.DriftModeMinDisplacementPercent,
            DriftModeAtrMultiplier = _settings.DriftModeAtrMultiplier,
            DriftTrailingStopPercent = _settings.DriftTrailingStopPercent,
            DisplacementReentryEnabled = _settings.DisplacementReentryEnabled,
            DisplacementReentryPercent = _settings.DisplacementReentryPercent,
            DisplacementAtrMultiplier = _settings.DisplacementAtrMultiplier,
            DisplacementChopThreshold = _settings.DisplacementChopThreshold,
            DisplacementBbwLookback = _settings.DisplacementBbwLookback,
            DisplacementSlopeWindow = _settings.DisplacementSlopeWindow,
            DisplacementMinSlope = _settings.DisplacementMinSlope,
            EntryConfirmationTicks = _settings.EntryConfirmationTicks,
            BearEntryConfirmationTicks = _settings.BearEntryConfirmationTicks,
            BullOnlyMode = _settings.BullOnlyMode,
            // Exit strategy
            ScalpWaitSeconds = _settings.ExitStrategy.ScalpWaitSeconds,
            TrendWaitSeconds = _settings.ExitStrategy.TrendWaitSeconds,
            TrendConfidenceThreshold = _settings.ExitStrategy.TrendConfidenceThreshold,
            HoldNeutralIfUnderwater = _settings.ExitStrategy.HoldNeutralIfUnderwater,
            // Trade execution
            TrailingStopPercent = _settings.TrailingStopPercent,
            TrendRescueTrailingStopPercent = _settings.TrendRescueTrailingStopPercent,
            UseMarketableLimits = _settings.UseMarketableLimits,
            UseIocOrders = _settings.UseIocOrders,
            PendingOrderTimeoutSeconds = _settings.PendingOrderTimeoutSeconds,
            IocLimitOffsetCents = _settings.IocLimitOffsetCents,
            IocRetryStepCents = _settings.IocRetryStepCents,
            MaxSlippagePercent = _settings.MaxSlippagePercent,
            MaxChaseDeviationPercent = _settings.MaxChaseDeviationPercent,
            LastEntryMinutesBeforeClose = _settings.LastEntryMinutesBeforeClose,
            // Dynamic stop loss
            DynamicStopLossEnabled = _settings.DynamicStopLoss.Enabled,
            DynamicStopLossTiers = _settings.DynamicStopLoss.Tiers.Select(t => 
                new StopTier { TriggerProfitPercent = t.TriggerProfitPercent, StopPercent = t.StopPercent }).ToList(),
            // Trimming
            EnableTrimming = _settings.EnableTrimming,
            TrimTriggerPercent = _settings.TrimTriggerPercent,
            TrimRatio = _settings.TrimRatio,
            TrimSlopeThreshold = _settings.TrimSlopeThreshold,
            TrimCooldownSeconds = _settings.TrimCooldownSeconds,
            // Profit
            ProfitReinvestmentPercent = _settings.ProfitReinvestmentPercent,
            // Direction switch cooldown
            DirectionSwitchCooldownSeconds = _settings.DirectionSwitchCooldownSeconds,
            // Mean reversion strategy
            BaseDefaultStrategy = _settings.BaseDefaultStrategy,
            PhDefaultStrategy = _settings.PhDefaultStrategy,
            ChopOverrideEnabled = _settings.ChopOverrideEnabled,
            ChopUpperThreshold = _settings.ChopUpperThreshold,
            ChopLowerThreshold = _settings.ChopLowerThreshold,
            ChopTrendExitThreshold = _settings.ChopTrendExitThreshold,
            BollingerWindow = _settings.BollingerWindow,
            BollingerMultiplier = _settings.BollingerMultiplier,
            ChopPeriod = _settings.ChopPeriod,
            ChopCandleSeconds = _settings.ChopCandleSeconds,
            MrEntryLowPctB = _settings.MrEntryLowPctB,
            MrEntryHighPctB = _settings.MrEntryHighPctB,
            MrExitPctB = _settings.MrExitPctB,
            MeanRevStopPercent = _settings.MeanRevStopPercent,
            MrAtrStopMultiplier = _settings.MrAtrStopMultiplier,
            MrRequireRsi = _settings.MrRequireRsi,
            MrRsiPeriod = _settings.MrRsiPeriod,
            MrRsiOversold = _settings.MrRsiOversold,
            MrRsiOverbought = _settings.MrRsiOverbought,
        };
    }
    
    private void RestoreBaseValues()
    {
        // Signal generation
        _settings.MinVelocityThreshold = _baseValues.MinVelocityThreshold;
        _settings.EntryVelocityMultiplier = _baseValues.EntryVelocityMultiplier;
        _settings.SMAWindowSeconds = _baseValues.SMAWindowSeconds;
        _settings.SlopeWindowSize = _baseValues.SlopeWindowSize;
        _settings.ChopThresholdPercent = _baseValues.ChopThresholdPercent;
        _settings.MinChopAbsolute = _baseValues.MinChopAbsolute;
        _settings.TrendWindowSeconds = _baseValues.TrendWindowSeconds;
        _settings.EnableAdaptiveTrendWindow = _baseValues.EnableAdaptiveTrendWindow;
        _settings.ShortTrendSlopeWindow = _baseValues.ShortTrendSlopeWindow;
        _settings.ShortTrendSlopeThreshold = _baseValues.ShortTrendSlopeThreshold;
        _settings.DriftModeEnabled = _baseValues.DriftModeEnabled;
        _settings.DriftModeConsecutiveTicks = _baseValues.DriftModeConsecutiveTicks;
        _settings.DriftModeMinDisplacementPercent = _baseValues.DriftModeMinDisplacementPercent;
        _settings.DriftModeAtrMultiplier = _baseValues.DriftModeAtrMultiplier;
        _settings.DriftTrailingStopPercent = _baseValues.DriftTrailingStopPercent;
        _settings.DisplacementReentryEnabled = _baseValues.DisplacementReentryEnabled;
        _settings.DisplacementReentryPercent = _baseValues.DisplacementReentryPercent;
        _settings.DisplacementAtrMultiplier = _baseValues.DisplacementAtrMultiplier;
        _settings.DisplacementChopThreshold = _baseValues.DisplacementChopThreshold;
        _settings.DisplacementBbwLookback = _baseValues.DisplacementBbwLookback;
        _settings.DisplacementSlopeWindow = _baseValues.DisplacementSlopeWindow;
        _settings.DisplacementMinSlope = _baseValues.DisplacementMinSlope;
        _settings.EntryConfirmationTicks = _baseValues.EntryConfirmationTicks;
        _settings.BearEntryConfirmationTicks = _baseValues.BearEntryConfirmationTicks;
        _settings.BullOnlyMode = _baseValues.BullOnlyMode;
        // Exit strategy
        _settings.ExitStrategy.ScalpWaitSeconds = _baseValues.ScalpWaitSeconds;
        _settings.ExitStrategy.TrendWaitSeconds = _baseValues.TrendWaitSeconds;
        _settings.ExitStrategy.TrendConfidenceThreshold = _baseValues.TrendConfidenceThreshold;
        _settings.ExitStrategy.HoldNeutralIfUnderwater = _baseValues.HoldNeutralIfUnderwater;
        // Trade execution
        _settings.TrailingStopPercent = _baseValues.TrailingStopPercent;
        _settings.TrendRescueTrailingStopPercent = _baseValues.TrendRescueTrailingStopPercent;
        _settings.UseMarketableLimits = _baseValues.UseMarketableLimits;
        _settings.UseIocOrders = _baseValues.UseIocOrders;
        _settings.PendingOrderTimeoutSeconds = _baseValues.PendingOrderTimeoutSeconds;
        _settings.IocLimitOffsetCents = _baseValues.IocLimitOffsetCents;
        _settings.IocRetryStepCents = _baseValues.IocRetryStepCents;
        _settings.MaxSlippagePercent = _baseValues.MaxSlippagePercent;
        _settings.MaxChaseDeviationPercent = _baseValues.MaxChaseDeviationPercent;
        _settings.LastEntryMinutesBeforeClose = _baseValues.LastEntryMinutesBeforeClose;
        // Dynamic stop loss
        _settings.DynamicStopLoss.Enabled = _baseValues.DynamicStopLossEnabled;
        _settings.DynamicStopLoss.Tiers = _baseValues.DynamicStopLossTiers
            .Select(t => new StopTier { TriggerProfitPercent = t.TriggerProfitPercent, StopPercent = t.StopPercent })
            .ToList();
        // Trimming
        _settings.EnableTrimming = _baseValues.EnableTrimming;
        _settings.TrimTriggerPercent = _baseValues.TrimTriggerPercent;
        _settings.TrimRatio = _baseValues.TrimRatio;
        _settings.TrimSlopeThreshold = _baseValues.TrimSlopeThreshold;
        _settings.TrimCooldownSeconds = _baseValues.TrimCooldownSeconds;
        // Profit
        _settings.ProfitReinvestmentPercent = _baseValues.ProfitReinvestmentPercent;
        // Direction switch cooldown
        _settings.DirectionSwitchCooldownSeconds = _baseValues.DirectionSwitchCooldownSeconds;
        // Mean reversion strategy
        _settings.BaseDefaultStrategy = _baseValues.BaseDefaultStrategy;
        _settings.PhDefaultStrategy = _baseValues.PhDefaultStrategy;
        _settings.ChopOverrideEnabled = _baseValues.ChopOverrideEnabled;
        _settings.ChopUpperThreshold = _baseValues.ChopUpperThreshold;
        _settings.ChopLowerThreshold = _baseValues.ChopLowerThreshold;
        _settings.ChopTrendExitThreshold = _baseValues.ChopTrendExitThreshold;
        _settings.BollingerWindow = _baseValues.BollingerWindow;
        _settings.BollingerMultiplier = _baseValues.BollingerMultiplier;
        _settings.ChopPeriod = _baseValues.ChopPeriod;
        _settings.ChopCandleSeconds = _baseValues.ChopCandleSeconds;
        _settings.MrEntryLowPctB = _baseValues.MrEntryLowPctB;
        _settings.MrEntryHighPctB = _baseValues.MrEntryHighPctB;
        _settings.MrExitPctB = _baseValues.MrExitPctB;
        _settings.MeanRevStopPercent = _baseValues.MeanRevStopPercent;
        _settings.MrAtrStopMultiplier = _baseValues.MrAtrStopMultiplier;
        _settings.MrRequireRsi = _baseValues.MrRequireRsi;
        _settings.MrRsiPeriod = _baseValues.MrRsiPeriod;
        _settings.MrRsiOversold = _baseValues.MrRsiOversold;
        _settings.MrRsiOverbought = _baseValues.MrRsiOverbought;
    }
    
    private void ApplyOverrides(TradingSettingsOverrides o)
    {
        // Signal generation
        if (o.MinVelocityThreshold.HasValue) _settings.MinVelocityThreshold = o.MinVelocityThreshold.Value;
        if (o.EntryVelocityMultiplier.HasValue) _settings.EntryVelocityMultiplier = o.EntryVelocityMultiplier.Value;
        if (o.SMAWindowSeconds.HasValue) _settings.SMAWindowSeconds = o.SMAWindowSeconds.Value;
        if (o.SlopeWindowSize.HasValue) _settings.SlopeWindowSize = o.SlopeWindowSize.Value;
        if (o.ChopThresholdPercent.HasValue) _settings.ChopThresholdPercent = o.ChopThresholdPercent.Value;
        if (o.MinChopAbsolute.HasValue) _settings.MinChopAbsolute = o.MinChopAbsolute.Value;
        if (o.TrendWindowSeconds.HasValue) _settings.TrendWindowSeconds = o.TrendWindowSeconds.Value;
        if (o.EnableAdaptiveTrendWindow.HasValue) _settings.EnableAdaptiveTrendWindow = o.EnableAdaptiveTrendWindow.Value;
        if (o.ShortTrendSlopeWindow.HasValue) _settings.ShortTrendSlopeWindow = o.ShortTrendSlopeWindow.Value;
        if (o.ShortTrendSlopeThreshold.HasValue) _settings.ShortTrendSlopeThreshold = o.ShortTrendSlopeThreshold.Value;
        if (o.DriftModeEnabled.HasValue) _settings.DriftModeEnabled = o.DriftModeEnabled.Value;
        if (o.DriftModeConsecutiveTicks.HasValue) _settings.DriftModeConsecutiveTicks = o.DriftModeConsecutiveTicks.Value;
        if (o.DriftModeMinDisplacementPercent.HasValue) _settings.DriftModeMinDisplacementPercent = o.DriftModeMinDisplacementPercent.Value;
        if (o.DriftModeAtrMultiplier.HasValue) _settings.DriftModeAtrMultiplier = o.DriftModeAtrMultiplier.Value;
        if (o.DriftTrailingStopPercent.HasValue) _settings.DriftTrailingStopPercent = o.DriftTrailingStopPercent.Value;
        if (o.DisplacementReentryEnabled.HasValue) _settings.DisplacementReentryEnabled = o.DisplacementReentryEnabled.Value;
        if (o.DisplacementReentryPercent.HasValue) _settings.DisplacementReentryPercent = o.DisplacementReentryPercent.Value;
        if (o.DisplacementAtrMultiplier.HasValue) _settings.DisplacementAtrMultiplier = o.DisplacementAtrMultiplier.Value;
        if (o.DisplacementChopThreshold.HasValue) _settings.DisplacementChopThreshold = o.DisplacementChopThreshold.Value;
        if (o.DisplacementBbwLookback.HasValue) _settings.DisplacementBbwLookback = o.DisplacementBbwLookback.Value;
        if (o.DisplacementSlopeWindow.HasValue) _settings.DisplacementSlopeWindow = o.DisplacementSlopeWindow.Value;
        if (o.DisplacementMinSlope.HasValue) _settings.DisplacementMinSlope = o.DisplacementMinSlope.Value;
        if (o.EntryConfirmationTicks.HasValue) _settings.EntryConfirmationTicks = o.EntryConfirmationTicks.Value;
        if (o.BearEntryConfirmationTicks.HasValue) _settings.BearEntryConfirmationTicks = o.BearEntryConfirmationTicks.Value;
        if (o.BullOnlyMode.HasValue) _settings.BullOnlyMode = o.BullOnlyMode.Value;
        // Exit strategy (flattened → nested)
        if (o.ScalpWaitSeconds.HasValue) _settings.ExitStrategy.ScalpWaitSeconds = o.ScalpWaitSeconds.Value;
        if (o.TrendWaitSeconds.HasValue) _settings.ExitStrategy.TrendWaitSeconds = o.TrendWaitSeconds.Value;
        if (o.TrendConfidenceThreshold.HasValue) _settings.ExitStrategy.TrendConfidenceThreshold = o.TrendConfidenceThreshold.Value;
        if (o.HoldNeutralIfUnderwater.HasValue) _settings.ExitStrategy.HoldNeutralIfUnderwater = o.HoldNeutralIfUnderwater.Value;
        // Trade execution
        if (o.TrailingStopPercent.HasValue) _settings.TrailingStopPercent = o.TrailingStopPercent.Value;
        if (o.TrendRescueTrailingStopPercent.HasValue) _settings.TrendRescueTrailingStopPercent = o.TrendRescueTrailingStopPercent.Value;
        if (o.UseMarketableLimits.HasValue) _settings.UseMarketableLimits = o.UseMarketableLimits.Value;
        if (o.UseIocOrders.HasValue) _settings.UseIocOrders = o.UseIocOrders.Value;
        if (o.PendingOrderTimeoutSeconds.HasValue) _settings.PendingOrderTimeoutSeconds = o.PendingOrderTimeoutSeconds.Value;
        if (o.IocLimitOffsetCents.HasValue) _settings.IocLimitOffsetCents = o.IocLimitOffsetCents.Value;
        if (o.IocRetryStepCents.HasValue) _settings.IocRetryStepCents = o.IocRetryStepCents.Value;
        if (o.MaxSlippagePercent.HasValue) _settings.MaxSlippagePercent = o.MaxSlippagePercent.Value;
        if (o.MaxChaseDeviationPercent.HasValue) _settings.MaxChaseDeviationPercent = o.MaxChaseDeviationPercent.Value;
        if (o.LastEntryMinutesBeforeClose.HasValue) _settings.LastEntryMinutesBeforeClose = o.LastEntryMinutesBeforeClose.Value;
        // Dynamic stop loss (flattened → nested)
        if (o.DynamicStopLossEnabled.HasValue) _settings.DynamicStopLoss.Enabled = o.DynamicStopLossEnabled.Value;
        if (o.DynamicStopLossTiers != null)
        {
            _settings.DynamicStopLoss.Tiers = o.DynamicStopLossTiers
                .Select(t => new StopTier { TriggerProfitPercent = t.TriggerProfitPercent, StopPercent = t.StopPercent })
                .ToList();
        }
        // Trimming
        if (o.EnableTrimming.HasValue) _settings.EnableTrimming = o.EnableTrimming.Value;
        if (o.TrimTriggerPercent.HasValue) _settings.TrimTriggerPercent = o.TrimTriggerPercent.Value;
        if (o.TrimRatio.HasValue) _settings.TrimRatio = o.TrimRatio.Value;
        if (o.TrimSlopeThreshold.HasValue) _settings.TrimSlopeThreshold = o.TrimSlopeThreshold.Value;
        if (o.TrimCooldownSeconds.HasValue) _settings.TrimCooldownSeconds = o.TrimCooldownSeconds.Value;
        // Profit
        if (o.ProfitReinvestmentPercent.HasValue) _settings.ProfitReinvestmentPercent = o.ProfitReinvestmentPercent.Value;
        // Direction switch cooldown
        if (o.DirectionSwitchCooldownSeconds.HasValue) _settings.DirectionSwitchCooldownSeconds = o.DirectionSwitchCooldownSeconds.Value;
        // Mean reversion strategy
        if (o.BaseDefaultStrategy.HasValue) _settings.BaseDefaultStrategy = o.BaseDefaultStrategy.Value;
        if (o.PhDefaultStrategy.HasValue) _settings.PhDefaultStrategy = o.PhDefaultStrategy.Value;
        if (o.ChopOverrideEnabled.HasValue) _settings.ChopOverrideEnabled = o.ChopOverrideEnabled.Value;
        if (o.ChopUpperThreshold.HasValue) _settings.ChopUpperThreshold = o.ChopUpperThreshold.Value;
        if (o.ChopLowerThreshold.HasValue) _settings.ChopLowerThreshold = o.ChopLowerThreshold.Value;
        if (o.ChopTrendExitThreshold.HasValue) _settings.ChopTrendExitThreshold = o.ChopTrendExitThreshold.Value;
        if (o.BollingerWindow.HasValue) _settings.BollingerWindow = o.BollingerWindow.Value;
        if (o.BollingerMultiplier.HasValue) _settings.BollingerMultiplier = o.BollingerMultiplier.Value;
        if (o.ChopPeriod.HasValue) _settings.ChopPeriod = o.ChopPeriod.Value;
        if (o.ChopCandleSeconds.HasValue) _settings.ChopCandleSeconds = o.ChopCandleSeconds.Value;
        if (o.MrEntryLowPctB.HasValue) _settings.MrEntryLowPctB = o.MrEntryLowPctB.Value;
        if (o.MrEntryHighPctB.HasValue) _settings.MrEntryHighPctB = o.MrEntryHighPctB.Value;
        if (o.MrExitPctB.HasValue) _settings.MrExitPctB = o.MrExitPctB.Value;
        if (o.MeanRevStopPercent.HasValue) _settings.MeanRevStopPercent = o.MeanRevStopPercent.Value;
        if (o.MrAtrStopMultiplier.HasValue) _settings.MrAtrStopMultiplier = o.MrAtrStopMultiplier.Value;
        if (o.MrRequireRsi.HasValue) _settings.MrRequireRsi = o.MrRequireRsi.Value;
        if (o.MrRsiPeriod.HasValue) _settings.MrRsiPeriod = o.MrRsiPeriod.Value;
        if (o.MrRsiOversold.HasValue) _settings.MrRsiOversold = o.MrRsiOversold.Value;
        if (o.MrRsiOverbought.HasValue) _settings.MrRsiOverbought = o.MrRsiOverbought.Value;
    }
    
    private void LogChangedSettings(TimeBasedRule? rule)
    {
        if (rule == null)
        {
            _logger.LogInformation("[TIME RULES] Restored to base config values.");
            return;
        }
        
        var o = rule.Overrides;
        var changes = new List<string>();
        
        if (o.MinVelocityThreshold.HasValue) changes.Add($"MinVelocity={o.MinVelocityThreshold.Value}");
        if (o.EntryVelocityMultiplier.HasValue) changes.Add($"EntryVelMult={o.EntryVelocityMultiplier.Value}");
        if (o.SMAWindowSeconds.HasValue) changes.Add($"SMAWindow={o.SMAWindowSeconds.Value}s");
        if (o.SlopeWindowSize.HasValue) changes.Add($"SlopeWindow={o.SlopeWindowSize.Value}");
        if (o.ChopThresholdPercent.HasValue) changes.Add($"ChopThreshold={o.ChopThresholdPercent.Value:P4}");
        if (o.MinChopAbsolute.HasValue) changes.Add($"MinChop={o.MinChopAbsolute.Value}");
        if (o.TrendWindowSeconds.HasValue) changes.Add($"TrendWindow={o.TrendWindowSeconds.Value}s");
        if (o.EnableAdaptiveTrendWindow.HasValue) changes.Add($"AdaptiveTrend={o.EnableAdaptiveTrendWindow.Value}");
        if (o.ShortTrendSlopeWindow.HasValue) changes.Add($"ShortTrendSlope={o.ShortTrendSlopeWindow.Value}");
        if (o.ShortTrendSlopeThreshold.HasValue) changes.Add($"ShortTrendThreshold={o.ShortTrendSlopeThreshold.Value}");
        if (o.DriftModeEnabled.HasValue) changes.Add($"DriftMode={o.DriftModeEnabled.Value}");
        if (o.DriftModeConsecutiveTicks.HasValue) changes.Add($"DriftTicks={o.DriftModeConsecutiveTicks.Value}");
        if (o.DriftModeMinDisplacementPercent.HasValue) changes.Add($"DriftMinDisp={o.DriftModeMinDisplacementPercent.Value:P2}");
        if (o.DriftModeAtrMultiplier.HasValue) changes.Add($"DriftAtrMult={o.DriftModeAtrMultiplier.Value:F2}");
        if (o.DriftTrailingStopPercent.HasValue) changes.Add($"DriftStop={o.DriftTrailingStopPercent.Value:P2}");
        if (o.DisplacementReentryEnabled.HasValue) changes.Add($"DisplacementReentry={o.DisplacementReentryEnabled.Value}");
        if (o.DisplacementReentryPercent.HasValue) changes.Add($"DisplacementPct={o.DisplacementReentryPercent.Value:P2}");
        if (o.DisplacementAtrMultiplier.HasValue) changes.Add($"DispAtrMult={o.DisplacementAtrMultiplier.Value:F2}");
        if (o.DisplacementChopThreshold.HasValue) changes.Add($"DispChopThresh={o.DisplacementChopThreshold.Value:F0}");
        if (o.DisplacementBbwLookback.HasValue) changes.Add($"DispBbwLookback={o.DisplacementBbwLookback.Value}");
        if (o.DisplacementSlopeWindow.HasValue) changes.Add($"DispSlopeWin={o.DisplacementSlopeWindow.Value}");
        if (o.DisplacementMinSlope.HasValue) changes.Add($"DispMinSlope={o.DisplacementMinSlope.Value:E2}");
        if (o.BearEntryConfirmationTicks.HasValue) changes.Add($"BearConfirm={o.BearEntryConfirmationTicks.Value}");
        if (o.BullOnlyMode.HasValue) changes.Add($"BullOnly={o.BullOnlyMode.Value}");
        if (o.TrailingStopPercent.HasValue) changes.Add($"TrailingStop={o.TrailingStopPercent.Value:P2}");
        if (o.TrendRescueTrailingStopPercent.HasValue) changes.Add($"TrendRescueStop={o.TrendRescueTrailingStopPercent.Value:P2}");
        if (o.TrendWaitSeconds.HasValue) changes.Add($"TrendWait={o.TrendWaitSeconds.Value}s");
        if (o.TrendConfidenceThreshold.HasValue) changes.Add($"TrendConfidence={o.TrendConfidenceThreshold.Value}");
        if (o.UseMarketableLimits.HasValue) changes.Add($"MarketableLimits={o.UseMarketableLimits.Value}");
        if (o.UseIocOrders.HasValue) changes.Add($"IOC={o.UseIocOrders.Value}");
        if (o.PendingOrderTimeoutSeconds.HasValue) changes.Add($"PendingOrderTimeout={o.PendingOrderTimeoutSeconds.Value}s");
        if (o.EnableTrimming.HasValue) changes.Add($"Trimming={o.EnableTrimming.Value}");
        if (o.DynamicStopLossTiers != null) changes.Add($"StopTiers={o.DynamicStopLossTiers.Count}");
        if (o.BaseDefaultStrategy.HasValue) changes.Add($"BaseStrategy={o.BaseDefaultStrategy.Value}");
        if (o.PhDefaultStrategy.HasValue) changes.Add($"PhStrategy={o.PhDefaultStrategy.Value}");
        if (o.ChopOverrideEnabled.HasValue) changes.Add($"ChopOverride={o.ChopOverrideEnabled.Value}");
        if (o.ChopTrendExitThreshold.HasValue) changes.Add($"ChopTrendExit={o.ChopTrendExitThreshold.Value}");
        if (o.BollingerWindow.HasValue) changes.Add($"BBWindow={o.BollingerWindow.Value}");
        if (o.MrEntryLowPctB.HasValue) changes.Add($"MREntryLow={o.MrEntryLowPctB.Value}");
        if (o.MrEntryHighPctB.HasValue) changes.Add($"MREntryHigh={o.MrEntryHighPctB.Value}");
        if (o.MrExitPctB.HasValue) changes.Add($"MRExitPctB={o.MrExitPctB.Value}");
        if (o.MeanRevStopPercent.HasValue) changes.Add($"MRStop={o.MeanRevStopPercent.Value:P2}");
        if (o.MrAtrStopMultiplier.HasValue) changes.Add($"MRAtrMult={o.MrAtrStopMultiplier.Value}");
        if (o.MrRequireRsi.HasValue) changes.Add($"MRRsi={o.MrRequireRsi.Value}");
        if (o.MrRsiPeriod.HasValue) changes.Add($"MRRsiPeriod={o.MrRsiPeriod.Value}");
        if (o.MrRsiOversold.HasValue) changes.Add($"MRRsiOS={o.MrRsiOversold.Value}");
        if (o.MrRsiOverbought.HasValue) changes.Add($"MRRsiOB={o.MrRsiOverbought.Value}");
        
        if (changes.Count > 0)
        {
            _logger.LogInformation("[TIME RULES] Overrides: {Changes}", string.Join(" | ", changes));
        }
    }
}

/// <summary>
/// Immutable snapshot of settings values for before/after comparison during phase transitions.
/// Used by AnalystEngine to detect whether indicator calculators need rebuilding.
/// </summary>
public class SettingsSnapshot
{
    // Signal generation
    public decimal MinVelocityThreshold { get; init; }
    public decimal EntryVelocityMultiplier { get; init; }
    public int SMAWindowSeconds { get; init; }
    public int SlopeWindowSize { get; init; }
    public decimal ChopThresholdPercent { get; init; }
    public decimal MinChopAbsolute { get; init; }
    public int TrendWindowSeconds { get; init; }
    public bool EnableAdaptiveTrendWindow { get; init; }
    public int ShortTrendSlopeWindow { get; init; }
    public decimal ShortTrendSlopeThreshold { get; init; }
    public bool DriftModeEnabled { get; init; }
    public int DriftModeConsecutiveTicks { get; init; }
    public decimal DriftModeMinDisplacementPercent { get; init; }
    public decimal DriftModeAtrMultiplier { get; init; }
    public decimal DriftTrailingStopPercent { get; init; }
    public bool DisplacementReentryEnabled { get; init; }
    public decimal DisplacementReentryPercent { get; init; }
    public decimal DisplacementAtrMultiplier { get; init; }
    public decimal DisplacementChopThreshold { get; init; }
    public int DisplacementBbwLookback { get; init; }
    public int DisplacementSlopeWindow { get; init; }
    public decimal DisplacementMinSlope { get; init; }
    public int EntryConfirmationTicks { get; init; }
    public int BearEntryConfirmationTicks { get; init; }
    public bool BullOnlyMode { get; init; }
    // Exit strategy
    public int ScalpWaitSeconds { get; init; }
    public int TrendWaitSeconds { get; init; }
    public double TrendConfidenceThreshold { get; init; }
    public bool HoldNeutralIfUnderwater { get; init; }
    // Trade execution
    public decimal TrailingStopPercent { get; init; }
    public decimal TrendRescueTrailingStopPercent { get; init; }
    public bool UseMarketableLimits { get; init; }
    public bool UseIocOrders { get; init; }
    public int PendingOrderTimeoutSeconds { get; init; }
    public decimal IocLimitOffsetCents { get; init; }
    public decimal IocRetryStepCents { get; init; }
    public decimal MaxSlippagePercent { get; init; }
    public decimal MaxChaseDeviationPercent { get; init; }
    public decimal LastEntryMinutesBeforeClose { get; init; }
    // Dynamic stop loss
    public bool DynamicStopLossEnabled { get; init; }
    public List<StopTier> DynamicStopLossTiers { get; init; } = new();
    // Trimming
    public bool EnableTrimming { get; init; }
    public decimal TrimTriggerPercent { get; init; }
    public decimal TrimRatio { get; init; }
    public decimal TrimSlopeThreshold { get; init; }
    public int TrimCooldownSeconds { get; init; }
    // Profit
    public decimal ProfitReinvestmentPercent { get; init; }
    // Direction switch cooldown
    public int DirectionSwitchCooldownSeconds { get; init; }
    // Mean reversion strategy
    public StrategyMode BaseDefaultStrategy { get; init; }
    public StrategyMode PhDefaultStrategy { get; init; }
    public bool ChopOverrideEnabled { get; init; }
    public decimal ChopUpperThreshold { get; init; }
    public decimal ChopLowerThreshold { get; init; }
    public decimal ChopTrendExitThreshold { get; init; }
    public int BollingerWindow { get; init; }
    public decimal BollingerMultiplier { get; init; }
    public int ChopPeriod { get; init; }
    public int ChopCandleSeconds { get; init; }
    public decimal MrEntryLowPctB { get; init; }
    public decimal MrEntryHighPctB { get; init; }
    public decimal MrExitPctB { get; init; }
    public decimal MeanRevStopPercent { get; init; }
    public decimal MrAtrStopMultiplier { get; init; }
    public bool MrRequireRsi { get; init; }
    public int MrRsiPeriod { get; init; }
    public decimal MrRsiOversold { get; init; }
    public decimal MrRsiOverbought { get; init; }
}
