using MarketBlocks.Trade.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace qqqBot.Tests;

/// <summary>
/// Tests for the SimulatedBroker — the "Fake Broker" used in replay mode.
/// Validates order fills, slippage, cash tracking, position management, and P/L.
/// </summary>
public class SimulatedBrokerTests
{
    private static SimulatedBroker CreateBroker(
        decimal initialCash = 30_000m,
        decimal slippageBps = 0m,
        decimal spreadBps = 0m,
        bool volatilitySlippageEnabled = false)
        => new(NullLogger.Instance, initialCash,
            slippageBps: slippageBps,
            spreadBps: spreadBps,
            ovSpreadMultiplier: 1.0m,
            phSpreadMultiplier: 1.0m,
            volatilitySlippageEnabled: volatilitySlippageEnabled,
            volSlippageMultiplier: 0m,
            volWindowTicks: 60);

    // ───────────────────────── Buy Order Tests ─────────────────────────

    /// <summary>
    /// "The Clean Fill" — a basic buy order deducts cash correctly.
    /// </summary>
    [Fact]
    public async Task BuyOrder_DeductsCash_AndCreatesPosition()
    {
        var broker = CreateBroker(initialCash: 10_000m);
        broker.UpdatePrice("TQQQ", 100m);

        var order = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketBuy("TQQQ", 50), CancellationToken.None);

        Assert.Equal(BotOrderStatus.Filled, order.Status);
        Assert.Equal(50, order.FilledQuantity);
        Assert.Equal(100m, order.AverageFillPrice);

        // Cash: 10000 - (100 * 50) = 5000
        var cash = await broker.GetBuyingPowerAsync();
        Assert.Equal(5_000m, cash);

        // Position should exist
        var pos = await broker.GetPositionAsync("TQQQ");
        Assert.NotNull(pos);
        Assert.Equal(50, pos.Value.Quantity);
        Assert.Equal(100m, pos.Value.AverageEntryPrice);
    }

    /// <summary>
    /// "The Slippage Tax" — buy order fills at price + slippage.
    /// With 0.01% slippage on $100, slippage = $0.01, fill = $100.01.
    /// </summary>
    [Fact]
    public async Task BuyOrder_AppliesSlippageUpward()
    {
        var broker = CreateBroker(initialCash: 50_000m, slippageBps: 1m);
        broker.UpdatePrice("QQQ", 500m);

        var order = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketBuy("QQQ", 10), CancellationToken.None);

        // slippage = 500 * 0.0001 = 0.05. Fill = 500.05
        Assert.Equal(500.05m, order.AverageFillPrice);
        Assert.Equal(BotOrderStatus.Filled, order.Status);
    }

    /// <summary>
    /// "Broke Trader" — insufficient funds rejects the order.
    /// </summary>
    [Fact]
    public async Task BuyOrder_InsufficientFunds_IsRejected()
    {
        var broker = CreateBroker(initialCash: 1_000m);
        broker.UpdatePrice("TQQQ", 100m);

        // Trying to buy 100 shares @ $100 = $10,000, but only have $1,000
        var order = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketBuy("TQQQ", 100), CancellationToken.None);

        Assert.Equal(BotOrderStatus.Rejected, order.Status);
        Assert.Equal(0, order.FilledQuantity);

        // Cash unchanged
        var cash = await broker.GetBuyingPowerAsync();
        Assert.Equal(1_000m, cash);
    }

    /// <summary>
    /// "The Ghost Order" — no price data means the order is rejected.
    /// </summary>
    [Fact]
    public async Task BuyOrder_NoPriceData_IsRejected()
    {
        var broker = CreateBroker();

        // Never called UpdatePrice — symbol has no price
        var order = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketBuy("UNKNOWN", 10), CancellationToken.None);

        Assert.Equal(BotOrderStatus.Rejected, order.Status);
    }

    // ───────────────────────── Sell Order Tests ─────────────────────────

    /// <summary>
    /// "The Round Trip" — buy then sell, verify realized P/L.
    /// Buy 100 @ $50, sell 100 @ $60 = $1000 profit.
    /// </summary>
    [Fact]
    public async Task SellOrder_RealizesProfit_AndRemovesPosition()
    {
        var broker = CreateBroker(initialCash: 10_000m);
        broker.UpdatePrice("TQQQ", 50m);

        // Buy 100 shares @ $50 → cash = 10000 - 5000 = 5000
        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("TQQQ", 100), CancellationToken.None);

        // Price rises
        broker.UpdatePrice("TQQQ", 60m);

        // Sell 100 shares @ $60 → cash = 5000 + 6000 = 11000
        var sell = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketSell("TQQQ", 100), CancellationToken.None);

        Assert.Equal(BotOrderStatus.Filled, sell.Status);
        Assert.Equal(60m, sell.AverageFillPrice);

        // Cash should be $11,000
        var cash = await broker.GetBuyingPowerAsync();
        Assert.Equal(11_000m, cash);

        // Position should be gone
        var pos = await broker.GetPositionAsync("TQQQ");
        Assert.Null(pos);
    }

    /// <summary>
    /// "The Slippage Tax (Sell Side)" — sell fills slightly lower than market.
    /// </summary>
    [Fact]
    public async Task SellOrder_AppliesSlippageDownward()
    {
        var broker = CreateBroker(initialCash: 50_000m, slippageBps: 10m); // 10 bps = 0.1%
        broker.UpdatePrice("QQQ", 500m);

        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("QQQ", 10), CancellationToken.None);

        var sell = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketSell("QQQ", 10), CancellationToken.None);

        // Sell slippage: 500 - (500 * 10/10000) = 500 - 0.50 = 499.50
        Assert.Equal(499.50m, sell.AverageFillPrice);
    }

    // ───────────────────────── Position Averaging Tests ─────────────────────────

    /// <summary>
    /// "Dollar-Cost Averaging" — two buys at different prices average correctly.
    /// Buy 50 @ $100, then buy 50 @ $110. Average = ($5000 + $5500) / 100 = $105.
    /// </summary>
    [Fact]
    public async Task MultipleBuys_AverageEntryPriceIsWeightedCorrectly()
    {
        var broker = CreateBroker(initialCash: 50_000m);

        broker.UpdatePrice("TQQQ", 100m);
        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("TQQQ", 50), CancellationToken.None);

        broker.UpdatePrice("TQQQ", 110m);
        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("TQQQ", 50), CancellationToken.None);

        var pos = await broker.GetPositionAsync("TQQQ");
        Assert.NotNull(pos);
        Assert.Equal(100, pos.Value.Quantity);
        Assert.Equal(105m, pos.Value.AverageEntryPrice);
    }

    /// <summary>
    /// "Partial Liquidation" — sell half the position, the rest stays.
    /// </summary>
    [Fact]
    public async Task PartialSell_ReducesPosition_KeepsAverage()
    {
        var broker = CreateBroker(initialCash: 50_000m);
        broker.UpdatePrice("TQQQ", 100m);

        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("TQQQ", 100), CancellationToken.None);

        broker.UpdatePrice("TQQQ", 120m);
        await broker.SubmitOrderAsync(BotOrderRequest.MarketSell("TQQQ", 50), CancellationToken.None);

        var pos = await broker.GetPositionAsync("TQQQ");
        Assert.NotNull(pos);
        Assert.Equal(50, pos.Value.Quantity);
        Assert.Equal(100m, pos.Value.AverageEntryPrice); // Average unchanged
    }

    // ───────────────────────── Multi-Symbol Tests ─────────────────────────

    /// <summary>
    /// "Multi-Asset Trader" — positions for different symbols are independent.
    /// </summary>
    [Fact]
    public async Task MultiSymbol_PositionsAreIndependent()
    {
        var broker = CreateBroker(initialCash: 100_000m);
        broker.UpdatePrice("TQQQ", 50m);
        broker.UpdatePrice("SQQQ", 30m);

        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("TQQQ", 100), CancellationToken.None);
        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("SQQQ", 200), CancellationToken.None);

        var tqqqPos = await broker.GetPositionAsync("TQQQ");
        var sqqqPos = await broker.GetPositionAsync("SQQQ");
        Assert.NotNull(tqqqPos);
        Assert.NotNull(sqqqPos);
        Assert.Equal(100, tqqqPos.Value.Quantity);
        Assert.Equal(200, sqqqPos.Value.Quantity);

        var allPositions = await broker.GetAllPositionsAsync();
        Assert.Equal(2, allPositions.Count);

        // Cash: 100000 - (50*100) - (30*200) = 100000 - 5000 - 6000 = 89000
        var cash = await broker.GetBuyingPowerAsync();
        Assert.Equal(89_000m, cash);
    }

    // ───────────────────────── Query Method Tests ─────────────────────────

    /// <summary>
    /// GetPositionAsync returns null for symbols we don't hold.
    /// </summary>
    [Fact]
    public async Task GetPosition_NoPosition_ReturnsNull()
    {
        var broker = CreateBroker();
        var pos = await broker.GetPositionAsync("NVDA");
        Assert.Null(pos);
    }

    /// <summary>
    /// GetLatestPriceAsync throws when no price has been set.
    /// </summary>
    [Fact]
    public async Task GetLatestPrice_NoPriceData_Throws()
    {
        var broker = CreateBroker();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.GetLatestPriceAsync("NVDA"));
    }

    /// <summary>
    /// GetLatestPriceAsync returns the most recently updated price.
    /// </summary>
    [Fact]
    public async Task GetLatestPrice_ReturnsUpdatedPrice()
    {
        var broker = CreateBroker();
        broker.UpdatePrice("QQQ", 500m);
        broker.UpdatePrice("QQQ", 510m);

        var price = await broker.GetLatestPriceAsync("QQQ");
        Assert.Equal(510m, price);
    }

    /// <summary>
    /// ValidateSymbolAsync always returns true in simulation.
    /// </summary>
    [Fact]
    public async Task ValidateSymbol_AlwaysReturnsTrue()
    {
        var broker = CreateBroker();
        var valid = await broker.ValidateSymbolAsync("ANYTHING");
        Assert.True(valid);
    }

    /// <summary>
    /// CancelOrderAsync always returns false (orders fill instantly).
    /// </summary>
    [Fact]
    public async Task CancelOrder_AlwaysReturnsFalse()
    {
        var broker = CreateBroker();
        var result = await broker.CancelOrderAsync(Guid.NewGuid());
        Assert.False(result);
    }

    /// <summary>
    /// GetOrderAsync returns a previously submitted order.
    /// </summary>
    [Fact]
    public async Task GetOrder_ReturnsSubmittedOrder()
    {
        var broker = CreateBroker();
        broker.UpdatePrice("TQQQ", 100m);

        var submitted = await broker.SubmitOrderAsync(
            BotOrderRequest.MarketBuy("TQQQ", 10), CancellationToken.None);

        var retrieved = await broker.GetOrderAsync(submitted.OrderId);
        Assert.Equal(submitted.OrderId, retrieved.OrderId);
        Assert.Equal(BotOrderStatus.Filled, retrieved.Status);
    }

    /// <summary>
    /// GetOrderAsync throws for unknown order IDs.
    /// </summary>
    [Fact]
    public async Task GetOrder_UnknownId_Throws()
    {
        var broker = CreateBroker();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.GetOrderAsync(Guid.NewGuid()));
    }

    // ───────────────────────── Limit Price Tests ─────────────────────────

    /// <summary>
    /// When a limit price is provided, slippage is applied to the limit price, not latest.
    /// </summary>
    [Fact]
    public async Task LimitOrder_UsesLimitPriceForSlippageBase()
    {
        var broker = CreateBroker(initialCash: 50_000m, slippageBps: 10m); // 10 bps = 0.1%
        broker.UpdatePrice("QQQ", 500m);

        // Limit buy at $495 — slippage applies to $495 not $500
        var order = await broker.SubmitOrderAsync(
            BotOrderRequest.LimitBuy("QQQ", 10, 495m), CancellationToken.None);

        // Fill = 495 + (495 * 10/10000) = 495 + 0.495 = 495.50 (rounded)
        Assert.Equal(495.50m, order.AverageFillPrice);
    }

    // ───────────────────────── Edge Cases ─────────────────────────

    /// <summary>
    /// GetAllPositionsAsync returns empty when no positions held.
    /// </summary>
    [Fact]
    public async Task GetAllPositions_EmptyByDefault()
    {
        var broker = CreateBroker();
        var all = await broker.GetAllPositionsAsync();
        Assert.Empty(all);
    }

    /// <summary>
    /// Position shows current market value based on latest price.
    /// </summary>
    [Fact]
    public async Task Position_ReflectsLatestMarketValue()
    {
        var broker = CreateBroker(initialCash: 50_000m);
        broker.UpdatePrice("TQQQ", 100m);
        await broker.SubmitOrderAsync(BotOrderRequest.MarketBuy("TQQQ", 100), CancellationToken.None);

        // Price moves up
        broker.UpdatePrice("TQQQ", 120m);

        var pos = await broker.GetPositionAsync("TQQQ");
        Assert.NotNull(pos);
        Assert.Equal(120m, pos.Value.CurrentPrice);
        Assert.Equal(12_000m, pos.Value.MarketValue); // 100 * 120
    }
}
