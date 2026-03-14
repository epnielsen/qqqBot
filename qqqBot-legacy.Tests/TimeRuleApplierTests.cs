using qqqBot;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace qqqBot.Tests;

public class TimeRuleApplierTests
{
    private static TradingSettings CreateBaseSettings()
    {
        return new TradingSettings
        {
            BullSymbol = "TQQQ",
            BearSymbol = "SQQQ",
            StartingAmount = 10000m,
            MaxSlippagePercent = 0.002m,
            // Mid-Day Lull base values
            MinVelocityThreshold = 0.000001m,
            SMAWindowSeconds = 180,
            SlopeWindowSize = 20,
            ChopThresholdPercent = 0.0011m,
            MinChopAbsolute = 0.02m,
            TrendWindowSeconds = 1800,
            TrailingStopPercent = 0.002m,
            UseMarketableLimits = true,
            UseIocOrders = true,
            IocLimitOffsetCents = 8m,
            IocRetryStepCents = 5m,
            EnableTrimming = true,
            TrimTriggerPercent = 0.0025m,
            TrimSlopeThreshold = 0.000001m,
            TrimCooldownSeconds = 300,
            TrimRatio = 0.50m,
            ExitStrategy = new DynamicExitConfig
            {
                ScalpWaitSeconds = 30,
                TrendWaitSeconds = 300,
                TrendConfidenceThreshold = 0.00008,
                HoldNeutralIfUnderwater = true
            },
            DynamicStopLoss = new DynamicStopConfig
            {
                Enabled = true,
                Tiers = new List<StopTier>
                {
                    new() { TriggerProfitPercent = 0.003m, StopPercent = 0.0015m },
                    new() { TriggerProfitPercent = 0.005m, StopPercent = 0.001m },
                    new() { TriggerProfitPercent = 0.008m, StopPercent = 0.0008m }
                }
            },
            TimeRules = new List<TimeBasedRule>
            {
                new()
                {
                    Name = "Open Volatility",
                    StartTime = new TimeSpan(9, 30, 0),
                    EndTime = new TimeSpan(10, 30, 0),
                    Overrides = new TradingSettingsOverrides
                    {
                        MinVelocityThreshold = 0.00002m,
                        SMAWindowSeconds = 120,
                        ChopThresholdPercent = 0.001m,
                        MinChopAbsolute = 0.05m,
                        TrendWindowSeconds = 900,
                        TrendWaitSeconds = 180,
                        TrendConfidenceThreshold = 0.00012,
                        UseMarketableLimits = false,
                        UseIocOrders = false,
                        IocLimitOffsetCents = 20m,
                        IocRetryStepCents = 10m,
                        TrailingStopPercent = 0.005m,
                        EnableTrimming = false,
                        TrimTriggerPercent = 0.005m,
                        TrimSlopeThreshold = 0.000002m,
                        TrimCooldownSeconds = 60,
                        TrimRatio = 0.33m,
                        DynamicStopLossEnabled = true,
                        DynamicStopLossTiers = new List<StopTier>
                        {
                            new() { TriggerProfitPercent = 0.005m, StopPercent = 0.003m },
                            new() { TriggerProfitPercent = 0.008m, StopPercent = 0.002m },
                            new() { TriggerProfitPercent = 0.012m, StopPercent = 0.0015m }
                        }
                    }
                },
                new()
                {
                    Name = "Power Hour",
                    StartTime = new TimeSpan(14, 0, 0),
                    EndTime = new TimeSpan(16, 0, 0),
                    Overrides = new TradingSettingsOverrides
                    {
                        MinVelocityThreshold = 0.000002m,
                        SMAWindowSeconds = 120,
                        SlopeWindowSize = 15,
                        ChopThresholdPercent = 0.0015m,
                        TrendWaitSeconds = 120,
                        TrendConfidenceThreshold = 0.00012,
                        UseMarketableLimits = false,
                        UseIocOrders = false,
                        IocLimitOffsetCents = 10m,
                        TrailingStopPercent = 0.0015m,
                        TrimTriggerPercent = 0.003m,
                        TrimSlopeThreshold = 0.000002m,
                        TrimCooldownSeconds = 60,
                        DynamicStopLossEnabled = true,
                        DynamicStopLossTiers = new List<StopTier>
                        {
                            new() { TriggerProfitPercent = 0.003m, StopPercent = 0.001m },
                            new() { TriggerProfitPercent = 0.005m, StopPercent = 0.0008m },
                            new() { TriggerProfitPercent = 0.008m, StopPercent = 0.0006m }
                        }
                    }
                }
            }
        };
    }

    private static TimeRuleApplier CreateApplier(TradingSettings? settings = null)
    {
        settings ??= CreateBaseSettings();
        var logger = NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>();
        return new TimeRuleApplier(settings, logger);
    }

    // ====================================================================
    // BASIC PHASE DETECTION
    // ====================================================================

    [Fact]
    public void CheckAndApply_AtMarketOpen_SwitchesToOpenVolatility()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var changed = applier.CheckAndApply(new TimeSpan(9, 30, 0));

        Assert.True(changed);
        Assert.Equal("Open Volatility", applier.ActivePhaseName);
        Assert.Equal(0.00002m, settings.MinVelocityThreshold);
        Assert.Equal(120, settings.SMAWindowSeconds);
        Assert.Equal(0.001m, settings.ChopThresholdPercent);
        Assert.Equal(0.05m, settings.MinChopAbsolute);
        Assert.Equal(900, settings.TrendWindowSeconds);
        Assert.False(settings.UseMarketableLimits);
        Assert.False(settings.UseIocOrders);
        Assert.Equal(0.005m, settings.TrailingStopPercent);
        Assert.False(settings.EnableTrimming);
    }

    [Fact]
    public void CheckAndApply_AtMidDay_StaysInBaseConfig()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // First transition to Open to establish a non-null phase
        applier.CheckAndApply(new TimeSpan(9, 30, 0));

        // Now transition to mid-day (no rule matches → base config)
        var changed = applier.CheckAndApply(new TimeSpan(10, 30, 0));

        Assert.True(changed); // Phase changed from Open to Base
        Assert.Null(applier.ActivePhaseName);
        // Verify base values are restored
        Assert.Equal(0.000001m, settings.MinVelocityThreshold);
        Assert.Equal(180, settings.SMAWindowSeconds);
        Assert.True(settings.UseMarketableLimits);
        Assert.True(settings.UseIocOrders);
        Assert.Equal(0.002m, settings.TrailingStopPercent);
        Assert.True(settings.EnableTrimming);
    }

    [Fact]
    public void CheckAndApply_AtPowerHour_SwitchesToPowerHour()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var changed = applier.CheckAndApply(new TimeSpan(14, 0, 0));

        Assert.True(changed);
        Assert.Equal("Power Hour", applier.ActivePhaseName);
        Assert.Equal(0.000002m, settings.MinVelocityThreshold);
        Assert.Equal(120, settings.SMAWindowSeconds);
        Assert.Equal(15, settings.SlopeWindowSize);
        Assert.Equal(0.0015m, settings.ChopThresholdPercent);
        Assert.False(settings.UseMarketableLimits);
        Assert.False(settings.UseIocOrders);
        Assert.Equal(0.0015m, settings.TrailingStopPercent);
    }

    // ====================================================================
    // IDEMPOTENCE
    // ====================================================================

    [Fact]
    public void CheckAndApply_SamePhaseRepeated_ReturnsFalse()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // First call → phase change
        Assert.True(applier.CheckAndApply(new TimeSpan(9, 35, 0)));
        // Subsequent calls in same phase → no change
        Assert.False(applier.CheckAndApply(new TimeSpan(9, 36, 0)));
        Assert.False(applier.CheckAndApply(new TimeSpan(10, 0, 0)));
        Assert.False(applier.CheckAndApply(new TimeSpan(10, 29, 59)));
    }

    [Fact]
    public void CheckAndApply_NoRulesConfigured_AlwaysReturnsFalse()
    {
        var settings = CreateBaseSettings();
        settings.TimeRules.Clear();
        var applier = CreateApplier(settings);

        Assert.False(applier.CheckAndApply(new TimeSpan(9, 30, 0)));
        Assert.False(applier.CheckAndApply(new TimeSpan(14, 0, 0)));
        Assert.Null(applier.ActivePhaseName);
    }

    // ====================================================================
    // FULL DAY CYCLE
    // ====================================================================

    [Fact]
    public void FullDayCycle_Open_MidDay_PowerHour_Close()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // 09:30 → Open Volatility
        Assert.True(applier.CheckAndApply(new TimeSpan(9, 30, 0)));
        Assert.Equal("Open Volatility", applier.ActivePhaseName);
        Assert.Equal(120, settings.SMAWindowSeconds);

        // 10:30 → Mid-Day Lull (base config)
        Assert.True(applier.CheckAndApply(new TimeSpan(10, 30, 0)));
        Assert.Null(applier.ActivePhaseName);
        Assert.Equal(180, settings.SMAWindowSeconds);

        // 13:59 → still Mid-Day
        Assert.False(applier.CheckAndApply(new TimeSpan(13, 59, 59)));
        Assert.Null(applier.ActivePhaseName);

        // 14:00 → Power Hour
        Assert.True(applier.CheckAndApply(new TimeSpan(14, 0, 0)));
        Assert.Equal("Power Hour", applier.ActivePhaseName);
        Assert.Equal(15, settings.SlopeWindowSize);

        // 15:59 → still Power Hour
        Assert.False(applier.CheckAndApply(new TimeSpan(15, 59, 59)));
        Assert.Equal("Power Hour", applier.ActivePhaseName);

        // 16:00 → no rule (post-market → back to base)
        Assert.True(applier.CheckAndApply(new TimeSpan(16, 0, 0)));
        Assert.Null(applier.ActivePhaseName);
        Assert.Equal(20, settings.SlopeWindowSize); // Restored to base
    }

    // ====================================================================
    // BASE VALUE RESTORATION
    // ====================================================================

    [Fact]
    public void PhaseTransition_RestoresAllBaseValues_BeforeApplyingNewOverrides()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Open Volatility modifies many settings
        applier.CheckAndApply(new TimeSpan(9, 30, 0));
        Assert.Equal(0.00002m, settings.MinVelocityThreshold);
        Assert.False(settings.UseIocOrders);
        Assert.Equal(20m, settings.IocLimitOffsetCents);

        // Transition to Power Hour — should NOT carry over Open's changes
        applier.CheckAndApply(new TimeSpan(14, 0, 0));
        Assert.Equal(0.000002m, settings.MinVelocityThreshold); // Power Hour's value, not Open's
        Assert.False(settings.UseIocOrders); // Power Hour also overrides this
        Assert.Equal(10m, settings.IocLimitOffsetCents); // Power Hour's value, not Open's 20

        // Back to base (no rule matches)
        applier.CheckAndApply(new TimeSpan(16, 0, 0));
        Assert.Equal(0.000001m, settings.MinVelocityThreshold); // Base restored
        Assert.True(settings.UseIocOrders); // Base restored
        Assert.Equal(8m, settings.IocLimitOffsetCents); // Base restored
    }

    // ====================================================================
    // DYNAMIC STOP LOSS TIERS
    // ====================================================================

    [Fact]
    public void DynamicStopLoss_Tiers_OverriddenPerPhase()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Base tiers
        Assert.Equal(3, settings.DynamicStopLoss.Tiers.Count);
        Assert.Equal(0.003m, settings.DynamicStopLoss.Tiers[0].TriggerProfitPercent);
        Assert.Equal(0.0015m, settings.DynamicStopLoss.Tiers[0].StopPercent);

        // Open Volatility → wider tiers
        applier.CheckAndApply(new TimeSpan(9, 30, 0));
        Assert.Equal(3, settings.DynamicStopLoss.Tiers.Count);
        Assert.Equal(0.005m, settings.DynamicStopLoss.Tiers[0].TriggerProfitPercent);
        Assert.Equal(0.003m, settings.DynamicStopLoss.Tiers[0].StopPercent);

        // Power Hour → tighter tiers
        applier.CheckAndApply(new TimeSpan(14, 0, 0));
        Assert.Equal(3, settings.DynamicStopLoss.Tiers.Count);
        Assert.Equal(0.003m, settings.DynamicStopLoss.Tiers[0].TriggerProfitPercent);
        Assert.Equal(0.001m, settings.DynamicStopLoss.Tiers[0].StopPercent);

        // Back to base → original tiers
        applier.CheckAndApply(new TimeSpan(16, 0, 0));
        Assert.Equal(0.003m, settings.DynamicStopLoss.Tiers[0].TriggerProfitPercent);
        Assert.Equal(0.0015m, settings.DynamicStopLoss.Tiers[0].StopPercent);
    }

    // ====================================================================
    // EXIT STRATEGY OVERRIDES
    // ====================================================================

    [Fact]
    public void ExitStrategy_OverriddenPerPhase()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Base values
        Assert.Equal(300, settings.ExitStrategy.TrendWaitSeconds);
        Assert.Equal(0.00008, settings.ExitStrategy.TrendConfidenceThreshold);

        // Open Volatility
        applier.CheckAndApply(new TimeSpan(9, 30, 0));
        Assert.Equal(180, settings.ExitStrategy.TrendWaitSeconds);
        Assert.Equal(0.00012, settings.ExitStrategy.TrendConfidenceThreshold);

        // Power Hour
        applier.CheckAndApply(new TimeSpan(14, 0, 0));
        Assert.Equal(120, settings.ExitStrategy.TrendWaitSeconds);
        Assert.Equal(0.00012, settings.ExitStrategy.TrendConfidenceThreshold);

        // Back to base
        applier.CheckAndApply(new TimeSpan(16, 0, 0));
        Assert.Equal(300, settings.ExitStrategy.TrendWaitSeconds);
        Assert.Equal(0.00008, settings.ExitStrategy.TrendConfidenceThreshold);
    }

    // ====================================================================
    // INDICATOR SETTINGS CHANGED DETECTION
    // ====================================================================

    [Fact]
    public void IndicatorSettingsChanged_DetectsWindowSizeChanges()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Take snapshot before phase change
        var before = applier.SnapshotCurrentSettings();

        // Transition to Open Volatility (SMAWindowSeconds 180→120, TrendWindowSeconds 1800→900)
        applier.CheckAndApply(new TimeSpan(9, 30, 0));

        Assert.True(applier.IndicatorSettingsChanged(before));
    }

    [Fact]
    public void IndicatorSettingsChanged_ReturnsFalse_WhenOnlyTradeSettingsChange()
    {
        var settings = CreateBaseSettings();
        // Create a rule that only changes trade execution settings
        settings.TimeRules.Clear();
        settings.TimeRules.Add(new TimeBasedRule
        {
            Name = "Trade Only Change",
            StartTime = new TimeSpan(11, 0, 0),
            EndTime = new TimeSpan(12, 0, 0),
            Overrides = new TradingSettingsOverrides
            {
                TrailingStopPercent = 0.003m,
                UseMarketableLimits = false
            }
        });
        var applier = CreateApplier(settings);

        var before = applier.SnapshotCurrentSettings();
        applier.CheckAndApply(new TimeSpan(11, 0, 0));

        Assert.False(applier.IndicatorSettingsChanged(before));
    }

    [Fact]
    public void IndicatorSettingsChanged_DetectsSlopeWindowChange()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var before = applier.SnapshotCurrentSettings();

        // Power Hour changes SlopeWindowSize 20→15
        applier.CheckAndApply(new TimeSpan(14, 0, 0));

        Assert.True(applier.IndicatorSettingsChanged(before));
    }

    // ====================================================================
    // SETTINGS CHANGED FLAG
    // ====================================================================

    [Fact]
    public void SettingsChangedSinceLastCheck_TracksProperly()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // No phase change yet
        Assert.False(applier.SettingsChangedSinceLastCheck);

        // Phase change
        applier.CheckAndApply(new TimeSpan(9, 30, 0));
        Assert.True(applier.SettingsChangedSinceLastCheck);

        // Same phase again — resets to false
        applier.CheckAndApply(new TimeSpan(9, 31, 0));
        Assert.False(applier.SettingsChangedSinceLastCheck);
    }

    // ====================================================================
    // EDGE CASES
    // ====================================================================

    [Fact]
    public void TimeBasedRule_IsActive_InclusiveStart_ExclusiveEnd()
    {
        var rule = new TimeBasedRule
        {
            Name = "Test",
            StartTime = new TimeSpan(9, 30, 0),
            EndTime = new TimeSpan(10, 30, 0),
            Overrides = new TradingSettingsOverrides()
        };

        Assert.True(rule.IsActive(new TimeSpan(9, 30, 0)));    // Start inclusive
        Assert.True(rule.IsActive(new TimeSpan(10, 0, 0)));     // Middle
        Assert.True(rule.IsActive(new TimeSpan(10, 29, 59)));   // Just before end
        Assert.False(rule.IsActive(new TimeSpan(10, 30, 0)));   // End exclusive
        Assert.False(rule.IsActive(new TimeSpan(9, 29, 59)));   // Before start
    }

    [Fact]
    public void NullOverrideProperties_DoNotOverwriteSettings()
    {
        var settings = CreateBaseSettings();
        // Create a rule with minimal overrides (most properties null)
        settings.TimeRules.Clear();
        settings.TimeRules.Add(new TimeBasedRule
        {
            Name = "Minimal",
            StartTime = new TimeSpan(11, 0, 0),
            EndTime = new TimeSpan(12, 0, 0),
            Overrides = new TradingSettingsOverrides
            {
                MinVelocityThreshold = 0.005m
                // All other properties remain null → should NOT affect settings
            }
        });
        var applier = CreateApplier(settings);

        // Record base values
        var baseSmaWindow = settings.SMAWindowSeconds;
        var baseTrimming = settings.EnableTrimming;
        var baseIoc = settings.UseIocOrders;

        applier.CheckAndApply(new TimeSpan(11, 0, 0));

        Assert.Equal(0.005m, settings.MinVelocityThreshold); // Overridden
        Assert.Equal(baseSmaWindow, settings.SMAWindowSeconds); // NOT overridden
        Assert.Equal(baseTrimming, settings.EnableTrimming); // NOT overridden
        Assert.Equal(baseIoc, settings.UseIocOrders); // NOT overridden
    }

    [Fact]
    public void PreMarket_NoRuleActive_StaysInBase()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Before market open — no rules match
        Assert.False(applier.CheckAndApply(new TimeSpan(8, 0, 0)));
        Assert.Null(applier.ActivePhaseName);
    }

    // ====================================================================
    // SNAPSHOT ACCURACY
    // ====================================================================

    [Fact]
    public void SnapshotCurrentSettings_CapturesAllValues()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var snapshot = applier.SnapshotCurrentSettings();

        Assert.Equal(settings.MinVelocityThreshold, snapshot.MinVelocityThreshold);
        Assert.Equal(settings.SMAWindowSeconds, snapshot.SMAWindowSeconds);
        Assert.Equal(settings.SlopeWindowSize, snapshot.SlopeWindowSize);
        Assert.Equal(settings.ChopThresholdPercent, snapshot.ChopThresholdPercent);
        Assert.Equal(settings.MinChopAbsolute, snapshot.MinChopAbsolute);
        Assert.Equal(settings.TrendWindowSeconds, snapshot.TrendWindowSeconds);
        Assert.Equal(settings.TrailingStopPercent, snapshot.TrailingStopPercent);
        Assert.Equal(settings.UseMarketableLimits, snapshot.UseMarketableLimits);
        Assert.Equal(settings.UseIocOrders, snapshot.UseIocOrders);
        Assert.Equal(settings.EnableTrimming, snapshot.EnableTrimming);
        Assert.Equal(settings.TrimTriggerPercent, snapshot.TrimTriggerPercent);
        Assert.Equal(settings.TrimRatio, snapshot.TrimRatio);
        Assert.Equal(settings.TrimCooldownSeconds, snapshot.TrimCooldownSeconds);
        Assert.Equal(settings.ExitStrategy.TrendWaitSeconds, snapshot.TrendWaitSeconds);
        Assert.Equal(settings.ExitStrategy.TrendConfidenceThreshold, snapshot.TrendConfidenceThreshold);
        Assert.Equal(settings.DynamicStopLoss.Enabled, snapshot.DynamicStopLossEnabled);
        Assert.Equal(settings.DynamicStopLoss.Tiers.Count, snapshot.DynamicStopLossTiers.Count);
    }

    [Fact]
    public void DynamicStopLoss_TiersSnapshot_IsDeepCopy()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var snapshot = applier.SnapshotCurrentSettings();

        // Mutate the original tiers
        settings.DynamicStopLoss.Tiers[0].StopPercent = 0.999m;

        // Snapshot should be unaffected
        Assert.Equal(0.0015m, snapshot.DynamicStopLossTiers[0].StopPercent);
    }

    // ====================================================================
    // MONOTONIC TIME GUARD (Phase boundary oscillation fix)
    // ====================================================================

    [Fact]
    public void MonotonicTimeGuard_BackwardTimestamp_DoesNotCausePhaseOscillation()
    {
        // Simulates interleaved tick streams (QQQ/TQQQ/SQQQ) where
        // timestamps can briefly go backwards at phase boundaries.
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Enter Open Volatility at 09:30:00
        Assert.True(applier.CheckAndApply(new TimeSpan(9, 30, 0)));
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        // Forward to 10:30:01 — transition to Base Config
        Assert.True(applier.CheckAndApply(new TimeSpan(10, 30, 1)));
        Assert.Null(applier.ActivePhaseName); // Base Config

        // Now a TQQQ tick arrives with an earlier timestamp (10:29:59.500)
        // This should be IGNORED — not cause a transition back to Open Volatility
        Assert.False(applier.CheckAndApply(new TimeSpan(10, 29, 59)));
        Assert.Null(applier.ActivePhaseName); // Still Base Config — no oscillation!
    }

    [Fact]
    public void MonotonicTimeGuard_ForwardTimestamp_TransitionsNormally()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Normal forward progression through phases (using rules from CreateBaseSettings:
        // Open Volatility 09:30-10:30, Power Hour 14:00-16:00, Base Config elsewhere)
        Assert.True(applier.CheckAndApply(new TimeSpan(9, 30, 0)));
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        Assert.True(applier.CheckAndApply(new TimeSpan(10, 30, 0)));
        Assert.Null(applier.ActivePhaseName); // Base Config (no Mid-Day Lull rule in test setup)

        // Still Base Config at 12:00 (no rule matches)
        Assert.False(applier.CheckAndApply(new TimeSpan(12, 0, 0)));
        Assert.Null(applier.ActivePhaseName);

        Assert.True(applier.CheckAndApply(new TimeSpan(14, 0, 0)));
        Assert.Equal("Power Hour", applier.ActivePhaseName);
    }

    [Fact]
    public void MonotonicTimeGuard_InterleavedTicks_AtBoundary_NoFlipFlop()
    {
        // Simulates exact boundary scenario:
        // QQQ at 10:29:59.900 → Open Vol
        // TQQQ at 10:30:00.100 → Base Config (transition)
        // SQQQ at 10:29:59.950 → should NOT flip back to Open Vol
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        int transitionCount = 0;

        // QQQ tick just before boundary
        if (applier.CheckAndApply(new TimeSpan(10, 29, 59))) transitionCount++;
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        // TQQQ tick just after boundary → transition
        if (applier.CheckAndApply(new TimeSpan(10, 30, 0))) transitionCount++;
        Assert.Null(applier.ActivePhaseName); // Base Config

        // SQQQ tick with earlier timestamp → ignored
        if (applier.CheckAndApply(new TimeSpan(10, 29, 59))) transitionCount++;

        // Only 2 transitions should have occurred (→Open Vol, →Base Config)
        // NOT 3 (no flip back to Open Vol)
        Assert.Equal(2, transitionCount);
        Assert.Null(applier.ActivePhaseName); // Still Base Config
    }

    [Fact]
    public void DirectionSwitchCooldown_IsSnapshotAndRestored()
    {
        var settings = CreateBaseSettings();
        settings.DirectionSwitchCooldownSeconds = 120;
        settings.TimeRules.Clear();
        settings.TimeRules.Add(new TimeBasedRule
        {
            Name = "Test Phase",
            StartTime = new TimeSpan(10, 0, 0),
            EndTime = new TimeSpan(11, 0, 0),
            Overrides = new TradingSettingsOverrides
            {
                DirectionSwitchCooldownSeconds = 60
            }
        });
        var applier = CreateApplier(settings);

        // Base value
        Assert.Equal(120, settings.DirectionSwitchCooldownSeconds);

        // Enter phase — override to 60
        applier.CheckAndApply(new TimeSpan(10, 0, 0));
        Assert.Equal(60, settings.DirectionSwitchCooldownSeconds);

        // Exit phase — restored to 120
        applier.CheckAndApply(new TimeSpan(11, 0, 0));
        Assert.Equal(120, settings.DirectionSwitchCooldownSeconds);
    }

    // ====================================================================
    // MULTI-CALLER PHASE BOUNCE FIX (GLOBAL MAX TIME)
    // ====================================================================

    [Fact]
    public void MultiCaller_AnalystAheadOfTrader_NoPhaseBounce()
    {
        // Reproduces the bug: analyst races ahead past OV boundary (10:30)
        // while trader is still in OV window. The global max time approach
        // ensures that once the analyst advances past the boundary, ALL callers
        // see the updated phase — preventing settings inconsistency.
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Analyst enters OV at 09:30 — first transition
        Assert.True(applier.CheckAndApply(new TimeSpan(9, 30, 0), "analyst"));
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        // Trader at same time — phase already set, no transition
        Assert.False(applier.CheckAndApply(new TimeSpan(9, 30, 0), "trader"));
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        int transitionCount = 1; // one so far (analyst's OV entry)

        // Analyst races ahead past OV end (10:31) — global max time advances
        // Phase transitions OV→Base
        if (applier.CheckAndApply(new TimeSpan(10, 31, 0), "analyst")) transitionCount++;
        Assert.Equal(2, transitionCount);
        Assert.Null(applier.ActivePhaseName); // Base Config

        // Trader is still in OV at 09:45 — but global max is 10:31 (past OV).
        // Phase stays Base Config. Settings remain consistent. No bounce!
        if (applier.CheckAndApply(new TimeSpan(9, 45, 0), "trader")) transitionCount++;
        Assert.Equal(2, transitionCount); // No new transition!

        // Many subsequent calls from both callers — NO new transitions
        if (applier.CheckAndApply(new TimeSpan(10, 32, 0), "analyst")) transitionCount++;
        if (applier.CheckAndApply(new TimeSpan(9, 46, 0), "trader")) transitionCount++;
        if (applier.CheckAndApply(new TimeSpan(10, 33, 0), "analyst")) transitionCount++;
        if (applier.CheckAndApply(new TimeSpan(9, 47, 0), "trader")) transitionCount++;
        if (applier.CheckAndApply(new TimeSpan(10, 34, 0), "analyst")) transitionCount++;
        if (applier.CheckAndApply(new TimeSpan(9, 48, 0), "trader")) transitionCount++;

        // Still just 2 transitions (enter OV + exit OV) — no bouncing!
        Assert.Equal(2, transitionCount);
    }

    [Fact]
    public void MultiCaller_BothConverge_SettlesCorrectly()
    {
        // After the analyst races ahead, the trader eventually catches up.
        // With global max time, the phase is already correct — no extra transition
        // when the trader "converges."
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Analyst enters OV (first transition)
        Assert.True(applier.CheckAndApply(new TimeSpan(9, 30, 0), "analyst"));

        // Trader at same time — no transition (already OV)
        Assert.False(applier.CheckAndApply(new TimeSpan(9, 30, 0), "trader"));

        // Analyst exits OV (second transition) — global max advances to 10:31
        Assert.True(applier.CheckAndApply(new TimeSpan(10, 31, 0), "analyst"));
        Assert.Null(applier.ActivePhaseName); // Base Config

        // Trader still in OV window — but global max is 10:31, so phase stays Base
        Assert.False(applier.CheckAndApply(new TimeSpan(9, 45, 0), "trader"));

        int lateTransitions = 0;

        // Trader catches up past OV boundary — phase is already Base Config,
        // so no additional transition fires. Settings remain consistent.
        if (applier.CheckAndApply(new TimeSpan(10, 31, 0), "trader")) lateTransitions++;
        Assert.Null(applier.ActivePhaseName); // Both agree: Base Config
        Assert.Equal(0, lateTransitions); // No extra transition!

        // Neither caller should trigger further transitions
        if (applier.CheckAndApply(new TimeSpan(10, 32, 0), "analyst")) lateTransitions++;
        if (applier.CheckAndApply(new TimeSpan(10, 32, 0), "trader")) lateTransitions++;
        Assert.Equal(0, lateTransitions);
    }

    [Fact]
    public void MultiCaller_GlobalMaxTime_SettingsRemainConsistent()
    {
        // Verifies the key property: settings are ALWAYS consistent with the
        // active phase. The old per-caller approach would leave settings in Base
        // while the trader thought it was still in OV.
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Enter OV — settings get OV overrides
        applier.CheckAndApply(new TimeSpan(9, 30, 0), "analyst");
        var ovTrailingStop = settings.TrailingStopPercent;

        // Analyst exits OV — settings restored to base
        applier.CheckAndApply(new TimeSpan(10, 31, 0), "analyst");
        var baseTrailingStop = settings.TrailingStopPercent;

        // Trader calls at 09:45 — but global max is 10:31, so settings stay at base.
        // This is the critical fix: settings must NOT flip back to OV overrides.
        applier.CheckAndApply(new TimeSpan(9, 45, 0), "trader");
        Assert.Equal(baseTrailingStop, settings.TrailingStopPercent);
    }

    // ====================================================================
    // DAY BOUNDARY RESET (Part B fix — multi-day runs)
    // ====================================================================

    /// <summary>
    /// Core regression test: without the day boundary fix, the high-water mark from
    /// Day N's 16:00 blocks all Day N+1 timestamps (09:30 < 16:00), freezing the phase
    /// at Power Hour. This test verifies that passing a new date correctly resets.
    /// </summary>
    [Fact]
    public void CheckAndApply_DayBoundary_ResetsPhaseFromPowerHourToOV()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        // Day 1: transition through all phases ending in Power Hour
        var day1 = new DateTime(2026, 3, 5);
        applier.CheckAndApply(day1.Add(new TimeSpan(9, 30, 0)), "analyst");
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        applier.CheckAndApply(day1.Add(new TimeSpan(10, 30, 0)), "analyst");
        Assert.Null(applier.ActivePhaseName); // Base Config

        applier.CheckAndApply(day1.Add(new TimeSpan(14, 0, 0)), "analyst");
        Assert.Equal("Power Hour", applier.ActivePhaseName);

        applier.CheckAndApply(day1.Add(new TimeSpan(15, 59, 0)), "analyst");
        Assert.Equal("Power Hour", applier.ActivePhaseName);

        // Day 2: first tick at 09:30 — must reset to Open Volatility, NOT stay at Power Hour
        var day2 = new DateTime(2026, 3, 6);
        var changed = applier.CheckAndApply(day2.Add(new TimeSpan(9, 30, 0)), "analyst");

        Assert.True(changed);
        Assert.Equal("Open Volatility", applier.ActivePhaseName);
        Assert.True(applier.DayBoundaryDetected);
        // Verify OV overrides are applied (not PH overrides stuck from yesterday)
        Assert.Equal(0.00002m, settings.MinVelocityThreshold);
    }

    /// <summary>
    /// Verifies that DayBoundaryDetected is only true on the first call of a new day.
    /// </summary>
    [Fact]
    public void CheckAndApply_DayBoundary_FlagOnlySetOnFirstCall()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var day1 = new DateTime(2026, 3, 5);
        applier.CheckAndApply(day1.Add(new TimeSpan(14, 0, 0)), "analyst");

        var day2 = new DateTime(2026, 3, 6);
        applier.CheckAndApply(day2.Add(new TimeSpan(9, 30, 0)), "analyst");
        Assert.True(applier.DayBoundaryDetected);

        // Second call on same day — DayBoundaryDetected should be false
        applier.CheckAndApply(day2.Add(new TimeSpan(9, 31, 0)), "analyst");
        Assert.False(applier.DayBoundaryDetected);
    }

    /// <summary>
    /// Verifies that both analyst and trader callers work correctly across day boundaries.
    /// The trader's high-water mark from Day N must not block Day N+1.
    /// </summary>
    [Fact]
    public void CheckAndApply_DayBoundary_ResetsAllCallerHighWaterMarks()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var day1 = new DateTime(2026, 3, 5);
        // Both callers advance to late in the day
        applier.CheckAndApply(day1.Add(new TimeSpan(15, 30, 0)), "analyst");
        applier.CheckAndApply(day1.Add(new TimeSpan(15, 45, 0)), "trader");
        Assert.Equal("Power Hour", applier.ActivePhaseName);

        // Day 2: analyst goes first
        var day2 = new DateTime(2026, 3, 6);
        applier.CheckAndApply(day2.Add(new TimeSpan(9, 30, 0)), "analyst");
        Assert.Equal("Open Volatility", applier.ActivePhaseName);

        // Trader at 09:31 on Day 2 — must NOT be blocked by Day 1's 15:45
        var traderChanged = applier.CheckAndApply(day2.Add(new TimeSpan(9, 31, 0)), "trader");
        // No phase change (still OV), but the call must succeed (not be rejected by HWM)
        Assert.False(traderChanged); // Same phase
        Assert.Equal("Open Volatility", applier.ActivePhaseName);
    }

    /// <summary>
    /// Verifies settings are fully restored to base values after day reset
    /// (not left with stale Power Hour overrides).
    /// </summary>
    [Fact]
    public void CheckAndApply_DayBoundary_RestoresBaseSettingsBeforeNewPhase()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        var day1 = new DateTime(2026, 3, 5);
        applier.CheckAndApply(day1.Add(new TimeSpan(14, 0, 0)), "analyst");
        // PH overrides are applied
        Assert.Equal("Power Hour", applier.ActivePhaseName);
        var phVelocity = settings.MinVelocityThreshold; // PH override value

        // Day 2 at 11:00 (Base Config — no rule matches)
        var day2 = new DateTime(2026, 3, 6);
        applier.CheckAndApply(day2.Add(new TimeSpan(11, 0, 0)), "analyst");
        Assert.Null(applier.ActivePhaseName); // Base Config

        // Verify base values are restored, not PH overrides
        Assert.Equal(0.000001m, settings.MinVelocityThreshold);
        Assert.NotEqual(phVelocity, settings.MinVelocityThreshold);
    }

    /// <summary>
    /// ResetForNewDay can be called externally (e.g., from TraderEngine's day reset).
    /// Verifies it clears phase state so the next CheckAndApply evaluates fresh.
    /// </summary>
    [Fact]
    public void ResetForNewDay_ClearsPhaseState()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        applier.CheckAndApply(new TimeSpan(14, 0, 0));
        Assert.Equal("Power Hour", applier.ActivePhaseName);

        applier.ResetForNewDay();

        Assert.Null(applier.ActivePhaseName);
        // Base values restored
        Assert.Equal(0.000001m, settings.MinVelocityThreshold);
        Assert.Equal(180, settings.SMAWindowSeconds);
        Assert.True(settings.UseMarketableLimits);

        // Can re-enter OV at 09:30 (HWM was cleared)
        var changed = applier.CheckAndApply(new TimeSpan(9, 30, 0));
        Assert.True(changed);
        Assert.Equal("Open Volatility", applier.ActivePhaseName);
    }

    /// <summary>
    /// TimeSpan-only overload (no date) still works — maintains backward compatibility.
    /// Day boundary detection requires the DateTime overload.
    /// </summary>
    [Fact]
    public void CheckAndApply_TimeSpanOverload_StillEnforcesMonotonicGuard()
    {
        var settings = CreateBaseSettings();
        var applier = CreateApplier(settings);

        applier.CheckAndApply(new TimeSpan(14, 0, 0));
        Assert.Equal("Power Hour", applier.ActivePhaseName);

        // Going backwards is still rejected without a date
        var changed = applier.CheckAndApply(new TimeSpan(9, 30, 0));
        Assert.False(changed);
        Assert.Equal("Power Hour", applier.ActivePhaseName);
    }
}
