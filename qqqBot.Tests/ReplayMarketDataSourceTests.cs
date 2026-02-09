using System.Globalization;
using System.Threading.Channels;
using MarketBlocks.Bots.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace qqqBot.Tests;

/// <summary>
/// Tests for the ReplayMarketDataSource — "The Player" that replays CSV data
/// through the AnalystEngine as if it were live.
/// </summary>
public class ReplayMarketDataSourceTests : IDisposable
{
    private readonly string _tempDir;

    public ReplayMarketDataSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"replay_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Helper: creates a standard CSV file for the given symbol and date.
    /// </summary>
    private void WriteCsvFile(string symbol, string date, IEnumerable<(DateTime ts, decimal price)> ticks)
    {
        var sanitized = symbol.Replace("/", "-").Replace("\\", "-");
        var filePath = Path.Combine(_tempDir, $"{date}_market_data_{sanitized}.csv");
        using var writer = new StreamWriter(filePath);
        writer.WriteLine("TimestampUTC,Symbol,Price,Volume,Source");
        foreach (var (ts, price) in ticks)
        {
            writer.WriteLine($"{ts.ToString("O", CultureInfo.InvariantCulture)},{symbol},{price},100,TestData");
        }
    }

    /// <summary>
    /// "Basic Playback" — all ticks from the CSV appear in the channel in order.
    /// </summary>
    [Fact]
    public async Task BasicPlayback_AllTicksEmittedInOrder()
    {
        var baseTime = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        var ticks = Enumerable.Range(0, 5)
            .Select(i => (baseTime.AddSeconds(i), 500m + i))
            .ToList();
        WriteCsvFile("QQQ", "20260206", ticks);

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        var subs = new[] { new AnalystSubscription("QQQ", true) };

        await source.SubscribeAsync(subs, channel.Writer, CancellationToken.None);

        // Read all ticks (channel should be completed after replay)
        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        Assert.Equal(5, received.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal("QQQ", received[i].Symbol);
            Assert.Equal(500m + i, received[i].Price);
            Assert.True(received[i].IsBenchmark);
        }
    }

    /// <summary>
    /// "Channel Completion" — the writer is completed after all data is played.
    /// </summary>
    [Fact]
    public async Task PlaybackComplete_CompletesChannel()
    {
        var baseTime = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        WriteCsvFile("QQQ", "20260206", new[] { (baseTime, 500m) });

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        await source.SubscribeAsync(
            new[] { new AnalystSubscription("QQQ", false) },
            channel.Writer, CancellationToken.None);

        // Channel reader should terminate (Complete has been called)
        var allRead = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            allRead.Add(tick);

        Assert.Single(allRead);
        // If we get here without hanging, the channel was properly completed
    }

    /// <summary>
    /// "Multi-Symbol Merge" — ticks from multiple CSV files are interleaved by timestamp.
    /// </summary>
    [Fact]
    public async Task MultiSymbol_InterleavedByTimestamp()
    {
        var baseTime = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);

        // 1-second gaps within each symbol — below the 1.5s interpolation threshold
        WriteCsvFile("QQQ", "20260206", new[]
        {
            (baseTime.AddMilliseconds(0), 500m),
            (baseTime.AddMilliseconds(1000), 501m),
            (baseTime.AddMilliseconds(2000), 502m),
        });

        WriteCsvFile("TQQQ", "20260206", new[]
        {
            (baseTime.AddMilliseconds(500), 60m),
            (baseTime.AddMilliseconds(1500), 61m),
        });

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        var subs = new[]
        {
            new AnalystSubscription("QQQ", true),
            new AnalystSubscription("TQQQ", false),
        };

        await source.SubscribeAsync(subs, channel.Writer, CancellationToken.None);

        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        Assert.Equal(5, received.Count);

        // Should be interleaved: QQQ, TQQQ, QQQ, TQQQ, QQQ
        Assert.Equal("QQQ", received[0].Symbol);
        Assert.Equal("TQQQ", received[1].Symbol);
        Assert.Equal("QQQ", received[2].Symbol);
        Assert.Equal("TQQQ", received[3].Symbol);
        Assert.Equal("QQQ", received[4].Symbol);

        // Benchmark flag preserved
        Assert.True(received[0].IsBenchmark);
        Assert.False(received[1].IsBenchmark);
    }

    /// <summary>
    /// "Missing File Graceful" — missing CSV for a subscribed symbol doesn't crash,
    /// it just skips that symbol.
    /// </summary>
    [Fact]
    public async Task MissingCsvFile_SkipsSymbolGracefully()
    {
        var baseTime = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        WriteCsvFile("QQQ", "20260206", new[] { (baseTime, 500m) });
        // No file for TQQQ

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        var subs = new[]
        {
            new AnalystSubscription("QQQ", true),
            new AnalystSubscription("TQQQ", false), // no data file for this
        };

        await source.SubscribeAsync(subs, channel.Writer, CancellationToken.None);

        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        // Only QQQ ticks should appear
        Assert.Single(received);
        Assert.Equal("QQQ", received[0].Symbol);
    }

    /// <summary>
    /// "No Data At All" — if no CSVs exist at all, the channel completes immediately.
    /// </summary>
    [Fact]
    public async Task NoDataFiles_CompletesChannelImmediately()
    {
        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        await source.SubscribeAsync(
            new[] { new AnalystSubscription("QQQ", false) },
            channel.Writer, CancellationToken.None);

        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        Assert.Empty(received);
    }

    /// <summary>
    /// "Instant Replay" — speed=0 means no delays, should complete nearly instantly even with many ticks.
    /// </summary>
    [Fact]
    public async Task SpeedZero_CompletesWithoutDelay()
    {
        var baseTime = new DateTime(2026, 2, 6, 9, 30, 0, DateTimeKind.Utc);
        // 100 ticks, each 1 minute apart (real-time would be 100 minutes)
        var ticks = Enumerable.Range(0, 100)
            .Select(i => (baseTime.AddMinutes(i), 500m + i))
            .ToList();
        WriteCsvFile("QQQ", "20260206", ticks);

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await source.SubscribeAsync(
            new[] { new AnalystSubscription("QQQ", false) },
            channel.Writer, CancellationToken.None);
        sw.Stop();

        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        Assert.True(received.Count >= 100, $"Interpolation should expand 100 bars to many ticks, got {received.Count}");
        // Speed=0 should finish in well under 1 second
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Speed=0 took {sw.ElapsedMilliseconds}ms — should be near instant");
    }

    /// <summary>
    /// "ConnectAsync Required" — calling SubscribeAsync before ConnectAsync throws.
    /// </summary>
    [Fact]
    public async Task SubscribeBeforeConnect_Throws()
    {
        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        var channel = Channel.CreateUnbounded<PriceTick>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.SubscribeAsync(
                new[] { new AnalystSubscription("QQQ", false) },
                channel.Writer, CancellationToken.None));
    }

    /// <summary>
    /// "Malformed CSV Lines" — bad rows are skipped without crashing.
    /// </summary>
    [Fact]
    public async Task MalformedCsvLines_AreSkipped()
    {
        var filePath = Path.Combine(_tempDir, "20260206_market_data_QQQ.csv");
        using (var writer = new StreamWriter(filePath))
        {
            writer.WriteLine("TimestampUTC,Symbol,Price,Volume,Source");
            writer.WriteLine("2026-02-06T14:30:00.0000000Z,QQQ,500,100,Test"); // valid
            writer.WriteLine("bad_timestamp,QQQ,501,100,Test");                  // bad timestamp
            writer.WriteLine("2026-02-06T14:30:02.0000000Z,QQQ");               // too few columns
            writer.WriteLine("2026-02-06T14:30:01.0000000Z,QQQ,503,100,Test"); // valid (1s gap — no interpolation)
        }

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        await source.SubscribeAsync(
            new[] { new AnalystSubscription("QQQ", false) },
            channel.Writer, CancellationToken.None);

        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        // Only the 2 valid lines should come through
        Assert.Equal(2, received.Count);
        Assert.Equal(500m, received[0].Price);
        Assert.Equal(503m, received[1].Price);
    }

    /// <summary>
    /// "Timestamp Preservation" — original CSV timestamps are carried through to PriceTick.
    /// </summary>
    [Fact]
    public async Task Timestamps_PreservedFromCsv()
    {
        var t1 = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 2, 6, 14, 30, 1, DateTimeKind.Utc); // 1s gap — no interpolation
        WriteCsvFile("QQQ", "20260206", new[] { (t1, 500m), (t2, 501m) });

        var source = new ReplayMarketDataSource(
            NullLogger.Instance, _tempDir, new DateOnly(2026, 2, 6), speedMultiplier: 0);

        await source.ConnectAsync(CancellationToken.None);

        var channel = Channel.CreateUnbounded<PriceTick>();
        await source.SubscribeAsync(
            new[] { new AnalystSubscription("QQQ", false) },
            channel.Writer, CancellationToken.None);

        var received = new List<PriceTick>();
        await foreach (var tick in channel.Reader.ReadAllAsync())
            received.Add(tick);

        Assert.Equal(t1, received[0].TimestampUtc);
        Assert.Equal(t2, received[1].TimestampUtc);
    }
}
