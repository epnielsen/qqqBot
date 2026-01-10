using Alpaca.Markets;
using MarketBlocks.Core.Domain;
using MarketBlocks.Core.Interfaces;

namespace MarketBlocks.Infrastructure.Alpaca;

/// <summary>
/// Alpaca Markets implementation of IBrokerExecution.
/// Adapts Alpaca.Markets API to broker-agnostic Core interfaces.
/// </summary>
public sealed class AlpacaExecutionAdapter : IBrokerExecution
{
    private readonly IAlpacaTradingClient _tradingClient;
    private readonly IAlpacaDataClient? _dataClient;

    public AlpacaExecutionAdapter(IAlpacaTradingClient tradingClient, IAlpacaDataClient? dataClient = null)
    {
        _tradingClient = tradingClient ?? throw new ArgumentNullException(nameof(tradingClient));
        _dataClient = dataClient;
    }

    /// <inheritdoc />
    public async Task<BotOrder> SubmitOrderAsync(BotOrderRequest request, CancellationToken cancellationToken = default)
    {
        // Map BotOrderRequest to Alpaca NewOrderRequest
        var alpacaRequest = MapToAlpacaRequest(request);
        
        // Submit order to Alpaca
        var order = await _tradingClient.PostOrderAsync(alpacaRequest, cancellationToken);
        
        // OPTIMIZATION: Check if order is already in terminal state
        // If so, return immediately without calling GetOrderAsync
        if (IsTerminalStatus(order.OrderStatus))
        {
            return MapToBotOrder(order);
        }
        
        // For non-terminal states (New, Accepted, PartiallyFilled), 
        // caller may need to poll GetOrderAsync for final status
        return MapToBotOrder(order);
    }

    /// <inheritdoc />
    public async Task<BotOrder> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await _tradingClient.GetOrderAsync(orderId, cancellationToken);
        return MapToBotOrder(order);
    }

    /// <inheritdoc />
    public async Task<bool> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _tradingClient.CancelOrderAsync(orderId, cancellationToken);
            return true;
        }
        catch (RestClientErrorException)
        {
            // Order may already be filled/canceled
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<BotPosition?> GetPositionAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            var position = await _tradingClient.GetPositionAsync(symbol, cancellationToken);
            return MapToBotPosition(position);
        }
        catch (RestClientErrorException ex) when (ex.Message.Contains("position does not exist"))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BotPosition>> GetAllPositionsAsync(CancellationToken cancellationToken = default)
    {
        var positions = await _tradingClient.ListPositionsAsync(cancellationToken);
        return positions.Select(MapToBotPosition).ToList();
    }

    /// <inheritdoc />
    public async Task<decimal> GetBuyingPowerAsync(CancellationToken cancellationToken = default)
    {
        var account = await _tradingClient.GetAccountAsync(cancellationToken);
        return account.BuyingPower ?? 0m;
    }

    /// <inheritdoc />
    public async Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (_dataClient == null)
        {
            throw new InvalidOperationException("Data client not configured. Cannot get latest price.");
        }
        
        var request = new LatestMarketDataRequest(symbol);
        var trade = await _dataClient.GetLatestTradeAsync(request, cancellationToken);
        return trade.Price;
    }

    /// <inheritdoc />
    public async Task<bool> ValidateSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        try
        {
            var asset = await _tradingClient.GetAssetAsync(symbol, cancellationToken);
            return asset.IsTradable;
        }
        catch (RestClientErrorException)
        {
            return false;
        }
    }

    #region Mapping Helpers

    private static NewOrderRequest MapToAlpacaRequest(BotOrderRequest request)
    {
        var orderSide = request.Side == BotOrderSide.Buy ? OrderSide.Buy : OrderSide.Sell;
        var quantity = OrderQuantity.Fractional(request.Quantity);
        
        NewOrderRequest alpacaRequest = request.Type switch
        {
            BotOrderType.Market => new NewOrderRequest(request.Symbol, quantity, orderSide, OrderType.Market, MapTimeInForce(request.TimeInForce)),
            BotOrderType.Limit when request.LimitPrice.HasValue => new NewOrderRequest(request.Symbol, quantity, orderSide, OrderType.Limit, MapTimeInForce(request.TimeInForce))
            {
                LimitPrice = request.LimitPrice.Value
            },
            BotOrderType.StopLimit when request.LimitPrice.HasValue && request.StopPrice.HasValue => new NewOrderRequest(request.Symbol, quantity, orderSide, OrderType.StopLimit, MapTimeInForce(request.TimeInForce))
            {
                LimitPrice = request.LimitPrice.Value,
                StopPrice = request.StopPrice.Value
            },
            BotOrderType.TrailingStop when request.StopPrice.HasValue => new NewOrderRequest(request.Symbol, quantity, orderSide, OrderType.TrailingStop, MapTimeInForce(request.TimeInForce))
            {
                TrailOffsetInDollars = request.StopPrice.Value
            },
            _ => throw new ArgumentException($"Invalid order type/price combination: Type={request.Type}, LimitPrice={request.LimitPrice}, StopPrice={request.StopPrice}")
        };
        
        if (!string.IsNullOrEmpty(request.ClientOrderId))
        {
            alpacaRequest.ClientOrderId = request.ClientOrderId;
        }
        
        return alpacaRequest;
    }

    private static TimeInForce MapTimeInForce(BotTimeInForce tif) => tif switch
    {
        BotTimeInForce.Day => TimeInForce.Day,
        BotTimeInForce.Gtc => TimeInForce.Gtc,
        BotTimeInForce.Ioc => TimeInForce.Ioc,
        BotTimeInForce.Fok => TimeInForce.Fok,
        BotTimeInForce.Opg => TimeInForce.Opg,
        BotTimeInForce.Cls => TimeInForce.Cls,
        _ => TimeInForce.Day
    };

    private static bool IsTerminalStatus(OrderStatus status) => status is
        OrderStatus.Filled or
        OrderStatus.Canceled or
        OrderStatus.Expired or
        OrderStatus.Rejected or
        OrderStatus.DoneForDay;

    private static BotOrder MapToBotOrder(IOrder order) => new()
    {
        OrderId = order.OrderId,
        ClientOrderId = order.ClientOrderId,
        Symbol = order.Symbol,
        Side = order.OrderSide == OrderSide.Buy ? BotOrderSide.Buy : BotOrderSide.Sell,
        Type = MapOrderType(order.OrderType),
        Status = MapOrderStatus(order.OrderStatus),
        Quantity = (long)order.Quantity,
        FilledQuantity = (long)order.FilledQuantity,
        AverageFillPrice = order.AverageFillPrice,
        LimitPrice = order.LimitPrice,
        SubmittedAtUtc = order.SubmittedAtUtc,
        FilledAtUtc = order.FilledAtUtc
    };

    private static BotOrderType MapOrderType(OrderType type) => type switch
    {
        OrderType.Market => BotOrderType.Market,
        OrderType.Limit => BotOrderType.Limit,
        OrderType.StopLimit => BotOrderType.StopLimit,
        OrderType.TrailingStop => BotOrderType.TrailingStop,
        _ => BotOrderType.Market
    };

    private static BotOrderStatus MapOrderStatus(OrderStatus status) => status switch
    {
        OrderStatus.New => BotOrderStatus.New,
        OrderStatus.Accepted => BotOrderStatus.Accepted,
        OrderStatus.PartiallyFilled => BotOrderStatus.PartiallyFilled,
        OrderStatus.Filled => BotOrderStatus.Filled,
        OrderStatus.Canceled => BotOrderStatus.Canceled,
        OrderStatus.Expired => BotOrderStatus.Expired,
        OrderStatus.Rejected => BotOrderStatus.Rejected,
        OrderStatus.PendingCancel => BotOrderStatus.PendingCancel,
        OrderStatus.PendingReplace => BotOrderStatus.PendingReplace,
        OrderStatus.Suspended => BotOrderStatus.Suspended,
        OrderStatus.DoneForDay => BotOrderStatus.Canceled, // Map DoneForDay to Canceled
        _ => BotOrderStatus.New
    };

    private static BotPosition MapToBotPosition(IPosition position) => new(
        Symbol: position.Symbol,
        Quantity: (long)position.Quantity,
        AverageEntryPrice: position.AverageEntryPrice,
        CurrentPrice: position.AssetCurrentPrice,
        MarketValue: position.MarketValue
    );

    #endregion
}
