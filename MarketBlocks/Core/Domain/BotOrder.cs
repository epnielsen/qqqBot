namespace MarketBlocks.Core.Domain;

/// <summary>
/// Broker-agnostic order result/status.
/// Represents an order that has been submitted to the broker.
/// </summary>
public sealed class BotOrder
{
    /// <summary>Broker-assigned order ID.</summary>
    public required Guid OrderId { get; init; }
    
    /// <summary>Client-provided order ID (if any).</summary>
    public string? ClientOrderId { get; init; }
    
    /// <summary>Symbol being traded.</summary>
    public required string Symbol { get; init; }
    
    /// <summary>Order side (buy/sell).</summary>
    public required BotOrderSide Side { get; init; }
    
    /// <summary>Order type.</summary>
    public required BotOrderType Type { get; init; }
    
    /// <summary>Current order status.</summary>
    public required BotOrderStatus Status { get; init; }
    
    /// <summary>Original quantity requested.</summary>
    public required long Quantity { get; init; }
    
    /// <summary>Quantity that has been filled.</summary>
    public long FilledQuantity { get; init; }
    
    /// <summary>Average fill price (null if no fills yet).</summary>
    public decimal? AverageFillPrice { get; init; }
    
    /// <summary>Limit price (for limit orders).</summary>
    public decimal? LimitPrice { get; init; }
    
    /// <summary>Time the order was submitted.</summary>
    public DateTime? SubmittedAtUtc { get; init; }
    
    /// <summary>Time the order was filled (null if not filled).</summary>
    public DateTime? FilledAtUtc { get; init; }
    
    /// <summary>
    /// Whether the order is in a terminal state (filled, canceled, expired, rejected).
    /// </summary>
    public bool IsTerminal => Status is 
        BotOrderStatus.Filled or 
        BotOrderStatus.Canceled or 
        BotOrderStatus.Expired or 
        BotOrderStatus.Rejected;
    
    /// <summary>
    /// Whether any portion of the order has been filled.
    /// </summary>
    public bool HasFill => FilledQuantity > 0 && AverageFillPrice.HasValue;
    
    /// <summary>
    /// Remaining quantity to be filled.
    /// </summary>
    public long RemainingQuantity => Quantity - FilledQuantity;
    
    /// <summary>
    /// Total value of fills (FilledQuantity × AverageFillPrice).
    /// </summary>
    public decimal TotalFillValue => FilledQuantity * (AverageFillPrice ?? 0m);
}
