namespace qqqBot.Core.Domain;

/// <summary>
/// Trade price message for the high-performance channel pipeline.
/// Struct to avoid heap allocation on the hot path.
/// </summary>
public readonly struct TradeTick
{
    /// <summary>Trade price.</summary>
    public decimal Price { get; init; }
    
    /// <summary>Timestamp of the trade (UTC).</summary>
    public DateTime TimestampUtc { get; init; }
    
    /// <summary>
    /// Source identifier: true = primary benchmark (e.g., QQQ), false = secondary (e.g., BTC).
    /// </summary>
    public bool IsBenchmark { get; init; }
    
    /// <summary>
    /// Optional symbol for multi-symbol pipelines.
    /// </summary>
    public string? Symbol { get; init; }
    
    /// <summary>
    /// Trade size/volume (if available from data source).
    /// </summary>
    public long? Size { get; init; }
    
    /// <summary>
    /// Creates a benchmark tick.
    /// </summary>
    public static TradeTick Benchmark(decimal price, DateTime? timestampUtc = null, string? symbol = null)
        => new() 
        { 
            Price = price, 
            TimestampUtc = timestampUtc ?? DateTime.UtcNow, 
            IsBenchmark = true,
            Symbol = symbol
        };
    
    /// <summary>
    /// Creates a secondary (non-benchmark) tick.
    /// </summary>
    public static TradeTick Secondary(decimal price, DateTime? timestampUtc = null, string? symbol = null)
        => new() 
        { 
            Price = price, 
            TimestampUtc = timestampUtc ?? DateTime.UtcNow, 
            IsBenchmark = false,
            Symbol = symbol
        };
}
