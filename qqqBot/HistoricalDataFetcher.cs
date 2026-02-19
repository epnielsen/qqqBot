using System.Globalization;
using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// "The Time Machine" — downloads historical market data and saves it as standard CSV.
/// 
/// Supports two data sources (tried in order):
///   1. Alpaca Historical Bars (IMarketDataSource) — 1-min OHLCV, "what the bot saw"
///   2. FMP Historical Candles (IMarketDataAdapter) — 1-min OHLCV, clean third-party data
/// 
/// Output format matches MarketDataRecorder's live output:
///   TimestampUTC,Symbol,Price,Volume,Source
///   2026-02-06T14:30:00.0000000Z,QQQ,608.50,12345,AlpacaBar
/// 
/// For 1-min bars, we emit the Close price as the "tick" — this gives one data point
/// per minute, which is sufficient for signal replay. For higher fidelity, use live
/// recorded data from MarketDataRecorder.
/// 
/// Usage: dotnet run -- --fetch-history --date=2026-02-06 --symbols=QQQ,TQQQ,SQQQ
/// </summary>
public class HistoricalDataFetcher
{
    private readonly IMarketDataSource? _alpacaSource;
    private readonly IMarketDataAdapter? _fmpAdapter;
    private readonly ILogger _logger;
    private readonly string _dataDirectory;

    public HistoricalDataFetcher(
        IMarketDataSource? alpacaSource,
        IMarketDataAdapter? fmpAdapter,
        ILogger logger,
        string? dataDirectory = null)
    {
        _alpacaSource = alpacaSource;
        _fmpAdapter = fmpAdapter;
        _logger = logger;
        _dataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(_dataDirectory);
    }

    /// <summary>
    /// Downloads historical data for the given symbols and date, saving to CSV.
    /// Returns the list of file paths created.
    /// </summary>
    public async Task<List<string>> FetchAsync(
        DateOnly date,
        IReadOnlyList<string> symbols,
        CancellationToken ct = default)
    {
        var files = new List<string>();
        var dateStr = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var from = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var to = date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc);

        _logger.LogInformation("[FETCH] Downloading historical data for {Date}: {Symbols}",
            dateStr, string.Join(", ", symbols));

        foreach (var symbol in symbols)
        {
            try
            {
                var bars = await FetchBarsForSymbolAsync(symbol, from, to, ct);
                if (bars == null || bars.Count == 0)
                {
                    _logger.LogWarning("[FETCH] No data returned for {Symbol} on {Date}", symbol, dateStr);
                    continue;
                }

                var filePath = SaveBarsToCsv(symbol, dateStr, bars);
                files.Add(filePath);
                _logger.LogInformation("[FETCH] Saved {Count} bars for {Symbol} -> {File}",
                    bars.Count, symbol, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FETCH] Failed to fetch {Symbol} for {Date}", symbol, dateStr);
            }
        }

        return files;
    }

    private async Task<IReadOnlyList<Ohlcv>?> FetchBarsForSymbolAsync(
        string symbol, DateTime from, DateTime to, CancellationToken ct)
    {
        // Strategy 1: Try Alpaca (broker-aligned data — "what the bot saw")
        if (_alpacaSource != null)
        {
            try
            {
                _logger.LogInformation("[FETCH] Trying Alpaca for {Symbol}...", symbol);
                var bars = await _alpacaSource.GetHistoricalBarsAsync(
                    symbol, from, to, CandleResolution.OneMinute, 
                    includeExtendedHours: false, ct);
                
                if (bars.Count > 0)
                {
                    _logger.LogInformation("[FETCH] Alpaca returned {Count} bars for {Symbol}", bars.Count, symbol);
                    return bars;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FETCH] Alpaca failed for {Symbol}, falling back to FMP", symbol);
            }
        }

        // Strategy 2: FMP fallback (clean third-party data)
        if (_fmpAdapter != null)
        {
            try
            {
                _logger.LogInformation("[FETCH] Trying FMP for {Symbol}...", symbol);
                // FMP GetCandlesAsync takes count; we request a full day (~390 regular-hours minutes)
                var bars = await _fmpAdapter.GetCandlesAsync(symbol, CandleResolution.OneMinute, 500, ct);
                
                if (bars.Count > 0)
                {
                    // Filter to the requested date
                    var filtered = bars
                        .Where(b => b.Timestamp >= from && b.Timestamp <= to)
                        .OrderBy(b => b.Timestamp)
                        .ToList();

                    _logger.LogInformation("[FETCH] FMP returned {Count} bars for {Symbol} (filtered to {Date})",
                        filtered.Count, symbol, from.ToString("yyyy-MM-dd"));
                    return filtered;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FETCH] FMP also failed for {Symbol}", symbol);
            }
        }

        return null;
    }

    private string SaveBarsToCsv(string symbol, string dateStr, IReadOnlyList<Ohlcv> bars)
    {
        var sanitized = symbol.Replace("/", "-").Replace("\\", "-");
        var filePath = Path.Combine(_dataDirectory, $"{dateStr}_market_data_{sanitized}.csv");

        using var writer = new StreamWriter(filePath, append: false);
        writer.WriteLine("TimestampUTC,Symbol,Price,Volume,Source,Open,High,Low");

        string source = _alpacaSource != null ? "AlpacaBar" : "FmpBar";
        foreach (var bar in bars.OrderBy(b => b.Timestamp))
        {
            var ts = bar.Timestamp.ToString("O", CultureInfo.InvariantCulture);
            writer.WriteLine($"{ts},{symbol},{bar.Close},{bar.Volume},{source},{bar.Open},{bar.High},{bar.Low}");
        }

        return filePath;
    }
}
