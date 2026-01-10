using System;
using System.Threading.Tasks;
using Xunit;
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

