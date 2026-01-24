// ============================================================================
// QQQ Trading Bot - Refactored with Producer/Consumer Architecture
// This demonstrates the new decoupled design using Channel<MarketRegime>
// ============================================================================

using System.Threading.Channels;
using Alpaca.Markets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;
using MarketBlocks.Core.Services;
using MarketBlocks.Infrastructure.Alpaca;
using MarketBlocks.Infrastructure.Common;
using MarketBlocks.Components;

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
    
    public static async Task RunAsync(string[] args)
    {
        var host = CreateHostBuilder(args).Build();
        await host.RunAsync();
    }
    
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.SetBasePath(AppContext.BaseDirectory)
                      .AddJsonFile("appsettings.json", optional: false)
                      .AddUserSecrets(typeof(ProgramRefactored).Assembly);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;
                
                // Load and validate API credentials
                var apiKey = configuration["Alpaca:ApiKey"];
                var apiSecret = configuration["Alpaca:ApiSecret"];
                
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
                
                // Build TradingSettings from configuration
                var settings = BuildTradingSettings(configuration);
                services.AddSingleton(settings);
                
                // Register Alpaca clients
                var secretKey = new SecretKey(apiKey, apiSecret);
                services.AddSingleton(Alpaca.Markets.Environments.Paper.GetAlpacaTradingClient(secretKey));
                services.AddSingleton(Alpaca.Markets.Environments.Paper.GetAlpacaDataClient(secretKey));
                services.AddSingleton(Alpaca.Markets.Environments.Paper.GetAlpacaCryptoDataClient(secretKey));
                
                // Register broker execution adapter
                services.AddSingleton<IBrokerExecution, AlpacaExecutionAdapter>();
                
                // Register IOC executor (both concrete and interface)
                services.AddSingleton<IocMachineGunExecutor>(sp =>
                {
                    var broker = sp.GetRequiredService<IBrokerExecution>();
                    var s = sp.GetRequiredService<MarketBlocks.Core.Domain.TradingSettings>();
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
                    var s = sp.GetRequiredService<MarketBlocks.Core.Domain.TradingSettings>();
                    if (s.LowLatencyMode)
                    {
                        return Alpaca.Markets.Environments.Paper.GetAlpacaDataStreamingClient(secretKey);
                    }
                    // Return a null placeholder - won't be used in polling mode
                    return null!;
                });
                
                services.AddSingleton(sp =>
                {
                    var s = sp.GetRequiredService<MarketBlocks.Core.Domain.TradingSettings>();
                    if (s.LowLatencyMode)
                    {
                        return Alpaca.Markets.Environments.Paper.GetAlpacaCryptoStreamingClient(secretKey);
                    }
                    return null!;
                });
                
                // Register market data source for AnalystEngine (uses IAnalystMarketDataSource)
                services.AddSingleton<MarketBlocks.Core.Services.IAnalystMarketDataSource>(sp =>
                {
                    var s = sp.GetRequiredService<MarketBlocks.Core.Domain.TradingSettings>();
                    var broker = sp.GetRequiredService<IBrokerExecution>();
                    
                    if (s.LowLatencyMode)
                    {
                        // Low-latency mode uses WebSocket streaming
                        var stockClient = sp.GetRequiredService<IAlpacaDataStreamingClient>();
                        var cryptoClient = sp.GetRequiredService<IAlpacaCryptoStreamingClient>();
                        var logger = sp.GetRequiredService<ILogger<StreamingAnalystDataSource>>();
                        return new StreamingAnalystDataSource(stockClient, cryptoClient, logger);
                    }
                    else
                    {
                        var logger = sp.GetRequiredService<ILogger<PollingAnalystDataSource>>();
                        return new PollingAnalystDataSource(broker, s.PollingIntervalSeconds * 1000, logger);
                    }
                });
                
                // Register TraderEngine first (Trader is injected into Analyst for position awareness)
                services.AddSingleton<TraderEngine>();
                
                // Register AnalystEngine with position callbacks from TraderEngine
                services.AddSingleton<AnalystEngine>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<AnalystEngine>>();
                    var s = sp.GetRequiredService<MarketBlocks.Core.Domain.TradingSettings>();
                    var marketSource = sp.GetRequiredService<MarketBlocks.Core.Services.IAnalystMarketDataSource>();
                    var trader = sp.GetRequiredService<TraderEngine>();
                    
                    return new AnalystEngine(
                        logger,
                        s,
                        marketSource,
                        () => trader.CurrentPosition,
                        () => trader.CurrentShares);
                });
                
                // Register the orchestration service that wires Analyst → Trader
                services.AddHostedService<TradingOrchestrator>();
            });
    
    private static MarketBlocks.Core.Domain.TradingSettings BuildTradingSettings(IConfiguration configuration)
    {
        return new MarketBlocks.Core.Domain.TradingSettings
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
            NeutralWaitSeconds = configuration.GetValue("TradingBot:NeutralWaitSeconds", 30),
            WatchBtc = configuration.GetValue("TradingBot:WatchBtc", false),
            MonitorSlippage = configuration.GetValue("TradingBot:MonitorSlippage", false),
            TrailingStopPercent = configuration.GetValue("TradingBot:TrailingStopPercent", 0.0m),
            StopLossCooldownSeconds = configuration.GetValue("TradingBot:StopLossCooldownSeconds", 10),
            StartingAmount = configuration.GetValue("TradingBot:StartingAmount", 10000m),
            UseMarketableLimits = configuration.GetValue("TradingBot:UseMarketableLimits", false),
            MaxSlippagePercent = configuration.GetValue("TradingBot:MaxSlippagePercent", 0.002m),
            MaxChaseDeviationPercent = configuration.GetValue("TradingBot:MaxChaseDeviationPercent", 0.003m),
            LowLatencyMode = configuration.GetValue("TradingBot:LowLatencyMode", false),
            UseIocOrders = configuration.GetValue("TradingBot:UseIocOrders", false),
            IocLimitOffsetCents = configuration.GetValue("TradingBot:IocLimitOffsetCents", 1m),
            IocMaxRetries = configuration.GetValue("TradingBot:IocMaxRetries", 5),
            IocRetryStepCents = configuration.GetValue("TradingBot:IocRetryStepCents", 1m),
            IocMaxDeviationPercent = configuration.GetValue("TradingBot:IocMaxDeviationPercent", 0.005m),
            IocRemainingSharesTolerance = configuration.GetValue("TradingBot:IocRemainingSharesTolerance", 2),
            KeepAlivePingSeconds = configuration.GetValue("TradingBot:KeepAlivePingSeconds", 5),
            WarmUpIterations = configuration.GetValue("TradingBot:WarmUpIterations", 10000),
            StatusLogIntervalSeconds = configuration.GetValue("TradingBot:StatusLogIntervalSeconds", 5)
        };
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
    private readonly MarketBlocks.Core.Domain.TradingSettings _settings;
    private readonly IHostApplicationLifetime _lifetime;
    
    public TradingOrchestrator(
        ILogger<TradingOrchestrator> logger,
        AnalystEngine analyst,
        TraderEngine trader,
        MarketBlocks.Core.Domain.TradingSettings settings,
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
        _logger.LogInformation("Configuration:");
        _logger.LogInformation("  Bot ID: {BotId}", _settings.BotId);
        _logger.LogInformation("  Bull: {Bull} | Bear: {Bear} | Benchmark: {Bench}",
            _settings.BullSymbol,
            _settings.BearSymbol ?? "(none - bull-only)",
            _settings.BenchmarkSymbol);
        _logger.LogInformation("  SMA Window: {Window}s | Chop: {Chop:P3}",
            _settings.SMAWindowSeconds, _settings.ChopThresholdPercent);
        _logger.LogInformation("  Mode: {Mode}",
            _settings.LowLatencyMode ? "Low-Latency Streaming" : "HTTP Polling");
        _logger.LogInformation("");
        
        try
        {
            // Start trader (consumer) first - it needs to be ready to receive
            _logger.LogInformation("[ORCHESTRATOR] Starting Trader Engine...");
            await _trader.StartAsync(_analyst.RegimeChannel, stoppingToken);
            
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
