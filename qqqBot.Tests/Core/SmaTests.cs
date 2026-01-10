using Xunit;
using qqqBot.Core.Math;

namespace qqqBot.Tests.Core;

public class SmaTests
{
    [Fact]
    public void IncrementalSma_CalculatesCorrectly()
    {
        // Arrange: Window size 3
        var sma = new IncrementalSma(3);

        // Act & Assert
        // T1: Add 10 -> Avg 10
        Assert.Equal(10m, sma.Add(10m));
        
        // T2: Add 20 -> Avg 15
        Assert.Equal(15m, sma.Add(20m));

        // T3: Add 30 -> Avg 20 (Full window: 10, 20, 30)
        Assert.Equal(20m, sma.Add(30m));
        Assert.True(sma.IsFull);

        // T4: Add 40 -> Avg 30 (Window slides: 20, 30, 40)
        Assert.Equal(30m, sma.Add(40m));
    }
    
    [Fact]
    public void IncrementalSma_Clear_ResetsState()
    {
        // Arrange
        var sma = new IncrementalSma(3);
        sma.Add(10m);
        sma.Add(20m);
        sma.Add(30m);
        Assert.True(sma.IsFull);
        
        // Act
        sma.Clear();
        
        // Assert
        Assert.Equal(0, sma.Count);
        Assert.False(sma.IsFull);
        Assert.Equal(0m, sma.CurrentAverage);
    }
    
    [Fact]
    public void IncrementalSma_Seed_InitializesBuffer()
    {
        // Arrange
        var sma = new IncrementalSma(3);
        
        // Act
        sma.Seed(new[] { 10m, 20m, 30m });
        
        // Assert
        Assert.True(sma.IsFull);
        Assert.Equal(20m, sma.CurrentAverage);
    }
}
