using MarketBlocks.Bots.Domain;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Non-generic backward-compatible alias for <see cref="TradingStateManager{TState}"/>
/// specialized to <see cref="TradingState"/>. Existing code that uses
/// <c>new TradingStateManager(...)</c> continues to work unchanged.
/// </summary>
public class TradingStateManager : TradingStateManager<TradingState>
{
    public TradingStateManager(string stateFilePath, int flushIntervalSeconds = 5, ILogger? logger = null)
        : base(stateFilePath, flushIntervalSeconds, logger)
    {
    }
}
