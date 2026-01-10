using System.Threading.Channels;

namespace qqqBot.Core.Interfaces;

/// <summary>
/// Broker-agnostic interface for real-time market data.
/// Implementations push trade ticks to a channel for reactive consumption.
/// </summary>
public interface IMarketDataSource : IAsyncDisposable
{
    /// <summary>
    /// Connects to the market data source and begins authentication.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Disconnects from the market data source.
    /// </summary>
    Task DisconnectAsync();
    
    /// <summary>
    /// Subscribes to real-time trades for a symbol.
    /// Trades will be written to the provided channel.
    /// </summary>
    /// <param name="symbol">Ticker symbol to subscribe to.</param>
    /// <param name="tickWriter">Channel writer to receive trade ticks.</param>
    /// <param name="isBenchmark">Whether this is the primary benchmark symbol.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SubscribeAsync(
        string symbol, 
        ChannelWriter<Domain.TradeTick> tickWriter, 
        bool isBenchmark = true,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Unsubscribes from real-time trades for a symbol.
    /// </summary>
    /// <param name="symbol">Ticker symbol to unsubscribe from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets historical trade data for SMA seeding.
    /// </summary>
    /// <param name="symbol">Ticker symbol.</param>
    /// <param name="count">Number of data points to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Historical prices (most recent last).</returns>
    Task<IReadOnlyList<decimal>> GetHistoricalPricesAsync(
        string symbol, 
        int count, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Whether the data source is currently connected.
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event Action<bool>? ConnectionStateChanged;
    
    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    event Action<Exception>? OnError;
}
