using System.Net.Http.Headers;
using System.Text.Json;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace MarketBlocks.Infrastructure.Tradier;

/// <summary>
/// Tradier implementation of IBrokerExecution.
/// Adapts Tradier REST API to broker-agnostic Core interfaces.
/// </summary>
public sealed class TradierExecutionAdapter : IBrokerExecution
{
    private readonly HttpClient _httpClient;
    private readonly TradierOptions _options;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TradierExecutionAdapter(HttpClient httpClient, IOptions<TradierOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        
        // Configure HttpClient
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <inheritdoc />
    public async Task<BotOrder> SubmitOrderAsync(BotOrderRequest request, CancellationToken cancellationToken = default)
    {
        var formData = BuildOrderFormData(request);
        var content = new FormUrlEncodedContent(formData);
        
        var response = await _httpClient.PostAsync(
            $"/accounts/{_options.AccountId}/orders", 
            content, 
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        var orderElement = doc.RootElement.GetProperty("order");
        var tradierId = orderElement.GetProperty("id").GetInt32();
        var status = orderElement.GetProperty("status").GetString() ?? "pending";
        
        return new BotOrder
        {
            OrderId = TradierIdHelper.ToGuid(tradierId),
            ClientOrderId = request.ClientOrderId,
            Symbol = request.Symbol,
            Side = request.Side,
            Type = request.Type,
            Status = MapOrderStatus(status),
            Quantity = request.Quantity,
            FilledQuantity = 0,
            LimitPrice = request.LimitPrice,
            SubmittedAtUtc = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public async Task<BotOrder> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var tradierId = TradierIdHelper.ToTradierId(orderId);
        
        var response = await _httpClient.GetAsync(
            $"/accounts/{_options.AccountId}/orders/{tradierId}",
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOrderResponse(json, orderId);
    }

    /// <inheritdoc />
    public async Task<bool> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var tradierId = TradierIdHelper.ToTradierId(orderId);
        
        try
        {
            var response = await _httpClient.DeleteAsync(
                $"/accounts/{_options.AccountId}/orders/{tradierId}",
                cancellationToken);
            
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Order may already be filled/canceled
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<BotPosition?> GetPositionAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var positions = await GetAllPositionsAsync(cancellationToken);
        return positions.FirstOrDefault(p => 
            string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BotPosition>> GetAllPositionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/accounts/{_options.AccountId}/positions",
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParsePositionsResponse(json);
    }

    /// <inheritdoc />
    public async Task<decimal> GetBuyingPowerAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/accounts/{_options.AccountId}/balances",
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        var balances = doc.RootElement.GetProperty("balances");
        
        // Try margin buying power first, fall back to cash
        if (balances.TryGetProperty("margin", out var margin) &&
            margin.TryGetProperty("buying_power", out var bp))
        {
            return bp.GetDecimal();
        }
        
        if (balances.TryGetProperty("cash", out var cash) &&
            cash.TryGetProperty("cash_available", out var cashAvail))
        {
            return cashAvail.GetDecimal();
        }
        
        return 0m;
    }

    /// <inheritdoc />
    public async Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(
            $"/markets/quotes?symbols={symbol}",
            cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        
        var quotes = doc.RootElement.GetProperty("quotes");
        
        // Handle single quote vs array
        JsonElement quote;
        if (quotes.TryGetProperty("quote", out var quoteElement))
        {
            quote = quoteElement.ValueKind == JsonValueKind.Array 
                ? quoteElement[0] 
                : quoteElement;
        }
        else
        {
            throw new InvalidOperationException($"No quote found for symbol {symbol}");
        }
        
        // Prefer last trade price, fall back to bid/ask midpoint
        if (quote.TryGetProperty("last", out var last) && last.ValueKind != JsonValueKind.Null)
        {
            return last.GetDecimal();
        }
        
        var bid = quote.TryGetProperty("bid", out var bidEl) ? bidEl.GetDecimal() : 0m;
        var ask = quote.TryGetProperty("ask", out var askEl) ? askEl.GetDecimal() : 0m;
        return (bid + ask) / 2m;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/markets/quotes?symbols={symbol}",
                cancellationToken);
            
            if (!response.IsSuccessStatusCode)
                return false;
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            
            var quotes = doc.RootElement.GetProperty("quotes");
            if (quotes.TryGetProperty("quote", out var quote))
            {
                // Check for "unmatched" symbols
                if (quote.ValueKind == JsonValueKind.Object && 
                    quote.TryGetProperty("symbol", out _))
                {
                    return true;
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    #region Private Helpers

    private static Dictionary<string, string> BuildOrderFormData(BotOrderRequest request)
    {
        var data = new Dictionary<string, string>
        {
            ["class"] = "equity",
            ["symbol"] = request.Symbol,
            ["side"] = request.Side == BotOrderSide.Buy ? "buy" : "sell",
            ["quantity"] = request.Quantity.ToString(),
            ["type"] = MapOrderType(request.Type),
            ["duration"] = MapTimeInForce(request.TimeInForce)
        };
        
        if (request.LimitPrice.HasValue)
        {
            data["price"] = request.LimitPrice.Value.ToString("F2");
        }
        
        if (request.StopPrice.HasValue)
        {
            data["stop"] = request.StopPrice.Value.ToString("F2");
        }
        
        if (!string.IsNullOrEmpty(request.ClientOrderId))
        {
            data["tag"] = request.ClientOrderId;
        }
        
        return data;
    }

    private static string MapOrderType(BotOrderType type) => type switch
    {
        BotOrderType.Market => "market",
        BotOrderType.Limit => "limit",
        BotOrderType.StopLimit => "stop_limit",
        BotOrderType.TrailingStop => "trailing_stop",
        _ => "market"
    };

    private static string MapTimeInForce(BotTimeInForce tif) => tif switch
    {
        BotTimeInForce.Day => "day",
        BotTimeInForce.Gtc => "gtc",
        BotTimeInForce.Ioc => "immediate",
        BotTimeInForce.Fok => "fill_or_kill",
        BotTimeInForce.Opg => "day", // Tradier doesn't have market-on-open
        BotTimeInForce.Cls => "day", // Tradier doesn't have market-on-close
        _ => "day"
    };

    private static BotOrderStatus MapOrderStatus(string status) => status.ToLowerInvariant() switch
    {
        "pending" => BotOrderStatus.New,
        "open" => BotOrderStatus.Accepted,
        "partially_filled" => BotOrderStatus.PartiallyFilled,
        "filled" => BotOrderStatus.Filled,
        "canceled" => BotOrderStatus.Canceled,
        "cancelled" => BotOrderStatus.Canceled,
        "expired" => BotOrderStatus.Expired,
        "rejected" => BotOrderStatus.Rejected,
        _ => BotOrderStatus.New
    };

    private static BotOrderSide MapOrderSide(string side) => side.ToLowerInvariant() switch
    {
        "buy" => BotOrderSide.Buy,
        "buy_to_cover" => BotOrderSide.Buy,
        "sell" => BotOrderSide.Sell,
        "sell_short" => BotOrderSide.Sell,
        _ => BotOrderSide.Buy
    };

    private static BotOrderType MapOrderTypeFromString(string type) => type.ToLowerInvariant() switch
    {
        "market" => BotOrderType.Market,
        "limit" => BotOrderType.Limit,
        "stop_limit" => BotOrderType.StopLimit,
        "trailing_stop" => BotOrderType.TrailingStop,
        _ => BotOrderType.Market
    };

    private BotOrder ParseOrderResponse(string json, Guid orderId)
    {
        using var doc = JsonDocument.Parse(json);
        var order = doc.RootElement.GetProperty("order");
        
        var symbol = order.GetProperty("symbol").GetString() ?? "";
        var side = order.GetProperty("side").GetString() ?? "buy";
        var type = order.GetProperty("type").GetString() ?? "market";
        var status = order.GetProperty("status").GetString() ?? "pending";
        var quantity = order.GetProperty("quantity").GetInt64();
        
        long filledQty = 0;
        if (order.TryGetProperty("exec_quantity", out var execQty))
        {
            filledQty = execQty.GetInt64();
        }
        
        decimal? avgPrice = null;
        if (order.TryGetProperty("avg_fill_price", out var avgPriceEl) && 
            avgPriceEl.ValueKind != JsonValueKind.Null)
        {
            avgPrice = avgPriceEl.GetDecimal();
        }
        
        decimal? limitPrice = null;
        if (order.TryGetProperty("price", out var priceEl) && 
            priceEl.ValueKind != JsonValueKind.Null)
        {
            limitPrice = priceEl.GetDecimal();
        }
        
        string? clientOrderId = null;
        if (order.TryGetProperty("tag", out var tagEl) && 
            tagEl.ValueKind != JsonValueKind.Null)
        {
            clientOrderId = tagEl.GetString();
        }
        
        DateTime? submittedAt = null;
        if (order.TryGetProperty("create_date", out var createDateEl))
        {
            if (DateTime.TryParse(createDateEl.GetString(), out var parsed))
            {
                submittedAt = parsed.ToUniversalTime();
            }
        }
        
        DateTime? filledAt = null;
        if (order.TryGetProperty("transaction_date", out var transDateEl) && 
            transDateEl.ValueKind != JsonValueKind.Null)
        {
            if (DateTime.TryParse(transDateEl.GetString(), out var parsed))
            {
                filledAt = parsed.ToUniversalTime();
            }
        }
        
        return new BotOrder
        {
            OrderId = orderId,
            ClientOrderId = clientOrderId,
            Symbol = symbol,
            Side = MapOrderSide(side),
            Type = MapOrderTypeFromString(type),
            Status = MapOrderStatus(status),
            Quantity = quantity,
            FilledQuantity = filledQty,
            AverageFillPrice = avgPrice,
            LimitPrice = limitPrice,
            SubmittedAtUtc = submittedAt,
            FilledAtUtc = filledAt
        };
    }

    private static List<BotPosition> ParsePositionsResponse(string json)
    {
        var positions = new List<BotPosition>();
        
        using var doc = JsonDocument.Parse(json);
        var positionsRoot = doc.RootElement.GetProperty("positions");
        
        // Handle null positions (no positions)
        if (positionsRoot.ValueKind == JsonValueKind.Null)
            return positions;
        
        // Handle single position vs array
        if (!positionsRoot.TryGetProperty("position", out var positionElement))
            return positions;
        
        var positionArray = positionElement.ValueKind == JsonValueKind.Array
            ? positionElement.EnumerateArray()
            : new[] { positionElement }.AsEnumerable().Select(x => x);
        
        foreach (var pos in positionArray)
        {
            var symbol = pos.GetProperty("symbol").GetString() ?? "";
            var quantity = pos.GetProperty("quantity").GetInt64();
            var costBasis = pos.GetProperty("cost_basis").GetDecimal();
            var avgPrice = quantity != 0 ? costBasis / Math.Abs(quantity) : 0m;
            
            decimal? currentPrice = null;
            if (pos.TryGetProperty("last_price", out var lastPriceEl) && 
                lastPriceEl.ValueKind != JsonValueKind.Null)
            {
                currentPrice = lastPriceEl.GetDecimal();
            }
            
            decimal? marketValue = null;
            if (pos.TryGetProperty("market_value", out var mvEl) && 
                mvEl.ValueKind != JsonValueKind.Null)
            {
                marketValue = mvEl.GetDecimal();
            }
            
            positions.Add(new BotPosition(symbol, quantity, avgPrice, currentPrice, marketValue));
        }
        
        return positions;
    }

    #endregion
}
