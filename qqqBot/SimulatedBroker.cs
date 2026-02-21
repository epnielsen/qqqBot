using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// "The Fake Broker" — handles Buy/Sell orders during Replay without hitting Alpaca.
/// 
/// Tracks cash balance, positions, and fills orders at the current replay price
/// with realistic spread and slippage simulation:
///   - Synthetic bid/ask spread (wider during OV and PH phases)
///   - Volatility-scaled slippage (higher during high-ATR periods)
///   - Configurable base slippage in basis points
/// 
/// This ensures replay never sends real orders to the exchange while producing
/// realistic transaction costs for backtesting.
/// 
/// Usage: Injected by ProgramRefactored when --mode=replay is specified.
/// </summary>
public sealed class SimulatedBroker : IBrokerExecution
{
    private readonly ILogger _logger;
    private readonly decimal _initialCash;
    private readonly object _lock = new();

    // Configuration
    private readonly decimal _slippageBps;           // Base slippage in basis points
    private readonly decimal _spreadBps;             // Base half-spread in basis points
    private readonly decimal _ovSpreadMultiplier;     // Spread multiplier during Open Volatility (09:30-10:13 ET)
    private readonly decimal _phSpreadMultiplier;     // Spread multiplier during Power Hour (14:00-16:00 ET)
    private readonly bool _volatilitySlippageEnabled; // Use volatility-scaled slippage
    private readonly decimal _volSlippageMultiplier;  // Multiplier for volatility slippage (k × σ)
    private readonly int _volWindowTicks;             // Rolling window for volatility calculation

    // Stochastic execution RNG (seeded for reproducible replay results)
    private readonly Random _rng;
    private readonly int _seed;
    private readonly double _slippageVarianceFactor;  // Variance multiplier on total slippage (0 = deterministic)

    // Alpaca Auction mode: simulate realistic fill difficulty during market open
    private readonly bool _auctionModeEnabled;
    private readonly int _auctionWindowMinutes;           // Minutes after 09:30 ET where auction mode applies
    private readonly double _auctionTimeoutProbability;    // Probability order returns New (will timeout)
    private readonly double _auctionPartialFillProbability; // Probability of partial fill (vs full fill)
    private readonly double _auctionMinFillRatio;          // Minimum partial fill ratio (e.g., 0.3 = 30% of requested)

    // Simulated state
    private decimal _cashBalance;
    private readonly Dictionary<string, SimulatedPosition> _positions = new();
    private readonly Dictionary<Guid, BotOrder> _orders = new();
    private readonly Dictionary<string, decimal> _latestPrices = new();

    // Track P/L
    private decimal _realizedPnL;
    private int _tradeCount;

    // High/low watermarks (real-time equity including unrealized)
    private decimal _peakEquity;
    private DateTime _peakEquityTime;
    private decimal _troughEquity;
    private DateTime _troughEquityTime;
    private bool _watermarkInitialized;

    // Track latest timestamp for phase-aware spread
    private DateTime _latestTimestampUtc;
    private readonly TimeZoneInfo _easternZone;

    // Rolling price history for volatility calculation
    private readonly Dictionary<string, Queue<decimal>> _priceHistory = new();

    // Cumulative spread/slippage tracking for summary
    private decimal _totalSpreadCost;
    private decimal _totalSlippageCost;

    // Auction mode stats
    private int _auctionTimeouts;
    private int _auctionPartialFills;
    private int _auctionFullFills;

    private record SimulatedPosition(string Symbol, long Quantity, decimal AverageEntryPrice);

    public SimulatedBroker(
        ILogger logger,
        decimal initialCash = 30_000m,
        decimal slippageBps = 1.0m,
        decimal spreadBps = 2.0m,
        decimal ovSpreadMultiplier = 3.0m,
        decimal phSpreadMultiplier = 1.5m,
        bool volatilitySlippageEnabled = true,
        decimal volSlippageMultiplier = 0.5m,
        int volWindowTicks = 60,
        int seed = 0,
        double slippageVarianceFactor = 0.5,
        bool auctionModeEnabled = false,
        int auctionWindowMinutes = 7,
        double auctionTimeoutProbability = 0.6,
        double auctionPartialFillProbability = 0.4,
        double auctionMinFillRatio = 0.3)
    {
        _logger = logger;
        _initialCash = initialCash;
        _cashBalance = initialCash;
        _slippageBps = slippageBps;
        _spreadBps = spreadBps;
        _ovSpreadMultiplier = ovSpreadMultiplier;
        _phSpreadMultiplier = phSpreadMultiplier;
        _volatilitySlippageEnabled = volatilitySlippageEnabled;
        _volSlippageMultiplier = volSlippageMultiplier;
        _volWindowTicks = volWindowTicks;
        _seed = seed;
        _rng = new Random(seed);
        _slippageVarianceFactor = slippageVarianceFactor;
        _auctionModeEnabled = auctionModeEnabled;
        _auctionWindowMinutes = auctionWindowMinutes;
        _auctionTimeoutProbability = auctionTimeoutProbability;
        _auctionPartialFillProbability = auctionPartialFillProbability;
        _auctionMinFillRatio = auctionMinFillRatio;
        _easternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        _logger.LogInformation("[SIM-BROKER] ==========================================");
        _logger.LogInformation("[SIM-BROKER]  S I M U L A T E D   B R O K E R");
        _logger.LogInformation("[SIM-BROKER]  Starting Cash: ${Cash:N2}", initialCash);
        _logger.LogInformation("[SIM-BROKER]  Base Slippage: {Slip} bps", slippageBps);
        _logger.LogInformation("[SIM-BROKER]  Base Spread:   {Spread} bps (OV ×{OV}, PH ×{PH})",
            spreadBps, ovSpreadMultiplier, phSpreadMultiplier);
        _logger.LogInformation("[SIM-BROKER]  Vol Slippage:  {Enabled} (k={K}, window={W})",
            volatilitySlippageEnabled, volSlippageMultiplier, volWindowTicks);
        _logger.LogInformation("[SIM-BROKER]  RNG Seed:      {Seed} | Slippage Variance: {Var:P0}",
            seed, slippageVarianceFactor);
        if (auctionModeEnabled)
        {
            _logger.LogInformation("[SIM-BROKER]  Auction Mode:  ENABLED (window={Win}m, timeout={TO:P0}, partial={PF:P0}, minRatio={MR:P0})",
                auctionWindowMinutes, auctionTimeoutProbability, auctionPartialFillProbability, auctionMinFillRatio);
        }
        _logger.LogInformation("[SIM-BROKER] ==========================================");
    }

    /// <summary>
    /// Update the latest known price for a symbol (called by the replay pipeline).
    /// Tracks equity high/low watermarks and maintains rolling price history for volatility.
    /// </summary>
    public void UpdatePrice(string symbol, decimal price, DateTime timestampUtc = default)
    {
        lock (_lock)
        {
            _latestPrices[symbol] = price;
            if (timestampUtc != default)
                _latestTimestampUtc = timestampUtc;

            // Maintain rolling price history for volatility calculation
            if (_volatilitySlippageEnabled)
            {
                if (!_priceHistory.TryGetValue(symbol, out var history))
                {
                    history = new Queue<decimal>();
                    _priceHistory[symbol] = history;
                }
                history.Enqueue(price);
                while (history.Count > _volWindowTicks)
                    history.Dequeue();
            }

            // Track equity watermarks when we have a valid timestamp (replay mode)
            if (timestampUtc != default)
            {
                var equity = _cashBalance;
                foreach (var pos in _positions.Values)
                {
                    var px = _latestPrices.GetValueOrDefault(pos.Symbol, pos.AverageEntryPrice);
                    equity += px * pos.Quantity;
                }

                if (!_watermarkInitialized)
                {
                    _peakEquity = equity;
                    _peakEquityTime = timestampUtc;
                    _troughEquity = equity;
                    _troughEquityTime = timestampUtc;
                    _watermarkInitialized = true;
                }
                else
                {
                    if (equity > _peakEquity)
                    {
                        _peakEquity = equity;
                        _peakEquityTime = timestampUtc;
                    }
                    if (equity < _troughEquity)
                    {
                        _troughEquity = equity;
                        _troughEquityTime = timestampUtc;
                    }
                }
            }
        }
    }

    public Task<BotOrder> SubmitOrderAsync(BotOrderRequest request, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var orderId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            // Determine fill price with slippage
            // Priority: HintPrice (decision-tick price from TraderEngine) > LimitPrice > _latestPrices
            // HintPrice captures the price the trader saw when it decided to trade, which may differ
            // significantly from _latestPrices when the replay pipeline races ahead at speed=0.
            decimal basePrice = request.HintPrice ?? request.LimitPrice ?? _latestPrices.GetValueOrDefault(request.Symbol, 0m);
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

            // --- Phase-aware spread ---
            decimal spreadMultiplier = 1.0m;
            if (_latestTimestampUtc != default)
            {
                var eastern = TimeZoneInfo.ConvertTimeFromUtc(_latestTimestampUtc, _easternZone);
                var tod = eastern.TimeOfDay;
                if (tod >= new TimeSpan(9, 30, 0) && tod < new TimeSpan(10, 13, 0))
                    spreadMultiplier = _ovSpreadMultiplier;   // Open Volatility: wider spread
                else if (tod >= new TimeSpan(14, 0, 0) && tod < new TimeSpan(16, 0, 0))
                    spreadMultiplier = _phSpreadMultiplier;   // Power Hour: moderately wider
            }
            decimal halfSpread = basePrice * (_spreadBps / 10_000m) * spreadMultiplier;

            // --- Slippage: base + volatility-scaled ---
            decimal baseSlippage = basePrice * (_slippageBps / 10_000m);
            decimal volSlippage = 0m;
            if (_volatilitySlippageEnabled &&
                _priceHistory.TryGetValue(request.Symbol, out var hist) && hist.Count >= 5)
            {
                // Rolling standard deviation of recent prices
                var prices = hist.ToArray();
                decimal mean = 0m;
                foreach (var p in prices) mean += p;
                mean /= prices.Length;
                decimal sumSqDev = 0m;
                foreach (var p in prices)
                {
                    var dev = p - mean;
                    sumSqDev += dev * dev;
                }
                decimal sigma = (decimal)Math.Sqrt((double)(sumSqDev / prices.Length));
                volSlippage = _volSlippageMultiplier * sigma;
            }
            decimal totalSlippage = baseSlippage + volSlippage;

            // Stochastic slippage variance: ±50% by default (seeded RNG for reproducibility)
            if (_slippageVarianceFactor > 0)
            {
                var variance = 1.0 + (_rng.NextDouble() - 0.5) * _slippageVarianceFactor;
                totalSlippage *= (decimal)variance;
                if (totalSlippage < 0) totalSlippage = 0;
            }

            // --- Alpaca Auction mode: simulate opening fill difficulty ---
            if (_auctionModeEnabled && IsInAuctionWindow())
            {
                var roll = _rng.NextDouble();
                if (roll < _auctionTimeoutProbability)
                {
                    // Simulate order that won't fill (TraderEngine will timeout after 10s and cancel)
                    _auctionTimeouts++;
                    var pendingOrder = new BotOrder
                    {
                        OrderId = orderId,
                        ClientOrderId = request.ClientOrderId,
                        Symbol = request.Symbol,
                        Side = request.Side,
                        Type = request.Type,
                        Status = BotOrderStatus.New,
                        Quantity = request.Quantity,
                        FilledQuantity = 0,
                        SubmittedAtUtc = now
                    };
                    _orders[orderId] = pendingOrder;
                    _logger.LogInformation("[SIM-BROKER] AUCTION: {Side} {Qty} {Symbol} submitted during auction window — simulating no fill (timeout expected)",
                        request.Side, request.Quantity, request.Symbol);
                    return Task.FromResult(pendingOrder);
                }

                var partialRoll = _rng.NextDouble();
                if (partialRoll < _auctionPartialFillProbability)
                {
                    // Simulate partial fill (like Feb 20's 95/205 scenario)
                    var fillRatio = _auctionMinFillRatio + _rng.NextDouble() * (1.0 - _auctionMinFillRatio);
                    var filledQty = (long)Math.Max(1, Math.Round(request.Quantity * fillRatio));
                    if (filledQty >= request.Quantity) filledQty = request.Quantity; // Cap at full

                    _auctionPartialFills++;

                    // Compute fill price for partial fill
                    decimal partialFillPrice = request.Side == BotOrderSide.Buy
                        ? basePrice + halfSpread + totalSlippage
                        : basePrice - halfSpread - totalSlippage;
                    partialFillPrice = Math.Round(partialFillPrice, 2);
                    var partialFillValue = partialFillPrice * filledQty;

                    if (request.Side == BotOrderSide.Buy)
                    {
                        if (partialFillValue > _cashBalance)
                        {
                            // Can't even afford partial — treat as timeout
                            _auctionTimeouts++;
                            var noFillOrder = new BotOrder
                            {
                                OrderId = orderId,
                                ClientOrderId = request.ClientOrderId,
                                Symbol = request.Symbol,
                                Side = request.Side,
                                Type = request.Type,
                                Status = BotOrderStatus.New,
                                Quantity = request.Quantity,
                                FilledQuantity = 0,
                                SubmittedAtUtc = now
                            };
                            _orders[orderId] = noFillOrder;
                            return Task.FromResult(noFillOrder);
                        }

                        _cashBalance -= partialFillValue;

                        // Update position with partial quantity
                        if (_positions.TryGetValue(request.Symbol, out var existingPartial))
                        {
                            var totalCost = existingPartial.AverageEntryPrice * existingPartial.Quantity + partialFillPrice * filledQty;
                            var totalQty = existingPartial.Quantity + filledQty;
                            var newAvg = totalQty > 0 ? totalCost / totalQty : 0;
                            _positions[request.Symbol] = new SimulatedPosition(request.Symbol, totalQty, newAvg);
                        }
                        else
                        {
                            _positions[request.Symbol] = new SimulatedPosition(request.Symbol, filledQty, partialFillPrice);
                        }
                    }

                    _tradeCount++;
                    _totalSpreadCost += halfSpread * filledQty;
                    _totalSlippageCost += totalSlippage * filledQty;

                    var partialOrder = new BotOrder
                    {
                        OrderId = orderId,
                        ClientOrderId = request.ClientOrderId,
                        Symbol = request.Symbol,
                        Side = request.Side,
                        Type = request.Type,
                        Status = BotOrderStatus.PartiallyFilled,
                        Quantity = request.Quantity,
                        FilledQuantity = filledQty,
                        AverageFillPrice = partialFillPrice,
                        LimitPrice = request.LimitPrice,
                        SubmittedAtUtc = now,
                        FilledAtUtc = now
                    };
                    _orders[orderId] = partialOrder;

                    _logger.LogWarning("[SIM-BROKER] AUCTION: Partial fill {Side} {Filled}/{Total} {Symbol} @ {Price:N2} (ratio: {Ratio:P0}). Cash: ${Cash:N2}",
                        request.Side, filledQty, request.Quantity, request.Symbol, partialFillPrice, (double)filledQty / request.Quantity, _cashBalance);

                    return Task.FromResult(partialOrder);
                }

                // Passed both rolls — full fill during auction (lucky)
                _auctionFullFills++;
            }

            // --- Apply: Buy pays more, Sell receives less ---
            decimal fillPrice = request.Side == BotOrderSide.Buy
                ? basePrice + halfSpread + totalSlippage
                : basePrice - halfSpread - totalSlippage;
            fillPrice = Math.Round(fillPrice, 2);

            // Track cumulative costs (per-share × quantity)
            _totalSpreadCost += halfSpread * request.Quantity;
            _totalSlippageCost += totalSlippage * request.Quantity;

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

            _logger.LogInformation("[SIM-BROKER] {Side} {Qty} {Symbol} @ {Price:N2} (spread: {Spd:N4}, slip: {Slip:N4}). Cash: ${Cash:N2}",
                request.Side, request.Quantity, request.Symbol, fillPrice, halfSpread, totalSlippage, _cashBalance);

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
        lock (_lock)
        {
            if (_orders.TryGetValue(orderId, out var order) &&
                (order.Status == BotOrderStatus.New || order.Status == BotOrderStatus.Accepted ||
                 order.Status == BotOrderStatus.PartiallyFilled))
            {
                // Replace with canceled version (preserving any partial fill info)
                _orders[orderId] = new BotOrder
                {
                    OrderId = order.OrderId,
                    ClientOrderId = order.ClientOrderId,
                    Symbol = order.Symbol,
                    Side = order.Side,
                    Type = order.Type,
                    Status = BotOrderStatus.Canceled,
                    Quantity = order.Quantity,
                    FilledQuantity = order.FilledQuantity,
                    AverageFillPrice = order.AverageFillPrice,
                    LimitPrice = order.LimitPrice,
                    SubmittedAtUtc = order.SubmittedAtUtc,
                    FilledAtUtc = order.FilledAtUtc
                };
                _logger.LogInformation("[SIM-BROKER] Canceled order {OrderId} (was {Status}, filled {Filled}/{Total})",
                    orderId, order.Status, order.FilledQuantity, order.Quantity);
                return Task.FromResult(true);
            }
            // Already filled/canceled/rejected — nothing to cancel
            return Task.FromResult(false);
        }
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

    public Task<decimal> GetEquityAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Total equity = cash + market value of all positions
            var equity = _cashBalance;
            foreach (var kvp in _positions)
            {
                if (_latestPrices.TryGetValue(kvp.Key, out var price))
                    equity += kvp.Value.Quantity * price;
                else
                    equity += kvp.Value.Quantity * kvp.Value.AverageEntryPrice;
            }
            return Task.FromResult(equity);
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
            _logger.LogInformation("[SIM-BROKER]  Spread Cost:    ${Cost:N2}", _totalSpreadCost);
            _logger.LogInformation("[SIM-BROKER]  Slippage Cost:  ${Cost:N2}", _totalSlippageCost);
            _logger.LogInformation("[SIM-BROKER]  Total Txn Cost: ${Cost:N2}", _totalSpreadCost + _totalSlippageCost);
            if (_tradeCount > 0)
                _logger.LogInformation("[SIM-BROKER]  Avg Cost/Trade: ${Cost:N2}", (_totalSpreadCost + _totalSlippageCost) / _tradeCount);

            if (_watermarkInitialized)
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var peakLocal = TimeZoneInfo.ConvertTimeFromUtc(_peakEquityTime, eastern);
                var troughLocal = TimeZoneInfo.ConvertTimeFromUtc(_troughEquityTime, eastern);
                var peakPnL = _peakEquity - _initialCash;
                var troughPnL = _troughEquity - _initialCash;
                _logger.LogInformation("[SIM-BROKER]  Peak P/L:       {PnL:+$#,##0.00;-$#,##0.00} ({Pct:P2}) at {Time:HH:mm:ss} ET",
                    peakPnL, peakPnL / _initialCash, peakLocal);
                _logger.LogInformation("[SIM-BROKER]  Trough P/L:     {PnL:+$#,##0.00;-$#,##0.00} ({Pct:P2}) at {Time:HH:mm:ss} ET",
                    troughPnL, troughPnL / _initialCash, troughLocal);
            }

            foreach (var pos in _positions.Values.Where(p => p.Quantity > 0))
            {
                var px = _latestPrices.GetValueOrDefault(pos.Symbol, pos.AverageEntryPrice);
                var unrealized = (px - pos.AverageEntryPrice) * pos.Quantity;
                _logger.LogInformation("[SIM-BROKER]  Open Position:  {Qty} {Symbol} @ {Avg:N2} (unrealized: ${PnL:N2})",
                    pos.Quantity, pos.Symbol, pos.AverageEntryPrice, unrealized);
            }

            _logger.LogInformation("[SIM-BROKER]  RNG Seed:       {Seed}", _seed);
            if (_auctionModeEnabled)
            {
                var auctionTotal = _auctionTimeouts + _auctionPartialFills + _auctionFullFills;
                _logger.LogInformation("[SIM-BROKER]  Auction Orders: {Total} (timeouts: {TO}, partial: {PF}, full: {FF})",
                    auctionTotal, _auctionTimeouts, _auctionPartialFills, _auctionFullFills);
            }

            _logger.LogInformation("[SIM-BROKER] ==========================================");
        }
    }

    /// <summary>
    /// Check if the current timestamp is within the auction window (e.g., 09:30-09:37 ET).
    /// During this window, Alpaca Auction mode may simulate fill difficulty.
    /// </summary>
    private bool IsInAuctionWindow()
    {
        if (_latestTimestampUtc == default) return false;
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(_latestTimestampUtc, _easternZone);
        var tod = eastern.TimeOfDay;
        var auctionStart = new TimeSpan(9, 30, 0);
        var auctionEnd = auctionStart + TimeSpan.FromMinutes(_auctionWindowMinutes);
        return tod >= auctionStart && tod < auctionEnd;
    }
}
