using System.Diagnostics;
using System.Text.Json;
using Alpaca.Markets;
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

// Run the trading bot
await RunTradingBotAsync();

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
// TRADING BOT
// ============================================================================
async Task RunTradingBotAsync()
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

    // Load trading settings
    var settings = new TradingSettings
    {
        PollingIntervalSeconds = configuration.GetValue("TradingBot:PollingIntervalSeconds", 5),
        BullSymbol = configuration["TradingBot:BullSymbol"] ?? "TQQQ",
        BearSymbol = configuration["TradingBot:BearSymbol"] ?? "SQQQ",
        BenchmarkSymbol = configuration["TradingBot:BenchmarkSymbol"] ?? "QQQ",
        CryptoBenchmarkSymbol = configuration["TradingBot:CryptoBenchmarkSymbol"] ?? "BTC/USD",
        SMALength = configuration.GetValue("TradingBot:SMALength", 12),
        ChopThresholdPercent = configuration.GetValue("TradingBot:ChopThresholdPercent", 0.0015m),
        NeutralWaitSeconds = configuration.GetValue("TradingBot:NeutralWaitSeconds", 30),
        StartingAmount = configuration.GetValue("TradingBot:StartingAmount", 10000m)
    };

    // Load or initialize trading state
    var stateFilePath = Path.Combine(AppContext.BaseDirectory, "trading_state.json");
    var tradingState = LoadTradingState(stateFilePath);
    
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

    Log($"Configuration loaded:");
    Log($"  Benchmark: {settings.BenchmarkSymbol}");
    Log($"  Crypto Benchmark (early trading): {settings.CryptoBenchmarkSymbol}");
    Log($"  Bull ETF: {settings.BullSymbol}");
    Log($"  Bear ETF: {settings.BearSymbol}");
    Log($"  SMA Length: {settings.SMALength}");
    Log($"  Chop Threshold: {settings.ChopThresholdPercent * 100:N3}%");
    Log($"  Neutral Wait: {settings.NeutralWaitSeconds}s");
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

    // Initialize Alpaca clients (Paper Trading)
    var secretKey = new SecretKey(apiKey, apiSecret);
    
    using var tradingClient = Environments.Paper.GetAlpacaTradingClient(secretKey);
    using var dataClient = Environments.Paper.GetAlpacaDataClient(secretKey);
    using var cryptoDataClient = Environments.Paper.GetAlpacaCryptoDataClient(secretKey);

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

    // Eastern Time Zone
    var easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    Log("=== Trading Bot Active ===\n");

    // Initialize Rolling SMA Engine
    var priceQueue = new Queue<decimal>();
    
    // Seed the Rolling SMA Queue
    await SeedRollingSmaAsync(settings, dataClient, cryptoDataClient, priceQueue, easternZone);
    Log($"Rolling SMA Engine Initialized with {priceQueue.Count} data points.");

    // Track last market closed notification to avoid spam
    DateTime? lastMarketClosedLog = null;
    const int marketClosedLogIntervalMinutes = 30;

    // Track neutral state duration
    DateTime? neutralDetectionTime = null;
    
    // Logging state
    DateTime lastLogTime = DateTime.MinValue;
    string lastSignal = string.Empty;

    // Main trading loop
    while (true)
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
            
            // Determine which benchmark to use based on time
            // Use BTC/USD from 9:30-9:55 AM (before QQQ has enough bars for SMA)
            // Use QQQ after 9:55 AM
            var earlyTradingEnd = new TimeSpan(9, 55, 0);
            var usesCrypto = easternNow.TimeOfDay < earlyTradingEnd;
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

            // Calculate Hysteresis Bands
            var upperBand = currentSma * (1 + settings.ChopThresholdPercent);
            var lowerBand = currentSma * (1 - settings.ChopThresholdPercent);

            // Determine Signal
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

            // LOGGING CONTROL
            bool stateChanged = signal != lastSignal;
            // Use 30s as default log interval
            bool shouldLog = stateChanged || (DateTime.UtcNow - lastLogTime).TotalSeconds >= 30;

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
                Log($"    Signal: {signal} 🟢🔴⚪");

                lastLogTime = DateTime.UtcNow;
            }
            
            lastSignal = signal;

            // Execute Strategy based on Signal
            if (signal == "MARKET_CLOSE")
            {
                neutralDetectionTime = null; // Reset neutral timer
                await EnsureNeutralAsync(tradingState, tradingClient, dataClient, stateFilePath, settings, reason: "MARKET CLOSE", showStatus: shouldLog);
            }
            else if (signal == "BULL")
            {
                neutralDetectionTime = null; // Reset neutral timer
                await EnsurePositionAsync(settings.BullSymbol, settings.BearSymbol, tradingState, tradingClient, dataClient, stateFilePath);
            }
            else if (signal == "BEAR")
            {
                neutralDetectionTime = null; // Reset neutral timer
                await EnsurePositionAsync(settings.BearSymbol, settings.BullSymbol, tradingState, tradingClient, dataClient, stateFilePath);
            }
            else // NEUTRAL
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
            // Wait for next poll (Micro-polling)
            await Task.Delay(TimeSpan.FromSeconds(settings.PollingIntervalSeconds));
        }
        catch (Exception delayEx)
        {
            LogError($"Error during delay: {delayEx.Message}");
        }
    }
}

// Data Seeding Logic
async Task SeedRollingSmaAsync(TradingSettings settings, IAlpacaDataClient dataClient, IAlpacaCryptoDataClient cryptoDataClient, Queue<decimal> priceQueue, TimeZoneInfo easternZone)
{
    Log("Seeding Rolling SMA Queue...");
    var utcNow = DateTime.UtcNow;
    var easternNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, easternZone);
    
    var earlyTradingEnd = new TimeSpan(9, 55, 0);
    var usesCrypto = easternNow.TimeOfDay < earlyTradingEnd;
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
}

// Execution Logic: Enter Position
async Task EnsurePositionAsync(
    string targetSymbol, 
    string oppositeSymbol, 
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath)
{
    // Get current positions from Alpaca
    var positions = await tradingClient.ListPositionsAsync();
    var targetPosition = positions.FirstOrDefault(p => p.Symbol.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase));
    var oppositePosition = positions.FirstOrDefault(p => p.Symbol.Equals(oppositeSymbol, StringComparison.OrdinalIgnoreCase));
    
    // Also check local state
    var localHoldsTarget = tradingState.CurrentPosition?.Equals(targetSymbol, StringComparison.OrdinalIgnoreCase) ?? false;
    var localHoldsOpposite = tradingState.CurrentPosition?.Equals(oppositeSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

    // 1. Liquidate Opposite if held
    if (oppositePosition != null || localHoldsOpposite)
    {
        Log($"Current signal targets {targetSymbol}, but holding {oppositeSymbol}. Liquidating...");
        await LiquidatePositionAsync(oppositeSymbol, oppositePosition, tradingState, tradingClient, dataClient, stateFilePath);
    }

    // 2. Buy Target if not held
    var alreadyHoldsTarget = targetPosition != null || localHoldsTarget;
    if (!alreadyHoldsTarget)
    {
        await BuyPositionAsync(targetSymbol, tradingState, tradingClient, dataClient, stateFilePath);
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
        await LiquidatePositionAsync(settings.BullSymbol, bullPosition, tradingState, tradingClient, dataClient, stateFilePath);
    }

    // Check for Bear Symbol
    var bearPosition = positions.FirstOrDefault(p => p.Symbol.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase));
    var localHoldsBear = tradingState.CurrentPosition?.Equals(settings.BearSymbol, StringComparison.OrdinalIgnoreCase) ?? false;

    if (bearPosition != null || localHoldsBear)
    {
         Log($"Signal is {reason}. Liquidating {settings.BearSymbol} to Cash.");
         await LiquidatePositionAsync(settings.BearSymbol, bearPosition, tradingState, tradingClient, dataClient, stateFilePath);
    }

    if (showStatus)
    {
        Log($"[{reason}] Sitting in Cash ⚪");
    }
}


// Helper: Liquidate Position
async Task LiquidatePositionAsync(
    string symbol, 
    IPosition? position, 
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath)
{
    // Calculate expected sale proceeds before liquidating
    var quoteRequest = new LatestMarketDataRequest(symbol)
    {
        Feed = MarketDataFeed.Iex
    };
    var trade = await dataClient.GetLatestTradeAsync(quoteRequest);
    var price = trade.Price;
    
    // Use Alpaca position quantity if available, otherwise use local state
    var shareCount = position?.Quantity ?? tradingState.CurrentShares;
    var saleProceeds = shareCount * price;
    
    // Liquidate
    Log($"[SELL] Liquidating {shareCount} shares of {symbol} @ ~${price:N2}");
    Log($"       Expected proceeds: ${saleProceeds:N2}");
    
    try
    {
        if (position != null)
        {
            await tradingClient.DeletePositionAsync(new DeletePositionRequest(symbol));
        }
        else
        {
            Log($"[WARN] Position {symbol} not found on Alpaca. Updating local state to match.");
        }
    }
    catch (Exception ex)
    {
        // 404 Not Found is common if position was already closed
        if (ex.Message.Contains("position not found"))
        {
            Log($"[WARN] Position already closed ({ex.Message}). updating local state.");
        }
        else
        {
            throw; // Re-throw other errors
        }
    }
    
    // Update trading state
    tradingState.AvailableCash = saleProceeds + tradingState.AccumulatedLeftover;
    tradingState.AccumulatedLeftover = 0m; 
    tradingState.CurrentPosition = null;
    tradingState.CurrentShares = 0;
    SaveTradingState(stateFilePath, tradingState);
    
    // Display balance and P/L after sale
    var totalBalance = tradingState.AvailableCash + tradingState.AccumulatedLeftover;
    var profitLoss = totalBalance - tradingState.StartingAmount;
    LogBalanceWithPL(totalBalance, profitLoss);
    
    // Small delay to allow order to process
    await Task.Delay(2000);
}

// Helper: Buy Position
async Task BuyPositionAsync(
    string symbol,
    TradingState tradingState, 
    IAlpacaTradingClient tradingClient, 
    IAlpacaDataClient dataClient, 
    string stateFilePath)
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
        Log($"[BUY] {symbol} x {quantity} @ ~${price:N2}");
        Log($"       Total cost: ${totalCost:N2}");
        Log($"       Leftover cash: ${leftover:N2}");
        
        var orderRequest = new NewOrderRequest(
            symbol,
            OrderQuantity.FromInt64(quantity),
            OrderSide.Buy,
            OrderType.Market,
            TimeInForce.Day
        );

        var order = await tradingClient.PostOrderAsync(orderRequest);
        LogSuccess($"Order submitted: {order.OrderId}");
        
        // Update trading state
        tradingState.AvailableCash = 0m; 
        tradingState.AccumulatedLeftover = leftover; 
        tradingState.CurrentPosition = symbol;
        tradingState.CurrentShares = quantity;
        tradingState.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
        SaveTradingState(stateFilePath, tradingState);
        
        Log($"       State saved. Accumulated leftover: ${tradingState.AccumulatedLeftover:N2}");
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
    public int PollingIntervalSeconds { get; set; } = 5;
    public string BullSymbol { get; set; } = "TQQQ";
    public string BearSymbol { get; set; } = "SQQQ";
    public string BenchmarkSymbol { get; set; } = "QQQ";
    public string CryptoBenchmarkSymbol { get; set; } = "BTC/USD";
    public int SMALength { get; set; } = 12;
    public decimal ChopThresholdPercent { get; set; } = 0.0015m;
    public int NeutralWaitSeconds { get; set; } = 30;
    public decimal StartingAmount { get; set; } = 10000m;
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
}

// Marker class for user secrets
partial class Program { }
