using System;
using System.Threading.Tasks;
using qqqBot.Core.Domain;
using qqqBot.Core.Interfaces;

namespace qqqBot;

/// <summary>
/// Result of IOC machine-gun execution.
/// </summary>
public class IocExecutionResult
{
    public long FilledQty { get; set; }
    public decimal AvgPrice { get; set; }
    public decimal TotalProceeds { get; set; }
    public int AttemptsUsed { get; set; }
    public bool AbortedDueToDeviation { get; set; }
    public decimal FinalPriceAttempted { get; set; }
}

/// <summary>
/// IOC (Immediate-Or-Cancel) Machine Gun executor.
/// Submits IOC limit orders with progressive price chasing until filled.
/// Now uses broker-agnostic IBrokerExecution interface.
/// </summary>
public class IocMachineGunExecutor
{
    private readonly IBrokerExecution _broker;
    private readonly Func<string> _clientOrderIdGenerator;
    private readonly Action<string>? _logger;
    
    public IocMachineGunExecutor(
        IBrokerExecution broker, 
        Func<string> clientOrderIdGenerator,
        Action<string>? logger = null)
    {
        _broker = broker;
        _clientOrderIdGenerator = clientOrderIdGenerator;
        _logger = logger;
    }
    
    /// <summary>
    /// Execute IOC orders with machine-gun retry logic.
    /// Progressively chases price until target quantity is filled or deviation limit is hit.
    /// </summary>
    /// <param name="symbol">Symbol to trade.</param>
    /// <param name="targetQty">Target quantity to fill.</param>
    /// <param name="side">Buy or Sell.</param>
    /// <param name="startPrice">Starting limit price.</param>
    /// <param name="priceStepCents">Price increment per retry (in cents).</param>
    /// <param name="maxRetries">Maximum number of order attempts.</param>
    /// <param name="maxDeviationPercent">Maximum price deviation from start (e.g., 0.005 = 0.5%).</param>
    /// <returns>Execution result with fill details.</returns>
    public async Task<IocExecutionResult> ExecuteAsync(
        string symbol,
        long targetQty,
        BotOrderSide side,
        decimal startPrice,
        decimal priceStepCents,
        int maxRetries,
        decimal maxDeviationPercent)
    {
        var result = new IocExecutionResult();
        long totalFilled = 0;
        decimal totalProceeds = 0m;
        decimal currentPrice = startPrice;
        var originalPrice = startPrice;
        
        for (int attempt = 0; attempt < maxRetries && totalFilled < targetQty; attempt++)
        {
            result.AttemptsUsed = attempt + 1;
            result.FinalPriceAttempted = currentPrice;
            
            var remainingQty = targetQty - totalFilled;
            
            // Check price deviation before submitting
            var deviation = Math.Abs((currentPrice - originalPrice) / originalPrice);
            if (deviation > maxDeviationPercent)
            {
                _logger?.Invoke($"[IOC] Price deviation {deviation:P2} exceeds limit {maxDeviationPercent:P2}. Stopping retries.");
                result.AbortedDueToDeviation = true;
                break;
            }
            
            // Submit IOC order using broker-agnostic interface
            var limitPrice = Math.Round(currentPrice, 2);
            var request = side == BotOrderSide.Buy 
                ? BotOrderRequest.IocLimitBuy(symbol, remainingQty, limitPrice, _clientOrderIdGenerator())
                : BotOrderRequest.IocLimitSell(symbol, remainingQty, limitPrice, _clientOrderIdGenerator());
            
            try
            {
                // The Adapter handles the optimization (returning filled state immediately if possible)
                var order = await _broker.SubmitOrderAsync(request);
                
                // Check for fills (Full or Partial)
                if (order.FilledQuantity > 0)
                {
                    var filledQty = order.FilledQuantity;
                    var avgPrice = order.AverageFillPrice ?? limitPrice;
                    totalFilled += filledQty;
                    totalProceeds += filledQty * avgPrice;
                    
                    if (order.Status == BotOrderStatus.Filled)
                    {
                        _logger?.Invoke($"[IOC] Attempt {attempt + 1}: FILLED {filledQty} @ ${avgPrice:N4}");
                        break; // Done - fully filled
                    }
                    else
                    {
                        // Partial fill (BotOrderStatus.PartiallyFilled or Canceled with partial fills)
                        _logger?.Invoke($"[IOC] Attempt {attempt + 1}: Partial {filledQty}/{remainingQty} @ ${avgPrice:N4}");
                        
                        // Chase price - increment for buys, decrement for sells
                        if (side == BotOrderSide.Buy)
                            currentPrice += (priceStepCents / 100m);
                        else
                            currentPrice -= (priceStepCents / 100m);
                    }
                }
                else
                {
                    // No fill - adjust price and retry
                    if (side == BotOrderSide.Buy)
                        currentPrice += (priceStepCents / 100m);
                    else
                        currentPrice -= (priceStepCents / 100m);
                    
                    _logger?.Invoke($"[IOC] Attempt {attempt + 1}: No Fill. Retrying at ${currentPrice:N2}...");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[IOC] Attempt {attempt + 1} error: {ex.Message}");
            }
        }
        
        result.FilledQty = totalFilled;
        result.AvgPrice = totalFilled > 0 ? (totalProceeds / totalFilled) : 0m;
        result.TotalProceeds = totalProceeds;
        
        return result;
    }
}
