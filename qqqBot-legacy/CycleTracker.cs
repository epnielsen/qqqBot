namespace qqqBot;

/// <summary>
/// Measures the market's "Rhythm" by tracking how often the SMA Slope flips
/// from Positive to Negative (Zero-Crossing Detection).
/// 
/// Concept: Market Phasing / Cycle Analysis
/// 
/// The bot already has Speed (Velocity via Slope) and Intensity (Volatility via VolatilityTracker),
/// but it lacked Rhythm (Periodicity). This tracker fills that gap.
/// 
/// How it works:
///   1. Event:       SMA Slope crosses 0.0 (momentum flip).
///   2. Measurement: Time elapsed since the last flip.
///   3. Metric:      AverageHalfCycleSeconds — e.g., if the slope flips every 2 minutes,
///                   the half-cycle is 120s.
/// 
/// Usage:
///   - Feed every slope value via AddSlope()
///   - Read GetMetrics() to get the current rhythm and stability
/// 
/// Insight: If TrendWaitSeconds is 180s but the cycle is only 120s, the bot is
/// mathematically guaranteed to hold past the reversal and lose money.
/// The Trader can use CycleTracker metrics to auto-tune its patience.
/// </summary>
public class CycleTracker
{
    private DateTime? _lastCrossingTime;
    private int _lastSign;
    private readonly List<double> _halfCycleDurations = new();
    private readonly object _lock = new();

    /// <summary>
    /// Rolling window of half-cycle measurements to determine the current rhythm.
    /// 10 cycles gives a stable average without being too slow to adapt.
    /// </summary>
    private readonly int _historySize;
    
    /// <summary>
    /// Minimum duration (seconds) for a half-cycle to count.
    /// Filters out micro-flips caused by noise (e.g., slope jittering around zero).
    /// </summary>
    private readonly double _minDurationSeconds;

    public CycleTracker(int historySize = 10, double minDurationSeconds = 10)
    {
        _historySize = historySize;
        _minDurationSeconds = minDurationSeconds;
    }

    /// <summary>
    /// Feed a new slope value. Detects zero-crossings and measures the duration
    /// of each "leg" (bull run or bear run) as a half-cycle.
    /// </summary>
    /// <param name="slope">The current SMA slope (positive = bullish, negative = bearish).</param>
    /// <param name="now">The timestamp of this observation.</param>
    public void AddSlope(decimal slope, DateTime now)
    {
        lock (_lock)
        {
            int currentSign = Math.Sign(slope);

            // Skip zero slopes (indeterminate direction)
            if (currentSign == 0) return;

            // 1. Initialize: First non-zero slope sets the baseline
            if (_lastSign == 0)
            {
                _lastSign = currentSign;
                _lastCrossingTime = now;
                return;
            }

            // 2. Detect Zero Crossing (Slope Flip)
            if (currentSign != _lastSign)
            {
                if (_lastCrossingTime.HasValue)
                {
                    // Measure duration of that "leg" (e.g., the bull run duration)
                    var duration = (now - _lastCrossingTime.Value).TotalSeconds;

                    // Filter noise: Ignore micro-flips (< threshold seconds)
                    if (duration > _minDurationSeconds)
                    {
                        _halfCycleDurations.Add(duration);
                        if (_halfCycleDurations.Count > _historySize)
                            _halfCycleDurations.RemoveAt(0);
                    }
                }

                _lastSign = currentSign;
                _lastCrossingTime = now;
            }
        }
    }

    /// <summary>
    /// Get the current cycle metrics.
    /// </summary>
    /// <returns>
    /// AvgHalfCycle: Average half-cycle duration in seconds (e.g., 120 = slope flips every 2 minutes).
    /// Stability: Standard deviation of half-cycle durations.
    ///   - Low stability value = Regular "Sine Wave" oscillation (predictable rhythm).
    ///   - High stability value = Random chaos (no reliable rhythm).
    /// Returns (0, 0) if fewer than 3 measurements are available.
    /// </returns>
    public (double AvgHalfCycle, double Stability) GetMetrics()
    {
        lock (_lock)
        {
            if (_halfCycleDurations.Count < 3) return (0, 0);

            double avg = _halfCycleDurations.Average();

            // Standard Deviation: measures how consistent the rhythm is.
            // Low StdDev = the market is oscillating like a sine wave (exploitable).
            // High StdDev = the durations are all over the place (noise).
            double sumSquares = _halfCycleDurations.Sum(d => Math.Pow(d - avg, 2));
            double stdDev = Math.Sqrt(sumSquares / _halfCycleDurations.Count);

            return (avg, stdDev);
        }
    }
}
