using qqqBot;
using System.Threading.Channels;
using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace qqqBot.Tests;

/// <summary>
/// Tests for PH Resume Mode: when the daily profit target fires before Power Hour,
/// the engine arms a resume flag and resumes trading when the PH phase starts at 14:00 ET.
/// </summary>
public class TraderEngine_PhResumeTests : IDisposable
{
    // Fixed date: Feb 10, 2025 — winter, so ET = UTC-5
    private static readonly TimeZoneInfo EasternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    private static readonly DateTime TestDateEastern = new(2025, 2, 10); // Monday
    private readonly List<string> _testDirs = new();

    public void Dispose()
    {
        foreach (var dir in _testDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Convert Eastern time-of-day on the test date to UTC.
    /// </summary>
    private static DateTime EasternToUtc(int hour, int minute, int second = 0)
    {
        var eastern = TestDateEastern.Add(new TimeSpan(hour, minute, second));
        return TimeZoneInfo.ConvertTimeToUtc(eastern, EasternZone);
    }

    private static MarketRegime CreateRegimeAtTime(string signal, decimal price, DateTime utcTime,
        decimal? bullPrice = null, decimal? bearPrice = null)
    {
        return new MarketRegime(
            Signal: signal,
            BenchmarkPrice: price,
            SmaValue: price,
            Slope: signal == "BULL" ? 0.05m : (signal == "BEAR" ? -0.05m : 0m),
            UpperBand: price * 1.01m,
            LowerBand: price * 0.99m,
            TimestampUtc: utcTime,
            Reason: $"Test signal: {signal}",
            BullPrice: bullPrice ?? price,
            BearPrice: bearPrice ?? price
        );
    }

    /// <summary>
    /// Creates a TradingSettings with TimeRules for OV (09:30-10:13) and PH (14:00-16:00).
    /// PH overrides are empty (Base settings), matching the Combined approach.
    /// </summary>
    private static TradingSettings CreateSettingsWithTimeRules(bool resumeInPowerHour = true)
    {
        return new TradingSettings
        {
            BullSymbol = "TQQQ",
            BearSymbol = "SQQQ",
            BenchmarkSymbol = "QQQ",
            StartingAmount = 10000m,
            MaxSlippagePercent = 0.002m,
            TrailingStopPercent = 0.01m,
            StopLossCooldownSeconds = 0,
            UseIocOrders = false,
            BypassMarketHoursCheck = true, // Replay mode — use regime timestamps
            ResumeInPowerHour = resumeInPowerHour,
            DailyProfitTargetPercent = 0m, // Disabled (already fired in pre-seeded state)
            ExitStrategy = new DynamicExitConfig
            {
                ScalpWaitSeconds = -1, // Instant (no wait)
                TrendWaitSeconds = -1,
                TrendConfidenceThreshold = 0.00008,
                HoldNeutralIfUnderwater = false
            },
            TimeRules = new List<TimeBasedRule>
            {
                new()
                {
                    Name = "Open Volatility",
                    StartTime = new TimeSpan(9, 30, 0),
                    EndTime = new TimeSpan(10, 13, 0),
                    Overrides = new TradingSettingsOverrides
                    {
                        SMAWindowSeconds = 120,
                        ChopThresholdPercent = 0.0015m,
                        TrailingStopPercent = 0.005m
                    }
                },
                new()
                {
                    Name = "Power Hour",
                    StartTime = new TimeSpan(14, 0, 0),
                    EndTime = new TimeSpan(16, 0, 0),
                    Overrides = new TradingSettingsOverrides() // Empty = Base settings
                }
            }
        };
    }

    /// <summary>
    /// Creates a TradingStateManager with pre-seeded state.
    /// The state's CurrentTradingDay is set to match the test date to avoid day-reset clearing HaltReason.
    /// </summary>
    private (TradingStateManager manager, string dir) CreateStateManager(
        HaltReason haltReason = HaltReason.None,
        bool phResumeArmed = false,
        decimal startingAmount = 10000m,
        decimal availableCash = 10000m)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"PhResume_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _testDirs.Add(dir);

        var stateFilePath = Path.Combine(dir, "trading_state.json");
        var manager = new TradingStateManager(stateFilePath, 1);

        // Pre-seed state
        var state = manager.Load();
        state.IsInitialized = true;
        state.StartingAmount = startingAmount;
        state.AvailableCash = availableCash;
        state.AccumulatedLeftover = 0m;
        state.HaltReason = haltReason;
        state.PhResumeArmed = phResumeArmed;
        state.CurrentTradingDay = TestDateEastern.ToString("yyyy-MM-dd"); // Match test date
        manager.Save(state, forceImmediate: true);

        return (manager, dir);
    }

    /// <summary>
    /// Sets up a standard mock broker that accepts any order and returns a filled result.
    /// Tracks submitted orders via the orderCount reference.
    /// </summary>
    private static Mock<IBrokerExecution> CreateMockBroker(Action? onOrderSubmitted = null)
    {
        var mockBroker = new Mock<IBrokerExecution>();

        mockBroker.Setup(b => b.GetLatestPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);

        mockBroker.Setup(b => b.SubmitOrderAsync(It.IsAny<BotOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotOrderRequest req, CancellationToken _) =>
            {
                onOrderSubmitted?.Invoke();
                return new BotOrder
                {
                    OrderId = Guid.NewGuid(),
                    Symbol = req.Symbol,
                    Side = req.Side,
                    Type = req.Type,
                    Status = BotOrderStatus.Filled,
                    Quantity = req.Quantity,
                    FilledQuantity = req.Quantity,
                    AverageFillPrice = 100m
                };
            });

        mockBroker.Setup(b => b.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new BotOrder
            {
                OrderId = id,
                Symbol = "TQQQ",
                Side = BotOrderSide.Buy,
                Type = BotOrderType.Market,
                Status = BotOrderStatus.Filled,
                Quantity = 100,
                FilledQuantity = 100,
                AverageFillPrice = 100m
            });

        return mockBroker;
    }

    // =========================================================================
    // TEST: PH Resume — Profit target fires before PH, resumes at 14:00
    // =========================================================================

    [Fact]
    public async Task PhResume_ProfitTargetBeforePH_ResumesAtPowerHour()
    {
        // Arrange: State = halted with ProfitTarget, PhResumeArmed = true
        var (stateManager, _) = CreateStateManager(
            haltReason: HaltReason.ProfitTarget,
            phResumeArmed: true);

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: true);
        var orderCount = 0;
        var mockBroker = CreateMockBroker(onOrderSubmitted: () => Interlocked.Increment(ref orderCount));
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Send regime during Base phase (13:55 ET) — should be blocked by halt
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(13, 55), bullPrice: 100m));
        await Task.Delay(300);

        var ordersBeforePH = orderCount;

        // Send regime at 14:01 ET — triggers PH phase transition → CheckPhResume → resume
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(14, 1), bullPrice: 100m));
        await Task.Delay(500);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        // Assert
        Assert.Equal(0, ordersBeforePH); // No trades while halted in Base phase
        Assert.True(orderCount > 0, "Expected at least 1 order after PH resume");
        Assert.Equal(HaltReason.None, engine.State.HaltReason);
        Assert.False(engine.State.PhResumeArmed);

        stateManager.Dispose();
    }

    // =========================================================================
    // TEST: PH Resume — Loss limit fires, does NOT resume at PH
    // =========================================================================

    [Fact]
    public async Task PhResume_LossLimit_NeverResumes()
    {
        // Arrange: State = halted with LossLimit (never arms PH resume)
        var (stateManager, _) = CreateStateManager(
            haltReason: HaltReason.LossLimit,
            phResumeArmed: false);

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: true);
        var orderCount = 0;
        var mockBroker = CreateMockBroker(onOrderSubmitted: () => Interlocked.Increment(ref orderCount));
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Send Base regime then PH regime
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(13, 55), bullPrice: 100m));
        await Task.Delay(200);
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(14, 1), bullPrice: 100m));
        await Task.Delay(300);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        // Assert: Still halted, no trades
        Assert.Equal(0, orderCount);
        Assert.Equal(HaltReason.LossLimit, engine.State.HaltReason);
    }

    // =========================================================================
    // TEST: PH Resume — Feature disabled → stays halted at PH
    // =========================================================================

    [Fact]
    public async Task PhResume_FeatureDisabled_StaysHalted()
    {
        // Arrange: ProfitTarget halted but ResumeInPowerHour = false
        // PhResumeArmed should be false because SetHaltReason wouldn't arm when feature is off
        var (stateManager, _) = CreateStateManager(
            haltReason: HaltReason.ProfitTarget,
            phResumeArmed: false); // Not armed because feature was disabled

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: false);
        var orderCount = 0;
        var mockBroker = CreateMockBroker(onOrderSubmitted: () => Interlocked.Increment(ref orderCount));
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Transition to PH
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(13, 55), bullPrice: 100m));
        await Task.Delay(200);
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(14, 1), bullPrice: 100m));
        await Task.Delay(300);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        // Assert: Still halted — PH Resume not armed
        Assert.Equal(0, orderCount);
        Assert.Equal(HaltReason.ProfitTarget, engine.State.HaltReason);
    }

    // =========================================================================
    // TEST: PH Resume — Target fires DURING PH → no resume (can't resume same phase)
    // =========================================================================

    [Fact]
    public async Task PhResume_TargetDuringPH_DoesNotResume()
    {
        // Arrange: ProfitTarget fired during PH → PhResumeArmed = false
        // (SetHaltReason checks time < 14:00, so firing at 14:30 doesn't arm)
        var (stateManager, _) = CreateStateManager(
            haltReason: HaltReason.ProfitTarget,
            phResumeArmed: false);

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: true);
        var orderCount = 0;
        var mockBroker = CreateMockBroker(onOrderSubmitted: () => Interlocked.Increment(ref orderCount));
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Send PH-time regimes (already in PH, no phase transition to trigger resume)
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(14, 30), bullPrice: 100m));
        await Task.Delay(300);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        // Assert: Still halted — PhResumeArmed was false
        Assert.Equal(0, orderCount);
        Assert.Equal(HaltReason.ProfitTarget, engine.State.HaltReason);
    }

    // =========================================================================
    // TEST: PH Resume — No halt, normal PH trading works
    // =========================================================================

    [Fact]
    public async Task PhResume_NoHalt_NormalPHTrading()
    {
        // Arrange: No halt — normal operation. Verify PH Resume doesn't break anything.
        var (stateManager, _) = CreateStateManager(
            haltReason: HaltReason.None,
            phResumeArmed: false);

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: true);
        var orderCount = 0;
        var mockBroker = CreateMockBroker(onOrderSubmitted: () => Interlocked.Increment(ref orderCount));
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Send BULL signal during PH (no halt, should trade normally)
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(14, 1), bullPrice: 100m));
        await Task.Delay(500);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        // Assert: Normal trading — at least 1 order placed
        Assert.True(orderCount > 0, "Expected normal PH trading when not halted");
        Assert.Equal(HaltReason.None, engine.State.HaltReason);
    }

    // =========================================================================
    // TEST: PH Resume — State persistence verified
    // =========================================================================

    [Fact]
    public async Task PhResume_StatePersistedAfterResume()
    {
        // Arrange: Pre-seed halted + armed state
        var (stateManager, dir) = CreateStateManager(
            haltReason: HaltReason.ProfitTarget,
            phResumeArmed: true);

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: true);
        var mockBroker = CreateMockBroker();
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Trigger PH transition to resume
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(13, 55), bullPrice: 100m));
        await Task.Delay(200);
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("BULL", 100m, EasternToUtc(14, 1), bullPrice: 100m));
        await Task.Delay(500);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }
        stateManager.Dispose();

        // Assert: Load state from disk — verify HaltReason and PhResumeArmed are cleared
        var stateManager2 = new TradingStateManager(Path.Combine(dir, "trading_state.json"), 1);
        var persistedState = stateManager2.Load();

        Assert.Equal(HaltReason.None, persistedState.HaltReason);
        Assert.False(persistedState.PhResumeArmed);

        stateManager2.Dispose();
    }

    // =========================================================================
    // TEST: PH Resume — Daily target disabled for PH session after resume
    // =========================================================================

    [Fact]
    public async Task PhResume_DisablesDailyTargetForPHSession()
    {
        // Arrange: Pre-seed halted + armed state with a non-zero daily target
        var (stateManager, _) = CreateStateManager(
            haltReason: HaltReason.ProfitTarget,
            phResumeArmed: true);

        var settings = CreateSettingsWithTimeRules(resumeInPowerHour: true);
        settings.DailyProfitTargetPercent = 1.75m; // Will be zeroed by CheckPhResume
        settings.DailyProfitTarget = 175m;

        var mockBroker = CreateMockBroker();
        var mockIoc = new Mock<IIocExecutor>();
        var mockLogger = new Mock<ILogger<TraderEngine>>();
        var timeApplier = new TimeRuleApplier(settings,
            NullLoggerFactory.Instance.CreateLogger<TimeRuleApplier>());

        var engine = new TraderEngine(
            mockLogger.Object, settings, mockBroker.Object, mockIoc.Object, stateManager, timeApplier);

        var channel = Channel.CreateUnbounded<MarketRegime>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var engineTask = engine.StartAsync(channel.Reader, cts.Token);

        // Act: Trigger PH transition
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("NEUTRAL", 100m, EasternToUtc(13, 55)));
        await Task.Delay(200);
        await channel.Writer.WriteAsync(
            CreateRegimeAtTime("NEUTRAL", 100m, EasternToUtc(14, 1)));
        await Task.Delay(300);

        cts.Cancel();
        try { await engineTask; } catch (OperationCanceledException) { }

        // Assert: DailyProfitTargetPercent should be zeroed for PH session
        Assert.Equal(0m, settings.DailyProfitTargetPercent);
        Assert.Equal(0m, settings.DailyProfitTarget);
        // Daily target tracking state should be reset
        Assert.False(engine.State.DailyTargetArmed);
    }
}
