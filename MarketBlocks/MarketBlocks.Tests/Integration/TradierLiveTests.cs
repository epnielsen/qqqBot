using System.Threading.Channels;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;
using MarketBlocks.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace MarketBlocks.Tests.Integration;

/// <summary>
/// Live integration tests for Tradier brokerage adapters.
/// These tests require valid API credentials configured via User Secrets.
/// 
/// To set up credentials:
///   cd MarketBlocks.Tests
///   dotnet user-secrets init
///   dotnet user-secrets set "Tradier:AccessToken" "YOUR_ACCESS_TOKEN"
///   dotnet user-secrets set "Tradier:AccountId" "YOUR_ACCOUNT_ID"
///   dotnet user-secrets set "Tradier:UseSandbox" "true"
/// 
/// Run integration tests only:
///   dotnet test --filter "Category=Integration"
/// 
/// Skip integration tests:
///   dotnet test --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class TradierLiveTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private ServiceProvider? _serviceProvider;
    private IMarketDataSource? _dataSource;
    private IBrokerExecution? _execution;
    private bool _isConfigured;

    public TradierLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Build configuration from user secrets
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<TradierLiveTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var accessToken = configuration["Tradier:AccessToken"];
        var accountId = configuration["Tradier:AccountId"];

        if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(accountId))
        {
            _output.WriteLine("⚠️ Tradier credentials not configured. Skipping integration tests.");
            _output.WriteLine("To configure, run:");
            _output.WriteLine("  dotnet user-secrets init");
            _output.WriteLine("  dotnet user-secrets set \"Tradier:AccessToken\" \"YOUR_TOKEN\"");
            _output.WriteLine("  dotnet user-secrets set \"Tradier:AccountId\" \"YOUR_ACCOUNT_ID\"");
            _isConfigured = false;
            return;
        }

        _isConfigured = true;
        _output.WriteLine("✅ Tradier credentials loaded from User Secrets");

        // Build service provider
        var services = new ServiceCollection();
        services.AddTradier(configuration);
        _serviceProvider = services.BuildServiceProvider();

        // Resolve services
        _dataSource = _serviceProvider.GetRequiredService<IMarketDataSource>();
        _execution = _serviceProvider.GetRequiredService<IBrokerExecution>();

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }

        _serviceProvider?.Dispose();
    }

    [Fact]
    public async Task FullTradingLoopAsync()
    {
        // Skip if not configured
        if (!_isConfigured)
        {
            _output.WriteLine("Test skipped: credentials not configured.");
            return;
        }

        const string symbol = "MSFT";
        Guid? orderId = null;

        try
        {
            // ═══════════════════════════════════════════════════════════════════
            // Step 1: Connect to streaming service
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine("\n📡 Step 1: Connecting to Tradier streaming service...");
            await _dataSource!.ConnectAsync();
            Assert.True(_dataSource.IsConnected, "Failed to connect to streaming service");
            _output.WriteLine("   ✅ Connected successfully");

            // ═══════════════════════════════════════════════════════════════════
            // Step 2: Get a quote via REST API
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine($"\n💰 Step 2: Getting latest price for {symbol}...");
            var price = await _execution!.GetLatestPriceAsync(symbol);
            Assert.True(price > 0, "Price should be positive");
            _output.WriteLine($"   ✅ {symbol} price: ${price:F2}");

            // ═══════════════════════════════════════════════════════════════════
            // Step 3: Subscribe and receive streaming ticks
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine($"\n📊 Step 3: Subscribing to {symbol} trades...");
            var channel = Channel.CreateUnbounded<TradeTick>();
            await _dataSource.SubscribeAsync(symbol, channel.Writer, isBenchmark: true);
            _output.WriteLine("   ⏳ Waiting for 1-2 trade ticks (30 second timeout)...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tickCount = 0;
            var targetTicks = 2;

            try
            {
                await foreach (var tick in channel.Reader.ReadAllAsync(cts.Token))
                {
                    tickCount++;
                    _output.WriteLine($"   📈 Tick {tickCount}: {tick.Symbol} @ ${tick.Price:F2} at {tick.TimestampUtc:HH:mm:ss.fff}");
                    
                    if (tickCount >= targetTicks)
                        break;
                }
                _output.WriteLine($"   ✅ Received {tickCount} ticks");
            }
            catch (OperationCanceledException) when (tickCount == 0)
            {
                _output.WriteLine("   ⚠️ No ticks received (market may be closed). Continuing...");
            }

            // ═══════════════════════════════════════════════════════════════════
            // Step 4: Submit a safe limit order (very low price, won't execute)
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine($"\n📝 Step 4: Submitting safe limit order...");
            var safePrice = 10.00m; // Very low price - won't execute
            var orderRequest = BotOrderRequest.LimitBuy(symbol, 1, safePrice, "integration-test");
            
            _output.WriteLine($"   Order: Buy 1 {symbol} @ ${safePrice:F2} (Limit, Day)");
            var order = await _execution.SubmitOrderAsync(orderRequest);
            orderId = order.OrderId;
            
            Assert.NotEqual(Guid.Empty, order.OrderId);
            _output.WriteLine($"   ✅ Order submitted: ID = {order.OrderId}");
            _output.WriteLine($"   Status: {order.Status}");

            // ═══════════════════════════════════════════════════════════════════
            // Step 5: Verify order exists
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine($"\n🔍 Step 5: Verifying order status...");
            var fetchedOrder = await _execution.GetOrderAsync(order.OrderId);
            Assert.Equal(order.OrderId, fetchedOrder.OrderId);
            Assert.Equal(symbol, fetchedOrder.Symbol);
            _output.WriteLine($"   ✅ Order verified: {fetchedOrder.Symbol}, Status: {fetchedOrder.Status}");

            // ═══════════════════════════════════════════════════════════════════
            // Step 6: Cancel the order
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine($"\n❌ Step 6: Cancelling order...");
            var canceled = await _execution.CancelOrderAsync(order.OrderId);
            Assert.True(canceled, "Order should be cancelable");
            _output.WriteLine($"   ✅ Order cancel request accepted");

            // Verify cancellation
            await Task.Delay(500); // Brief delay for order status to update
            var canceledOrder = await _execution.GetOrderAsync(order.OrderId);
            _output.WriteLine($"   Final status: {canceledOrder.Status}");
            orderId = null; // Clear so cleanup doesn't try to cancel again

            // ═══════════════════════════════════════════════════════════════════
            // Step 7: Disconnect
            // ═══════════════════════════════════════════════════════════════════
            _output.WriteLine($"\n🔌 Step 7: Disconnecting...");
            await _dataSource.DisconnectAsync();
            Assert.False(_dataSource.IsConnected);
            _output.WriteLine("   ✅ Disconnected successfully");

            _output.WriteLine("\n" + new string('═', 60));
            _output.WriteLine("🎉 FULL TRADING LOOP COMPLETED SUCCESSFULLY!");
            _output.WriteLine(new string('═', 60));
        }
        finally
        {
            // Cleanup: ensure order is canceled if test failed mid-way
            if (orderId.HasValue && _execution != null)
            {
                try
                {
                    _output.WriteLine($"\n🧹 Cleanup: Cancelling order {orderId.Value}...");
                    await _execution.CancelOrderAsync(orderId.Value);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"   ⚠️ Cleanup cancel failed: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public async Task GetBuyingPowerAsync()
    {
        if (!_isConfigured)
        {
            _output.WriteLine("Test skipped: credentials not configured.");
            return;
        }

        _output.WriteLine("💵 Getting account buying power...");
        var buyingPower = await _execution!.GetBuyingPowerAsync();
        _output.WriteLine($"   Buying Power: ${buyingPower:N2}");
        Assert.True(buyingPower >= 0, "Buying power should be non-negative");
    }

    [Fact]
    public async Task GetPositionsAsync()
    {
        if (!_isConfigured)
        {
            _output.WriteLine("Test skipped: credentials not configured.");
            return;
        }

        _output.WriteLine("📊 Getting all positions...");
        var positions = await _execution!.GetAllPositionsAsync();
        _output.WriteLine($"   Found {positions.Count} position(s)");
        
        foreach (var pos in positions)
        {
            _output.WriteLine($"   • {pos.Symbol}: {pos.Quantity} shares @ ${pos.AverageEntryPrice:F2} (Value: ${pos.MarketValue:N2})");
        }
    }

    [Fact]
    public async Task ValidateSymbolAsync()
    {
        if (!_isConfigured)
        {
            _output.WriteLine("Test skipped: credentials not configured.");
            return;
        }

        _output.WriteLine("🔍 Validating symbols...");
        
        var validSymbol = await _execution!.ValidateSymbolAsync("AAPL");
        Assert.True(validSymbol, "AAPL should be valid");
        _output.WriteLine("   ✅ AAPL is valid");

        var invalidSymbol = await _execution.ValidateSymbolAsync("NOTAREALSYMBOL123");
        Assert.False(invalidSymbol, "NOTAREALSYMBOL123 should be invalid");
        _output.WriteLine("   ✅ NOTAREALSYMBOL123 is invalid (as expected)");
    }

    [Fact]
    public async Task GetHistoricalPricesAsync()
    {
        if (!_isConfigured)
        {
            _output.WriteLine("Test skipped: credentials not configured.");
            return;
        }

        _output.WriteLine("📈 Getting historical prices for SPY...");
        
        await _dataSource!.ConnectAsync();
        var prices = await _dataSource.GetHistoricalPricesAsync("SPY", 10);
        
        _output.WriteLine($"   Retrieved {prices.Count} prices:");
        for (int i = 0; i < prices.Count; i++)
        {
            _output.WriteLine($"   Day {i + 1}: ${prices[i]:F2}");
        }

        Assert.True(prices.Count > 0, "Should have some historical prices");
        Assert.All(prices, p => Assert.True(p > 0, "Prices should be positive"));
    }
}
