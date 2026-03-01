using MarketBlocks.Trade.Components;

namespace qqqBot;

/// <summary>
/// Extension methods for TrailingStopEngine that use qqqBot-specific TradingState.
/// These were extracted from TrailingStopEngine when it moved to MarketBlocks.Trade.
/// </summary>
public static class TrailingStopEngineExtensions
{
    /// <summary>
    /// Initialize state from a trading state object (for restart persistence).
    /// </summary>
    public static void LoadFromState(this TrailingStopEngine engine, TradingState state)
    {
        engine.HighWaterMark = state.HighWaterMark ?? 0m;
        engine.LowWaterMark = state.LowWaterMark ?? 0m;
        engine.VirtualStopPrice = state.TrailingStopValue ?? 0m;
        engine.IsStoppedOut = state.IsStoppedOut;
        engine.StoppedOutDirection = state.StoppedOutDirection ?? string.Empty;
        engine.WashoutLevel = state.WashoutLevel ?? 0m;
        engine.StopoutTime = string.IsNullOrEmpty(state.StopoutTimestamp) 
            ? null 
            : DateTime.TryParse(state.StopoutTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : (DateTime?)null;
    }
    
    /// <summary>
    /// Save state to a trading state object.
    /// </summary>
    public static void SaveToState(this TrailingStopEngine engine, TradingState state)
    {
        state.HighWaterMark = engine.HighWaterMark > 0 ? engine.HighWaterMark : null;
        state.LowWaterMark = engine.LowWaterMark > 0 ? engine.LowWaterMark : null;
        state.TrailingStopValue = engine.VirtualStopPrice > 0 ? engine.VirtualStopPrice : null;
        state.IsStoppedOut = engine.IsStoppedOut;
        state.StoppedOutDirection = engine.IsStoppedOut ? engine.StoppedOutDirection : null;
        state.WashoutLevel = engine.IsStoppedOut ? engine.WashoutLevel : null;
        state.StopoutTimestamp = engine.StopoutTime?.ToString("o");
    }
}
