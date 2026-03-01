using qqqBot;

namespace qqqBot.Tests;

public class MarketRegimeTests
{
    [Fact]
    public void MarketRegime_CanBeCreated()
    {
        var regime = new MarketRegime(
            Signal: "BULL",
            BenchmarkPrice: 500.50m,
            SmaValue: 500.00m,
            Slope: 0.05m,
            UpperBand: 500.75m,
            LowerBand: 499.25m,
            TimestampUtc: DateTime.UtcNow,
            Reason: "Price > UpperBand"
        );

        Assert.Equal("BULL", regime.Signal);
        Assert.Equal(500.50m, regime.BenchmarkPrice);
        Assert.Equal(500.00m, regime.SmaValue);
        Assert.Equal(0.05m, regime.Slope);
        Assert.Equal(500.75m, regime.UpperBand);
        Assert.Equal(499.25m, regime.LowerBand);
        Assert.Equal("Price > UpperBand", regime.Reason);
    }

    [Fact]
    public void MarketRegime_IsStale_ReturnsTrueForOldData()
    {
        var oldTimestamp = DateTime.UtcNow.AddSeconds(-15);
        var regime = new MarketRegime(
            Signal: "BEAR",
            BenchmarkPrice: 499.50m,
            SmaValue: 500.00m,
            Slope: -0.05m,
            UpperBand: 500.75m,
            LowerBand: 499.25m,
            TimestampUtc: oldTimestamp,
            Reason: "Price < LowerBand"
        );

        Assert.True(regime.IsStale(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void MarketRegime_IsStale_ReturnsFalseForFreshData()
    {
        var regime = new MarketRegime(
            Signal: "NEUTRAL",
            BenchmarkPrice: 500.00m,
            SmaValue: 500.00m,
            Slope: 0m,
            UpperBand: 500.75m,
            LowerBand: 499.25m,
            TimestampUtc: DateTime.UtcNow,
            Reason: "Price within bands"
        );

        Assert.False(regime.IsStale(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void MarketRegime_RecordEquality_Works()
    {
        var timestamp = DateTime.UtcNow;
        var regime1 = new MarketRegime("BULL", 500m, 499m, 0.05m, 501m, 497m, timestamp, "Test");
        var regime2 = new MarketRegime("BULL", 500m, 499m, 0.05m, 501m, 497m, timestamp, "Test");

        Assert.Equal(regime1, regime2);
    }

    [Fact]
    public void MarketRegime_RecordInequality_Works()
    {
        var timestamp = DateTime.UtcNow;
        var regime1 = new MarketRegime("BULL", 500m, 499m, 0.05m, 501m, 497m, timestamp, "Test");
        var regime2 = new MarketRegime("BEAR", 500m, 499m, -0.05m, 501m, 497m, timestamp, "Test");

        Assert.NotEqual(regime1, regime2);
    }

    [Fact]
    public void MarketRegime_SupportsMarketCloseSignal()
    {
        var regime = new MarketRegime(
            Signal: "MARKET_CLOSE",
            BenchmarkPrice: 500.00m,
            SmaValue: 500.00m,
            Slope: 0m,
            UpperBand: 500.75m,
            LowerBand: 499.25m,
            TimestampUtc: DateTime.UtcNow,
            Reason: "Market closing"
        );

        Assert.Equal("MARKET_CLOSE", regime.Signal);
    }
}
