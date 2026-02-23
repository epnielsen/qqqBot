using System.Diagnostics;
using System.Globalization;
using MarketBlocks.Bots.Domain;
using MarketBlocks.Bots.Services;
using MarketBlocks.Trade.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Specification for a single parallel replay run.
/// </summary>
internal sealed record ReplayRunSpec
{
    /// <summary>Date to replay.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>Trading settings for this run (deep-cloned per run).</summary>
    public required MarketBlocks.Bots.Domain.TradingSettings Settings { get; init; }

    /// <summary>RNG seed for broker slippage and Brownian bridge interpolation.</summary>
    public required int Seed { get; init; }

    /// <summary>Playback speed (0 = max speed).</summary>
    public double Speed { get; init; }

    /// <summary>Optional start time filter (Eastern).</summary>
    public TimeOnly? StartTime { get; init; }

    /// <summary>Optional end time filter (Eastern).</summary>
    public TimeOnly? EndTime { get; init; }

    /// <summary>Directory for log files.</summary>
    public required string LogDirectory { get; init; }

    /// <summary>Directory for temporary state files.</summary>
    public required string StateFileDirectory { get; init; }

    /// <summary>Directory containing market data CSVs.</summary>
    public required string MarketDataDirectory { get; init; }

    /// <summary>Optional label for this config variant.</summary>
    public string? ConfigLabel { get; init; }

    /// <summary>SimulatedBroker configuration loaded from the config file.</summary>
    public IConfiguration? BrokerConfig { get; init; }
}

/// <summary>
/// Factory that constructs and runs a complete, isolated replay pipeline
/// (AnalystEngine + TraderEngine + SimulatedBroker + supporting services)
/// without using the Generic Host or DI container.
///
/// Each invocation creates its own object graph — no shared mutable state.
/// Safe for concurrent execution from ParallelReplayRunner.
/// </summary>
internal sealed class ReplayPipelineFactory
{
    /// <summary>
    /// Runs a single replay to completion and returns a structured result.
    /// All objects are created, used, and disposed within this method — fully isolated.
    /// </summary>
    public async Task<ReplayResult> RunReplayAsync(ReplayRunSpec spec, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        FileLoggerProvider? fileLoggerProvider = null;
        ILoggerFactory? loggerFactory = null;
        TradingStateManager? stateManager = null;

        try
        {
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

            // ── 2. Settings ──
            var settings = spec.Settings;
            settings.BypassMarketHoursCheck = true; // Replay runs outside market hours

            // ── 3. SimulatedBroker ──
            var cfg = spec.BrokerConfig;
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

            // ── 10. Run the pipeline ──
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Start trader (consumer) first so it's ready to receive
            await trader.StartAsync(analyst.RegimeChannel, cts.Token);

            // Start analyst (producer) — replays CSV data
            await analyst.StartAsync(cts.Token);

            // Wait for both to finish with a safety timeout
            var analystDone = analyst.ExecuteTask ?? Task.CompletedTask;
            var traderDone = trader.ExecuteTask ?? Task.CompletedTask;
            var bothDone = Task.WhenAll(analystDone, traderDone);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
            var winner = await Task.WhenAny(bothDone, timeoutTask);

            if (winner == timeoutTask && !bothDone.IsCompleted)
            {
                replayLogger.LogWarning("[PARALLEL-REPLAY] Timeout waiting for engines (seed={Seed})", spec.Seed);
            }

            // Stop engines gracefully
            await analyst.StopAsync(CancellationToken.None);
            await trader.StopAsync(CancellationToken.None);

            // Print summary to log file
            broker.PrintSummary();

            sw.Stop();

            // ── 11. Collect result ──
            var result = broker.GetResult(
                replayDate: spec.Date,
                seed: spec.Seed,
                configLabel: spec.ConfigLabel,
                wallClockDuration: sw.Elapsed);

            // Cleanup
            analyst.Dispose();
            trader.Dispose();

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new ReplayResult
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
