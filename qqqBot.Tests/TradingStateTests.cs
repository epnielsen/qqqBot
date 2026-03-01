using qqqBot;
using MarketBlocks.Bots.Domain;

namespace qqqBot.Tests;

public class TradingStateTests
{
    [Fact]
    public void NewTradingState_HasDefaultValues()
    {
        var state = new TradingState();

        Assert.Equal(0m, state.AvailableCash);
        Assert.Equal(0m, state.AccumulatedLeftover);
        Assert.False(state.IsInitialized);
        Assert.Null(state.LastTradeTimestamp);
        Assert.Null(state.CurrentPosition);
        Assert.Equal(0, state.CurrentShares);
        Assert.Equal(0m, state.StartingAmount);
        Assert.Null(state.Metadata);
        Assert.Null(state.OrphanedShares);
        Assert.Null(state.HighWaterMark);
        Assert.Null(state.LowWaterMark);
        Assert.Null(state.TrailingStopValue);
        Assert.False(state.IsStoppedOut);
    }

    [Fact]
    public void TradingState_CanSetAllProperties()
    {
        var state = new TradingState
        {
            AvailableCash = 10000m,
            AccumulatedLeftover = 0.50m,
            IsInitialized = true,
            LastTradeTimestamp = "2026-01-24T10:00:00Z",
            CurrentPosition = "TQQQ",
            CurrentShares = 150,
            StartingAmount = 10000m,
            DayStartBalance = 10050m,
            DayStartDate = "2026-01-24",
            HighWaterMark = 10200m,
            LowWaterMark = 9800m,
            TrailingStopValue = 10000m,
            IsStoppedOut = true,
            StoppedOutDirection = "Bull",
            WashoutLevel = 9500m,
            StopoutTimestamp = "2026-01-24T11:00:00Z",
            SlidingBandPositionHighWaterMark = 10150m,
            SlidingBandPositionLowWaterMark = 9900m
        };

        Assert.Equal(10000m, state.AvailableCash);
        Assert.Equal(0.50m, state.AccumulatedLeftover);
        Assert.True(state.IsInitialized);
        Assert.Equal("2026-01-24T10:00:00Z", state.LastTradeTimestamp);
        Assert.Equal("TQQQ", state.CurrentPosition);
        Assert.Equal(150, state.CurrentShares);
        Assert.Equal(10000m, state.StartingAmount);
        Assert.Equal(10050m, state.DayStartBalance);
        Assert.Equal("2026-01-24", state.DayStartDate);
        Assert.Equal(10200m, state.HighWaterMark);
        Assert.Equal(9800m, state.LowWaterMark);
        Assert.Equal(10000m, state.TrailingStopValue);
        Assert.True(state.IsStoppedOut);
        Assert.Equal("Bull", state.StoppedOutDirection);
        Assert.Equal(9500m, state.WashoutLevel);
        Assert.Equal("2026-01-24T11:00:00Z", state.StopoutTimestamp);
        Assert.Equal(10150m, state.SlidingBandPositionHighWaterMark);
        Assert.Equal(9900m, state.SlidingBandPositionLowWaterMark);
    }
}

public class OrphanedPositionTests
{
    [Fact]
    public void NewOrphanedPosition_HasDefaultValues()
    {
        var orphan = new OrphanedPosition();

        Assert.Equal(string.Empty, orphan.Symbol);
        Assert.Equal(0, orphan.Shares);
        Assert.Null(orphan.CreatedAt);
    }

    [Fact]
    public void OrphanedPosition_CanSetAllProperties()
    {
        var orphan = new OrphanedPosition
        {
            Symbol = "TQQQ",
            Shares = 5,
            CreatedAt = "2026-01-24T10:30:00Z"
        };

        Assert.Equal("TQQQ", orphan.Symbol);
        Assert.Equal(5, orphan.Shares);
        Assert.Equal("2026-01-24T10:30:00Z", orphan.CreatedAt);
    }
}
