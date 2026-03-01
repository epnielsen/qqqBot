using Microsoft.Extensions.Configuration;

namespace qqqBot;

/// <summary>
/// qqqBot-specific extension of <see cref="MarketBlocks.Bots.Domain.ReplayRunSpec"/>
/// that adds broker configuration needed by <see cref="SimulatedBroker"/>.
/// 
/// <para>The framework's <c>ReplayRunSpec</c> is strategy-agnostic.
/// This subclass carries the <see cref="IConfiguration"/> section
/// for SimulatedBroker parameters (slippage, spread, auction mode, etc.).</para>
/// </summary>
internal sealed record QqqReplayRunSpec : MarketBlocks.Bots.Domain.ReplayRunSpec
{
    /// <summary>
    /// SimulatedBroker configuration loaded from the config file.
    /// Contains slippage, spread, auction mode, and volatility-slippage parameters.
    /// </summary>
    public IConfiguration? BrokerConfig { get; init; }
}
