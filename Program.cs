using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Alpaca.Markets;
using CsvHelper;
using Microsoft.Extensions.Configuration;
using qqqBot;

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

// Parse command line overrides
var cmdOverrides = ParseCommandLineOverrides(args);
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
CommandLineOverrides? ParseCommandLineOverrides(string[] args)
{
    var overrides = new CommandLineOverrides();
    
    foreach (var arg in args)
    {
        if (arg.StartsWith("-bull=", StringComparison.OrdinalIgnoreCase))
        {
            overrides.BullTicker = arg.Substring("-bull=".Length).Trim().ToUpperInvariant();
        }
        else if (arg.StartsWith("-bear=", StringComparison.OrdinalIgnoreCase))
        {
            overrides.BearTicker = arg.Substring("-bear=".Length).Trim().ToUpperInvariant();
        }
        else if (arg.StartsWith("-benchmark=", StringComparison.OrdinalIgnoreCase))
        {
            overrides.BenchmarkTicker = arg.Substring("-benchmark=".Length).Trim().ToUpperInvariant();
        }
        else if (arg.Equals("-usebtc", StringComparison.OrdinalIgnoreCase))
        {
            overrides.UseBtcEarlyTrading = true;
        }
        else if (arg.StartsWith("-neutralwait=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg.Substring("-neutralwait=".Length).Trim();
            if (int.TryParse(value, out var seconds) && seconds > 0)
            {
                overrides.NeutralWaitSecondsOverride = seconds;
            }
            else
            {
                LogError($"-neutralwait must be a positive integer. Got: {value}");
                return null;
            }
        }
        else if (arg.Equals("-watchbtc", StringComparison.OrdinalIgnoreCase))
        {
            overrides.WatchBtc = true;
        }
        else if (arg.StartsWith("-minchop=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg.Substring("-minchop=".Length).Trim();
            if (decimal.TryParse(value, out var dollars) && dollars >= 0)
            {
                overrides.MinChopAbsoluteOverride = dollars;
            }
            else
            {
                LogError($"-minchop must be a non-negative number. Got: {value}");
                return null;
            }
        }
        else if (arg.StartsWith("-botid=", StringComparison.OrdinalIgnoreCase))
        {
            overrides.BotIdOverride = arg.Substring("-botid=".Length).Trim();
        }
        else if (arg.Equals("-monitor", StringComparison.OrdinalIgnoreCase))
        {
            overrides.MonitorSlippage = true;
        }
        else if (arg.StartsWith("-trail=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg.Substring("-trail=".Length).Trim();
            if (decimal.TryParse(value, out var pct) && pct >= 0)
            {
                // Convert user input (e.g., 0.2 for 0.2%) to decimal (0.002)
                overrides.TrailingStopPercentOverride = pct / 100m;
            }
            else
            {
                LogError($"-trail must be a non-negative number (percent). Got: {value}");
                return null;
            }
        }
        else if (arg.Equals("-limit", StringComparison.OrdinalIgnoreCase))
        {
            overrides.UseMarketableLimits = true;
        }
        else if (arg.StartsWith("-maxslip=", StringComparison.OrdinalIgnoreCase))
        {
            var value = arg.Substring("-maxslip=".Length).Trim();
            if (decimal.TryParse(value, out var pct) && pct >= 0)
            {
                // Convert user input (e.g., 0.2 for 0.2%) to decimal (0.002)
                overrides.MaxSlippagePercentOverride = pct / 100m;
            }
            else
            {
                LogError($"-maxslip must be a non-negative number (percent). Got: {value}");
                return null;
            }
        }
        else if (arg.Equals("-lowlatency", StringComparison.OrdinalIgnoreCase))
        {
            overrides.LowLatencyMode = true;
        }
        else if (arg.Equals("-ioc", StringComparison.OrdinalIgnoreCase))
        {
            overrides.UseIocOrders = true;
        }
    }
    
    // Validation: -bear may not be specified without -bull
    if (!string.IsNullOrEmpty(overrides.BearTicker) && string.IsNullOrEmpty(overrides.BullTicker))
    {
        LogError("-bear may not be specified without -bull. Please specify -bull=TICKER or remove -bear.");
        return null;
    }
    
    // If only -bull is specified, use bull ticker as both benchmark and bull (neutral/bear -> cash)
    if (!string.IsNullOrEmpty(overrides.BullTicker) && string.IsNullOrEmpty(overrides.BearTicker))
    {
        overrides.BenchmarkTicker ??= overrides.BullTicker;
        overrides.BullOnlyMode = true;
    }
    
    // If only -benchmark is specified, use it as both benchmark and bull (neutral/bear -> cash)
    if (!string.IsNullOrEmpty(overrides.BenchmarkTicker) && string.IsNullOrEmpty(overrides.BullTicker))
    {
        overrides.BullTicker = overrides.BenchmarkTicker;
        overrides.BullOnlyMode = true;
    }
    
    overrides.HasOverrides = !string.IsNullOrEmpty(overrides.BullTicker) || 
                             !string.IsNullOrEmpty(overrides.BearTicker) || 
                             !string.IsNullOrEmpty(overrides.BenchmarkTicker);
    
    return overrides;
}

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
    
    // Streaming clients for real-time data (initialized later, after settings are loaded)
    IAlpacaDataStreamingClient? stockStreamClient = null;
    IAlpacaCryptoStreamingClient? cryptoStreamClient = null;
    
    // Shared streaming state (thread-safe access via lock)
    var streamLock = new object();
    decimal _latestBenchmarkPrice = 0m;
    decimal _latestBtcPrice = 0m;
    DateTime _lastBenchmarkUpdate = DateTime.MinValue;
    DateTime _lastBtcUpdate = DateTime.MinValue;
    bool _streamConnected = false;
    
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
            if (!await ValidateTickerAsync(ticker, tradingClient))
            {
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
        await LiquidateConfiguredPositionsAsync(configSettings, tradingState, tradingClient, dataClient, stateFilePath);
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
    Log($"  Neutral Wait: {settings.NeutralWaitSeconds}s");
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
    
    // Track subscriptions for reconnection
    IAlpacaDataSubscription<ITrade>? benchmarkSubscription = null;
    IAlpacaDataSubscription<ITrade>? btcSubscription = null;
    
    // Reconnection state
    bool _stockStreamReconnecting = false;
    bool _cryptoStreamReconnecting = false;
    DateTime _lastStockReconnectAttempt = DateTime.MinValue;
    DateTime _lastCryptoReconnectAttempt = DateTime.MinValue;
    const int RECONNECT_COOLDOWN_SECONDS = 30; // Don't retry reconnection more than once per 30s
    
    // Helper: Connect/reconnect stock stream
    async Task ConnectStockStreamAsync()
    {
        if (_stockStreamReconnecting) return;
        
        // Cooldown check - don't spam reconnections
        if (DateTime.UtcNow - _lastStockReconnectAttempt < TimeSpan.FromSeconds(RECONNECT_COOLDOWN_SECONDS))
        {
            return;
        }
        
        _stockStreamReconnecting = true;
        _lastStockReconnectAttempt = DateTime.UtcNow;
        
        try
        {
            // Dispose old client if exists
            if (stockStreamClient != null)
            {
                try { await stockStreamClient.DisconnectAsync(); } catch { }
                try { stockStreamClient.Dispose(); } catch { }
            }
            
            stockStreamClient = Environments.Paper.GetAlpacaDataStreamingClient(secretKey);
            
            // Set up error/disconnect handler
            stockStreamClient.OnError += (ex) =>
            {
                LogError($"[STREAM] Stock stream error: {ex.Message}");
            };
            
            // Subscribe to trade updates for benchmark
            benchmarkSubscription = stockStreamClient.GetTradeSubscription(settings.BenchmarkSymbol);
            benchmarkSubscription.Received += (trade) =>
            {
                // Always update the locked variable (for polling mode fallback)
                lock (streamLock)
                {
                    _latestBenchmarkPrice = trade.Price;
                    _lastBenchmarkUpdate = DateTime.UtcNow;
                }
                
                // LOW-LATENCY MODE: Also write to channel for reactive pipeline
                if (settings.LowLatencyMode && Volatile.Read(ref _lowLatencyActive))
                {
                    // TryWrite is non-blocking - if channel is full, DropOldest kicks in
                    tradeChannel.Writer.TryWrite(new TradeTick 
                    { 
                        Price = trade.Price, 
                        Timestamp = DateTime.UtcNow,
                        IsBenchmark = true 
                    });
                }
            };
            
            await stockStreamClient.ConnectAndAuthenticateAsync();
            await stockStreamClient.SubscribeAsync(benchmarkSubscription);
            
            // Reset the staleness timer to give new connection time to receive data
            lock (streamLock)
            {
                _lastBenchmarkUpdate = DateTime.UtcNow;
            }
            
            Log($"  ✓ Stock stream connected: {settings.BenchmarkSymbol}");
        }
        catch (Exception ex)
        {
            LogError($"[STREAM] Failed to connect stock stream: {ex.Message}");
        }
        finally
        {
            _stockStreamReconnecting = false;
        }
    }
    
    // Helper: Connect/reconnect crypto stream
    async Task ConnectCryptoStreamAsync()
    {
        if (_cryptoStreamReconnecting) return;
        
        // Cooldown check - don't spam reconnections
        if (DateTime.UtcNow - _lastCryptoReconnectAttempt < TimeSpan.FromSeconds(RECONNECT_COOLDOWN_SECONDS))
        {
            return;
        }
        
        _cryptoStreamReconnecting = true;
        _lastCryptoReconnectAttempt = DateTime.UtcNow;
        
        try
        {
            // Dispose old client if exists
            if (cryptoStreamClient != null)
            {
                try { await cryptoStreamClient.DisconnectAsync(); } catch { }
                try { cryptoStreamClient.Dispose(); } catch { }
            }
            
            cryptoStreamClient = Environments.Paper.GetAlpacaCryptoStreamingClient(secretKey);
            
            // Set up error handler
            cryptoStreamClient.OnError += (ex) =>
            {
                LogError($"[STREAM] Crypto stream error: {ex.Message}");
            };
            
            btcSubscription = cryptoStreamClient.GetTradeSubscription(settings.CryptoBenchmarkSymbol);
            btcSubscription.Received += (trade) =>
            {
                // Always update the locked variable (for polling mode fallback)
                lock (streamLock)
                {
                    _latestBtcPrice = trade.Price;
                    _lastBtcUpdate = DateTime.UtcNow;
                }
                
                // LOW-LATENCY MODE: Also write to channel for reactive pipeline (if using BTC for early trading)
                if (settings.LowLatencyMode && Volatile.Read(ref _lowLatencyActive) && settings.UseBtcEarlyTrading)
                {
                    tradeChannel.Writer.TryWrite(new TradeTick 
                    { 
                        Price = trade.Price, 
                        Timestamp = DateTime.UtcNow,
                        IsBenchmark = false  // BTC, not benchmark
                    });
                }
            };
            
            await cryptoStreamClient.ConnectAndAuthenticateAsync();
            await cryptoStreamClient.SubscribeAsync(btcSubscription);
            
            // Reset the staleness timer to give new connection time to receive data
            lock (streamLock)
            {
                _lastBtcUpdate = DateTime.UtcNow;
            }
            
            Log($"  ✓ Crypto stream connected: {settings.CryptoBenchmarkSymbol}");
        }
        catch (Exception ex)
        {
            LogError($"[STREAM] Failed to connect crypto stream: {ex.Message}");
        }
        finally
        {
            _cryptoStreamReconnecting = false;
        }
    }
    
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
                    
                    // Only log if latency is concerning (>50ms)
                    if (sw.ElapsedMilliseconds > 50)
                    {
                        Log($"[KEEP-ALIVE] Ping: {sw.ElapsedMilliseconds}ms (elevated)");
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
    
    // Track if streams have been started this session
    bool _streamsStarted = false;

    Log("=== Trading Bot Active ===\n");

    // Initialize Rolling SMA Engine
    var priceQueue = new Queue<decimal>();
    var smaSeeded = false;

    // BTC Correlation Queue (for -watchbtc mode)
    var btcQueue = new Queue<decimal>();

    // Track last market closed notification to avoid spam
    DateTime? lastMarketClosedLog = null;
    const int marketClosedLogIntervalMinutes = 30;

    // Track neutral state duration
    DateTime? neutralDetectionTime = null;
    
    // Virtual trailing stop state (for protecting profits during intra-trend pullbacks)
    // Works for both BULL (tracks high water mark) and BEAR (tracks low water mark)
    decimal _highWaterMark = 0m;     // Highest benchmark price since BULL entry
    decimal _lowWaterMark = 0m;      // Lowest benchmark price since BEAR entry
    decimal _virtualStopPrice = 0m;  // Stop trigger level
    bool _isStoppedOut = false;       // Washout latch engaged
    string _stoppedOutDirection = ""; // "BULL" or "BEAR" - which direction was stopped out
    decimal _washoutLevel = 0m;      // Price required to re-enter after stop-out
    DateTime? _stopoutTime = null;   // Time of stop-out (for cooldown)
    
    // Logging state
    DateTime lastLogTime = DateTime.MinValue;
    string lastSignal = string.Empty;
    
    // Slippage callback for passing to helper functions
    Action<decimal>? slippageCallback = settings.MonitorSlippage 
        ? (delta) => { _cumulativeSlippage += delta; }
        : null;

    // =========================================================================
    // LOW-LATENCY MODE: Reactive Pipeline Consumer
    // =========================================================================
    // This method runs instead of the polling loop when LowLatencyMode is enabled.
    // It consumes trades from the channel and executes strategy with O(1) SMA.
    async Task RunReactivePipelineAsync(CancellationToken ct)
    {
        Log("\n[LOW-LATENCY] Starting reactive pipeline consumer...");
        Log("[LOW-LATENCY] Processing trades as they arrive (no polling delay).\n");
        
        // Local state for the pipeline
        DateTime? pipelineNeutralDetectionTime = null;
        DateTime pipelineLastLogTime = DateTime.MinValue;
        string pipelineLastSignal = string.Empty;
        
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
        
        // Log restored state if any
        if (pipelineHwm > 0 || pipelineLwm > 0)
        {
            Log($"[LOW-LATENCY] Restored trailing stop state from disk:");
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
                    
                    // Skip if market closed (shouldn't happen but safety check)
                    if (!IsMarketOpen(easternNow))
                    {
                        continue;
                    }
                    
                    // For early trading, only process BTC ticks; otherwise only benchmark ticks
                    var earlyTradingEnd = new TimeSpan(9, 55, 0);
                    var usesCrypto = settings.UseBtcEarlyTrading && easternNow.TimeOfDay < earlyTradingEnd;
                    
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
                    decimal percentageWidth = currentSma * settings.ChopThresholdPercent;
                    decimal effectiveWidth = Math.Max(percentageWidth, settings.MinChopAbsolute);
                    var upperBand = currentSma + effectiveWidth;
                    var lowerBand = currentSma - effectiveWidth;
                    
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
                        
                        decimal btcPrice;
                        lock (streamLock) { btcPrice = _latestBtcPrice; }
                        
                        if (btcPrice > btcUpperBand)
                            finalSignal = "BULL";
                        else if (btcPrice < btcLowerBand)
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
                        lock (streamLock) { return _latestBenchmarkPrice > 0 ? _latestBenchmarkPrice : currentPrice; }
                    };
                    
                    // Execute trading logic based on signal
                    // STATE-AWARE GUARDS: Only call expensive async methods if action is needed
                    // This prevents API spam (60+ ListPositionsAsync calls per minute)
                    if (finalSignal == "MARKET_CLOSE")
                    {
                        // Only liquidate if we actually have a position
                        if (tradingState.CurrentPosition != null && tradingState.CurrentShares > 0)
                        {
                            await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, 
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
                            await EnsurePositionAsync(settings.BullSymbol, settings.BearSymbol, tradingState, tradingClient, 
                                dataClient, stateFilePath, settings, slippageLock, slippageCallback, _slippageLogFile, 
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
                                await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, 
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
                                await EnsurePositionAsync(settings.BearSymbol!, settings.BullSymbol, tradingState, tradingClient, 
                                    dataClient, stateFilePath, settings, slippageLock, slippageCallback, _slippageLogFile, 
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
                                await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, 
                                    showStatus: shouldLog, slippageLock: slippageLock, updateSlippage: slippageCallback, 
                                    slippageLogFile: _slippageLogFile, getSignalAtPrice: signalChecker);
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
                    LogError($"[LOW-LATENCY] Error processing tick: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("[LOW-LATENCY] Pipeline shutdown requested.");
        }
        catch (Exception ex)
        {
            LogError($"[LOW-LATENCY] Pipeline error: {ex.Message}");
        }
        
        Log("[LOW-LATENCY] Reactive pipeline stopped.");
    }

    // Main trading loop
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var easternNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternZone);

            // Start streams 2 minutes before market open (9:28 AM ET)
            if (!_streamsStarted && ShouldStartStreams(easternNow))
            {
                try
                {
                    Log("Starting real-time data streams (2 minutes before market open)...");
                    await ConnectStockStreamAsync();
                    await ConnectCryptoStreamAsync();
                    _streamConnected = true;
                    _streamsStarted = true;
                    Log("Real-time data streams ready.\n");
                    
                    // LOW-LATENCY MODE: Run warm-up routines before market open
                    if (settings.LowLatencyMode && !_warmedUp)
                    {
                        Log("\n[LOW-LATENCY MODE] Running pre-market warm-up...");
                        
                        // 1. JIT compile strategy logic
                        WarmUpStrategyLogic(settings.WarmUpIterations);
                        
                        // 2. Prime HTTP connection pool
                        await WarmUpConnectionsAsync();
                        
                        // 3. Start keep-alive pinger
                        StartKeepAlivePinger();
                        
                        _warmedUp = true;
                        Log("[LOW-LATENCY MODE] Warm-up complete. Ready for market open.\n");
                        
                        // 4. Seed the IncrementalSma with historical data
                        Log("[LOW-LATENCY] Seeding IncrementalSma with historical data...");
                        await SeedIncrementalSmaAsync(settings, dataClient, cryptoDataClient, incrementalSma, incrementalBtcSma, easternZone);
                        Log($"[LOW-LATENCY] IncrementalSma initialized with {incrementalSma.Count} data points.\n");
                    }
                    
                    // LOW-LATENCY MODE: Switch to reactive pipeline
                    if (settings.LowLatencyMode && _warmedUp)
                    {
                        Log("[LOW-LATENCY] Switching to reactive pipeline mode...");
                        Volatile.Write(ref _lowLatencyActive, true); // Enable channel writes in stream handlers (thread-safe)
                        
                        // Run the reactive pipeline (blocks until cancelled)
                        await RunReactivePipelineAsync(cancellationToken);
                        
                        // If we get here, pipeline was cancelled - exit the main loop
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Failed to initialize streaming: {ex.Message}");
                    LogError("Falling back to polling mode...\n");
                    _streamConnected = false;
                    _streamsStarted = true; // Don't retry repeatedly
                }
            }

            // Check if market is open
            if (!IsMarketOpen(easternNow))
            {
                // Only log market closed message every 30 minutes to reduce noise
                if (lastMarketClosedLog == null || 
                    (DateTime.Now - lastMarketClosedLog.Value).TotalMinutes >= marketClosedLogIntervalMinutes)
                {
                    Log($"Market Closed. Waiting for open... (ET: {easternNow:HH:mm:ss})");
                    lastMarketClosedLog = DateTime.Now;
                }
                
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break; // Exit loop on shutdown
                }
                continue;
            }
            
            // Reset the closed log tracker when market opens
            lastMarketClosedLog = null;
            
            // Seed the Rolling SMA Queue once when market opens
            if (!smaSeeded)
            {
                await SeedRollingSmaAsync(settings, dataClient, cryptoDataClient, priceQueue, btcQueue, easternZone);
                Log($"Rolling SMA Engine Initialized with {priceQueue.Count} data points.");
                smaSeeded = true;
            }
            
            // Track day start balance for daily P/L calculation
            var todayDateStr = easternNow.ToString("yyyy-MM-dd");
            if (tradingState.DayStartDate != todayDateStr)
            {
                var currentBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
                tradingState.DayStartBalance = currentBalance;
                tradingState.DayStartDate = todayDateStr;
                SaveTradingState(stateFilePath, tradingState);
                Log($"New trading day detected. Day start balance: ${currentBalance:N2}");
            }
            
            // Determine which benchmark to use based on time
            // Use BTC/USD from 9:30-9:55 AM (before QQQ has enough bars for SMA)
            // Only use BTC/USD if enabled (default for config, disabled for CLI overrides unless -usebtc)
            var earlyTradingEnd = new TimeSpan(9, 55, 0);
            var usesCrypto = settings.UseBtcEarlyTrading && easternNow.TimeOfDay < earlyTradingEnd;
            var benchmarkSymbol = usesCrypto ? settings.CryptoBenchmarkSymbol : settings.BenchmarkSymbol;
            
            decimal currentPrice = 0m;
            DateTime lastUpdate;
            
            // Read from streaming data (zero-latency memory read)
            if (_streamConnected)
            {
                lock (streamLock)
                {
                    if (usesCrypto)
                    {
                        currentPrice = _latestBtcPrice;
                        lastUpdate = _lastBtcUpdate;
                    }
                    else
                    {
                        currentPrice = _latestBenchmarkPrice;
                        lastUpdate = _lastBenchmarkUpdate;
                    }
                }
                
                // Staleness check - if no update in 15 seconds, stream may be disconnected
                // Only log and attempt reconnection if we're not in cooldown period
                if (currentPrice > 0 && DateTime.UtcNow - lastUpdate > TimeSpan.FromSeconds(15))
                {
                    var cooldownActive = usesCrypto 
                        ? DateTime.UtcNow - _lastCryptoReconnectAttempt < TimeSpan.FromSeconds(RECONNECT_COOLDOWN_SECONDS)
                        : DateTime.UtcNow - _lastStockReconnectAttempt < TimeSpan.FromSeconds(RECONNECT_COOLDOWN_SECONDS);
                    
                    if (!cooldownActive)
                    {
                        var staleSec = (DateTime.UtcNow - lastUpdate).TotalSeconds;
                        Log($"[WARN] Data stream stale ({staleSec:F1}s). Triggering reconnection...");
                        
                        // Trigger reconnection in background (don't await - non-blocking)
                        if (usesCrypto)
                        {
                            _ = ConnectCryptoStreamAsync();
                        }
                        else
                        {
                            _ = ConnectStockStreamAsync();
                        }
                    }
                    
                    currentPrice = 0m; // Force HTTP fallback for this iteration
                }
            }
            
            // Fallback to HTTP polling if stream unavailable or stale
            if (currentPrice == 0m)
            {
                if (usesCrypto)
                {
                    var latestTrades = await cryptoDataClient.ListLatestTradesAsync(
                        new LatestDataListRequest([benchmarkSymbol]));
                    currentPrice = latestTrades[benchmarkSymbol].Price;
                }
                else
                {
                    var quoteRequest = new LatestMarketDataRequest(benchmarkSymbol)
                    {
                        Feed = MarketDataFeed.Iex
                    };
                    var latestTrade = await dataClient.GetLatestTradeAsync(quoteRequest);
                    currentPrice = latestTrade.Price;
                }
            }

            // Update Rolling SMA Queue
            priceQueue.Enqueue(currentPrice);
            if (priceQueue.Count > settings.SMALength)
            {
                priceQueue.Dequeue();
            }

            var currentSma = priceQueue.Average();

            // Calculate Hysteresis Bands (with absolute floor for low-priced stocks)
            decimal percentageWidth = currentSma * settings.ChopThresholdPercent;
            decimal effectiveWidth = Math.Max(percentageWidth, settings.MinChopAbsolute);
            var upperBand = currentSma + effectiveWidth;
            var lowerBand = currentSma - effectiveWidth;

            // Determine Primary Signal (from benchmark)
            string signal;
            var timeOfDay = easternNow.TimeOfDay;
            // Force liquidation 2 minutes before market close (3:58 PM ET)
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

            // BTC Correlation Logic (Neutral Nudge / Tie-Breaker)
            string btcSignal = "NEUTRAL";
            string finalSignal = signal;
            
            if (settings.WatchBtc)
            {
                // Read BTC price from stream (zero-latency)
                decimal btcPrice = 0m;
                bool btcStreamStale = false;
                if (_streamConnected)
                {
                    lock (streamLock)
                    {
                        if (_latestBtcPrice > 0)
                        {
                            if (DateTime.UtcNow - _lastBtcUpdate < TimeSpan.FromSeconds(15))
                            {
                                btcPrice = _latestBtcPrice;
                            }
                            else
                            {
                                btcStreamStale = true;
                            }
                        }
                    }
                }
                
                // Trigger reconnection if BTC stream is stale
                if (btcStreamStale)
                {
                    _ = ConnectCryptoStreamAsync();
                }
                
                // Fallback to HTTP if stream unavailable or stale
                if (btcPrice == 0m)
                {
                    var btcTrades = await cryptoDataClient.ListLatestTradesAsync(
                        new LatestDataListRequest([settings.CryptoBenchmarkSymbol]));
                    btcPrice = btcTrades[settings.CryptoBenchmarkSymbol].Price;
                }
                
                // Update BTC SMA Queue (same length as primary)
                btcQueue.Enqueue(btcPrice);
                if (btcQueue.Count > settings.SMALength)
                {
                    btcQueue.Dequeue();
                }
                
                // Calculate BTC signal using same SMA/Band logic
                if (btcQueue.Count >= settings.SMALength)
                {
                    var btcSma = btcQueue.Average();
                    var btcUpperBand = btcSma * (1 + settings.ChopThresholdPercent);
                    var btcLowerBand = btcSma * (1 - settings.ChopThresholdPercent);
                    
                    if (btcPrice > btcUpperBand)
                        btcSignal = "BULL";
                    else if (btcPrice < btcLowerBand)
                        btcSignal = "BEAR";
                    else
                        btcSignal = "NEUTRAL";
                }
                
                // Apply BTC nudge only when primary signal is NEUTRAL
                if (signal == "NEUTRAL" && btcSignal != "NEUTRAL")
                {
                    finalSignal = btcSignal;
                }
            }

            // LOGGING CONTROL (check against finalSignal for state changes)
            bool stateChanged = finalSignal != lastSignal;
            // Use 30s as default log interval
            bool shouldLog = stateChanged || (DateTime.UtcNow - lastLogTime).TotalSeconds >= 30;

            // Log BTC nudge if it occurred
            if (settings.WatchBtc && signal == "NEUTRAL" && finalSignal != "NEUTRAL" && shouldLog)
            {
                Log($"    [BTC NUDGE] Overriding NEUTRAL to {finalSignal} (BTC correlation)");
            }

            if (shouldLog)
            {
                decimal currentBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
                
                // Add market value of held positions to reflect true portfolio value
                if (!string.IsNullOrEmpty(tradingState.CurrentPosition) && tradingState.CurrentShares > 0)
                {
                    try
                    {
                        var position = await tradingClient.GetPositionAsync(tradingState.CurrentPosition);
                        if (position != null)
                        {
                            currentBalance += position.MarketValue ?? 0m;
                        }
                    }
                    catch (Exception)
                    {
                        // If position fetch fails (e.g. API error), we might underreport balance temporarily.
                    }
                }

                // Get cumulative slippage if monitoring enabled
                decimal? slippage = null;
                if (settings.MonitorSlippage)
                {
                    lock (slippageLock)
                    {
                        slippage = _cumulativeSlippage;
                    }
                }
                
                LogBalanceWithPL(currentBalance, currentBalance - settings.StartingAmount, slippage);

                Log($"--- {easternNow:HH:mm:ss} ET | {benchmarkSymbol}: ${currentPrice:N2} | SMA: ${currentSma:N2} ---");
                Log($"    Bands: [${lowerBand:N2} - ${upperBand:N2}]");
                Log($"    Signal: {finalSignal} 🟢🔴⚪{(signal != finalSignal ? $" (was {signal})" : "")}");

                lastLogTime = DateTime.UtcNow;
            }
            
            lastSignal = finalSignal;

            // ====================================================================
            // VIRTUAL TRAILING STOP LOGIC
            // ====================================================================
            // Priority: SMA Flip > Trailing Stop > Normal Strategy
            // When holding BULL: track high water mark, calc stop, trigger on breach
            // After stop-out: latch prevents re-entry until cooldown expires
            
            bool trailingStopTriggered = false;
            bool latchBlocksEntry = false;
            
            if (settings.TrailingStopPercent > 0)
            {
                bool holdingBull = tradingState.CurrentPosition == settings.BullSymbol && tradingState.CurrentShares > 0;
                bool holdingBear = tradingState.CurrentPosition == settings.BearSymbol && tradingState.CurrentShares > 0;
                
                if (holdingBull)
                {
                    // BULL: Track HIGH water mark, stop triggers when price DROPS below threshold
                    if (currentPrice > _highWaterMark || _highWaterMark == 0m)
                    {
                        var oldHwm = _highWaterMark;
                        _highWaterMark = currentPrice;
                        _virtualStopPrice = _highWaterMark * (1m - settings.TrailingStopPercent);
                        
                        if (shouldLog && oldHwm > 0m)
                        {
                            Log($"    [TRAILING STOP] HWM updated: ${oldHwm:N2} -> ${_highWaterMark:N2}, Stop: ${_virtualStopPrice:N2} (trail: {settings.TrailingStopPercent:P2})");
                        }
                        else if (shouldLog)
                        {
                            Log($"    [TRAILING STOP] Initialized: HWM=${_highWaterMark:N2}, Stop=${_virtualStopPrice:N2} (trail: {settings.TrailingStopPercent:P2})");
                        }
                    }
                    
                    // Check if trailing stop is breached (price dropped below stop)
                    if (currentPrice <= _virtualStopPrice)
                    {
                        trailingStopTriggered = true;
                        _isStoppedOut = true;
                        _stoppedOutDirection = "BULL";
                        _washoutLevel = _highWaterMark; // Must recover to peak to re-enter BULL
                        _stopoutTime = DateTime.UtcNow;
                        
                        if (shouldLog)
                        {
                            LogWarning($"[TRAILING STOP] BULL: Price ${currentPrice:N2} breached stop ${_virtualStopPrice:N2} (HWM: ${_highWaterMark:N2})");
                            Log($"    Engaging washout latch - will block BULL re-entry for {settings.StopLossCooldownSeconds}s");
                        }
                    }
                }
                else if (holdingBear)
                {
                    // BEAR: Track LOW water mark, stop triggers when price RISES above threshold
                    if (currentPrice < _lowWaterMark || _lowWaterMark == 0m)
                    {
                        var oldLwm = _lowWaterMark;
                        _lowWaterMark = currentPrice;
                        _virtualStopPrice = _lowWaterMark * (1m + settings.TrailingStopPercent);
                        
                        if (shouldLog && oldLwm > 0m)
                        {
                            Log($"    [TRAILING STOP] LWM updated: ${oldLwm:N2} -> ${_lowWaterMark:N2}, Stop: ${_virtualStopPrice:N2} (trail: {settings.TrailingStopPercent:P2})");
                        }
                        else if (shouldLog)
                        {
                            Log($"    [TRAILING STOP] Initialized: LWM=${_lowWaterMark:N2}, Stop=${_virtualStopPrice:N2} (trail: {settings.TrailingStopPercent:P2})");
                        }
                    }
                    
                    // Check if trailing stop is breached (price rose above stop)
                    if (currentPrice >= _virtualStopPrice)
                    {
                        trailingStopTriggered = true;
                        _isStoppedOut = true;
                        _stoppedOutDirection = "BEAR";
                        _washoutLevel = _lowWaterMark; // Must drop to trough to re-enter BEAR
                        _stopoutTime = DateTime.UtcNow;
                        
                        if (shouldLog)
                        {
                            LogWarning($"[TRAILING STOP] BEAR: Price ${currentPrice:N2} breached stop ${_virtualStopPrice:N2} (LWM: ${_lowWaterMark:N2})");
                            Log($"    Engaging washout latch - will block BEAR re-entry for {settings.StopLossCooldownSeconds}s");
                        }
                    }
                }
                else
                {
                    // Not holding any position - check if washout latch is blocking re-entry
                    if (_isStoppedOut && _stopoutTime.HasValue)
                    {
                        var timeSinceStopout = (DateTime.UtcNow - _stopoutTime.Value).TotalSeconds;
                        bool cooldownExpired = timeSinceStopout >= settings.StopLossCooldownSeconds;
                        
                        // Latch clear conditions depend on which direction was stopped out
                        bool priceRecovered = _stoppedOutDirection == "BULL" 
                            ? currentPrice >= _washoutLevel  // BULL: price must recover to HWM
                            : currentPrice <= _washoutLevel; // BEAR: price must drop to LWM
                        
                        if (cooldownExpired && priceRecovered)
                        {
                            _isStoppedOut = false;
                            _stoppedOutDirection = "";
                            _highWaterMark = 0m;
                            _lowWaterMark = 0m;
                            _virtualStopPrice = 0m;
                            _washoutLevel = 0m;
                            _stopoutTime = null;
                            
                            if (shouldLog) Log($"    [LATCH CLEARED] Price recovered to ${currentPrice:N2} after cooldown");
                        }
                        else
                        {
                            // Latch still active - block entry in the stopped-out direction
                            if (finalSignal == _stoppedOutDirection)
                            {
                                latchBlocksEntry = true;
                                if (shouldLog)
                                {
                                    var remaining = Math.Max(0, settings.StopLossCooldownSeconds - timeSinceStopout);
                                    var needDir = _stoppedOutDirection == "BULL" ? "above" : "below";
                                    Log($"    [LATCH ACTIVE] Blocking {_stoppedOutDirection} entry (cooldown: {remaining:F1}s, need: {needDir} ${_washoutLevel:N2}, have: ${currentPrice:N2})");
                                }
                            }
                        }
                    }
                }
            }
            
            // On successful entry, reset trailing stop state for that direction
            void ResetTrailingStopOnBullEntry()
            {
                if (settings.TrailingStopPercent > 0)
                {
                    _highWaterMark = currentPrice;
                    _lowWaterMark = 0m;
                    _virtualStopPrice = _highWaterMark * (1m - settings.TrailingStopPercent);
                    _isStoppedOut = false;
                    _stoppedOutDirection = "";
                    _washoutLevel = 0m;
                    _stopoutTime = null;
                }
            }
            
            void ResetTrailingStopOnBearEntry()
            {
                if (settings.TrailingStopPercent > 0)
                {
                    _highWaterMark = 0m;
                    _lowWaterMark = currentPrice;
                    _virtualStopPrice = _lowWaterMark * (1m + settings.TrailingStopPercent);
                    _isStoppedOut = false;
                    _stoppedOutDirection = "";
                    _washoutLevel = 0m;
                    _stopoutTime = null;
                }
            }

            // Execute Strategy based on finalSignal
            // Priority: SMA Flip always wins > Trailing Stop > Latch Block > Normal Wait
            
            // TRAILING STOP TRIGGER: Immediate liquidation (overrides signal)
            if (trailingStopTriggered)
            {
                neutralDetectionTime = null;
                await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, reason: "TRAILING STOP HIT", showStatus: shouldLog, slippageLock: slippageLock, updateSlippage: slippageCallback, slippageLogFile: _slippageLogFile);
            }
            else if (finalSignal == "MARKET_CLOSE")
            {
                neutralDetectionTime = null; // Reset neutral timer
                await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, reason: "MARKET CLOSE", showStatus: shouldLog, slippageLock: slippageLock, updateSlippage: slippageCallback, slippageLogFile: _slippageLogFile);
                
                // Shut down streams after end-of-day liquidation
                if (_streamConnected)
                {
                    Log("Shutting down streams for end of day...");
                    if (stockStreamClient != null)
                    {
                        try { await stockStreamClient.DisconnectAsync(); stockStreamClient.Dispose(); stockStreamClient = null; } catch { }
                    }
                    if (cryptoStreamClient != null)
                    {
                        try { await cryptoStreamClient.DisconnectAsync(); cryptoStreamClient.Dispose(); cryptoStreamClient = null; } catch { }
                    }
                    _streamConnected = false;
                    _streamsStarted = false; // Allow restart next trading day
                    Log("Streams disconnected. Waiting for next trading day.\n");
                }
            }
            else if (finalSignal == "BULL")
            {
                // Check if washout latch blocks re-entry
                if (latchBlocksEntry)
                {
                    // Latch active - hold current position (or stay neutral)
                    // Don't reset neutral timer - we're waiting for latch to clear
                }
                else
                {
                    neutralDetectionTime = null; // Reset neutral timer
                    var wasBull = tradingState.CurrentPosition == settings.BullSymbol && tradingState.CurrentShares > 0;
                    var bullHoldInfo = (currentPrice, lowerBand, upperBand, settings.TrailingStopPercent > 0 && _virtualStopPrice > 0 && _highWaterMark > 0 ? _virtualStopPrice : (decimal?)null);
                    
                    // Create price getter for smart chaser logic (reads from stream)
                    Func<decimal> priceGetter = () => {
                        lock (streamLock) { return _latestBenchmarkPrice > 0 ? _latestBenchmarkPrice : currentPrice; }
                    };
                    
                    // Create signal checker for smart exit chaser (V-shaped reversal detection)
                    // Captures current bands to re-evaluate signal at any price
                    Func<decimal, string> signalChecker = (price) => {
                        if (price > upperBand) return "BULL";
                        if (price < lowerBand) return "BEAR";
                        return "NEUTRAL";
                    };
                    
                    await EnsurePositionAsync(settings.BullSymbol, settings.BearSymbol, tradingState, tradingClient, dataClient, stateFilePath, settings, slippageLock, slippageCallback, _slippageLogFile, bullHoldInfo, priceGetter, signalChecker);
                    
                    // If we just entered BULL position, reset trailing stop tracking
                    if (!wasBull && tradingState.CurrentPosition == settings.BullSymbol && tradingState.CurrentShares > 0)
                    {
                        ResetTrailingStopOnBullEntry();
                    }
                }
            }
            else if (finalSignal == "BEAR")
            {
                // Check if washout latch blocks re-entry to BEAR
                if (latchBlocksEntry)
                {
                    // Latch active - hold current position (or stay neutral)
                    // Don't reset neutral timer - we're waiting for latch to clear
                }
                else
                {
                    neutralDetectionTime = null; // Reset neutral timer
                    
                    // In bull-only mode, BEAR signal dumps to cash
                    if (settings.BullOnlyMode)
                    {
                        // Create signal checker for smart exit chaser
                        Func<decimal, string> signalChecker = (price) => {
                            if (price > upperBand) return "BULL";
                            if (price < lowerBand) return "BEAR";
                            return "NEUTRAL";
                        };
                        await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, reason: "BEAR (bull-only mode)", showStatus: shouldLog, slippageLock: slippageLock, updateSlippage: slippageCallback, slippageLogFile: _slippageLogFile, getSignalAtPrice: signalChecker);
                    }
                    else
                    {
                        var wasBear = tradingState.CurrentPosition == settings.BearSymbol && tradingState.CurrentShares > 0;
                        var bearHoldInfo = (currentPrice, lowerBand, upperBand, settings.TrailingStopPercent > 0 && _virtualStopPrice > 0 && _lowWaterMark > 0 ? _virtualStopPrice : (decimal?)null);
                        
                        // Create price getter for smart chaser logic (reads from stream)
                        Func<decimal> priceGetter = () => {
                            lock (streamLock) { return _latestBenchmarkPrice > 0 ? _latestBenchmarkPrice : currentPrice; }
                        };
                        
                        // Create signal checker for smart exit chaser (V-shaped reversal detection)
                        Func<decimal, string> signalChecker = (price) => {
                            if (price > upperBand) return "BULL";
                            if (price < lowerBand) return "BEAR";
                            return "NEUTRAL";
                        };
                        
                        await EnsurePositionAsync(settings.BearSymbol!, settings.BullSymbol, tradingState, tradingClient, dataClient, stateFilePath, settings, slippageLock, slippageCallback, _slippageLogFile, bearHoldInfo, priceGetter, signalChecker);
                        
                        // If we just entered BEAR position, reset trailing stop tracking
                        if (!wasBear && tradingState.CurrentPosition == settings.BearSymbol && tradingState.CurrentShares > 0)
                        {
                            ResetTrailingStopOnBearEntry();
                        }
                    }
                }
            }
            else // NEUTRAL (true chop - BTC didn't nudge or WatchBtc is off)
            {
                if (neutralDetectionTime == null)
                {
                    neutralDetectionTime = DateTime.UtcNow;
                    if (shouldLog) Log($"    [NEUTRAL DETECTED] Waiting {settings.NeutralWaitSeconds}s for confirmation...");
                }

                var elapsed = (DateTime.UtcNow - neutralDetectionTime.Value).TotalSeconds;
                if (elapsed >= settings.NeutralWaitSeconds)
                {
                        // Create signal checker for smart exit chaser
                        Func<decimal, string> signalChecker = (price) => {
                            if (price > upperBand) return "BULL";
                            if (price < lowerBand) return "BEAR";
                            return "NEUTRAL";
                        };
                        await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, showStatus: shouldLog, slippageLock: slippageLock, updateSlippage: slippageCallback, slippageLogFile: _slippageLogFile, getSignalAtPrice: signalChecker);
                }
                else
                {
                        if (shouldLog) Log($"    [NEUTRAL PENDING] Waiting... ({elapsed:F1}/{settings.NeutralWaitSeconds}s). Holding positions.");
                }
            }
            
            if (shouldLog) Log("");
        }
        catch (Exception ex)
        {
            LogError($"Error in trading loop: {ex.Message}");
            LogError($"Stack trace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                LogError($"Inner exception: {ex.InnerException.Message}");
            }
            Log("Continuing after error...\n");
        }

        try
        {
            // Wait for next poll (Micro-polling) - use cancellation token
            await Task.Delay(TimeSpan.FromSeconds(settings.PollingIntervalSeconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested - exit loop gracefully
            break;
        }
        catch (Exception delayEx)
        {
            LogError($"Error during delay: {delayEx.Message}");
        }
    }
    
    // =========================================================================
    // GRACEFUL SHUTDOWN - Liquidate positions before exit
    // =========================================================================
    Log("\n=== Graceful Shutdown ===");
    
    if (!string.IsNullOrEmpty(tradingState.CurrentPosition) && tradingState.CurrentShares > 0)
    {
        Log($"Attempting to liquidate {tradingState.CurrentShares} shares of {tradingState.CurrentPosition}...");
        try
        {
            var positions = await tradingClient.ListPositionsAsync();
            var currentPosition = positions.FirstOrDefault(p => 
                p.Symbol.Equals(tradingState.CurrentPosition, StringComparison.OrdinalIgnoreCase));
            
            var liquidated = await LiquidatePositionAsync(
                tradingState.CurrentPosition, 
                currentPosition, 
                tradingState, 
                tradingClient, 
                dataClient, 
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
    
    // Dispose streaming clients
    if (stockStreamClient != null)
    {
        try
        {
            await stockStreamClient.DisconnectAsync();
            stockStreamClient.Dispose();
            Log("Stock stream disconnected.");
        }
        catch (Exception ex)
        {
            Log($"Warning: Error disconnecting stock stream: {ex.Message}");
        }
    }
    
    if (cryptoStreamClient != null)
    {
        try
        {
            await cryptoStreamClient.DisconnectAsync();
            cryptoStreamClient.Dispose();
            Log("Crypto stream disconnected.");
        }
        catch (Exception ex)
        {
            Log($"Warning: Error disconnecting crypto stream: {ex.Message}");
        }
    }
    
    // Stop keep-alive pinger (low-latency mode)
    StopKeepAlivePinger();
    
    Log("Bot shutdown complete. Final state saved.");
    Log($"Final Balance: ${tradingState.AvailableCash + tradingState.AccumulatedLeftover:N2}");
}

// Validate ticker symbol using GetAssetAsync and checking IsTradable
async Task<bool> ValidateTickerAsync(string ticker, IAlpacaTradingClient tradingClient)
{
    try
    {
        var asset = await tradingClient.GetAssetAsync(ticker);
        if (asset == null)
        {
            LogError($"[Error] Invalid Ticker: {ticker} - Asset not found");
            return false;
        }
        if (!asset.IsTradable)
        {
            LogError($"[Error] Invalid Ticker: {ticker} - Asset is not tradable");
            return false;
        }
        return true;
    }
    catch (Exception ex)
    {
        LogError($"[Error] Invalid Ticker: {ticker} - {ex.Message}");
        return false;
    }
}

// Liquidate positions from config file before trading with override tickers
// IMPORTANT: Only liquidate positions this instance claims ownership of (via local state)
async Task LiquidateConfiguredPositionsAsync(
    TradingSettings configSettings,
    TradingState tradingState,
    IAlpacaTradingClient tradingClient,
    IAlpacaDataClient dataClient,
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
    
    var positions = await tradingClient.ListPositionsAsync();
    var ownedPosition = positions.FirstOrDefault(p => 
        p.Symbol.Equals(ownedSymbol, StringComparison.OrdinalIgnoreCase));
    
    if (ownedPosition != null || tradingState.CurrentShares > 0)
    {
        Log($"Liquidating locally-owned position: {ownedSymbol} ({tradingState.CurrentShares} shares)");
        await LiquidatePositionAsync(ownedSymbol, ownedPosition, tradingState, tradingClient, dataClient, stateFilePath, configSettings);
    }
}

// Data Seeding Logic
async Task SeedRollingSmaAsync(TradingSettings settings, IAlpacaDataClient dataClient, IAlpacaCryptoDataClient cryptoDataClient, Queue<decimal> priceQueue, Queue<decimal> btcQueue, TimeZoneInfo easternZone)
{
    Log("Seeding Rolling SMA Queue...");
    var utcNow = DateTime.UtcNow;
    var easternNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternZone);
    
    // Only use BTC/USD for seeding if enabled (default for config, disabled for CLI overrides unless -usebtc)
    var earlyTradingEnd = new TimeSpan(9, 55, 0);
    var usesCrypto = settings.UseBtcEarlyTrading && easternNow.TimeOfDay < earlyTradingEnd;
    var benchmarkSymbol = usesCrypto ? settings.CryptoBenchmarkSymbol : settings.BenchmarkSymbol;

    // Use UTC for API requests. Fetch last 15 minutes to be safe.
    var endTimeUtc = DateTime.UtcNow;
    var startTimeUtc = endTimeUtc.AddMinutes(-15); 

    decimal seedPrice;

    if (usesCrypto)
    {
         var cryptoBarsRequest = new HistoricalCryptoBarsRequest(
            benchmarkSymbol,
            startTimeUtc,
            endTimeUtc,
            BarTimeFrame.Minute
        );
        var cryptoBarsResponse = await cryptoDataClient.ListHistoricalBarsAsync(cryptoBarsRequest);
        var lastBar = cryptoBarsResponse.Items.LastOrDefault();
        
        if (lastBar == null)
        {
             // Fallback to latest trade if no bars
            var latestTrades = await cryptoDataClient.ListLatestTradesAsync(new LatestDataListRequest([benchmarkSymbol]));
            seedPrice = latestTrades[benchmarkSymbol].Price;
        }
        else
        {
            seedPrice = lastBar.Close;
        }
    }
    else
    {
        var stockBarsRequest = new HistoricalBarsRequest(
            benchmarkSymbol,
            startTimeUtc,
            endTimeUtc,
            BarTimeFrame.Minute
        )
        {
            Feed = MarketDataFeed.Iex
        };

        var stockBarsResponse = await dataClient.ListHistoricalBarsAsync(stockBarsRequest);
        var lastBar = stockBarsResponse.Items.LastOrDefault();
          if (lastBar == null)
        {
             // Fallback to latest trade if no bars
            var quoteRequest = new LatestMarketDataRequest(benchmarkSymbol) { Feed = MarketDataFeed.Iex };
            var trade = await dataClient.GetLatestTradeAsync(quoteRequest);
            seedPrice = trade.Price;
        }
        else
        {
             seedPrice = lastBar.Close;
        }
    }

    Log($"Seeding with price: ${seedPrice:N2} (x{settings.SMALength})");
    for (int i = 0; i < settings.SMALength; i++)
    {
        priceQueue.Enqueue(seedPrice);
    }
    
    // Seed BTC queue if WatchBtc is enabled
    if (settings.WatchBtc)
    {
        Log("Seeding BTC Correlation Queue...");
        var btcBarsRequest = new HistoricalCryptoBarsRequest(
            settings.CryptoBenchmarkSymbol,
            startTimeUtc,
            endTimeUtc,
            BarTimeFrame.Minute
        );
        var btcBarsResponse = await cryptoDataClient.ListHistoricalBarsAsync(btcBarsRequest);
        var btcLastBar = btcBarsResponse.Items.LastOrDefault();
        
        decimal btcSeedPrice;
        if (btcLastBar == null)
        {
            var btcLatestTrades = await cryptoDataClient.ListLatestTradesAsync(new LatestDataListRequest([settings.CryptoBenchmarkSymbol]));
            btcSeedPrice = btcLatestTrades[settings.CryptoBenchmarkSymbol].Price;
        }
        else
        {
            btcSeedPrice = btcLastBar.Close;
        }
        
        Log($"Seeding BTC with price: ${btcSeedPrice:N2} (x{settings.SMALength})");
        for (int i = 0; i < settings.SMALength; i++)
        {
            btcQueue.Enqueue(btcSeedPrice);
        }
    }
}

// Data Seeding Logic for IncrementalSma (Low-Latency Mode)
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
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath,
    TradingSettings settings,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    (decimal quote, decimal lowerBand, decimal upperBand, decimal? stopPrice)? holdDisplayInfo = null,
    Func<decimal>? getCurrentPrice = null,
    Func<decimal, string>? getSignalAtPrice = null)
{
    // Get current positions from Alpaca
    var positions = await tradingClient.ListPositionsAsync();
    var targetPosition = positions.FirstOrDefault(p => p.Symbol.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase));
    var oppositePosition = !string.IsNullOrEmpty(oppositeSymbol) 
        ? positions.FirstOrDefault(p => p.Symbol.Equals(oppositeSymbol, StringComparison.OrdinalIgnoreCase))
        : null;
    
    // Also check local state
    var localHoldsTarget = tradingState.CurrentPosition?.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase) ?? false;
    var localHoldsOpposite = !string.IsNullOrEmpty(oppositeSymbol) && 
        (tradingState.CurrentPosition?.Equals(oppositeSymbol, StringComparison.OrdinalIgnoreCase) ?? false);

    // 1. Liquidate Opposite if held (only if we have an opposite symbol)
    // LiquidatePositionAsync now WAITS for fill before returning, preventing race conditions
    if (!string.IsNullOrEmpty(oppositeSymbol) && (oppositePosition != null || localHoldsOpposite))
    {
        Log($"Current signal targets {targetSymbol}, but holding {oppositeSymbol}. Liquidating...");
        var liquidated = await LiquidatePositionAsync(oppositeSymbol, oppositePosition, tradingState, tradingClient, dataClient, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
        
        if (!liquidated)
        {
            LogError($"[ERROR] Failed to liquidate {oppositeSymbol}. Aborting position switch.");
            return; // Don't proceed to buy if liquidation failed
        }
    }

    // 2. SAFEGUARD: Re-check Alpaca positions before buying to prevent dual holdings
    // This catches edge cases where external orders or race conditions left positions open
    positions = await tradingClient.ListPositionsAsync();
    oppositePosition = !string.IsNullOrEmpty(oppositeSymbol) 
        ? positions.FirstOrDefault(p => p.Symbol.Equals(oppositeSymbol, StringComparison.OrdinalIgnoreCase))
        : null;
    
    if (oppositePosition != null && oppositePosition.Quantity > 0)
    {
        LogError($"[SAFEGUARD] Opposite position {oppositeSymbol} still exists ({oppositePosition.Quantity} shares). Aborting buy to prevent dual holdings.");
        return;
    }

    // 3. Buy Target if not held
    targetPosition = positions.FirstOrDefault(p => p.Symbol.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase));
    var alreadyHoldsTarget = targetPosition != null || (tradingState.CurrentPosition?.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase) ?? false);
    
    if (!alreadyHoldsTarget)
    {
        await BuyPositionAsync(targetSymbol, tradingState, tradingClient, dataClient, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getCurrentPrice);
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
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath,
    TradingSettings settings,
    string reason = "NEUTRAL",
    bool showStatus = true,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    Func<decimal, string>? getSignalAtPrice = null)
{
    // Get current positions from Alpaca
    var positions = await tradingClient.ListPositionsAsync();
    
    // Check for Bull Symbol
    var bullPosition = positions.FirstOrDefault(p => p.Symbol.Equals(settings.BullSymbol, StringComparison.OrdinalIgnoreCase));
    var localHoldsBull = tradingState.CurrentPosition?.Equals(settings.BullSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

    if (bullPosition != null || localHoldsBull)
    {
        Log($"Signal is {reason}. Liquidating {settings.BullSymbol} to Cash.");
        await LiquidatePositionAsync(settings.BullSymbol, bullPosition, tradingState, tradingClient, dataClient, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
    }

    // Check for Bear Symbol (only if BearSymbol is configured)
    if (!string.IsNullOrEmpty(settings.BearSymbol))
    {
        var bearPosition = positions.FirstOrDefault(p => p.Symbol.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase));
        var localHoldsBear = tradingState.CurrentPosition?.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

        if (bearPosition != null || localHoldsBear)
        {
             Log($"Signal is {reason}. Liquidating {settings.BearSymbol} to Cash.");
             await LiquidatePositionAsync(settings.BearSymbol, bearPosition, tradingState, tradingClient, dataClient, stateFilePath, settings, slippageLock, updateSlippage, slippageLogFile, getSignalAtPrice);
        }
    }

    if (showStatus)
    {
        Log($"[{reason}] Sitting in Cash ⚪");
    }
}


// Helper: Liquidate Position
// Returns true if liquidation succeeded, false if it failed
// IMPORTANT: This method now WAITS for the sell order to fill before returning
// to prevent race conditions where a buy order is placed before the sell completes.
async Task<bool> LiquidatePositionAsync(
    string symbol, 
    IPosition? position, 
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath,
    TradingSettings settings,
    object? slippageLock = null,
    Action<decimal>? updateSlippage = null,
    string? slippageLogFile = null,
    Func<decimal, string>? getSignalAtPrice = null) // Optional: for smart exit chaser
{
    // Calculate expected sale proceeds before liquidating
    var quoteRequest = new LatestMarketDataRequest(symbol)
    {
        Feed = MarketDataFeed.Iex
    };
    var trade = await dataClient.GetLatestTradeAsync(quoteRequest);
    var quotePrice = trade.Price;
    
    // Use LOCAL state share count (not Alpaca position) to support multi-instance scenarios
    // This ensures we only sell shares this bot instance "owns", not shares from other instances
    var shareCount = tradingState.CurrentShares;
    
    if (shareCount <= 0)
    {
        Log($"[WARN] No shares to liquidate for {symbol} (local state shows 0). Clearing state.");
        tradingState.CurrentPosition = null;
        tradingState.CurrentShares = 0;
        SaveTradingState(stateFilePath, tradingState);
        return true; // Nothing to sell, state is clean
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
        var (filledQty, avgPrice, totalProceeds) = await ExecuteIocMachineGunAsync(
            symbol,
            shareCount,
            OrderSide.Sell,
            limitPrice,
            settings.IocRetryStepCents,
            settings.IocMaxRetries,
            settings.IocMaxDeviationPercent,
            settings,
            tradingClient);
        
        if (filledQty > 0)
        {
            // Success - update state with actual fill
            var iocProceeds = totalProceeds;
            var slippage = iocProceeds - estimatedProceeds;
            
            tradingState.AvailableCash = iocProceeds + tradingState.AccumulatedLeftover;
            
            // If we sold everything
            if (filledQty >= shareCount)
            {
                tradingState.CurrentPosition = null;
                tradingState.CurrentShares = 0;
                SaveTradingState(stateFilePath, tradingState);
                
                var slipLabel = slippage >= 0 ? "favorable" : "unfavorable";
                LogSuccess($"[FILL] IOC Sell complete: {filledQty} @ ${avgPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})");
                
                // Track slippage if monitoring enabled
                if (settings.MonitorSlippage && slippageLock != null && updateSlippage != null)
                {
                    var tradeSlippage = (avgPrice - quotePrice) * filledQty;
                    lock (slippageLock)
                    {
                        updateSlippage(tradeSlippage);
                    }
                    
                    if (slippageLogFile != null)
                    {
                        var favor = Math.Sign(tradeSlippage);
                        var csvLine = $"{DateTime.UtcNow:s},{symbol},Sell,{filledQty},{quotePrice:F4},{avgPrice:F4},{tradeSlippage:F2},{favor}";
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
                var remainingShares = shareCount - filledQty;
                tradingState.CurrentShares = remainingShares;
                SaveTradingState(stateFilePath, tradingState);
                
                LogWarning($"[FILL] IOC Sell partial: {filledQty}/{shareCount} @ ${avgPrice:N4}. {remainingShares} shares remaining.");
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
    // STANDARD ORDER PATH (Limit or Market orders)
    // =========================================================================
    Guid? sellOrderId = null;
    
    try
    {
        NewOrderRequest sellOrder;
        
        if (useLimit)
        {
            sellOrder = new NewOrderRequest(
                symbol,
                OrderQuantity.FromInt64(shareCount),
                OrderSide.Sell,
                OrderType.Limit,
                TimeInForce.Day
            )
            {
                ClientOrderId = settings.GenerateClientOrderId(),
                LimitPrice = limitPrice
            };
        }
        else
        {
            sellOrder = new NewOrderRequest(
                symbol,
                OrderQuantity.FromInt64(shareCount),
                OrderSide.Sell,
                OrderType.Market,
                TimeInForce.Day
            )
            {
                ClientOrderId = settings.GenerateClientOrderId()
            };
        }
        
        var order = await tradingClient.PostOrderAsync(sellOrder);
        LogSuccess($"Sell order submitted: {order.OrderId} (ClientId: {order.ClientOrderId})");
        sellOrderId = order.OrderId;
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
            var filledOrder = await tradingClient.GetOrderAsync(sellOrderId!.Value);
            
            if (filledOrder.OrderStatus == OrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
            {
                actualPrice = filledOrder.AverageFillPrice.Value;
                actualQty = (long)filledOrder.FilledQuantity;
                actualProceeds = actualQty * actualPrice;
                fillConfirmed = true;
                
                var slippage = actualProceeds - estimatedProceeds;
                if (Math.Abs(slippage) > 0.001m)
                {
                    // For SELL: positive slippage = got more than expected = favorable
                    var slipLabel = slippage >= 0 ? "favorable" : "unfavorable";
                    Log($"[FILL] Sell confirmed: {actualQty} @ ${actualPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})");
                }
                else
                {
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
            else if (filledOrder.OrderStatus == OrderStatus.Canceled || 
                     filledOrder.OrderStatus == OrderStatus.Expired ||
                     filledOrder.OrderStatus == OrderStatus.Rejected)
            {
                // Order failed - don't update state, position still exists
                LogError($"[FILL] Sell order {filledOrder.OrderStatus} - position still held");
                return false;
            }
            else if (filledOrder.OrderStatus == OrderStatus.PartiallyFilled)
            {
                // Partially filled - keep waiting for full fill
                var partialQty = (long)filledOrder.FilledQuantity;
                Log($"[FILL] Partial fill: {partialQty}/{shareCount} shares...");
                filledQtySoFar = partialQty;
            }
            
            // EXIT CHASER LOGIC: Limit order hasn't filled in time
            // SMART EXIT: Re-check signal before forcing market sell
            // This catches "V-shaped" reversals where price recovers during timeout
            if (useLimit && !chaserTriggered && (DateTime.UtcNow - startTime).TotalMilliseconds >= chaserTimeoutMs)
            {
                chaserTriggered = true;
                filledQtySoFar = (long)filledOrder.FilledQuantity;
                
                // Cancel the limit order first
                LogWarning($"[EXECUTION] Sell limit order timed out after {chaserTimeoutMs}ms. Evaluating exit...");
                try
                {
                    await tradingClient.CancelOrderAsync(sellOrderId!.Value);
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
                var cancelledOrder = await tradingClient.GetOrderAsync(sellOrderId!.Value);
                filledQtySoFar = (long)cancelledOrder.FilledQuantity;
                
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
                        // Get fresh price from API (stream price passed via signal checker)
                        decimal currentPrice = quotePrice;
                        try
                        {
                            var latestTrade = await dataClient.GetLatestTradeAsync(new LatestMarketDataRequest(symbol) { Feed = MarketDataFeed.Iex });
                            currentPrice = latestTrade.Price;
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
                        
                        var chaserOrder = new NewOrderRequest(
                            symbol,
                            OrderQuantity.FromInt64(remainingQty),
                            OrderSide.Sell,
                            OrderType.Market,
                            TimeInForce.Day
                        )
                        {
                            ClientOrderId = settings.GenerateClientOrderId()
                        };
                        
                        var chaserResult = await tradingClient.PostOrderAsync(chaserOrder);
                        sellOrderId = chaserResult.OrderId; // Track the chaser order now
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

// ============================================================================
// IOC MACHINE-GUN EXECUTION HELPER
// ============================================================================
// For true low-latency, IOC orders are fire-and-check synchronously.
// If cancelled, immediately re-fire at adjusted price without polling delays.
// Returns (filledQty, avgPrice, totalProceeds)
async Task<(long FilledQty, decimal AvgPrice, decimal TotalProceeds)> ExecuteIocMachineGunAsync(
    string symbol,
    long targetQty,
    OrderSide side,
    decimal startPrice,
    decimal priceStepCents,
    int maxRetries,
    decimal maxDeviationPercent,
    TradingSettings settings,
    IAlpacaTradingClient tradingClient)
{
    long totalFilled = 0;
    decimal totalProceeds = 0m;
    decimal currentPrice = startPrice;
    var originalPrice = startPrice;
    
    for (int attempt = 0; attempt < maxRetries && totalFilled < targetQty; attempt++)
    {
        var remainingQty = targetQty - totalFilled;
        
        // Check price deviation limit
        var deviation = Math.Abs((currentPrice - originalPrice) / originalPrice);
        if (deviation > maxDeviationPercent)
        {
            LogWarning($"[IOC] Price deviation {deviation:P2} exceeds limit {maxDeviationPercent:P2}. Stopping retries.");
            break;
        }
        
        // Submit IOC order
        var limitPrice = Math.Round(currentPrice, 2);
        var orderRequest = new NewOrderRequest(
            symbol,
            OrderQuantity.FromInt64(remainingQty),
            side,
            OrderType.Limit,
            TimeInForce.Ioc
        )
        {
            ClientOrderId = settings.GenerateClientOrderId(),
            LimitPrice = limitPrice
        };
        
        try
        {
            var order = await tradingClient.PostOrderAsync(orderRequest);
            
            // OPTIMIZATION: Check PostOrderAsync response FIRST before GetOrderAsync
            // IOC orders often resolve immediately - save an HTTP round trip if already terminal
            IOrder filledOrder;
            
            if (order.OrderStatus == OrderStatus.Filled || 
                order.OrderStatus == OrderStatus.Canceled ||
                order.OrderStatus == OrderStatus.Expired)
            {
                // Order already resolved - use response directly (saves ~50% latency)
                filledOrder = order;
            }
            else
            {
                // Order still pending - fetch updated status (rare for IOC)
                filledOrder = await tradingClient.GetOrderAsync(order.OrderId);
            }
            
            if (filledOrder.OrderStatus == OrderStatus.Filled || 
                (filledOrder.FilledQuantity > 0 && filledOrder.AverageFillPrice.HasValue))
            {
                var filledQty = (long)filledOrder.FilledQuantity;
                var avgPrice = filledOrder.AverageFillPrice ?? limitPrice;
                totalFilled += filledQty;
                totalProceeds += filledQty * avgPrice;
                
                if (filledOrder.OrderStatus == OrderStatus.Filled)
                {
                    Log($"[IOC] Attempt {attempt + 1}: FILLED {filledQty} @ ${avgPrice:N4}");
                    break; // Fully filled
                }
                else
                {
                    // Partial fill - exhausted liquidity at this price, must chase up
                    Log($"[IOC] Attempt {attempt + 1}: Partial {filledQty}/{remainingQty} @ ${avgPrice:N4}");
                    if (side == OrderSide.Buy)
                    {
                        currentPrice += (priceStepCents / 100m); // Bid higher for remaining
                    }
                    else
                    {
                        currentPrice -= (priceStepCents / 100m); // Ask lower for remaining
                    }
                }
            }
            else if (filledOrder.OrderStatus == OrderStatus.Canceled ||
                     filledOrder.OrderStatus == OrderStatus.Expired)
            {
                // No fill - adjust price and retry immediately
                if (side == OrderSide.Buy)
                {
                    currentPrice += (priceStepCents / 100m); // Bid higher
                }
                else
                {
                    currentPrice -= (priceStepCents / 100m); // Ask lower
                }
                Log($"[IOC] Attempt {attempt + 1}: Cancelled. Retrying at ${currentPrice:N2}...");
            }
            else
            {
                // Pending or other status - shouldn't happen for IOC, but wait briefly
                Log($"[IOC] Attempt {attempt + 1}: Status={filledOrder.OrderStatus}. Waiting...");
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            LogError($"[IOC] Attempt {attempt + 1} error: {ex.Message}");
            await Task.Delay(100); // Brief pause before retry on error
        }
    }
    
    var avgFillPrice = totalFilled > 0 ? (totalProceeds / totalFilled) : 0m;
    return (totalFilled, avgFillPrice, totalProceeds);
}

// Helper: Buy Position
async Task BuyPositionAsync(
    string symbol,
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
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

    // Get target ETF price
    var quoteRequest = new LatestMarketDataRequest(symbol)
    {
        Feed = MarketDataFeed.Iex
    };
    var trade = await dataClient.GetLatestTradeAsync(quoteRequest);
    var basePrice = trade.Price;

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
            var (filledQty, avgPrice, totalProceeds) = await ExecuteIocMachineGunAsync(
                symbol,
                quantity,
                OrderSide.Buy,
                limitPrice,
                settings.IocRetryStepCents,
                settings.IocMaxRetries,
                settings.IocMaxDeviationPercent,
                settings,
                tradingClient);
            
            if (filledQty > 0)
            {
                // Success - update state with actual fill
                var actualCost = totalProceeds;
                var actualLeftover = availableForPurchase - actualCost;
                var slippage = actualCost - (filledQty * basePrice);
                
                tradingState.AvailableCash = 0m;
                tradingState.AccumulatedLeftover = actualLeftover;
                tradingState.CurrentPosition = symbol;
                tradingState.CurrentShares = filledQty;
                tradingState.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
                SaveTradingState(stateFilePath, tradingState);
                
                var slipLabel = slippage >= 0 ? "unfavorable" : "favorable";
                LogSuccess($"[FILL] IOC Buy complete: {filledQty} @ ${avgPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})");
                
                // Track slippage if monitoring enabled
                if (settings.MonitorSlippage && slippageLock != null && updateSlippage != null)
                {
                    var tradeSlippage = (basePrice - avgPrice) * filledQty;
                    lock (slippageLock)
                    {
                        updateSlippage(tradeSlippage);
                    }
                    
                    if (slippageLogFile != null)
                    {
                        var favor = Math.Sign(tradeSlippage);
                        var csvLine = $"{DateTime.UtcNow:s},{symbol},Buy,{filledQty},{basePrice:F4},{avgPrice:F4},{tradeSlippage:F2},{favor}";
                        _ = File.AppendAllTextAsync(slippageLogFile, csvLine + Environment.NewLine);
                    }
                }
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
        NewOrderRequest orderRequest;
        
        if (useLimit)
        {
            orderRequest = new NewOrderRequest(
                symbol,
                OrderQuantity.FromInt64(quantity),
                OrderSide.Buy,
                OrderType.Limit,
                TimeInForce.Day
            )
            {
                ClientOrderId = settings.GenerateClientOrderId(),
                LimitPrice = limitPrice
            };
        }
        else
        {
            orderRequest = new NewOrderRequest(
                symbol,
                OrderQuantity.FromInt64(quantity),
                OrderSide.Buy,
                OrderType.Market,
                TimeInForce.Day
            )
            {
                ClientOrderId = settings.GenerateClientOrderId()
            };
        }

        var order = await tradingClient.PostOrderAsync(orderRequest);
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
                    var filledOrder = await tradingClient.GetOrderAsync(orderId);
                    
                    if (filledOrder.OrderStatus == OrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                    {
                        var actualPrice = filledOrder.AverageFillPrice.Value;
                        var actualQty = (long)filledOrder.FilledQuantity;
                        var actualCost = actualQty * actualPrice;
                        var slippage = actualCost - estimatedCost;
                        
                        if (Math.Abs(slippage) > 0.001m)
                        {
                            tradingState.AccumulatedLeftover -= slippage;
                            tradingState.CurrentShares = actualQty;
                            SaveTradingState(stateFilePath, tradingState);
                            var slipLabel = slippage <= 0 ? "favorable" : "unfavorable";
                            Log($"[FILL] Buy confirmed: {actualQty} @ ${actualPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2} {slipLabel})");
                        }
                        else
                        {
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
                    else if (filledOrder.OrderStatus == OrderStatus.Canceled || 
                             filledOrder.OrderStatus == OrderStatus.Expired ||
                             filledOrder.OrderStatus == OrderStatus.Rejected)
                    {
                        // Order cancelled/rejected - rollback state
                        LogError($"[FILL] Buy order {filledOrder.OrderStatus} - rolling back state");
                        tradingState.AvailableCash = preBuyCash;
                        tradingState.AccumulatedLeftover = 0m;
                        tradingState.CurrentPosition = null;
                        tradingState.CurrentShares = 0;
                        SaveTradingState(stateFilePath, tradingState);
                        LogError($"[FILL] State rolled back: ${preBuyCash:N2} cash restored, no position");
                        return;
                    }
                    else if (useLimit && !chaserTriggered && (DateTime.UtcNow - startTime).TotalMilliseconds >= chaserTimeoutMs)
                    {
                        // SMART CHASER LOGIC FOR ENTRIES
                        // Priority: VALUE / SIGNAL VALIDITY
                        // If price runs away, don't blindly chase - re-evaluate first
                        chaserTriggered = true;
                        filledQty = (long)filledOrder.FilledQuantity;
                        
                        // Cancel the limit order first
                        LogWarning($"[EXECUTION] Buy limit order timed out after {chaserTimeoutMs}ms. Evaluating chase...");
                        try
                        {
                            await tradingClient.CancelOrderAsync(orderId);
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
                        var cancelledOrder = await tradingClient.GetOrderAsync(orderId);
                        filledQty = (long)cancelledOrder.FilledQuantity;
                        
                        if (cancelledOrder.AverageFillPrice.HasValue && filledQty > 0)
                        {
                            totalFillCost = filledQty * cancelledOrder.AverageFillPrice.Value;
                        }
                        
                        var remainingQty = quantity - filledQty;
                        
                        if (remainingQty > 0)
                        {
                            // SMART ENTRY LOGIC: Check if price has moved too far
                            decimal currentPrice = getCurrentPrice?.Invoke() ?? 0m;
                            
                            // Fallback to API if stream price unavailable
                            if (currentPrice <= 0m)
                            {
                                try
                                {
                                    var latestTrade = await dataClient.GetLatestTradeAsync(new LatestMarketDataRequest(symbol) { Feed = MarketDataFeed.Iex });
                                    currentPrice = latestTrade.Price;
                                }
                                catch { currentPrice = quotePrice; } // Use original if API fails
                            }
                            
                            var percentMove = Math.Abs((currentPrice - quotePrice) / quotePrice);
                            
                            if (percentMove < settings.MaxChaseDeviationPercent)
                            {
                                // Price is stable - chase with market order
                                LogWarning($"[EXECUTION] Price moved {percentMove:P2} (< {settings.MaxChaseDeviationPercent:P2}). CHASING with Market Order.");
                                
                                var chaserOrder = new NewOrderRequest(
                                    symbol,
                                    OrderQuantity.FromInt64(remainingQty),
                                    OrderSide.Buy,
                                    OrderType.Market,
                                    TimeInForce.Day
                                )
                                {
                                    ClientOrderId = settings.GenerateClientOrderId()
                                };
                                
                                var chaserResult = await tradingClient.PostOrderAsync(chaserOrder);
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
// CONFIGURATION CLASSES
// ============================================================================
class TradingSettings
{
    public string BotId { get; set; } = "main"; // Unique identifier for this bot instance
    public int PollingIntervalSeconds { get; set; } = 1;
    public string BullSymbol { get; set; } = "TQQQ";
    public string? BearSymbol { get; set; } = "SQQQ";
    public string BenchmarkSymbol { get; set; } = "QQQ";
    public string CryptoBenchmarkSymbol { get; set; } = "BTC/USD";
    public int SMAWindowSeconds { get; set; } = 60; // Total time window for rolling average
    public decimal ChopThresholdPercent { get; set; } = 0.0015m;
    public decimal MinChopAbsolute { get; set; } = 0.02m; // Absolute floor for hysteresis (tick-aware)
    public int NeutralWaitSeconds { get; set; } = 30;
    public decimal StartingAmount { get; set; } = 10000m;
    public bool BullOnlyMode { get; set; } = false;
    public bool UseBtcEarlyTrading { get; set; } = false; // Use BTC/USD as early trading weathervane
    public bool WatchBtc { get; set; } = false; // Use BTC as tie-breaker during NEUTRAL
    public bool MonitorSlippage { get; set; } = false; // Track and log slippage per trade
    public decimal TrailingStopPercent { get; set; } = 0.0m; // 0 = disabled, e.g. 0.002 = 0.2%
    public int StopLossCooldownSeconds { get; set; } = 10; // Washout latch duration
    public bool UseMarketableLimits { get; set; } = false; // Use limit orders instead of market orders
    public decimal MaxSlippagePercent { get; set; } = 0.002m; // 0.2% max slippage for limit orders
    public decimal MaxChaseDeviationPercent { get; set; } = 0.003m; // 0.3% max price move before aborting entry chase
    
    // LOW-LATENCY MODE SETTINGS
    public bool LowLatencyMode { get; set; } = false;     // Enable channel-based reactive pipeline
    public bool UseIocOrders { get; set; } = false;       // Use IOC limit orders ("sniper mode")
    public decimal IocLimitOffsetCents { get; set; } = 1m; // Offset above ask (buy) or below bid (sell)
    public int IocMaxRetries { get; set; } = 5;           // Max retries before fallback to market order
    public decimal IocRetryStepCents { get; set; } = 1m;  // Price step per retry (cents)
    public decimal IocMaxDeviationPercent { get; set; } = 0.005m; // Max price chase before stopping (0.5%)
    public int KeepAlivePingSeconds { get; set; } = 5;    // HTTP connection keep-alive ping interval
    public int WarmUpIterations { get; set; } = 10000;    // JIT warm-up iterations before market open
    
    // Derived: Calculate queue size dynamically from window and interval
    public int SMALength => Math.Max(1, SMAWindowSeconds / PollingIntervalSeconds);
    
    // Generate a client order ID with bot prefix for order tracking
    public string GenerateClientOrderId() => $"qqqBot-{BotId}-{Guid.NewGuid():N}";
}

class CommandLineOverrides
{
    public string? BullTicker { get; set; }
    public string? BearTicker { get; set; }
    public string? BenchmarkTicker { get; set; }
    public bool BullOnlyMode { get; set; }
    public bool HasOverrides { get; set; }
    public bool UseBtcEarlyTrading { get; set; }
    public int? NeutralWaitSecondsOverride { get; set; }
    public decimal? MinChopAbsoluteOverride { get; set; }
    public bool WatchBtc { get; set; }
    public string? BotIdOverride { get; set; }
    public bool MonitorSlippage { get; set; }
    public decimal? TrailingStopPercentOverride { get; set; }
    public bool UseMarketableLimits { get; set; }
    public decimal? MaxSlippagePercentOverride { get; set; }
    // Low-latency mode flags
    public bool LowLatencyMode { get; set; }
    public bool UseIocOrders { get; set; }
}

// CSV record for report export
class TradeRecord
{
    public string Timestamp { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? FilledPrice { get; set; }
    public decimal? FilledValue { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

// ============================================================================
// HIGH-PERFORMANCE DATA STRUCTURES
// ============================================================================

/// <summary>
/// O(1) Incremental SMA calculator using circular buffer and running sum.
/// Avoids O(N) recalculation on every tick for low-latency trading.
/// </summary>
class IncrementalSma
{
    private readonly decimal[] _buffer;
    private readonly int _capacity;
    private int _count;
    private int _head;
    private decimal _runningSum;
    
    public IncrementalSma(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer = new decimal[capacity];
        _count = 0;
        _head = 0;
        _runningSum = 0m;
    }
    
    /// <summary>Add a new price and return the updated SMA. O(1) complexity.</summary>
    public decimal Add(decimal price)
    {
        if (_count < _capacity)
        {
            // Buffer not full yet - just add
            _buffer[_count] = price;
            _runningSum += price;
            _count++;
        }
        else
        {
            // Buffer full - subtract oldest, add newest (circular)
            decimal oldest = _buffer[_head];
            _runningSum = _runningSum - oldest + price;
            _buffer[_head] = price;
            _head = (_head + 1) % _capacity;
        }
        
        return _runningSum / _count;
    }
    
    /// <summary>Current SMA value (or 0 if empty).</summary>
    public decimal CurrentAverage => _count > 0 ? _runningSum / _count : 0m;
    
    /// <summary>Number of samples currently in buffer.</summary>
    public int Count => _count;
    
    /// <summary>Whether buffer has reached full capacity.</summary>
    public bool IsFull => _count >= _capacity;
    
    /// <summary>Reset to empty state.</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
        _runningSum = 0m;
        Array.Clear(_buffer, 0, _buffer.Length);
    }
    
    /// <summary>Seed with initial values (for warm-up from historical data).</summary>
    public void Seed(IEnumerable<decimal> prices)
    {
        Clear();
        foreach (var price in prices)
        {
            Add(price);
        }
    }
}

/// <summary>
/// Trade price message for the high-performance channel pipeline.
/// Struct to avoid heap allocation.
/// </summary>
struct TradeTick
{
    public decimal Price;
    public DateTime Timestamp;
    public bool IsBenchmark; // true = benchmark (QQQ), false = BTC
}

// Marker class for user secrets
partial class Program { }
