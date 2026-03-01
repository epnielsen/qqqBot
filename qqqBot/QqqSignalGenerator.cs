using System.Threading.Channels;
using MarketBlocks.Bots.Domain;
using MarketBlocks.Bots.Interfaces;
using MarketBlocks.Bots.Services;

namespace qqqBot;

/// <summary>
/// Adapts <see cref="AnalystEngine"/> to the <see cref="ISignalGenerator{TSignal}"/> interface.
/// 
/// <para><b>Lifetime contract</b>: <see cref="StartAsync"/> returns a Task that represents
/// the full execution lifetime of the underlying <see cref="AnalystEngine"/>
/// (it awaits <c>ExecuteTask</c>). This satisfies <see cref="PipelineHost{TSignal}"/>'s
/// requirement that generator/dispatcher tasks represent their full running lifetime.</para>
/// </summary>
internal sealed class QqqSignalGenerator : ISignalGenerator<MarketRegime>
{
    private readonly AnalystEngine _analyst;

    public QqqSignalGenerator(AnalystEngine analyst)
    {
        _analyst = analyst ?? throw new ArgumentNullException(nameof(analyst));
    }

    /// <inheritdoc />
    public ChannelReader<MarketRegime> SignalChannel => _analyst.RegimeChannel;

    /// <inheritdoc />
    /// <remarks>
    /// Starts the underlying <see cref="AnalystEngine"/> (which extends <c>BackgroundService</c>)
    /// and then awaits its <c>ExecuteTask</c> so this Task represents the full production lifetime.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _analyst.StartAsync(cancellationToken);
        await (_analyst.ExecuteTask ?? Task.CompletedTask);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
        => _analyst.StopAsync(cancellationToken);
}
