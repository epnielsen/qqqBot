using System.Threading.Channels;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;

namespace MarketBlocks.Infrastructure.Common;

/// <summary>
/// Polling-based implementation of IMarketDataSource.
/// Converts HTTP polling into a stream of TradeTicks for unified pipeline processing.
/// </summary>
public sealed class PollingDataSource : IMarketDataSource
{
    private readonly IBrokerExecution _broker;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _logger;
    
    private readonly Dictionary<string, SubscriptionInfo> _subscriptions = new();
    private readonly object _subscriptionLock = new();
    
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _isConnected;

    /// <summary>
    /// Information about a subscribed symbol.
    /// </summary>
    private record SubscriptionInfo(
        string Symbol,
        ChannelWriter<TradeTick> TickWriter,
        bool IsBenchmark);

    public PollingDataSource(
        IBrokerExecution broker,
        TimeSpan pollInterval,
        Action<string>? logger = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _pollInterval = pollInterval;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public event Action<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public event Action<Exception>? OnError;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
            return Task.CompletedTask;

        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = RunPollingLoopAsync(_pollingCts.Token);
        _isConnected = true;
        
        ConnectionStateChanged?.Invoke(true);
        _logger?.Invoke("[POLLING] Data source connected");
        
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (!_isConnected)
            return;

        _isConnected = false;
        
        if (_pollingCts != null)
        {
            await _pollingCts.CancelAsync();
            _pollingCts.Dispose();
            _pollingCts = null;
        }

        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
            _pollingTask = null;
        }

        lock (_subscriptionLock)
        {
            _subscriptions.Clear();
        }

        ConnectionStateChanged?.Invoke(false);
        _logger?.Invoke("[POLLING] Data source disconnected");
    }

    /// <inheritdoc />
    public Task SubscribeAsync(
        string symbol,
        ChannelWriter<TradeTick> tickWriter,
        bool isBenchmark = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(tickWriter);

        lock (_subscriptionLock)
        {
            _subscriptions[symbol] = new SubscriptionInfo(symbol, tickWriter, isBenchmark);
        }

        _logger?.Invoke($"[POLLING] Subscribed to {symbol} (benchmark={isBenchmark})");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        lock (_subscriptionLock)
        {
            _subscriptions.Remove(symbol);
        }

        _logger?.Invoke($"[POLLING] Unsubscribed from {symbol}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The main polling loop that fetches prices and writes ticks to subscribers.
    /// </summary>
    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Get snapshot of subscriptions
                List<SubscriptionInfo> subs;
                lock (_subscriptionLock)
                {
                    subs = _subscriptions.Values.ToList();
                }

                if (subs.Count == 0)
                    continue;

                // Poll each subscribed symbol
                foreach (var sub in subs)
                {
                    try
                    {
                        var price = await _broker.GetLatestPriceAsync(sub.Symbol);
                        
                        if (price > 0)
                        {
                            var tick = sub.IsBenchmark
                                ? TradeTick.Benchmark(price, DateTime.UtcNow, sub.Symbol)
                                : TradeTick.Secondary(price, DateTime.UtcNow, sub.Symbol);

                            // Non-blocking write (drops if channel is full)
                            sub.TickWriter.TryWrite(tick);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue polling
                        OnError?.Invoke(ex);
                        _logger?.Invoke($"[POLLING] Error fetching {sub.Symbol}: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            _logger?.Invoke($"[POLLING] Fatal error in polling loop: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<decimal>> GetHistoricalPricesAsync(
        string symbol,
        int count,
        CancellationToken cancellationToken = default)
    {
        // PollingDataSource doesn't have direct historical data access.
        // Return current price repeated for seeding (simple fallback).
        // For real historical data, use the AlpacaSourceAdapter's implementation.
        try
        {
            var currentPrice = await _broker.GetLatestPriceAsync(symbol);
            var prices = new List<decimal>(count);
            for (int i = 0; i < count; i++)
            {
                prices.Add(currentPrice);
            }
            return prices;
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[POLLING] Error fetching historical prices for {symbol}: {ex.Message}");
            return Array.Empty<decimal>();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
