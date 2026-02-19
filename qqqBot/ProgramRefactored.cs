// ============================================================================
// QQQ Trading Bot - Refactored with Producer/Consumer Architecture
// This demonstrates the new decoupled design using Channel<MarketRegime>
// ============================================================================

using System.Globalization;
using System.Threading.Channels;
using Alpaca.Markets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using MarketBlocks.Trade.Services;
using MarketBlocks.Bots.Domain;
using MarketBlocks.Bots.Interfaces;
using MarketBlocks.Bots.Services;
using MarketBlocks.Infrastructure.Alpaca;
using MarketBlocks.Infrastructure.Common;
using MarketBlocks.Infrastructure.Data.Fmp;
using MarketBlocks.Trade.Components;
using Refit;

namespace qqqBot;

/// <summary>
/// Refactored entry point using Generic Host and Producer/Consumer architecture.
/// 
/// Architecture:
/// ┌─────────────────────────────────────────────────────────────────────────┐
/// │                         AnalystEngine (Producer)                         │
/// │   Market Data → SMA Calculation → Band Logic → Signal Generation         │
/// └────────────────────────────────┬────────────────────────────────────────┘
///                                  │ Channel<MarketRegime>
///                                  ▼
/// ┌─────────────────────────────────────────────────────────────────────────┐
/// │                         TraderEngine (Consumer)                          │
/// │   Regime Signal → Position Logic → IOC/Market Orders → State Persist     │
/// └─────────────────────────────────────────────────────────────────────────┘
/// </summary>
public static class ProgramRefactored
{
    private const string PAPER_KEY_PREFIX = "PK";

    /// <summary>
    /// Parses a date string that can be either YYYYMMDD or YYYY-MM-DD.
    /// </summary>
    internal static bool TryParseFlexibleDate(string? input, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrEmpty(input)) return false;

        // Try YYYYMMDD first (compact format)
        if (DateOnly.TryParseExact(input, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        // Fall back to standard YYYY-MM-DD
        return DateOnly.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
    
    public static async Task RunAsync(string[] args)
    {
        var overrides = CommandLineOverrides.Parse(args);
        // If overrides are null, it means parsing failed (error logged to console inside Parse), so we exit.
        // However, if args is empty, Parse returns a valid object with HasOverrides=false.
        // It only returns null on validation error.
        if (overrides == null) return;

        // --- Fetch History Mode (standalone, exits after download) ---
        if (overrides.FetchHistory)
        {
            await RunFetchHistoryAsync(overrides);
            return;
        }

        var host = CreateHostBuilder(args, overrides).Build();
        await host.RunAsync();
    }
    
    // Store config file name for startup logging
    internal static string ConfigFileName { get; private set; } = "appsettings.json";
    
    // Store replay mode flag for DI access
    internal static bool IsReplayMode { get; private set; }
    internal static CommandLineOverrides? CurrentOverrides { get; private set; }
    
    /// <summary>
    /// Standalone fetch-history mode: downloads historical data and exits.
    /// Usage: dotnet run -- --fetch-history --date=2026-02-06 --symbols=QQQ,TQQQ,SQQQ
    /// </summary>
    private static async Task RunFetchHistoryAsync(CommandLineOverrides overrides)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger<HistoricalDataFetcher>();
        
        if (string.IsNullOrEmpty(overrides.ReplayDate))
        {
            Console.Error.WriteLine("[ERROR] --fetch-history requires --date=YYYYMMDD (or YYYY-MM-DD)");
            return;
        }
        
        if (!TryParseFlexibleDate(overrides.ReplayDate, out var date))
        {
            Console.Error.WriteLine($"[ERROR] Invalid date format: {overrides.ReplayDate}. Use YYYYMMDD or YYYY-MM-DD.");
            return;
        }
        
        var symbols = overrides.SymbolsOverride?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? new[] { "QQQ", "TQQQ", "SQQQ" };

        // Try to load API credentials for Alpaca/FMP
        var configFileName = overrides.ConfigFile ?? "appsettings.json";
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(configFileName, optional: true)
            .AddUserSecrets(typeof(ProgramRefactored).Assembly, optional: true)
            .Build();
        
        IMarketDataSource? alpacaSource = null;
        IMarketDataAdapter? fmpAdapter = null;
        
        // Try Alpaca
        var apiKey = config["Alpaca:ApiKey"];
        var apiSecret = config["Alpaca:ApiSecret"];
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
        {
            try
            {
                var secretKey = new SecretKey(apiKey, apiSecret);
                var dataClient = Alpaca.Markets.Environments.Paper.GetAlpacaDataClient(secretKey);
                var adapterLogger = loggerFactory.CreateLogger<AlpacaSourceAdapter>();
                alpacaSource = new AlpacaSourceAdapter(null, null, dataClient, adapterLogger);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[FETCH] Could not initialize Alpaca data client");
            }
        }
        
        // Try FMP
        var fmpKey = config["Fmp:ApiKey"] ?? Environment.GetEnvironmentVariable("FMP_API_KEY");
        if (!string.IsNullOrEmpty(fmpKey))
        {
            try
            {
                var fmpApi = Refit.RestService.For<IFmpMarketDataApi>("https://financialmodelingprep.com/stable");
                var fmpLogger = loggerFactory.CreateLogger<FmpMarketDataAdapter>();
                var adapter = new FmpMarketDataAdapter(fmpApi, fmpLogger);
                adapter.SetCredentials(fmpKey);
                fmpAdapter = adapter;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[FETCH] Could not initialize FMP adapter");
            }
        }
        
        var fetchDataDir = config["TradingBot:MarketDataDirectory"];
        if (string.IsNullOrWhiteSpace(fetchDataDir))
            fetchDataDir = Path.Combine(@"C:\dev\TradeEcosystem", "data", "market");
        
        var fetcher = new HistoricalDataFetcher(alpacaSource, fmpAdapter, logger, fetchDataDir);
        var files = await fetcher.FetchAsync(date, symbols);
        
        if (files.Count > 0)
        {
            Console.WriteLine($"\n✓ Downloaded {files.Count} file(s):");
            foreach (var f in files)
                Console.WriteLine($"  {f}");
        }
        else
        {
            Console.Error.WriteLine("\n✗ No data downloaded. Check API credentials and date.");
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args, CommandLineOverrides overrides)
    {
        // Determine which config file to use
        var configFileName = overrides?.ConfigFile ?? "appsettings.json";
        ConfigFileName = configFileName; // Store for logging later
        IsReplayMode = overrides?.Mode == "replay";
        CurrentOverrides = overrides;
        
        return Host.CreateDefaultBuilder() // Do not pass args here to prevent default CLI parser from crashing on custom flags
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory)
                      .AddJsonFile(configFileName, optional: false)
                      .AddUserSecrets(typeof(ProgramRefactored).Assembly);
            })
            .ConfigureLogging((context, logging) =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                
                // Determine log directory: prefer config, fall back to AppContext.BaseDirectory/logs
                var configLogDir = context.Configuration["TradingBot:LogDirectory"];
                var logDirectory = !string.IsNullOrWhiteSpace(configLogDir)
                    ? configLogDir
                    : Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDirectory);
                
                // Build log file name: replay gets unique per-run names; live gets daily
                string logFileName;
                if (IsReplayMode && CurrentOverrides != null)
                {
                    var replayDateStr = CurrentOverrides.ReplayDate ?? "unknown";
                    // Normalize date string (remove dashes for filename)
                    replayDateStr = replayDateStr.Replace("-", "");
                    var nowStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    
                    // Include segment range if specified
                    var segmentSuffix = "";
                    if (CurrentOverrides.StartTime.HasValue || CurrentOverrides.EndTime.HasValue)
                    {
                        var start = CurrentOverrides.StartTime?.ToString("HHmm") ?? "open";
                        var end = CurrentOverrides.EndTime?.ToString("HHmm") ?? "close";
                        segmentSuffix = $"_{start}-{end}";
                    }
                    
                    logFileName = $"qqqbot_replay_{replayDateStr}{segmentSuffix}_{nowStamp}.log";
                }
                else
                {
                    logFileName = $"qqqbot_{DateTime.Now:yyyyMMdd}.log";
                }
                
                var logFilePath = Path.Combine(logDirectory, logFileName);
                logging.AddProvider(new FileLoggerProvider(logFilePath));
                
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;
                var isReplay = IsReplayMode;
                
                // Resolve market data directory from config (default: C:\dev\TradeEcosystem\data\market)
                var marketDataDir = configuration["TradingBot:MarketDataDirectory"];
                if (string.IsNullOrWhiteSpace(marketDataDir))
                    marketDataDir = Path.Combine(@"C:\dev\TradeEcosystem", "data", "market");
                Directory.CreateDirectory(marketDataDir);
                
                // Load and validate API credentials (skip in Replay mode)
                var apiKey = configuration["Alpaca:ApiKey"];
                var apiSecret = configuration["Alpaca:ApiSecret"];
                SecretKey? secretKey = null;
                
                if (!isReplay)
                {
                    if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
                    {
                        throw new InvalidOperationException(
                            "API credentials not found. Please run with --setup flag first.");
                    }
                    
                    if (!apiKey.StartsWith(PAPER_KEY_PREFIX, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "SAFETY VIOLATION: Live trading keys detected! " +
                            "This bot is designed for PAPER TRADING ONLY.");
                    }
                    
                    secretKey = new SecretKey(apiKey, apiSecret);
                }
                
                // Build TradingSettings from configuration
                var settings = BuildTradingSettings(configuration);
                
                // Apply command line overrides if any
                if (overrides != null)
                {
                    if (overrides.BullTicker != null) settings.BullSymbol = overrides.BullTicker;
                    if (overrides.BearTicker != null) settings.BearSymbol = overrides.BearTicker;
                    if (overrides.BenchmarkTicker != null) settings.BenchmarkSymbol = overrides.BenchmarkTicker;
                    if (overrides.BullOnlyMode) settings.BullOnlyMode = true;
                    if (overrides.UseBtcEarlyTrading) settings.UseBtcEarlyTrading = true;
                    if (overrides.WatchBtc) settings.WatchBtc = true;
                    if (overrides.ScalpWaitSecondsOverride.HasValue) settings.ExitStrategy.ScalpWaitSeconds = overrides.ScalpWaitSecondsOverride.Value;
                    if (overrides.TrendWaitSecondsOverride.HasValue) settings.ExitStrategy.TrendWaitSeconds = overrides.TrendWaitSecondsOverride.Value;
                    if (overrides.MinChopAbsoluteOverride.HasValue) settings.MinChopAbsolute = overrides.MinChopAbsoluteOverride.Value;
                    if (overrides.BotIdOverride != null) settings.BotId = overrides.BotIdOverride;
                    if (overrides.MonitorSlippage) settings.MonitorSlippage = true;
                    if (overrides.TrailingStopPercentOverride.HasValue) settings.TrailingStopPercent = overrides.TrailingStopPercentOverride.Value;
                    if (overrides.UseMarketableLimits) settings.UseMarketableLimits = true;
                    if (overrides.MaxSlippagePercentOverride.HasValue) settings.MaxSlippagePercent = overrides.MaxSlippagePercentOverride.Value;
                    if (overrides.LowLatencyMode) settings.LowLatencyMode = true;
                    if (overrides.UseIocOrders) settings.UseIocOrders = true;
                }
                
                services.AddSingleton(settings);
                
                // Register TimeRuleApplier (auto phase switching)
                // Only registers if TimeRules are configured; otherwise null is injected
                if (settings.TimeRules.Count > 0)
                {
                    services.AddSingleton<TimeRuleApplier>(sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<TimeRuleApplier>>();
                        return new TimeRuleApplier(settings, logger);
                    });
                }
                
                if (isReplay)
                {
                    // Replay runs outside real market hours — skip the wall-clock gate
                    settings.BypassMarketHoursCheck = true;

                    // =============================================
                    // REPLAY MODE: Simulated broker + CSV playback
                    // =============================================
                    
                    // Register SimulatedBroker as IBrokerExecution
                    services.AddSingleton<SimulatedBroker>(sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<SimulatedBroker>>();
                        return new SimulatedBroker(logger, settings.StartingAmount);
                    });
                    services.AddSingleton<IBrokerExecution>(sp => sp.GetRequiredService<SimulatedBroker>());
                    
                    // IOC executor wraps the simulated broker
                    services.AddSingleton<IocMachineGunExecutor>(sp =>
                    {
                        var broker = sp.GetRequiredService<IBrokerExecution>();
                        var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                        var logger = sp.GetRequiredService<ILogger<IocMachineGunExecutor>>();
                        return new IocMachineGunExecutor(
                            broker,
                            () => s.GenerateClientOrderId(),
                            msg => logger.LogInformation("{Message}", msg),
                            () => 50);
                    });
                    services.AddSingleton<IIocExecutor>(sp => sp.GetRequiredService<IocMachineGunExecutor>());
                    
                    // State manager (isolated from live state)
                    var replayStateFile = Path.Combine(AppContext.BaseDirectory, "replay_trading_state.json");
                    // Delete stale replay state to start fresh
                    if (File.Exists(replayStateFile)) File.Delete(replayStateFile);
                    services.AddSingleton(new TradingStateManager(replayStateFile));
                    
                    // ReplayMarketDataSource as IAnalystMarketDataSource
                    var replayDate = TryParseFlexibleDate(CurrentOverrides?.ReplayDate, out var rd)
                        ? rd : DateOnly.FromDateTime(DateTime.Now.AddDays(-1));
                    var replaySpeed = CurrentOverrides?.ReplaySpeed ?? 10.0;
                    var dataDir = marketDataDir;
                    var segmentStart = CurrentOverrides?.StartTime;
                    var segmentEnd = CurrentOverrides?.EndTime;
                    
                    services.AddSingleton<MarketBlocks.Bots.Services.IAnalystMarketDataSource>(sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<ReplayMarketDataSource>>();
                        var simBroker = sp.GetRequiredService<SimulatedBroker>();
                        
                        // Auto-detect: skip Brownian bridge interpolation for recorded tick data.
                        // Recorded data has sub-second resolution; historical API data has 60s bars.
                        // Check the first CSV file's average tick gap to decide.
                        var dateStr = replayDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                        bool skipInterpolation = IsHighResolutionData(dataDir, dateStr, logger);
                        
                        return new ReplayMarketDataSource(logger, dataDir, replayDate, replaySpeed,
                            onPriceUpdate: (s, p, t) => simBroker.UpdatePrice(s, p, t),
                            onTimeAdvance: null,  // Clock now advances on analyst's consumer side for determinism
                            startTime: segmentStart,
                            endTime: segmentEnd,
                            skipInterpolation: skipInterpolation);
                    });
                    
                    // No historical source or FMP needed for replay (CSV has all data)
                    services.AddSingleton<IMarketDataSource>(sp => null!);
                    services.AddSingleton<IMarketDataAdapter>(sp => null!);
                }
                else
                {
                    // =============================================
                    // LIVE MODE: Real Alpaca broker + streaming/polling
                    // =============================================
                    
                    // Register Alpaca clients
                    services.AddSingleton(Alpaca.Markets.Environments.Paper.GetAlpacaTradingClient(secretKey!));
                    services.AddSingleton(Alpaca.Markets.Environments.Paper.GetAlpacaDataClient(secretKey!));
                    services.AddSingleton(Alpaca.Markets.Environments.Paper.GetAlpacaCryptoDataClient(secretKey!));
                    
                    // Register broker execution adapter
                    services.AddSingleton<IBrokerExecution, AlpacaExecutionAdapter>();
                    
                    // Register IOC executor (both concrete and interface)
                    services.AddSingleton<IocMachineGunExecutor>(sp =>
                    {
                        var broker = sp.GetRequiredService<IBrokerExecution>();
                        var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                        var logger = sp.GetRequiredService<ILogger<IocMachineGunExecutor>>();
                        return new IocMachineGunExecutor(
                            broker,
                            () => s.GenerateClientOrderId(),
                            msg => logger.LogInformation("{Message}", msg),
                            () => 50); // Default poll delay
                    });
                    services.AddSingleton<IIocExecutor>(sp => sp.GetRequiredService<IocMachineGunExecutor>());
                    
                    // Register state manager
                    var stateFilePath = Path.Combine(AppContext.BaseDirectory, "trading_state.json");
                    services.AddSingleton(new TradingStateManager(stateFilePath));
                    
                    // Register streaming clients (only created when low-latency mode is enabled)
                    services.AddSingleton(sp =>
                    {
                        var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                        if (s.LowLatencyMode)
                        {
                            return Alpaca.Markets.Environments.Paper.GetAlpacaDataStreamingClient(secretKey!);
                        }
                        // Return a null placeholder - won't be used in polling mode
                        return null!;
                    });
                    
                    services.AddSingleton(sp =>
                    {
                        var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                        if (s.LowLatencyMode)
                        {
                            return Alpaca.Markets.Environments.Paper.GetAlpacaCryptoStreamingClient(secretKey!);
                        }
                        return null!;
                    });
                    
                    // Register market data source for AnalystEngine (uses IAnalystMarketDataSource)
                    // Also wraps with MarketDataRecorder tap for live CSV recording
                    services.AddSingleton<MarketDataRecorder>(sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<MarketDataRecorder>>();
                        return new MarketDataRecorder(logger, marketDataDir);
                    });
                    services.AddHostedService(sp => sp.GetRequiredService<MarketDataRecorder>());
                    
                    services.AddSingleton<MarketBlocks.Bots.Services.IAnalystMarketDataSource>(sp =>
                    {
                        var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                        var broker = sp.GetRequiredService<IBrokerExecution>();
                        var recorder = sp.GetRequiredService<MarketDataRecorder>();
                        
                        IAnalystMarketDataSource inner;
                        if (s.LowLatencyMode)
                        {
                            // Low-latency mode uses WebSocket streaming
                            var stockClient = sp.GetRequiredService<IAlpacaDataStreamingClient>();
                            var cryptoClient = sp.GetRequiredService<IAlpacaCryptoStreamingClient>();
                            var logger = sp.GetRequiredService<ILogger<StreamingAnalystDataSource>>();
                            inner = new StreamingAnalystDataSource(stockClient, cryptoClient, logger);
                        }
                        else
                        {
                            var logger = sp.GetRequiredService<ILogger<PollingAnalystDataSource>>();
                            inner = new PollingAnalystDataSource(broker, s.PollingIntervalSeconds * 1000, logger);
                        }
                        
                        // Wrap with recording decorator (CSV Black Box Recorder)
                        return new RecordingAnalystDataSource(inner, recorder);
                    });
                    
                    // Register historical data source for Hydration (Cold Start -> Hot Start)
                    services.AddSingleton<IMarketDataSource>(sp =>
                    {
                        var dataClient = sp.GetRequiredService<IAlpacaDataClient>();
                        var logger = sp.GetRequiredService<ILogger<AlpacaSourceAdapter>>();
                        // Create adapter with just the data client (no streaming for history)
                        return new AlpacaSourceAdapter(
                            sp.GetRequiredService<IAlpacaDataStreamingClient>(),
                            sp.GetRequiredService<IAlpacaCryptoStreamingClient>(),
                            dataClient,
                            logger);
                    });
                    
                    // Register FMP adapter as fallback for hydration
                    services.AddSingleton<IMarketDataAdapter>(sp =>
                    {
                        var fmpApi = RestService.For<IFmpMarketDataApi>("https://financialmodelingprep.com/stable");
                        var logger = sp.GetRequiredService<ILogger<FmpMarketDataAdapter>>();
                        var adapter = new FmpMarketDataAdapter(fmpApi, logger);
                        
                        // Set FMP credentials from config
                        var config = sp.GetRequiredService<IConfiguration>();
                        var fmpKey = config["Fmp:ApiKey"] ?? Environment.GetEnvironmentVariable("FMP_API_KEY");
                        if (!string.IsNullOrEmpty(fmpKey))
                        {
                            adapter.SetCredentials(fmpKey);
                            logger.LogInformation("[FMP] Fallback adapter configured for hydration");
                        }
                        else
                        {
                            logger.LogWarning("[FMP] No API key configured. Fallback hydration unavailable.");
                        }
                        
                        return adapter;
                    });
                }
                
                // Register TraderEngine first (Trader is injected into Analyst for position awareness)
                services.AddSingleton<TraderEngine>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<TraderEngine>>();
                    var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                    var broker = sp.GetRequiredService<IBrokerExecution>();
                    var iocExecutor = sp.GetRequiredService<IIocExecutor>();
                    var stateManager = sp.GetRequiredService<TradingStateManager>();
                    var timeRuleApplier = sp.GetService<TimeRuleApplier>();
                    return new TraderEngine(logger, s, broker, iocExecutor, stateManager, timeRuleApplier);
                });
                
                // Register AnalystEngine with callbacks from TraderEngine
                // - Position callbacks: for Sliding Bands feature
                // - Signal callbacks: for restart recovery (Amnesia Prevention)
                // - Historical source: for Hydration (Hot Start) — null in Replay mode
                // - Fallback adapter: FMP for when Alpaca SIP restriction kicks in — null in Replay mode
                services.AddSingleton<AnalystEngine>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<AnalystEngine>>();
                    var s = sp.GetRequiredService<MarketBlocks.Bots.Domain.TradingSettings>();
                    var marketSource = sp.GetRequiredService<MarketBlocks.Bots.Services.IAnalystMarketDataSource>();
                    var historicalSource = sp.GetService<IMarketDataSource>();  // null in replay
                    var fallbackAdapter = sp.GetService<IMarketDataAdapter>(); // null in replay
                    var trader = sp.GetRequiredService<TraderEngine>();
                    var timeRuleApplier = sp.GetService<TimeRuleApplier>();
                    
                    return new AnalystEngine(
                        logger,
                        s,
                        marketSource,
                        historicalSource,                            // Historical source for Hydration (Alpaca)
                        fallbackAdapter,                             // Fallback for Hydration (FMP)
                        () => trader.CurrentPosition,
                        () => trader.CurrentShares,
                        () => trader.LastAnalystSignal,              // Get persisted signal
                        signal => trader.SaveLastAnalystSignal(signal),  // Save signal on change
                        timeRuleApplier,                             // Time-based phase switching
                        onTickProcessed: IsReplayMode
                            ? utc => FileLoggerProvider.ClockOverride = utc.ToLocalTime()
                            : null);                                 // Clock advances at consumer side in replay
                });
                
                // Register the orchestration service that wires Analyst → Trader
                if (isReplay)
                    services.AddHostedService<ReplayOrchestrator>();
                else
                    services.AddHostedService<TradingOrchestrator>();
            });
    }
    
    private static MarketBlocks.Bots.Domain.TradingSettings BuildTradingSettings(IConfiguration configuration)
    {
        var settings = new MarketBlocks.Bots.Domain.TradingSettings
        {
            BotId = configuration["TradingBot:BotId"] ?? "main",
            PollingIntervalSeconds = configuration.GetValue("TradingBot:PollingIntervalSeconds", 1),
            BullSymbol = configuration["TradingBot:BullSymbol"] ?? "TQQQ",
            BearSymbol = configuration["TradingBot:BearSymbol"] ?? "SQQQ",
            BenchmarkSymbol = configuration["TradingBot:BenchmarkSymbol"] ?? "QQQ",
            CryptoBenchmarkSymbol = configuration["TradingBot:CryptoBenchmarkSymbol"] ?? "BTC/USD",
            SMAWindowSeconds = configuration.GetValue("TradingBot:SMAWindowSeconds", 60),
            ChopThresholdPercent = configuration.GetValue("TradingBot:ChopThresholdPercent", 0.0015m),
            MinChopAbsolute = configuration.GetValue("TradingBot:MinChopAbsolute", 0.02m),
            SlidingBand = configuration.GetValue("TradingBot:SlidingBand", false),
            SlidingBandFactor = configuration.GetValue("TradingBot:SlidingBandFactor", 0.5m),
            ExitStrategy = new MarketBlocks.Bots.Domain.DynamicExitConfig
            {
                ScalpWaitSeconds = configuration.GetValue("TradingBot:ExitStrategy:ScalpWaitSeconds", 0),
                TrendWaitSeconds = configuration.GetValue("TradingBot:ExitStrategy:TrendWaitSeconds", 120),
                TrendConfidenceThreshold = configuration.GetValue("TradingBot:ExitStrategy:TrendConfidenceThreshold", 0.00015)
            },
            WatchBtc = configuration.GetValue("TradingBot:WatchBtc", false),
            MonitorSlippage = configuration.GetValue("TradingBot:MonitorSlippage", false),
            TrailingStopPercent = configuration.GetValue("TradingBot:TrailingStopPercent", 0.0m),
            TrendRescueTrailingStopPercent = configuration.GetValue("TradingBot:TrendRescueTrailingStopPercent", 0.0m),
            StopLossCooldownSeconds = configuration.GetValue("TradingBot:StopLossCooldownSeconds", 10),
            DirectionSwitchCooldownSeconds = configuration.GetValue("TradingBot:DirectionSwitchCooldownSeconds", 0),
            StartingAmount = configuration.GetValue("TradingBot:StartingAmount", 10000m),
            UseMarketableLimits = configuration.GetValue("TradingBot:UseMarketableLimits", false),
            MaxSlippagePercent = configuration.GetValue("TradingBot:MaxSlippagePercent", 0.002m),
            MaxChaseDeviationPercent = configuration.GetValue("TradingBot:MaxChaseDeviationPercent", 0.003m),
            // Hybrid Engine Settings
            MinVelocityThreshold = configuration.GetValue("TradingBot:MinVelocityThreshold", 0.0001m),
            EntryVelocityMultiplier = configuration.GetValue("TradingBot:EntryVelocityMultiplier", 2.0m),
            SlopeWindowSize = configuration.GetValue("TradingBot:SlopeWindowSize", 5),
            EntryConfirmationTicks = configuration.GetValue("TradingBot:EntryConfirmationTicks", 2),
            BearEntryConfirmationTicks = configuration.GetValue("TradingBot:BearEntryConfirmationTicks", 0),
            TrendWindowSeconds = configuration.GetValue("TradingBot:TrendWindowSeconds", 1800),
            // Adaptive Trend Window (Opening Blindness Fix)
            EnableAdaptiveTrendWindow = configuration.GetValue("TradingBot:EnableAdaptiveTrendWindow", true),
            ShortTrendSlopeWindow = configuration.GetValue("TradingBot:ShortTrendSlopeWindow", 90),
            ShortTrendSlopeThreshold = configuration.GetValue("TradingBot:ShortTrendSlopeThreshold", 0.00002m),
            // Drift Mode + Displacement Re-Entry
            DriftModeEnabled = configuration.GetValue("TradingBot:DriftModeEnabled", false),
            DriftModeConsecutiveTicks = configuration.GetValue("TradingBot:DriftModeConsecutiveTicks", 60),
            DriftModeMinDisplacementPercent = configuration.GetValue("TradingBot:DriftModeMinDisplacementPercent", 0.002m),
            DriftModeAtrMultiplier = configuration.GetValue("TradingBot:DriftModeAtrMultiplier", 0m),
            DriftTrailingStopPercent = configuration.GetValue("TradingBot:DriftTrailingStopPercent", 0m),
            DisplacementReentryEnabled = configuration.GetValue("TradingBot:DisplacementReentryEnabled", false),
            DisplacementReentryPercent = configuration.GetValue("TradingBot:DisplacementReentryPercent", 0.005m),
            DisplacementAtrMultiplier = configuration.GetValue("TradingBot:DisplacementAtrMultiplier", 2.0m),
            DisplacementChopThreshold = configuration.GetValue("TradingBot:DisplacementChopThreshold", 40m),
            DisplacementBbwLookback = configuration.GetValue("TradingBot:DisplacementBbwLookback", 20),
            // Low-Latency Mode Settings
            LowLatencyMode = configuration.GetValue("TradingBot:LowLatencyMode", false),
            UseIocOrders = configuration.GetValue("TradingBot:UseIocOrders", false),
            IocLimitOffsetCents = configuration.GetValue("TradingBot:IocLimitOffsetCents", 1m),
            IocMaxRetries = configuration.GetValue("TradingBot:IocMaxRetries", 5),
            IocRetryStepCents = configuration.GetValue("TradingBot:IocRetryStepCents", 1m),
            IocMaxDeviationPercent = configuration.GetValue("TradingBot:IocMaxDeviationPercent", 0.005m),
            IocRemainingSharesTolerance = configuration.GetValue("TradingBot:IocRemainingSharesTolerance", 2),
            BuyRetryCooldownSeconds = configuration.GetValue("TradingBot:BuyRetryCooldownSeconds", 15),
            MaxBuyRetryCooldownSeconds = configuration.GetValue("TradingBot:MaxBuyRetryCooldownSeconds", 60),
            MarketOpenDelaySeconds = configuration.GetValue("TradingBot:MarketOpenDelaySeconds", 15),
            LastEntryMinutesBeforeClose = configuration.GetValue("TradingBot:LastEntryMinutesBeforeClose", 2.0m),
            KeepAlivePingSeconds = configuration.GetValue("TradingBot:KeepAlivePingSeconds", 5),
            WarmUpIterations = configuration.GetValue("TradingBot:WarmUpIterations", 10000),
            StatusLogIntervalSeconds = configuration.GetValue("TradingBot:StatusLogIntervalSeconds", 5),
            TakeProfitAmount = configuration.GetValue("TradingBot:TakeProfitAmount", 0m),
            // Profit Management Settings
            ProfitReinvestmentPercent = configuration.GetValue("TradingBot:ProfitReinvestmentPercent", 0.5m),
            EnableTrimming = configuration.GetValue("TradingBot:EnableTrimming", true),
            TrimRatio = configuration.GetValue("TradingBot:TrimRatio", 0.33m),
            TrimTriggerPercent = configuration.GetValue("TradingBot:TrimTriggerPercent", 0.015m),
            TrimSlopeThreshold = configuration.GetValue("TradingBot:TrimSlopeThreshold", 0.000005m),
            TrimCooldownSeconds = configuration.GetValue("TradingBot:TrimCooldownSeconds", 120),
            // Daily Profit Target
            DailyProfitTarget = configuration.GetValue("TradingBot:DailyProfitTarget", 0m),
            DailyProfitTargetPercent = configuration.GetValue("TradingBot:DailyProfitTargetPercent", 0m),
            DailyProfitTargetRealtime = configuration.GetValue("TradingBot:DailyProfitTargetRealtime", false),
            DailyProfitTargetTrailingStopPercent = configuration.GetValue("TradingBot:DailyProfitTargetTrailingStopPercent", 0m),
            
            // Daily Loss Limit
            DailyLossLimit = configuration.GetValue("TradingBot:DailyLossLimit", 0m),
            DailyLossLimitPercent = configuration.GetValue("TradingBot:DailyLossLimitPercent", 0m),
            
            // PH Resume Mode
            ResumeInPowerHour = configuration.GetValue("TradingBot:ResumeInPowerHour", false),
            
            // Analyst Phase Reset
            AnalystPhaseResetMode = Enum.Parse<MarketBlocks.Bots.Domain.AnalystPhaseResetMode>(
                configuration.GetValue("TradingBot:AnalystPhaseResetMode", "None")!, ignoreCase: true),
            AnalystPhaseResetSeconds = configuration.GetValue("TradingBot:AnalystPhaseResetSeconds", 120),
            
            // Mean Reversion Strategy
            BaseDefaultStrategy = Enum.Parse<MarketBlocks.Bots.Domain.StrategyMode>(
                configuration.GetValue("TradingBot:BaseDefaultStrategy", "Trend")!, ignoreCase: true),
            PhDefaultStrategy = Enum.Parse<MarketBlocks.Bots.Domain.StrategyMode>(
                configuration.GetValue("TradingBot:PhDefaultStrategy", "Trend")!, ignoreCase: true),
            ChopOverrideEnabled = configuration.GetValue("TradingBot:ChopOverrideEnabled", false),
            ChopUpperThreshold = configuration.GetValue("TradingBot:ChopUpperThreshold", 61.8m),
            ChopLowerThreshold = configuration.GetValue("TradingBot:ChopLowerThreshold", 38.2m),
            BollingerWindow = configuration.GetValue("TradingBot:BollingerWindow", 20),
            BollingerMultiplier = configuration.GetValue("TradingBot:BollingerMultiplier", 2.0m),
            ChopPeriod = configuration.GetValue("TradingBot:ChopPeriod", 14),
            ChopCandleSeconds = configuration.GetValue("TradingBot:ChopCandleSeconds", 60),
            MrEntryLowPctB = configuration.GetValue("TradingBot:MrEntryLowPctB", 0.2m),
            MrEntryHighPctB = configuration.GetValue("TradingBot:MrEntryHighPctB", 0.8m),
            MrExitPctB = configuration.GetValue("TradingBot:MrExitPctB", 0.5m),
            MeanRevStopPercent = configuration.GetValue("TradingBot:MeanRevStopPercent", 0.003m),
            MrAtrStopMultiplier = configuration.GetValue("TradingBot:MrAtrStopMultiplier", 2.0m),
            MrRequireRsi = configuration.GetValue("TradingBot:MrRequireRsi", true),
            MrRsiPeriod = configuration.GetValue("TradingBot:MrRsiPeriod", 14),
            MrRsiOversold = configuration.GetValue("TradingBot:MrRsiOversold", 30m),
            MrRsiOverbought = configuration.GetValue("TradingBot:MrRsiOverbought", 70m)
        };
        
        // Parse DynamicStopLoss (nested object with tiers)
        var dynamicStopSection = configuration.GetSection("TradingBot:DynamicStopLoss");
        if (dynamicStopSection.Exists())
        {
            settings.DynamicStopLoss = new MarketBlocks.Bots.Domain.DynamicStopConfig
            {
                Enabled = dynamicStopSection.GetValue("Enabled", false),
                Tiers = new List<MarketBlocks.Bots.Domain.StopTier>()
            };
            var tiersSection = dynamicStopSection.GetSection("Tiers");
            foreach (var tierSection in tiersSection.GetChildren())
            {
                settings.DynamicStopLoss.Tiers.Add(new MarketBlocks.Bots.Domain.StopTier
                {
                    TriggerProfitPercent = tierSection.GetValue<decimal>("TriggerProfitPercent"),
                    StopPercent = tierSection.GetValue<decimal>("StopPercent")
                });
            }
        }
        
        // Parse HoldNeutralIfUnderwater (nested inside ExitStrategy, already parsed above but override was missing)
        settings.ExitStrategy.HoldNeutralIfUnderwater = configuration.GetValue("TradingBot:ExitStrategy:HoldNeutralIfUnderwater", true);
        
        // Parse TimeRules (auto phase switching)
        var timeRulesSection = configuration.GetSection("TradingBot:TimeRules");
        if (timeRulesSection.Exists())
        {
            foreach (var ruleSection in timeRulesSection.GetChildren())
            {
                var rule = new MarketBlocks.Bots.Domain.TimeBasedRule
                {
                    Name = ruleSection["Name"] ?? "Unnamed",
                    StartTime = TimeSpan.Parse(ruleSection["StartTime"] ?? "00:00"),
                    EndTime = TimeSpan.Parse(ruleSection["EndTime"] ?? "23:59"),
                    Overrides = ParseOverrides(ruleSection.GetSection("Overrides"))
                };
                settings.TimeRules.Add(rule);
            }
        }
        
        return settings;
    }
    
    private static MarketBlocks.Bots.Domain.TradingSettingsOverrides ParseOverrides(IConfigurationSection section)
    {
        var o = new MarketBlocks.Bots.Domain.TradingSettingsOverrides();
        if (!section.Exists()) return o;
        
        // Signal generation
        if (section["MinVelocityThreshold"] != null) o.MinVelocityThreshold = section.GetValue<decimal>("MinVelocityThreshold");
        if (section["EntryVelocityMultiplier"] != null) o.EntryVelocityMultiplier = section.GetValue<decimal>("EntryVelocityMultiplier");
        if (section["SMAWindowSeconds"] != null) o.SMAWindowSeconds = section.GetValue<int>("SMAWindowSeconds");
        if (section["SlopeWindowSize"] != null) o.SlopeWindowSize = section.GetValue<int>("SlopeWindowSize");
        if (section["ChopThresholdPercent"] != null) o.ChopThresholdPercent = section.GetValue<decimal>("ChopThresholdPercent");
        if (section["MinChopAbsolute"] != null) o.MinChopAbsolute = section.GetValue<decimal>("MinChopAbsolute");
        if (section["TrendWindowSeconds"] != null) o.TrendWindowSeconds = section.GetValue<int>("TrendWindowSeconds");
        if (section["EnableAdaptiveTrendWindow"] != null) o.EnableAdaptiveTrendWindow = section.GetValue<bool>("EnableAdaptiveTrendWindow");
        if (section["ShortTrendSlopeWindow"] != null) o.ShortTrendSlopeWindow = section.GetValue<int>("ShortTrendSlopeWindow");
        if (section["ShortTrendSlopeThreshold"] != null) o.ShortTrendSlopeThreshold = section.GetValue<decimal>("ShortTrendSlopeThreshold");
        if (section["DriftModeEnabled"] != null) o.DriftModeEnabled = section.GetValue<bool>("DriftModeEnabled");
        if (section["DriftModeConsecutiveTicks"] != null) o.DriftModeConsecutiveTicks = section.GetValue<int>("DriftModeConsecutiveTicks");
        if (section["DriftModeMinDisplacementPercent"] != null) o.DriftModeMinDisplacementPercent = section.GetValue<decimal>("DriftModeMinDisplacementPercent");
        if (section["DriftModeAtrMultiplier"] != null) o.DriftModeAtrMultiplier = section.GetValue<decimal>("DriftModeAtrMultiplier");
        if (section["DriftTrailingStopPercent"] != null) o.DriftTrailingStopPercent = section.GetValue<decimal>("DriftTrailingStopPercent");
        if (section["DisplacementReentryEnabled"] != null) o.DisplacementReentryEnabled = section.GetValue<bool>("DisplacementReentryEnabled");
        if (section["DisplacementReentryPercent"] != null) o.DisplacementReentryPercent = section.GetValue<decimal>("DisplacementReentryPercent");
        if (section["DisplacementAtrMultiplier"] != null) o.DisplacementAtrMultiplier = section.GetValue<decimal>("DisplacementAtrMultiplier");
        if (section["DisplacementChopThreshold"] != null) o.DisplacementChopThreshold = section.GetValue<decimal>("DisplacementChopThreshold");
        if (section["DisplacementBbwLookback"] != null) o.DisplacementBbwLookback = section.GetValue<int>("DisplacementBbwLookback");
        if (section["EntryConfirmationTicks"] != null) o.EntryConfirmationTicks = section.GetValue<int>("EntryConfirmationTicks");
        if (section["BearEntryConfirmationTicks"] != null) o.BearEntryConfirmationTicks = section.GetValue<int>("BearEntryConfirmationTicks");
        if (section["BullOnlyMode"] != null) o.BullOnlyMode = section.GetValue<bool>("BullOnlyMode");
        // Exit strategy (flattened)
        if (section["ScalpWaitSeconds"] != null) o.ScalpWaitSeconds = section.GetValue<int>("ScalpWaitSeconds");
        if (section["TrendWaitSeconds"] != null) o.TrendWaitSeconds = section.GetValue<int>("TrendWaitSeconds");
        if (section["TrendConfidenceThreshold"] != null) o.TrendConfidenceThreshold = section.GetValue<double>("TrendConfidenceThreshold");
        if (section["HoldNeutralIfUnderwater"] != null) o.HoldNeutralIfUnderwater = section.GetValue<bool>("HoldNeutralIfUnderwater");
        // Trade execution
        if (section["TrailingStopPercent"] != null) o.TrailingStopPercent = section.GetValue<decimal>("TrailingStopPercent");
        if (section["TrendRescueTrailingStopPercent"] != null) o.TrendRescueTrailingStopPercent = section.GetValue<decimal>("TrendRescueTrailingStopPercent");
        if (section["UseMarketableLimits"] != null) o.UseMarketableLimits = section.GetValue<bool>("UseMarketableLimits");
        if (section["UseIocOrders"] != null) o.UseIocOrders = section.GetValue<bool>("UseIocOrders");
        if (section["IocLimitOffsetCents"] != null) o.IocLimitOffsetCents = section.GetValue<decimal>("IocLimitOffsetCents");
        if (section["IocRetryStepCents"] != null) o.IocRetryStepCents = section.GetValue<decimal>("IocRetryStepCents");
        if (section["MaxSlippagePercent"] != null) o.MaxSlippagePercent = section.GetValue<decimal>("MaxSlippagePercent");
        if (section["MaxChaseDeviationPercent"] != null) o.MaxChaseDeviationPercent = section.GetValue<decimal>("MaxChaseDeviationPercent");
        if (section["LastEntryMinutesBeforeClose"] != null) o.LastEntryMinutesBeforeClose = section.GetValue<decimal>("LastEntryMinutesBeforeClose");
        // Dynamic stop loss (flattened)
        if (section["DynamicStopLossEnabled"] != null) o.DynamicStopLossEnabled = section.GetValue<bool>("DynamicStopLossEnabled");
        var tiersSection = section.GetSection("DynamicStopLossTiers");
        if (tiersSection.Exists())
        {
            o.DynamicStopLossTiers = new List<MarketBlocks.Bots.Domain.StopTier>();
            foreach (var tierSection in tiersSection.GetChildren())
            {
                o.DynamicStopLossTiers.Add(new MarketBlocks.Bots.Domain.StopTier
                {
                    TriggerProfitPercent = tierSection.GetValue<decimal>("TriggerProfitPercent"),
                    StopPercent = tierSection.GetValue<decimal>("StopPercent")
                });
            }
        }
        // Trimming
        if (section["EnableTrimming"] != null) o.EnableTrimming = section.GetValue<bool>("EnableTrimming");
        if (section["TrimTriggerPercent"] != null) o.TrimTriggerPercent = section.GetValue<decimal>("TrimTriggerPercent");
        if (section["TrimRatio"] != null) o.TrimRatio = section.GetValue<decimal>("TrimRatio");
        if (section["TrimSlopeThreshold"] != null) o.TrimSlopeThreshold = section.GetValue<decimal>("TrimSlopeThreshold");
        if (section["TrimCooldownSeconds"] != null) o.TrimCooldownSeconds = section.GetValue<int>("TrimCooldownSeconds");
        // Profit
        if (section["ProfitReinvestmentPercent"] != null) o.ProfitReinvestmentPercent = section.GetValue<decimal>("ProfitReinvestmentPercent");
        // Direction switch cooldown
        if (section["DirectionSwitchCooldownSeconds"] != null) o.DirectionSwitchCooldownSeconds = section.GetValue<int>("DirectionSwitchCooldownSeconds");
        // Mean reversion strategy
        if (section["BaseDefaultStrategy"] != null) o.BaseDefaultStrategy = Enum.Parse<MarketBlocks.Bots.Domain.StrategyMode>(section["BaseDefaultStrategy"]!, ignoreCase: true);
        if (section["PhDefaultStrategy"] != null) o.PhDefaultStrategy = Enum.Parse<MarketBlocks.Bots.Domain.StrategyMode>(section["PhDefaultStrategy"]!, ignoreCase: true);
        if (section["ChopOverrideEnabled"] != null) o.ChopOverrideEnabled = section.GetValue<bool>("ChopOverrideEnabled");
        if (section["ChopUpperThreshold"] != null) o.ChopUpperThreshold = section.GetValue<decimal>("ChopUpperThreshold");
        if (section["ChopLowerThreshold"] != null) o.ChopLowerThreshold = section.GetValue<decimal>("ChopLowerThreshold");
        if (section["BollingerWindow"] != null) o.BollingerWindow = section.GetValue<int>("BollingerWindow");
        if (section["BollingerMultiplier"] != null) o.BollingerMultiplier = section.GetValue<decimal>("BollingerMultiplier");
        if (section["ChopPeriod"] != null) o.ChopPeriod = section.GetValue<int>("ChopPeriod");
        if (section["ChopCandleSeconds"] != null) o.ChopCandleSeconds = section.GetValue<int>("ChopCandleSeconds");
        if (section["MrEntryLowPctB"] != null) o.MrEntryLowPctB = section.GetValue<decimal>("MrEntryLowPctB");
        if (section["MrEntryHighPctB"] != null) o.MrEntryHighPctB = section.GetValue<decimal>("MrEntryHighPctB");
        if (section["MrExitPctB"] != null) o.MrExitPctB = section.GetValue<decimal>("MrExitPctB");
        if (section["MeanRevStopPercent"] != null) o.MeanRevStopPercent = section.GetValue<decimal>("MeanRevStopPercent");
        if (section["MrAtrStopMultiplier"] != null) o.MrAtrStopMultiplier = section.GetValue<decimal>("MrAtrStopMultiplier");
        if (section["MrRequireRsi"] != null) o.MrRequireRsi = section.GetValue<bool>("MrRequireRsi");
        if (section["MrRsiPeriod"] != null) o.MrRsiPeriod = section.GetValue<int>("MrRsiPeriod");
        if (section["MrRsiOversold"] != null) o.MrRsiOversold = section.GetValue<decimal>("MrRsiOversold");
        if (section["MrRsiOverbought"] != null) o.MrRsiOverbought = section.GetValue<decimal>("MrRsiOverbought");
        
        return o;
    }
    
    /// <summary>
    /// Peek at the first CSV for the given date and decide whether the data is
    /// high-resolution recorded ticks (sub-second) or low-resolution historical bars (60 s+).
    /// Returns true when interpolation should be skipped (i.e. data is already dense enough).
    /// </summary>
    private static bool IsHighResolutionData(string dataDir, string dateStr, ILogger logger)
    {
        const int samplesToRead = 100;
        const double highResThresholdSeconds = 10.0; // avg gap < 10s ⇒ high-res

        try
        {
            var pattern = $"{dateStr}_market_data_*.csv";
            var csvFiles = Directory.GetFiles(dataDir, pattern);
            if (csvFiles.Length == 0)
            {
                logger.LogWarning("IsHighResolutionData: no CSV files matching {Pattern} in {Dir}", pattern, dataDir);
                return false; // fall back to interpolation
            }

            var filePath = csvFiles[0]; // any symbol will do — they all share the same recording cadence
            using var reader = new StreamReader(filePath);

            // Skip header
            var header = reader.ReadLine();
            if (header == null) return false;

            var timestamps = new List<DateTime>(samplesToRead);
            string? line;
            while (timestamps.Count < samplesToRead && (line = reader.ReadLine()) != null)
            {
                var comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (DateTime.TryParse(line.AsSpan(0, comma),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var ts))
                {
                    timestamps.Add(ts);
                }
            }

            if (timestamps.Count < 2)
            {
                logger.LogWarning("IsHighResolutionData: not enough data rows in {File}", filePath);
                return false;
            }

            // Compute average gap between consecutive timestamps
            double totalGapSeconds = 0;
            for (int i = 1; i < timestamps.Count; i++)
                totalGapSeconds += (timestamps[i] - timestamps[i - 1]).TotalSeconds;

            double avgGap = totalGapSeconds / (timestamps.Count - 1);
            bool highRes = avgGap < highResThresholdSeconds;

            if (highRes)
                logger.LogInformation("High-resolution tick data detected (avg gap {Gap:F2}s). Skipping Brownian bridge interpolation.", avgGap);
            else
                logger.LogInformation("Low-resolution bar data detected (avg gap {Gap:F1}s). Using Brownian bridge interpolation.", avgGap);

            return highRes;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "IsHighResolutionData: error checking data resolution, defaulting to interpolation");
            return false;
        }
    }
}

/// <summary>
/// Orchestration service that starts the Analyst and Trader engines
/// and wires them together via Channel<MarketRegime>.
/// </summary>
public class TradingOrchestrator : BackgroundService
{
    private readonly ILogger<TradingOrchestrator> _logger;
    private readonly AnalystEngine _analyst;
    private readonly TraderEngine _trader;
    private readonly MarketBlocks.Bots.Domain.TradingSettings _settings;
    private readonly IHostApplicationLifetime _lifetime;
    
    public TradingOrchestrator(
        ILogger<TradingOrchestrator> logger,
        AnalystEngine analyst,
        TraderEngine trader,
        MarketBlocks.Bots.Domain.TradingSettings settings,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _analyst = analyst;
        _trader = trader;
        _settings = settings;
        _lifetime = lifetime;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== QQQ Trading Bot Starting ===");
        _logger.LogInformation("Architecture: Producer/Consumer with Channel<MarketRegime>");
        _logger.LogInformation("");
        
        // Log configuration file and key parameters
        _logger.LogInformation("Config File: {ConfigFile}", ProgramRefactored.ConfigFileName);
        _logger.LogInformation("");
        _logger.LogInformation("Analytics:");
        _logger.LogInformation("  MinVelocityThreshold: {Value}", _settings.MinVelocityThreshold);
            _logger.LogInformation("  EntryVelocityMultiplier: {Value}", _settings.EntryVelocityMultiplier);
        _logger.LogInformation("  SMAWindowSeconds: {Value}", _settings.SMAWindowSeconds);
        _logger.LogInformation("  SlopeWindowSize: {Value}", _settings.SlopeWindowSize);
        _logger.LogInformation("  ChopThresholdPercent: {Value:P4}", _settings.ChopThresholdPercent);
        _logger.LogInformation("  MinChopAbsolute: {Value:C}", _settings.MinChopAbsolute);
        _logger.LogInformation("  TrendWindowSeconds: {Value}", _settings.TrendWindowSeconds);
        _logger.LogInformation("");
        _logger.LogInformation("Trading:");
        _logger.LogInformation("  UseMarketableLimits: {Value}", _settings.UseMarketableLimits);
        _logger.LogInformation("  IocLimitOffsetCents: {Value}", _settings.IocLimitOffsetCents);
        _logger.LogInformation("  TrailingStopPercent: {Value:P2}", _settings.TrailingStopPercent);
        _logger.LogInformation("  TrendRescueTrailingStopPercent: {Value:P2}", _settings.TrendRescueTrailingStopPercent);
        _logger.LogInformation("");
        _logger.LogInformation("Profit:");
        _logger.LogInformation("  ProfitReinvestmentPercent: {Value:P0}", _settings.ProfitReinvestmentPercent);
        _logger.LogInformation("  EnableTrimming: {Value}", _settings.EnableTrimming);
        _logger.LogInformation("  TrimRatio: {Value:P0}", _settings.TrimRatio);
        _logger.LogInformation("  TrimTriggerPercent: {Value:P2}", _settings.TrimTriggerPercent);
        _logger.LogInformation("  TrimSlopeThreshold: {Value}", _settings.TrimSlopeThreshold);
        _logger.LogInformation("  TrimCooldownSeconds: {Value}", _settings.TrimCooldownSeconds);
        _logger.LogInformation("");
        _logger.LogInformation("Exit Strategy:");
        _logger.LogInformation("  ScalpWaitSeconds: {Value}", _settings.ExitStrategy.ScalpWaitSeconds);
        _logger.LogInformation("  TrendWaitSeconds: {Value}", _settings.ExitStrategy.TrendWaitSeconds);
        _logger.LogInformation("  TrendConfidenceThreshold: {Value}", _settings.ExitStrategy.TrendConfidenceThreshold);
        _logger.LogInformation("");
        _logger.LogInformation("Symbols:");
        _logger.LogInformation("  Bot ID: {BotId}", _settings.BotId);
        _logger.LogInformation("  Bull: {Bull} | Bear: {Bear} | Benchmark: {Bench}",
            _settings.BullSymbol,
            _settings.BearSymbol ?? "(none - bull-only)",
            _settings.BenchmarkSymbol);
        _logger.LogInformation("  Mode: {Mode}",
            _settings.LowLatencyMode ? "Low-Latency Streaming" : "HTTP Polling");
        if (_settings.TimeRules.Count > 0)
        {
            _logger.LogInformation("  Time Rules: {Count} phase(s) configured", _settings.TimeRules.Count);
            foreach (var rule in _settings.TimeRules)
                _logger.LogInformation("    {Name}: {Start} -> {End}", 
                    rule.Name, rule.StartTime.ToString(@"hh\:mm"), rule.EndTime.ToString(@"hh\:mm"));
        }
        _logger.LogInformation("");
        
        try
        {
            // Start trader (consumer) first - it needs to be ready to receive
            _logger.LogInformation("[ORCHESTRATOR] Starting Trader Engine...");
            await _trader.StartAsync(_analyst.RegimeChannel, stoppingToken);
            
            // Check if repair mode was triggered during startup verification
            if (_trader.RepairModeTriggered)
            {
                _logger.LogWarning("[ORCHESTRATOR] Repair mode triggered. Shutting down...");
                _lifetime.StopApplication();
                return;
            }
            
            // Start analyst (producer) - it will begin emitting signals
            _logger.LogInformation("[ORCHESTRATOR] Starting Analyst Engine...");
            await _analyst.StartAsync(stoppingToken);
            
            _logger.LogInformation("[ORCHESTRATOR] Both engines running. Ctrl+C to shutdown gracefully.");
            
            // Wait for cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[ORCHESTRATOR] Shutdown requested.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ORCHESTRATOR] Fatal error");
            _lifetime.StopApplication();
        }
        finally
        {
            // Graceful shutdown
            _logger.LogInformation("[ORCHESTRATOR] Stopping engines...");
            await _analyst.StopAsync(CancellationToken.None);
            await _trader.StopAsync(CancellationToken.None);
            _logger.LogInformation("[ORCHESTRATOR] Shutdown complete.");
        }
    }
}

/// <summary>
/// Replay orchestrator that runs the Analyst and Trader engines against recorded CSV data.
/// Automatically shuts down the host when replay completes and prints a summary.
/// </summary>
public class ReplayOrchestrator : BackgroundService
{
    private readonly ILogger<ReplayOrchestrator> _logger;
    private readonly AnalystEngine _analyst;
    private readonly TraderEngine _trader;
    private readonly SimulatedBroker _simulatedBroker;
    private readonly MarketBlocks.Bots.Domain.TradingSettings _settings;
    private readonly IHostApplicationLifetime _lifetime;

    public ReplayOrchestrator(
        ILogger<ReplayOrchestrator> logger,
        AnalystEngine analyst,
        TraderEngine trader,
        SimulatedBroker simulatedBroker,
        MarketBlocks.Bots.Domain.TradingSettings settings,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _analyst = analyst;
        _trader = trader;
        _simulatedBroker = simulatedBroker;
        _settings = settings;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== QQQ Trading Bot — REPLAY MODE ===");
        _logger.LogInformation("Config File: {ConfigFile}", ProgramRefactored.ConfigFileName);
        
        // Replay metadata header
        var replayOverrides = ProgramRefactored.CurrentOverrides;
        _logger.LogInformation("Replay Date:     {Date}", replayOverrides?.ReplayDate ?? "unknown");
        _logger.LogInformation("Wall-Clock Start: {Now}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        _logger.LogInformation("Speed:           {Speed}x", replayOverrides?.ReplaySpeed ?? 10.0);
        if (replayOverrides?.StartTime.HasValue == true || replayOverrides?.EndTime.HasValue == true)
        {
            _logger.LogInformation("Segment:         {Start} -> {End} (Eastern)",
                replayOverrides.StartTime?.ToString("HH:mm") ?? "open",
                replayOverrides.EndTime?.ToString("HH:mm") ?? "close");
        }
        _logger.LogInformation("Starting Amount: ${Amount:N2}", _settings.StartingAmount);
        _logger.LogInformation("Bull: {Bull} | Bear: {Bear} | Benchmark: {Bench}",
            _settings.BullSymbol,
            _settings.BearSymbol ?? "(none - bull-only)",
            _settings.BenchmarkSymbol);
        _logger.LogInformation("");

        try
        {
            // Start trader (consumer) first
            _logger.LogInformation("[REPLAY] Starting Trader Engine...");
            await _trader.StartAsync(_analyst.RegimeChannel, stoppingToken);

            // Start analyst (producer) — will replay CSV and complete
            _logger.LogInformation("[REPLAY] Starting Analyst Engine (CSV replay)...");
            await _analyst.StartAsync(stoppingToken);

            _logger.LogInformation("[REPLAY] Waiting for replay to complete...");
            
            // Wait for both engines to finish naturally, but with a safety timeout
            // to prevent zombie processes if an engine hangs.
            var analystDone = _analyst.ExecuteTask ?? Task.CompletedTask;
            var traderDone  = _trader.ExecuteTask  ?? Task.CompletedTask;
            var bothDone = Task.WhenAll(analystDone, traderDone);
            
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            var winner = await Task.WhenAny(bothDone, timeoutTask);
            
            if (winner == timeoutTask)
            {
                var analystStatus = analystDone.IsCompleted ? "done" : "HANGING";
                var traderStatus = traderDone.IsCompleted ? "done" : "HANGING";
                _logger.LogWarning("[REPLAY] ⚠ Timeout waiting for engines to finish! " +
                    "Analyst: {AnalystStatus}, Trader: {TraderStatus}. Forcing shutdown.",
                    analystStatus, traderStatus);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal — replay data exhausted or Ctrl+C
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[REPLAY] Error during replay");
        }
        finally
        {
            _logger.LogInformation("[REPLAY] Stopping engines...");
            await _analyst.StopAsync(CancellationToken.None);
            await _trader.StopAsync(CancellationToken.None);

            // Print the simulated trading summary
            _simulatedBroker.PrintSummary();

            _logger.LogInformation("[REPLAY] Replay complete. Shutting down.");
            Environment.ExitCode = 0;
            _lifetime.StopApplication();
        }
    }
}
