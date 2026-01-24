using System.Threading.Channels;
using MarketBlocks.Trade.Interfaces;
using MarketBlocks.Bots.Services;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Adapts HTTP polling to IAnalystMarketDataSource for the AnalystEngine.
/// Polls the broker for quotes and writes PriceTick to the analyst's channel.
/// </summary>
public sealed class PollingAnalystDataSource : IAnalystMarketDataSource
{
    private readonly IBrokerExecution _broker;
    private readonly int _pollIntervalMs;
    private readonly ILogger _logger;
    
    private readonly Dictionary<string, SubscriptionInfo> _subscriptions = new();
    private readonly object _subscriptionLock = new();
    
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _isConnected;
    
    private record SubscriptionInfo(
        string Symbol,
        ChannelWriter<PriceTick> Writer,
        bool IsBenchmark);
    
    public PollingAnalystDataSource(
        IBrokerExecution broker,
        int pollIntervalMs,
        ILogger logger)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _pollIntervalMs = pollIntervalMs;
        _logger = logger;
    }
    
    public Task ConnectAsync(CancellationToken ct)
    {
        if (_isConnected)
            return Task.CompletedTask;
        
        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollingTask = RunPollingLoopAsync(_pollingCts.Token);
        _isConnected = true;
        
        _logger.LogInformation("[POLLING] Analyst data source connected");
        return Task.CompletedTask;
    }
    
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
            try { await _pollingTask; } catch (OperationCanceledException) { }
            _pollingTask = null;
        }
        
        _logger.LogInformation("[POLLING] Analyst data source disconnected");
    }
    
    public Task SubscribeAsync(string symbol, ChannelWriter<PriceTick> writer, bool isBenchmark, CancellationToken ct)
    {
        lock (_subscriptionLock)
        {
            _subscriptions[symbol] = new SubscriptionInfo(symbol, writer, isBenchmark);
        }
        
        _logger.LogInformation("[POLLING] Subscribed to {Symbol} (benchmark: {IsBenchmark})", symbol, isBenchmark);
        return Task.CompletedTask;
    }
    
    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAllSymbolsAsync(ct);
                await Task.Delay(_pollIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[POLLING] Error during poll cycle");
                await Task.Delay(1000, ct); // Back off on error
            }
        }
    }
    
    private async Task PollAllSymbolsAsync(CancellationToken ct)
    {
        List<SubscriptionInfo> subscriptions;
        lock (_subscriptionLock)
        {
            subscriptions = _subscriptions.Values.ToList();
        }
        
        foreach (var sub in subscriptions)
        {
            if (ct.IsCancellationRequested) break;
            
            try
            {
                var price = await _broker.GetLatestPriceAsync(sub.Symbol, ct);
                if (price > 0)
                {
                    var tick = new PriceTick(price, sub.IsBenchmark, DateTime.UtcNow);
                    await sub.Writer.WriteAsync(tick, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[POLLING] Failed to get price for {Symbol}", sub.Symbol);
            }
        }
    }
}
