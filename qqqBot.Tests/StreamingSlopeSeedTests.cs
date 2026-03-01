using qqqBot;
using Xunit;
using MarketBlocks.Trade.Math;
using System;
using System.Linq;

namespace qqqBot.Tests;

/// <summary>
/// Tests for StreamingSlope.Seed(), Clear(), and GetValues() — 
/// new methods added for time-based phase reconfiguration (pre-hydration).
/// </summary>
public class StreamingSlopeSeedTests
{
    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var slope = new StreamingSlope(5);
        slope.Add(1m);
        slope.Add(2m);
        slope.Add(3m);
        Assert.Equal(3, slope.Count);

        slope.Clear();

        Assert.Equal(0, slope.Count);
        Assert.False(slope.IsReady);
        Assert.Equal(0m, slope.CurrentSlope);
    }

    [Fact]
    public void Seed_PopulatesFromEnumerable()
    {
        var slope = new StreamingSlope(5);

        slope.Seed(new decimal[] { 10m, 11m, 12m, 13m, 14m });

        Assert.True(slope.IsReady);
        Assert.Equal(5, slope.Count);
        Assert.True(slope.CurrentSlope > 0); // Upward trend
    }

    [Fact]
    public void Seed_ClearsExistingData()
    {
        var slope = new StreamingSlope(3);
        slope.Add(100m);
        slope.Add(99m);
        slope.Add(98m);
        Assert.True(slope.CurrentSlope < 0); // Downward

        slope.Seed(new decimal[] { 1m, 2m, 3m });

        Assert.True(slope.CurrentSlope > 0); // Now upward
    }

    [Fact]
    public void Seed_TakesLastNValues_WhenMoreThanWindowSize()
    {
        var slope = new StreamingSlope(3);

        // Seed with 5 values — only last 3 should be kept
        slope.Seed(new decimal[] { 1m, 2m, 100m, 101m, 102m });

        Assert.True(slope.IsReady);
        Assert.Equal(3, slope.Count);
        // Should reflect the trend of 100, 101, 102 (positive)
        Assert.True(slope.CurrentSlope > 0);
    }

    [Fact]
    public void Seed_WithFewerThanWindowSize_PartiallyFills()
    {
        var slope = new StreamingSlope(10);

        slope.Seed(new decimal[] { 1m, 2m, 3m });

        Assert.False(slope.IsReady);
        Assert.Equal(3, slope.Count);
    }

    [Fact]
    public void GetValues_ReturnsChronologicalOrder_NotFull()
    {
        var slope = new StreamingSlope(5);
        slope.Add(10m);
        slope.Add(20m);
        slope.Add(30m);

        var values = slope.GetValues();

        Assert.Equal(3, values.Count);
        Assert.Equal(10m, values[0]);
        Assert.Equal(20m, values[1]);
        Assert.Equal(30m, values[2]);
    }

    [Fact]
    public void GetValues_ReturnsChronologicalOrder_Full()
    {
        var slope = new StreamingSlope(3);
        slope.Add(10m);
        slope.Add(20m);
        slope.Add(30m);
        slope.Add(40m); // Evicts 10, buffer: [20, 30, 40]

        var values = slope.GetValues();

        Assert.Equal(3, values.Count);
        Assert.Equal(20m, values[0]);
        Assert.Equal(30m, values[1]);
        Assert.Equal(40m, values[2]);
    }

    [Fact]
    public void GetValues_EmptySlope_ReturnsEmpty()
    {
        var slope = new StreamingSlope(5);

        var values = slope.GetValues();

        Assert.Empty(values);
    }

    [Fact]
    public void Seed_ThenAdd_ContinuesCorrectly()
    {
        var slope = new StreamingSlope(3);
        slope.Seed(new decimal[] { 1m, 2m, 3m });

        slope.Add(4m); // Buffer now: [2, 3, 4]

        Assert.True(slope.IsReady);
        var values = slope.GetValues();
        Assert.Equal(3, values.Count);
        Assert.Equal(2m, values[0]);
        Assert.Equal(3m, values[1]);
        Assert.Equal(4m, values[2]);
    }

    [Fact]
    public void SlopeConsistency_SeededVsManuallyAdded()
    {
        // A seeded slope should produce the same slope as one built manually
        var seeded = new StreamingSlope(5);
        var manual = new StreamingSlope(5);

        var data = new decimal[] { 100m, 101m, 102m, 103m, 104m };

        seeded.Seed(data);
        foreach (var d in data) manual.Add(d);

        Assert.Equal(manual.CurrentSlope, seeded.CurrentSlope);
    }

    [Fact]
    public void GetValues_RoundTrip_ProducesSameSlope()
    {
        // Seed → GetValues → Seed again → should produce same slope
        var original = new StreamingSlope(5);
        var values = new decimal[] { 100m, 101m, 103m, 102m, 105m };
        original.Seed(values);
        var originalSlope = original.CurrentSlope;

        var clone = new StreamingSlope(5);
        clone.Seed(original.GetValues());

        Assert.Equal(originalSlope, clone.CurrentSlope);
    }

    [Fact]
    public void GetValues_AfterWraparound_StillChronological()
    {
        var slope = new StreamingSlope(3);
        // Add enough to cause multiple wraparounds
        slope.Add(1m); slope.Add(2m); slope.Add(3m); // Full
        slope.Add(4m); slope.Add(5m); // Wrapped around
        slope.Add(6m); slope.Add(7m); slope.Add(8m); // Multiple wraps

        var values = slope.GetValues();

        Assert.Equal(3, values.Count);
        Assert.Equal(6m, values[0]);
        Assert.Equal(7m, values[1]);
        Assert.Equal(8m, values[2]);
    }

    [Fact]
    public void RebuildWithDifferentWindowSize_PreservesDataFromOldCalculator()
    {
        // Simulates what AnalystEngine.ReconfigureIndicators does
        var oldSlope = new StreamingSlope(5);
        for (int i = 0; i < 10; i++)
            oldSlope.Add(100m + i);

        var oldValues = oldSlope.GetValues();
        Assert.Equal(5, oldValues.Count);

        // Rebuild with smaller window
        var newSlope = new StreamingSlope(3);
        newSlope.Seed(oldValues); // Takes last 3 of the 5 values

        Assert.True(newSlope.IsReady);
        Assert.Equal(3, newSlope.Count);
        Assert.True(newSlope.CurrentSlope > 0); // Upward trend preserved

        // Rebuild with larger window
        var bigSlope = new StreamingSlope(10);
        bigSlope.Seed(oldValues); // Only 5 values for 10-slot window

        Assert.False(bigSlope.IsReady); // Not enough data for full window
        Assert.Equal(5, bigSlope.Count);
    }
}
