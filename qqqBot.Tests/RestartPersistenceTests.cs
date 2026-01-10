namespace qqqBot.Tests;

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

