using System.Threading.Channels;
using Alpaca.Markets;
using MarketBlocks.Bots.Services;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Adapts Alpaca WebSocket streaming to IAnalystMarketDataSource for the AnalystEngine.
/// Provides low-latency real-time price updates via WebSocket instead of HTTP polling.
/// </summary>
public sealed class StreamingAnalystDataSource : IAnalystMarketDataSource, IAsyncDisposable
{
    private readonly IAlpacaDataStreamingClient _stockClient;
    private readonly IAlpacaCryptoStreamingClient? _cryptoClient;
    private readonly ILogger _logger;
    
    private readonly Dictionary<string, IAlpacaDataSubscription<ITrade>> _stockSubscriptions = new();
    private readonly Dictionary<string, IAlpacaDataSubscription<ITrade>> _cryptoSubscriptions = new();
    private readonly object _subscriptionLock = new();
    
    private bool _stockConnected;
    private bool _cryptoConnected;
    private bool _disposed;
    
    public StreamingAnalystDataSource(
        IAlpacaDataStreamingClient stockClient,
        IAlpacaCryptoStreamingClient? cryptoClient,
        ILogger logger)
    {
        _stockClient = stockClient ?? throw new ArgumentNullException(nameof(stockClient));
        _cryptoClient = cryptoClient;
        _logger = logger;
        
        // Wire up error handlers
        _stockClient.OnError += ex => _logger.LogError(ex, "[STREAMING] Stock stream error");
        if (_cryptoClient != null)
        {
            _cryptoClient.OnError += ex => _logger.LogError(ex, "[STREAMING] Crypto stream error");
        }
    }
    
    public bool IsConnected => _stockConnected || _cryptoConnected;
    
    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamingAnalystDataSource));
        
        // Connect stock streaming client
        try
        {
            var status = await _stockClient.ConnectAndAuthenticateAsync(ct);
            _stockConnected = status == AuthStatus.Authorized;
            
            if (_stockConnected)
            {
                _logger.LogInformation("[STREAMING] Stock stream connected and authenticated");
            }
            else
            {
                _logger.LogWarning("[STREAMING] Stock stream connection returned status: {Status}", status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STREAMING] Failed to connect stock stream");
            throw;
        }
        
        // Connect crypto streaming client (optional)
        if (_cryptoClient != null)
        {
            try
            {
                var status = await _cryptoClient.ConnectAndAuthenticateAsync(ct);
                _cryptoConnected = status == AuthStatus.Authorized;
                
                if (_cryptoConnected)
                {
                    _logger.LogInformation("[STREAMING] Crypto stream connected and authenticated");
                }
                else
                {
                    _logger.LogWarning("[STREAMING] Crypto stream connection returned status: {Status}", status);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[STREAMING] Failed to connect crypto stream (optional)");
                // Don't throw - crypto is optional
            }
        }
    }
    
    public async Task DisconnectAsync()
    {
        // Unsubscribe all
        lock (_subscriptionLock)
        {
            _stockSubscriptions.Clear();
            _cryptoSubscriptions.Clear();
        }
        
        // Disconnect clients
        try
        {
            if (_stockConnected)
            {
                await _stockClient.DisconnectAsync();
                _stockConnected = false;
                _logger.LogInformation("[STREAMING] Stock stream disconnected");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[STREAMING] Error disconnecting stock stream");
        }
        
        if (_cryptoClient != null)
        {
            try
            {
                if (_cryptoConnected)
                {
                    await _cryptoClient.DisconnectAsync();
                    _cryptoConnected = false;
                    _logger.LogInformation("[STREAMING] Crypto stream disconnected");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[STREAMING] Error disconnecting crypto stream");
            }
        }
    }
    
    public async Task SubscribeAsync(string symbol, ChannelWriter<PriceTick> writer, bool isBenchmark, CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamingAnalystDataSource));
        
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(writer);
        
        // Determine if this is a crypto symbol (contains '/')
        bool isCrypto = symbol.Contains('/'); // e.g., "BTC/USD"
        
        if (isCrypto)
        {
            if (_cryptoClient == null || !_cryptoConnected)
            {
                _logger.LogWarning("[STREAMING] Cannot subscribe to crypto symbol {Symbol} - crypto client not available", symbol);
                return;
            }
            
            var subscription = _cryptoClient.GetTradeSubscription(symbol);
            subscription.Received += trade =>
            {
                var tick = new PriceTick(trade.Price, isBenchmark, trade.TimestampUtc);
                // Non-blocking write (drops if channel is full - better than blocking the WebSocket handler)
                writer.TryWrite(tick);
            };
            
            await _cryptoClient.SubscribeAsync(subscription, ct);
            
            lock (_subscriptionLock)
            {
                _cryptoSubscriptions[symbol] = subscription;
            }
            
            _logger.LogInformation("[STREAMING] Subscribed to crypto {Symbol} (benchmark: {IsBenchmark})", symbol, isBenchmark);
        }
        else
        {
            if (!_stockConnected)
            {
                throw new InvalidOperationException($"Cannot subscribe to {symbol} - stock stream not connected");
            }
            
            var subscription = _stockClient.GetTradeSubscription(symbol);
            subscription.Received += trade =>
            {
                var tick = new PriceTick(trade.Price, isBenchmark, trade.TimestampUtc);
                // Non-blocking write (drops if channel is full)
                writer.TryWrite(tick);
            };
            
            await _stockClient.SubscribeAsync(subscription, ct);
            
            lock (_subscriptionLock)
            {
                _stockSubscriptions[symbol] = subscription;
            }
            
            _logger.LogInformation("[STREAMING] Subscribed to stock {Symbol} (benchmark: {IsBenchmark})", symbol, isBenchmark);
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        
        _disposed = true;
        await DisconnectAsync();
        
        // Dispose clients if they implement IDisposable
        if (_stockClient is IDisposable stockDisposable)
        {
            stockDisposable.Dispose();
        }
        
        if (_cryptoClient is IDisposable cryptoDisposable)
        {
            cryptoDisposable.Dispose();
        }
    }
}
