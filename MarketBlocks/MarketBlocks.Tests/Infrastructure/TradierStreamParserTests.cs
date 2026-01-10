using System.Text;
using System.Threading.Channels;
using Xunit;
using MarketBlocks.Core.Domain;
using MarketBlocks.Infrastructure.Tradier; // Logic to be implemented

namespace MarketBlocks.Tests.Infrastructure.Tradier;

public class TradierStreamParserTests
{
    [Fact]
    public async Task ParseStream_HandlesNewlineDelimitedJson()
    {
        // Arrange
        // Simulate a Tradier stream: a trade, a heartbeat (to be ignored), and another trade
        var jsonStream = 
            "{\"type\":\"trade\",\"symbol\":\"SPY\",\"price\":400.50,\"date\":\"1700000000\"}\n" +
            "{\"type\":\"heartbeat\"}\n" +
            "{\"type\":\"trade\",\"symbol\":\"AAPL\",\"price\":150.25,\"date\":\"1700000060\"}\n";

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(jsonStream));
        var channel = Channel.CreateUnbounded<TradeTick>();
        
        // We assume the adapter has a public/internal method to parse a stream directly
        // allowing us to test logic without making HTTP calls.
        var parser = new TradierStreamParser(); 

        // Act
        await parser.ParseToChannelAsync(memoryStream, channel.Writer, CancellationToken.None);
        channel.Writer.Complete();

        // Assert
        var tick1 = await channel.Reader.ReadAsync();
        Assert.Equal("SPY", tick1.Symbol);
        Assert.Equal(400.50m, tick1.Price);
        
        var tick2 = await channel.Reader.ReadAsync();
        Assert.Equal("AAPL", tick2.Symbol);
        Assert.Equal(150.25m, tick2.Price);
        
        // Ensure no more items (Heartbeat should be skipped)
        Assert.False(await channel.Reader.WaitToReadAsync());
    }
}