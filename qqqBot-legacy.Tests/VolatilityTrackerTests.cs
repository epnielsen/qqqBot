using qqqBot;

namespace qqqBot.Tests;

/// <summary>
/// Tests for VolatilityTracker ("Weather Station").
/// Verifies rolling window metrics, pruning, and edge cases.
/// </summary>
public class VolatilityTrackerTests
{
    [Fact]
    public void GetMetrics_EmptyTracker_ReturnsZeros()
    {
        var tracker = new VolatilityTracker(60);
        var metrics = tracker.GetMetrics();

        Assert.Equal(0m, metrics.High);
        Assert.Equal(0m, metrics.Low);
        Assert.Equal(0m, metrics.Range);
        Assert.Equal(0m, metrics.PercentVolatility);
    }

    [Fact]
    public void GetMetrics_SingleTick_ReturnsZeroRange()
    {
        var tracker = new VolatilityTracker(60);
        tracker.AddTick(500m, DateTime.UtcNow);

        var metrics = tracker.GetMetrics();

        Assert.Equal(500m, metrics.High);
        Assert.Equal(500m, metrics.Low);
        Assert.Equal(0m, metrics.Range);
        Assert.Equal(0m, metrics.PercentVolatility);
    }

    [Fact]
    public void GetMetrics_CalculatesCorrectRange()
    {
        var tracker = new VolatilityTracker(60);
        var now = DateTime.UtcNow;

        tracker.AddTick(100m, now);
        tracker.AddTick(101m, now.AddSeconds(1));
        tracker.AddTick(99m, now.AddSeconds(2));

        var metrics = tracker.GetMetrics();

        Assert.Equal(101m, metrics.High);
        Assert.Equal(99m, metrics.Low);
        Assert.Equal(2m, metrics.Range);
        Assert.Equal(2m / 99m, metrics.PercentVolatility);
    }

    [Fact]
    public void AddTick_PrunesExpiredData()
    {
        var tracker = new VolatilityTracker(60);
        var now = DateTime.UtcNow;

        // Add an old tick (70 seconds ago) - should be pruned
        tracker.AddTick(50m, now.AddSeconds(-70));
        // Add a current tick
        tracker.AddTick(100m, now);

        var metrics = tracker.GetMetrics();

        // Old $50 tick should be pruned; only $100 remains
        Assert.Equal(100m, metrics.High);
        Assert.Equal(100m, metrics.Low);
        Assert.Equal(0m, metrics.Range);
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void GetMetrics_WindowSlidesCorrectly()
    {
        var tracker = new VolatilityTracker(10); // 10-second window for fast testing
        var baseTime = DateTime.UtcNow;

        // Tick at T=0: Price spike to 200
        tracker.AddTick(200m, baseTime);
        // Tick at T=5: Price drops to 100
        tracker.AddTick(100m, baseTime.AddSeconds(5));

        var metrics1 = tracker.GetMetrics();
        Assert.Equal(200m, metrics1.High);
        Assert.Equal(100m, metrics1.Low);
        Assert.Equal(100m, metrics1.Range);

        // Tick at T=15: The 200 spike is now outside the 10s window
        tracker.AddTick(105m, baseTime.AddSeconds(15));

        var metrics2 = tracker.GetMetrics();
        // 200 should be pruned, leaving only 100 and 105
        Assert.Equal(105m, metrics2.High);
        Assert.Equal(100m, metrics2.Low);
        Assert.Equal(5m, metrics2.Range);
    }

    [Fact]
    public void GetMetrics_HighVolatility_CalculatesStormFactor()
    {
        var tracker = new VolatilityTracker(60);
        var now = DateTime.UtcNow;

        // Simulate a volatile 60-second candle for QQQ ~$600
        tracker.AddTick(600.00m, now);
        tracker.AddTick(601.50m, now.AddSeconds(10));
        tracker.AddTick(599.00m, now.AddSeconds(20));
        tracker.AddTick(602.00m, now.AddSeconds(30));
        tracker.AddTick(598.50m, now.AddSeconds(40));

        var metrics = tracker.GetMetrics();

        Assert.Equal(602.00m, metrics.High);
        Assert.Equal(598.50m, metrics.Low);
        Assert.Equal(3.50m, metrics.Range);
        // 3.50 / 598.50 ≈ 0.00585 (0.585% volatility = Stormy!)
        Assert.True(metrics.PercentVolatility > 0.005m, 
            $"Expected stormy volatility > 0.5%, got {metrics.PercentVolatility:P3}");
    }

    [Fact]
    public void GetMetrics_LowVolatility_CalculatesCalmFactor()
    {
        var tracker = new VolatilityTracker(60);
        var now = DateTime.UtcNow;

        // Simulate a calm 60-second candle for QQQ ~$600
        tracker.AddTick(600.00m, now);
        tracker.AddTick(600.10m, now.AddSeconds(10));
        tracker.AddTick(599.95m, now.AddSeconds(20));
        tracker.AddTick(600.05m, now.AddSeconds(30));

        var metrics = tracker.GetMetrics();

        // Range = 0.15, PercentVol = 0.15 / 599.95 ≈ 0.025% (Sunny)
        Assert.True(metrics.PercentVolatility < 0.001m, 
            $"Expected calm volatility < 0.1%, got {metrics.PercentVolatility:P3}");
    }

    [Fact]
    public void Reset_ClearsAllData()
    {
        var tracker = new VolatilityTracker(60);
        tracker.AddTick(100m, DateTime.UtcNow);
        tracker.AddTick(200m, DateTime.UtcNow);

        Assert.Equal(2, tracker.Count);

        tracker.Reset();

        Assert.Equal(0, tracker.Count);
        var metrics = tracker.GetMetrics();
        Assert.Equal(0m, metrics.High);
    }
}
