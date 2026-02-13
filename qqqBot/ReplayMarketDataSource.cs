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
    private readonly Action<string, decimal>? _onPriceUpdate;
    private readonly Action<DateTime>? _onTimeAdvance;
    private readonly int _replaySeed;
    private bool _connected;

    /// <summary>
    /// Parsed tick from CSV, sortable by timestamp.
    /// </summary>
    private record CsvTick(DateTime TimestampUtc, string Symbol, decimal Price, long Volume, string Source);

    public ReplayMarketDataSource(
        ILogger logger,
        string dataDirectory,
        DateOnly replayDate,
        double speedMultiplier = 1.0,
        Action<string, decimal>? onPriceUpdate = null,
        Action<DateTime>? onTimeAdvance = null,
        int? replaySeed = null)
    {
        _logger = logger;
        _dataDirectory = dataDirectory;
        _replayDate = replayDate;
        _speedMultiplier = speedMultiplier;
        _onPriceUpdate = onPriceUpdate;
        _onTimeAdvance = onTimeAdvance;
        _replaySeed = replaySeed ?? _replayDate.DayNumber;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _connected = true;
        _logger.LogInformation("[REPLAY] ==========================================");
        _logger.LogInformation("[REPLAY]  R E P L A Y   M O D E");
        _logger.LogInformation("[REPLAY]  Date: {Date}", _replayDate);
        _logger.LogInformation("[REPLAY]  Speed: {Speed}x", _speedMultiplier);
        _logger.LogInformation("[REPLAY]  Seed:  {Seed}", _replaySeed);
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
            var symbolSeed = _replaySeed ^ StableHash(sub.Symbol);
            var ticks = InterpolateWithBrownianBridge(rawTicks, symbolSeed);
            allTicks.AddRange(ticks);
            _logger.LogInformation("[REPLAY] Loaded {Raw} bars -> {Count} ticks (Brownian bridge, seed={Seed}) from {File}",
                rawTicks.Count, ticks.Count, symbolSeed, filePath);
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
            _onPriceUpdate?.Invoke(tick.Symbol, tick.Price);

            var priceTick = new PriceTick(tick.Symbol, tick.Price, isBenchmark, tick.TimestampUtc);
            await writer.WriteAsync(priceTick, ct);
        }

        _logger.LogInformation("[REPLAY] ✓ Playback complete. {Count} ticks replayed.", allTicks.Count);
        
        // Signal end of data — the AnalystEngine loop will detect channel completion
        writer.Complete();
    }

    /// <summary>
    /// Interpolates between consecutive bars using a Brownian bridge (guided random walk).
    /// Each intermediate tick drifts toward the next bar's known price while adding
    /// realistic microstructure noise. The walk is seeded so replays are deterministic.
    /// Already-high-resolution data (sub-second gaps) passes through unchanged.
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
                    var p0 = ticks[i].Price;
                    var p1 = ticks[i + 1].Price;

                    // Noise scale: ~0.5 basis point per step, proportional to price level.
                    // Over 60 steps (1-min bar): std dev ≈ price × 0.00005 × √60 ≈ 0.04%
                    var sigma = p0 * 0.00005m;

                    var current = p0;
                    for (int s = 1; s < steps; s++)
                    {
                        int remaining = steps - s;
                        // Drift: steer toward the target price so we arrive on time
                        var drift = (p1 - current) / remaining;
                        // Noise: Gaussian perturbation, dampened near the endpoint
                        var dampening = (decimal)Math.Sqrt((double)remaining / steps);
                        var noise = sigma * (decimal)NextGaussian(rng) * dampening;

                        current += drift + noise;

                        // Safety: prevent negative or zero prices
                        if (current <= 0) current = p0 * 0.001m;

                        var time = ticks[i].TimestampUtc.AddSeconds(s);
                        result.Add(new CsvTick(time, ticks[i].Symbol, Math.Round(current, 4), 0, "Interpolated"));
                    }
                }
            }
        }

        return result;
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

                // Use the symbol from the subscription, not the CSV (handles case mismatches)
                ticks.Add(new CsvTick(timestamp, expectedSymbol, price, volume, source));
            }
            catch (FormatException)
            {
                // Skip malformed lines
            }
        }

        return ticks;
    }
}
