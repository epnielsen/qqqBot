using System.Threading.Channels;
using MarketBlocks.Bots.Services;

namespace qqqBot;

/// <summary>
/// Decorator that wraps an IAnalystMarketDataSource and tees every PriceTick
/// to the MarketDataRecorder's buffer for live CSV recording.
/// 
/// The inner data source writes ticks to the AnalystEngine's channel as normal.
/// This wrapper intercepts those writes and also forwards them to the recorder.
/// </summary>
public sealed class RecordingAnalystDataSource : IAnalystMarketDataSource
{
    private readonly IAnalystMarketDataSource _inner;
    private readonly MarketDataRecorder _recorder;

    public RecordingAnalystDataSource(IAnalystMarketDataSource inner, MarketDataRecorder recorder)
    {
        _inner = inner;
        _recorder = recorder;
    }

    public Task ConnectAsync(CancellationToken ct) => _inner.ConnectAsync(ct);
    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public Task SubscribeAsync(
        IEnumerable<AnalystSubscription> subscriptions,
        ChannelWriter<PriceTick> writer,
        CancellationToken ct)
    {
        // Create a tee: a channel that forwards ticks to both the original writer
        // AND the recorder's buffer
        var teeChannel = Channel.CreateUnbounded<PriceTick>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        // Background task: read from tee, write to both destinations
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var tick in teeChannel.Reader.ReadAllAsync(ct))
                {
                    // Forward to the real analyst channel
                    await writer.WriteAsync(tick, ct);
                    
                    // Also send to the recorder (fire-and-forget, non-blocking)
                    _recorder.Writer.TryWrite(tick);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // When the inner source completes, flush the recorder
                _recorder.FlushAll();
            }
        }, ct);

        // Subscribe the inner source to write into our tee channel
        return _inner.SubscribeAsync(subscriptions, teeChannel.Writer, ct);
    }
}
