using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Alpaca.Markets;
using CsvHelper;
using Microsoft.Extensions.Configuration;

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
    Log("\n⚠️  Shutdown requested (Ctrl+C). Liquidating positions...");
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
        PollingIntervalSeconds = configuration.GetValue("TradingBot:PollingIntervalSeconds", 5),
        BullSymbol = configuration["TradingBot:BullSymbol"] ?? "TQQQ",
        BearSymbol = configuration["TradingBot:BearSymbol"] ?? "SQQQ",
        BenchmarkSymbol = configuration["TradingBot:BenchmarkSymbol"] ?? "QQQ",
        CryptoBenchmarkSymbol = configuration["TradingBot:CryptoBenchmarkSymbol"] ?? "BTC/USD",
        SMALength = configuration.GetValue("TradingBot:SMALength", 12),
        ChopThresholdPercent = configuration.GetValue("TradingBot:ChopThresholdPercent", 0.0015m),
        MinChopAbsolute = configuration.GetValue("TradingBot:MinChopAbsolute", 0.02m),
        NeutralWaitSeconds = configuration.GetValue("TradingBot:NeutralWaitSeconds", 30),
        WatchBtc = configuration.GetValue("TradingBot:WatchBtc", false),
        StartingAmount = configuration.GetValue("TradingBot:StartingAmount", 10000m)
    };
    
    // Create effective settings (may be modified by command line overrides)
    // BTC/USD early trading is enabled by default, but disabled for CLI overrides unless -usebtc is specified
    var useBtcEarlyTrading = !cmdOverrides.HasOverrides || cmdOverrides.UseBtcEarlyTrading;
    
    var settings = new TradingSettings
    {
        BotId = cmdOverrides.BotIdOverride ?? configSettings.BotId,
        PollingIntervalSeconds = configSettings.PollingIntervalSeconds,
        BullSymbol = cmdOverrides.BullTicker ?? configSettings.BullSymbol,
        BearSymbol = cmdOverrides.BullOnlyMode ? null : (cmdOverrides.BearTicker ?? configSettings.BearSymbol),
        BenchmarkSymbol = cmdOverrides.BenchmarkTicker ?? configSettings.BenchmarkSymbol,
        CryptoBenchmarkSymbol = configSettings.CryptoBenchmarkSymbol,
        SMALength = configSettings.SMALength,
        ChopThresholdPercent = configSettings.ChopThresholdPercent,
        MinChopAbsolute = cmdOverrides.MinChopAbsoluteOverride ?? configSettings.MinChopAbsolute,
        NeutralWaitSeconds = cmdOverrides.NeutralWaitSecondsOverride ?? configSettings.NeutralWaitSeconds,
        StartingAmount = configSettings.StartingAmount,
        BullOnlyMode = cmdOverrides.BullOnlyMode,
        UseBtcEarlyTrading = useBtcEarlyTrading,
        WatchBtc = cmdOverrides.WatchBtc || configSettings.WatchBtc
    };

    // Initialize Alpaca clients (Paper Trading) - needed for ticker validation
    var secretKey = new SecretKey(apiKey, apiSecret);
    
    using var tradingClient = Environments.Paper.GetAlpacaTradingClient(secretKey);
    using var dataClient = Environments.Paper.GetAlpacaDataClient(secretKey);
    using var cryptoDataClient = Environments.Paper.GetAlpacaCryptoDataClient(secretKey);

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
    Log($"  SMA Length: {settings.SMALength}");
    Log($"  Chop Threshold: {settings.ChopThresholdPercent * 100:N3}%");
    Log($"  Min Chop Absolute: ${settings.MinChopAbsolute:N4}");
    Log($"  Neutral Wait: {settings.NeutralWaitSeconds}s");
    Log($"  BTC Correlation (Neutral Nudge): {(settings.WatchBtc ? "Enabled" : "Disabled")}");
    Log($"  Polling Interval: {settings.PollingIntervalSeconds}s");
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
    
    // Logging state
    DateTime lastLogTime = DateTime.MinValue;
    string lastSignal = string.Empty;

    // Main trading loop
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var utcNow = DateTime.UtcNow;
            var easternNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternZone);

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
                
                await Task.Delay(TimeSpan.FromMinutes(1));
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
                // Fetch BTC price
                var btcTrades = await cryptoDataClient.ListLatestTradesAsync(
                    new LatestDataListRequest([settings.CryptoBenchmarkSymbol]));
                var btcPrice = btcTrades[settings.CryptoBenchmarkSymbol].Price;
                
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

                LogBalanceWithPL(currentBalance, currentBalance - settings.StartingAmount);

                Log($"--- {easternNow:HH:mm:ss} ET | {benchmarkSymbol}: ${currentPrice:N2} | SMA: ${currentSma:N2} ---");
                Log($"    Bands: [${lowerBand:N2} - ${upperBand:N2}]");
                Log($"    Signal: {finalSignal} 🟢🔴⚪{(signal != finalSignal ? $" (was {signal})" : "")}");

                lastLogTime = DateTime.UtcNow;
            }
            
            lastSignal = finalSignal;

            // Execute Strategy based on finalSignal
            if (finalSignal == "MARKET_CLOSE")
            {
                neutralDetectionTime = null; // Reset neutral timer
                await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, reason: "MARKET CLOSE", showStatus: shouldLog);
            }
            else if (finalSignal == "BULL")
            {
                neutralDetectionTime = null; // Reset neutral timer
                await EnsurePositionAsync(settings.BullSymbol, settings.BearSymbol, tradingState, tradingClient, dataClient, stateFilePath, settings);
            }
            else if (finalSignal == "BEAR")
            {
                neutralDetectionTime = null; // Reset neutral timer
                // In bull-only mode, BEAR signal dumps to cash
                if (settings.BullOnlyMode)
                {
                    await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, reason: "BEAR (bull-only mode)", showStatus: shouldLog);
                }
                else
                {
                    await EnsurePositionAsync(settings.BearSymbol!, settings.BullSymbol, tradingState, tradingClient, dataClient, stateFilePath, settings);
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
                        await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, showStatus: shouldLog);
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
                settings);
            
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

// Execution Logic: Enter Position
async Task EnsurePositionAsync(
    string targetSymbol, 
    string? oppositeSymbol, 
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath,
    TradingSettings settings)
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
    if (!string.IsNullOrEmpty(oppositeSymbol) && (oppositePosition != null || localHoldsOpposite))
    {
        Log($"Current signal targets {targetSymbol}, but holding {oppositeSymbol}. Liquidating...");
        await LiquidatePositionAsync(oppositeSymbol, oppositePosition, tradingState, tradingClient, dataClient, stateFilePath, settings);
    }

    // 2. Buy Target if not held
    var alreadyHoldsTarget = targetPosition != null || localHoldsTarget;
    if (!alreadyHoldsTarget)
    {
        await BuyPositionAsync(targetSymbol, tradingState, tradingClient, dataClient, stateFilePath, settings);
    }
    else
    {
        var heldShares = targetPosition?.Quantity ?? tradingState.CurrentShares;
        Log($"[HOLD] Staying Long {targetSymbol} ({heldShares} shares).");
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
    bool showStatus = true)
{
    // Get current positions from Alpaca
    var positions = await tradingClient.ListPositionsAsync();
    
    // Check for Bull Symbol
    var bullPosition = positions.FirstOrDefault(p => p.Symbol.Equals(settings.BullSymbol, StringComparison.OrdinalIgnoreCase));
    var localHoldsBull = tradingState.CurrentPosition?.Equals(settings.BullSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

    if (bullPosition != null || localHoldsBull)
    {
        Log($"Signal is {reason}. Liquidating {settings.BullSymbol} to Cash.");
        await LiquidatePositionAsync(settings.BullSymbol, bullPosition, tradingState, tradingClient, dataClient, stateFilePath, settings);
    }

    // Check for Bear Symbol (only if BearSymbol is configured)
    if (!string.IsNullOrEmpty(settings.BearSymbol))
    {
        var bearPosition = positions.FirstOrDefault(p => p.Symbol.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase));
        var localHoldsBear = tradingState.CurrentPosition?.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

        if (bearPosition != null || localHoldsBear)
        {
             Log($"Signal is {reason}. Liquidating {settings.BearSymbol} to Cash.");
             await LiquidatePositionAsync(settings.BearSymbol, bearPosition, tradingState, tradingClient, dataClient, stateFilePath, settings);
        }
    }

    if (showStatus)
    {
        Log($"[{reason}] Sitting in Cash ⚪");
    }
}


// Helper: Liquidate Position
// Returns true if liquidation succeeded, false if it failed
async Task<bool> LiquidatePositionAsync(
    string symbol, 
    IPosition? position, 
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath,
    TradingSettings settings)
{
    // Calculate expected sale proceeds before liquidating
    var quoteRequest = new LatestMarketDataRequest(symbol)
    {
        Feed = MarketDataFeed.Iex
    };
    var trade = await dataClient.GetLatestTradeAsync(quoteRequest);
    var price = trade.Price;
    
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
    
    var saleProceeds = shareCount * price;
    
    // Liquidate using a market sell order for specific share count (not DeletePositionAsync)
    Log($"[SELL] Liquidating {shareCount} shares of {symbol} @ ~${price:N2}");
    Log($"       Expected proceeds: ${saleProceeds:N2}");
    
    bool sellSucceeded = false;
    bool positionNotFound = false;
    
    Guid? sellOrderId = null;
    
    try
    {
        var sellOrder = new NewOrderRequest(
            symbol,
            OrderQuantity.FromInt64(shareCount),
            OrderSide.Sell,
            OrderType.Market,
            TimeInForce.Day
        )
        {
            ClientOrderId = settings.GenerateClientOrderId()
        };
        
        var order = await tradingClient.PostOrderAsync(sellOrder);
        LogSuccess($"Sell order submitted: {order.OrderId} (ClientId: {order.ClientOrderId})");
        sellOrderId = order.OrderId;
        sellSucceeded = true;
    }
    catch (Exception ex)
    {
        // Handle case where position doesn't exist or insufficient shares
        if (ex.Message.Contains("insufficient") || ex.Message.Contains("not found"))
        {
            Log($"[WARN] Could not sell shares ({ex.Message}). Position may not exist.");
            positionNotFound = true;
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
    
    // Only update trading state if sell succeeded or position doesn't exist
    if (sellSucceeded || positionNotFound)
    {
        // Use estimate immediately (non-blocking)
        tradingState.AvailableCash = sellSucceeded ? (saleProceeds + tradingState.AccumulatedLeftover) : tradingState.AccumulatedLeftover;
        tradingState.AccumulatedLeftover = 0m; 
        tradingState.CurrentPosition = null;
        tradingState.CurrentShares = 0;
        SaveTradingState(stateFilePath, tradingState);
        
        // Display balance and P/L after sale
        var totalBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
        var profitLoss = totalBalance - tradingState.StartingAmount;
        LogBalanceWithPL(totalBalance, profitLoss);
        
        // Fire-and-forget: poll for actual fill price and apply correction
        if (sellSucceeded && sellOrderId.HasValue)
        {
            var orderId = sellOrderId.Value;
            var estimatedProceeds = saleProceeds;
            var preSellPosition = symbol; // Capture for rollback
            var preSellShares = shareCount;
            var preSellLeftover = tradingState.AccumulatedLeftover; // Already zeroed, but was passed in
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 20; i++) // Up to 10 seconds
                    {
                        await Task.Delay(500);
                        var filledOrder = await tradingClient.GetOrderAsync(orderId);
                        
                        if (filledOrder.OrderStatus == OrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                        {
                            var actualPrice = filledOrder.AverageFillPrice.Value;
                            var actualQty = (long)filledOrder.FilledQuantity;
                            var actualProceeds = actualQty * actualPrice;
                            var slippage = actualProceeds - estimatedProceeds;
                            
                            if (Math.Abs(slippage) > 0.001m)
                            {
                                // Apply correction: if we got more, increase cash; if less, decrease it
                                tradingState.AvailableCash += slippage;
                                SaveTradingState(stateFilePath, tradingState);
                                Log($"[FILL] Sell confirmed: {actualQty} @ ${actualPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2})");
                            }
                            else
                            {
                                Log($"[FILL] Sell confirmed: {actualQty} @ ${actualPrice:N4}");
                            }
                            return;
                        }
                        else if (filledOrder.OrderStatus == OrderStatus.Canceled || 
                                 filledOrder.OrderStatus == OrderStatus.Expired ||
                                 filledOrder.OrderStatus == OrderStatus.Rejected)
                        {
                            // ROLLBACK: Restore state to pre-sell condition (still holding position)
                            LogError($"[FILL] Sell order {filledOrder.OrderStatus} - rolling back state");
                            tradingState.AvailableCash = 0m;
                            tradingState.AccumulatedLeftover = preSellLeftover;
                            tradingState.CurrentPosition = preSellPosition;
                            tradingState.CurrentShares = preSellShares;
                            SaveTradingState(stateFilePath, tradingState);
                            LogError($"[FILL] State rolled back: holding {preSellShares} {preSellPosition}");
                            return;
                        }
                    }
                    Log($"[FILL] Sell fill confirmation timeout - using estimate");
                }
                catch (Exception ex)
                {
                    Log($"[FILL] Error polling sell fill: {ex.Message}");
                }
            });
        }
        
        return true;
    }
    
    return false;
}

// Helper: Buy Position
async Task BuyPositionAsync(
    string symbol,
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath,
    TradingSettings settings)
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
    var price = trade.Price;

    // Calculate max shares
    var quantity = (long)(availableForPurchase / price);
    var totalCost = quantity * price;
    var leftover = availableForPurchase - totalCost;

    if (quantity > 0)
    {
        Log($"[BUY] {symbol} x {quantity} @ ~${price:N2} (estimated)");
        
        var orderRequest = new NewOrderRequest(
            symbol,
            OrderQuantity.FromInt64(quantity),
            OrderSide.Buy,
            OrderType.Market,
            TimeInForce.Day
        )
        {
            ClientOrderId = settings.GenerateClientOrderId()
        };

        var order = await tradingClient.PostOrderAsync(orderRequest);
        LogSuccess($"Order submitted: {order.OrderId} (ClientId: {order.ClientOrderId})");
        
        // Update state immediately with estimate (non-blocking)
        Log($"       Estimated: {quantity} shares @ ${price:N2} = ${totalCost:N2}");
        
        tradingState.AvailableCash = 0m; 
        tradingState.AccumulatedLeftover = leftover; 
        tradingState.CurrentPosition = symbol;
        tradingState.CurrentShares = quantity;
        tradingState.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
        SaveTradingState(stateFilePath, tradingState);
        
        // Fire-and-forget: poll for actual fill price and apply correction
        var orderId = order.OrderId;
        var estimatedCost = totalCost;
        var preBuyCash = availableForPurchase; // Capture for rollback
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 20; i++) // Up to 10 seconds
                {
                    await Task.Delay(500);
                    var filledOrder = await tradingClient.GetOrderAsync(orderId);
                    
                    if (filledOrder.OrderStatus == OrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                    {
                        var actualPrice = filledOrder.AverageFillPrice.Value;
                        var actualQty = (long)filledOrder.FilledQuantity;
                        var actualCost = actualQty * actualPrice;
                        var slippage = actualCost - estimatedCost;
                        
                        if (Math.Abs(slippage) > 0.001m)
                        {
                            // Apply correction: if we paid more, reduce leftover; if less, increase it
                            tradingState.AccumulatedLeftover -= slippage;
                            tradingState.CurrentShares = actualQty;
                            SaveTradingState(stateFilePath, tradingState);
                            Log($"[FILL] Buy confirmed: {actualQty} @ ${actualPrice:N4} (slippage: {(slippage >= 0 ? "+" : "")}{slippage:N2})");
                        }
                        else
                        {
                            Log($"[FILL] Buy confirmed: {actualQty} @ ${actualPrice:N4}");
                        }
                        return;
                    }
                    else if (filledOrder.OrderStatus == OrderStatus.Canceled || 
                             filledOrder.OrderStatus == OrderStatus.Expired ||
                             filledOrder.OrderStatus == OrderStatus.Rejected)
                    {
                        // ROLLBACK: Restore state to pre-buy condition
                        LogError($"[FILL] Buy order {filledOrder.OrderStatus} - rolling back state");
                        tradingState.AvailableCash = preBuyCash;
                        tradingState.AccumulatedLeftover = 0m;
                        tradingState.CurrentPosition = null;
                        tradingState.CurrentShares = 0;
                        SaveTradingState(stateFilePath, tradingState);
                        LogError($"[FILL] State rolled back: ${preBuyCash:N2} cash restored, no position");
                        return;
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
        LogError($"Share price: ${price:N2}");
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

void LogSuccess(string message)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
    Console.ResetColor();
}

void LogBalanceWithPL(decimal balance, decimal profitLoss)
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
    
    Console.WriteLine(plText);
    Console.ResetColor();
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
    public int PollingIntervalSeconds { get; set; } = 5;
    public string BullSymbol { get; set; } = "TQQQ";
    public string? BearSymbol { get; set; } = "SQQQ";
    public string BenchmarkSymbol { get; set; } = "QQQ";
    public string CryptoBenchmarkSymbol { get; set; } = "BTC/USD";
    public int SMALength { get; set; } = 12;
    public decimal ChopThresholdPercent { get; set; } = 0.0015m;
    public decimal MinChopAbsolute { get; set; } = 0.02m; // Absolute floor for hysteresis (tick-aware)
    public int NeutralWaitSeconds { get; set; } = 30;
    public decimal StartingAmount { get; set; } = 10000m;
    public bool BullOnlyMode { get; set; } = false;
    public bool UseBtcEarlyTrading { get; set; } = true; // Use BTC/USD as early trading weathervane
    public bool WatchBtc { get; set; } = false; // Use BTC as tie-breaker during NEUTRAL
    
    // Generate a client order ID with bot prefix for order tracking
    public string GenerateClientOrderId() => $"qqqBot-{BotId}-{Guid.NewGuid():N}";
}

class TradingState
{
    public decimal AvailableCash { get; set; }
    public decimal AccumulatedLeftover { get; set; }
    public bool IsInitialized { get; set; }
    public string? LastTradeTimestamp { get; set; }
    public string? CurrentPosition { get; set; }
    public long CurrentShares { get; set; }
    public decimal StartingAmount { get; set; } // Track original amount for P/L calculation
    public decimal DayStartBalance { get; set; } // Balance at start of trading day (for daily P/L)
    public string? DayStartDate { get; set; } // Date when DayStartBalance was recorded (yyyy-MM-dd)
    public TradingStateMetadata? Metadata { get; set; } // Track symbols used during session
}

class TradingStateMetadata
{
    public string? SymbolBull { get; set; }
    public string? SymbolBear { get; set; }
    public string? SymbolIndex { get; set; }
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

// Marker class for user secrets
partial class Program { }
