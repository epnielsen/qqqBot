namespace MarketBlocks.Core.Domain;

/// <summary>
/// Broker-agnostic order side (buy or sell).
/// </summary>
public enum BotOrderSide
{
    Buy,
    Sell
}

/// <summary>
/// Broker-agnostic order type.
/// </summary>
public enum BotOrderType
{
    Market,
    Limit,
    StopLimit,
    TrailingStop
}

/// <summary>
/// Broker-agnostic time-in-force specifier.
/// </summary>
public enum BotTimeInForce
{
    /// <summary>Day order - expires at end of trading day.</summary>
    Day,
    
    /// <summary>Good-til-canceled - remains active until filled or canceled.</summary>
    Gtc,
    
    /// <summary>Immediate-or-cancel - fill immediately or cancel unfilled portion.</summary>
    Ioc,
    
    /// <summary>Fill-or-kill - must fill entire order immediately or cancel.</summary>
    Fok,
    
    /// <summary>On-open - execute at market open.</summary>
    Opg,
    
    /// <summary>On-close - execute at market close.</summary>
    Cls
}

/// <summary>
/// Broker-agnostic order status.
/// </summary>
public enum BotOrderStatus
{
    /// <summary>Order received but not yet processed.</summary>
    New,
    
    /// <summary>Order accepted and working.</summary>
    Accepted,
    
    /// <summary>Order partially filled.</summary>
    PartiallyFilled,
    
    /// <summary>Order completely filled.</summary>
    Filled,
    
    /// <summary>Order canceled (by user or system).</summary>
    Canceled,
    
    /// <summary>Order expired (time-in-force reached).</summary>
    Expired,
    
    /// <summary>Order rejected by broker.</summary>
    Rejected,
    
    /// <summary>Order pending cancel request.</summary>
    PendingCancel,
    
    /// <summary>Order pending replacement.</summary>
    PendingReplace,
    
    /// <summary>Order suspended.</summary>
    Suspended
}
