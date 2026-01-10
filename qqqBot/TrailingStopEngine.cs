using System;

namespace qqqBot;

/// <summary>
/// Encapsulates trailing stop logic for testability.
/// Manages high/low water marks, stop triggers, and washout latch.
/// </summary>
public class TrailingStopEngine
{
    // Configuration
    public decimal TrailingStopPercent { get; set; }
    public int StopLossCooldownSeconds { get; set; }
    public string BullSymbol { get; set; } = "TQQQ";
    public string? BearSymbol { get; set; } = "SQQQ";
    
    // State
    public decimal HighWaterMark { get; set; }
    public decimal LowWaterMark { get; set; }
    public decimal VirtualStopPrice { get; set; }
    public bool IsStoppedOut { get; set; }
    public string StoppedOutDirection { get; set; } = string.Empty;
    public decimal WashoutLevel { get; set; }
    public DateTime? StopoutTime { get; set; }
    
    // Time provider for testing (injectable)
    public Func<DateTime> GetUtcNow { get; set; } = () => DateTime.UtcNow;
    
    /// <summary>
    /// Result of processing a price tick through the trailing stop engine.
    /// </summary>
    public class ProcessResult
    {
        public bool StopTriggered { get; set; }
        public bool LatchCleared { get; set; }
        public bool LatchBlocksEntry { get; set; }
        public string? ForcedSignal { get; set; } // "NEUTRAL" if stop triggered
    }
    
    /// <summary>
    /// Process a price tick and update trailing stop state.
    /// </summary>
    /// <param name="currentPrice">Current benchmark price</param>
    /// <param name="currentPosition">Current position symbol (TQQQ, SQQQ, or null)</param>
    /// <param name="currentShares">Number of shares held</param>
    /// <param name="upperBand">Upper hysteresis band (used for washout level)</param>
    /// <param name="lowerBand">Lower hysteresis band (used for washout level)</param>
    /// <returns>Result indicating any state changes</returns>
    public ProcessResult ProcessTick(
        decimal currentPrice,
        string? currentPosition,
        long currentShares,
        decimal upperBand,
        decimal lowerBand)
    {
        var result = new ProcessResult();
        
        if (TrailingStopPercent <= 0)
        {
            return result; // Trailing stop disabled
        }
        
        // Update water marks and check for stop trigger
        if (currentPosition == BullSymbol && currentShares > 0)
        {
            // BULL position - track highs, stop on drop
            if (currentPrice > HighWaterMark || HighWaterMark == 0m)
            {
                HighWaterMark = currentPrice;
                VirtualStopPrice = HighWaterMark * (1 - TrailingStopPercent);
            }
            
            // Check for stop trigger
            if (VirtualStopPrice > 0 && currentPrice <= VirtualStopPrice && !IsStoppedOut)
            {
                IsStoppedOut = true;
                StoppedOutDirection = "BULL";
                StopoutTime = GetUtcNow();
                WashoutLevel = upperBand;
                result.StopTriggered = true;
                result.ForcedSignal = "NEUTRAL";
            }
        }
        else if (!string.IsNullOrEmpty(BearSymbol) && currentPosition == BearSymbol && currentShares > 0)
        {
            // BEAR position - track lows, stop on rise
            if (currentPrice < LowWaterMark || LowWaterMark == 0m)
            {
                LowWaterMark = currentPrice;
                VirtualStopPrice = LowWaterMark * (1 + TrailingStopPercent);
            }
            
            // Check for stop trigger (price rising)
            if (VirtualStopPrice > 0 && currentPrice >= VirtualStopPrice && !IsStoppedOut)
            {
                IsStoppedOut = true;
                StoppedOutDirection = "BEAR";
                StopoutTime = GetUtcNow();
                WashoutLevel = lowerBand;
                result.StopTriggered = true;
                result.ForcedSignal = "NEUTRAL";
            }
        }
        
        // Check washout latch
        if (IsStoppedOut && StopoutTime.HasValue)
        {
            var elapsed = (GetUtcNow() - StopoutTime.Value).TotalSeconds;
            
            if (elapsed < StopLossCooldownSeconds)
            {
                // Still in cooldown period
                result.LatchBlocksEntry = true;
            }
            else if ((StoppedOutDirection == "BULL" && currentPrice > WashoutLevel) ||
                     (StoppedOutDirection == "BEAR" && currentPrice < WashoutLevel))
            {
                // Price recovered above/below washout level - clear latch
                ClearLatch();
                result.LatchCleared = true;
            }
            else
            {
                // Cooldown expired but price hasn't recovered
                result.LatchBlocksEntry = true;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Check if the current state should trigger an immediate stop loss.
    /// Used on startup to detect if restored state indicates stop-out condition.
    /// </summary>
    public bool ShouldTriggerImmediateStopLoss(decimal currentPrice, string? currentPosition, long currentShares)
    {
        if (TrailingStopPercent <= 0 || currentShares <= 0 || string.IsNullOrEmpty(currentPosition))
        {
            return false;
        }
        
        if (currentPosition == BullSymbol && HighWaterMark > 0)
        {
            // We have a high water mark - check if current price is below stop level
            var stopLevel = HighWaterMark * (1 - TrailingStopPercent);
            return currentPrice <= stopLevel;
        }
        else if (currentPosition == BearSymbol && LowWaterMark > 0)
        {
            // We have a low water mark - check if current price is above stop level
            var stopLevel = LowWaterMark * (1 + TrailingStopPercent);
            return currentPrice >= stopLevel;
        }
        
        return false;
    }
    
    /// <summary>
    /// Clear the washout latch and reset water marks.
    /// </summary>
    public void ClearLatch()
    {
        IsStoppedOut = false;
        HighWaterMark = 0m;
        LowWaterMark = 0m;
        VirtualStopPrice = 0m;
        StoppedOutDirection = string.Empty;
        WashoutLevel = 0m;
        StopoutTime = null;
    }
    
    /// <summary>
    /// Initialize state from a trading state object (for restart persistence).
    /// </summary>
    public void LoadFromState(TradingState state)
    {
        HighWaterMark = state.HighWaterMark ?? 0m;
        LowWaterMark = state.LowWaterMark ?? 0m;
        VirtualStopPrice = state.TrailingStopValue ?? 0m;
        IsStoppedOut = state.IsStoppedOut;
        StoppedOutDirection = state.StoppedOutDirection ?? string.Empty;
        WashoutLevel = state.WashoutLevel ?? 0m;
        StopoutTime = string.IsNullOrEmpty(state.StopoutTimestamp) 
            ? null 
            : DateTime.TryParse(state.StopoutTimestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : (DateTime?)null;
    }
    
    /// <summary>
    /// Save state to a trading state object.
    /// </summary>
    public void SaveToState(TradingState state)
    {
        state.HighWaterMark = HighWaterMark > 0 ? HighWaterMark : null;
        state.LowWaterMark = LowWaterMark > 0 ? LowWaterMark : null;
        state.TrailingStopValue = VirtualStopPrice > 0 ? VirtualStopPrice : null;
        state.IsStoppedOut = IsStoppedOut;
        state.StoppedOutDirection = IsStoppedOut ? StoppedOutDirection : null;
        state.WashoutLevel = IsStoppedOut ? WashoutLevel : null;
        state.StopoutTimestamp = StopoutTime?.ToString("o");
    }
}
