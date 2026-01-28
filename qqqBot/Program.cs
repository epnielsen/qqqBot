using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Alpaca.Markets;
using CsvHelper;
using Microsoft.Extensions.Configuration;
using qqqBot;
using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using MarketBlocks.Trade.Math;
using MarketBlocks.Infrastructure.Alpaca;
using MarketBlocks.Infrastructure.Common;
using MarketBlocks.Trade.Components;

// Alias local types to avoid ambiguity with MarketBlocks.Trade.Domain versions
using TradingState = qqqBot.TradingState;
using TradingStateMetadata = qqqBot.TradingStateMetadata;
using OrphanedPosition = qqqBot.OrphanedPosition;

// ============================================================================
// QQQ Trading Bot - .NET 10 Alpaca Paper Trading Bot
// Strategy: SMA-based Stop & Reverse between TQQQ (Bull) and SQQQ (Bear)
// ============================================================================

const string PAPER_KEY_PREFIX = "PK";

// Check for setup mode
if (args.Length > 0 && args[0].Equals("--setup", StringComparison.OrdinalIgnoreCase))
{
    await RunSetupAsync();
    return;
}

// Check for report mode
if (args.Any(a => a.Equals("-report", StringComparison.OrdinalIgnoreCase)))
{
    await RunReportAsync(args);
    return;
}

// Check for legacy mode (Monolithic architecture)
// ERROR: We default to refactored mode now. Use --legacy to run the old logic.
if (args.Any(a => a.Equals("--legacy", StringComparison.OrdinalIgnoreCase)))
{
    args = args.Where(a => !a.Equals("--legacy", StringComparison.OrdinalIgnoreCase)).ToArray();
}
else
{
    // Default: Refactored mode (Producer/Consumer architecture)
    var filteredArgs = args.Where(a => !a.Equals("--refactored", StringComparison.OrdinalIgnoreCase)).ToArray();
    await qqqBot.ProgramRefactored.RunAsync(filteredArgs);
    return;
}

// Parse command line overrides
var cmdOverrides = CommandLineOverrides.Parse(args);
if (cmdOverrides == null)
{
    // Parsing failed with error message already printed
    return;
}

// Set up graceful shutdown on Ctrl+C
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true; // Prevent immediate termination
    Log("\n⚠️  Shutdown requested (Ctrl+C). Exiting gracefully...");
    cts.Cancel();
};

// Run the trading bot
await RunTradingBotAsync(cmdOverrides, cts.Token);

// ============================================================================
// COMMAND LINE PARSING
// ============================================================================
// Logic moved to CommandLineOverrides.cs


// ============================================================================
// SETUP CONFIGURATION
// ============================================================================
async Task RunSetupAsync()
{
    Log("=== Alpaca API Key Setup ===");
    Log("This will store your API credentials in .NET User Secrets.");
    Log("IMPORTANT: Only PAPER trading keys (starting with 'PK') are allowed!\n");

    string apiKey;
    while (true)
    {
        Console.Write("Enter Alpaca API Key: ");
        apiKey = Console.ReadLine()?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey))
        {
            LogError("API Key cannot be empty. Please try again.");
            continue;
        }

        if (!apiKey.StartsWith(PAPER_KEY_PREFIX, StringComparison.OrdinalIgnoreCase))
        {
            LogError("LIVE TRADING KEYS NOT ALLOWED");
            LogError("API Key must start with 'PK' (Paper Trading key). Please try again.\n");
            continue;
        }

        break;
    }

    Console.Write("Enter Alpaca API Secret: ");
    var apiSecret = Console.ReadLine()?.Trim() ?? string.Empty;

    if (string.IsNullOrEmpty(apiSecret))
    {
        LogError("API Secret cannot be empty. Setup aborted.");
        return;
    }

    // Store in User Secrets using dotnet CLI
    Log("\nStoring credentials in User Secrets...");

    var projectDir = Directory.GetCurrentDirectory();
    
    var keyResult = await RunDotnetCommandAsync($"user-secrets set \"Alpaca:ApiKey\" \"{apiKey}\" --project \"{projectDir}\"");
    if (!keyResult)
    {
        LogError("Failed to store API Key. Make sure you have the .NET SDK installed.");
        return;
    }

    var secretResult = await RunDotnetCommandAsync($"user-secrets set \"Alpaca:ApiSecret\" \"{apiSecret}\" --project \"{projectDir}\"");
    if (!secretResult)
    {
        LogError("Failed to store API Secret.");
        return;
    }

    LogSuccess("\nSetup complete! Your credentials have been securely stored.");
    LogSuccess("You can now run the bot without the --setup flag.");
}

async Task<bool> RunDotnetCommandAsync(string arguments)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return false;

        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

// ============================================================================
// REPORT GENERATION
// ============================================================================
async Task RunReportAsync(string[] args)
{
    Log("=== QQQ Trading Bot - Daily Report ===\n");

    // Load configuration
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddUserSecrets<Program>()
        .Build();

    // Load API credentials
    var apiKey = configuration["Alpaca:ApiKey"];
    var apiSecret = configuration["Alpaca:ApiSecret"];

    if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
    {
        LogError("API credentials not found. Please run with --setup flag first.");
        return;
    }

    // CRITICAL: Runtime safety check
    if (!apiKey.StartsWith(PAPER_KEY_PREFIX, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "SAFETY VIOLATION: Live trading keys detected! " +
            "This bot is designed for PAPER TRADING ONLY.");
    }

    // Parse optional -bull, -bear, and -botid overrides from args
    string? bullOverride = null;
    string? bearOverride = null;
    string? botIdOverride = null;
    foreach (var arg in args)
    {
        if (arg.StartsWith("-bull=", StringComparison.OrdinalIgnoreCase))
            bullOverride = arg.Substring("-bull=".Length).Trim().ToUpperInvariant();
        else if (arg.StartsWith("-bear=", StringComparison.OrdinalIgnoreCase))
            bearOverride = arg.Substring("-bear=".Length).Trim().ToUpperInvariant();
        else if (arg.StartsWith("-botid=", StringComparison.OrdinalIgnoreCase))
            botIdOverride = arg.Substring("-botid=".Length).Trim();
    }

    // Load symbols and BotId from config (with optional overrides)
    var bullSymbol = bullOverride ?? configuration["TradingBot:BullSymbol"] ?? "TQQQ";
    var bearSymbol = bearOverride ?? configuration["TradingBot:BearSymbol"] ?? "SQQQ";
    var botId = botIdOverride ?? configuration["TradingBot:BotId"] ?? "main";
    var clientOrderPrefix = $"qqqBot-{botId}-";

    Log($"Generating report for: {bullSymbol} / {bearSymbol} (BotId: {botId})");

    // Initialize Alpaca client
    var secretKey = new SecretKey(apiKey, apiSecret);
    using var tradingClient = Environments.Paper.GetAlpacaTradingClient(secretKey);

    // Get today's date range in UTC
    var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    var easternNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
    var todayEastern = easternNow.Date;
    
    // Midnight ET to now (includes pre-market orders)
    var startOfDayUtc = TimeZoneInfo.ConvertTimeToUtc(todayEastern, easternZone);
    var endOfDayUtc = DateTime.UtcNow;

    Log($"Fetching orders from {startOfDayUtc:yyyy-MM-dd HH:mm:ss} UTC to {endOfDayUtc:yyyy-MM-dd HH:mm:ss} UTC...\n");

    // Fetch all closed orders for today, paginating if necessary
    var allOrders = new List<IOrder>();
    DateTime? lastOrderTime = null;
    int pageCount = 0;
    // Track seen order IDs to avoid duplicates from pagination edge cases
    var seenOrderIds = new HashSet<Guid>();
    
    while (true)
    {
        pageCount++;
        var ordersRequest = new ListOrdersRequest
        {
            OrderStatusFilter = OrderStatusFilter.Closed,
            RollUpNestedOrders = false,
            LimitOrderNumber = 500,
            OrderListSorting = SortDirection.Ascending
        }.WithInterval(new Interval<DateTime>(lastOrderTime ?? startOfDayUtc, endOfDayUtc));

        var pageOrders = await tradingClient.ListOrdersAsync(ordersRequest);
        
        if (pageOrders.Count == 0)
            break;
            
        // Filter out duplicates by OrderId (handles edge case where orders share CreatedAtUtc)
        var newOrders = pageOrders
            .Where(o => !seenOrderIds.Contains(o.OrderId))
            .ToList();
        
        if (newOrders.Count == 0)
            break;
        
        foreach (var order in newOrders)
            seenOrderIds.Add(order.OrderId);
            
        allOrders.AddRange(newOrders);
        lastOrderTime = pageOrders.Max(o => o.CreatedAtUtc);
        
        Log($"  Page {pageCount}: Retrieved {pageOrders.Count} orders, {newOrders.Count} new (total: {allOrders.Count})");
        
        // If we got fewer than 500, we've reached the end
        if (pageOrders.Count < 500)
            break;
    }
    
    Log($"Retrieved {allOrders.Count} total closed orders from API.");
    
    // Filter to our symbols AND today's fills AND this bot's orders (by ClientOrderId prefix)
    var relevantOrders = allOrders
        .Where(o => (o.Symbol.Equals(bullSymbol, StringComparison.OrdinalIgnoreCase) ||
                     o.Symbol.Equals(bearSymbol, StringComparison.OrdinalIgnoreCase)) &&
                    o.FilledAtUtc != null &&
                    o.FilledAtUtc >= startOfDayUtc &&
                    o.FilledAtUtc <= endOfDayUtc &&
                    (o.ClientOrderId?.StartsWith(clientOrderPrefix, StringComparison.OrdinalIgnoreCase) ?? false))
        .OrderBy(o => o.FilledAtUtc)
        .ToList();
    
    Log($"Filtered to {relevantOrders.Count} orders for {bullSymbol}/{bearSymbol} with BotId '{botId}' filled today.");
    
    // If no orders match this bot, show orders without ClientOrderId filter for comparison
    if (relevantOrders.Count == 0)
    {
        var allSymbolOrders = allOrders
            .Where(o => (o.Symbol.Equals(bullSymbol, StringComparison.OrdinalIgnoreCase) ||
                         o.Symbol.Equals(bearSymbol, StringComparison.OrdinalIgnoreCase)) &&
                        o.FilledAtUtc != null &&
                        o.FilledAtUtc >= startOfDayUtc &&
                        o.FilledAtUtc <= endOfDayUtc)
            .ToList();
        
        if (allSymbolOrders.Count > 0)
        {
            Log($"Note: Found {allSymbolOrders.Count} orders for {bullSymbol}/{bearSymbol} without BotId filter.");
            Log("These may be from before BotId tagging was implemented, or from other bot instances.");
        }
        
        Log("No trades found for today with this BotId.");
        return;
    }
    
    // Show date range of included orders for verification
    var firstFill = relevantOrders.First().FilledAtUtc;
    var lastFill = relevantOrders.Last().FilledAtUtc;
    Log($"Order range: {firstFill:yyyy-MM-dd HH:mm:ss} UTC to {lastFill:yyyy-MM-dd HH:mm:ss} UTC");

    // Build trade records for CSV
    var records = new List<TradeRecord>();
    decimal totalBuyValue = 0m;
    decimal totalSellValue = 0m;
    int buyCount = 0;
    int sellCount = 0;

    foreach (var order in relevantOrders)
    {
        var filledQty = order.FilledQuantity;
        var avgPrice = order.AverageFillPrice;
        var filledValue = filledQty * (avgPrice ?? 0m);

        var record = new TradeRecord
        {
            Timestamp = order.FilledAtUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            Symbol = order.Symbol,
            Side = order.OrderSide.ToString(),
            Quantity = filledQty,
            FilledPrice = avgPrice,
            FilledValue = filledValue,
            OrderId = order.OrderId.ToString(),
            Status = order.OrderStatus.ToString()
        };
        records.Add(record);

        if (order.OrderSide == OrderSide.Buy)
        {
            totalBuyValue += filledValue;
            buyCount++;
        }
        else
        {
            totalSellValue += filledValue;
            sellCount++;
        }
    }

    // Generate filename: qqqBot-report-BULL-BEAR-YYYYMMDD.csv
    var dateStr = todayEastern.ToString("yyyyMMdd");
    var fileName = $"qqqBot-report-{bullSymbol}-{bearSymbol}-{dateStr}.csv";
    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);

    // Write CSV (with graceful handling if file is locked)
    try
    {
        using (var writer = new StreamWriter(filePath))
        using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(records);
        }
        LogSuccess($"Report saved to: {filePath}");
    }
    catch (IOException ex) when (ex.Message.Contains("being used by another process"))
    {
        LogError($"Cannot write to {fileName} - file is open in another program (Excel?).");
        LogError("Close the file and run the report again, or the report will only display to console.");
    }
    
    Log("");

    // Load trading state for P/L calculations
    var stateFilePath = Path.Combine(AppContext.BaseDirectory, "trading_state.json");
    var tradingState = LoadTradingState(stateFilePath);
    
    // Calculate current balance
    var currentBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
    
    // Get StartingAmount from config (the original capital)
    var startingAmount = configuration.GetValue("TradingBot:StartingAmount", 10000m);
    
    // Total P/L (from original starting amount)
    var totalPL = currentBalance - startingAmount;
    var totalPLPercent = startingAmount > 0 ? (totalPL / startingAmount) * 100 : 0;
    
    // Daily P/L (from day start balance)
    var dayStartBalance = tradingState.DayStartBalance > 0 ? tradingState.DayStartBalance : startingAmount;
    var dailyPL = currentBalance - dayStartBalance;
    var dailyPLPercent = dayStartBalance > 0 ? (dailyPL / dayStartBalance) * 100 : 0;
    
    // P/L verification from fills
    // For a balanced day (start in cash, end in cash), Net from Fills should equal Daily P/L
    var fillBasedPL = totalSellValue - totalBuyValue;
    var discrepancy = dailyPL - fillBasedPL;

    // Print summary
    Log("=== Summary ===");
    Log($"Total Transactions: {records.Count} ({buyCount} buys, {sellCount} sells)");
    Log($"Total Buy Value:    ${totalBuyValue:N2}");
    Log($"Total Sell Value:   ${totalSellValue:N2}");
    Log($"Net from Fills:     ${fillBasedPL:N2}");
    Log("");
    Log($"Day Start Balance:  ${dayStartBalance:N2}");
    Log($"Current Balance:    ${currentBalance:N2}");
    Log("");
    
    // Show first and last trades for context
    var firstOrder = relevantOrders.First();
    var lastOrder = relevantOrders.Last();
    Log($"First trade: {firstOrder.OrderSide} {firstOrder.Symbol} @ {firstOrder.FilledAtUtc:HH:mm:ss} UTC");
    Log($"Last trade:  {lastOrder.OrderSide} {lastOrder.Symbol} @ {lastOrder.FilledAtUtc:HH:mm:ss} UTC");
    
    // Check for imbalance
    if (buyCount != sellCount)
    {
        Log($"⚠️  Buy/Sell imbalance: {Math.Abs(buyCount - sellCount)} unmatched {(buyCount > sellCount ? "buy(s)" : "sell(s)")}");
    }
    Log("");
    
    // Daily P/L
    if (dailyPL >= 0)
    {
        LogSuccess($"Daily P/L:          +${dailyPL:N2} (+{dailyPLPercent:N2}%)");
    }
    else
    {
        LogError($"Daily P/L:          -${Math.Abs(dailyPL):N2} ({dailyPLPercent:N2}%)");
    }
    
    // Show discrepancy if any
    if (Math.Abs(discrepancy) > 0.01m)
    {
        Log($"  Net from Fills:   ${fillBasedPL:N2}");
        Log($"  Discrepancy:      ${discrepancy:N2} (orders from other bot instances or before BotId tagging)");
    }
    
    // Total P/L
    if (totalPL >= 0)
    {
        LogSuccess($"Total P/L:          +${totalPL:N2} (+{totalPLPercent:N2}%)");
    }
    else
    {
        LogError($"Total P/L:          -${Math.Abs(totalPL):N2} ({totalPLPercent:N2}%)");
    }
}

// ============================================================================
// TRADING BOT
// ============================================================================
async Task RunTradingBotAsync(CommandLineOverrides cmdOverrides, CancellationToken cancellationToken = default)
{
    Log("=== QQQ Trading Bot Starting ===\n");

    // Load configuration
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddUserSecrets<Program>()
        .Build();

    // Load API credentials
    var apiKey = configuration["Alpaca:ApiKey"];
    var apiSecret = configuration["Alpaca:ApiSecret"];

    if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
    {
        LogError("API credentials not found. Please run with --setup flag first.");
        LogError("Usage: dotnet run --setup");
        return;
    }

    // CRITICAL: Runtime safety check
    if (!apiKey.StartsWith(PAPER_KEY_PREFIX, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "SAFETY VIOLATION: Live trading keys detected! " +
            "This bot is designed for PAPER TRADING ONLY. " +
            "Please reconfigure with paper trading keys (starting with 'PK').");
    }

    // Load trading settings from config file
    var configSettings = new TradingSettings
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
        // Low-latency mode settings
        LowLatencyMode = configuration.GetValue("TradingBot:LowLatencyMode", false),
        UseIocOrders = configuration.GetValue("TradingBot:UseIocOrders", false),
        IocLimitOffsetCents = configuration.GetValue("TradingBot:IocLimitOffsetCents", 1m),
        IocMaxRetries = configuration.GetValue("TradingBot:IocMaxRetries", 5),
        IocRetryStepCents = configuration.GetValue("TradingBot:IocRetryStepCents", 1m),
        IocMaxDeviationPercent = configuration.GetValue("TradingBot:IocMaxDeviationPercent", 0.005m),
        KeepAlivePingSeconds = configuration.GetValue("TradingBot:KeepAlivePingSeconds", 5),
        WarmUpIterations = configuration.GetValue("TradingBot:WarmUpIterations", 10000)
    };
    
    // Create effective settings (may be modified by command line overrides)
    // Determine whether to use BTC/USD for early trading.
    // Use the config setting by default, but allow CLI to override when provided.
    var useBtcEarlyTrading = cmdOverrides.HasOverrides ? cmdOverrides.UseBtcEarlyTrading : configSettings.UseBtcEarlyTrading;
    
    var settings = new TradingSettings
    {
        BotId = cmdOverrides.BotIdOverride ?? configSettings.BotId,
        PollingIntervalSeconds = configSettings.PollingIntervalSeconds,
        BullSymbol = cmdOverrides.BullTicker ?? configSettings.BullSymbol,
        BearSymbol = cmdOverrides.BullOnlyMode ? null : (cmdOverrides.BearTicker ?? configSettings.BearSymbol),
        BenchmarkSymbol = cmdOverrides.BenchmarkTicker ?? configSettings.BenchmarkSymbol,
        CryptoBenchmarkSymbol = configSettings.CryptoBenchmarkSymbol,
        SMAWindowSeconds = configSettings.SMAWindowSeconds,
        ChopThresholdPercent = configSettings.ChopThresholdPercent,
        MinChopAbsolute = cmdOverrides.MinChopAbsoluteOverride ?? configSettings.MinChopAbsolute,
        SlidingBand = configSettings.SlidingBand,
        SlidingBandFactor = configSettings.SlidingBandFactor,
        NeutralWaitSeconds = cmdOverrides.NeutralWaitSecondsOverride ?? configSettings.NeutralWaitSeconds,
        StartingAmount = configSettings.StartingAmount,
        BullOnlyMode = cmdOverrides.BullOnlyMode,
        UseBtcEarlyTrading = useBtcEarlyTrading,
        WatchBtc = cmdOverrides.WatchBtc || configSettings.WatchBtc,
        MonitorSlippage = cmdOverrides.MonitorSlippage || configSettings.MonitorSlippage,
        TrailingStopPercent = cmdOverrides.TrailingStopPercentOverride ?? configSettings.TrailingStopPercent,
        StopLossCooldownSeconds = configSettings.StopLossCooldownSeconds,
        UseMarketableLimits = cmdOverrides.UseMarketableLimits || configSettings.UseMarketableLimits,
        MaxSlippagePercent = cmdOverrides.MaxSlippagePercentOverride ?? configSettings.MaxSlippagePercent,
        MaxChaseDeviationPercent = configSettings.MaxChaseDeviationPercent,
        // Low-latency mode settings (pass through from config)
        LowLatencyMode = cmdOverrides.LowLatencyMode || configSettings.LowLatencyMode,
        UseIocOrders = cmdOverrides.UseIocOrders || configSettings.UseIocOrders,
        IocLimitOffsetCents = configSettings.IocLimitOffsetCents,
        IocMaxRetries = configSettings.IocMaxRetries,
        IocRetryStepCents = configSettings.IocRetryStepCents,
        IocMaxDeviationPercent = configSettings.IocMaxDeviationPercent,
        KeepAlivePingSeconds = configSettings.KeepAlivePingSeconds,
        WarmUpIterations = configSettings.WarmUpIterations
    };

    // Initialize Alpaca clients (Paper Trading) - needed for ticker validation
    var secretKey = new SecretKey(apiKey, apiSecret);
    
    using var tradingClient = Environments.Paper.GetAlpacaTradingClient(secretKey);
    using var dataClient = Environments.Paper.GetAlpacaDataClient(secretKey);
    using var cryptoDataClient = Environments.Paper.GetAlpacaCryptoDataClient(secretKey);
    
    // Initialize Broker-Agnostic Adapters
    IBrokerExecution broker = new AlpacaExecutionAdapter(tradingClient, dataClient);
    
    // Initialize streaming clients (only needed for low-latency mode)
    IAlpacaDataStreamingClient? stockStreamClient = null;
    IAlpacaCryptoStreamingClient? cryptoStreamClient = null;
    
    // Choose data source based on mode:
    // - LowLatencyMode: Real-time streaming via AlpacaSourceAdapter
    // - Standard Mode: HTTP polling via PollingDataSource (still feeds the unified pipeline)
    IMarketDataSource marketSource;
    if (settings.LowLatencyMode)
    {
        stockStreamClient = Environments.Paper.GetAlpacaDataStreamingClient(secretKey);
        cryptoStreamClient = Environments.Paper.GetAlpacaCryptoStreamingClient(secretKey);
        marketSource = new AlpacaSourceAdapter(stockStreamClient, cryptoStreamClient, dataClient);
        Log("[MODE] Low-Latency Streaming enabled");
    }
    else
    {
        marketSource = new PollingDataSource(broker, TimeSpan.FromSeconds(settings.PollingIntervalSeconds), msg => Log(msg));
        Log($"[MODE] Standard Polling enabled ({settings.PollingIntervalSeconds}s interval)");
    }
    
    // Track average ping for dynamic IOC poll delay (2x avg ping)
    const int PingSampleCount = 10;
    var _pingSamples = new Queue<long>(PingSampleCount);
    var _pingSamplesLock = new object();
    long _avgPingMs = IocMachineGunExecutor.DefaultPollDelayMs; // Start with default
    
    int GetIocPollDelayMs()
    {
        lock (_pingSamplesLock)
        {
            // Return 2x average ping, with floor of default and ceiling of 200ms
            return (int)Math.Clamp(_avgPingMs * 2, IocMachineGunExecutor.DefaultPollDelayMs, 200);
        }
    }
    
    // IocMachineGunExecutor for IOC order execution (uses 2x avg ping for poll delay)
    var iocExecutor = new IocMachineGunExecutor(
        broker,
        () => settings.GenerateClientOrderId(),
        msg => Log(msg),
        GetIocPollDelayMs);
    
    // Slippage monitoring state (thread-safe)
    var slippageLock = new object();
    decimal _cumulativeSlippage = 0m;
    string? _slippageLogFile = null;
    
    // Initialize slippage log file if monitoring enabled
    if (settings.MonitorSlippage)
    {
        var bearSymbol = settings.BearSymbol ?? "CASH";
        _slippageLogFile = Path.Combine(AppContext.BaseDirectory, 
            $"qqqBot-slippage-log-{settings.BullSymbol}-{bearSymbol}-{DateTime.UtcNow:yyyyMMdd}.csv");
        
        // Write header if file doesn't exist
        if (!File.Exists(_slippageLogFile))
        {
            File.WriteAllText(_slippageLogFile, "Timestamp,Symbol,Side,Quantity,QuotePrice,FillPrice,Slippage,Favor" + Environment.NewLine);
        }
        Log($"  Slippage Monitoring: Enabled → {Path.GetFileName(_slippageLogFile)}");
    }

    // Validate command line override tickers before any trades
    if (cmdOverrides.HasOverrides)
    {
        Log("Command line overrides detected. Validating tickers...");
        
        var tickersToValidate = new List<string>();
        if (!string.IsNullOrEmpty(cmdOverrides.BullTicker))
            tickersToValidate.Add(cmdOverrides.BullTicker);
        if (!string.IsNullOrEmpty(cmdOverrides.BearTicker))
            tickersToValidate.Add(cmdOverrides.BearTicker);
        if (!string.IsNullOrEmpty(cmdOverrides.BenchmarkTicker) && 
            cmdOverrides.BenchmarkTicker != cmdOverrides.BullTicker)
            tickersToValidate.Add(cmdOverrides.BenchmarkTicker);
        
        foreach (var ticker in tickersToValidate.Distinct())
        {
            if (!await broker.ValidateSymbolAsync(ticker))
            {
                LogError($"[Error] Invalid Ticker: {ticker}");
                LogError("Please verify the ticker symbol and try again.");
                return;
            }
            LogSuccess($"  Validated: {ticker} (tradable)");
        }
        
        // Log explicit override summary
        Log("");
        LogSuccess($"Running with Overrides: Bull={settings.BullSymbol}, Bear={settings.BearSymbol ?? "(none)"}, Bench={settings.BenchmarkSymbol}");
        Log("");
    }

    // Load or initialize trading state
    var stateFilePath = Path.Combine(AppContext.BaseDirectory, "trading_state.json");
    var tradingState = LoadTradingState(stateFilePath);
    
    // Clear trailing stop state on restart - it should be recalculated fresh
    // This prevents stale stop levels from triggering unexpected exits
    tradingState.HighWaterMark = null;
    tradingState.LowWaterMark = null;
    tradingState.TrailingStopValue = null;
    
    // Check for symbol mismatch between saved state and current settings
    if (tradingState.Metadata != null)
    {
        var symbolsMatch = 
            tradingState.Metadata.SymbolBull == settings.BullSymbol &&
            tradingState.Metadata.SymbolBear == settings.BearSymbol &&
            tradingState.Metadata.SymbolIndex == settings.BenchmarkSymbol;
        
        if (!symbolsMatch)
        {
            Log("⚠️  Config mismatch: Discarding old state");
            Log($"   Previous: Bull={tradingState.Metadata.SymbolBull}, Bear={tradingState.Metadata.SymbolBear}, Bench={tradingState.Metadata.SymbolIndex}");
            Log($"   Current:  Bull={settings.BullSymbol}, Bear={settings.BearSymbol ?? "(none)"}, Bench={settings.BenchmarkSymbol}");
            
            // Reset to neutral/cash state but preserve financial tracking
            tradingState.CurrentPosition = null;
            tradingState.CurrentShares = 0;
            
            // Update metadata to reflect new symbols
            tradingState.Metadata = new TradingStateMetadata
            {
                SymbolBull = settings.BullSymbol,
                SymbolBear = settings.BearSymbol,
                SymbolIndex = settings.BenchmarkSymbol
            };
            SaveTradingState(stateFilePath, tradingState);
            Log("   State reset to Neutral/Cash. SMA will be re-seeded.\n");
        }
    }
    else
    {
        // First time or missing metadata - initialize it
        tradingState.Metadata = new TradingStateMetadata
        {
            SymbolBull = settings.BullSymbol,
            SymbolBear = settings.BearSymbol,
            SymbolIndex = settings.BenchmarkSymbol
        };
        SaveTradingState(stateFilePath, tradingState);
    }
    
    // Initialize state if first run
    if (!tradingState.IsInitialized)
    {
        tradingState.AvailableCash = settings.StartingAmount;
        tradingState.AccumulatedLeftover = 0m;
        tradingState.StartingAmount = settings.StartingAmount; // Track for P/L calculation
        tradingState.IsInitialized = true;
        SaveTradingState(stateFilePath, tradingState);
        Log($"Initialized trading with starting amount: ${settings.StartingAmount:N2}");
    }
    
    // Migrate: If StartingAmount is missing from old state file, use config value
    if (tradingState.StartingAmount == 0m)
    {
        tradingState.StartingAmount = settings.StartingAmount;
        SaveTradingState(stateFilePath, tradingState);
        Log($"Migrated StartingAmount from config: ${settings.StartingAmount:N2}");
    }

    // If command line overrides are active, liquidate configured positions first
    if (cmdOverrides.HasOverrides)
    {
        Log("Command line overrides active. Checking for configured positions to liquidate...");
        await LiquidateConfiguredPositionsAsync(configSettings, tradingState, broker, iocExecutor, stateFilePath);
        Log("");
    }

    Log($"Configuration loaded:");
    Log($"  Bot ID: {settings.BotId}");
    Log($"  Benchmark: {settings.BenchmarkSymbol}");
    if (settings.UseBtcEarlyTrading)
    {
        Log($"  Crypto Benchmark (early trading): {settings.CryptoBenchmarkSymbol}");
    }
    else
    {
        Log($"  Early Trading: Using {settings.BenchmarkSymbol} (BTC/USD disabled)");
    }
    Log($"  Bull ETF: {settings.BullSymbol}");
    if (settings.BullOnlyMode)
    {
        Log($"  Bear ETF: (disabled - bull-only mode)");
        Log($"  Mode: Bull-only (neutral/bear signals dump to cash)");
    }
    else
    {
        Log($"  Bear ETF: {settings.BearSymbol}");
    }
    Log($"  SMA Window: {settings.SMAWindowSeconds}s | Heartbeat: {settings.PollingIntervalSeconds}s | Queue Size: {settings.SMALength}");
    Log($"  Chop Threshold: {settings.ChopThresholdPercent * 100:N3}%");
    Log($"  Min Chop Absolute: ${settings.MinChopAbsolute:N4}");
    if (settings.SlidingBand)
    {
        Log($"  Sliding Band: Enabled (factor: {settings.SlidingBandFactor:N2})");
    }
    else
    {
        Log($"  Sliding Band: Disabled");
    }
    Log($"  Neutral Wait: {(settings.NeutralWaitSeconds < 0 ? "Hold-Through (no liquidate)" : $"{settings.NeutralWaitSeconds}s")}");
    Log($"  BTC Correlation (Neutral Nudge): {(settings.WatchBtc ? "Enabled" : "Disabled")}");
    Log($"  Starting Amount: ${tradingState.StartingAmount:N2}");
    Log($"  Current Available Cash: ${tradingState.AvailableCash:N2}");
    Log($"  Accumulated Leftover: ${tradingState.AccumulatedLeftover:N2}");
    if (tradingState.CurrentPosition != null)
    {
        Log($"  Current Position: {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}");
    }
    
    // Show current balance with P/L on startup
    var startupBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
    var startupPL = startupBalance - tradingState.StartingAmount;
    Log($"");
    LogBalanceWithPL(startupBalance, startupPL);
    Log($"");

    // Verify connection
    try
    {
        var account = await tradingClient.GetAccountAsync();
        LogSuccess($"Connected to Alpaca Paper Trading");
        Log($"  Account ID: {account.AccountId}");
        Log($"  Buying Power: ${account.BuyingPower:N2}");
        Log($"  Portfolio Value: ${account.Equity:N2}\n");
    }
    catch (Exception ex)
    {
        LogError($"Failed to connect to Alpaca: {ex.Message}");
        return;
    }
    
    // Startup check: Verify local state matches Alpaca positions
    if (!string.IsNullOrEmpty(tradingState.CurrentPosition) && tradingState.CurrentShares > 0)
    {
        try
        {
            var positions = await tradingClient.ListPositionsAsync();
            var matchingPosition = positions.FirstOrDefault(p => 
                p.Symbol.Equals(tradingState.CurrentPosition, StringComparison.OrdinalIgnoreCase));
            
            if (matchingPosition == null)
            {
                LogError($"⚠️  STATE MISMATCH: Local state shows {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}");
                LogError($"   but Alpaca has NO position for this symbol.");
                LogError($"   This may happen if a previous shutdown failed to liquidate.");
                LogError($"   Clearing local position state and continuing with available cash.");
                
                // Clear position tracking but keep cash tracking
                tradingState.CurrentPosition = null;
                tradingState.CurrentShares = 0;
                SaveTradingState(stateFilePath, tradingState);
            }
            else if (matchingPosition.Quantity < tradingState.CurrentShares)
            {
                LogError($"⚠️  STATE MISMATCH: Local state shows {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}");
                LogError($"   but Alpaca only has {matchingPosition.Quantity} shares.");
                LogError($"   Adjusting local state to match Alpaca.");
                
                tradingState.CurrentShares = (long)matchingPosition.Quantity;
                SaveTradingState(stateFilePath, tradingState);
            }
        }
        catch (Exception ex)
        {
            LogError($"Warning: Could not verify position state: {ex.Message}");
            Log("Continuing with local state...");
        }
    }

    // Eastern Time Zone
    var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    // =========================================================================
    // LOW-LATENCY MODE INFRASTRUCTURE (must be declared before stream handlers)
    // =========================================================================
    
    // High-performance bounded channel for trade prices (DropOldest prevents lag)
    var tradeChannel = Channel.CreateBounded<TradeTick>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false // Multiple streams may write
    });
    
    // Flag to signal when low-latency pipeline is active (enables channel writes in stream handlers)
    // Use Volatile.Read/Write for thread-safe access from stream handler callbacks
    bool _lowLatencyActive = false;
    
    // Disk I/O throttle for trailing stop persistence (avoid hot path blocking)
    DateTime _lastTrailingStopDiskWrite = DateTime.MinValue;
    const int TRAILING_STOP_DISK_WRITE_INTERVAL_SECONDS = 5;
    
    // O(1) SMA calculator for low-latency mode
    var incrementalSma = new IncrementalSma(settings.SMALength);
    var incrementalBtcSma = new IncrementalSma(settings.SMALength);

    // =========================================================================
    // STREAMING DATA INITIALIZATION
    // =========================================================================
    Log("Initializing real-time data streams...");
    
    // Wire up error handler for streaming adapter
    marketSource.OnError += (ex) => LogError($"[STREAM] Error: {ex.Message}");
    marketSource.ConnectionStateChanged += (connected) => 
    {
        if (connected)
            Log("[STREAM] Connection established");
        else
            Log("[STREAM] Connection lost");
    };
    
    // Helper: Check if it's time to start streams (2 minutes before market open)
    bool ShouldStartStreams(DateTime easternTime)
    {
        if (easternTime.DayOfWeek == DayOfWeek.Saturday || easternTime.DayOfWeek == DayOfWeek.Sunday)
            return false;
        
        // Start streams at 9:28 AM ET (2 minutes before market open)
        var streamStartTime = new TimeSpan(9, 28, 0);
        var marketClose = new TimeSpan(16, 0, 0);
        var currentTime = easternTime.TimeOfDay;
        
        return currentTime >= streamStartTime && currentTime < marketClose;
    }
    
    // =========================================================================
    // LOW-LATENCY MODE: Connection Warming & Keep-Alive
    // =========================================================================
    
    // Track warm-up state
    bool _warmedUp = false;
    CancellationTokenSource? _keepAliveCts = null;
    Task? _keepAliveTask = null;
    
    // JIT warm-up: Force .NET to compile hot-path code before real money is on the line
    void WarmUpStrategyLogic(int iterations)
    {
        Log($"[WARM-UP] JIT compiling strategy logic ({iterations} iterations)...");
        var stopwatch = Stopwatch.StartNew();
        
        // Create a temporary IncrementalSma for warm-up
        var warmUpSma = new IncrementalSma(settings.SMALength);
        var random = new Random(42); // Deterministic seed for reproducibility
        
        // Simulate price movements and signal calculations
        for (int i = 0; i < iterations; i++)
        {
            // Generate dummy price in realistic range
            decimal dummyPrice = 500m + (decimal)(random.NextDouble() * 10 - 5);
            
            // Force SMA calculation (hot path)
            var sma = warmUpSma.Add(dummyPrice);
            
            // Force signal calculation (hot path)
            var upperBand = sma * (1 + settings.ChopThresholdPercent);
            var lowerBand = sma * (1 - settings.ChopThresholdPercent);
            string signal = dummyPrice > upperBand ? "BULL" : dummyPrice < lowerBand ? "BEAR" : "NEUTRAL";
            
            // Prevent dead code elimination
            if (signal == "INVALID") throw new InvalidOperationException();
        }
        
        stopwatch.Stop();
        Log($"[WARM-UP] JIT compilation complete ({stopwatch.ElapsedMilliseconds}ms)");
    }
    
    // Connection warm-up: Prime HTTP connection pool to avoid cold-start latency
    async Task WarmUpConnectionsAsync()
    {
        Log("[WARM-UP] Priming HTTP connection pool...");
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // Fire several requests to establish and warm up connections
            // GetClockAsync is lightweight and safe to call repeatedly
            for (int i = 0; i < 5; i++)
            {
                var clock = await tradingClient.GetClockAsync();
                await Task.Delay(100); // Brief pause between requests
            }
            
            // Also warm up data client with a simple request
            try
            {
                var latestTrade = await dataClient.GetLatestTradeAsync(new LatestMarketDataRequest(settings.BenchmarkSymbol) { Feed = MarketDataFeed.Iex });
            }
            catch { /* Ignore - may fail if market closed */ }
            
            stopwatch.Stop();
            Log($"[WARM-UP] Connection pool primed ({stopwatch.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            Log($"[WARM-UP] Connection warming failed: {ex.Message}");
        }
    }
    
    // Keep-alive pinger: Maintain hot connections to Alpaca API
    void StartKeepAlivePinger()
    {
        if (_keepAliveTask != null) return; // Already running
        
        _keepAliveCts = new CancellationTokenSource();
        var pingInterval = TimeSpan.FromSeconds(settings.KeepAlivePingSeconds);
        
        _keepAliveTask = Task.Run(async () =>
        {
            Log($"[KEEP-ALIVE] Started (ping every {settings.KeepAlivePingSeconds}s)");
            
            while (!_keepAliveCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pingInterval, _keepAliveCts.Token);
                    
                    // Lightweight ping to keep SSL/TCP connection warm
                    var sw = Stopwatch.StartNew();
                    await tradingClient.GetClockAsync();
                    sw.Stop();
                    
                    // Track rolling average ping for IOC poll delay
                    lock (_pingSamplesLock)
                    {
                        if (_pingSamples.Count >= PingSampleCount)
                            _pingSamples.Dequeue();
                        _pingSamples.Enqueue(sw.ElapsedMilliseconds);
                        _avgPingMs = (long)_pingSamples.Average();
                    }
                    
                    // Only log if latency is concerning (>50ms)
                    if (sw.ElapsedMilliseconds > 50)
                    {
                        Log($"[KEEP-ALIVE] Ping: {sw.ElapsedMilliseconds}ms (elevated, avg: {_avgPingMs}ms)");
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Normal shutdown
                }
                catch (Exception ex)
                {
                    // Connection may have dropped - will be re-established on next ping
                    Log($"[KEEP-ALIVE] Ping failed: {ex.Message}");
                }
            }
            
            Log("[KEEP-ALIVE] Stopped");
        }, _keepAliveCts.Token);
    }
    
    void StopKeepAlivePinger()
    {
        if (_keepAliveCts != null)
        {
            _keepAliveCts.Cancel();
            _keepAliveCts.Dispose();
            _keepAliveCts = null;
        }
        _keepAliveTask = null;
    }

    Log("=== Trading Bot Active ===\n");

    // Track last market closed notification to avoid spam
    DateTime? lastMarketClosedLog = null;
    const int marketClosedLogIntervalMinutes = 30;
    
    // Virtual trailing stop state (for protecting profits during intra-trend pullbacks)
    // Works for both BULL (tracks high water mark) and BEAR (tracks low water mark)
    decimal _highWaterMark = 0m;     // Highest benchmark price since BULL entry
    decimal _lowWaterMark = 0m;      // Lowest benchmark price since BEAR entry
    decimal _virtualStopPrice = 0m;  // Stop trigger level
    bool _isStoppedOut = false;       // Washout latch engaged
    string _stoppedOutDirection = ""; // "BULL" or "BEAR" - which direction was stopped out
    decimal _washoutLevel = 0m;      // Price required to re-enter after stop-out
    DateTime? _stopoutTime = null;   // Time of stop-out (for cooldown)
    
    // Slippage callback for passing to helper functions
    Action<decimal>? slippageCallback = settings.MonitorSlippage 
        ? (delta) => { _cumulativeSlippage += delta; }
        : null;

    // =========================================================================
    // UNIFIED REACTIVE PIPELINE - Consumes ticks from any data source
    // =========================================================================
    // This is the ONE loop that processes all market data, whether from:
    // - AlpacaSourceAdapter (real-time streaming in LowLatencyMode)
    // - PollingDataSource (HTTP polling converted to channel ticks)
    async Task RunReactivePipelineAsync(CancellationToken ct)
    {
        Log("\n[PIPELINE] Starting unified reactive pipeline...");
        Log("[PIPELINE] Processing trades as they arrive.\n");
        
        // Local state for the pipeline
        DateTime? pipelineNeutralDetectionTime = null;
        DateTime pipelineLastLogTime = DateTime.MinValue;
        string pipelineLastSignal = string.Empty;
        
        // Track latest prices for cross-stream correlation (BTC nudge)
        decimal pipelineLatestBenchmarkPrice = 0m;
        decimal pipelineLatestBtcPrice = 0m;
        
        // Track last day for day start balance calculation
        string pipelineLastDayStr = tradingState.DayStartDate ?? string.Empty;
        
        // Track market closed logging
        DateTime? pipelineMarketClosedLog = null;
        
        // RESTORE trailing stop state from disk (survives restarts)
        decimal pipelineHwm = tradingState.HighWaterMark ?? 0m;
        decimal pipelineLwm = tradingState.LowWaterMark ?? 0m;
        decimal pipelineVirtualStop = tradingState.TrailingStopValue ?? 0m;
        bool pipelineStoppedOut = tradingState.IsStoppedOut;
        string pipelineStoppedOutDir = tradingState.StoppedOutDirection ?? string.Empty;
        decimal pipelineWashoutLevel = tradingState.WashoutLevel ?? 0m;
        DateTime? pipelineStopoutTime = string.IsNullOrEmpty(tradingState.StopoutTimestamp) 
            ? null 
            : DateTime.TryParse(tradingState.StopoutTimestamp, out var ts) ? ts : (DateTime?)null;
        
        // Sliding band: Track benchmark high (for BULL) and low (for BEAR)
        // These reset when position changes
        decimal pipelineSlidingBandHigh = 0m;
        decimal pipelineSlidingBandLow = 0m;
        
        // Log restored state if any
        if (pipelineHwm > 0 || pipelineLwm > 0)
        {
            Log($"[PIPELINE] Restored trailing stop state from disk:");
            if (pipelineHwm > 0) Log($"    HighWaterMark: ${pipelineHwm:N2}, Stop: ${pipelineVirtualStop:N2}");
            if (pipelineLwm > 0) Log($"    LowWaterMark: ${pipelineLwm:N2}, Stop: ${pipelineVirtualStop:N2}");
            if (pipelineStoppedOut) Log($"    StoppedOut: {pipelineStoppedOutDir}, Washout: ${pipelineWashoutLevel:N2}");
        }
        
        // Also sync to memory variables for other parts of code
        _highWaterMark = pipelineHwm;
        _lowWaterMark = pipelineLwm;
        _virtualStopPrice = pipelineVirtualStop;
        _isStoppedOut = pipelineStoppedOut;
        _stoppedOutDirection = pipelineStoppedOutDir;
        _washoutLevel = pipelineWashoutLevel;
        _stopoutTime = pipelineStopoutTime;
        
        try
        {
            await foreach (var tick in tradeChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var easternNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
                    
                    // Handle market closed state (log periodically but don't process signals)
                    if (!IsMarketOpen(easternNow))
                    {
                        // Only log market closed message every 30 minutes
                        if (pipelineMarketClosedLog == null || 
                            (DateTime.Now - pipelineMarketClosedLog.Value).TotalMinutes >= marketClosedLogIntervalMinutes)
                        {
                            Log($"Market Closed. Waiting for open... (ET: {easternNow:HH:mm:ss})");
                            pipelineMarketClosedLog = DateTime.Now;
                        }
                        continue;
                    }
                    
                    // Reset market closed tracker when market opens
                    pipelineMarketClosedLog = null;
                    
                    // Track day start balance for daily P/L calculation
                    var todayDateStr = easternNow.ToString("yyyy-MM-dd");
                    if (pipelineLastDayStr != todayDateStr)
                    {
                        var currentBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
                        tradingState.DayStartBalance = currentBalance;
                        tradingState.DayStartDate = todayDateStr;
                        pipelineLastDayStr = todayDateStr;
                        SaveTradingState(stateFilePath, tradingState);
                        Log($"New trading day detected. Day start balance: ${currentBalance:N2}");
                        
                        // Reset sliding band highs/lows for new trading day
                        pipelineSlidingBandHigh = 0m;
                        pipelineSlidingBandLow = 0m;
                    }
                    
                    // For early trading, only process BTC ticks; otherwise only benchmark ticks
                    var earlyTradingEnd = new TimeSpan(9, 55, 0);
                    var usesCrypto = settings.UseBtcEarlyTrading && easternNow.TimeOfDay < earlyTradingEnd;
                    
                    // Track latest prices for each stream (even if we don't process this tick for signals)
                    if (tick.IsBenchmark)
                        pipelineLatestBenchmarkPrice = tick.Price;
                    else
                        pipelineLatestBtcPrice = tick.Price;
                    
                    // Skip processing if this isn't the primary tick for current mode
                    if (usesCrypto && tick.IsBenchmark) continue;  // Skip QQQ during early trading
                    if (!usesCrypto && !tick.IsBenchmark) continue; // Skip BTC during normal trading
                    
                    var currentPrice = tick.Price;
                    
                    // O(1) SMA calculation - the key optimization!
                    decimal currentSma;
                    if (tick.IsBenchmark)
                    {
                        currentSma = incrementalSma.Add(currentPrice);
                    }
                    else
                    {
                        currentSma = incrementalBtcSma.Add(currentPrice);
                    }
                    
                    // Calculate Hysteresis Bands
                    // Standard: band center = SMA, bands = [SMA - width, SMA + width]
                    // When SlidingBand is enabled with factor (0 < factor < 1):
                    // - In BULL: track benchmark high, exit when price < (high - width * factor)
                    // - In BEAR: track benchmark low, exit when price > (low + width * factor)
                    // - In Neutral/Cash: standard SMA-based bands
                    
                    // First calculate standard width based on SMA
                    decimal percentageWidth = currentSma * settings.ChopThresholdPercent;
                    decimal effectiveWidth = Math.Max(percentageWidth, settings.MinChopAbsolute);
                    
                    decimal upperBand;
                    decimal lowerBand;
                    decimal bandCenter; // For logging
                    
                    if (settings.SlidingBand && tick.IsBenchmark)
                    {
                        bool inBull = tradingState.CurrentPosition == settings.BullSymbol && tradingState.CurrentShares > 0;
                        bool inBear = tradingState.CurrentPosition == settings.BearSymbol && tradingState.CurrentShares > 0;
                        
                        if (inBull)
                        {
                            // Track benchmark high - band slides up with price
                            if (currentPrice > pipelineSlidingBandHigh || pipelineSlidingBandHigh == 0m)
                            {
                                pipelineSlidingBandHigh = currentPrice;
                            }
                            // BULL exits when price falls below (high - width * factor)
                            // Upper band is high + width*factor (needs to exceed to get stronger BULL)
                            bandCenter = pipelineSlidingBandHigh;
                            upperBand = pipelineSlidingBandHigh + (effectiveWidth * settings.SlidingBandFactor);
                            lowerBand = pipelineSlidingBandHigh - (effectiveWidth * settings.SlidingBandFactor);
                        }
                        else if (inBear)
                        {
                            // Track benchmark low - band slides down with price
                            if (currentPrice < pipelineSlidingBandLow || pipelineSlidingBandLow == 0m)
                            {
                                pipelineSlidingBandLow = currentPrice;
                            }
                            // BEAR exits when price rises above (low + width * factor)
                            bandCenter = pipelineSlidingBandLow;
                            upperBand = pipelineSlidingBandLow + (effectiveWidth * settings.SlidingBandFactor);
                            lowerBand = pipelineSlidingBandLow - (effectiveWidth * settings.SlidingBandFactor);
                        }
                        else
                        {
                            // Neutral/cash - use standard SMA bands, reset sliding values
                            bandCenter = currentSma;
                            upperBand = currentSma + effectiveWidth;
                            lowerBand = currentSma - effectiveWidth;
                            pipelineSlidingBandHigh = 0m;
                            pipelineSlidingBandLow = 0m;
                        }
                    }
                    else
                    {
                        // Standard behavior
                        bandCenter = currentSma;
                        upperBand = currentSma + effectiveWidth;
                        lowerBand = currentSma - effectiveWidth;
                    }
                    
                    // Determine signal
                    string signal;
                    var timeOfDay = easternNow.TimeOfDay;
                    var marketCloseCutoff = new TimeSpan(15, 58, 0);
                    
                    if (timeOfDay >= marketCloseCutoff)
                    {
                        signal = "MARKET_CLOSE";
                    }
                    else if (currentPrice > upperBand)
                    {
                        signal = "BULL";
                    }
                    else if (currentPrice < lowerBand)
                    {
                        signal = "BEAR";
                    }
                    else
                    {
                        signal = "NEUTRAL";
                    }
                    
                    // Apply BTC nudge if in neutral and WatchBtc is enabled
                    string finalSignal = signal;
                    if (settings.WatchBtc && signal == "NEUTRAL" && incrementalBtcSma.IsFull)
                    {
                        var btcSma = incrementalBtcSma.CurrentAverage;
                        var btcUpperBand = btcSma * (1 + settings.ChopThresholdPercent);
                        var btcLowerBand = btcSma * (1 - settings.ChopThresholdPercent);
                        
                        if (pipelineLatestBtcPrice > btcUpperBand)
                            finalSignal = "BULL";
                        else if (pipelineLatestBtcPrice < btcLowerBand)
                            finalSignal = "BEAR";
                    }
                    
                    // Trailing stop logic (update water marks)
                    if (settings.TrailingStopPercent > 0)
                    {
                        if (tradingState.CurrentPosition == settings.BullSymbol && tradingState.CurrentShares > 0)
                        {
                            if (currentPrice > pipelineHwm || pipelineHwm == 0m)
                            {
                                pipelineHwm = currentPrice;
                                pipelineVirtualStop = pipelineHwm * (1 - settings.TrailingStopPercent);
                            }
                            
                            // Check for stop trigger
                            if (pipelineVirtualStop > 0 && currentPrice <= pipelineVirtualStop && !pipelineStoppedOut)
                            {
                                pipelineStoppedOut = true;
                                pipelineStoppedOutDir = "BULL";
                                pipelineStopoutTime = DateTime.UtcNow;
                                pipelineWashoutLevel = upperBand;
                                Log($"[TRAILING STOP] BULL stop triggered @ ${currentPrice:N2} (stop was ${pipelineVirtualStop:N2})");
                                finalSignal = "NEUTRAL"; // Force exit
                            }
                        }
                        else if (!string.IsNullOrEmpty(settings.BearSymbol) && 
                                 tradingState.CurrentPosition == settings.BearSymbol && tradingState.CurrentShares > 0)
                        {
                            if (currentPrice < pipelineLwm || pipelineLwm == 0m)
                            {
                                pipelineLwm = currentPrice;
                                pipelineVirtualStop = pipelineLwm * (1 + settings.TrailingStopPercent);
                            }
                            
                            // Check for stop trigger (price rising)
                            if (pipelineVirtualStop > 0 && currentPrice >= pipelineVirtualStop && !pipelineStoppedOut)
                            {
                                pipelineStoppedOut = true;
                                pipelineStoppedOutDir = "BEAR";
                                pipelineStopoutTime = DateTime.UtcNow;
                                pipelineWashoutLevel = lowerBand;
                                Log($"[TRAILING STOP] BEAR stop triggered @ ${currentPrice:N2} (stop was ${pipelineVirtualStop:N2})");
                                finalSignal = "NEUTRAL"; // Force exit
                            }
                        }
                    }
                    
                    // Check washout latch
                    bool latchBlocksEntry = false;
                    if (pipelineStoppedOut && pipelineStopoutTime.HasValue)
                    {
                        var elapsed = (DateTime.UtcNow - pipelineStopoutTime.Value).TotalSeconds;
                        if (elapsed < settings.StopLossCooldownSeconds)
                        {
                            latchBlocksEntry = true;
                        }
                        else if ((pipelineStoppedOutDir == "BULL" && currentPrice > pipelineWashoutLevel) ||
                                 (pipelineStoppedOutDir == "BEAR" && currentPrice < pipelineWashoutLevel))
                        {
                            // Price recovered above washout level - clear latch
                            pipelineStoppedOut = false;
                            pipelineHwm = 0m;
                            pipelineLwm = 0m;
                            pipelineVirtualStop = 0m;
                            Log($"[LATCH CLEAR] Price recovered to ${currentPrice:N2}. Re-entry allowed.");
                        }
                        else
                        {
                            latchBlocksEntry = true;
                        }
                    }
                    
                    // Throttle logging (once per second)
                    var shouldLog = (DateTime.Now - pipelineLastLogTime).TotalSeconds >= 1;
                    if (shouldLog)
                    {
                        var signalChanged = finalSignal != pipelineLastSignal;
                        pipelineLastLogTime = DateTime.Now;
                        pipelineLastSignal = finalSignal;
                        
                        if (signalChanged || (DateTime.Now - pipelineLastLogTime).TotalSeconds >= 5)
                        {
                            var benchLabel = tick.IsBenchmark ? settings.BenchmarkSymbol : settings.CryptoBenchmarkSymbol;
                            Log($"[{easternNow:HH:mm:ss}] {benchLabel}: ${currentPrice:N2} | SMA: ${currentSma:N2} | Band: [${lowerBand:N2}-${upperBand:N2}] | Signal: {finalSignal}");
                        }
                    }
                    
                    // Create signal checker for smart exit chaser
                    Func<decimal, string> signalChecker = (price) => {
                        if (price > upperBand) return "BULL";
                        if (price < lowerBand) return "BEAR";
                        return "NEUTRAL";
                    };
                    
                    // Create price getter
                    Func<decimal> priceGetter = () => {
                        return pipelineLatestBenchmarkPrice > 0 ? pipelineLatestBenchmarkPrice : currentPrice;
                    };
                    
                    // Execute trading logic based on signal
                    // STATE-AWARE GUARDS: Only call expensive async methods if action is needed
                    // This prevents API spam (60+ ListPositionsAsync calls per minute)
                    if (finalSignal == "MARKET_CLOSE")
                    {
                        // Only liquidate if we actually have a position
                        if (tradingState.CurrentPosition != null && tradingState.CurrentShares > 0)
                        {
                            await EnsureNeutralAsync(tradingState, broker, iocExecutor, stateFilePath, settings, 
                                reason: "MARKET_CLOSE", showStatus: shouldLog, slippageLock: slippageLock, 
                                updateSlippage: slippageCallback, slippageLogFile: _slippageLogFile, getSignalAtPrice: signalChecker);
                        }
                        else if (shouldLog)
                        {
                            Log($"[{easternNow:HH:mm:ss}] MARKET_CLOSE - Already flat, no action needed.");
                        }
                    }
                    else if (finalSignal == "BULL" && !latchBlocksEntry)
                    {
                        pipelineNeutralDetectionTime = null;
                        
                        // STATE CHECK: Only call async method if we're NOT already in BULL
                        bool alreadyInBull = tradingState.CurrentPosition == settings.BullSymbol && tradingState.CurrentShares > 0;
                        
                        if (!alreadyInBull)
                        {
                            var holdInfo = (currentPrice, lowerBand, upperBand, pipelineVirtualStop > 0 ? pipelineVirtualStop : (decimal?)null);
                            await EnsurePositionAsync(settings.BullSymbol, settings.BearSymbol, tradingState, broker, iocExecutor,
                                stateFilePath, settings, slippageLock, slippageCallback, _slippageLogFile, 
                                holdInfo, priceGetter, signalChecker);
                            
                            // Reset trailing stop if we just entered
                            if (tradingState.CurrentPosition == settings.BullSymbol)
                            {
                                pipelineHwm = currentPrice;
                                pipelineVirtualStop = pipelineHwm * (1 - settings.TrailingStopPercent);
                                // Persist to state
                                tradingState.HighWaterMark = pipelineHwm;
                                tradingState.TrailingStopValue = pipelineVirtualStop;
                                tradingState.LowWaterMark = null;
                                SaveTradingState(stateFilePath, tradingState);
                            }
                        }
                        // else: Already in BULL - no API call needed, just hold
                    }
                    else if (finalSignal == "BEAR" && !latchBlocksEntry)
                    {
                        pipelineNeutralDetectionTime = null;
                        
                        if (settings.BullOnlyMode)
                        {
                            // Only liquidate if we have a position
                            if (tradingState.CurrentPosition != null && tradingState.CurrentShares > 0)
                            {
                                await EnsureNeutralAsync(tradingState, broker, iocExecutor, stateFilePath, settings, 
                                    reason: "BEAR (bull-only)", showStatus: shouldLog, slippageLock: slippageLock, 
                                    updateSlippage: slippageCallback, slippageLogFile: _slippageLogFile, getSignalAtPrice: signalChecker);
                            }
                        }
                        else
                        {
                            // STATE CHECK: Only call async method if we're NOT already in BEAR
                            bool alreadyInBear = tradingState.CurrentPosition == settings.BearSymbol && tradingState.CurrentShares > 0;
                            
                            if (!alreadyInBear)
                            {
                                var holdInfo = (currentPrice, lowerBand, upperBand, pipelineVirtualStop > 0 ? pipelineVirtualStop : (decimal?)null);
                                await EnsurePositionAsync(settings.BearSymbol!, settings.BullSymbol, tradingState, broker, iocExecutor,
                                    stateFilePath, settings, slippageLock, slippageCallback, _slippageLogFile, 
                                    holdInfo, priceGetter, signalChecker);
                                
                                // Reset trailing stop if we just entered
                                if (tradingState.CurrentPosition == settings.BearSymbol)
                                {
                                    pipelineLwm = currentPrice;
                                    pipelineVirtualStop = pipelineLwm * (1 + settings.TrailingStopPercent);
                                    // Persist to state
                                    tradingState.LowWaterMark = pipelineLwm;
                                    tradingState.TrailingStopValue = pipelineVirtualStop;
                                    tradingState.HighWaterMark = null;
                                    SaveTradingState(stateFilePath, tradingState);
                                }
                            }
                            // else: Already in BEAR - no API call needed, just hold
                        }
                    }
                    else if (finalSignal == "NEUTRAL")
                    {
                        // NeutralWaitSeconds == -1 means "hold through neutral" - don't liquidate
                        // The position will only change on BULL <-> BEAR flip (or EOD/shutdown)
                        if (settings.NeutralWaitSeconds < 0)
                        {
                            // Hold current position - do nothing
                        }
                        else
                        {
                            if (pipelineNeutralDetectionTime == null)
                            {
                                pipelineNeutralDetectionTime = DateTime.UtcNow;
                            }
                            
                            var elapsed = (DateTime.UtcNow - pipelineNeutralDetectionTime.Value).TotalSeconds;
                            if (elapsed >= settings.NeutralWaitSeconds)
                            {
                                // Only liquidate if we have a position
                                if (tradingState.CurrentPosition != null && tradingState.CurrentShares > 0)
                                {
                                    await EnsureNeutralAsync(tradingState, broker, iocExecutor, stateFilePath, settings, 
                                        showStatus: shouldLog, slippageLock: slippageLock, updateSlippage: slippageCallback, 
                                        slippageLogFile: _slippageLogFile, getSignalAtPrice: signalChecker);
                                }
                            }
                        }
                    }
                    
                    // Persist trailing stop state with DEBOUNCE to avoid disk I/O blocking hot path
                    // Rule: Save immediately on CRITICAL events (IsStoppedOut change), otherwise max once per 5 seconds
                    if (settings.TrailingStopPercent > 0 && (pipelineHwm > 0 || pipelineLwm > 0))
                    {
                        bool stoppedOutChanged = tradingState.IsStoppedOut != pipelineStoppedOut;
                        bool watermarkChanged = tradingState.HighWaterMark != pipelineHwm ||
                                               tradingState.LowWaterMark != pipelineLwm ||
                                               tradingState.TrailingStopValue != pipelineVirtualStop;
                        
                        // Always update in-memory state immediately
                        tradingState.HighWaterMark = pipelineHwm > 0 ? pipelineHwm : null;
                        tradingState.LowWaterMark = pipelineLwm > 0 ? pipelineLwm : null;
                        tradingState.TrailingStopValue = pipelineVirtualStop > 0 ? pipelineVirtualStop : null;
                        tradingState.IsStoppedOut = pipelineStoppedOut;
                        tradingState.StoppedOutDirection = pipelineStoppedOut ? pipelineStoppedOutDir : null;
                        tradingState.WashoutLevel = pipelineStoppedOut ? pipelineWashoutLevel : null;
                        tradingState.StopoutTimestamp = pipelineStopoutTime?.ToString("o");
                        
                        // CRITICAL: Save immediately if stop-out state changed (can't lose this)
                        // DEBOUNCE: Otherwise, only save if 5+ seconds since last disk write
                        bool shouldWriteToDisk = stoppedOutChanged || 
                            (watermarkChanged && (DateTime.UtcNow - _lastTrailingStopDiskWrite).TotalSeconds >= TRAILING_STOP_DISK_WRITE_INTERVAL_SECONDS);
                        
                        if (shouldWriteToDisk)
                        {
                            SaveTradingState(stateFilePath, tradingState);
                            _lastTrailingStopDiskWrite = DateTime.UtcNow;
                        }
                    }
                    
                    // Sync back to outer scope (for graceful shutdown)
                    _highWaterMark = pipelineHwm;
                    _lowWaterMark = pipelineLwm;
                    _virtualStopPrice = pipelineVirtualStop;
                    _isStoppedOut = pipelineStoppedOut;
                    _stoppedOutDirection = pipelineStoppedOutDir;
                    _washoutLevel = pipelineWashoutLevel;
                    _stopoutTime = pipelineStopoutTime;
                }
                catch (Exception ex)
                {
                    LogError($"[PIPELINE] Error processing tick: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("[PIPELINE] Shutdown requested.");
        }
        catch (Exception ex)
        {
            LogError($"[PIPELINE] Error: {ex.Message}");
        }
        
        Log("[PIPELINE] Reactive pipeline stopped.");
    }

    // =========================================================================
    // UNIFIED EXECUTION: Both Streaming and Polling modes now use the pipeline
    // =========================================================================
    // The data source (AlpacaSourceAdapter or PollingDataSource) feeds the channel,
    // and RunReactivePipelineAsync consumes it. This eliminates the "Two Brains" problem.
    
    try
    {
        // Wait for a good time to start (2 minutes before market open, or immediately if market is open)
        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
        while (!ShouldStartStreams(easternNow) && !cancellationToken.IsCancellationRequested)
        {
            // Log market closed status periodically
            if (lastMarketClosedLog == null || 
                (DateTime.Now - lastMarketClosedLog.Value).TotalMinutes >= marketClosedLogIntervalMinutes)
            {
                Log($"Market Closed. Waiting for open... (ET: {easternNow:HH:mm:ss})");
                lastMarketClosedLog = DateTime.Now;
            }
            
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            easternNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, easternZone);
        }
        
        if (cancellationToken.IsCancellationRequested)
            goto Shutdown;
        
        lastMarketClosedLog = null;
        
        // Seed the IncrementalSma with historical data before starting
        Log("[PIPELINE] Seeding IncrementalSma with historical data...");
        await SeedIncrementalSmaAsync(settings, dataClient, cryptoDataClient, incrementalSma, incrementalBtcSma, easternZone);
        Log($"[PIPELINE] IncrementalSma initialized with {incrementalSma.Count} data points.\n");
        
        // Run warm-up routines if low-latency mode
        if (settings.LowLatencyMode && !_warmedUp)
        {
            Log("\n[LOW-LATENCY MODE] Running pre-market warm-up...");
            WarmUpStrategyLogic(settings.WarmUpIterations);
            await WarmUpConnectionsAsync();
            StartKeepAlivePinger();
            _warmedUp = true;
            Log("[LOW-LATENCY MODE] Warm-up complete.\n");
        }
        
        // Connect and subscribe to market data
        Log("Connecting to market data source...");
        await marketSource.ConnectAsync(cancellationToken);
        
        await marketSource.SubscribeAsync(settings.BenchmarkSymbol, tradeChannel.Writer, isBenchmark: true, cancellationToken);
        Log($"  ✓ Subscribed to {settings.BenchmarkSymbol}");
        
        if (settings.WatchBtc || settings.UseBtcEarlyTrading)
        {
            await marketSource.SubscribeAsync(settings.CryptoBenchmarkSymbol, tradeChannel.Writer, isBenchmark: false, cancellationToken);
            Log($"  ✓ Subscribed to {settings.CryptoBenchmarkSymbol}");
        }
        
        Log("Market data source ready.\n");
        
        // Enable channel writes for streaming mode
        if (settings.LowLatencyMode)
        {
            Volatile.Write(ref _lowLatencyActive, true);
        }
        
        // THE ONE RING TO RULE THEM ALL - Run the unified reactive pipeline
        await RunReactivePipelineAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
        Log("Pipeline shutdown requested.");
    }
    catch (Exception ex)
    {
        LogError($"Pipeline error: {ex.Message}");
    }
    
    Shutdown:
    
    // =========================================================================
    // GRACEFUL SHUTDOWN - Liquidate positions before exit
    // =========================================================================
    Log("\n=== Graceful Shutdown ===");
    
    if (!string.IsNullOrEmpty(tradingState.CurrentPosition) && tradingState.CurrentShares > 0)
    {
        Log($"Attempting to liquidate {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}...");
        try
        {
            var currentPosition = await broker.GetPositionAsync(tradingState.CurrentPosition);
            
            var liquidated = await LiquidatePositionAsync(
                tradingState.CurrentPosition, 
                currentPosition, 
                tradingState, 
                broker,
                iocExecutor, 
                stateFilePath,
                settings,
                slippageLock,
                slippageCallback,
                _slippageLogFile);
            
            if (liquidated)
            {
                LogSuccess("Position liquidated successfully.");
            }
            else
            {
                LogError("⚠️  POSITION NOT LIQUIDATED - Market may be closed.");
                LogError($"   You still hold {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}");
                LogError("   State file preserved. Position will be liquidated on next run when market opens.");
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to liquidate position during shutdown: {ex.Message}");
            LogError($"⚠️  You may still hold {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}");
        }
    }
    else
    {
        Log("No positions to liquidate. Already in cash.");
    }
    
    // Disconnect streaming adapter
    try
    {
        await marketSource.DisconnectAsync();
        Log("Market data source disconnected.");
    }
    catch (Exception ex)
    {
        Log($"Warning: Error disconnecting data source: {ex.Message}");
    }
    
    // Dispose streaming clients if they were created (low-latency mode only)
    stockStreamClient?.Dispose();
    cryptoStreamClient?.Dispose();
    
    // Stop keep-alive pinger (low-latency mode)
    StopKeepAlivePinger();
    
    Log("Bot shutdown complete. Final state saved.");
    Log($"Final Balance: ${tradingState.AvailableCash + tradingState.AccumulatedLeftover:N2}");
}

// Liquidate positions from config file before trading with override tickers
// IMPORTANT: Only liquidate positions this instance claims ownership of (via local state)
async Task LiquidateConfiguredPositionsAsync(
    TradingSettings configSettings,
    TradingState tradingState,
    IBrokerExecution broker,
    IocMachineGunExecutor iocExecutor,
    string stateFilePath)
{
    // Only proceed if local state claims ownership of a position
    // This prevents liquidating positions owned by other bot instances
    if (string.IsNullOrEmpty(tradingState.CurrentPosition) || tradingState.CurrentShares <= 0)
    {
        Log("No locally-owned positions to liquidate (fresh state or already in cash).");
        return;
    }
    
    var ownedSymbol = tradingState.CurrentPosition;
    
    // Only liquidate if the owned position matches one of the configured symbols
    var isBullOwned = ownedSymbol.Equals(configSettings.BullSymbol, StringComparison.OrdinalIgnoreCase);
    var isBearOwned = !string.IsNullOrEmpty(configSettings.BearSymbol) && 
                      ownedSymbol.Equals(configSettings.BearSymbol, StringComparison.OrdinalIgnoreCase);
    
    if (!isBullOwned && !isBearOwned)
    {
        Log($"Locally-owned position ({ownedSymbol}) doesn't match configured symbols. Skipping liquidation.");
        return;
    }
    
    var ownedPosition = await broker.GetPositionAsync(ownedSymbol);
    
    if (ownedPosition != null || tradingState.CurrentShares > 0)
    {
        Log($"Liquidating locally-owned position: {ownedSymbol} ({tradingState.CurrentShares} shares)");
        await LiquidatePositionAsync(ownedSymbol, ownedPosition, tradingState, broker, iocExecutor, stateFilePath, configSettings);
    }
}

// Data Seeding Logic for IncrementalSma
async Task SeedIncrementalSmaAsync(TradingSettings settings, IAlpacaDataClient dataClient, IAlpacaCryptoDataClient cryptoDataClient, IncrementalSma sma, IncrementalSma btcSma, TimeZoneInfo easternZone)
{
    var utcNow = DateTime.UtcNow;
    var easternNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternZone);
    
    // Fetch historical bars to seed the SMA with actual price history
    var endTimeUtc = DateTime.UtcNow;
    var startTimeUtc = endTimeUtc.AddMinutes(-Math.Max(settings.SMAWindowSeconds / 60 + 5, 15)); 

    try
    {
        // Seed benchmark SMA with historical minute bars
        var stockBarsRequest = new HistoricalBarsRequest(
            settings.BenchmarkSymbol,
            startTimeUtc,
            endTimeUtc,
            BarTimeFrame.Minute
        )
        {
            Feed = MarketDataFeed.Iex
        };

        var stockBarsResponse = await dataClient.ListHistoricalBarsAsync(stockBarsRequest);
        var bars = stockBarsResponse.Items.TakeLast(settings.SMALength).ToList();
        
        if (bars.Count > 0)
        {
            foreach (var bar in bars)
            {
                sma.Add(bar.Close);
            }
            Log($"  ✓ Benchmark SMA seeded with {bars.Count} historical bars");
        }
        else
        {
            // Fallback: seed with current price
            var quoteRequest = new LatestMarketDataRequest(settings.BenchmarkSymbol) { Feed = MarketDataFeed.Iex };
            var trade = await dataClient.GetLatestTradeAsync(quoteRequest);
            for (int i = 0; i < settings.SMALength; i++)
            {
                sma.Add(trade.Price);
            }
            Log($"  ✓ Benchmark SMA seeded with current price: ${trade.Price:N2} (x{settings.SMALength})");
        }
    }
    catch (Exception ex)
    {
        LogError($"  Failed to seed benchmark SMA: {ex.Message}");
    }
    
    // Seed BTC SMA if WatchBtc is enabled or UseBtcEarlyTrading
    if (settings.WatchBtc || settings.UseBtcEarlyTrading)
    {
        try
        {
            var cryptoBarsRequest = new HistoricalCryptoBarsRequest(
                settings.CryptoBenchmarkSymbol,
                startTimeUtc,
                endTimeUtc,
                BarTimeFrame.Minute
            );
            var cryptoBarsResponse = await cryptoDataClient.ListHistoricalBarsAsync(cryptoBarsRequest);
            var btcBars = cryptoBarsResponse.Items.TakeLast(settings.SMALength).ToList();
            
            if (btcBars.Count > 0)
            {
                foreach (var bar in btcBars)
                {
                    btcSma.Add(bar.Close);
                }
                Log($"  ✓ BTC SMA seeded with {btcBars.Count} historical bars");
            }
            else
            {
                // Fallback
                var latestTrades = await cryptoDataClient.ListLatestTradesAsync(new LatestDataListRequest([settings.CryptoBenchmarkSymbol]));
                var btcPrice = latestTrades[settings.CryptoBenchmarkSymbol].Price;
                for (int i = 0; i < settings.SMALength; i++)
                {
                    btcSma.Add(btcPrice);
                }
                Log($"  ✓ BTC SMA seeded with current price: ${btcPrice:N2} (x{settings.SMALength})");
            }
        }
        catch (Exception ex)
        {
            LogError($"  Failed to seed BTC SMA: {ex.Message}");
        }
    }
}

// Execution Logic: Enter Position
async Task EnsurePositionAsync(
    string targetSymbol, 
    string? oppositeSymbol, 
    TradingState tradingState, 
    IBrokerExecution broker,
    IocMachineGunExecutor iocExecutor,
    string stateFilePath,
    TradingSettings settings,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    (decimal quote, decimal lowerBand, decimal upperBand, decimal? stopPrice)? holdDisplayInfo = null,
    Func<decimal>? getCurrentPrice = null,
    Func<decimal, string>? getSignalAtPrice = null)
{
    // First, clean up any orphaned shares from previous partial fills
    await CleanupOrphanedSharesAsync(tradingState, broker, iocExecutor, stateFilePath, settings);
    
    // Get current positions from broker
    var targetPosition = await broker.GetPositionAsync(targetSymbol);
    BotPosition? oppositePosition = null;
    if (!string.IsNullOrEmpty(oppositeSymbol))
    {
        oppositePosition = await broker.GetPositionAsync(oppositeSymbol);
    }
    
    // Also check local state
    var localHoldsTarget = tradingState.CurrentPosition?.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase) ?? false;
    var localHoldsOpposite = !string.IsNullOrEmpty(oppositeSymbol) && 
        (tradingState.CurrentPosition?.Equals(oppositeSymbol, StringComparison.OrdinalIgnoreCase) ?? false);

    // 1. Liquidate Opposite if held (only if we have an opposite symbol)
    // LiquidatePositionAsync now WAITS for fill before returning, preventing race conditions
    if (!string.IsNullOrEmpty(oppositeSymbol) && (oppositePosition != null || localHoldsOpposite))
    {
        Log($"Current signal targets {targetSymbol}, but holding {oppositeSymbol}. Liquidating...");
        var liquidated = await LiquidatePositionAsync(oppositeSymbol, oppositePosition, tradingState, broker, iocExecutor, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
        
        if (!liquidated)
        {
            LogError($"[ERROR] Failed to liquidate {oppositeSymbol}. Aborting position switch.");
            return; // Don't proceed to buy if liquidation failed
        }
    }

    // 2. SAFEGUARD: Re-check broker positions before buying to prevent dual holdings
    // This catches edge cases where external orders or race conditions left positions open
    if (!string.IsNullOrEmpty(oppositeSymbol))
    {
        oppositePosition = await broker.GetPositionAsync(oppositeSymbol);
    }
    
    if (oppositePosition is BotPosition oppPos && oppPos.Quantity != 0)
    {
        if (oppPos.Quantity > 0)
        {
            // LONG position exists - sync state and attempt emergency liquidation
            LogWarning($"[SAFEGUARD] Opposite position {oppositeSymbol} still exists ({oppPos.Quantity} shares). Syncing state and attempting liquidation...");
            
            // Sync local state from broker to fix mismatch
            tradingState.CurrentPosition = oppositeSymbol;
            tradingState.CurrentShares = oppPos.Quantity;
            SaveTradingState(stateFilePath, tradingState);
            
            // Attempt to liquidate the orphaned position
            var emergencyLiquidated = await LiquidatePositionAsync(oppositeSymbol!, oppositePosition, tradingState, broker, iocExecutor, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
            
            if (!emergencyLiquidated)
            {
                LogError($"[SAFEGUARD] Emergency liquidation of {oppositeSymbol} failed. Aborting buy to prevent dual holdings.");
                return;
            }
            
            // Verify liquidation succeeded
            var verifyPos = await broker.GetPositionAsync(oppositeSymbol!);
            if (verifyPos is BotPosition stillHeld && stillHeld.Quantity > 0)
            {
                LogError($"[SAFEGUARD] {oppositeSymbol} still has {stillHeld.Quantity} shares after emergency liquidation. Aborting.");
                return;
            }
            
            LogSuccess($"[SAFEGUARD] Emergency liquidation of {oppositeSymbol} successful. Proceeding with buy.");
        }
        else
        {
            // SHORT position exists - this bot doesn't manage shorts
            LogError($"[SAFEGUARD] SHORT position detected: {oppositeSymbol} has {oppPos.Quantity} shares. This bot does not manage short positions. Aborting buy to prevent complications.");
            return;
        }
    }

    // 3. Buy Target if not held
    targetPosition = await broker.GetPositionAsync(targetSymbol);
    var alreadyHoldsTarget = targetPosition != null || (tradingState.CurrentPosition?.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase) ?? false);
    
    if (!alreadyHoldsTarget)
    {
        await BuyPositionAsync(targetSymbol, tradingState, broker, iocExecutor, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getCurrentPrice);
    }
    else
    {
        var heldShares = targetPosition?.Quantity ?? tradingState.CurrentShares;
        if (holdDisplayInfo.HasValue)
        {
            var (quote, lower, upper, stop) = holdDisplayInfo.Value;
            var stopStr = stop.HasValue ? $"${stop.Value:N2}" : "N/A";
            Log($"[HOLD] Staying Long {targetSymbol} ({heldShares} shares) - Band: [${lower:N2}-${upper:N2}] - Quote: ${quote:N2} - Stop: {stopStr}");
        }
        else
        {
            Log($"[HOLD] Staying Long {targetSymbol} ({heldShares} shares).");
        }
    }
}

// Execution Logic: Enter Neutral (Cash)
async Task EnsureNeutralAsync(
    TradingState tradingState, 
    IBrokerExecution broker,
    IocMachineGunExecutor iocExecutor, 
    string stateFilePath,
    TradingSettings settings,
    string reason = "NEUTRAL",
    bool showStatus = true,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    Func<decimal, string>? getSignalAtPrice = null)
{
    // Get current positions from broker
    var bullPosition = await broker.GetPositionAsync(settings.BullSymbol);
    var localHoldsBull = tradingState.CurrentPosition?.Equals(settings.BullSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

    if (bullPosition != null || localHoldsBull)
    {
        Log($"Signal is {reason}. Liquidating {settings.BullSymbol} to Cash.");
        await LiquidatePositionAsync(settings.BullSymbol, bullPosition, tradingState, broker, iocExecutor, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
    }

    // Check for Bear Symbol (only if BearSymbol is configured)
    if (!string.IsNullOrEmpty(settings.BearSymbol))
    {
        var bearPosition = await broker.GetPositionAsync(settings.BearSymbol);
        var localHoldsBear = tradingState.CurrentPosition?.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

        if (bearPosition != null || localHoldsBear)
        {
             Log($"Signal is {reason}. Liquidating {settings.BearSymbol} to Cash.");
             await LiquidatePositionAsync(settings.BearSymbol, bearPosition, tradingState, broker, iocExecutor, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
        }
    }

    if (showStatus)
    {
        Log($"[{reason}] Sitting in Cash ⚪");
    }
}


// Helper: Cleanup Orphaned Shares
// Called after a successful position switch to liquidate any remaining shares from partial fills
async Task CleanupOrphanedSharesAsync(
    TradingState tradingState,
    IBrokerExecution broker,
    IocMachineGunExecutor iocExecutor,
    string stateFilePath,
    TradingSettings settings)
{
    if (tradingState.OrphanedShares == null || tradingState.OrphanedShares.Shares <= 0)
    {
        return; // No orphans to clean up
    }
    
    var orphan = tradingState.OrphanedShares;
    Log($"[ORPHAN] Cleaning up {orphan.Shares} orphaned share(s) of {orphan.Symbol}...");
    
    try
    {
        // Get current price for the orphaned symbol
        var orphanPrice = await broker.GetLatestPriceAsync(orphan.Symbol);
        var limitPrice = orphanPrice - (settings.IocLimitOffsetCents / 100m); // Aggressive sell
        
        // Use IOC machine gun to liquidate orphans
        var result = await iocExecutor.ExecuteAsync(
            orphan.Symbol,
            orphan.Shares,
            BotOrderSide.Sell,
            limitPrice,
            settings.IocRetryStepCents,
            settings.IocMaxRetries + 2, // Extra retries for cleanup
            settings.IocMaxDeviationPercent * 2); // More aggressive deviation allowance
        
        if (result.FilledQty > 0)
        {
            // Add proceeds to cash
            tradingState.AvailableCash += result.TotalProceeds;
            
            if (result.FilledQty >= orphan.Shares)
            {
                // Fully cleaned up
                tradingState.OrphanedShares = null;
                SaveTradingState(stateFilePath, tradingState);
                LogSuccess($"[ORPHAN] Cleanup complete: Sold {result.FilledQty} @ ${result.AvgPrice:N4} (+${result.TotalProceeds:N2})");
            }
            else
            {
                // Still have some orphans left
                var remaining = orphan.Shares - result.FilledQty;
                tradingState.OrphanedShares.Shares = remaining;
                SaveTradingState(stateFilePath, tradingState);
                LogWarning($"[ORPHAN] Partial cleanup: Sold {result.FilledQty} @ ${result.AvgPrice:N4}. {remaining} share(s) still orphaned.");
            }
        }
        else
        {
            // IOC failed - try a market order as last resort
            LogWarning($"[ORPHAN] IOC cleanup failed. Attempting market order...");
            
            var marketRequest = new BotOrderRequest
            {
                Symbol = orphan.Symbol,
                Quantity = orphan.Shares,
                Side = BotOrderSide.Sell,
                Type = BotOrderType.Market,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = settings.GenerateClientOrderId()
            };
            
            try
            {
                var order = await broker.SubmitOrderAsync(marketRequest);
                Log($"[ORPHAN] Market order submitted: {order.OrderId}");
                
                // Wait for fill
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(500);
                    var filledOrder = await broker.GetOrderAsync(order.OrderId);
                    
                    if (filledOrder.Status == BotOrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                    {
                        var proceeds = filledOrder.FilledQuantity * filledOrder.AverageFillPrice.Value;
                        tradingState.AvailableCash += proceeds;
                        tradingState.OrphanedShares = null;
                        SaveTradingState(stateFilePath, tradingState);
                        LogSuccess($"[ORPHAN] Market cleanup complete: Sold {filledOrder.FilledQuantity} @ ${filledOrder.AverageFillPrice:N4} (+${proceeds:N2})");
                        return;
                    }
                }
                
                LogError($"[ORPHAN] Market order did not fill within timeout. Orphan remains.");
            }
            catch (Exception marketEx)
            {
                LogError($"[ORPHAN] Market order failed: {marketEx.Message}");
            }
        }
    }
    catch (Exception ex)
    {
        LogError($"[ORPHAN] Cleanup failed: {ex.Message}. Will retry on next cycle.");
    }
}


// Helper: Liquidate Position
// Returns true if liquidation succeeded, false if it failed
// IMPORTANT: This method now WAITS for the sell order to fill before returning
// to prevent race conditions where a buy order is placed before the sell completes.
async Task<bool> LiquidatePositionAsync(
    string symbol, 
    BotPosition? position, 
    TradingState tradingState, 
    IBrokerExecution broker,
    IocMachineGunExecutor iocExecutor, 
    string stateFilePath,
    TradingSettings settings,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    Func<decimal, string>? getSignalAtPrice = null) // Optional: for smart exit chaser
{
    // Calculate expected sale proceeds before liquidating
    var quotePrice = await broker.GetLatestPriceAsync(symbol);
    
    // Use LOCAL state share count (not Alpaca position) to support multi-instance scenarios
    // This ensures we only sell shares this bot instance "owns", not shares from other instances
    var shareCount = tradingState.CurrentShares;
    
    if (shareCount <= 0)
    {
        // LOCAL STATE SHOWS 0 - but check if broker has a real position (state/broker mismatch)
        // This can happen if trading_state.json was deleted or corrupted while positions exist
        if (position is { } pos && pos.Quantity != 0)
        {
            // CRITICAL: Broker has shares but local state doesn't! This is a dangerous mismatch.
            // For LONG positions (Quantity > 0): Sync state and liquidate to prevent dual holdings
            // For SHORT positions (Quantity < 0): Log error but don't auto-liquidate shorts
            if (pos.Quantity > 0)
            {
                LogWarning($"[STATE MISMATCH] Local state shows 0 shares but broker has {pos.Quantity} shares of {symbol}. Syncing state and liquidating...");
                tradingState.CurrentPosition = symbol;
                tradingState.CurrentShares = pos.Quantity;
                shareCount = pos.Quantity;
                SaveTradingState(stateFilePath, tradingState);
                // Continue to liquidation below
            }
            else
            {
                // SHORT POSITION detected - this bot doesn't manage shorts, so don't touch it
                LogError($"[STATE MISMATCH] Broker shows SHORT position of {pos.Quantity} shares of {symbol}. This bot does not manage short positions - manual intervention required.");
                // Clear local state but return false to prevent position switch
                tradingState.CurrentPosition = null;
                tradingState.CurrentShares = 0;
                SaveTradingState(stateFilePath, tradingState);
                return false; // Block position switch - there's a short position we can't handle
            }
        }
        else
        {
            Log($"[WARN] No shares to liquidate for {symbol} (local state shows 0). Clearing state.");
            tradingState.CurrentPosition = null;
            tradingState.CurrentShares = 0;
            SaveTradingState(stateFilePath, tradingState);
            return true; // Nothing to sell, state is clean
        }
    }
    
    var estimatedProceeds = shareCount * quotePrice;
    
    // Determine order execution mode
    var useIoc = settings.UseIocOrders;
    var useLimit = settings.UseMarketableLimits && !useIoc; // IOC takes precedence
    
    // Calculate limit price based on mode
    decimal limitPrice;
    if (useIoc)
    {
        // IOC "Sniper Mode": Limit at Bid - offset cents for instant fills
        limitPrice = Math.Round(quotePrice - (settings.IocLimitOffsetCents / 100m), 2);
    }
    else if (useLimit)
    {
        // Marketable Limit: Allow MaxSlippagePercent deviation
        limitPrice = Math.Round(quotePrice * (1m - settings.MaxSlippagePercent), 2);
    }
    else
    {
        limitPrice = 0m; // Market order
    }
    
    // Liquidate using market, limit, or IOC sell order
    var orderTypeStr = useIoc ? $"IOC LIMIT @ ${limitPrice:N2} (Machine-Gun)" : (useLimit ? $"LIMIT @ ${limitPrice:N2}" : "MARKET");
    Log($"[SELL] Liquidating {shareCount} shares of {symbol} @ ~${quotePrice:N2} ({orderTypeStr})");
    Log($"       Expected proceeds: ${estimatedProceeds:N2}");
    
    // =========================================================================
    // IOC MACHINE-GUN FAST PATH FOR SELLS
    // =========================================================================
    if (useIoc)
    {
        var result = await iocExecutor.ExecuteAsync(
            symbol,
            shareCount,
            BotOrderSide.Sell,
            limitPrice,
            settings.IocRetryStepCents,
            settings.IocMaxRetries,
            settings.IocMaxDeviationPercent);
        
        if (result.FilledQty > 0)
        {
            // Success - update state with actual fill
            var iocProceeds = result.TotalProceeds;
            var slippage = iocProceeds - estimatedProceeds;
            
            // ACCUMULATE proceeds (don't overwrite) - handles partial fill retries correctly
            // On first call: AvailableCash was 0, AccumulatedLeftover had the buy change
            // On retry after partial: AvailableCash has previous proceeds, AccumulatedLeftover is 0
            tradingState.AvailableCash += iocProceeds + tradingState.AccumulatedLeftover;
            tradingState.AccumulatedLeftover = 0m; // Clear leftover - it's now part of AvailableCash
            
            // If we sold everything
            if (result.FilledQty >= shareCount)
            {
                tradingState.CurrentPosition = null;
                tradingState.CurrentShares = 0;
                SaveTradingState(stateFilePath, tradingState);
                
                var slipLabel = slippage > 0 ? "favorable" : (slippage < 0 ? "unfavorable" : "neutral");
                var slipMessage = $"[FILL] IOC Sell complete: {result.FilledQty} @ ${result.AvgPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})";
                if (slippage > 0) LogSuccess(slipMessage); else if (slippage < 0) LogRed(slipMessage); else LogBlue(slipMessage);
                
                // Track slippage if monitoring enabled
                if (settings.MonitorSlippage && slippageLock != null && updateSlippage != null)
                {
                    var tradeSlippage = (result.AvgPrice - quotePrice) * result.FilledQty;
                    lock (slippageLock)
                    {
                        updateSlippage(tradeSlippage);
                    }
                    
                    if (slippageLogFile != null)
                    {
                        var favor = Math.Sign(tradeSlippage);
                        var csvLine = $"{DateTime.UtcNow:s},{symbol},Sell,{result.FilledQty},{quotePrice:F4},{result.AvgPrice:F4},{tradeSlippage:F2},{favor}";
                        _ = File.AppendAllTextAsync(slippageLogFile, csvLine + Environment.NewLine);
                    }
                }
                
                // Display balance and P/L
                var iocTotalBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
                var iocProfitLoss = iocTotalBalance - tradingState.StartingAmount;
                LogBalanceWithPL(iocTotalBalance, iocProfitLoss);
                
                return true;
            }
            else
            {
                // Partial fill - still have shares
                var remainingShares = shareCount - result.FilledQty;
                
                // Check if remaining shares are within tolerance ("good enough" liquidation)
                if (remainingShares <= settings.IocRemainingSharesTolerance)
                {
                    // Queue orphaned shares for cleanup after position switch completes
                    tradingState.OrphanedShares = new OrphanedPosition
                    {
                        Symbol = symbol,
                        Shares = remainingShares,
                        CreatedAt = DateTime.UtcNow.ToString("o")
                    };
                    
                    LogWarning($"[FILL] IOC Sell partial: {result.FilledQty}/{shareCount} @ ${result.AvgPrice:N4}. Queued {remainingShares} share(s) for cleanup after position switch.");
                    
                    // Clear main position - we're treating this as liquidated for position switch purposes
                    tradingState.CurrentPosition = null;
                    tradingState.CurrentShares = 0;
                    SaveTradingState(stateFilePath, tradingState);
                    
                    // Display balance and P/L (same as complete fill)
                    var partialTotalBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
                    var partialProfitLoss = partialTotalBalance - tradingState.StartingAmount;
                    LogBalanceWithPL(partialTotalBalance, partialProfitLoss);
                    
                    return true; // Treat as successful liquidation
                }
                
                tradingState.CurrentShares = remainingShares;
                SaveTradingState(stateFilePath, tradingState);
                
                LogWarning($"[FILL] IOC Sell partial: {result.FilledQty}/{shareCount} @ ${result.AvgPrice:N4}. {remainingShares} shares remaining.");
                return false; // Partial liquidation
            }
        }
        else
        {
            // IOC failed completely - nothing sold
            LogError($"[FILL] IOC Sell failed after {settings.IocMaxRetries} attempts. Falling back to market order...");
            // Fall through to standard order path with market order
            useIoc = false;
            useLimit = false;
        }
    }
    
    // =========================================================================
    // STANDARD ORDER PATH (Limit or Market orders) - using broker interface
    // =========================================================================
    BotOrder? sellOrder = null;
    
    try
    {
        BotOrderRequest sellRequest;
        
        if (useLimit)
        {
            sellRequest = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = shareCount,
                Side = BotOrderSide.Sell,
                Type = BotOrderType.Limit,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = settings.GenerateClientOrderId(),
                LimitPrice = limitPrice
            };
        }
        else
        {
            sellRequest = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = shareCount,
                Side = BotOrderSide.Sell,
                Type = BotOrderType.Market,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = settings.GenerateClientOrderId()
            };
        }
        
        sellOrder = await broker.SubmitOrderAsync(sellRequest);
        LogSuccess($"Sell order submitted: {sellOrder.OrderId} (ClientId: {sellOrder.ClientOrderId})");
    }
    catch (Exception ex)
    {
        // Handle case where position doesn't exist or insufficient shares
        if (ex.Message.Contains("insufficient") || ex.Message.Contains("not found"))
        {
            Log($"[WARN] Could not sell shares ({ex.Message}). Position may not exist.");
            tradingState.CurrentPosition = null;
            tradingState.CurrentShares = 0;
            SaveTradingState(stateFilePath, tradingState);
            return true; // Position doesn't exist, state is clean
        }
        else if (ex.Message.Contains("market") || ex.Message.Contains("closed") || ex.Message.Contains("outside"))
        {
            LogError($"[WARN] Market may be closed. Cannot liquidate: {ex.Message}");
            return false; // Don't update state - position still exists
        }
        else
        {
            LogError($"[ERROR] Liquidation failed: {ex.Message}");
            return false; // Don't update state on unknown errors
        }
    }
    
    // CRITICAL: Wait for the sell order to fill before updating state and returning
    // This prevents race conditions where buy orders are placed before sells complete
    decimal actualProceeds = estimatedProceeds; // Default to estimate if polling fails
    long actualQty = shareCount;
    decimal actualPrice = quotePrice;
    bool fillConfirmed = false;
    var startTime = DateTime.UtcNow;
    var chaserTimeoutMs = 5000; // 5 second timeout for limit orders before chaser kicks in
    bool chaserTriggered = false;
    long filledQtySoFar = 0;
    
    // Standard limit/market orders - poll every 500ms
    var pollDelayMs = 500;
    var maxIterations = 20; // 10s total
    
    for (int i = 0; i < maxIterations; i++)
    {
        await Task.Delay(pollDelayMs);
        try
        {
            var filledOrder = await broker.GetOrderAsync(sellOrder!.OrderId);
            
            if (filledOrder.Status == BotOrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
            {
                actualPrice = filledOrder.AverageFillPrice.Value;
                actualQty = filledOrder.FilledQuantity;
                actualProceeds = actualQty * actualPrice;
                fillConfirmed = true;
                
                var slippage = actualProceeds - estimatedProceeds;
                if (Math.Abs(slippage) > 0.001m)
                {
                    // For SELL: positive slippage = got more than expected = favorable
                    var slipLabel = slippage > 0 ? "favorable" : "unfavorable";
                    var slipMessage = $"[FILL] Sell confirmed: {actualQty} @ ${actualPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})";
                    if (slippage > 0) LogSuccess(slipMessage); else LogRed(slipMessage);
                }
                else
                {
                    LogBlue($"[FILL] Sell confirmed: {actualQty} @ ${actualPrice:N4} (slippage: +0.00 neutral)");
                    Log($"[FILL] Sell confirmed: {actualQty} @ ${actualPrice:N4}");
                }
                
                // Track slippage if monitoring enabled
                // For SELL: positive slippage = got more than quoted (good)
                if (settings.MonitorSlippage && slippageLock != null && updateSlippage != null)
                {
                    var tradeSlippage = (actualPrice - quotePrice) * actualQty;
                    lock (slippageLock)
                    {
                        updateSlippage(tradeSlippage);
                    }
                    
                    // Log to CSV
                    if (slippageLogFile != null)
                    {
                        var favor = Math.Sign(tradeSlippage);
                        var csvLine = $"{DateTime.UtcNow:s},{symbol},Sell,{actualQty},{quotePrice:F4},{actualPrice:F4},{tradeSlippage:F2},{favor}";
                        try { await File.AppendAllTextAsync(slippageLogFile, csvLine + Environment.NewLine); } catch { }
                    }
                }
                break;
            }
            else if (filledOrder.Status == BotOrderStatus.Canceled || 
                     filledOrder.Status == BotOrderStatus.Expired ||
                     filledOrder.Status == BotOrderStatus.Rejected)
            {
                // Order failed - don't update state, position still exists
                LogError($"[FILL] Sell order {filledOrder.Status} - position still held");
                return false;
            }
            else if (filledOrder.Status == BotOrderStatus.PartiallyFilled)
            {
                // Partially filled - keep waiting for full fill
                var partialQty = filledOrder.FilledQuantity;
                Log($"[FILL] Partial fill: {partialQty}/{shareCount} shares...");
                filledQtySoFar = partialQty;
            }
            
            // EXIT CHASER LOGIC: Limit order hasn't filled in time
            // SMART EXIT: Re-check signal before forcing market sell
            // This catches "V-shaped" reversals where price recovers during timeout
            if (useLimit && !chaserTriggered && (DateTime.UtcNow - startTime).TotalMilliseconds >= chaserTimeoutMs)
            {
                chaserTriggered = true;
                filledQtySoFar = filledOrder.FilledQuantity;
                
                // Cancel the limit order first
                LogWarning($"[EXECUTION] Sell limit order timed out after {chaserTimeoutMs}ms. Evaluating exit...");
                try
                {
                    await broker.CancelOrderAsync(sellOrder!.OrderId);
                    Log($"    Limit order cancelled.");
                }
                catch (Exception cancelEx)
                {
                    // Order may have filled while we were trying to cancel
                    if (cancelEx.Message.Contains("filled", StringComparison.OrdinalIgnoreCase) ||
                        cancelEx.Message.Contains("cannot be cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"    Order already filled during cancel attempt.");
                        continue; // Next iteration will pick up the fill
                    }
                    LogError($"    Cancel error: {cancelEx.Message}");
                }
                
                // Wait for cancel confirmation
                await Task.Delay(500);
                var cancelledOrder = await broker.GetOrderAsync(sellOrder!.OrderId);
                filledQtySoFar = cancelledOrder.FilledQuantity;
                
                if (cancelledOrder.AverageFillPrice.HasValue && filledQtySoFar > 0)
                {
                    actualProceeds = filledQtySoFar * cancelledOrder.AverageFillPrice.Value;
                }
                
                var remainingQty = shareCount - filledQtySoFar;
                
                if (remainingQty > 0)
                {
                    // SMART EXIT LOGIC: Re-check signal before forcing market sell
                    // This catches "V-shaped" reversals where price recovers during timeout
                    bool shouldForceExit = true;
                    
                    if (getSignalAtPrice != null)
                    {
                        // Get fresh price from broker
                        decimal currentPrice = quotePrice;
                        try
                        {
                            currentPrice = await broker.GetLatestPriceAsync(symbol);
                        }
                        catch { /* Use original quote price */ }
                        
                        var currentSignal = getSignalAtPrice(currentPrice);
                        
                        // Check if signal has recovered (false alarm)
                        // Selling BULL (TQQQ) but signal is now BULL = false alarm, abort
                        // Selling BEAR (SQQQ) but signal is now BEAR = false alarm, abort
                        bool isBullSymbol = symbol.Equals(settings.BullSymbol, StringComparison.OrdinalIgnoreCase);
                        bool isBearSymbol = !string.IsNullOrEmpty(settings.BearSymbol) && symbol.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase);
                        
                        bool isFalseAlarm = (isBullSymbol && currentSignal == "BULL") ||
                                            (isBearSymbol && currentSignal == "BEAR");
                        
                        if (isFalseAlarm)
                        {
                            shouldForceExit = false;
                            LogWarning($"[ABORT EXIT] Sell limit timed out, but price recovered to ${currentPrice:N2}.");
                            Log($"    Signal is now {currentSignal}. Holding position (V-shaped reversal detected).");
                            
                            // Keep the shares we still have
                            if (filledQtySoFar > 0)
                            {
                                // Partial fill happened - update state to reflect sold shares
                                tradingState.CurrentShares = shareCount - filledQtySoFar;
                                tradingState.AvailableCash += actualProceeds;
                                SaveTradingState(stateFilePath, tradingState);
                                Log($"    Kept {tradingState.CurrentShares} shares, sold {filledQtySoFar} (partial fill).");
                            }
                            
                            return false; // Indicate we didn't fully liquidate
                        }
                        else
                        {
                            Log($"    Signal is {currentSignal} (still bad). Proceeding with forced exit.");
                        }
                    }
                    
                    if (shouldForceExit)
                    {
                        // Submit chaser market order for remaining shares
                        LogWarning($"[EXECUTION] Forced MARKET sell order for remaining {remainingQty} shares.");
                        
                        var chaserRequest = new BotOrderRequest
                        {
                            Symbol = symbol,
                            Quantity = remainingQty,
                            Side = BotOrderSide.Sell,
                            Type = BotOrderType.Market,
                            TimeInForce = BotTimeInForce.Day,
                            ClientOrderId = settings.GenerateClientOrderId()
                        };
                        
                        var chaserResult = await broker.SubmitOrderAsync(chaserRequest);
                        sellOrder = chaserResult; // Track the chaser order now
                        Log($"    Chaser sell order submitted: {chaserResult.OrderId}");
                    }
                }
                else
                {
                    // All shares filled during cancel - we're done
                    Log($"    All {filledQtySoFar} shares filled during cancel.");
                    actualQty = filledQtySoFar;
                    fillConfirmed = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[FILL] Error polling sell status: {ex.Message}");
        }
    }
    
    if (!fillConfirmed)
    {
        Log($"[FILL] Sell fill confirmation timeout - using estimate (${estimatedProceeds:N2})");
    }
    
    // NOW update trading state after sell is confirmed (or timed out with estimate)
    tradingState.AvailableCash = actualProceeds + tradingState.AccumulatedLeftover;
    tradingState.AccumulatedLeftover = 0m; 
    tradingState.CurrentPosition = null;
    tradingState.CurrentShares = 0;
    
    // Clear trailing stop state on position exit
    tradingState.HighWaterMark = null;
    tradingState.LowWaterMark = null;
    tradingState.TrailingStopValue = null;
    // Note: Keep IsStoppedOut and related fields - they control the washout latch
    
    SaveTradingState(stateFilePath, tradingState);
    
    // Display balance and P/L after sale
    var totalBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
    var profitLoss = totalBalance - tradingState.StartingAmount;
    LogBalanceWithPL(totalBalance, profitLoss);
    
    return true;
}

// Helper: Buy Position
async Task BuyPositionAsync(
    string symbol,
    TradingState tradingState, 
    IBrokerExecution broker,
    IocMachineGunExecutor iocExecutor,
    string stateFilePath,
    TradingSettings settings,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    Func<decimal>? getCurrentPrice = null) // Optional: for smart chaser logic
{
    // Use our tracked available cash
    var availableForPurchase = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
    
    if (availableForPurchase <= 0)
    {
        Log($"No available cash for trading. Available: ${availableForPurchase:N2}");
        return;
    }

    // Get target ETF price from broker
    var basePrice = await broker.GetLatestPriceAsync(symbol);

    // Determine order execution mode
    var useIoc = settings.UseIocOrders;
    var useLimit = settings.UseMarketableLimits && !useIoc; // IOC takes precedence
    
    // Calculate limit price based on mode
    decimal limitPrice;
    if (useIoc)
    {
        // IOC "Sniper Mode": Limit at Ask + offset cents for instant fills
        limitPrice = Math.Round(basePrice + (settings.IocLimitOffsetCents / 100m), 2);
    }
    else if (useLimit)
    {
        // Marketable Limit: Allow MaxSlippagePercent deviation
        limitPrice = Math.Round(basePrice * (1m + settings.MaxSlippagePercent), 2);
    }
    else
    {
        limitPrice = 0m; // Market order
    }

    // Calculate max shares (use limit price if applicable to ensure we can afford at max slippage)
    var effectivePrice = (useIoc || useLimit) ? limitPrice : basePrice;
    var quantity = (long)(availableForPurchase / effectivePrice);
    var totalCost = quantity * basePrice; // Estimate at base price
    var leftover = availableForPurchase - (quantity * effectivePrice);

    if (quantity > 0)
    {
        var orderTypeStr = useIoc ? $"IOC LIMIT @ ${limitPrice:N2} (Machine-Gun)" : (useLimit ? $"LIMIT @ ${limitPrice:N2}" : "MARKET");
        Log($"[BUY] {symbol} x {quantity} @ ~${basePrice:N2} ({orderTypeStr})");
        
        // =====================================================================
        // IOC MACHINE-GUN FAST PATH
        // For IOC orders, use synchronous retry logic without polling delays
        // =====================================================================
        if (useIoc)
        {
            var result = await iocExecutor.ExecuteAsync(
                symbol,
                quantity,
                BotOrderSide.Buy,
                limitPrice,
                settings.IocRetryStepCents,
                settings.IocMaxRetries,
                settings.IocMaxDeviationPercent);
            
            if (result.FilledQty > 0)
            {
                // Success - update state with actual fill
                var actualCost = result.TotalProceeds;
                var actualLeftover = availableForPurchase - actualCost;
                var slippage = actualCost - (result.FilledQty * basePrice);
                
                tradingState.AvailableCash = 0m;
                tradingState.AccumulatedLeftover = actualLeftover;
                tradingState.CurrentPosition = symbol;
                tradingState.CurrentShares = result.FilledQty;
                tradingState.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
                SaveTradingState(stateFilePath, tradingState);
                
                var slipLabel = slippage < 0 ? "favorable" : (slippage > 0 ? "unfavorable" : "neutral");
                var slipMessage = $"[FILL] IOC Buy complete: {result.FilledQty} @ ${result.AvgPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})";
                if (slippage < 0) LogSuccess(slipMessage); else if (slippage > 0) LogRed(slipMessage); else LogBlue(slipMessage);
                
                // Track slippage if monitoring enabled
                if (settings.MonitorSlippage && slippageLock != null && updateSlippage != null)
                {
                    var tradeSlippage = (basePrice - result.AvgPrice) * result.FilledQty;
                    lock (slippageLock)
                    {
                        updateSlippage(tradeSlippage);
                    }
                    
                    if (slippageLogFile != null)
                    {
                        var favor = Math.Sign(tradeSlippage);
                        var csvLine = $"{DateTime.UtcNow:s},{symbol},Buy,{result.FilledQty},{basePrice:F4},{result.AvgPrice:F4},{tradeSlippage:F2},{favor}";
                        _ = File.AppendAllTextAsync(slippageLogFile, csvLine + Environment.NewLine);
                    }
                }
                
                // Cleanup any orphaned shares from previous partial liquidation
                await CleanupOrphanedSharesAsync(tradingState, broker, iocExecutor, stateFilePath, settings);
            }
            else
            {
                // IOC failed completely - rollback state
                LogWarning($"[FILL] IOC Buy failed after {settings.IocMaxRetries} attempts. Price moved too fast.");
                tradingState.AvailableCash = availableForPurchase;
                tradingState.AccumulatedLeftover = 0m;
                tradingState.CurrentPosition = null;
                tradingState.CurrentShares = 0;
                SaveTradingState(stateFilePath, tradingState);
            }
            
            return; // Exit - IOC path complete
        }
        
        // =====================================================================
        // STANDARD ORDER PATH (Limit or Market orders)
        // =====================================================================
        BotOrderRequest orderRequest;
        
        if (useLimit)
        {
            orderRequest = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = quantity,
                Side = BotOrderSide.Buy,
                Type = BotOrderType.Limit,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = settings.GenerateClientOrderId(),
                LimitPrice = limitPrice
            };
        }
        else
        {
            orderRequest = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = quantity,
                Side = BotOrderSide.Buy,
                Type = BotOrderType.Market,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = settings.GenerateClientOrderId()
            };
        }

        var order = await broker.SubmitOrderAsync(orderRequest);
        LogSuccess($"Order submitted: {order.OrderId} (ClientId: {order.ClientOrderId})");
        
        // Update state immediately with estimate (non-blocking)
        Log($"       Estimated: {quantity} shares @ ${basePrice:N2} = ${totalCost:N2}");
        
        tradingState.AvailableCash = 0m; 
        tradingState.AccumulatedLeftover = leftover; 
        tradingState.CurrentPosition = symbol;
        tradingState.CurrentShares = quantity;
        tradingState.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
        SaveTradingState(stateFilePath, tradingState);
        
        // Poll for fill with optional chaser logic for limit orders
        var orderId = order.OrderId;
        var estimatedCost = totalCost;
        var quotePrice = basePrice; // Capture quote price for slippage calculation
        var preBuyCash = availableForPurchase; // Capture for rollback
        var chaserTimeoutMs = 5000; // 5 second timeout for limit orders before chaser kicks in
        
        _ = Task.Run(async () =>
        {
            try
            {
                var startTime = DateTime.UtcNow;
                long filledQty = 0;
                decimal totalFillCost = 0m;
                bool chaserTriggered = false;
                
                // Standard limit orders - poll every 500ms
                var pollDelayMs = 500;
                var maxIterations = 20; // 10s total
                
                for (int i = 0; i < maxIterations; i++)
                {
                    await Task.Delay(pollDelayMs);
                    var filledOrder = await broker.GetOrderAsync(orderId);
                    
                    if (filledOrder.Status == BotOrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                    {
                        var actualPrice = filledOrder.AverageFillPrice.Value;
                        var actualQty = filledOrder.FilledQuantity;
                        var actualCost = actualQty * actualPrice;
                        var slippage = actualCost - estimatedCost;
                        
                        if (Math.Abs(slippage) > 0.001m)
                        {
                            tradingState.AccumulatedLeftover -= slippage;
                            tradingState.CurrentShares = actualQty;
                            SaveTradingState(stateFilePath, tradingState);
                            var slipLabel = slippage < 0 ? "favorable" : "unfavorable";
                            var slipMessage = $"[FILL] Buy confirmed: {actualQty} @ ${actualPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})";
                            if (slippage < 0) LogSuccess(slipMessage); else LogRed(slipMessage);
                        }
                        else
                        {
                            LogBlue($"[FILL] Buy confirmed: {actualQty} @ ${actualPrice:N4} (slippage: +0.00 neutral)");
                            Log($"[FILL] Buy confirmed: {actualQty} @ ${actualPrice:N4}");
                        }
                        
                        // Track slippage if monitoring enabled
                        if (settings.MonitorSlippage && slippageLock != null && updateSlippage != null)
                        {
                            var tradeSlippage = (quotePrice - actualPrice) * actualQty;
                            lock (slippageLock)
                            {
                                updateSlippage(tradeSlippage);
                            }
                            
                            if (slippageLogFile != null)
                            {
                                var favor = Math.Sign(tradeSlippage);
                                var csvLine = $"{DateTime.UtcNow:s},{symbol},Buy,{actualQty},{quotePrice:F4},{actualPrice:F4},{tradeSlippage:F2},{favor}";
                                _ = File.AppendAllTextAsync(slippageLogFile, csvLine + Environment.NewLine);
                            }
                        }
                        return;
                    }
                    else if (filledOrder.Status == BotOrderStatus.Canceled || 
                             filledOrder.Status == BotOrderStatus.Expired ||
                             filledOrder.Status == BotOrderStatus.Rejected)
                    {
                        // Order cancelled/rejected - but check for PARTIAL FILL first!
                        var partialQty = filledOrder.FilledQuantity;
                        var partialPrice = filledOrder.AverageFillPrice ?? basePrice;
                        
                        if (partialQty > 0)
                        {
                            // CRITICAL: Partial fill occurred - we OWN these shares on the broker!
                            // Do NOT rollback to pre-buy state - that would orphan the shares
                            var partialCost = partialQty * partialPrice;
                            var remainingCash = preBuyCash - partialCost;
                            
                            LogWarning($"[FILL] Buy order {filledOrder.Status} with PARTIAL FILL: {partialQty}/{quantity} @ ${partialPrice:N4}");
                            tradingState.AvailableCash = remainingCash;
                            tradingState.AccumulatedLeftover = 0m;
                            tradingState.CurrentPosition = symbol;
                            tradingState.CurrentShares = partialQty;
                            SaveTradingState(stateFilePath, tradingState);
                            LogWarning($"[FILL] State updated for partial: {partialQty} shares of {symbol}, ${remainingCash:N2} cash");
                        }
                        else
                        {
                            // No fill at all - safe to rollback completely
                            LogError($"[FILL] Buy order {filledOrder.Status} - rolling back state");
                            tradingState.AvailableCash = preBuyCash;
                            tradingState.AccumulatedLeftover = 0m;
                            tradingState.CurrentPosition = null;
                            tradingState.CurrentShares = 0;
                            SaveTradingState(stateFilePath, tradingState);
                            LogError($"[FILL] State rolled back: ${preBuyCash:N2} cash restored, no position");
                        }
                        return;
                    }
                    else if (useLimit && !chaserTriggered && (DateTime.UtcNow - startTime).TotalMilliseconds >= chaserTimeoutMs)
                    {
                        // SMART CHASER LOGIC FOR ENTRIES
                        // Priority: VALUE / SIGNAL VALIDITY
                        // If price runs away, don't blindly chase - re-evaluate first
                        chaserTriggered = true;
                        filledQty = filledOrder.FilledQuantity;
                        
                        // Cancel the limit order first
                        LogWarning($"[EXECUTION] Buy limit order timed out after {chaserTimeoutMs}ms. Evaluating chase...");
                        try
                        {
                            await broker.CancelOrderAsync(orderId);
                            Log($"    Limit order cancelled.");
                        }
                        catch (Exception cancelEx)
                        {
                            // Order may have filled while we were trying to cancel
                            if (cancelEx.Message.Contains("filled", StringComparison.OrdinalIgnoreCase) ||
                                cancelEx.Message.Contains("cannot be cancelled", StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"    Order already filled during cancel attempt.");
                                continue; // Next iteration will pick up the fill
                            }
                            LogError($"    Cancel error: {cancelEx.Message}");
                        }
                        
                        // Wait for cancel confirmation
                        await Task.Delay(500);
                        var cancelledOrder = await broker.GetOrderAsync(orderId);
                        filledQty = cancelledOrder.FilledQuantity;
                        
                        if (cancelledOrder.AverageFillPrice.HasValue && filledQty > 0)
                        {
                            totalFillCost = filledQty * cancelledOrder.AverageFillPrice.Value;
                        }
                        
                        var remainingQty = quantity - filledQty;
                        
                        if (remainingQty > 0)
                        {
                            // SMART ENTRY LOGIC: Check if price has moved too far
                            decimal currentPrice = getCurrentPrice?.Invoke() ?? 0m;
                            
                            // Fallback to broker if stream price unavailable
                            if (currentPrice <= 0m)
                            {
                                try
                                {
                                    currentPrice = await broker.GetLatestPriceAsync(symbol);
                                }
                                catch { currentPrice = quotePrice; } // Use original if API fails
                            }
                            
                            var percentMove = Math.Abs((currentPrice - quotePrice) / quotePrice);
                            
                            if (percentMove < settings.MaxChaseDeviationPercent)
                            {
                                // Price is stable - chase with market order
                                LogWarning($"[EXECUTION] Price moved {percentMove:P2} (< {settings.MaxChaseDeviationPercent:P2}). CHASING with Market Order.");
                                
                                var chaserRequest = new BotOrderRequest
                                {
                                    Symbol = symbol,
                                    Quantity = remainingQty,
                                    Side = BotOrderSide.Buy,
                                    Type = BotOrderType.Market,
                                    TimeInForce = BotTimeInForce.Day,
                                    ClientOrderId = settings.GenerateClientOrderId()
                                };
                                
                                var chaserResult = await broker.SubmitOrderAsync(chaserRequest);
                                orderId = chaserResult.OrderId; // Track the chaser order now
                                Log($"    Chaser order submitted: {chaserResult.OrderId}");
                            }
                            else
                            {
                                // Price moved too much - ABORT entry, stay in cash (safety)
                                LogWarning($"[ABORT] Price moved {percentMove:P2} (> {settings.MaxChaseDeviationPercent:P2}). Entry aborted - staying in CASH.");
                                Log($"    Original: ${quotePrice:N2} -> Current: ${currentPrice:N2}");
                                
                                // Rollback state - we're staying in cash
                                if (filledQty > 0)
                                {
                                    // Partial fill - keep those shares but adjust state
                                    tradingState.CurrentShares = filledQty;
                                    tradingState.AccumulatedLeftover = preBuyCash - totalFillCost;
                                    SaveTradingState(stateFilePath, tradingState);
                                    Log($"    Keeping {filledQty} shares from partial fill.");
                                }
                                else
                                {
                                    // No fill at all - full rollback to cash
                                    tradingState.AvailableCash = preBuyCash;
                                    tradingState.AccumulatedLeftover = 0m;
                                    tradingState.CurrentPosition = null;
                                    tradingState.CurrentShares = 0;
                                    SaveTradingState(stateFilePath, tradingState);
                                    Log($"    Full rollback: ${preBuyCash:N2} cash restored.");
                                }
                                return; // Exit - don't chase
                            }
                        }
                        else
                        {
                            // All shares filled during cancel - we're done
                            Log($"    All {filledQty} shares filled during cancel.");
                        }
                    }
                }
                Log($"[FILL] Buy fill confirmation timeout - using estimate");
            }
            catch (Exception ex)
            {
                Log($"[FILL] Error polling fill: {ex.Message}");
            }
        });
    }
    else
    {
        // STUCK: Cannot afford any shares - report and quit
        LogError("=== BOT STUCK - INSUFFICIENT FUNDS ===");
        LogError($"Cannot afford even 1 share of {symbol}");
        LogError($"Share price: ${basePrice:N2}");
        LogError($"Available funds: ${availableForPurchase:N2}");
        
        var finalBalance = availableForPurchase;
        var finalPL = finalBalance - tradingState.StartingAmount;
        LogBalanceWithPL(finalBalance, finalPL);
        
        // Save final state
        tradingState.AccumulatedLeftover = availableForPurchase;
        tradingState.AvailableCash = 0m;
        SaveTradingState(stateFilePath, tradingState);
        
        LogError("Bot shutting down. Please add funds or adjust StartingAmount to continue.");
        Environment.Exit(1); 
    }
}

bool IsMarketOpen(DateTime easternTime)
{
    // Check if weekend
    if (easternTime.DayOfWeek == DayOfWeek.Saturday || 
        easternTime.DayOfWeek == DayOfWeek.Sunday)
    {
        return false;
    }

    // Market hours: 9:30 AM - 4:00 PM ET
    var marketOpen = new TimeSpan(9, 30, 0);
    var marketClose = new TimeSpan(16, 0, 0);
    var currentTime = easternTime.TimeOfDay;

    if (currentTime < marketOpen || currentTime >= marketClose)
    {
        return false;
    }

    // Note: This does not check for market holidays.
    // For production, use the Alpaca Calendar API.
    return true;
}

// ============================================================================
// LOGGING HELPERS
// ============================================================================
void Log(string message)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
}

void LogError(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}");
    Console.ResetColor();
}

void LogWarning(string message)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARNING: {message}");
    Console.ResetColor();
}

void LogSuccess(string message)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    Console.ResetColor();
}

void LogRed(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    Console.ResetColor();
}

void LogBlue(string message)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    Console.ResetColor();
}

void LogBalanceWithPL(decimal balance, decimal profitLoss, decimal? slippage = null)
{
    var timestamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]";
    var balanceText = $"Balance: ${balance:N2}";
    var plSign = profitLoss >= 0 ? "+" : "";
    var plText = $"[{plSign}{profitLoss:N2}]";
    
    Console.Write($"{timestamp} {balanceText} ");
    
    if (profitLoss > 0)
        Console.ForegroundColor = ConsoleColor.Green;
    else if (profitLoss < 0)
        Console.ForegroundColor = ConsoleColor.Red;
    else
        Console.ForegroundColor = ConsoleColor.Yellow;
    
    Console.Write(plText);
    Console.ResetColor();
    
    // Display cumulative slippage if provided
    if (slippage.HasValue)
    {
        var slipSign = slippage.Value >= 0 ? "+" : "";
        Console.Write(" | Slip: ");
        
        if (slippage.Value > 0)
            Console.ForegroundColor = ConsoleColor.Green;
        else if (slippage.Value < 0)
            Console.ForegroundColor = ConsoleColor.Red;
        
        Console.Write($"{slipSign}${slippage.Value:N2}");
        Console.ResetColor();
    }
    
    Console.WriteLine();
}

// ============================================================================
// STATE MANAGEMENT
// ============================================================================
TradingState LoadTradingState(string filePath)
{
    try
    {
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<TradingState>(json) ?? new TradingState();
        }
    }
    catch (Exception ex)
    {
        LogError($"Failed to load trading state: {ex.Message}");
    }
    return new TradingState();
}

void SaveTradingState(string filePath, TradingState state)
{
    try
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(state, options);
        File.WriteAllText(filePath, json);
    }
    catch (Exception ex)
    {
        LogError($"Failed to save trading state: {ex.Message}");
    }
}

// ============================================================================
// HIGH-PERFORMANCE DATA STRUCTURES
// ============================================================================

// Marker class for user secrets
partial class Program { }
