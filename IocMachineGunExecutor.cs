using System;
using System.Threading.Tasks;
using Alpaca.Markets;

namespace qqqBot;

/// <summary>
/// Interface for IOC order execution to enable testing with mocks.
/// </summary>
public interface IIocExecutor
{
    /// <summary>
    /// Execute IOC orders with machine-gun retry logic.
    /// </summary>
    Task<IocExecutionResult> ExecuteAsync(
        string symbol,
        long targetQty,
        OrderSide side,
        decimal startPrice,
        decimal priceStepCents,
        int maxRetries,
        decimal maxDeviationPercent);
}

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
/// Mock-friendly interface for trading client operations needed by IOC executor.
/// </summary>
public interface IOrderClient
{
    Task<IOrder> PostOrderAsync(NewOrderRequest request);
    Task<IOrder> GetOrderAsync(Guid orderId);
}

/// <summary>
/// Wrapper around real Alpaca trading client for production use.
/// </summary>
public class AlpacaOrderClient : IOrderClient
{
    private readonly IAlpacaTradingClient _client;
    
    public AlpacaOrderClient(IAlpacaTradingClient client)
    {
        _client = client;
    }
    
    public Task<IOrder> PostOrderAsync(NewOrderRequest request) => _client.PostOrderAsync(request);
    public Task<IOrder> GetOrderAsync(Guid orderId) => _client.GetOrderAsync(orderId);
}

/// <summary>
/// IOC Machine Gun executor - testable implementation.
/// </summary>
public class IocMachineGunExecutor : IIocExecutor
{
    private readonly IOrderClient _orderClient;
    private readonly Func<string> _clientOrderIdGenerator;
    private readonly Action<string>? _logger;
    
    public IocMachineGunExecutor(
        IOrderClient orderClient, 
        Func<string> clientOrderIdGenerator,
        Action<string>? logger = null)
    {
        _orderClient = orderClient;
        _clientOrderIdGenerator = clientOrderIdGenerator;
        _logger = logger;
    }
    
    public async Task<IocExecutionResult> ExecuteAsync(
        string symbol,
        long targetQty,
        OrderSide side,
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
            
            // Check price deviation limit BEFORE submitting order
            var deviation = Math.Abs((currentPrice - originalPrice) / originalPrice);
            if (deviation > maxDeviationPercent)
            {
                _logger?.Invoke($"[IOC] Price deviation {deviation:P2} exceeds limit {maxDeviationPercent:P2}. Stopping retries.");
                result.AbortedDueToDeviation = true;
                break;
            }
            
            // Submit IOC order
            var limitPrice = Math.Round(currentPrice, 2);
            var orderRequest = new NewOrderRequest(
                symbol,
                OrderQuantity.FromInt64(remainingQty),
                side,
                OrderType.Limit,
                TimeInForce.Ioc
            )
            {
                ClientOrderId = _clientOrderIdGenerator(),
                LimitPrice = limitPrice
            };
            
            try
            {
                var order = await _orderClient.PostOrderAsync(orderRequest);
                
                // OPTIMIZATION: Check PostOrderAsync response FIRST before GetOrderAsync
                IOrder filledOrder;
                
                if (order.OrderStatus == OrderStatus.Filled || 
                    order.OrderStatus == OrderStatus.Canceled ||
                    order.OrderStatus == OrderStatus.Expired)
                {
                    filledOrder = order;
                }
                else
                {
                    filledOrder = await _orderClient.GetOrderAsync(order.OrderId);
                }
                
                if (filledOrder.OrderStatus == OrderStatus.Filled || 
                    (filledOrder.FilledQuantity > 0 && filledOrder.AverageFillPrice.HasValue))
                {
                    var filledQty = (long)filledOrder.FilledQuantity;
                    var avgPrice = filledOrder.AverageFillPrice ?? limitPrice;
                    totalFilled += filledQty;
                    totalProceeds += filledQty * avgPrice;
                    
                    if (filledOrder.OrderStatus == OrderStatus.Filled)
                    {
                        _logger?.Invoke($"[IOC] Attempt {attempt + 1}: FILLED {filledQty} @ ${avgPrice:N4}");
                        break;
                    }
                    else
                    {
                        // Partial fill - need to bump price for next attempt
                        _logger?.Invoke($"[IOC] Attempt {attempt + 1}: Partial {filledQty}/{remainingQty} @ ${avgPrice:N4}");
                        if (side == OrderSide.Buy)
                        {
                            currentPrice += (priceStepCents / 100m);
                        }
                        else
                        {
                            currentPrice -= (priceStepCents / 100m);
                        }
                    }
                }
                else if (filledOrder.OrderStatus == OrderStatus.Canceled ||
                         filledOrder.OrderStatus == OrderStatus.Expired)
                {
                    // No fill - adjust price and retry immediately
                    if (side == OrderSide.Buy)
                    {
                        currentPrice += (priceStepCents / 100m);
                    }
                    else
                    {
                        currentPrice -= (priceStepCents / 100m);
                    }
                    _logger?.Invoke($"[IOC] Attempt {attempt + 1}: Cancelled. Retrying at ${currentPrice:N2}...");
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
