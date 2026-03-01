namespace qqqBot;

/// <summary>
/// Maintains a rolling window of price ticks to calculate real-time volatility ("Weather").
/// Tracks 60-second High/Low candle data to measure the current battle between buyers and sellers.
/// 
/// Usage:
///   - Feed every benchmark tick via AddTick()
///   - Read GetMetrics() to get the current "storm factor"
///   
/// The PercentVolatility metric tells the Trader whether conditions are:
///   - Sunny  (low vol, tight range)  → Tighten stops, sniper mode
///   - Stormy (high vol, wide range)  → Widen stops, raise entry confidence
/// </summary>
public class VolatilityTracker
{
    private readonly int _windowSeconds;
    private readonly List<(decimal Price, DateTime Time)> _history = new();
    private readonly object _lock = new();

    public VolatilityTracker(int windowSeconds = 60)
    {
        _windowSeconds = windowSeconds;
    }

    /// <summary>
    /// Record a new price tick. Old ticks beyond the rolling window are pruned.
    /// </summary>
    public void AddTick(decimal price, DateTime timestampUtc)
    {
        lock (_lock)
        {
            _history.Add((price, timestampUtc));

            // Prune expired data (keep only last N seconds)
            var cutoff = timestampUtc.AddSeconds(-_windowSeconds);
            int removeCount = 0;
            while (removeCount < _history.Count && _history[removeCount].Time < cutoff)
            {
                removeCount++;
            }
            if (removeCount > 0)
            {
                _history.RemoveRange(0, removeCount);
            }
        }
    }

    /// <summary>
    /// Get current volatility metrics from the rolling window.
    /// </summary>
    /// <returns>
    /// High: highest price in window,
    /// Low: lowest price in window,
    /// Range: absolute spread (High - Low),
    /// PercentVolatility: range as a fraction of the low price (Range / Low)
    /// </returns>
    public (decimal High, decimal Low, decimal Range, decimal PercentVolatility) GetMetrics()
    {
        lock (_lock)
        {
            if (_history.Count == 0)
                return (0, 0, 0, 0);

            decimal max = _history[0].Price;
            decimal min = _history[0].Price;

            for (int i = 1; i < _history.Count; i++)
            {
                var p = _history[i].Price;
                if (p > max) max = p;
                if (p < min) min = p;
            }

            decimal range = max - min;
            decimal percent = min > 0 ? range / min : 0;

            return (max, min, range, percent);
        }
    }

    /// <summary>
    /// Number of ticks currently in the rolling window.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _history.Count;
            }
        }
    }

    /// <summary>
    /// Reset the tracker (e.g., on new trading day).
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _history.Clear();
        }
    }
}
