using System.Threading.Channels;
using Alpaca.Markets;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;

namespace MarketBlocks.Infrastructure.Alpaca;

/// <summary>
/// Alpaca Markets implementation of IMarketDataSource.
/// Adapts Alpaca streaming API to broker-agnostic Core interfaces.
/// </summary>
public sealed class AlpacaSourceAdapter : IMarketDataSource
{
    private readonly IAlpacaDataStreamingClient _stockClient;
    private readonly IAlpacaCryptoStreamingClient? _cryptoClient;
    private readonly IAlpacaDataClient? _dataClient;
    
    private readonly Dictionary<string, IAlpacaDataSubscription<ITrade>> _stockSubscriptions = new();
    private readonly Dictionary<string, IAlpacaDataSubscription<ITrade>> _cryptoSubscriptions = new();
    private readonly object _subscriptionLock = new();
    
    private bool _stockConnected;
    private bool _cryptoConnected;

    public AlpacaSourceAdapter(
        IAlpacaDataStreamingClient stockClient,
        IAlpacaCryptoStreamingClient? cryptoClient = null,
        IAlpacaDataClient? dataClient = null)
    {
        _stockClient = stockClient ?? throw new ArgumentNullException(nameof(stockClient));
        _cryptoClient = cryptoClient;
        _dataClient = dataClient;
        
        // Wire up error handlers
        _stockClient.OnError += ex => OnError?.Invoke(ex);
        if (_cryptoClient != null)
        {
            _cryptoClient.OnError += ex => OnError?.Invoke(ex);
        }
    }

    /// <inheritdoc />
    public bool IsConnected => _stockConnected || _cryptoConnected;

    /// <inheritdoc />
    public event Action<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public event Action<Exception>? OnError;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Connect stock streaming client
        try
        {
            await _stockClient.ConnectAndAuthenticateAsync(cancellationToken);
            _stockConnected = true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            throw;
        }
        
        // Connect crypto streaming client (if available)
        if (_cryptoClient != null)
        {
            try
            {
                await _cryptoClient.ConnectAndAuthenticateAsync(cancellationToken);
                _cryptoConnected = true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
                // Don't throw - crypto is optional
            }
        }
        
        ConnectionStateChanged?.Invoke(IsConnected);
    }

    /// <inheritdoc />
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
            await _stockClient.DisconnectAsync();
            _stockConnected = false;
        }
        catch { /* Ignore disconnect errors */ }
        
        if (_cryptoClient != null)
        {
            try
            {
                await _cryptoClient.DisconnectAsync();
                _cryptoConnected = false;
            }
            catch { /* Ignore disconnect errors */ }
        }
        
        ConnectionStateChanged?.Invoke(false);
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(
        string symbol, 
        ChannelWriter<TradeTick> tickWriter, 
        bool isBenchmark = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(tickWriter);
        
        // Determine if this is a crypto symbol
        bool isCrypto = symbol.Contains('/'); // e.g., "BTC/USD"
        
        if (isCrypto)
        {
            if (_cryptoClient == null)
            {
                throw new InvalidOperationException("Crypto client not configured. Cannot subscribe to crypto symbols.");
            }
            
            var subscription = _cryptoClient.GetTradeSubscription(symbol);
            subscription.Received += trade =>
            {
                var tick = isBenchmark 
                    ? TradeTick.Benchmark(trade.Price, trade.TimestampUtc, symbol)
                    : TradeTick.Secondary(trade.Price, trade.TimestampUtc, symbol);
                
                // Non-blocking write (drops if channel is full)
                tickWriter.TryWrite(tick);
            };
            
            await _cryptoClient.SubscribeAsync(subscription, cancellationToken);
            
            lock (_subscriptionLock)
            {
                _cryptoSubscriptions[symbol] = subscription;
            }
        }
        else
        {
            var subscription = _stockClient.GetTradeSubscription(symbol);
            subscription.Received += trade =>
            {
                var tick = isBenchmark 
                    ? TradeTick.Benchmark(trade.Price, trade.TimestampUtc, symbol)
                    : TradeTick.Secondary(trade.Price, trade.TimestampUtc, symbol);
                
                // Non-blocking write (drops if channel is full)
                tickWriter.TryWrite(tick);
            };
            
            await _stockClient.SubscribeAsync(subscription, cancellationToken);
            
            lock (_subscriptionLock)
            {
                _stockSubscriptions[symbol] = subscription;
            }
        }
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        
        bool isCrypto = symbol.Contains('/');
        
        if (isCrypto && _cryptoClient != null)
        {
            lock (_subscriptionLock)
            {
                if (_cryptoSubscriptions.TryGetValue(symbol, out var subscription))
                {
                    _ = _cryptoClient.UnsubscribeAsync(subscription, cancellationToken);
                    _cryptoSubscriptions.Remove(symbol);
                }
            }
        }
        else
        {
            lock (_subscriptionLock)
            {
                if (_stockSubscriptions.TryGetValue(symbol, out var subscription))
                {
                    _ = _stockClient.UnsubscribeAsync(subscription, cancellationToken);
                    _stockSubscriptions.Remove(symbol);
                }
            }
        }
        
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<decimal>> GetHistoricalPricesAsync(
        string symbol, 
        int count, 
        CancellationToken cancellationToken = default)
    {
        if (_dataClient == null)
        {
            throw new InvalidOperationException("Data client not configured. Cannot get historical prices.");
        }
        
        // Request more bars than needed in case of gaps
        var requestCount = count + 10;
        
        var request = new HistoricalBarsRequest(symbol, DateTime.UtcNow.AddMinutes(-requestCount), DateTime.UtcNow, BarTimeFrame.Minute);
        var bars = await _dataClient.ListHistoricalBarsAsync(request, cancellationToken);
        
        // Extract close prices (most recent last)
        var prices = bars.Items
            .OrderBy(b => b.TimeUtc)
            .Select(b => b.Close)
            .TakeLast(count)
            .ToList();
        
        return prices;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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
        
        if (_dataClient is IDisposable dataDisposable)
        {
            dataDisposable.Dispose();
        }
    }
}
