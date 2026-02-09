using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// "The Fake Broker" — handles Buy/Sell orders during Replay without hitting Alpaca.
/// 
/// Tracks cash balance, positions, and fills orders at the current replay price
/// with optional slippage simulation. This ensures replay never sends real orders
/// to the exchange.
/// 
/// Also implements GetLatestPriceAsync by tracking the last seen price per symbol.
/// 
/// Usage: Injected by ProgramRefactored when --mode=replay is specified.
/// </summary>
public sealed class SimulatedBroker : IBrokerExecution
{
    private readonly ILogger _logger;
    private readonly decimal _initialCash;
    private readonly decimal _slippagePercent;
    private readonly object _lock = new();

    // Simulated state
    private decimal _cashBalance;
    private readonly Dictionary<string, SimulatedPosition> _positions = new();
    private readonly Dictionary<Guid, BotOrder> _orders = new();
    private readonly Dictionary<string, decimal> _latestPrices = new();

    // Track P/L
    private decimal _realizedPnL;
    private int _tradeCount;

    private record SimulatedPosition(string Symbol, long Quantity, decimal AverageEntryPrice);

    public SimulatedBroker(ILogger logger, decimal initialCash = 30_000m, decimal slippagePercent = 0.0001m)
    {
        _logger = logger;
        _initialCash = initialCash;
        _cashBalance = initialCash;
        _slippagePercent = slippagePercent;

        _logger.LogInformation("[SIM-BROKER] ==========================================");
        _logger.LogInformation("[SIM-BROKER]  S I M U L A T E D   B R O K E R");
        _logger.LogInformation("[SIM-BROKER]  Starting Cash: ${Cash:N2}", initialCash);
        _logger.LogInformation("[SIM-BROKER]  Slippage: {Slip:P3}", slippagePercent);
        _logger.LogInformation("[SIM-BROKER] ==========================================");
    }

    /// <summary>
    /// Update the latest known price for a symbol (called by the replay pipeline).
    /// </summary>
    public void UpdatePrice(string symbol, decimal price)
    {
        lock (_lock)
        {
            _latestPrices[symbol] = price;
        }
    }

    public Task<BotOrder> SubmitOrderAsync(BotOrderRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var orderId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            // Determine fill price with slippage
            decimal basePrice = request.LimitPrice ?? _latestPrices.GetValueOrDefault(request.Symbol, 0m);
            if (basePrice <= 0)
            {
                _logger.LogWarning("[SIM-BROKER] No price available for {Symbol}. Rejecting order.", request.Symbol);
                var rejected = new BotOrder
                {
                    OrderId = orderId,
                    ClientOrderId = request.ClientOrderId,
                    Symbol = request.Symbol,
                    Side = request.Side,
                    Type = request.Type,
                    Status = BotOrderStatus.Rejected,
                    Quantity = request.Quantity,
                    FilledQuantity = 0,
                    SubmittedAtUtc = now
                };
                _orders[orderId] = rejected;
                return Task.FromResult(rejected);
            }

            // Apply slippage: Buy slightly higher, Sell slightly lower
            decimal slippage = basePrice * _slippagePercent;
            decimal fillPrice = request.Side == BotOrderSide.Buy
                ? basePrice + slippage
                : basePrice - slippage;
            fillPrice = Math.Round(fillPrice, 2);

            // Execute the fill
            var fillValue = fillPrice * request.Quantity;

            if (request.Side == BotOrderSide.Buy)
            {
                if (fillValue > _cashBalance)
                {
                    _logger.LogWarning("[SIM-BROKER] Insufficient funds for {Qty} {Symbol} @ {Price:N2} (need ${Need:N2}, have ${Have:N2})",
                        request.Quantity, request.Symbol, fillPrice, fillValue, _cashBalance);

                    var rejectedOrder = new BotOrder
                    {
                        OrderId = orderId,
                        ClientOrderId = request.ClientOrderId,
                        Symbol = request.Symbol,
                        Side = request.Side,
                        Type = request.Type,
                        Status = BotOrderStatus.Rejected,
                        Quantity = request.Quantity,
                        FilledQuantity = 0,
                        SubmittedAtUtc = now
                    };
                    _orders[orderId] = rejectedOrder;
                    return Task.FromResult(rejectedOrder);
                }

                _cashBalance -= fillValue;

                // Update position
                if (_positions.TryGetValue(request.Symbol, out var existing))
                {
                    var totalCost = existing.AverageEntryPrice * existing.Quantity + fillPrice * request.Quantity;
                    var totalQty = existing.Quantity + request.Quantity;
                    var newAvg = totalQty > 0 ? totalCost / totalQty : 0;
                    _positions[request.Symbol] = new SimulatedPosition(request.Symbol, totalQty, newAvg);
                }
                else
                {
                    _positions[request.Symbol] = new SimulatedPosition(request.Symbol, request.Quantity, fillPrice);
                }
            }
            else // Sell
            {
                _cashBalance += fillValue;

                if (_positions.TryGetValue(request.Symbol, out var existing) && existing.Quantity > 0)
                {
                    var pnl = (fillPrice - existing.AverageEntryPrice) * request.Quantity;
                    _realizedPnL += pnl;

                    var remaining = existing.Quantity - request.Quantity;
                    if (remaining <= 0)
                        _positions.Remove(request.Symbol);
                    else
                        _positions[request.Symbol] = existing with { Quantity = remaining };
                }
            }

            _tradeCount++;

            var order = new BotOrder
            {
                OrderId = orderId,
                ClientOrderId = request.ClientOrderId,
                Symbol = request.Symbol,
                Side = request.Side,
                Type = request.Type,
                Status = BotOrderStatus.Filled,
                Quantity = request.Quantity,
                FilledQuantity = request.Quantity,
                AverageFillPrice = fillPrice,
                LimitPrice = request.LimitPrice,
                SubmittedAtUtc = now,
                FilledAtUtc = now
            };
            _orders[orderId] = order;

            // Keep latest market price up-to-date after every fill (use base price, not slippage-adjusted fill)
            _latestPrices[request.Symbol] = basePrice;

            _logger.LogInformation("[SIM-BROKER] {Side} {Qty} {Symbol} @ {Price:N2} (slippage: {Slip:N4}). Cash: ${Cash:N2}",
                request.Side, request.Quantity, request.Symbol, fillPrice, slippage, _cashBalance);

            return Task.FromResult(order);
        }
    }

    public Task<BotOrder> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_orders.TryGetValue(orderId, out var order))
                return Task.FromResult(order);
            
            throw new InvalidOperationException($"[SIM-BROKER] Order {orderId} not found");
        }
    }

    public Task<bool> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        // All simulated orders fill immediately; nothing to cancel
        return Task.FromResult(false);
    }

    public Task<int> CancelAllOpenOrdersAsync(string symbol, CancellationToken cancellationToken = default)
    {
        // All simulated orders fill immediately; nothing to cancel
        return Task.FromResult(0);
    }

    public Task<BotPosition?> GetPositionAsync(string symbol, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_positions.TryGetValue(symbol, out var pos) && pos.Quantity > 0)
            {
                var currentPrice = _latestPrices.GetValueOrDefault(symbol, pos.AverageEntryPrice);
                BotPosition result = new(symbol, pos.Quantity, pos.AverageEntryPrice, currentPrice,
                    currentPrice * pos.Quantity);
                return Task.FromResult<BotPosition?>(result);
            }
            return Task.FromResult<BotPosition?>(null);
        }
    }

    public Task<IReadOnlyList<BotPosition>> GetAllPositionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var list = new List<BotPosition>();
            foreach (var kvp in _positions)
            {
                if (kvp.Value.Quantity > 0)
                {
                    var currentPrice = _latestPrices.GetValueOrDefault(kvp.Key, kvp.Value.AverageEntryPrice);
                    list.Add(new BotPosition(kvp.Key, kvp.Value.Quantity, kvp.Value.AverageEntryPrice,
                        currentPrice, currentPrice * kvp.Value.Quantity));
                }
            }
            return Task.FromResult<IReadOnlyList<BotPosition>>(list);
        }
    }

    public Task<decimal> GetBuyingPowerAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_cashBalance);
        }
    }

    public Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_latestPrices.TryGetValue(symbol, out var price))
                return Task.FromResult(price);
            
            throw new InvalidOperationException($"[SIM-BROKER] No price data for {symbol}");
        }
    }

    public Task<bool> ValidateSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true); // All symbols valid in simulation
    }

    /// <summary>
    /// Prints a summary of the simulated trading session.
    /// </summary>
    public void PrintSummary()
    {
        lock (_lock)
        {
            var equity = _cashBalance;
            foreach (var pos in _positions.Values)
            {
                var px = _latestPrices.GetValueOrDefault(pos.Symbol, pos.AverageEntryPrice);
                equity += px * pos.Quantity;
            }

            _logger.LogInformation("[SIM-BROKER] ==========================================");
            _logger.LogInformation("[SIM-BROKER]  R E P L A Y   S U M M A R Y");
            _logger.LogInformation("[SIM-BROKER]  Starting Cash:  ${Cash:N2}", _initialCash);
            _logger.LogInformation("[SIM-BROKER]  Ending Cash:    ${Cash:N2}", _cashBalance);
            _logger.LogInformation("[SIM-BROKER]  Ending Equity:  ${Equity:N2}", equity);
            _logger.LogInformation("[SIM-BROKER]  Realized P/L:   ${PnL:N2}", _realizedPnL);
            _logger.LogInformation("[SIM-BROKER]  Net Return:     {Return:P2}", (equity - _initialCash) / _initialCash);
            _logger.LogInformation("[SIM-BROKER]  Total Trades:   {Count}", _tradeCount);

            foreach (var pos in _positions.Values.Where(p => p.Quantity > 0))
            {
                var px = _latestPrices.GetValueOrDefault(pos.Symbol, pos.AverageEntryPrice);
                var unrealized = (px - pos.AverageEntryPrice) * pos.Quantity;
                _logger.LogInformation("[SIM-BROKER]  Open Position:  {Qty} {Symbol} @ {Avg:N2} (unrealized: ${PnL:N2})",
                    pos.Quantity, pos.Symbol, pos.AverageEntryPrice, unrealized);
            }

            _logger.LogInformation("[SIM-BROKER] ==========================================");
        }
    }
}
