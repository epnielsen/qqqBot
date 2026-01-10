using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Alpaca.Markets;
using qqqBot;
using qqqBot.Core.Domain;
using qqqBot.Core.Interfaces;

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

