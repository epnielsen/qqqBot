using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace MarketBlocks.Infrastructure.Tradier;

/// <summary>
/// Tradier implementation of IMarketDataSource.
/// Uses Tradier's HTTP streaming API for real-time market data.
/// </summary>
public sealed class TradierSourceAdapter : IMarketDataSource
{
    private readonly HttpClient _httpClient;
    private readonly TradierOptions _options;
    private readonly TradierStreamParser _parser;
    
    private readonly Dictionary<string, SubscriptionInfo> _subscriptions = new();
    private readonly object _subscriptionLock = new();
    
    private string? _sessionId;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private bool _isConnected;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TradierSourceAdapter(HttpClient httpClient, IOptions<TradierOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _parser = new TradierStreamParser();
        
        // Configure HttpClient for REST API calls
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public event Action<bool>? ConnectionStateChanged;

    /// <inheritdoc />
    public event Action<Exception>? OnError;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
            return;
        
        try
        {
            // Get a streaming session ID
            _sessionId = await CreateStreamingSessionAsync(cancellationToken);
            _isConnected = true;
            ConnectionStateChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        // Cancel the stream
        if (_streamCts != null)
        {
            await _streamCts.CancelAsync();
            _streamCts.Dispose();
            _streamCts = null;
        }
        
        // Wait for stream task to complete
        if (_streamTask != null)
        {
            try
            {
                await _streamTask;
            }
            catch (OperationCanceledException) { }
            _streamTask = null;
        }
        
        // Clear subscriptions
        lock (_subscriptionLock)
        {
            _subscriptions.Clear();
        }
        
        _sessionId = null;
        _isConnected = false;
        ConnectionStateChanged?.Invoke(false);
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(
        string symbol, 
        ChannelWriter<TradeTick> tickWriter, 
        bool isBenchmark = true,
        CancellationToken cancellationToken = default)
    {
        if (!_isConnected || string.IsNullOrEmpty(_sessionId))
        {
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        }
        
        lock (_subscriptionLock)
        {
            _subscriptions[symbol.ToUpperInvariant()] = new SubscriptionInfo(tickWriter, isBenchmark);
        }
        
        // Restart the stream with updated symbols
        await RestartStreamAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        bool hadSubscription;
        lock (_subscriptionLock)
        {
            hadSubscription = _subscriptions.Remove(symbol.ToUpperInvariant());
        }
        
        if (hadSubscription)
        {
            await RestartStreamAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<decimal>> GetHistoricalPricesAsync(
        string symbol, 
        int count, 
        CancellationToken cancellationToken = default)
    {
        // Use time series endpoint to get historical data
        // Request daily bars and extract close prices
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var startDate = DateTime.UtcNow.AddDays(-count * 2).ToString("yyyy-MM-dd"); // Extra days for weekends/holidays
        
        var response = await _httpClient.GetAsync(
            $"{_options.BaseUrl}/markets/history?symbol={symbol}&interval=daily&start={startDate}&end={endDate}",
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        var prices = new List<decimal>();
        
        if (doc.RootElement.TryGetProperty("history", out var history) &&
            history.ValueKind != JsonValueKind.Null &&
            history.TryGetProperty("day", out var days))
        {
            var daysArray = days.ValueKind == JsonValueKind.Array
                ? days.EnumerateArray()
                : new[] { days }.AsEnumerable().Select(x => x);
            
            foreach (var day in daysArray)
            {
                if (day.TryGetProperty("close", out var close))
                {
                    prices.Add(close.GetDecimal());
                }
            }
        }
        
        // Return the most recent 'count' prices
        return prices.TakeLast(count).ToList();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        
        await DisconnectAsync();
        _disposed = true;
    }

    #region Private Helpers

    private async Task<string> CreateStreamingSessionAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"{_options.BaseUrl}/markets/events/session",
            null,
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        var stream = doc.RootElement.GetProperty("stream");
        return stream.GetProperty("sessionid").GetString() 
            ?? throw new InvalidOperationException("Failed to get session ID");
    }

    private async Task RestartStreamAsync(CancellationToken cancellationToken)
    {
        // Stop existing stream
        if (_streamCts != null)
        {
            await _streamCts.CancelAsync();
            _streamCts.Dispose();
        }
        
        if (_streamTask != null)
        {
            try
            {
                await _streamTask;
            }
            catch (OperationCanceledException) { }
        }
        
        // Get current symbols
        string[] symbols;
        lock (_subscriptionLock)
        {
            symbols = _subscriptions.Keys.ToArray();
        }
        
        if (symbols.Length == 0)
            return;
        
        // Start new stream
        _streamCts = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _streamCts.Token, cancellationToken);
        
        _streamTask = RunStreamAsync(symbols, linkedCts.Token);
    }

    private async Task RunStreamAsync(string[] symbols, CancellationToken cancellationToken)
    {
        try
        {
            // Build the streaming URL
            var symbolList = string.Join(",", symbols);
            var streamUrl = $"{_options.StreamUrl}/markets/events?sessionid={_sessionId}&symbols={symbolList}&filter=trade&linebreak=true";
            
            // Open the HTTP stream
            using var response = await _httpClient.GetAsync(
                streamUrl, 
                HttpCompletionOption.ResponseHeadersRead, 
                cancellationToken);
            
            response.EnsureSuccessStatusCode();
            
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                
                if (line == null)
                    break;
                
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                
                try
                {
                    await ProcessLineAsync(line, cancellationToken);
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("type", out var typeElement))
            return;
        
        var eventType = typeElement.GetString();
        
        // Ignore heartbeats
        if (eventType != "trade")
            return;
        
        // Get the symbol
        if (!root.TryGetProperty("symbol", out var symbolElement))
            return;
        
        var symbol = symbolElement.GetString()?.ToUpperInvariant();
        if (string.IsNullOrEmpty(symbol))
            return;
        
        // Find the subscription
        SubscriptionInfo? subscription;
        lock (_subscriptionLock)
        {
            if (!_subscriptions.TryGetValue(symbol, out subscription))
                return;
        }
        
        // Parse the tick
        var tick = _parser.ParseLine(line, subscription.IsBenchmark);
        if (tick.HasValue)
        {
            await subscription.Writer.WriteAsync(tick.Value, cancellationToken);
        }
    }

    private sealed record SubscriptionInfo(ChannelWriter<TradeTick> Writer, bool IsBenchmark);

    #endregion
}
