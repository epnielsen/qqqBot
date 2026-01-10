namespace qqqBot.Core.Domain;

/// <summary>
/// Broker-agnostic order request.
/// Immutable record for thread-safety and value semantics.
/// </summary>
/// <param name="Symbol">The ticker symbol to trade.</param>
/// <param name="Quantity">Number of shares/units to trade.</param>
/// <param name="Side">Buy or sell.</param>
/// <param name="Type">Order type (market, limit, etc.).</param>
/// <param name="TimeInForce">How long the order remains active.</param>
/// <param name="LimitPrice">Limit price (required for Limit orders).</param>
/// <param name="StopPrice">Stop price (for stop orders).</param>
/// <param name="ClientOrderId">Optional client-generated order ID for tracking.</param>
public readonly record struct BotOrderRequest(
    string Symbol,
    long Quantity,
    BotOrderSide Side,
    BotOrderType Type,
    BotTimeInForce TimeInForce = BotTimeInForce.Day,
    decimal? LimitPrice = null,
    decimal? StopPrice = null,
    string? ClientOrderId = null)
{
    /// <summary>
    /// Creates a market buy order.
    /// </summary>
    public static BotOrderRequest MarketBuy(string symbol, long quantity, string? clientOrderId = null)
        => new(symbol, quantity, BotOrderSide.Buy, BotOrderType.Market, BotTimeInForce.Day, ClientOrderId: clientOrderId);
    
    /// <summary>
    /// Creates a market sell order.
    /// </summary>
    public static BotOrderRequest MarketSell(string symbol, long quantity, string? clientOrderId = null)
        => new(symbol, quantity, BotOrderSide.Sell, BotOrderType.Market, BotTimeInForce.Day, ClientOrderId: clientOrderId);
    
    /// <summary>
    /// Creates an IOC (immediate-or-cancel) limit buy order.
    /// </summary>
    public static BotOrderRequest IocLimitBuy(string symbol, long quantity, decimal limitPrice, string? clientOrderId = null)
        => new(symbol, quantity, BotOrderSide.Buy, BotOrderType.Limit, BotTimeInForce.Ioc, limitPrice, ClientOrderId: clientOrderId);
    
    /// <summary>
    /// Creates an IOC (immediate-or-cancel) limit sell order.
    /// </summary>
    public static BotOrderRequest IocLimitSell(string symbol, long quantity, decimal limitPrice, string? clientOrderId = null)
        => new(symbol, quantity, BotOrderSide.Sell, BotOrderType.Limit, BotTimeInForce.Ioc, limitPrice, ClientOrderId: clientOrderId);
    
    /// <summary>
    /// Creates a day limit buy order.
    /// </summary>
    public static BotOrderRequest LimitBuy(string symbol, long quantity, decimal limitPrice, string? clientOrderId = null)
        => new(symbol, quantity, BotOrderSide.Buy, BotOrderType.Limit, BotTimeInForce.Day, limitPrice, ClientOrderId: clientOrderId);
    
    /// <summary>
    /// Creates a day limit sell order.
    /// </summary>
    public static BotOrderRequest LimitSell(string symbol, long quantity, decimal limitPrice, string? clientOrderId = null)
        => new(symbol, quantity, BotOrderSide.Sell, BotOrderType.Limit, BotTimeInForce.Day, limitPrice, ClientOrderId: clientOrderId);
}
