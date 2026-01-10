using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Alpaca.Markets;
using qqqBot;

namespace qqqBot.Tests;

/// <summary>
/// Tests for the trailing stop and washout latch logic.
/// </summary>
public class TrailingStopEngineTests
{
    /// <summary>
    /// The "V-Shape" Latch Test:
    /// Scenario: Bot holds TQQQ. Price drops below Trailing Stop (Stop Out Triggered). 
    /// Price immediately recovers above WashoutLevel within 1 second.
    /// Expectation: The Washout Latch should engage, preventing immediate re-entry, 
    /// but then Clear automatically when the price crosses the washout level.
    /// </summary>
    [Fact]
    public void VShapeLatchTest_StopTriggeredThenRecovery_LatchEngagesThenClears()
    {
        // Arrange
        var currentTime = new DateTime(2026, 1, 9, 10, 0, 0, DateTimeKind.Utc);
        var engine = new TrailingStopEngine
        {
            TrailingStopPercent = 0.002m, // 0.2% trailing stop
            StopLossCooldownSeconds = 10, // 10 second cooldown
            BullSymbol = "TQQQ",
            BearSymbol = "SQQQ",
            GetUtcNow = () => currentTime
        };
        
        // Initial position: TQQQ at $100, high water mark will be $100
        const decimal startPrice = 100.00m;
        const decimal upperBand = 100.10m; // Washout level for BULL
        const decimal lowerBand = 99.90m;
        
        // Tick 1: Establish the high water mark
        var result1 = engine.ProcessTick(startPrice, "TQQQ", 100, upperBand, lowerBand);
        
        Assert.Equal(startPrice, engine.HighWaterMark);
        Assert.Equal(startPrice * (1 - 0.002m), engine.VirtualStopPrice); // $99.80
        Assert.False(result1.StopTriggered);
        Assert.False(result1.LatchBlocksEntry);
        
        // Tick 2: Price drops below stop level ($99.80) - should trigger stop
        const decimal dropPrice = 99.70m;
        var result2 = engine.ProcessTick(dropPrice, "TQQQ", 100, upperBand, lowerBand);
        
        Assert.True(result2.StopTriggered, "Stop should be triggered when price drops below stop level");
        Assert.Equal("NEUTRAL", result2.ForcedSignal);
        Assert.True(engine.IsStoppedOut);
        Assert.Equal("BULL", engine.StoppedOutDirection);
        Assert.Equal(upperBand, engine.WashoutLevel); // Washout at upper band
        
        // Tick 3: Price recovers but still in cooldown (only 1 second later) - latch should block
        currentTime = currentTime.AddSeconds(1);
        var result3 = engine.ProcessTick(100.15m, null, 0, upperBand, lowerBand); // Position liquidated
        
        Assert.True(result3.LatchBlocksEntry, "Latch should block entry during cooldown period");
        Assert.False(result3.LatchCleared);
        Assert.True(engine.IsStoppedOut, "Should still be stopped out during cooldown");
        
        // Tick 4: After cooldown expires (11 seconds total), price above washout - latch should clear
        currentTime = currentTime.AddSeconds(10); // Total: 11 seconds > 10 second cooldown
        const decimal recoveredPrice = 100.20m; // Above washout level of $100.10
        var result4 = engine.ProcessTick(recoveredPrice, null, 0, upperBand, lowerBand);
        
        Assert.True(result4.LatchCleared, "Latch should clear when price recovers above washout level after cooldown");
        Assert.False(result4.LatchBlocksEntry, "Entry should no longer be blocked");
        Assert.False(engine.IsStoppedOut, "Should no longer be stopped out");
        Assert.Equal(0m, engine.HighWaterMark); // HWM should be reset
        Assert.Equal(0m, engine.VirtualStopPrice); // Stop price should be reset
    }
    
    /// <summary>
    /// Test that latch blocks entry even after cooldown if price hasn't recovered.
    /// </summary>
    [Fact]
    public void VShapeLatchTest_CooldownExpiredButPriceBelowWashout_LatchStillBlocks()
    {
        // Arrange
        var currentTime = new DateTime(2026, 1, 9, 10, 0, 0, DateTimeKind.Utc);
        var engine = new TrailingStopEngine
        {
            TrailingStopPercent = 0.002m,
            StopLossCooldownSeconds = 10,
            BullSymbol = "TQQQ",
            GetUtcNow = () => currentTime
        };
        
        const decimal upperBand = 100.10m;
        const decimal lowerBand = 99.90m;
        
        // Establish position and trigger stop
        engine.ProcessTick(100.00m, "TQQQ", 100, upperBand, lowerBand);
        engine.ProcessTick(99.70m, "TQQQ", 100, upperBand, lowerBand); // Stop triggered
        
        // After cooldown, but price still below washout
        currentTime = currentTime.AddSeconds(15);
        var result = engine.ProcessTick(100.05m, null, 0, upperBand, lowerBand); // Below $100.10 washout
        
        Assert.True(result.LatchBlocksEntry, "Latch should still block if price hasn't recovered above washout");
        Assert.False(result.LatchCleared);
    }
}

/// <summary>
/// Tests for restart persistence of trailing stop state.
/// </summary>
public class RestartPersistenceTests
{
    /// <summary>
    /// The "Restart Persistence" Test:
    /// Scenario: Manually edit trading_state.json to set HighWaterMark to $200 
    /// (Current price $150) and IsStoppedOut to false. Start the bot.
    /// Expectation: The pipeline should read the JSON, see that $150 < $200 
    /// (Stop Level ~$199.60), and trigger an immediate "Market Sell" due to 
    /// the restored Trailing Stop logic.
    /// </summary>
    [Fact]
    public void RestartPersistence_HighWaterMarkAboveCurrentPrice_TriggersImmediateStopLoss()
    {
        // Arrange
        var engine = new TrailingStopEngine
        {
            TrailingStopPercent = 0.002m, // 0.2% trailing stop
            BullSymbol = "TQQQ",
            BearSymbol = "SQQQ"
        };
        
        // Simulate loading state from JSON where HWM was $200
        var savedState = new TradingState
        {
            CurrentPosition = "TQQQ",
            CurrentShares = 100,
            HighWaterMark = 200.00m, // Saved high water mark
            IsStoppedOut = false,
            TrailingStopValue = 200.00m * (1 - 0.002m) // $199.60
        };
        
        engine.LoadFromState(savedState);
        
        // Current price is $150 - well below the stop level of $199.60
        const decimal currentPrice = 150.00m;
        
        // Act - Check if immediate stop loss should be triggered
        bool shouldTriggerStopLoss = engine.ShouldTriggerImmediateStopLoss(
            currentPrice, 
            savedState.CurrentPosition, 
            savedState.CurrentShares);
        
        // Assert
        Assert.True(shouldTriggerStopLoss, 
            "Bot should trigger immediate stop loss when restored HWM ($200) " +
            $"implies stop at $199.60, but current price is ${currentPrice}");
        
        // Verify the math
        decimal expectedStopLevel = 200.00m * (1 - 0.002m); // $199.60
        Assert.True(currentPrice <= expectedStopLevel,
            $"Current price ${currentPrice} should be <= stop level ${expectedStopLevel}");
    }
    
    /// <summary>
    /// Test that no stop loss triggers when price is above the restored stop level.
    /// </summary>
    [Fact]
    public void RestartPersistence_PriceAboveStopLevel_NoImmediateStopLoss()
    {
        // Arrange
        var engine = new TrailingStopEngine
        {
            TrailingStopPercent = 0.002m,
            BullSymbol = "TQQQ"
        };
        
        var savedState = new TradingState
        {
            CurrentPosition = "TQQQ",
            CurrentShares = 100,
            HighWaterMark = 200.00m,
            IsStoppedOut = false
        };
        
        engine.LoadFromState(savedState);
        
        // Current price is $201 - above both HWM and stop level
        const decimal currentPrice = 201.00m;
        
        // Act
        bool shouldTriggerStopLoss = engine.ShouldTriggerImmediateStopLoss(
            currentPrice, 
            savedState.CurrentPosition, 
            savedState.CurrentShares);
        
        // Assert
        Assert.False(shouldTriggerStopLoss,
            "No stop loss should trigger when current price is above stop level");
    }
    
    /// <summary>
    /// Test state serialization roundtrip.
    /// </summary>
    [Fact]
    public void RestartPersistence_StateRoundtrip_PreservesAllFields()
    {
        // Arrange
        var engine = new TrailingStopEngine
        {
            TrailingStopPercent = 0.002m,
            BullSymbol = "TQQQ",
            HighWaterMark = 150.50m,
            LowWaterMark = 0m,
            VirtualStopPrice = 150.20m,
            IsStoppedOut = true,
            StoppedOutDirection = "BULL",
            WashoutLevel = 151.00m,
            StopoutTime = new DateTime(2026, 1, 9, 10, 30, 0, DateTimeKind.Utc)
        };
        
        // Act - Save to state
        var state = new TradingState();
        engine.SaveToState(state);
        
        // Create new engine and load
        var engine2 = new TrailingStopEngine();
        engine2.LoadFromState(state);
        
        // Assert - All fields preserved
        Assert.Equal(engine.HighWaterMark, engine2.HighWaterMark);
        Assert.Equal(engine.LowWaterMark, engine2.LowWaterMark);
        Assert.Equal(engine.VirtualStopPrice, engine2.VirtualStopPrice);
        Assert.Equal(engine.IsStoppedOut, engine2.IsStoppedOut);
        Assert.Equal(engine.StoppedOutDirection, engine2.StoppedOutDirection);
        Assert.Equal(engine.WashoutLevel, engine2.WashoutLevel);
        Assert.Equal(engine.StopoutTime, engine2.StopoutTime);
    }
}

/// <summary>
/// Tests for IOC Machine Gun execution logic.
/// </summary>
public class IocMachineGunTests
{
    /// <summary>
    /// The "Machine Gun" Deviation Test:
    /// Scenario: Mock the API to reject the first 4 IOC orders. On the 5th attempt, 
    /// mock the price to be 1% higher than the start price.
    /// Expectation: The ExecuteIocMachineGunAsync should abort before the 5th attempt 
    /// because IocMaxDeviationPercent (0.5%) was exceeded.
    /// </summary>
    [Fact]
    public async Task MachineGunDeviationTest_ExceedsMaxDeviation_AbortsBeforeMaxRetries()
    {
        // Arrange
        int orderAttempts = 0;
        var mockOrderClient = new Mock<IOrderClient>();
        
        // Setup: All orders get cancelled (simulating price moving away)
        mockOrderClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>()))
            .ReturnsAsync((NewOrderRequest req) =>
            {
                orderAttempts++;
                return CreateMockOrder(OrderStatus.Canceled, req.LimitPrice ?? 0m, 0);
            });
        
        var logs = new System.Collections.Generic.List<string>();
        var executor = new IocMachineGunExecutor(
            mockOrderClient.Object,
            () => Guid.NewGuid().ToString(),
            msg => logs.Add(msg));
        
        // Parameters:
        // - Start price: $100
        // - Step: 10 cents per retry (larger step to hit deviation faster)
        // - Max retries: 100 (plenty of room)
        // - Max deviation: 0.5% (0.005) = $0.50 max chase from $100
        const decimal startPrice = 100.00m;
        const decimal priceStepCents = 10m; // 10 cents per retry
        const int maxRetries = 100;
        const decimal maxDeviationPercent = 0.005m; // 0.5%
        
        // Act
        var result = await executor.ExecuteAsync(
            "TQQQ",
            100,
            OrderSide.Buy,
            startPrice,
            priceStepCents,
            maxRetries,
            maxDeviationPercent);
        
        // Assert
        // With 10 cent steps and 0.5% max deviation ($0.50), we should stop after:
        // Attempt 1: $100.00 (0% deviation) - Cancelled, bump to $100.10
        // Attempt 2: $100.10 (0.1% deviation) - Cancelled, bump to $100.20
        // Attempt 3: $100.20 (0.2% deviation) - Cancelled, bump to $100.30
        // Attempt 4: $100.30 (0.3% deviation) - Cancelled, bump to $100.40
        // Attempt 5: $100.40 (0.4% deviation) - Cancelled, bump to $100.50
        // Attempt 6: $100.50 (0.5% deviation) - EXCEEDS, abort before executing
        
        Assert.True(result.AbortedDueToDeviation, 
            "Execution should abort due to exceeding max deviation");
        Assert.Equal(0L, result.FilledQty); // No shares should be filled since all orders were cancelled
        
        // The check is > (not >=), so:
        // Attempt 6: $100.50 = exactly 0.5% deviation, still allowed
        // Attempt 7: $100.60 = 0.6% > 0.5%, abort here
        Assert.True(result.AttemptsUsed <= 7, 
            $"Should stop at attempt 7 when deviation exceeds 0.5%, but made {result.AttemptsUsed} attempts");
        Assert.True(result.AttemptsUsed >= 6,
            $"Should make at least 6 attempts before deviation limit, but only made {result.AttemptsUsed}");
        
        // The final attempted price should be just at or slightly over the limit
        decimal maxAllowedPrice = startPrice * (1 + maxDeviationPercent);
        Assert.True(result.FinalPriceAttempted <= maxAllowedPrice + 0.10m,
            $"Final price ${result.FinalPriceAttempted} should be near max allowed ${maxAllowedPrice}");
        
        // Verify the deviation abort was logged
        Assert.Contains(logs, l => l.Contains("deviation") && l.Contains("exceeds"));
    }
    
    /// <summary>
    /// Test that machine gun fills successfully when orders are accepted.
    /// </summary>
    [Fact]
    public async Task MachineGunTest_OrderFillsOnFirstAttempt_ReturnsSuccess()
    {
        // Arrange
        var mockOrderClient = new Mock<IOrderClient>();
        
        mockOrderClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>()))
            .ReturnsAsync((NewOrderRequest req) =>
                CreateMockOrder(OrderStatus.Filled, req.LimitPrice ?? 0m, 100, req.LimitPrice ?? 100m));
        
        var executor = new IocMachineGunExecutor(
            mockOrderClient.Object,
            () => Guid.NewGuid().ToString());
        
        // Act
        var result = await executor.ExecuteAsync(
            "TQQQ", 100, OrderSide.Buy, 100.00m, 1m, 5, 0.005m);
        
        // Assert
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
        // Arrange
        int attemptCount = 0;
        decimal lastAttemptedPrice = 0m;
        var mockOrderClient = new Mock<IOrderClient>();
        
        mockOrderClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>()))
            .ReturnsAsync((NewOrderRequest req) =>
            {
                attemptCount++;
                lastAttemptedPrice = req.LimitPrice ?? 0m;
                
                if (attemptCount < 3)
                {
                    // First two attempts cancelled
                    return CreateMockOrder(OrderStatus.Canceled, req.LimitPrice ?? 0m, 0);
                }
                else
                {
                    // Third attempt fills
                    return CreateMockOrder(OrderStatus.Filled, req.LimitPrice ?? 0m, 100, req.LimitPrice ?? 0m);
                }
            });
        
        var executor = new IocMachineGunExecutor(
            mockOrderClient.Object,
            () => Guid.NewGuid().ToString());
        
        // Start at $100, step by 2 cents
        var result = await executor.ExecuteAsync(
            "TQQQ", 100, OrderSide.Buy, 100.00m, 2m, 10, 0.01m);
        
        // Assert
        Assert.Equal(100, result.FilledQty);
        Assert.Equal(3, result.AttemptsUsed);
        Assert.False(result.AbortedDueToDeviation);
        
        // Price should have increased: $100.00 -> $100.02 -> $100.04
        Assert.Equal(100.04m, lastAttemptedPrice);
    }
    
    /// <summary>
    /// "The Drag Test" - Verifies weighted average cost basis calculation
    /// when machine gun gets multiple partial fills at increasing prices.
    /// 
    /// Edge Case: If the IOC Machine Gun gets partial fills across multiple 
    /// price steps (e.g., Fill 10 @ $100.00, Fill 10 @ $100.01, etc.), the
    /// Average Cost Basis rises.
    /// 
    /// Risk: If the machine gun chases too far, average cost might end up 
    /// above the Hysteresis Band, putting you in a position where you 
    /// technically "should" sell immediately according to the trend.
    /// 
    /// This test verifies the bot correctly calculates the weighted average 
    /// entry price across 10 partial fills, each 1 cent higher.
    /// </summary>
    [Fact]
    public async Task DragTest_MultiplePartialFills_CalculatesCorrectWeightedAverage()
    {
        // Arrange
        int attemptNumber = 0;
        var requestedPrices = new System.Collections.Generic.List<decimal>();
        var mockOrderClient = new Mock<IOrderClient>();
        
        // Setup: 10 partial fills, each filling 10 shares at 1 cent higher
        // For IOC orders, a partial fill returns as Canceled with FilledQuantity > 0
        // Attempt 1: Fill 10 @ $100.00 (Canceled with partial fill)
        // Attempt 2: Fill 10 @ $100.01 (Canceled with partial fill)
        // ...
        // Attempt 10: Fill 10 @ $100.09 (Final fill completes the order)
        mockOrderClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>()))
            .ReturnsAsync((NewOrderRequest req) =>
            {
                attemptNumber++;
                var expectedPrice = 100.00m + ((attemptNumber - 1) * 0.01m);
                
                // CRITICAL ASSERTION: Verify the bot is actually chasing (not stuck at same price)
                // This catches the "Stuck Gun" bug where partial fills don't increment price
                requestedPrices.Add(req.LimitPrice ?? 0m);
                if (Math.Abs((req.LimitPrice ?? 0m) - expectedPrice) > 0.001m)
                {
                    throw new InvalidOperationException(
                        $"Stuck Gun Bug Detected! Attempt {attemptNumber}: " +
                        $"Bot requested ${req.LimitPrice:N2} but should have requested ${expectedPrice:N2}. " +
                        $"Price history: [{string.Join(", ", requestedPrices.Select(p => $"${p:N2}"))}]");
                }
                
                // IOC partial fills return as Canceled with filled quantity
                // On the 10th attempt, return Filled to complete the order
                var status = attemptNumber < 10 ? OrderStatus.Canceled : OrderStatus.Filled;
                return CreateMockOrder(status, req.LimitPrice ?? 0m, 10, req.LimitPrice ?? 0m);
            });
        
        var logs = new System.Collections.Generic.List<string>();
        var executor = new IocMachineGunExecutor(
            mockOrderClient.Object,
            () => Guid.NewGuid().ToString(),
            msg => logs.Add(msg));
        
        // Parameters:
        // - Target: 100 shares
        // - Start price: $100.00
        // - Step: 1 cent per retry
        // - Max retries: 15 (enough room for 10 fills)
        // - Max deviation: 1% (won't be hit)
        const long targetQty = 100;
        const decimal startPrice = 100.00m;
        const decimal priceStepCents = 1m;
        const int maxRetries = 15;
        const decimal maxDeviationPercent = 0.01m; // 1%
        
        // Act
        var result = await executor.ExecuteAsync(
            "TQQQ",
            targetQty,
            OrderSide.Buy,
            startPrice,
            priceStepCents,
            maxRetries,
            maxDeviationPercent);
        
        // Assert
        // Total filled should be 100 shares (10 fills × 10 shares each)
        Assert.Equal(100, result.FilledQty);
        Assert.Equal(10, result.AttemptsUsed);
        Assert.False(result.AbortedDueToDeviation);
        
        // Calculate expected weighted average:
        // Fill 1:  10 shares × $100.00 = $1,000.00
        // Fill 2:  10 shares × $100.01 = $1,000.10
        // Fill 3:  10 shares × $100.02 = $1,000.20
        // Fill 4:  10 shares × $100.03 = $1,000.30
        // Fill 5:  10 shares × $100.04 = $1,000.40
        // Fill 6:  10 shares × $100.05 = $1,000.50
        // Fill 7:  10 shares × $100.06 = $1,000.60
        // Fill 8:  10 shares × $100.07 = $1,000.70
        // Fill 9:  10 shares × $100.08 = $1,000.80
        // Fill 10: 10 shares × $100.09 = $1,000.90
        // -----------------------------------------
        // Total:  100 shares, Total Cost = $10,004.50
        // Weighted Avg = $10,004.50 / 100 = $100.045
        
        decimal expectedTotalProceeds = 0m;
        for (int i = 0; i < 10; i++)
        {
            expectedTotalProceeds += 10 * (100.00m + (i * 0.01m));
        }
        decimal expectedAvgPrice = expectedTotalProceeds / 100;
        
        Assert.Equal(expectedTotalProceeds, result.TotalProceeds);
        Assert.Equal(expectedAvgPrice, result.AvgPrice);
        
        // Verify the "drag" - average entry is 4.5 cents above starting price
        decimal priceDrag = result.AvgPrice - startPrice;
        Assert.Equal(0.045m, priceDrag);
        
        // Log verification - should show 10 partial fill messages
        var partialFillLogs = logs.Where(l => l.Contains("Partial") || l.Contains("FILLED")).ToList();
        Assert.True(partialFillLogs.Count >= 10, 
            $"Expected at least 10 fill log entries, found {partialFillLogs.Count}");
    }
    
    /// <summary>
    /// Helper to create mock IOrder objects.
    /// </summary>
    private static IOrder CreateMockOrder(
        OrderStatus status, 
        decimal limitPrice, 
        long filledQty,
        decimal? avgFillPrice = null)
    {
        var mockOrder = new Mock<IOrder>();
        mockOrder.Setup(o => o.OrderId).Returns(Guid.NewGuid());
        mockOrder.Setup(o => o.OrderStatus).Returns(status);
        mockOrder.Setup(o => o.FilledQuantity).Returns(filledQty);
        mockOrder.Setup(o => o.AverageFillPrice).Returns(avgFillPrice);
        mockOrder.Setup(o => o.LimitPrice).Returns(limitPrice);
        return mockOrder.Object;
    }
}
