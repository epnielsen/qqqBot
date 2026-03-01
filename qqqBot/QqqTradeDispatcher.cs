using System.Threading.Channels;
using MarketBlocks.Bots.Interfaces;

namespace qqqBot;

/// <summary>
/// Adapts <see cref="TraderEngine"/> to the <see cref="ITradeDispatcher{TSignal}"/> interface.
/// 
/// <para><b>Lifetime contract</b>: <see cref="StartAsync"/> returns a Task that represents
/// the full execution lifetime of the underlying <see cref="TraderEngine"/>
/// (it awaits <c>ExecuteTask</c>). This satisfies <see cref="PipelineHost{TSignal}"/>'s
/// requirement that generator/dispatcher tasks represent their full running lifetime.</para>
/// 
/// <para>Also exposes <see cref="RepairModeTriggered"/> for the live orchestrator to
/// check after pipeline startup.</para>
/// </summary>
internal sealed class QqqTradeDispatcher : ITradeDispatcher<MarketRegime>
{
    private readonly TraderEngine _trader;

    public QqqTradeDispatcher(TraderEngine trader)
    {
        _trader = trader ?? throw new ArgumentNullException(nameof(trader));
    }

    /// <summary>
    /// Whether the underlying <see cref="TraderEngine"/> detected a state mismatch
    /// during startup and entered repair mode. Checked by the live orchestrator
    /// after pipeline start.
    /// </summary>
    public bool RepairModeTriggered => _trader.RepairModeTriggered;

    /// <inheritdoc />
    /// <remarks>
    /// Passes the signal channel to the underlying <see cref="TraderEngine"/>
    /// (which expects <c>ChannelReader&lt;MarketRegime&gt;</c>), starts its
    /// <c>BackgroundService</c>, and awaits <c>ExecuteTask</c> so this Task
    /// represents the full consumption lifetime.
    /// </remarks>
    public async Task StartAsync(ChannelReader<MarketRegime> signalChannel, CancellationToken cancellationToken)
    {
        await _trader.StartAsync(signalChannel, cancellationToken);
        await (_trader.ExecuteTask ?? Task.CompletedTask);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
        => _trader.StopAsync(cancellationToken);
}
