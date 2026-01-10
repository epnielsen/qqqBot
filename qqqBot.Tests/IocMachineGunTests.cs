using Moq;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;
using MarketBlocks.Components;

namespace qqqBot.Tests;

/// <summary>
/// Tests for IOC Machine Gun execution logic (broker-agnostic using IBrokerExecution).
/// </summary>
public class IocMachineGunTests
{
    /// <summary>
    /// "The Drag Test" - Verifies weighted average cost basis calculation
    /// when machine gun gets multiple partial fills at increasing prices.
    /// </summary>
    [Fact]
    public async Task DragTest_MultiplePartialFills_CalculatesCorrectWeightedAverage()
    {
        // Arrange
        int attemptNumber = 0;
        var mockBroker = new Mock<IBrokerExecution>();

        // Setup: 10 partial fills, each filling 10 shares at 1 cent higher
        mockBroker
            .Setup(c => c.SubmitOrderAsync(It.IsAny<BotOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotOrderRequest req, CancellationToken ct) =>
            {
                attemptNumber++;
                var expectedPrice = 100.00m + ((attemptNumber - 1) * 0.01m);

                // CRITICAL: Verify the bot is actually chasing (not stuck at same price)
                if (Math.Abs((req.LimitPrice ?? 0m) - expectedPrice) > 0.001m)
                {
                    throw new InvalidOperationException(
                        $"Stuck Gun Bug! Wanted ${expectedPrice:N2}, got ${req.LimitPrice:N2}");
                }

                // Simulate partial fills - on 10th attempt, fill completely
                var isDone = attemptNumber == 10;

                return new BotOrder
                {
                    OrderId = Guid.NewGuid(),
                    Symbol = "TQQQ",
                    Side = BotOrderSide.Buy,
                    Type = BotOrderType.Limit,
                    Status = isDone ? BotOrderStatus.Filled : BotOrderStatus.Canceled,
                    Quantity = req.Quantity,
                    FilledQuantity = 10, // Fill 10 shares per attempt
                    AverageFillPrice = req.LimitPrice
                };
            });

        var executor = new IocMachineGunExecutor(mockBroker.Object, () => "test-id");

        // Act
        var result = await executor.ExecuteAsync("TQQQ", 100, BotOrderSide.Buy, 100.00m, 1m, 15, 0.01m);

        // Assert
        Assert.Equal(100, result.FilledQty);
        Assert.Equal(10, result.AttemptsUsed);

        // Verify the "drag" calculation: 4.5 cents expected
        // Fill prices: $100.00, $100.01, ..., $100.09
        // Average = $100.045
        decimal priceDrag = result.AvgPrice - 100.00m;
        Assert.Equal(0.045m, priceDrag);
    }

    /// <summary>
    /// The "Machine Gun" Deviation Test - aborts when price moves too far.
    /// </summary>
    [Fact]
    public async Task DeviationTest_AbortsWhenPriceMovesTooFar()
    {
        var mockBroker = new Mock<IBrokerExecution>();
        
        // Always return Canceled with 0 fills
        mockBroker
            .Setup(x => x.SubmitOrderAsync(It.IsAny<BotOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BotOrder 
            { 
                OrderId = Guid.NewGuid(), 
                Symbol = "TQQQ", 
                Side = BotOrderSide.Buy, 
                Type = BotOrderType.Limit,
                Status = BotOrderStatus.Canceled, 
                Quantity = 100, 
                FilledQuantity = 0 
            });

        var logs = new System.Collections.Generic.List<string>();
        var executor = new IocMachineGunExecutor(mockBroker.Object, () => "test", msg => logs.Add(msg));

        // Max deviation 0.5% ($0.50). Start $100. Step $0.10.
        // Should abort around attempt 6-7 when deviation > 0.5%
        var result = await executor.ExecuteAsync("TQQQ", 100, BotOrderSide.Buy, 100m, 10m, 20, 0.005m);

        Assert.True(result.AbortedDueToDeviation);
        Assert.True(result.AttemptsUsed >= 5, $"Expected at least 5 attempts, got {result.AttemptsUsed}");
        Assert.True(result.AttemptsUsed <= 7, $"Expected at most 7 attempts, got {result.AttemptsUsed}");
        Assert.Contains(logs, l => l.Contains("deviation") && l.Contains("exceeds"));
    }

    /// <summary>
    /// Test that machine gun fills successfully when orders are accepted.
    /// </summary>
    [Fact]
    public async Task MachineGunTest_OrderFillsOnFirstAttempt_ReturnsSuccess()
    {
        var mockBroker = new Mock<IBrokerExecution>();
        
        mockBroker
            .Setup(c => c.SubmitOrderAsync(It.IsAny<BotOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotOrderRequest req, CancellationToken _) => new BotOrder
            {
                OrderId = Guid.NewGuid(),
                Symbol = req.Symbol,
                Side = req.Side,
                Type = BotOrderType.Limit,
                Status = BotOrderStatus.Filled,
                Quantity = req.Quantity,
                FilledQuantity = req.Quantity,
                AverageFillPrice = req.LimitPrice
            });
        
        var executor = new IocMachineGunExecutor(mockBroker.Object, () => Guid.NewGuid().ToString());
        
        var result = await executor.ExecuteAsync("TQQQ", 100, BotOrderSide.Buy, 100.00m, 1m, 5, 0.005m);
        
        Assert.Equal(100, result.FilledQty);
        Assert.Equal(1, result.AttemptsUsed);
        Assert.False(result.AbortedDueToDeviation);
    }

    /// <summary>
    /// Test that machine gun retries and eventually fills.
    /// </summary>
    [Fact]
    public async Task MachineGunTest_FillsOnThirdAttempt_CorrectPriceProgression()
    {
        int attemptCount = 0;
        var mockBroker = new Mock<IBrokerExecution>();
        
        mockBroker
            .Setup(c => c.SubmitOrderAsync(It.IsAny<BotOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BotOrderRequest req, CancellationToken _) =>
            {
                attemptCount++;
                
                if (attemptCount < 3)
                {
                    // First two attempts cancelled
                    return new BotOrder
                    {
                        OrderId = Guid.NewGuid(),
                        Symbol = req.Symbol,
                        Side = req.Side,
                        Type = BotOrderType.Limit,
                        Status = BotOrderStatus.Canceled,
                        Quantity = req.Quantity,
                        FilledQuantity = 0
                    };
                }
                else
                {
                    // Third attempt fills
                    return new BotOrder
                    {
                        OrderId = Guid.NewGuid(),
                        Symbol = req.Symbol,
                        Side = req.Side,
                        Type = BotOrderType.Limit,
                        Status = BotOrderStatus.Filled,
                        Quantity = req.Quantity,
                        FilledQuantity = req.Quantity,
                        AverageFillPrice = req.LimitPrice
                    };
                }
            });
        
        var executor = new IocMachineGunExecutor(mockBroker.Object, () => Guid.NewGuid().ToString());
        
        // Start at $100, step by 2 cents
        var result = await executor.ExecuteAsync("TQQQ", 100, BotOrderSide.Buy, 100.00m, 2m, 10, 0.01m);
        
        Assert.Equal(100, result.FilledQty);
        Assert.Equal(3, result.AttemptsUsed);
        Assert.False(result.AbortedDueToDeviation);
        
        // Price should have increased: $100.00 -> $100.02 -> $100.04
        Assert.Equal(100.04m, result.FinalPriceAttempted);
    }
}

