using System.Globalization;
using System.Threading.Channels;
using MarketBlocks.Bots.Services;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// "The Player" — replays recorded CSV data through the AnalystEngine as if it were live.
/// 
/// Implements IAnalystMarketDataSource so it plugs directly into the existing DI slot
/// where StreamingAnalystDataSource or PollingAnalystDataSource normally sit.
/// 
/// Features:
///   - Speed multiplier: 1.0x = real-time, 10.0x = 10× fast-forward, 0 = instant (max speed)
///   - Deterministic replay: timestamps from the CSV drive the PriceTick.TimestampUtc
///   - Multi-symbol: loads all {YYYYMMDD}_market_data_{symbol}.csv files, merges by timestamp
/// 
/// Usage: Injected by ProgramRefactored when --mode=replay is specified.
/// </summary>
public sealed class ReplayMarketDataSource : IAnalystMarketDataSource
{
    private readonly ILogger _logger;
    private readonly string _dataDirectory;
    private readonly DateOnly _replayDate;
    private readonly double _speedMultiplier;
    private readonly Action<string, decimal, DateTime>? _onPriceUpdate;
    private readonly Action<DateTime>? _onTimeAdvance;
    private readonly int _replaySeed;
    private readonly TimeOnly? _startTimeFilter; // Eastern time filter
    private readonly TimeOnly? _endTimeFilter;   // Eastern time filter
    private readonly bool _skipInterpolation;
    private readonly TimeZoneInfo _easternZone;
    private bool _connected;

    /// <summary>
    /// Parsed tick from CSV, sortable by timestamp.
    /// OHLC fields are populated for historical bar data (columns 5-7); null for recorded tick data.
    /// </summary>
    private record CsvTick(DateTime TimestampUtc, string Symbol, decimal Price, long Volume, string Source,
        decimal? Open = null, decimal? High = null, decimal? Low = null);

    public ReplayMarketDataSource(
        ILogger logger,
        string dataDirectory,
        DateOnly replayDate,
        double speedMultiplier = 1.0,
        Action<string, decimal, DateTime>? onPriceUpdate = null,
        Action<DateTime>? onTimeAdvance = null,
        int? replaySeed = null,
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        bool skipInterpolation = false)
    {
        _logger = logger;
        _dataDirectory = dataDirectory;
        _replayDate = replayDate;
        _speedMultiplier = speedMultiplier;
        _onPriceUpdate = onPriceUpdate;
        _onTimeAdvance = onTimeAdvance;
        _replaySeed = replaySeed ?? _replayDate.DayNumber;
        _startTimeFilter = startTime;
        _endTimeFilter = endTime;
        _skipInterpolation = skipInterpolation;
        _easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _connected = true;
        _logger.LogInformation("[REPLAY] ==========================================");
        _logger.LogInformation("[REPLAY]  R E P L A Y   M O D E");
        _logger.LogInformation("[REPLAY]  Date: {Date}", _replayDate);
        _logger.LogInformation("[REPLAY]  Speed: {Speed}x", _speedMultiplier);
        _logger.LogInformation("[REPLAY]  Seed:  {Seed}", _replaySeed);
        if (_startTimeFilter.HasValue || _endTimeFilter.HasValue)
        {
            _logger.LogInformation("[REPLAY]  Segment: {Start} -> {End} (Eastern)",
                _startTimeFilter?.ToString("HH:mm") ?? "open",
                _endTimeFilter?.ToString("HH:mm") ?? "close");
        }
        _logger.LogInformation("[REPLAY]  Data Dir: {Dir}", _dataDirectory);
        _logger.LogInformation("[REPLAY] ==========================================");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        _logger.LogInformation("[REPLAY] Data source disconnected.");
        return Task.CompletedTask;
    }

    public async Task SubscribeAsync(
        IEnumerable<AnalystSubscription> subscriptions,
        ChannelWriter<PriceTick> writer,
        CancellationToken ct)
    {
        if (!_connected)
            throw new InvalidOperationException("Call ConnectAsync before SubscribeAsync");

        var subList = subscriptions.ToList();
        var symbolMap = subList.ToDictionary(s => s.Symbol, s => s.IsBenchmark);

        _logger.LogInformation("[REPLAY] Subscriptions: {Symbols}",
            string.Join(", ", subList.Select(s => $"{s.Symbol} (bench={s.IsBenchmark})")));

        // Load all matching CSV files and merge into one timeline
        var allTicks = new List<CsvTick>();
        var dateStr = _replayDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        foreach (var sub in subList)
        {
            var sanitized = sub.Symbol.Replace("/", "-").Replace("\\", "-");
            var filePath = Path.Combine(_dataDirectory, $"{dateStr}_market_data_{sanitized}.csv");

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("[REPLAY] CSV not found: {File}. Skipping {Symbol}.", filePath, sub.Symbol);
                continue;
            }

            var rawTicks = LoadCsvFile(filePath, sub.Symbol);
            if (_skipInterpolation)
            {
                allTicks.AddRange(rawTicks);
                _logger.LogInformation("[REPLAY] Loaded {Count} ticks (raw, no interpolation) from {File}",
                    rawTicks.Count, filePath);
            }
            else
            {
                var symbolSeed = _replaySeed ^ StableHash(sub.Symbol);
                var ticks = InterpolateWithBrownianBridge(rawTicks, symbolSeed);
                allTicks.AddRange(ticks);
                _logger.LogInformation("[REPLAY] Loaded {Raw} bars -> {Count} ticks (Brownian bridge, seed={Seed}) from {File}",
                    rawTicks.Count, ticks.Count, symbolSeed, filePath);
            }
        }

        if (allTicks.Count == 0)
        {
            _logger.LogError("[REPLAY] No data loaded! Check that CSV files exist in {Dir} for date {Date}",
                _dataDirectory, dateStr);
            writer.Complete();
            return;
        }

        // Sort all ticks chronologically (interleave multi-symbol data)
        allTicks.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        // Apply segment time filter if specified (filter by Eastern time)
        if (_startTimeFilter.HasValue || _endTimeFilter.HasValue)
        {
            var beforeCount = allTicks.Count;
            allTicks = allTicks.Where(t =>
            {
                var eastern = TimeZoneInfo.ConvertTimeFromUtc(t.TimestampUtc, _easternZone);
                var timeOfDay = TimeOnly.FromDateTime(eastern);
                if (_startTimeFilter.HasValue && timeOfDay < _startTimeFilter.Value) return false;
                if (_endTimeFilter.HasValue && timeOfDay > _endTimeFilter.Value) return false;
                return true;
            }).ToList();
            _logger.LogInformation("[REPLAY] Segment filter applied: {Before} -> {After} ticks",
                beforeCount, allTicks.Count);
        }

        _logger.LogInformation("[REPLAY] Playing {Count} total ticks from {Start:HH:mm:ss} to {End:HH:mm:ss}",
            allTicks.Count,
            allTicks[0].TimestampUtc,
            allTicks[^1].TimestampUtc);

        // Replay loop
        DateTime? previousTimestamp = null;

        foreach (var tick in allTicks)
        {
            ct.ThrowIfCancellationRequested();

            // Simulate real-time delay between ticks
            if (previousTimestamp.HasValue && _speedMultiplier > 0)
            {
                var realGap = tick.TimestampUtc - previousTimestamp.Value;
                if (realGap > TimeSpan.Zero)
                {
                    var simulatedDelay = realGap / _speedMultiplier;
                    // Cap max delay at 5 seconds (in case of gaps in data)
                    if (simulatedDelay > TimeSpan.FromSeconds(5))
                        simulatedDelay = TimeSpan.FromSeconds(5);
                    
                    if (simulatedDelay > TimeSpan.FromMilliseconds(1))
                        await Task.Delay(simulatedDelay, ct);
                }
            }
            previousTimestamp = tick.TimestampUtc;

            // Determine if this symbol is a benchmark
            bool isBenchmark = symbolMap.TryGetValue(tick.Symbol, out var bench) && bench;

            // Advance the log-file clock so timestamps reflect replay time, not wall-clock
            _onTimeAdvance?.Invoke(tick.TimestampUtc);

            // Feed price to SimulatedBroker so it can fill orders at correct prices
            _onPriceUpdate?.Invoke(tick.Symbol, tick.Price, tick.TimestampUtc);

            var priceTick = new PriceTick(tick.Symbol, tick.Price, isBenchmark, tick.TimestampUtc, tick.Volume);
            
            // In replay mode the analyst may complete the channel early (e.g. session end at 16:00 ET).
            // TryWrite returns false if the channel is completed; for a bounded(1) channel we need
            // to use WriteAsync but catch ChannelClosedException.
            try
            {
                await writer.WriteAsync(priceTick, ct);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                _logger.LogInformation("[REPLAY] Channel closed by consumer (session ended). Stopping playback.");
                break;
            }
        }

        _logger.LogInformation("[REPLAY] ✓ Playback complete. {Count} ticks replayed.", allTicks.Count);
        
        // Signal end of data — the AnalystEngine loop will detect channel completion.
        // TryComplete tolerates the channel already being closed (e.g. session-end path).
        writer.TryComplete();
    }

    /// <summary>
    /// Interpolates between consecutive bars using a market-microstructure-aware tick generator.
    /// 
    /// When OHLC data is available (historical bars), generates ticks that:
    ///   - Are constrained to the candle's actual High/Low range
    ///   - Cluster into momentum bursts and pauses (ABM-lite) instead of uniform spacing
    ///   - Visit High and Low during the candle to produce realistic slope signals
    ///   - Distribute bar volume across synthetic ticks with front-loading
    /// 
    /// When only Close data is available (old format), falls back to the original
    /// Brownian bridge with Gaussian noise (backward compatible).
    /// 
    /// The walk is seeded so replays are deterministic.
    /// </summary>
    private static List<CsvTick> InterpolateWithBrownianBridge(List<CsvTick> ticks, int seed)
    {
        if (ticks.Count < 2) return ticks;

        var rng = new Random(seed);
        var result = new List<CsvTick>(ticks.Count * 60); // Pre-allocate for ~60x expansion

        for (int i = 0; i < ticks.Count; i++)
        {
            result.Add(ticks[i]); // Always keep the original data point

            if (i + 1 < ticks.Count)
            {
                var gapSeconds = (ticks[i + 1].TimestampUtc - ticks[i].TimestampUtc).TotalSeconds;

                if (gapSeconds > 1.5) // Only interpolate gaps > 1.5 seconds
                {
                    int steps = (int)gapSeconds;
                    var bar = ticks[i];

                    if (bar.Open.HasValue && bar.High.HasValue && bar.Low.HasValue)
                    {
                        // OHLC-aware microstructure interpolation
                        GenerateOhlcTicks(rng, result, bar, ticks[i + 1], steps);
                    }
                    else
                    {
                        // Legacy Brownian bridge (Close-only data)
                        GenerateBrownianBridgeTicks(rng, result, bar, ticks[i + 1], steps);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// OHLC-constrained microstructure tick generator (ABM-lite).
    /// 
    /// Generates a realistic price path within the candle's OHLC range:
    ///   1. Determines candle character (trending vs choppy) from body/range ratio
    ///   2. Builds waypoints: Open → (High or Low first, based on candle direction) → Close
    ///   3. Generates momentum-clustered tick segments between waypoints
    ///   4. Clamps all prices to [Low, High]
    ///   5. Distributes volume with front-loading bias
    /// </summary>
    private static void GenerateOhlcTicks(Random rng, List<CsvTick> result,
        CsvTick bar, CsvTick nextBar, int steps)
    {
        var open = bar.Open!.Value;
        var high = bar.High!.Value;
        var low = bar.Low!.Value;
        var close = nextBar.Price; // Close = next bar's price (which is the close of this bar)
        var range = high - low;
        var symbol = bar.Symbol;
        var barVolume = bar.Volume;

        // Edge case: zero-range candle (unchanged price)
        if (range <= 0)
        {
            for (int s = 1; s < steps; s++)
            {
                var time = bar.TimestampUtc.AddSeconds(s);
                var vol = DistributeVolume(barVolume, s, steps, rng);
                result.Add(new CsvTick(time, symbol, open, vol, "Interpolated"));
            }
            return;
        }

        // Determine candle character
        var body = Math.Abs(close - open);
        var bodyRatio = body / range;
        bool isBullish = close >= open;
        bool isTrending = bodyRatio > 0.5m; // Large body = trending candle

        // Build waypoints: Open → extremes → Close
        // For bullish candles: Open → Low → High → Close (dip-then-rally pattern)
        // For bearish candles: Open → High → Low → Close (rally-then-dip pattern)
        // For choppy candles (small body): visit both extremes with more oscillation
        var waypoints = new List<(decimal price, double fraction)>();

        if (isTrending)
        {
            if (isBullish)
            {
                // Bullish trending: dip to low early, then rally through high to close
                var lowFrac = 0.15 + rng.NextDouble() * 0.15;  // Visit low at 15-30%
                var highFrac = 0.65 + rng.NextDouble() * 0.20;  // Visit high at 65-85%
                waypoints.Add((low, lowFrac));
                waypoints.Add((high, highFrac));
            }
            else
            {
                // Bearish trending: pop to high early, then sell off through low to close
                var highFrac = 0.15 + rng.NextDouble() * 0.15;
                var lowFrac = 0.65 + rng.NextDouble() * 0.20;
                waypoints.Add((high, highFrac));
                waypoints.Add((low, lowFrac));
            }
        }
        else
        {
            // Choppy/doji: visit extremes with more oscillation, order is random
            if (rng.NextDouble() < 0.5)
            {
                waypoints.Add((high, 0.25 + rng.NextDouble() * 0.15));
                waypoints.Add((low, 0.55 + rng.NextDouble() * 0.15));
            }
            else
            {
                waypoints.Add((low, 0.25 + rng.NextDouble() * 0.15));
                waypoints.Add((high, 0.55 + rng.NextDouble() * 0.15));
            }
        }

        // Build segment list: Open → wp1 → wp2 → Close
        var segments = new List<(decimal startPrice, decimal endPrice, int startStep, int endStep)>();
        decimal prevPrice = open;
        int prevStep = 0;

        foreach (var (wpPrice, fraction) in waypoints)
        {
            int wpStep = Math.Max(prevStep + 1, Math.Min((int)(fraction * steps), steps - 2));
            if (wpStep > prevStep)
            {
                segments.Add((prevPrice, wpPrice, prevStep, wpStep));
                prevPrice = wpPrice;
                prevStep = wpStep;
            }
        }
        // Final segment to close
        if (prevStep < steps - 1)
        {
            segments.Add((prevPrice, close, prevStep, steps - 1));
        }

        // Generate ticks for each segment with momentum clustering
        foreach (var (segStart, segEnd, sStart, sEnd) in segments)
        {
            int segSteps = sEnd - sStart;
            if (segSteps <= 0) continue;

            var segRange = segEnd - segStart;
            var current = segStart;

            // Determine momentum profile for this segment
            // Trending candle segments get bursts; choppy segments get oscillation
            bool segIsMomentum = isTrending && Math.Abs(segRange) > range * 0.3m;

            for (int s = 1; s <= segSteps; s++)
            {
                int remaining = segSteps - s;
                decimal drift;
                decimal noise;

                if (segIsMomentum)
                {
                    // Momentum burst: stronger drift with variable intensity
                    // Creates steep slopes that StreamingSlope can detect
                    var intensity = 0.5 + 1.0 * Math.Sin(Math.PI * (double)s / segSteps); // Peak in middle
                    drift = segRange / segSteps * (decimal)intensity * 1.5m;

                    // Smaller noise during momentum (coherent price movement)
                    var sigma = range * 0.01m;
                    noise = sigma * (decimal)NextGaussian(rng) * 0.3m;
                }
                else
                {
                    // Choppy: guided walk with larger noise
                    drift = remaining > 0 ? (segEnd - current) / remaining : 0;

                    // Larger noise for oscillation
                    var sigma = range * 0.02m;
                    var dampening = remaining > 0 ? (decimal)Math.Sqrt((double)remaining / segSteps) : 0.1m;
                    noise = sigma * (decimal)NextGaussian(rng) * dampening;
                }

                current += drift + noise;

                // Clamp to candle's OHLC range
                current = Math.Max(low, Math.Min(high, current));

                var time = bar.TimestampUtc.AddSeconds(sStart + s);
                var vol = DistributeVolume(barVolume, sStart + s, steps, rng);
                result.Add(new CsvTick(time, symbol, Math.Round(current, 4), vol, "Interpolated"));
            }
        }
    }

    /// <summary>
    /// Legacy Brownian bridge interpolation for Close-only data (no OHLC available).
    /// Preserved for backward compatibility with recorded tick data or old CSV files.
    /// </summary>
    private static void GenerateBrownianBridgeTicks(Random rng, List<CsvTick> result,
        CsvTick bar, CsvTick nextBar, int steps)
    {
        var p0 = bar.Price;
        var p1 = nextBar.Price;

        // Noise scale: ~0.5 basis point per step, proportional to price level.
        var sigma = p0 * 0.00005m;

        var current = p0;
        for (int s = 1; s < steps; s++)
        {
            int remaining = steps - s;
            var drift = (p1 - current) / remaining;
            var dampening = (decimal)Math.Sqrt((double)remaining / steps);
            var noise = sigma * (decimal)NextGaussian(rng) * dampening;

            current += drift + noise;

            if (current <= 0) current = p0 * 0.001m;

            var time = bar.TimestampUtc.AddSeconds(s);
            result.Add(new CsvTick(time, bar.Symbol, Math.Round(current, 4), 0, "Interpolated"));
        }
    }

    /// <summary>
    /// Distributes a bar's total volume across synthetic ticks with front-loading bias.
    /// Real markets have higher volume at the start of each minute (market orders clustering).
    /// Returns 0 if the bar has no volume data.
    /// </summary>
    private static long DistributeVolume(long totalVolume, int currentStep, int totalSteps, Random rng)
    {
        if (totalVolume <= 0 || totalSteps <= 0) return 0;

        // Exponential decay: front-load volume
        double position = (double)currentStep / totalSteps;
        double weight = Math.Exp(-2.0 * position); // Decays from 1.0 to ~0.13

        // Add noise (±50% of weight)
        weight *= (0.5 + rng.NextDouble());

        // Scale to produce roughly correct total volume
        // Average weight ≈ 0.5, so scale = totalVolume / (totalSteps * 0.5)
        var vol = (long)(weight * totalVolume / (totalSteps * 0.4));
        return Math.Max(0, vol);
    }

    /// <summary>
    /// Box-Muller transform: generates a standard Normal random variate from a uniform RNG.
    /// </summary>
    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(); // (0, 1] — avoid log(0)
        double u2 = rng.NextDouble();        // [0, 1)
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Deterministic hash for a symbol string (not dependent on runtime GetHashCode).
    /// </summary>
    private static int StableHash(string s)
    {
        int hash = 17;
        foreach (char c in s)
            hash = hash * 31 + c;
        return hash;
    }

    private List<CsvTick> LoadCsvFile(string filePath, string expectedSymbol)
    {
        var ticks = new List<CsvTick>();

        foreach (var line in File.ReadLines(filePath).Skip(1)) // Skip header
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(',');
            if (parts.Length < 5) continue;

            try
            {
                var timestamp = DateTime.Parse(parts[0].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                var symbol = parts[1].Trim();
                var price = decimal.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
                long.TryParse(parts[3].Trim(), out var volume);
                var source = parts[4].Trim();

                // Parse optional OHLC columns (columns 5-7, present in historical bar data)
                decimal? open = null, high = null, low = null;
                if (parts.Length >= 8)
                {
                    decimal.TryParse(parts[5].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var o);
                    decimal.TryParse(parts[6].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var h);
                    decimal.TryParse(parts[7].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l);
                    if (o > 0) open = o;
                    if (h > 0) high = h;
                    if (l > 0) low = l;
                }

                // Use the symbol from the subscription, not the CSV (handles case mismatches)
                ticks.Add(new CsvTick(timestamp, expectedSymbol, price, volume, source, open, high, low));
            }
            catch (FormatException)
            {
                // Skip malformed lines
            }
        }

        return ticks;
    }
}
