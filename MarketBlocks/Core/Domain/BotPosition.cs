namespace MarketBlocks.Core.Domain;

/// <summary>
/// Broker-agnostic position representation.
/// Immutable record for thread-safety and value semantics.
/// </summary>
/// <param name="Symbol">The ticker symbol held.</param>
/// <param name="Quantity">Number of shares/units held (positive for long, negative for short).</param>
/// <param name="AverageEntryPrice">Average cost basis per share.</param>
/// <param name="CurrentPrice">Current market price (if known).</param>
/// <param name="MarketValue">Total market value of position.</param>
public readonly record struct BotPosition(
    string Symbol,
    long Quantity,
    decimal AverageEntryPrice,
    decimal? CurrentPrice = null,
    decimal? MarketValue = null)
{
    /// <summary>
    /// Whether this is a long position (quantity > 0).
    /// </summary>
    public bool IsLong => Quantity > 0;
    
    /// <summary>
    /// Whether this is a short position (quantity < 0).
    /// </summary>
    public bool IsShort => Quantity < 0;
    
    /// <summary>
    /// Absolute quantity (always positive).
    /// </summary>
    public long AbsoluteQuantity => System.Math.Abs(Quantity);
    
    /// <summary>
    /// Total cost basis (Quantity × AverageEntryPrice).
    /// </summary>
    public decimal CostBasis => AbsoluteQuantity * AverageEntryPrice;
    
    /// <summary>
    /// Unrealized P/L (if current price is known).
    /// </summary>
    public decimal? UnrealizedPnL => CurrentPrice.HasValue 
        ? (CurrentPrice.Value - AverageEntryPrice) * Quantity 
        : null;
    
    /// <summary>
    /// Unrealized P/L as a percentage (if current price is known).
    /// </summary>
    public decimal? UnrealizedPnLPercent => CurrentPrice.HasValue && AverageEntryPrice != 0
        ? (CurrentPrice.Value - AverageEntryPrice) / AverageEntryPrice * 100m
        : null;
    
    /// <summary>
    /// Creates an empty/flat position for a symbol.
    /// </summary>
    public static BotPosition Flat(string symbol) 
        => new(symbol, 0, 0m);
}
