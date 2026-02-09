using System.Globalization;
using System.Threading.Channels;
using MarketBlocks.Bots.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace qqqBot.Tests;

/// <summary>
/// Tests for the MarketDataRecorder — the "Black Box Recorder" that captures live ticks to CSV.
/// Uses temp directories so tests don't collide.
/// </summary>
public class MarketDataRecorderTests : IDisposable
{
    private readonly string _tempDir;

    public MarketDataRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"recorder_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// "First Blood" — writing one tick creates a CSV with the correct header and data row.
    /// </summary>
    [Fact]
    public async Task SingleTick_CreatesCsvWithHeaderAndRow()
    {
        var recorder = new MarketDataRecorder(new NullLogger<MarketDataRecorder>(), _tempDir);
        var cts = new CancellationTokenSource();

        // Start the background service
        var execTask = StartRecorderAsync(recorder, cts.Token);

        var timestamp = new DateTime(2026, 2, 6, 14, 30, 1, 123, DateTimeKind.Utc);
        var tick = new PriceTick("QQQ", 608.50m, false, timestamp);
        await recorder.Writer.WriteAsync(tick);

        // Give it a moment to process, then stop the recorder to release file handles
        await Task.Delay(100);
        cts.Cancel();
        try { await execTask; } catch (OperationCanceledException) { }

        // Find the file
        var expectedFile = Path.Combine(_tempDir, "20260206_market_data_QQQ.csv");
        Assert.True(File.Exists(expectedFile), $"Expected CSV file at {expectedFile}");

        var lines = await File.ReadAllLinesAsync(expectedFile);
        Assert.True(lines.Length >= 2, "CSV should have header + at least 1 data row");
        Assert.Equal("TimestampUTC,Symbol,Price,Volume,Source", lines[0]);
        Assert.Contains("QQQ", lines[1]);
        Assert.Contains("608.50", lines[1]);
        Assert.Contains("AlpacaTrade", lines[1]);
    }

    /// <summary>
    /// "Multi-Track Recording" — different symbols write to separate CSV files.
    /// </summary>
    [Fact]
    public async Task MultipleSymbols_CreateSeparateFiles()
    {
        var recorder = new MarketDataRecorder(new NullLogger<MarketDataRecorder>(), _tempDir);
        var cts = new CancellationTokenSource();
        var execTask = StartRecorderAsync(recorder, cts.Token);

        var timestamp = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        await recorder.Writer.WriteAsync(new PriceTick("QQQ", 500m, true, timestamp));
        await recorder.Writer.WriteAsync(new PriceTick("TQQQ", 60m, false, timestamp));
        await recorder.Writer.WriteAsync(new PriceTick("SQQQ", 15m, false, timestamp));

        await Task.Delay(100);
        cts.Cancel();
        try { await execTask; } catch (OperationCanceledException) { }

        Assert.True(File.Exists(Path.Combine(_tempDir, "20260206_market_data_QQQ.csv")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "20260206_market_data_TQQQ.csv")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "20260206_market_data_SQQQ.csv")));
    }

    /// <summary>
    /// "Symbol Sanitizer" — forward slashes in symbols (e.g., BTC/USD) are replaced with dashes.
    /// </summary>
    [Fact]
    public async Task SymbolWithSlash_IsSanitizedInFilename()
    {
        var recorder = new MarketDataRecorder(new NullLogger<MarketDataRecorder>(), _tempDir);
        var cts = new CancellationTokenSource();
        var execTask = StartRecorderAsync(recorder, cts.Token);

        var timestamp = new DateTime(2026, 2, 6, 10, 0, 0, DateTimeKind.Utc);
        await recorder.Writer.WriteAsync(new PriceTick("BTC/USD", 95000m, false, timestamp));

        await Task.Delay(100);
        cts.Cancel();
        try { await execTask; } catch (OperationCanceledException) { }

        // File should use BTC-USD, not BTC/USD
        var expectedFile = Path.Combine(_tempDir, "20260206_market_data_BTC-USD.csv");
        Assert.True(File.Exists(expectedFile), "Slash in symbol should be sanitized to dash");
    }

    /// <summary>
    /// "Continuous Recording" — multiple ticks for same symbol append to same file.
    /// </summary>
    [Fact]
    public async Task MultipleTicks_SameSymbol_AppendToSameFile()
    {
        var recorder = new MarketDataRecorder(new NullLogger<MarketDataRecorder>(), _tempDir);
        var cts = new CancellationTokenSource();
        var execTask = StartRecorderAsync(recorder, cts.Token);

        var baseTime = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
        {
            await recorder.Writer.WriteAsync(
                new PriceTick("QQQ", 500m + i, false, baseTime.AddSeconds(i)));
        }

        await Task.Delay(200);
        cts.Cancel();
        try { await execTask; } catch (OperationCanceledException) { }

        var file = Path.Combine(_tempDir, "20260206_market_data_QQQ.csv");
        var lines = await File.ReadAllLinesAsync(file);

        // Header + 10 data rows
        Assert.Equal(11, lines.Length);
    }

    /// <summary>
    /// "Day Rollover" — ticks on different dates create separate files.
    /// </summary>
    [Fact]
    public async Task DifferentDates_CreateSeparateFiles()
    {
        var recorder = new MarketDataRecorder(new NullLogger<MarketDataRecorder>(), _tempDir);
        var cts = new CancellationTokenSource();
        var execTask = StartRecorderAsync(recorder, cts.Token);

        var day1 = new DateTime(2026, 2, 6, 14, 30, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 2, 7, 14, 30, 0, DateTimeKind.Utc);

        await recorder.Writer.WriteAsync(new PriceTick("QQQ", 500m, false, day1));
        await Task.Delay(50);
        await recorder.Writer.WriteAsync(new PriceTick("QQQ", 510m, false, day2));

        await Task.Delay(200);
        cts.Cancel();
        try { await execTask; } catch (OperationCanceledException) { }

        Assert.True(File.Exists(Path.Combine(_tempDir, "20260206_market_data_QQQ.csv")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "20260207_market_data_QQQ.csv")));
    }

    /// <summary>
    /// "CSV Format Compliance" — verify the timestamp uses round-trip format ("O").
    /// </summary>
    [Fact]
    public async Task CsvTimestamp_UsesRoundTripFormat()
    {
        var recorder = new MarketDataRecorder(new NullLogger<MarketDataRecorder>(), _tempDir);
        var cts = new CancellationTokenSource();
        var execTask = StartRecorderAsync(recorder, cts.Token);

        var timestamp = new DateTime(2026, 2, 6, 14, 30, 1, 123, DateTimeKind.Utc);
        await recorder.Writer.WriteAsync(new PriceTick("QQQ", 500m, false, timestamp));

        // Give it a moment to process, then stop recorder to release file handles
        await Task.Delay(100);
        cts.Cancel();
        try { await execTask; } catch (OperationCanceledException) { }

        var file = Path.Combine(_tempDir, "20260206_market_data_QQQ.csv");
        var lines = await File.ReadAllLinesAsync(file);
        var dataRow = lines[1];

        // Round-trip format should contain "2026-02-06T14:30:01"
        Assert.Contains("2026-02-06T14:30:01", dataRow);

        // Verify it can be round-tripped back
        var parts = dataRow.Split(',');
        var parsed = DateTime.Parse(parts[0], CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.Equal(timestamp, parsed);
    }

    /// <summary>
    /// Helper: starts the recorder's background loop via ExecuteAsync.
    /// Uses reflection since ExecuteAsync is protected.
    /// </summary>
    private static Task StartRecorderAsync(MarketDataRecorder recorder, CancellationToken ct)
    {
        var method = typeof(MarketDataRecorder).GetMethod("ExecuteAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (Task)method!.Invoke(recorder, new object[] { ct })!;
    }
}
