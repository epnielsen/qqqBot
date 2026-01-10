using System.Text.Json;
using System.Threading.Channels;
using MarketBlocks.Core.Domain;

namespace MarketBlocks.Infrastructure.Tradier;

/// <summary>
/// Parser for Tradier's newline-delimited JSON streaming format.
/// Handles trade events from the HTTP streaming endpoint.
/// </summary>
public sealed class TradierStreamParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses a Tradier stream and writes trade ticks to a channel.
    /// </summary>
    /// <param name="stream">The HTTP stream from Tradier.</param>
    /// <param name="writer">Channel writer to output trade ticks.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="isBenchmark">Whether these are benchmark symbol ticks.</param>
    public async Task ParseToChannelAsync(
        Stream stream, 
        ChannelWriter<TradeTick> writer, 
        CancellationToken cancellationToken,
        bool isBenchmark = true)
    {
        using var reader = new StreamReader(stream);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            
            // End of stream
            if (line == null)
                break;
            
            // Skip empty lines
            if (string.IsNullOrWhiteSpace(line))
                continue;
            
            try
            {
                var tick = ParseLine(line, isBenchmark);
                if (tick.HasValue)
                {
                    await writer.WriteAsync(tick.Value, cancellationToken);
                }
            }
            catch (JsonException)
            {
                // Skip malformed JSON lines
                continue;
            }
        }
    }
    
    /// <summary>
    /// Parses a single line from the stream.
    /// </summary>
    /// <param name="line">JSON line to parse.</param>
    /// <param name="isBenchmark">Whether this is a benchmark symbol.</param>
    /// <returns>TradeTick if this was a trade event, null otherwise.</returns>
    public TradeTick? ParseLine(string line, bool isBenchmark = true)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        
        // Check the event type
        if (!root.TryGetProperty("type", out var typeElement))
            return null;
        
        var eventType = typeElement.GetString();
        
        // Only process trade events, ignore heartbeats and other types
        if (eventType != "trade")
            return null;
        
        // Extract trade data
        var symbol = root.TryGetProperty("symbol", out var symbolElement) 
            ? symbolElement.GetString() 
            : null;
        
        if (!root.TryGetProperty("price", out var priceElement))
            return null;
        
        var price = priceElement.GetDecimal();
        
        // Parse timestamp (Unix epoch seconds)
        DateTime timestampUtc = DateTime.UtcNow;
        if (root.TryGetProperty("date", out var dateElement))
        {
            if (dateElement.ValueKind == JsonValueKind.String)
            {
                if (long.TryParse(dateElement.GetString(), out var epochSeconds))
                {
                    timestampUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
                }
            }
            else if (dateElement.ValueKind == JsonValueKind.Number)
            {
                var epochSeconds = dateElement.GetInt64();
                timestampUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            }
        }
        
        // Parse size if available
        long? size = null;
        if (root.TryGetProperty("size", out var sizeElement) || 
            root.TryGetProperty("volume", out sizeElement))
        {
            size = sizeElement.GetInt64();
        }
        
        return new TradeTick
        {
            Symbol = symbol,
            Price = price,
            TimestampUtc = timestampUtc,
            IsBenchmark = isBenchmark,
            Size = size
        };
    }
}
