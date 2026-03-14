using System.Diagnostics;
using System.Globalization;
using MarketBlocks.Bots.Domain;
using MarketBlocks.Infrastructure.Simulation;
using MarketBlocks.Infrastructure.Replay;
using MarketBlocks.Infrastructure.Logging;
using MarketBlocks.Bots.Interfaces;
using MarketBlocks.Bots.Services;
using MarketBlocks.Trade.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using FrameworkReplayResult = MarketBlocks.Bots.Domain.ReplayResult;
using FrameworkReplayRunSpec = MarketBlocks.Bots.Domain.ReplayRunSpec;

namespace qqqBot;

/// <summary>
/// qqqBot implementation of <see cref="IReplayPipelineBuilder{TSignal}"/>.
/// 
/// <para>Constructs a fully isolated replay pipeline (AnalystEngine + TraderEngine +
/// SimulatedBroker + supporting services) and runs it via <see cref="PipelineHost{TSignal}"/>.
/// Adapted from the original <see cref="ReplayPipelineFactory"/>.</para>
/// 
/// <para>Each invocation creates its own object graph — no shared mutable state.
/// Safe for concurrent execution from <see cref="ParallelReplayRunner{TSignal}"/>.</para>
/// </summary>
internal sealed class QqqReplayPipelineBuilder : IReplayPipelineBuilder<MarketRegime>
{
    /// <inheritdoc />
    public async Task<FrameworkReplayResult> RunReplayAsync(FrameworkReplayRunSpec spec, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        FileLoggerProvider? fileLoggerProvider = null;
        ILoggerFactory? loggerFactory = null;
        TradingStateManager? stateManager = null;

        try
        {
            // Cast to qqqBot-specific spec for broker config access
            var brokerConfig = (spec as QqqReplayRunSpec)?.BrokerConfig;
            var settings = (TradingSettings)spec.Settings;

            // ── 1. Logging ──
            Directory.CreateDirectory(spec.LogDirectory);
            var dateStr = spec.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var label = spec.ConfigLabel?.Replace(" ", "_") ?? "default";
            var logFileName = $"replay_{dateStr}_{label}_seed{spec.Seed}.log";
            var logFilePath = Path.Combine(spec.LogDirectory, logFileName);

            fileLoggerProvider = new FileLoggerProvider(logFilePath);
            loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddProvider(fileLoggerProvider);
                // No console logger — output would interleave in parallel mode
            });

            var brokerLogger = loggerFactory.CreateLogger<SimulatedBroker>();
            var analystLogger = loggerFactory.CreateLogger<AnalystEngine>();
            var traderLogger = loggerFactory.CreateLogger<TraderEngine>();
            var replayLogger = loggerFactory.CreateLogger<ReplayMarketDataSource>();
            var stateLogger = loggerFactory.CreateLogger<TradingStateManager>();
            var pipelineLogger = loggerFactory.CreateLogger<PipelineHost<MarketRegime>>();

            // ── 2. Settings ──
            settings.BypassMarketHoursCheck = true; // Replay runs outside market hours

            // ── 3. SimulatedBroker ──
            var cfg = brokerConfig;
            var slipBps = cfg?.GetValue("SimulatedBroker:SlippageBasisPoints", 1.0m) ?? 1.0m;
            var spreadBps = cfg?.GetValue("SimulatedBroker:SpreadBasisPoints", 2.0m) ?? 2.0m;
            var ovMult = cfg?.GetValue("SimulatedBroker:OvSpreadMultiplier", 3.0m) ?? 3.0m;
            var phMult = cfg?.GetValue("SimulatedBroker:PhSpreadMultiplier", 1.5m) ?? 1.5m;
            var volEnabled = cfg?.GetValue("SimulatedBroker:VolatilitySlippageEnabled", true) ?? true;
            var volMult = cfg?.GetValue("SimulatedBroker:VolatilitySlippageMultiplier", 0.5m) ?? 0.5m;
            var volWindow = cfg?.GetValue("SimulatedBroker:VolatilityWindowTicks", 60) ?? 60;
            var slipVariance = cfg?.GetValue("SimulatedBroker:SlippageVarianceFactor", 0.5) ?? 0.5;
            var auctionEnabled = cfg?.GetValue("SimulatedBroker:AuctionMode:Enabled", false) ?? false;
            var auctionWindow = cfg?.GetValue("SimulatedBroker:AuctionMode:WindowMinutes", 7) ?? 7;
            var auctionTimeout = cfg?.GetValue("SimulatedBroker:AuctionMode:TimeoutProbability", 0.6) ?? 0.6;
            var auctionPartial = cfg?.GetValue("SimulatedBroker:AuctionMode:PartialFillProbability", 0.4) ?? 0.4;
            var auctionMinRatio = cfg?.GetValue("SimulatedBroker:AuctionMode:MinFillRatio", 0.3) ?? 0.3;

            var broker = new SimulatedBroker(
                brokerLogger, settings.StartingAmount,
                slipBps, spreadBps, ovMult, phMult,
                volEnabled, volMult, volWindow,
                spec.Seed, slipVariance,
                auctionEnabled, auctionWindow, auctionTimeout, auctionPartial, auctionMinRatio);

            // ── 4. IOC executor ──
            var iocLoggerInst = loggerFactory.CreateLogger<IocMachineGunExecutor>();
            var iocExecutor = new IocMachineGunExecutor(
                broker,
                () => settings.GenerateClientOrderId(),
                msg => iocLoggerInst.LogInformation("{Message}", msg),
                () => 50);

            // ── 5. State manager (unique temp file per run) ──
            Directory.CreateDirectory(spec.StateFileDirectory);
            var stateFile = Path.Combine(spec.StateFileDirectory, $"replay_state_{dateStr}_{spec.Seed}.json");
            if (File.Exists(stateFile)) File.Delete(stateFile);
            stateManager = new TradingStateManager(stateFile, 5, stateLogger);

            // ── 6. TimeRuleApplier ──
            TimeRuleApplier? timeRuleApplier = null;
            if (settings.TimeRules.Count > 0)
            {
                var trLogger = loggerFactory.CreateLogger<TimeRuleApplier>();
                timeRuleApplier = new TimeRuleApplier(settings, trLogger);
            }

            // ── 7. ReplayMarketDataSource ──
            bool skipInterpolation = ProgramRefactored.IsHighResolutionData(
                spec.MarketDataDirectory, dateStr, replayLogger);

            var replaySource = new ReplayMarketDataSource(
                replayLogger,
                spec.MarketDataDirectory,
                spec.Date,
                spec.Speed,
                onPriceUpdate: (s, p, t) => broker.UpdatePrice(s, p, t),
                onTimeAdvance: null,
                replaySeed: spec.Seed,
                startTime: spec.StartTime,
                endTime: spec.EndTime,
                skipInterpolation: skipInterpolation);

            // ── 8. TraderEngine ──
            var trader = new TraderEngine(traderLogger, settings, broker, iocExecutor, stateManager, timeRuleApplier);

            // ── 9. AnalystEngine ──
            var analyst = new AnalystEngine(
                analystLogger,
                settings,
                replaySource,
                historicalDataSource: null,   // No hydration needed for replay
                fallbackDataAdapter: null,
                () => trader.CurrentPosition,
                () => trader.CurrentShares,
                () => trader.LastAnalystSignal,
                signal => trader.SaveLastAnalystSignal(signal),
                timeRuleApplier,
                onTickProcessed: utc => fileLoggerProvider.ClockOverride = utc.ToLocalTime());

            // ── 10. Run the pipeline via PipelineHost ──
            var generator = new QqqSignalGenerator(analyst);
            var dispatcher = new QqqTradeDispatcher(trader);
            var pipeline = new PipelineHost<MarketRegime>(
                generator, dispatcher, PipelineMode.Replay, pipelineLogger);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await pipeline.StartAsync(cts.Token);

            // Wait for pipeline completion with safety timeout
            try
            {
                await pipeline.Completion.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            }
            catch (TimeoutException)
            {
                replayLogger.LogWarning("[PARALLEL-REPLAY] Timeout waiting for pipeline (seed={Seed})", spec.Seed);
            }

            await pipeline.StopAsync(CancellationToken.None);

            // Print summary to log file
            broker.PrintSummary();

            sw.Stop();

            // ── 11. Collect result ──
            // SimulatedBroker returns qqqBot.ReplayResult; map to framework type
            var localResult = broker.GetResult(
                replayDate: spec.Date,
                seed: spec.Seed,
                configLabel: spec.ConfigLabel,
                wallClockDuration: sw.Elapsed);

            // Cleanup
            analyst.Dispose();
            trader.Dispose();

            return new FrameworkReplayResult
            {
                Success = localResult.Success,
                ErrorMessage = localResult.ErrorMessage,
                ReplayDate = localResult.ReplayDate,
                RngSeed = localResult.RngSeed,
                ConfigLabel = localResult.ConfigLabel,
                StartingCash = localResult.StartingCash,
                EndingEquity = localResult.EndingEquity,
                RealizedPnL = localResult.RealizedPnL,
                NetReturnFraction = localResult.NetReturnFraction,
                TotalTrades = localResult.TotalTrades,
                SpreadCost = localResult.SpreadCost,
                SlippageCost = localResult.SlippageCost,
                PeakEquity = localResult.PeakEquity,
                PeakEquityTimeUtc = localResult.PeakEquityTimeUtc,
                TroughEquity = localResult.TroughEquity,
                TroughEquityTimeUtc = localResult.TroughEquityTimeUtc,
                WallClockDuration = localResult.WallClockDuration
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new FrameworkReplayResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ReplayDate = spec.Date,
                RngSeed = spec.Seed,
                ConfigLabel = spec.ConfigLabel,
                WallClockDuration = sw.Elapsed
            };
        }
        finally
        {
            // Clean up temp state file
            stateManager?.Dispose();
            var stateFile = Path.Combine(spec.StateFileDirectory,
                $"replay_state_{spec.Date:yyyyMMdd}_{spec.Seed}.json");
            try { if (File.Exists(stateFile)) File.Delete(stateFile); } catch { }

            fileLoggerProvider?.Dispose();
            loggerFactory?.Dispose();
        }
    }
}
