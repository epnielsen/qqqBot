using System.Globalization;
using System.Threading.Channels;
using MarketBlocks.Bots.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// "Black Box Recorder" — captures every live PriceTick to a daily CSV file.
/// 
/// Runs as a hosted background service. Taps into the AnalystEngine's internal
/// price channel via a separate Channel bridge so it never blocks the trading thread.
/// 
/// Output format (standard for all data tools):
///   TimestampUTC,Symbol,Price,Volume,Source
///   2026-02-06T14:30:01.1230000Z,QQQ,608.50,100,AlpacaTrade
/// 
/// Files are saved to: {BaseDirectory}/data/{YYYYMMDD}_market_data_{Symbol}.csv
/// </summary>
public sealed class MarketDataRecorder : BackgroundService
{
    private readonly ILogger<MarketDataRecorder> _logger;
    private readonly Channel<PriceTick> _buffer;
    private readonly string _dataDirectory;
    private readonly Dictionary<string, StreamWriter> _writers = new();
    private readonly object _writerLock = new();
    private string _currentDate = string.Empty;

    /// <summary>
    /// The ChannelWriter that external producers (AnalystEngine tick interception) write to.
    /// </summary>
    public ChannelWriter<PriceTick> Writer => _buffer.Writer;

    public MarketDataRecorder(ILogger<MarketDataRecorder> logger, string? dataDirectory = null)
    {
        _logger = logger;
        _dataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDirectory);

        // Unbounded channel: we never want to block the trading thread
        _buffer = Channel.CreateUnbounded<PriceTick>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[RECORDER] Market Data Recorder started. Output: {Dir}", _dataDirectory);

        try
        {
            await foreach (var tick in _buffer.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    WriteTick(tick);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[RECORDER] Failed to write tick for {Symbol}", tick.Symbol);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            FlushAndCloseAll();
            _logger.LogInformation("[RECORDER] Market Data Recorder stopped. Files flushed.");
        }
    }

    private void WriteTick(PriceTick tick)
    {
        var dateKey = tick.TimestampUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var symbolKey = SanitizeSymbol(tick.Symbol);
        var writerKey = $"{dateKey}_{symbolKey}";

        lock (_writerLock)
        {
            // Day rollover: close old writers if date changed
            if (_currentDate != dateKey)
            {
                FlushAndCloseAll();
                _currentDate = dateKey;
            }

            if (!_writers.TryGetValue(writerKey, out var writer))
            {
                var filePath = Path.Combine(_dataDirectory, $"{dateKey}_market_data_{symbolKey}.csv");
                bool isNew = !File.Exists(filePath);
                var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
                writer = new StreamWriter(stream) { AutoFlush = false };

                if (isNew)
                {
                    writer.WriteLine("TimestampUTC,Symbol,Price,Volume,Source");
                }

                _writers[writerKey] = writer;
                _logger.LogInformation("[RECORDER] Opened CSV: {File}", filePath);
            }

            // Write the tick row
            var timestamp = tick.TimestampUtc.ToString("O", CultureInfo.InvariantCulture);
            writer.WriteLine($"{timestamp},{tick.Symbol},{tick.Price},{0},AlpacaTrade");

            // Periodic flush (every write in append mode is cheap; true flush on close)
        }
    }

    /// <summary>
    /// Flush periodically from outside (e.g., on a timer or from orchestrator).
    /// </summary>
    public void FlushAll()
    {
        lock (_writerLock)
        {
            foreach (var writer in _writers.Values)
            {
                try { writer.Flush(); } catch { /* best effort */ }
            }
        }
    }

    private void FlushAndCloseAll()
    {
        lock (_writerLock)
        {
            foreach (var kvp in _writers)
            {
                try
                {
                    kvp.Value.Flush();
                    kvp.Value.Close();
                    kvp.Value.Dispose();
                }
                catch { /* best effort */ }
            }
            _writers.Clear();
        }
    }

    private static string SanitizeSymbol(string symbol)
        => symbol.Replace("/", "-").Replace("\\", "-");
}
