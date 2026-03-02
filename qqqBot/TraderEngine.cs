using System.Threading.Channels;
using MarketBlocks.Bots.Domain;
using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Trader Engine - Consumer in the Producer/Consumer pattern.
/// Consumes MarketRegime signals from the Analyst and executes trades.
/// Handles position management, trailing stops, and order execution.
/// </summary>
public class TraderEngine : BackgroundService
{
    private readonly ILogger<TraderEngine> _logger;
    private readonly TradingSettings _settings;
    private readonly IBrokerExecution _broker;
    private readonly IIocExecutor _iocExecutor;
    private readonly TradingStateManager _stateManager;
    private readonly TimeZoneInfo _easternZone;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    
    private ChannelReader<MarketRegime>? _regimeChannel;
    private TradingState _state;
    
    // Pending order tracking (replaces fire-and-forget Task.Run)
    private Guid? _pendingOrderId;
    private string? _pendingOrderSymbol;
    private long _pendingOrderQuantity;
    private decimal _pendingOrderBasePrice;
    private decimal _pendingOrderEffectivePrice;
    private DateTime? _pendingOrderSubmitTime;
    
    // Safe mode flag - set when liquidation fails
    private bool _isSafeMode;
    private string? _safeModeReason;
    
    // Repair mode - signals app should exit after repair
    private bool _repairModeTriggered;
    public bool RepairModeTriggered => _repairModeTriggered;
    
    // Trailing stop state
    private decimal _highWaterMark;
    private decimal _lowWaterMark;
    private decimal _etfHighWaterMark; // ETF price HWM for ratchet % (avoids benchmark/ETF domain mismatch)
    private decimal _virtualStopPrice;
    private bool _isStoppedOut;
    private string _stoppedOutDirection = string.Empty;
    private decimal _washoutLevel;
    private DateTime? _stopoutTime;
    private decimal _lastRatchetTierTrigger = -1m; // Track active ratchet tier for change logging
    private bool _isTrendRescuePosition; // Position was entered via trendRescue (wider trailing stop)
    private bool _isDriftPosition; // Position was entered via drift mode (wider trailing stop)
    
    // Effective "now" for the current tick: regime timestamp in replay, wall-clock in live
    private DateTime _currentUtc;
    
    // Neutral detection
    private DateTime? _neutralDetectionTime;
    
    // Last signal for deduplication
    private string _lastProcessedSignal = string.Empty;
    
    // Daily profit target tracking (HaltReason persisted in _state; replaces old volatile _dailyTargetReached)
    private bool _dailyTargetArmed;       // True when profit target reached but trailing stop active
    private decimal _dailyTargetPeakPnL;  // High water mark P/L for daily trailing stop
    private decimal _dailyTargetStopLevel; // Current trailing stop level for daily P/L
    
    // Phase tracking for PH Resume detection
    private string? _previousPhaseName;
    
    // MR hard stop cooldown — prevents cascade re-entry after stop-out
    // When true, MR_LONG/MR_SHORT signals are ignored until MR_FLAT resets the cycle
    private bool _mrHardStopCooldown;
    
    // MR entry benchmark price — for ATR-based stop computation in QQQ space
    private decimal _mrEntryBenchmarkPrice;
    private bool _mrEntryIsLong; // true = MR_LONG (bought TQQQ, QQQ dropping hurts), false = MR_SHORT
    
    // Periodic broker position sync (detect phantom positions mid-session)
    private DateTime _lastBrokerSyncTime = DateTime.MinValue;
    private const int BrokerSyncIntervalSeconds = 60; // Check every minute
    
    // Slippage tracking
    private readonly object _slippageLock = new();
    private decimal _cumulativeSlippage;
    private string? _slippageLogFile;
    
    // Status logging
    private DateTime _lastStatusLogTime = DateTime.MinValue;
    private string _lastLoggedSignal = string.Empty;
    private string? _lastDayStr;
    
    // Cached regime for trim decisions (Slope access)
    private MarketRegime? _lastRegime;
    
    // Direction switch cooldown: tracks the last time a BULL↔BEAR switch occurred
    private DateTime? _lastDirectionSwitchTime;
    // Base cooldown captured at construction (immune to shared-settings race in replay)
    private readonly int _baseDirectionSwitchCooldownSeconds;
    
    // Buy retry cooldown (prevents rapid-fire order spam after failures)
    private int _consecutiveBuyFailures;
    private DateTime? _buyRetryCooldownUntil;
    private string? _lastBuyFailureDirection; // Track which signal direction caused the failure
    private bool _buyCooldownLoggedStart; // Throttle: only log once at cooldown start (not per-tick)
    
    // Stream health monitoring — detects data feed disconnections (live mode only)
    private DateTime _lastTickReceivedUtc = DateTime.UtcNow;
    private bool _hasReceivedFirstTick = false;  // Watchdog ignores gaps until first tick received
    private bool _marketSessionEnded = false;    // Watchdog ignores gaps after MARKET_CLOSE until next session
    
    // Market-open delay (skip opening auction)
    private bool _marketOpenDelayLogged;
    
    // Time-based phase switching (auto config override)
    private readonly TimeRuleApplier? _timeRuleApplier;

    public TraderEngine(
        ILogger<TraderEngine> logger,
        TradingSettings settings,
        IBrokerExecution broker,
        IIocExecutor iocExecutor,
        TradingStateManager stateManager,
        TimeRuleApplier? timeRuleApplier = null)
    {
        _logger = logger;
        _settings = settings;
        // FAIL FAST: Validate settings immediately on startup
        _settings.Validate();
        _broker = broker;
        _iocExecutor = iocExecutor;
        _stateManager = stateManager;
        _timeRuleApplier = timeRuleApplier;
        _easternZone = EasternTimeZone.Instance;
        _baseDirectionSwitchCooldownSeconds = settings.DirectionSwitchCooldownSeconds;
        
        // Load persisted state
        _state = _stateManager.Load();
        
        // Initialize state if first run (no trading_state.json file existed)
        if (!_state.IsInitialized)
        {
            _state.AvailableCash = _settings.StartingAmount;
            _state.AccumulatedLeftover = 0m;
            _state.StartingAmount = _settings.StartingAmount;
            _state.IsInitialized = true;
            _stateManager.Save(_state);
            _logger.LogInformation("[TRADER] Initialized trading with starting amount: ${StartingAmount:N2}",
                _settings.StartingAmount);
        }
        
        // Always sync StartingAmount from config to state to ensure we use current settings
        if (_state.StartingAmount != _settings.StartingAmount)
        {
            var oldAmount = _state.StartingAmount;
            _state.StartingAmount = _settings.StartingAmount;
            _stateManager.Save(_state);
            _logger.LogInformation("[TRADER] Updated StartingAmount from config: ${Old:N2} -> ${New:N2}",
                oldAmount, _settings.StartingAmount);
        }
        
        // Restore trailing stop state
        _highWaterMark = _state.HighWaterMark ?? 0m;
        _lowWaterMark = _state.LowWaterMark ?? 0m;
        _virtualStopPrice = _state.TrailingStopValue ?? 0m;
        _isStoppedOut = _state.IsStoppedOut;
        _stoppedOutDirection = _state.StoppedOutDirection ?? string.Empty;
        _washoutLevel = _state.WashoutLevel ?? 0m;
        _stopoutTime = string.IsNullOrEmpty(_state.StopoutTimestamp) 
            ? null 
            : DateTime.TryParse(_state.StopoutTimestamp, out var ts) ? ts : null;
        
        // Restore daily profit target trailing stop state
        _dailyTargetArmed = _state.DailyTargetArmed;
        _dailyTargetPeakPnL = _state.DailyTargetPeakPnL ?? 0m;
        _dailyTargetStopLevel = _state.DailyTargetStopLevel ?? 0m;
        if (_dailyTargetArmed)
        {
            _logger.LogInformation("[DAILY TARGET] Restored armed trailing stop: Peak ${Peak:N2}, Stop ${Stop:N2}",
                _dailyTargetPeakPnL, _dailyTargetStopLevel);
        }
        
        // Restore halt state and PH Resume
        if (_state.HaltReason != HaltReason.None)
        {
            _logger.LogInformation("[TRADER] Restored halt state: {Reason}, PhResumeArmed: {PhResume}",
                _state.HaltReason, _state.PhResumeArmed);
        }
        
        // Initialize slippage log if monitoring enabled
        if (_settings.MonitorSlippage)
        {
            var dateStr = DateTime.UtcNow.ToString("yyyyMMdd");
            _slippageLogFile = $"qqqBot-slippage-log-{_settings.BullSymbol}-{_settings.BearSymbol}-{dateStr}.csv";
        }
    }

    /// <summary>
    /// Current trading state (read-only access for external components).
    /// </summary>
    public TradingState State => _state;

    /// <summary>
    /// Current position symbol (for Analyst band calculations).
    /// </summary>
    public string? CurrentPosition => _state.CurrentPosition;

    /// <summary>
    /// Current share count (for Analyst band calculations).
    /// </summary>
    public long CurrentShares => _state.CurrentShares;

    /// <summary>
    /// Get the last analyst signal (for restart recovery).
    /// </summary>
    public string? LastAnalystSignal => _state.LastAnalystSignal;

    /// <summary>
    /// Save the analyst signal to persistent state (called by AnalystEngine on signal change).
    /// </summary>
    public void SaveLastAnalystSignal(string signal)
    {
        _state.LastAnalystSignal = signal;
        _stateManager.Save(_state);
    }

    public async Task StartAsync(ChannelReader<MarketRegime> regimeChannel, CancellationToken cancellationToken)
    {
        _regimeChannel = regimeChannel;
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_regimeChannel == null)
        {
            _logger.LogError("[TRADER] No regime channel provided. Call StartAsync with a channel first.");
            return;
        }

        _logger.LogInformation("[TRADER] Starting trade execution engine...");
        _logger.LogInformation("[TRADER] Current state: Position={Pos}, Shares={Shares}, Cash=${Cash:N2}",
            _state.CurrentPosition ?? "CASH",
            _state.CurrentShares,
            _state.AvailableCash + _state.AccumulatedLeftover);
        
        // Log Dynamic Exit Strategy configuration
        _logger.LogInformation(
            "[CONFIG] Dynamic Exit Strategy: ScalpWait={ScalpWait}s | TrendWait={TrendWait}s | Threshold={Threshold:F6}",
            _settings.ExitStrategy.ScalpWaitSeconds,
            _settings.ExitStrategy.TrendWaitSeconds,
            _settings.ExitStrategy.TrendConfidenceThreshold);

        // CRITICAL: Verify local state matches broker positions on startup
        // This prevents dangerous mismatches when trading_state.json is deleted while positions exist
        await VerifyAndSyncBrokerStateAsync(stoppingToken);

        // If repair mode was triggered, exit immediately - do NOT continue trading
        if (_repairModeTriggered)
        {
            _logger.LogCritical("[TRADER] Repair mode active - stopping engine. Restart required.");
            return;
        }

        // Verify account has sufficient cash to trade
        var totalAvailable = _state.AvailableCash + _state.AccumulatedLeftover;
        if (totalAvailable <= 0 && _state.CurrentShares == 0)
        {
            _logger.LogCritical("[TRADER] INSUFFICIENT FUNDS: Account has ${Cash:N2} available. " +
                "Cannot trade with zero balance. Please fund the account or restore trading_state.json with a valid balance.",
                totalAvailable);
            throw new InvalidOperationException(
                $"Insufficient funds to trade. Available: ${totalAvailable:N2}. " +
                "Restore trading_state.json or check StartingAmount in appsettings.json.");
        }

        try
        {
            // Launch stream health watchdog (live mode only — replay streams are always "connected")
            Task? watchdogTask = !_settings.BypassMarketHoursCheck
                ? RunStreamWatchdogAsync(stoppingToken)
                : null;
            
            await foreach (var regime in _regimeChannel.ReadAllAsync(stoppingToken))
            {
                try
                {
                    // In replay mode, use the regime's tick timestamp as "now" so all
                    // time-sensitive logic (cooldowns, delays, phase switching) works correctly.
                    _currentUtc = _settings.BypassMarketHoursCheck ? regime.TimestampUtc : DateTime.UtcNow;
                    _lastTickReceivedUtc = DateTime.UtcNow; // Stream health: always wall-clock
                    _hasReceivedFirstTick = true;
                    _marketSessionEnded = false; // Reset on new session



                    // Check for pending order BEFORE processing new signals
                    if (_pendingOrderId.HasValue)
                    {
                        await ProcessPendingOrderAsync(stoppingToken);
                        
                        // If order is still pending, skip regime processing
                        if (_pendingOrderId.HasValue)
                        {
                            _logger.LogDebug("[TRADER] Order {OrderId} still pending. Skipping signal processing.", 
                                _pendingOrderId.Value);
                            continue;
                        }
                    }
                    
                    // --- Time-Based Phase Switching ---
                    // TraderEngine also checks for phase transitions to catch settings changes
                    // that only affect trade execution (trailing stop, IOC, trimming, etc.)
                    // AnalystEngine handles indicator reconfiguration separately.
                    if (_timeRuleApplier != null)
                    {
                        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
                        var phaseChanged = _timeRuleApplier.CheckAndApply(easternNow.TimeOfDay, "trader");
                        
                        // Detect phase transition for PH Resume
                        var currentPhase = _timeRuleApplier.ActivePhaseName;
                        if (phaseChanged || currentPhase != _previousPhaseName)
                        {
                            CheckPhResume();
                            _previousPhaseName = currentPhase;
                        }
                    }
                    
                    // Check safe mode
                    // CRITICAL: MARKET_CLOSE signals MUST bypass safe mode!
                    // End-of-day liquidation is mandatory - we cannot hold positions overnight.
                    if (_isSafeMode && regime.Signal != "MARKET_CLOSE")
                    {
                        _logger.LogWarning("[SAFE MODE] Trading halted: {Reason}. Manual intervention required.", 
                            _safeModeReason ?? "Unknown");
                        continue;
                    }
                    
                    if (_isSafeMode && regime.Signal == "MARKET_CLOSE")
                    {
                        _logger.LogWarning("[SAFE MODE] Bypassing safe mode for MARKET_CLOSE - end-of-day liquidation is MANDATORY.");
                    }
                    
                    // Market-open delay: skip the opening auction to avoid IOC/market order failures
                    if (_settings.MarketOpenDelaySeconds > 0 && !_settings.BypassMarketHoursCheck)
                    {
                        var easternForDelay = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
                        var tod = easternForDelay.TimeOfDay;
                        var marketOpen = new TimeSpan(9, 30, 0);
                        var delayEnd = marketOpen + TimeSpan.FromSeconds(_settings.MarketOpenDelaySeconds);
                        if (tod >= marketOpen && tod < delayEnd)
                        {
                            if (!_marketOpenDelayLogged)
                            {
                                _logger.LogInformation("[TRADER] Market open delay: waiting {Seconds}s for opening auction to complete.",
                                    _settings.MarketOpenDelaySeconds);
                                _marketOpenDelayLogged = true;
                            }
                            continue;
                        }
                        // Reset so we log again next trading day
                        if (tod >= delayEnd)
                            _marketOpenDelayLogged = false;
                    }
                    
                    // Periodic broker position verification to detect phantom positions mid-session
                    if ((DateTime.UtcNow - _lastBrokerSyncTime).TotalSeconds >= BrokerSyncIntervalSeconds)
                    {
                        await PeriodicPositionVerificationAsync(stoppingToken);
                        _lastBrokerSyncTime = DateTime.UtcNow;
                    }
                    
                    await ProcessRegimeAsync(regime, stoppingToken);
                }
                catch (TradingException tex)
                {
                    _logger.LogError(tex, "[TRADER] Trading exception: {Message}", tex.Message);
                    EnterSafeMode(tex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[TRADER] Error processing regime signal: {Signal}", regime.Signal);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[TRADER] Shutdown requested.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TRADER] Fatal error in execution loop");
        }
        finally
        {
            await GracefulShutdownAsync();
            _logger.LogInformation("[TRADER] Engine stopped.");
        }
    }

    /// <summary>
    /// Process a pending order - poll broker for status and update state accordingly.
    /// </summary>
    private async Task ProcessPendingOrderAsync(CancellationToken ct)
    {
        if (!_pendingOrderId.HasValue) return;
        
        try
        {
            var order = await _broker.GetOrderAsync(_pendingOrderId.Value);
            
            switch (order.Status)
            {
                case BotOrderStatus.Filled:
                    await HandleOrderFilledAsync(order);
                    ClearPendingOrder();
                    break;
                    
                case BotOrderStatus.PartiallyFilled:
                    // Continue waiting for full fill
                    _logger.LogDebug("[PENDING] Order {OrderId} partially filled: {Filled}/{Total}", 
                        order.OrderId, order.FilledQuantity, _pendingOrderQuantity);
                    
                    // Check for timeout
                    if (HasPendingOrderTimedOut())
                    {
                        _logger.LogWarning("[PENDING] Partial fill timeout. Cancelling remainder of order {OrderId}...", 
                            _pendingOrderId.Value);
                        
                        try
                        {
                            await _broker.CancelOrderAsync(_pendingOrderId.Value);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[PENDING] Cancel request failed for timed-out partial order.");
                        }
                        
                        // GHOST SHARE FIX: Immediately fetch the final order state to capture partial fills.
                        // We cannot wait for the next poll cycle - by then the bot may have tried to trade
                        // with stale state (e.g., selling 196 shares when only 85 remain).
                        await ReconcileCanceledOrderAsync(ct);
                    }
                    break;
                    
                case BotOrderStatus.Canceled:
                case BotOrderStatus.Rejected:
                case BotOrderStatus.Expired:
                    _logger.LogError("[PENDING] Order {OrderId} {Status}", 
                        order.OrderId, order.Status);
                    
                    // FIX: Check if we got any shares before the cancel/reject happened.
                    // In high-volatility markets, an order can partially fill fractions of a 
                    // second before the cancel request hits. If we blindly rollback, we lose
                    // track of these "ghost shares" which remain at the broker.
                    if (order.FilledQuantity > 0 && order.AverageFillPrice.HasValue)
                    {
                        _logger.LogWarning("[PENDING] Order {Status} but had partial fill ({Qty} @ ${Price:N4}). Accepting shares.", 
                            order.Status, order.FilledQuantity, order.AverageFillPrice.Value);
                        await HandleOrderFilledAsync(order);
                    }
                    else
                    {
                        // Only rollback if truly nothing filled
                        await RollbackPendingOrderStateAsync();
                        // Activate buy cooldown to prevent rapid-fire retries
                        ActivateBuyCooldown(_pendingOrderSymbol == _settings.BearSymbol ? "BEAR" : "BULL");
                    }
                    
                    ClearPendingOrder();
                    break;
                    
                default:
                    // Still open (New, Accepted, PendingNew, etc.)
                    if (HasPendingOrderTimedOut())
                    {
                        _logger.LogWarning("[PENDING] Order {OrderId} timed out after {Seconds}s. Attempting cancel.",
                            _pendingOrderId.Value, _settings.PendingOrderTimeoutSeconds);
                        
                        try
                        {
                            await _broker.CancelOrderAsync(_pendingOrderId.Value);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[PENDING] Cancel request failed. Will check status on next cycle.");
                        }
                        
                        // GHOST SHARE FIX: Immediately fetch the final order state.
                        // Even orders that appeared "New/Accepted" can have fills that arrived
                        // between our last check and the cancel request.
                        await ReconcileCanceledOrderAsync(ct);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PENDING] Failed to check order status for {OrderId}", _pendingOrderId.Value);
            
            // If we've been trying for too long, clear the pending state
            if (HasPendingOrderTimedOut())
            {
                _logger.LogWarning("[PENDING] Giving up on order {OrderId} after timeout.", _pendingOrderId.Value);
                ClearPendingOrder();
            }
        }
    }
    
    private bool HasPendingOrderTimedOut()
    {
        return _pendingOrderSubmitTime.HasValue && 
               (DateTime.UtcNow - _pendingOrderSubmitTime.Value).TotalSeconds > _settings.PendingOrderTimeoutSeconds;
    }
    
    /// <summary>
    /// GHOST SHARE FIX: Immediately fetch the final state of a canceled order and reconcile any partial fills.
    /// This prevents the desync where we cancel a partial fill and forget about it, leaving "ghost shares"
    /// at the broker that the bot doesn't know about.
    /// </summary>
    private async Task ReconcileCanceledOrderAsync(CancellationToken ct)
    {
        if (!_pendingOrderId.HasValue)
        {
            _logger.LogWarning("[RECONCILE] No pending order to reconcile.");
            return;
        }
        
        try
        {
            // Give the broker a moment to process the cancel and update the order status
            await Task.Delay(100, ct);
            
            var finalOrder = await _broker.GetOrderAsync(_pendingOrderId.Value, ct);
            
            if (finalOrder.FilledQuantity > 0 && finalOrder.AverageFillPrice.HasValue)
            {
                _logger.LogWarning("[RECONCILE] GHOST SHARES DETECTED: Canceled {Side} order had {Qty} fills @ ${Price:N4}. Applying to state.",
                    finalOrder.Side, finalOrder.FilledQuantity, finalOrder.AverageFillPrice.Value);
                
                if (finalOrder.Side == BotOrderSide.Buy)
                {
                    // Use the existing fill handler for buys
                    await HandleOrderFilledAsync(finalOrder);
                }
                else // SELL
                {
                    // Handle partial sell fills (the 2:50 PM scenario)
                    await HandleSellPartialFillAsync(finalOrder);
                }
                
                _logger.LogInformation("[RECONCILE] State reconciled. Shares: {Shares}, Cash: ${Cash:N2}",
                    _state.CurrentShares, _state.AvailableCash);
            }
            else if (finalOrder.FilledQuantity == 0)
            {
                // No fills at all - rollback the reserved cash (only for buys)
                if (finalOrder.Side == BotOrderSide.Buy)
                {
                    _logger.LogInformation("[RECONCILE] No fills on canceled buy order. Rolling back reserved cash.");
                    await RollbackPendingOrderStateAsync();
                    // Activate buy cooldown to prevent rapid-fire retries
                    ActivateBuyCooldown(_pendingOrderSymbol == _settings.BearSymbol ? "BEAR" : "BULL");
                }
                else
                {
                    _logger.LogInformation("[RECONCILE] No fills on canceled sell order. No state changes needed.");
                }
            }
            else
            {
                // Partial fill but no price (shouldn't happen, but handle gracefully)
                _logger.LogWarning("[RECONCILE] Partial fill ({Qty}) but no fill price. Using order price for reconciliation.",
                    finalOrder.FilledQuantity);
                
                // Create a modified order with the effective price
                var fixedOrder = new BotOrder
                {
                    OrderId = finalOrder.OrderId,
                    Symbol = finalOrder.Symbol,
                    Side = finalOrder.Side,
                    Type = finalOrder.Type,
                    Status = finalOrder.Status,
                    Quantity = finalOrder.Quantity,
                    FilledQuantity = finalOrder.FilledQuantity,
                    AverageFillPrice = _pendingOrderEffectivePrice
                };
                
                if (finalOrder.Side == BotOrderSide.Buy)
                {
                    await HandleOrderFilledAsync(fixedOrder);
                }
                else
                {
                    await HandleSellPartialFillAsync(fixedOrder);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RECONCILE] CRITICAL: Failed to reconcile canceled order {OrderId}. State may be desynced!", 
                _pendingOrderId.Value);
            
            // Don't clear pending order on failure - let the next poll cycle try again
            return;
        }
        
        // Clear the pending order now that we've reconciled
        ClearPendingOrder();
    }
    
    /// <summary>
    /// Handle partial fills on canceled sell orders (ghost shares scenario).
    /// Updates state to reflect shares that were actually sold.
    /// </summary>
    private async Task HandleSellPartialFillAsync(BotOrder order)
    {
        if (!order.AverageFillPrice.HasValue || order.FilledQuantity <= 0) return;
        
        await _stateLock.WaitAsync();
        try
        {
            var proceeds = order.FilledQuantity * order.AverageFillPrice.Value;
            var soldCostBasis = order.FilledQuantity * (_state.AverageEntryPrice ?? order.AverageFillPrice.Value);
            
            // Apply profit/loss split allocation
            ApplySplitAllocation(proceeds, soldCostBasis);
            
            // Reduce share count
            _state.CurrentShares -= order.FilledQuantity;
            
            if (_state.CurrentShares <= 0)
            {
                _state.CurrentPosition = null;
                _state.CurrentShares = 0;
                _state.AverageEntryPrice = null;
                ClearTrailingStopState();
                _logger.LogInformation("[RECONCILE] Ghost sell fill cleared position completely.");
            }
            
            _stateManager.Save(_state);
            
            _logger.LogInformation("[RECONCILE] Sell partial fill applied: {Qty} @ ${Price:N4}. Shares remaining: {Shares}",
                order.FilledQuantity, order.AverageFillPrice.Value, _state.CurrentShares);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task HandleOrderFilledAsync(BotOrder order)
    {
        if (!order.AverageFillPrice.HasValue) return;
        
        await _stateLock.WaitAsync();
        try
        {
            var actualCost = order.FilledQuantity * order.AverageFillPrice.Value;
            var allocatedCost = _pendingOrderQuantity * _pendingOrderEffectivePrice;
            
            // Reconcile cash: Add back the difference between what we allocated (reserved) 
            // and what we actually spent. This includes:
            // 1. Savings from price improvement (EffectivePrice - FillPrice)
            // 2. Returned cash from unfilled shares (Quantity - FilledQuantity)
            var unusedCash = allocatedCost - actualCost;
            
            // FIX: Unused cash from the trade allocation goes back to AvailableCash, NOT Bank
            _state.AvailableCash += unusedCash;
            
            // WEIGHTED AVERAGE PRICE logic for partial fills (IOC + Market)
            if (_state.CurrentShares > 0 && _state.AverageEntryPrice.HasValue)
            {
                var existingCost = _state.CurrentShares * _state.AverageEntryPrice.Value;
                var totalShares = _state.CurrentShares + order.FilledQuantity;
                var totalCost = existingCost + actualCost;
                
                _state.AverageEntryPrice = totalCost / totalShares;
                _state.CurrentShares = totalShares;
            }
            else
            {
                 _state.CurrentShares = order.FilledQuantity;
                 _state.AverageEntryPrice = order.AverageFillPrice.Value;
            }

            _state.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
            _stateManager.Save(_state);
            
            // Slippage tracking (still based on base/quote price for metrics)
            var slippage = actualCost - (order.FilledQuantity * _pendingOrderBasePrice);
            
            _logger.LogInformation("[FILL] Buy confirmed: {Qty} @ ${Price:N4} (slippage: {Slip:+0.00;-0.00})",
                order.FilledQuantity, order.AverageFillPrice.Value, slippage);
            
            // Per-fill price delta: expected vs actual
            var expectedPrice = _pendingOrderBasePrice;
            var actualPrice = order.AverageFillPrice.Value;
            var perFillDelta = (actualPrice - expectedPrice) * order.FilledQuantity;
            _logger.LogInformation("[FILL DELTA] Expected: ${Expected:N4} | Actual: ${Actual:N4} | Impact: {Sign}${Delta:N2} ({Qty} shares)",
                expectedPrice, actualPrice,
                perFillDelta >= 0 ? "+" : "-", System.Math.Abs(perFillDelta),
                order.FilledQuantity);
            
            if (unusedCash > 0)
            {
                _logger.LogInformation("[CASH] Reconciled ${Amount:N2} to available balance (savings + unfilled)", unusedCash);
            }
            
            // Successful fill -> reset buy cooldown
            ResetBuyCooldown();
            
            // Track slippage
            AddSlippage(slippage);
            
            // Cleanup any orphaned shares from previous partial liquidation
            await CleanupOrphanedSharesAsync(CancellationToken.None);
        }
        finally
        {
            _stateLock.Release();
        }
        
        // Non-blocking equity check after buy fill
        FireEquityCheck($"BUY {order.FilledQuantity} {order.Symbol}");
    }
    
    private async Task RollbackPendingOrderStateAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            // Restore the cash we reserved for this order back to AvailableCash.
            // NEVER touch AccumulatedLeftover (Bank) - it's sacred.
            var allocatedCash = _pendingOrderQuantity * _pendingOrderEffectivePrice;
            _state.AvailableCash += allocatedCash;
            
            // FIX: Do NOT blindly wipe the position. 
            // If we acquired shares via IOC partial fill prior to this pending order failure,
            // we must preserve them. Only clear position if we truly hold nothing.
            if (_state.CurrentShares <= 0)
            {
                _state.CurrentPosition = null;
                _state.CurrentShares = 0; // Ensure explicit 0
            }
            
            _stateManager.Save(_state);
            
            _logger.LogInformation("[ROLLBACK] Order failed. Restored cash: ${Cash:N2}. Shares held: {Shares}", 
                allocatedCash, _state.CurrentShares);
        }
        finally
        {
            _stateLock.Release();
        }
    }
    
    private void ClearPendingOrder()
    {
        _pendingOrderId = null;
        _pendingOrderSymbol = null;
        _pendingOrderQuantity = 0;
        _pendingOrderBasePrice = 0m;
        _pendingOrderEffectivePrice = 0m;
        _pendingOrderSubmitTime = null;
    }
    
    /// <summary>
    /// Check if buy cooldown is active. If so, log and return true to skip the buy.
    /// Resets cooldown if the signal direction has changed since the last failure.
    /// </summary>
    private bool IsBuyCooldownActive(string direction)
    {
        // If the signal direction changed (e.g. BEAR -> BULL), reset cooldown
        if (_lastBuyFailureDirection != null && _lastBuyFailureDirection != direction)
        {
            ResetBuyCooldown();
        }
        
        if (_buyRetryCooldownUntil.HasValue && _currentUtc < _buyRetryCooldownUntil.Value)
        {
            // Throttle: only log once when cooldown starts suppressing buys (not every tick)
            if (!_buyCooldownLoggedStart)
            {
                var remaining = (_buyRetryCooldownUntil.Value - _currentUtc).TotalSeconds;
                _logger.LogWarning("[TRADER] Buy cooldown active ({Failures} consecutive failures). Retry in {Remaining:N0}s. Suppressing buy signals until {Until:HH:mm:ss}.",
                    _consecutiveBuyFailures, remaining, _buyRetryCooldownUntil.Value);
                _buyCooldownLoggedStart = true;
            }
            return true;
        }
        
        // Cooldown expired naturally — log once and reset
        if (_buyCooldownLoggedStart)
        {
            _logger.LogInformation("[TRADER] Buy cooldown expired. Resuming signal processing.");
            _buyCooldownLoggedStart = false;
        }
        return false;
    }
    
    /// <summary>
    /// Activate exponential backoff after a failed buy attempt.
    /// Cooldown = min(BuyRetryCooldownSeconds * 2^(failures-1), MaxBuyRetryCooldownSeconds)
    /// </summary>
    private void ActivateBuyCooldown(string direction)
    {
        _consecutiveBuyFailures++;
        _lastBuyFailureDirection = direction;
        var backoffSeconds = _settings.BuyRetryCooldownSeconds * (1 << Math.Min(_consecutiveBuyFailures - 1, 6));
        backoffSeconds = Math.Min(backoffSeconds, _settings.MaxBuyRetryCooldownSeconds);
        _buyRetryCooldownUntil = _currentUtc + TimeSpan.FromSeconds(backoffSeconds);
        _logger.LogWarning("[TRADER] Buy failed ({Failures} consecutive). Cooldown {Seconds}s until {Until:HH:mm:ss}.",
            _consecutiveBuyFailures, backoffSeconds, _buyRetryCooldownUntil.Value);
    }
    
    private void ResetBuyCooldown()
    {
        if (_consecutiveBuyFailures > 0)
        {
            _logger.LogInformation("[TRADER] Buy cooldown reset (was {Failures} consecutive failures).", _consecutiveBuyFailures);
        }
        _consecutiveBuyFailures = 0;
        _buyRetryCooldownUntil = null;
        _lastBuyFailureDirection = null;
        _buyCooldownLoggedStart = false;
    }
    
    private void EnterSafeMode(string reason)
    {
        _isSafeMode = true;
        _safeModeReason = reason;
        _logger.LogCritical("[SAFE MODE] ACTIVATED: {Reason}. Trading halted until manual intervention.", reason);
    }
    
    /// <summary>
    /// Enter REPAIR MODE - attempt automatic repair, backup state, then signal for shutdown.
    /// </summary>
    private async Task EnterRepairModeAsync(
        string errorCondition,
        int bullQty, int bearQty,
        string? localSymbol, long localShares,
        CancellationToken ct)
    {
        _logger.LogCritical("╔════════════════════════════════════════════════════════════════╗");
        _logger.LogCritical("║                       REPAIR MODE ACTIVATED                    ║");
        _logger.LogCritical("╚════════════════════════════════════════════════════════════════╝");
        _logger.LogCritical("");
        _logger.LogCritical("[REPAIR] Error Condition: {Condition}", errorCondition);
        _logger.LogCritical("[REPAIR] === Current State ===");
        _logger.LogCritical("[REPAIR]   Local state: {Symbol} x {Shares}", 
            string.IsNullOrEmpty(localSymbol) ? "(none)" : localSymbol, localShares);
        _logger.LogCritical("[REPAIR]   Broker {Bull}: {BullQty} shares", _settings.BullSymbol, bullQty);
        _logger.LogCritical("[REPAIR]   Broker {Bear}: {BearQty} shares", _settings.BearSymbol, bearQty);
        _logger.LogCritical("");
        
        var repairSuccess = true;
        
        try
        {
            // REPAIR: Liquidate any SHORT positions
            if (bullQty < 0 && !string.IsNullOrEmpty(_settings.BullSymbol))
            {
                _logger.LogWarning("[REPAIR] Covering SHORT position: {Symbol} x {Qty}", _settings.BullSymbol, bullQty);
                repairSuccess &= await RepairLiquidatePositionAsync(_settings.BullSymbol!, Math.Abs(bullQty), isCoveringShort: true, ct);
            }
            
            if (bearQty < 0 && !string.IsNullOrEmpty(_settings.BearSymbol))
            {
                _logger.LogWarning("[REPAIR] Covering SHORT position: {Symbol} x {Qty}", _settings.BearSymbol, bearQty);
                repairSuccess &= await RepairLiquidatePositionAsync(_settings.BearSymbol!, Math.Abs(bearQty), isCoveringShort: true, ct);
            }
            
            // REPAIR: Liquidate DUAL positions (both bull and bear)
            if (bullQty > 0 && bearQty > 0)
            {
                _logger.LogWarning("[REPAIR] Liquidating DUAL positions...");
                if (!string.IsNullOrEmpty(_settings.BullSymbol))
                {
                    _logger.LogWarning("[REPAIR]   Selling {Symbol} x {Qty}", _settings.BullSymbol, bullQty);
                    repairSuccess &= await RepairLiquidatePositionAsync(_settings.BullSymbol, bullQty, isCoveringShort: false, ct);
                }
                
                if (!string.IsNullOrEmpty(_settings.BearSymbol))
                {
                    _logger.LogWarning("[REPAIR]   Selling {Symbol} x {Qty}", _settings.BearSymbol, bearQty);
                    repairSuccess &= await RepairLiquidatePositionAsync(_settings.BearSymbol, bearQty, isCoveringShort: false, ct);
                }
            }
            
            // REPAIR: Symbol mismatch - liquidate broker position  
            if (bullQty > 0 && !string.IsNullOrEmpty(localSymbol) && localSymbol != _settings.BullSymbol && !string.IsNullOrEmpty(_settings.BullSymbol))
            {
                _logger.LogWarning("[REPAIR] Symbol mismatch - liquidating broker position: {Symbol} x {Qty}", _settings.BullSymbol, bullQty);
                repairSuccess &= await RepairLiquidatePositionAsync(_settings.BullSymbol!, bullQty, isCoveringShort: false, ct);
            }
            
            if (bearQty > 0 && !string.IsNullOrEmpty(localSymbol) && localSymbol != _settings.BearSymbol && !string.IsNullOrEmpty(_settings.BearSymbol))
            {
                _logger.LogWarning("[REPAIR] Symbol mismatch - liquidating broker position: {Symbol} x {Qty}", _settings.BearSymbol, bearQty);
                repairSuccess &= await RepairLiquidatePositionAsync(_settings.BearSymbol!, bearQty, isCoveringShort: false, ct);
            }
            
            // Note: Phantom position (local has shares, broker doesn't) is handled by rename below
            
            // Rename state file (will be recreated fresh on next startup)
            var renamedPath = RenameStateFile();
            if (!string.IsNullOrEmpty(renamedPath))
            {
                _logger.LogInformation("[REPAIR] State file renamed to: {Path}", renamedPath);
                _logger.LogInformation("[REPAIR] A fresh state will be created on next startup.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[REPAIR] Error during repair operations");
            repairSuccess = false;
        }
        
        _logger.LogCritical("");
        if (repairSuccess)
        {
            _logger.LogCritical("[REPAIR] ✓ Repair completed successfully. Bot will now exit.");
            _logger.LogCritical("[REPAIR]   Restart the bot to resume trading with clean state.");
        }
        else
        {
            _logger.LogCritical("[REPAIR] ✗ Repair encountered errors. Manual intervention may be needed.");
            _logger.LogCritical("[REPAIR]   Check broker account and resolve any remaining issues.");
        }
        _logger.LogCritical("");
        
        // Signal that repair mode was triggered - orchestrator should shut down
        _repairModeTriggered = true;
    }
    
    /// <summary>
    /// Liquidate a position during repair mode using market orders.
    /// </summary>
    private async Task<bool> RepairLiquidatePositionAsync(string symbol, int quantity, bool isCoveringShort, CancellationToken ct)
    {
        try
        {
            var side = isCoveringShort ? BotOrderSide.Buy : BotOrderSide.Sell;
            var sideText = isCoveringShort ? "BUY (cover)" : "SELL";
            
            _logger.LogInformation("[REPAIR] Submitting market {Side} order: {Symbol} x {Qty}", sideText, symbol, quantity);
            
            // Fetch current price for HintPrice so SimBroker fills realistically
            decimal? repairHintPrice = null;
            try { repairHintPrice = await _broker.GetLatestPriceAsync(symbol, ct); } catch { /* best effort */ }
            
            var request = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = quantity,
                Side = side,
                Type = BotOrderType.Market,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = _settings.GenerateClientOrderId(),
                HintPrice = repairHintPrice
            };
            
            var order = await _broker.SubmitOrderAsync(request);
            _logger.LogInformation("[REPAIR] Order submitted: {OrderId}", order.OrderId);
            
            // Wait for fill (with timeout)
            var timeout = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < timeout)
            {
                await Task.Delay(500, ct);
                var filledOrder = await _broker.GetOrderAsync(order.OrderId);
                
                if (filledOrder.Status == BotOrderStatus.Filled)
                {
                    _logger.LogInformation("[REPAIR] ✓ Order filled: {Symbol} x {Qty} @ ${Price:N2}", 
                        symbol, filledOrder.FilledQuantity, filledOrder.AverageFillPrice);
                    return true;
                }
                
                if (filledOrder.Status == BotOrderStatus.Canceled || filledOrder.Status == BotOrderStatus.Rejected)
                {
                    _logger.LogError("[REPAIR] ✗ Order {Status}: {Symbol}", filledOrder.Status, symbol);
                    return false;
                }
            }
            
            _logger.LogError("[REPAIR] ✗ Order timed out: {Symbol}", symbol);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[REPAIR] ✗ Failed to liquidate {Symbol}: {Message}", symbol, ex.Message);
            return false;
        }
    }
    
    /// <summary>
    /// Rename the trading_state.json file with timestamp for analysis.
    /// The file will be recreated fresh on next startup.
    /// </summary>
    private string? RenameStateFile()
    {
        try
        {
            var stateFilePath = _stateManager.StateFilePath;
            if (!File.Exists(stateFilePath))
            {
                _logger.LogWarning("[REPAIR] No state file to rename at {Path}", stateFilePath);
                return null;
            }
            
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var directory = Path.GetDirectoryName(stateFilePath) ?? ".";
            var fileName = Path.GetFileNameWithoutExtension(stateFilePath);
            var extension = Path.GetExtension(stateFilePath);
            var renamedPath = Path.Combine(directory, $"{fileName}_{timestamp}{extension}");
            
            File.Move(stateFilePath, renamedPath);
            return renamedPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[REPAIR] Failed to rename state file");
            return null;
        }
    }
    
    /// <summary>
    /// Verify local state matches broker positions on startup.
    /// Handles two cases:
    /// 1. Local state has position - verify broker agrees
    /// 2. Local state is empty - check if broker has orphan positions
    /// 
    /// If mismatch detected, enters REPAIR MODE to fix automatically.
    /// </summary>
    private async Task VerifyAndSyncBrokerStateAsync(CancellationToken ct)
    {
        _logger.LogInformation("[STARTUP] Verifying broker positions match local state...");
        
        int bullQty = 0;
        int bearQty = 0;
        decimal bullAvgPrice = 0m;
        decimal bearAvgPrice = 0m;
        
        try
        {
            // Get broker positions for both symbols
            if (!string.IsNullOrEmpty(_settings.BullSymbol))
            {
                var bullPos = await _broker.GetPositionAsync(_settings.BullSymbol, ct);
                if (bullPos is { } pos)
                {
                    bullQty = (int)pos.Quantity;
                    bullAvgPrice = pos.AverageEntryPrice;
                }
            }
            
            if (!string.IsNullOrEmpty(_settings.BearSymbol))
            {
                var bearPos = await _broker.GetPositionAsync(_settings.BearSymbol, ct);
                if (bearPos is { } pos)
                {
                    bearQty = (int)pos.Quantity;
                    bearAvgPrice = pos.AverageEntryPrice;
                }
            }
        }
        catch (Exception ex)
        {
            // Broker verification failed - cannot repair, just exit
            _logger.LogCritical("╔════════════════════════════════════════════════════════════════╗");
            _logger.LogCritical("║                  BROKER VERIFICATION FAILED                    ║");
            _logger.LogCritical("╚════════════════════════════════════════════════════════════════╝");
            _logger.LogCritical("");
            _logger.LogCritical("[STARTUP] Failed to verify broker positions: {Message}", ex.Message);
            _logger.LogCritical("[STARTUP] Cannot determine account state. Bot will exit.");
            _logger.LogCritical("[STARTUP] Please check:");
            _logger.LogCritical("[STARTUP]   - Network connectivity");
            _logger.LogCritical("[STARTUP]   - API key validity");
            _logger.LogCritical("[STARTUP]   - Broker API status");
            _logger.LogCritical("");
            
            _repairModeTriggered = true;
            return;
        }
        
        // What does local state say?
        var localSymbol = _state.CurrentShares > 0 ? _state.CurrentPosition : null;
        var localShares = _state.CurrentShares;
            
        // Check for SHORT positions - REPAIR: cover them
        if (bullQty < 0 || bearQty < 0)
        {
            var shortSymbol = bullQty < 0 ? _settings.BullSymbol : _settings.BearSymbol;
            var shortQty = bullQty < 0 ? bullQty : bearQty;
            await EnterRepairModeAsync(
                $"SHORT position detected: {shortSymbol} x {shortQty}",
                bullQty, bearQty, localSymbol, localShares, ct);
            return;
        }
        
        // Check for dual long positions - REPAIR: liquidate both
        if (bullQty > 0 && bearQty > 0)
        {
            await EnterRepairModeAsync(
                $"DUAL positions detected: {_settings.BullSymbol} x {bullQty} AND {_settings.BearSymbol} x {bearQty}",
                bullQty, bearQty, localSymbol, localShares, ct);
            return;
        }
        
        // Determine what broker actually has
        var brokerSymbol = bullQty > 0 ? _settings.BullSymbol : (bearQty > 0 ? _settings.BearSymbol : null);
        var brokerShares = bullQty > 0 ? bullQty : (bearQty > 0 ? bearQty : 0);
        var brokerAvgPrice = bullQty > 0 ? bullAvgPrice : (bearQty > 0 ? bearAvgPrice : 0m);
        
        // Case 1: Both agree we have no position
        if (string.IsNullOrEmpty(brokerSymbol) && string.IsNullOrEmpty(localSymbol))
        {
            _logger.LogInformation("[STARTUP] ✓ Broker and local state agree: no position");
            return;
        }
        
        // Case 2: Both agree on position
        if (brokerSymbol == localSymbol && brokerShares == localShares)
        {
            _logger.LogInformation("[STARTUP] ✓ Broker and local state agree: {Symbol} x {Shares}", brokerSymbol, brokerShares);
            return;
        }
        
        // Case 3: LOCAL has position but BROKER doesn't (phantom position) - REPAIR: clear local state
        if (!string.IsNullOrEmpty(localSymbol) && localShares > 0 && string.IsNullOrEmpty(brokerSymbol))
        {
            await EnterRepairModeAsync(
                $"PHANTOM position: local says {localSymbol} x {localShares} but broker has nothing",
                bullQty, bearQty, localSymbol, localShares, ct);
            return;
        }
        
        // Case 4: BROKER has position but LOCAL doesn't (orphan on broker) - sync with broker's cost basis
        if (!string.IsNullOrEmpty(brokerSymbol) && brokerShares > 0 && (string.IsNullOrEmpty(localSymbol) || localShares == 0))
        {
            _logger.LogWarning("[STATE MISMATCH] Local state is empty but broker has {Symbol} x {Shares}. Syncing state.",
                brokerSymbol, brokerShares);
            
            var totalCostBasis = brokerShares * brokerAvgPrice;
            
            _logger.LogWarning(
                "[SYNC] ORPHAN POSITION FIX (STARTUP): Adopting broker position. " +
                "Using broker avg price ${BrokerAvg:F2} as truth. Total cost basis: ${CostBasis:F2}",
                brokerAvgPrice, totalCostBasis);
            
            await _stateLock.WaitAsync(ct);
            try
            {
                _state.CurrentPosition = brokerSymbol;
                _state.CurrentShares = brokerShares;
                _state.AverageEntryPrice = brokerAvgPrice;
                _state.AvailableCash = 0m;
                _state.AccumulatedLeftover = _state.StartingAmount - totalCostBasis;
                _stateManager.Save(_state);
                
                _logger.LogWarning(
                    "[SYNC] After orphan fix: Position={Symbol} x {Shares}, AvgPrice=${AvgPrice:F2}, AccumulatedLeftover=${Leftover:F2}",
                    _state.CurrentPosition, _state.CurrentShares, _state.AverageEntryPrice, _state.AccumulatedLeftover);
            }
            finally
            {
                _stateLock.Release();
            }
            
            _logger.LogInformation("[STARTUP] State synced from broker: {Symbol} x {Shares}", brokerSymbol, brokerShares);
            return;
        }
        
        // Case 5: Both have positions but they DISAGREE on symbol - REPAIR: liquidate broker position
        if (brokerSymbol != localSymbol)
        {
            await EnterRepairModeAsync(
                $"SYMBOL mismatch: local says {localSymbol} x {localShares} but broker has {brokerSymbol} x {brokerShares}",
                bullQty, bearQty, localSymbol, localShares, ct);
            return;
        }
        
        // Case 6: Same symbol but DISAGREE on quantity - sync with cost basis accounting
        if (brokerShares != localShares)
        {
            _logger.LogWarning("[STATE MISMATCH] Quantity mismatch for {Symbol}: local={Local}, broker={Broker}. Syncing to broker.",
                brokerSymbol, localShares, brokerShares);
            
            await _stateLock.WaitAsync(ct);
            try
            {
                if (brokerShares > localShares)
                {
                    // GHOST SHARES: Broker has MORE than we tracked
                    // These shares have cost basis but we didn't record the spend
                    var ghostQty = brokerShares - localShares;
                    var ghostCostBasis = ghostQty * brokerAvgPrice;
                    
                    _logger.LogWarning(
                        "[SYNC] GHOST SHARE FIX (STARTUP): Found {GhostQty} untracked shares. " +
                        "Using broker avg price ${BrokerAvg:F2} as truth. Ghost cost basis: ${GhostCost:F2}",
                        ghostQty, brokerAvgPrice, ghostCostBasis);
                    
                    // Deduct ghost cost from available cash (we spent money we didn't track)
                    var cashBeforeDeduction = _state.AvailableCash;
                    _state.AvailableCash = Math.Max(0m, _state.AvailableCash - ghostCostBasis);
                    
                    // Warn if deduction exhausted available cash - indicates deeper sync issue
                    if (cashBeforeDeduction - ghostCostBasis < 0)
                    {
                        _logger.LogWarning(
                            "[SYNC] ⚠️ Warning: Ghost share deduction exhausted available cash. " +
                            "Tried to deduct ${GhostCost:F2} from ${Cash:F2}. Account accounting may be desynchronized.",
                            ghostCostBasis, cashBeforeDeduction);
                    }
                    
                    // Use broker's average entry price as source of truth
                    _state.AverageEntryPrice = brokerAvgPrice;
                    
                    _logger.LogWarning(
                        "[SYNC] After ghost fix: AvailableCash=${Cash:F2}, AverageEntryPrice=${AvgPrice:F2}",
                        _state.AvailableCash, _state.AverageEntryPrice);
                }
                else
                {
                    // MISSING SHARES FIX (STARTUP): Broker has LESS than tracked.
                    // Shares were sold but we didn't record it.
                    // Try to get actual fill prices from recent orders.
                    var missingQty = localShares - brokerShares;
                    
                    var costBasisPrice = _state.AverageEntryPrice ?? brokerAvgPrice;
                    var costBasis = missingQty * costBasisPrice;
                    
                    decimal fillPrice;
                    bool usedActualFills = false;
                    
                    try
                    {
                        // Query fills from last hour (startup sync may cover longer period)
                        var recentFills = await _broker.GetRecentFilledOrdersAsync(
                            brokerSymbol!, 
                            DateTime.UtcNow.AddHours(-1), 
                            CancellationToken.None);
                        
                        var recentSells = recentFills
                            .Where(o => o.Side == BotOrderSide.Sell && o.AverageFillPrice > 0)
                            .ToList();
                        
                        if (recentSells.Count > 0)
                        {
                            var totalFilledQty = recentSells.Sum(o => o.FilledQuantity);
                            var totalValue = recentSells.Sum(o => o.FilledQuantity * o.AverageFillPrice!.Value);
                            
                            if (totalFilledQty > 0 && totalValue > 0)
                            {
                                fillPrice = totalValue / totalFilledQty;
                                usedActualFills = true;
                            }
                            else
                            {
                                fillPrice = await _broker.GetLatestPriceAsync(brokerSymbol!);
                            }
                        }
                        else
                        {
                            fillPrice = await _broker.GetLatestPriceAsync(brokerSymbol!);
                        }
                    }
                    catch
                    {
                        try { fillPrice = await _broker.GetLatestPriceAsync(brokerSymbol!); }
                        catch { fillPrice = costBasisPrice; }
                    }
                    
                    var proceeds = missingQty * fillPrice;
                    ApplySplitAllocation(proceeds, costBasis);
                    
                    if (usedActualFills)
                    {
                        _logger.LogWarning(
                            "[SYNC] MISSING SHARE FIX (STARTUP): Broker has {MissingQty} fewer shares than tracked. " +
                            "Proceeds ${Proceeds:N2} vs cost ${Cost:N2} (actual fill @ ${Price:N4}).",
                            missingQty, proceeds, costBasis, fillPrice);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "[SYNC] MISSING SHARE FIX (STARTUP): Broker has {MissingQty} fewer shares than tracked. " +
                            "Est. proceeds ${Proceeds:N2} vs cost ${Cost:N2} (market @ ${Price:N4}). " +
                            "⚠ Using ESTIMATED fill price — actual broker fill may differ. Check equity reconciliation.",
                            missingQty, proceeds, costBasis, fillPrice);
                    }
                    
                    // Use broker's average entry price as source of truth for remaining shares
                    if (brokerShares > 0)
                    {
                        _state.AverageEntryPrice = brokerAvgPrice;
                        _logger.LogWarning("[SYNC] Updated AvgEntryPrice to broker's: ${AvgPrice:N4}", brokerAvgPrice);
                    }
                }
                
                _state.CurrentShares = brokerShares;
                _stateManager.Save(_state);
            }
            finally
            {
                _stateLock.Release();
            }
            
            _logger.LogInformation("[STARTUP] State synced from broker: {Symbol} x {Shares}", brokerSymbol, brokerShares);
            return;
        }
    }
    
    /// <summary>
    /// Periodic position verification to detect phantom positions mid-session.
    /// This catches cases where the bot thinks it has shares but broker has nothing.
    /// Called every BrokerSyncIntervalSeconds during trading loop.
    /// </summary>
    private async Task PeriodicPositionVerificationAsync(CancellationToken ct)
    {
        // Skip if we don't think we have a position
        if (string.IsNullOrEmpty(_state.CurrentPosition) || _state.CurrentShares <= 0)
            return;
        
        try
        {
            var symbol = _state.CurrentPosition;
            var localShares = _state.CurrentShares;
            
            var position = await _broker.GetPositionAsync(symbol, ct);
            var brokerShares = (int)(position?.Quantity ?? 0);
            
            // Check for phantom position (local thinks we have shares, broker says no)
            if (brokerShares == 0 && localShares > 0)
            {
                _logger.LogCritical("[PERIODIC SYNC] PHANTOM POSITION DETECTED: " +
                    "Local state says {Symbol} x {LocalShares} but broker has NOTHING. " +
                    "Position was likely sold by broker. Reconciling with P/L tracking.",
                    symbol, localShares);
                
                // Try to get the current market price for a best-effort P/L estimate
                decimal estimatedFillPrice;
                try { estimatedFillPrice = await _broker.GetLatestPriceAsync(symbol); }
                catch { estimatedFillPrice = _state.AverageEntryPrice ?? 0m; }
                
                await _stateLock.WaitAsync(ct);
                try
                {
                    var costBasis = (_state.AverageEntryPrice ?? 0m) * localShares;
                    var estimatedProceeds = estimatedFillPrice * localShares;
                    
                    // Use ApplySplitAllocation to properly track P/L
                    ApplySplitAllocation(estimatedProceeds, costBasis);
                    
                    _state.CurrentPosition = null;
                    _state.CurrentShares = 0;
                    _state.AverageEntryPrice = null;
                    ClearTrailingStopState();
                    _stateManager.Save(_state);
                    
                    _logger.LogWarning("[PERIODIC SYNC] Cleared phantom position. " +
                        "Estimated proceeds ${Proceeds:N2} vs cost ${Cost:N2} (using market price ${Price:N4}). " +
                        "⚠ ESTIMATED fill — actual broker fill may differ.",
                        estimatedProceeds, costBasis, estimatedFillPrice);
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            // Check for quantity mismatch
            else if (brokerShares != localShares && brokerShares > 0)
            {
                _logger.LogWarning("[PERIODIC SYNC] Quantity mismatch: local={Local}, broker={Broker}. Syncing to broker.",
                    localShares, brokerShares);
                
                await _stateLock.WaitAsync(ct);
                try
                {
                    if (brokerShares < localShares)
                    {
                        // Broker has fewer - shares were sold without our tracking
                        // Try to get actual fill prices from recent orders
                        var missingQty = localShares - brokerShares;
                        var costBasis = missingQty * (_state.AverageEntryPrice ?? 0m);
                        
                        decimal fillPrice;
                        bool usedActualFills = false;
                        
                        try
                        {
                            // Query fills from last 5 minutes to capture recent untracked sells
                            var recentFills = await _broker.GetRecentFilledOrdersAsync(
                                symbol, 
                                DateTime.UtcNow.AddMinutes(-5), 
                                ct);
                            
                            // Look for sell orders that account for the missing shares
                            var recentSells = recentFills
                                .Where(o => o.Side == BotOrderSide.Sell && o.AverageFillPrice > 0)
                                .ToList();
                            
                            if (recentSells.Count > 0)
                            {
                                // Calculate weighted average fill price from actual fills
                                var totalFilledQty = recentSells.Sum(o => o.FilledQuantity);
                                var totalValue = recentSells.Sum(o => o.FilledQuantity * o.AverageFillPrice!.Value);
                                
                                if (totalFilledQty > 0 && totalValue > 0)
                                {
                                    fillPrice = totalValue / totalFilledQty;
                                    usedActualFills = true;
                                    _logger.LogInformation(
                                        "[PERIODIC SYNC] Found {Count} recent sell fill(s) totaling {Qty} shares @ avg ${Price:N4}",
                                        recentSells.Count, totalFilledQty, fillPrice);
                                }
                                else
                                {
                                    fillPrice = await _broker.GetLatestPriceAsync(symbol);
                                }
                            }
                            else
                            {
                                fillPrice = await _broker.GetLatestPriceAsync(symbol);
                            }
                        }
                        catch
                        {
                            // Fallback to market price if fill query fails
                            try { fillPrice = await _broker.GetLatestPriceAsync(symbol); }
                            catch { fillPrice = _state.AverageEntryPrice ?? 0m; }
                        }
                        
                        var proceeds = missingQty * fillPrice;
                        ApplySplitAllocation(proceeds, costBasis);
                        
                        if (usedActualFills)
                        {
                            _logger.LogWarning(
                                "[PERIODIC SYNC] Credited ${Proceeds:N2} for {Missing} missing shares (actual fill @ ${Price:N4}).",
                                proceeds, missingQty, fillPrice);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "[PERIODIC SYNC] Credited ${Proceeds:N2} for {Missing} missing shares (est. fill @ ${Price:N4}). " +
                                "⚠ ESTIMATED fill — actual broker fill may differ.",
                                proceeds, missingQty, fillPrice);
                        }
                    }
                    else
                    {
                        // Broker has more - deduct the extra shares' cost basis
                        var extraQty = brokerShares - localShares;
                        var brokerAvgPrice = position?.AverageEntryPrice ?? _state.AverageEntryPrice ?? 0m;
                        var deductAmount = extraQty * brokerAvgPrice;
                        _state.AvailableCash -= deductAmount;
                        _state.AverageEntryPrice = brokerAvgPrice;
                        _logger.LogWarning("[PERIODIC SYNC] Deducted ${Deduct:N2} for {Extra} ghost shares.",
                            deductAmount, extraQty);
                    }
                    
                    _state.CurrentShares = brokerShares;
                    _stateManager.Save(_state);
                }
                finally
                {
                    _stateLock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PERIODIC SYNC] Failed to verify position - broker may not have position.");
            // Not critical - we'll catch issues when we try to trade
        }
    }
    
    /// <summary>
    /// Verify no opposite position exists on broker before buying.
    /// This is a SAFEGUARD to prevent dual holdings when state/broker are out of sync.
    /// </summary>
    /// <returns>True if safe to proceed with buy, false if buy should be blocked.</returns>
    private async Task<bool> VerifyNoOppositePositionAsync(string oppositeSymbol, CancellationToken ct)
    {
        try
        {
            var oppositePos = await _broker.GetPositionAsync(oppositeSymbol, ct);
            
            if (oppositePos is not { } pos || pos.Quantity == 0)
            {
                return true; // No opposite position, safe to proceed
            }
            
            if (pos.Quantity > 0)
            {
                // LONG opposite position exists - attempt emergency liquidation
                _logger.LogWarning("[SAFEGUARD] Opposite position {Symbol} still exists ({Qty} shares). Syncing state and attempting liquidation...",
                    oppositeSymbol, pos.Quantity);
                
                // Sync local state from broker
                await _stateLock.WaitAsync(ct);
                try
                {
                    _state.CurrentPosition = oppositeSymbol;
                    _state.CurrentShares = pos.Quantity;
                    _stateManager.Save(_state);
                }
                finally
                {
                    _stateLock.Release();
                }
                
                // Attempt emergency liquidation
                if (!await LiquidateCurrentPositionAsync(ct))
                {
                    _logger.LogError("[SAFEGUARD] Emergency liquidation of {Symbol} failed. Aborting buy to prevent dual holdings.", oppositeSymbol);
                    return false;
                }
                
                // Verify liquidation succeeded
                var verifyPos = await _broker.GetPositionAsync(oppositeSymbol, ct);
                if (verifyPos is { } vp && vp.Quantity > 0)
                {
                    _logger.LogError("[SAFEGUARD] {Symbol} still has {Qty} shares after emergency liquidation. Aborting.",
                        oppositeSymbol, vp.Quantity);
                    return false;
                }
                
                _logger.LogInformation("[SAFEGUARD] Emergency liquidation of {Symbol} successful. Proceeding with buy.", oppositeSymbol);
                return true;
            }
            else
            {
                // SHORT position exists - this bot doesn't manage shorts
                _logger.LogError("[SAFEGUARD] SHORT position detected: {Symbol} has {Qty} shares. This bot does not manage short positions. Aborting buy.",
                    oppositeSymbol, pos.Quantity);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SAFEGUARD] Failed to verify opposite position. Proceeding with caution.");
            return true; // Allow buy on verification failure to avoid blocking legitimate trades
        }
    }
    
    /// <summary>
    /// Thread-safe state save helper.
    /// </summary>
    private void SaveStateSafe()
    {
        _stateLock.Wait();
        try
        {
            _stateManager.Save(_state);
        }
        finally
        {
            _stateLock.Release();
        }
    }
    
    /// <summary>
    /// Thread-safe async state save helper.
    /// </summary>
    private async Task SaveStateSafeAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            _stateManager.Save(_state);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task ProcessRegimeAsync(MarketRegime regime, CancellationToken ct)
    {
        // Check for stale data (skip in replay mode — regimes carry historical timestamps)
        if (!_settings.BypassMarketHoursCheck && regime.IsStale(MarketRegime.DefaultStaleThreshold))
        {
            _logger.LogWarning("[TRADER] Stale regime data ({Age}s old). Ignoring.",
                (_currentUtc - regime.TimestampUtc).TotalSeconds);
            return;
        }

        var signal = regime.Signal;
        var price = regime.BenchmarkPrice;
        
        // Cache regime for slope access (used by trim logic)
        _lastRegime = regime;
        
        // TRIM CHECK (replaces old Take Profit logic)
        // Trimming sells a portion of a winning position when momentum fades
        // SKIP for MeanReversion — MR holds full position until %B exit signal
        if (_settings.EnableTrimming && _state.CurrentShares > 0 && 
            !string.IsNullOrEmpty(_state.CurrentPosition) && _state.AverageEntryPrice.HasValue
            && regime.ActiveStrategy != StrategyMode.MeanReversion)
        {
            await CheckAndExecuteTrimAsync(regime, ct);
        }
        
        // Track day start balance for P/L calculations
        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
        var todayStr = easternNow.ToString("yyyy-MM-dd");
        if (_lastDayStr != todayStr)
        {
            // DAY BOUNDARY: Reset session P/L for new trading day
            if (_state.CurrentTradingDay != todayStr)
            {
                await _stateLock.WaitAsync();
                try
                {
                    _logger.LogInformation("[DAY RESET] New trading day. SessionPnL ${Old:N2} -> $0.00", _state.RealizedSessionPnL);
                    _state.RealizedSessionPnL = 0m;
                    _state.CurrentTradingDay = todayStr;
                    _state.LastTrimTime = null; // Reset trim cooldown
                    _state.HaltReason = HaltReason.None; // Reset halt state
                    _state.PhResumeArmed = false;
                    _dailyTargetArmed = false;
                    _dailyTargetPeakPnL = 0m;
                    _dailyTargetStopLevel = 0m;
                    _state.DailyTargetArmed = false;
                    _state.DailyTargetPeakPnL = null;
                    _state.DailyTargetStopLevel = null;
                    _lastDirectionSwitchTime = null; // Reset direction switch cooldown
                    _stateManager.Save(_state);
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            
            var currentBalance = _state.AvailableCash + _state.AccumulatedLeftover;
            decimal? brokerStartEquity = null;
            
            if (_state.CurrentShares > 0 && !string.IsNullOrEmpty(_state.CurrentPosition))
            {
                // Get actual ETF price, not benchmark price
                decimal etfPrice;
                if (_state.CurrentPosition == _settings.BullSymbol && regime.BullPrice.HasValue)
                    etfPrice = regime.BullPrice.Value;
                else if (_state.CurrentPosition == _settings.BearSymbol && regime.BearPrice.HasValue)
                    etfPrice = regime.BearPrice.Value;
                else
                    etfPrice = await _broker.GetLatestPriceAsync(_state.CurrentPosition);

                currentBalance += _state.CurrentShares * etfPrice;
            }
            
            // Also capture broker's actual equity at day start for reconciliation
            try
            {
                brokerStartEquity = await _broker.GetEquityAsync();
            }
            catch { /* best effort - SimBroker may not have data yet */ }
            
            await _stateLock.WaitAsync();
            try
            {
                _state.DayStartBalance = currentBalance;
                if (brokerStartEquity.HasValue)
                    _state.BrokerDayStartEquity = brokerStartEquity.Value;
                _state.DayStartDate = todayStr;
                _stateManager.Save(_state);
            }
            finally
            {
                _stateLock.Release();
            }
            
            _lastDayStr = todayStr;
            _logger.LogInformation("[TRADER] New trading day. Day start balance: ${Balance:N2}{BrokerEquity}", 
                currentBalance,
                brokerStartEquity.HasValue ? $" (broker equity: ${brokerStartEquity.Value:N2})" : "");
        }
        
        // Periodic status logging (based on configured interval, or on signal change)
        var shouldLog = (_currentUtc - _lastStatusLogTime).TotalSeconds >= _settings.StatusLogIntervalSeconds;
        var signalChanged = signal != _lastLoggedSignal;
        if (shouldLog || signalChanged)
        {
            await LogStatusAsync(regime, signal, ct);
            _lastStatusLogTime = _currentUtc;
            _lastLoggedSignal = signal;
        }
        
        // Check trailing stop — SKIP for MeanReversion (MR uses its own hard stop + BB exit)
        var latchBlocks = false;
        if (regime.ActiveStrategy != StrategyMode.MeanReversion)
        {
            var (modifiedSignal, latch) = EvaluateTrailingStop(signal, price, regime.UpperBand, regime.LowerBand, regime.SmaValue, regime.BullPrice, regime.BearPrice);
            signal = modifiedSignal;
            latchBlocks = latch;
        }
        
        // --- MR HARD STOP (ATR-based or fixed %) ---
        // For MeanReversion trades, use dynamic ATR-based stop when available,
        // falling back to fixed MeanRevStopPercent. ATR stop operates in QQQ (benchmark) space
        // to avoid leverage conversion issues.
        if (regime.ActiveStrategy == StrategyMode.MeanReversion 
            && _state.CurrentShares > 0 
            && _state.AverageEntryPrice.HasValue)
        {
            bool shouldStop = false;
            
            // Prefer ATR-based stop when ATR is available and multiplier > 0
            if (_settings.MrAtrStopMultiplier > 0 && regime.Atr.HasValue && regime.Atr.Value > 0 && _mrEntryBenchmarkPrice > 0)
            {
                decimal atrStopDistance = regime.Atr.Value * _settings.MrAtrStopMultiplier;
                decimal benchmarkMove = _mrEntryIsLong 
                    ? _mrEntryBenchmarkPrice - regime.BenchmarkPrice   // MR_LONG: QQQ dropping hurts
                    : regime.BenchmarkPrice - _mrEntryBenchmarkPrice;  // MR_SHORT: QQQ rising hurts
                
                if (benchmarkMove > atrStopDistance)
                {
                    _logger.LogWarning(
                        "[MR ATR STOP] Benchmark moved {Move:F4} against position (ATR stop: {AtrStop:F4} = {Mult:F1} x ATR {Atr:F4}). Forcing exit.",
                        benchmarkMove, atrStopDistance, _settings.MrAtrStopMultiplier, regime.Atr.Value);
                    shouldStop = true;
                }
            }
            else if (_settings.MeanRevStopPercent > 0)
            {
                // Fallback: fixed percentage stop on ETF price
                decimal currentEtfPrice;
                if (_state.CurrentPosition == _settings.BullSymbol && regime.BullPrice.HasValue)
                    currentEtfPrice = regime.BullPrice.Value;
                else if (_state.CurrentPosition == _settings.BearSymbol && regime.BearPrice.HasValue)
                    currentEtfPrice = regime.BearPrice.Value;
                else
                    currentEtfPrice = 0m;
                
                if (currentEtfPrice > 0)
                {
                    var unrealizedPct = (currentEtfPrice - _state.AverageEntryPrice.Value) / _state.AverageEntryPrice.Value;
                    if (unrealizedPct < -_settings.MeanRevStopPercent)
                    {
                        _logger.LogWarning(
                            "[MR HARD STOP] Unrealized loss {Loss:P2} exceeds MR stop {Stop:P2}. Forcing exit.",
                            unrealizedPct, _settings.MeanRevStopPercent);
                        shouldStop = true;
                    }
                }
            }
            
            if (shouldStop)
            {
                await EnsureNeutralAsync("MR hard stop triggered", ct);
                _mrHardStopCooldown = true;
                _mrEntryBenchmarkPrice = 0m;
                _logger.LogInformation("[MR] Hard-stop cooldown active — waiting for MR_FLAT before re-entry.");
                return;
            }
        }

        // Daily Profit Target — real-time mode: check realized + unrealized P/L every tick
        if (_state.HaltReason == HaltReason.None && _settings.DailyProfitTargetRealtime)
        {
            var effectiveTarget = _settings.EffectiveDailyProfitTarget;
            if (effectiveTarget > 0)
            {
                // Calculate combined P/L (realized + unrealized)
                decimal combinedPnL = _state.RealizedSessionPnL;
                decimal? currentEtfPrice = null;
                if (_state.CurrentShares > 0 && _state.AverageEntryPrice.HasValue)
                {
                    if (_state.CurrentPosition == _settings.BullSymbol && regime.BullPrice.HasValue)
                        currentEtfPrice = regime.BullPrice.Value;
                    else if (_state.CurrentPosition == _settings.BearSymbol && regime.BearPrice.HasValue)
                        currentEtfPrice = regime.BearPrice.Value;
                    if (currentEtfPrice.HasValue)
                        combinedPnL += (currentEtfPrice.Value - _state.AverageEntryPrice.Value) * _state.CurrentShares;
                }

                // Phase 1: Arm the trailing stop when target is first reached
                if (!_dailyTargetArmed && combinedPnL >= effectiveTarget)
                {
                    _dailyTargetArmed = true;
                    _dailyTargetPeakPnL = combinedPnL;
                    
                    if (_settings.DailyProfitTargetTrailingStopPercent > 0)
                    {
                        // Trailing stop mode: arm the stop but keep trading
                        _dailyTargetStopLevel = combinedPnL * (1m - _settings.DailyProfitTargetTrailingStopPercent / 100m);
                        _state.DailyTargetArmed = true;
                        _state.DailyTargetPeakPnL = _dailyTargetPeakPnL;
                        _state.DailyTargetStopLevel = _dailyTargetStopLevel;
                        _stateManager.Save(_state);
                        _logger.LogInformation(
                            "[DAILY TARGET] ★ P/L ${Combined:N2} reached target ${Target:N2}. Trailing stop ARMED at ${Stop:N2} ({Pct}% trail). Continuing to trade.",
                            combinedPnL, effectiveTarget, _dailyTargetStopLevel, _settings.DailyProfitTargetTrailingStopPercent);
                    }
                    else
                    {
                        // Legacy mode: immediate liquidation
                        SetHaltReason(HaltReason.ProfitTarget, "Daily profit target reached (real-time, legacy)");
                        _logger.LogInformation(
                            "[DAILY TARGET] ★ P/L ${Combined:N2} reached target ${Target:N2}. Liquidating now.",
                            combinedPnL, effectiveTarget);
                        await EnsureNeutralAsync("Daily profit target reached (real-time)", ct, currentEtfPrice);
                        return;
                    }
                }
                
                // Phase 2: Update trailing stop peak and check for stop trigger
                if (_dailyTargetArmed && _state.HaltReason == HaltReason.None)
                {
                    // Update peak P/L (ratchet up only)
                    if (combinedPnL > _dailyTargetPeakPnL)
                    {
                        _dailyTargetPeakPnL = combinedPnL;
                        var newStopLevel = combinedPnL * (1m - _settings.DailyProfitTargetTrailingStopPercent / 100m);
                        if (newStopLevel > _dailyTargetStopLevel)
                        {
                            _dailyTargetStopLevel = newStopLevel;
                            _state.DailyTargetPeakPnL = _dailyTargetPeakPnL;
                            _state.DailyTargetStopLevel = _dailyTargetStopLevel;
                            _stateManager.Save(_state);
                        }
                    }
                    
                    // Check if P/L dropped below trailing stop level
                    if (combinedPnL <= _dailyTargetStopLevel)
                    {
                        SetHaltReason(HaltReason.ProfitTarget, "Daily profit target trailing stop triggered");
                        _logger.LogInformation(
                            "[DAILY TARGET] ★ Trailing stop TRIGGERED. P/L ${Combined:N2} dropped below stop ${Stop:N2} (peak was ${Peak:N2}). Liquidating.",
                            combinedPnL, _dailyTargetStopLevel, _dailyTargetPeakPnL);
                        await EnsureNeutralAsync("Daily profit target trailing stop triggered", ct, currentEtfPrice);
                        return;
                    }
                }
            }
        }

        // Daily Profit Target — post-trade mode (non-realtime): arm/check after realized P/L changes
        if (_state.HaltReason == HaltReason.None && !_settings.DailyProfitTargetRealtime)
        {
            var effectiveTarget = _settings.EffectiveDailyProfitTarget;
            if (effectiveTarget > 0 && _dailyTargetArmed)
            {
                // Trailing stop is armed — track realized P/L against stop level
                if (_state.RealizedSessionPnL > _dailyTargetPeakPnL)
                {
                    _dailyTargetPeakPnL = _state.RealizedSessionPnL;
                    var newStopLevel = _state.RealizedSessionPnL * (1m - _settings.DailyProfitTargetTrailingStopPercent / 100m);
                    if (newStopLevel > _dailyTargetStopLevel)
                    {
                        _dailyTargetStopLevel = newStopLevel;
                        _state.DailyTargetPeakPnL = _dailyTargetPeakPnL;
                        _state.DailyTargetStopLevel = _dailyTargetStopLevel;
                        _stateManager.Save(_state);
                    }
                }
                
                if (_state.RealizedSessionPnL <= _dailyTargetStopLevel)
                {
                    SetHaltReason(HaltReason.ProfitTarget, "Post-trade daily target trailing stop triggered");
                    _logger.LogInformation(
                        "[DAILY TARGET] ★ Post-trade trailing stop TRIGGERED. Realized P/L ${Realized:N2} dropped below stop ${Stop:N2} (peak was ${Peak:N2}).",
                        _state.RealizedSessionPnL, _dailyTargetStopLevel, _dailyTargetPeakPnL);
                }
            }
        }

        // Daily Profit Target — stop trading once triggered (also covers daily loss limit)
        if (_state.HaltReason != HaltReason.None && signal != "MARKET_CLOSE")
        {
            if (_state.CurrentShares > 0)
            {
                _logger.LogInformation("[DAILY LIMIT] Liquidating remaining position (Session P/L: ${Session:N2}).",
                    _state.RealizedSessionPnL);
                await EnsureNeutralAsync("Daily limit reached", ct);
            }
            return;
        }

        // Execute based on signal
        switch (signal)
        {
            case "MARKET_CLOSE":
                await HandleMarketCloseAsync(regime, ct);
                break;

            case "BULL" when !latchBlocks:
                _neutralDetectionTime = null;
                await EnsureBullPositionAsync(regime, ct);
                break;

            case "BEAR" when !latchBlocks:
                _neutralDetectionTime = null;
                if (_settings.BullOnlyMode)
                {
                    await EnsureNeutralAsync("BEAR (bull-only mode)", ct);
                }
                else
                {
                    await EnsureBearPositionAsync(regime, ct);
                }
                break;

            case "NEUTRAL":
                await HandleNeutralAsync(regime, ct);
                break;
            
            // --- MEAN REVERSION SIGNALS ---
            case "MR_LONG" when !latchBlocks:
                if (_mrHardStopCooldown)
                {
                    // Skip — waiting for MR_FLAT to reset the cycle after a hard stop
                    break;
                }
                _neutralDetectionTime = null;
                _mrEntryBenchmarkPrice = regime.BenchmarkPrice;
                _mrEntryIsLong = true;
                await EnsureBullPositionAsync(regime, ct);
                break;
            
            case "MR_SHORT" when !latchBlocks:
                if (_mrHardStopCooldown)
                {
                    // Skip — waiting for MR_FLAT to reset the cycle after a hard stop
                    break;
                }
                _neutralDetectionTime = null;
                _mrEntryBenchmarkPrice = regime.BenchmarkPrice;
                _mrEntryIsLong = false;
                if (_settings.BullOnlyMode)
                {
                    await EnsureNeutralAsync("MR_SHORT (bull-only mode)", ct);
                }
                else
                {
                    await EnsureBearPositionAsync(regime, ct);
                }
                break;
            
            case "MR_FLAT":
                // MR_FLAT = immediate exit (no patience timer like NEUTRAL)
                if (_state.CurrentShares > 0)
                {
                    _logger.LogInformation("[MR] Mean reversion target reached (%B at midline). Exiting position.");
                    await EnsureNeutralAsync("MR mean reversion complete", ct);
                }
                if (_mrHardStopCooldown)
                {
                    _mrHardStopCooldown = false;
                    _logger.LogInformation("[MR] Hard-stop cooldown cleared — MR_FLAT received, ready for new trades.");
                }
                _mrEntryBenchmarkPrice = 0m;
                _neutralDetectionTime = null;
                break;
        }

        _lastProcessedSignal = signal;
    }

    private (string signal, bool latchBlocks) EvaluateTrailingStop(string signal, decimal price, decimal upperBand, decimal lowerBand, decimal smaValue, decimal? bullPrice = null, decimal? bearPrice = null)
    {
        if (_settings.TrailingStopPercent <= 0)
            return (signal, false);

        // ---------------------------------------------------------------
        // DYNAMIC PROFIT PROTECTION (Ratchet Stop)
        // As unrealized profit grows, the trailing stop automatically tightens
        // to prevent "round trip" scenarios where gains evaporate.
        // Uses ETF prices (TQQQ/SQQQ) for profit %, not benchmark (QQQ),
        // because AverageEntryPrice is always an ETF fill price.
        // ---------------------------------------------------------------
        decimal effectiveStopPercent;
        
        // Trend Rescue positions use a wider trailing stop without ratchet tightening.
        // These positions enter gradual trends where the normal 0.2% stop + ratchet
        // causes immediate churn (enter → stop → re-enter → stop).
        // Drift Mode positions also use a wider stop (same rationale — slow sustained moves).
        if (_isDriftPosition && _settings.DriftTrailingStopPercent > 0)
        {
            // max() ensures drift stop only WIDENS beyond phase's normal stop, never narrows
            // (e.g., during OV where TrailingStopPercent=0.50%, drift 0.35% would be tighter)
            effectiveStopPercent = Math.Max(_settings.DriftTrailingStopPercent, _settings.TrailingStopPercent);
        }
        else if (_isTrendRescuePosition && _settings.TrendRescueTrailingStopPercent > 0)
        {
            effectiveStopPercent = _settings.TrendRescueTrailingStopPercent;
        }
        else
        {
            effectiveStopPercent = _settings.TrailingStopPercent;
        
            if (_settings.DynamicStopLoss?.Enabled == true && _state.AverageEntryPrice is > 0 && _state.CurrentShares > 0)
            {
                // Calculate max unrealized profit % using ETF High/Low Water Mark
                // (avoids benchmark/ETF domain mismatch that produced absurd 1000%+ figures)
                decimal maxRunPercent;
                if (_state.CurrentPosition == _settings.BearSymbol)
                {
                    // BEAR = long SQQQ → profit when SQQQ goes UP → track ETF HIGH water mark
                    decimal etfHwm = _etfHighWaterMark > 0 ? _etfHighWaterMark : (bearPrice ?? price);
                    maxRunPercent = (etfHwm - _state.AverageEntryPrice.Value) / _state.AverageEntryPrice.Value;
                }
                else
                {
                    decimal etfHwm = _etfHighWaterMark > 0 ? _etfHighWaterMark : (bullPrice ?? price);
                    maxRunPercent = (etfHwm - _state.AverageEntryPrice.Value) / _state.AverageEntryPrice.Value;
                }
            
                // Find the tightest qualifying tier
                var activeTier = _settings.DynamicStopLoss.Tiers
                    .Where(t => maxRunPercent >= t.TriggerProfitPercent)
                    .OrderByDescending(t => t.TriggerProfitPercent)
                    .FirstOrDefault();
            
                if (activeTier != null)
                {
                    effectiveStopPercent = activeTier.StopPercent;
                
                    // Log only when the active tier changes (visibility into ratchet progression)
                    if (activeTier.TriggerProfitPercent != _lastRatchetTierTrigger)
                    {
                        _logger.LogInformation("[RATCHET] Profit {Run:P2} crossed tier {Trigger:P2} -> Stop tightened from {Old:P2} to {New:P2}",
                            maxRunPercent, activeTier.TriggerProfitPercent, _settings.TrailingStopPercent, effectiveStopPercent);
                        _lastRatchetTierTrigger = activeTier.TriggerProfitPercent;
                    }
                }
            }
        }

        // Update water marks based on position
        if (_state.CurrentPosition == _settings.BullSymbol && _state.CurrentShares > 0)
        {
            if (price > _highWaterMark || _highWaterMark == 0m)
            {
                _highWaterMark = price;
            }
            // Track ETF high water mark separately for ratchet profit %
            var currentEtfPrice = bullPrice ?? price;
            if (currentEtfPrice > _etfHighWaterMark || _etfHighWaterMark == 0m)
            {
                _etfHighWaterMark = currentEtfPrice;
            }
            
            // Recalculate stop with effective (possibly tightened) percent
            decimal candidateStop = _highWaterMark * (1 - effectiveStopPercent);
            
            // MONOTONIC SAFEGUARD: Stop price can never decrease (ratchet can only tighten)
            if (candidateStop > _virtualStopPrice)
            {
                _virtualStopPrice = candidateStop;
            }

            // Check for stop trigger
            if (_virtualStopPrice > 0 && price <= _virtualStopPrice && !_isStoppedOut)
            {
                // Use ETF price for profit/loss determination (not benchmark)
                bool isProfitTake = _state.AverageEntryPrice.HasValue && currentEtfPrice > _state.AverageEntryPrice.Value;
                decimal estimatedPnl = _state.AverageEntryPrice.HasValue
                    ? (currentEtfPrice - _state.AverageEntryPrice.Value) * _state.CurrentShares
                    : 0m;
                _isStoppedOut = true;
                _stoppedOutDirection = "BULL";
                _stopoutTime = _currentUtc;
                _washoutLevel = upperBand;
                var stopDistanceBull = System.Math.Abs(price - _virtualStopPrice);
                _logger.LogInformation(
                    "[TRAILING STOP] {Symbol} x{Shares} @ ~${EtfPrice:N2} (entry ${Entry:N2}) | Est P/L: ${PnL:N2} | Stop: ${Stop:N2} (${StopDist:N2} from {Bench}), eff%: {Pct:P2} | {Type}",
                    _settings.BullSymbol, _state.CurrentShares, currentEtfPrice, _state.AverageEntryPrice ?? 0m,
                    estimatedPnl, _virtualStopPrice, stopDistanceBull, _settings.BenchmarkSymbol, effectiveStopPercent, isProfitTake ? "Profit Take" : "Stop Loss");
                return ("NEUTRAL", false); // Force exit
            }
        }
        else if (!string.IsNullOrEmpty(_settings.BearSymbol) &&
                 _state.CurrentPosition == _settings.BearSymbol && _state.CurrentShares > 0)
        {
            if (price < _lowWaterMark || _lowWaterMark == 0m)
            {
                _lowWaterMark = price;
            }
            // Track ETF HIGH water mark for ratchet profit % (BEAR = long SQQQ → peak profit at highest SQQQ price)
            var currentEtfPrice = bearPrice ?? price;
            if (currentEtfPrice > _etfHighWaterMark || _etfHighWaterMark == 0m)
            {
                _etfHighWaterMark = currentEtfPrice;
            }
            
            // Recalculate stop with effective (possibly tightened) percent
            decimal candidateStop = _lowWaterMark * (1 + effectiveStopPercent);
            
            // MONOTONIC SAFEGUARD: For BEAR, stop can never increase (ratchet can only tighten)
            if (_virtualStopPrice == 0m || candidateStop < _virtualStopPrice)
            {
                _virtualStopPrice = candidateStop;
            }

            // Check for stop trigger (price rising)
            if (_virtualStopPrice > 0 && price >= _virtualStopPrice && !_isStoppedOut)
            {
                // Use ETF price for profit/loss determination (BEAR = long SQQQ → same direction as any long position)
                bool isProfitTake = _state.AverageEntryPrice.HasValue && currentEtfPrice > _state.AverageEntryPrice.Value;
                decimal estimatedPnl = _state.AverageEntryPrice.HasValue
                    ? (currentEtfPrice - _state.AverageEntryPrice.Value) * _state.CurrentShares
                    : 0m;
                _isStoppedOut = true;
                _stoppedOutDirection = "BEAR";
                _stopoutTime = _currentUtc;
                _washoutLevel = lowerBand;
                var stopDistanceBear = System.Math.Abs(_virtualStopPrice - price);
                _logger.LogInformation(
                    "[TRAILING STOP] {Symbol} x{Shares} @ ~${EtfPrice:N2} (entry ${Entry:N2}) | Est P/L: ${PnL:N2} | Stop: ${Stop:N2} (${StopDist:N2} from {Bench}), eff%: {Pct:P2} | {Type}",
                    _settings.BearSymbol, _state.CurrentShares, currentEtfPrice, _state.AverageEntryPrice ?? 0m,
                    estimatedPnl, _virtualStopPrice, stopDistanceBear, _settings.BenchmarkSymbol, effectiveStopPercent, isProfitTake ? "Profit Take" : "Stop Loss");
                return ("NEUTRAL", false); // Force exit
            }
        }

        // Check washout latch
        bool latchBlocks = false;
        if (_isStoppedOut && _stopoutTime.HasValue)
        {
            var elapsed = (_currentUtc - _stopoutTime.Value).TotalSeconds;
            if (elapsed < _settings.StopLossCooldownSeconds)
            {
                latchBlocks = true;
            }
            else if ((_stoppedOutDirection == "BULL" && price > _washoutLevel) ||
                     (_stoppedOutDirection == "BEAR" && price < _washoutLevel))
            {
                // Price recovered past washout level - clear latch
                _isStoppedOut = false;
                _highWaterMark = 0m;
                _lowWaterMark = 0m;
                _etfHighWaterMark = 0m;
                _virtualStopPrice = 0m;
                _lastRatchetTierTrigger = -1m;
                _isTrendRescuePosition = false;
                _isDriftPosition = false;
                _logger.LogInformation("[LATCH CLEAR] Price recovered to ${Price:N2} (crossed washout level ${WashoutLevel:N2}). Re-entry allowed.", price, _washoutLevel);
            }
            else if ((_stoppedOutDirection == "BULL" && price < smaValue) ||
                     (_stoppedOutDirection == "BEAR" && price > smaValue))
            {
                // SMART RESET: Price crossed SMA in opposite direction, proving genuine cooling
                // This prevents the "Washout Latch Deadlock" where wide ChopThreshold causes
                // dips to be classified as NEUTRAL instead of BEAR, keeping the latch stuck
                _isStoppedOut = false;
                _highWaterMark = 0m;
                _lowWaterMark = 0m;
                _etfHighWaterMark = 0m;
                _virtualStopPrice = 0m;
                _lastRatchetTierTrigger = -1m;
                _isTrendRescuePosition = false;
                _isDriftPosition = false;
                _logger.LogInformation("[LATCH CLEAR - SMART RESET] Price ${Price:N2} crossed SMA ${Sma:N2} ({Dir} stopout). Re-entry allowed.", 
                    price, smaValue, _stoppedOutDirection);
            }
            else
            {
                latchBlocks = true;
            }
        }

        return (signal, latchBlocks);
    }

    /// <summary>
    /// Resolves the effective DirectionSwitchCooldownSeconds for the TRADER's current market time.
    /// In replay mode the shared TradingSettings may reflect a different phase than the trader's
    /// current regime timestamp (because the analyst races ahead). This method looks up the
    /// active TimeBasedRule for _currentUtc and returns the correct phase-specific cooldown.
    /// </summary>
    private int GetEffectiveDirectionSwitchCooldown()
    {
        if (_settings.TimeRules.Count == 0)
            return _baseDirectionSwitchCooldownSeconds;

        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
        foreach (var rule in _settings.TimeRules)
        {
            if (rule.IsActive(easternNow.TimeOfDay) && rule.Overrides.DirectionSwitchCooldownSeconds.HasValue)
                return rule.Overrides.DirectionSwitchCooldownSeconds.Value;
        }

        return _baseDirectionSwitchCooldownSeconds;
    }

    private async Task EnsureBullPositionAsync(MarketRegime regime, CancellationToken ct)
    {
        // Check if already in position
        if (_state.CurrentPosition == _settings.BullSymbol && _state.CurrentShares > 0)
        {
            _logger.LogDebug("[HOLD] Already in BULL ({Symbol})", _settings.BullSymbol);
            return;
        }

        // End-of-day entry cutoff: don't enter new positions too close to MARKET_CLOSE
        if (_settings.LastEntryMinutesBeforeClose > 0)
        {
            var easternForCutoff = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
            var cutoffTime = new TimeSpan(15, 58, 0) - TimeSpan.FromMinutes((double)_settings.LastEntryMinutesBeforeClose);
            if (easternForCutoff.TimeOfDay >= cutoffTime)
            {
                _logger.LogInformation("[TRADER] EOD entry cutoff: {Time:HH:mm:ss} ET >= cutoff {Cutoff} — skipping BULL entry",
                    easternForCutoff, cutoffTime);
                return;
            }
        }

        // Liquidate opposite position first
        if (!string.IsNullOrEmpty(_settings.BearSymbol) &&
            _state.CurrentPosition == _settings.BearSymbol && _state.CurrentShares > 0)
        {
            // Direction switch cooldown: prevent rapid BEAR→BULL whipsaw
            var cooldown = GetEffectiveDirectionSwitchCooldown();
            if (cooldown > 0 && _lastDirectionSwitchTime.HasValue)
            {
                var elapsed = (_currentUtc - _lastDirectionSwitchTime.Value).TotalSeconds;
                if (elapsed < cooldown)
                {
                    _logger.LogInformation("[TRADER] Direction switch cooldown active ({Elapsed:F0}s < {Cooldown}s) — holding BEAR",
                        elapsed, cooldown);
                    return;
                }
            }
            
            _logger.LogInformation("[TRADER] Switching from BEAR to BULL - liquidating {Symbol} (at {Time:HH:mm:ss})", _settings.BearSymbol, _currentUtc);
            if (!await LiquidateCurrentPositionAsync(ct, regime.BearPrice))
            {
                // STRICT ENFORCEMENT: Failed liquidation triggers safe mode
                throw new TradingException(
                    $"Failed to liquidate BEAR position ({_settings.BearSymbol}). " +
                    "Cannot open BULL position with existing position.");
            }
            _lastDirectionSwitchTime = _currentUtc;
        }

        // SAFEGUARD: Verify no opposite position exists on broker before buying
        if (!string.IsNullOrEmpty(_settings.BearSymbol))
        {
            if (!await VerifyNoOppositePositionAsync(_settings.BearSymbol, ct))
            {
                return; // Safeguard blocked the buy
            }
        }

        // Buy BULL position
        // Note: BullSymbol is guaranteed non-null by Validate() call in constructor
        if (IsBuyCooldownActive("BULL"))
            return;
        
        await BuyPositionAsync(_settings.BullSymbol!, ct, regime.BullPrice);

        // Reset trailing stop for new position
        if (_state.CurrentPosition == _settings.BullSymbol)
        {
            _isTrendRescuePosition = regime.IsTrendRescueEntry;
            _isDriftPosition = regime.IsDriftEntry;
            decimal stopPercent;
            if (_isDriftPosition && _settings.DriftTrailingStopPercent > 0)
                stopPercent = Math.Max(_settings.DriftTrailingStopPercent, _settings.TrailingStopPercent);
            else if (_isTrendRescuePosition && _settings.TrendRescueTrailingStopPercent > 0)
                stopPercent = _settings.TrendRescueTrailingStopPercent;
            else
                stopPercent = _settings.TrailingStopPercent;
            _highWaterMark = regime.BenchmarkPrice;
            _etfHighWaterMark = regime.BullPrice ?? regime.BenchmarkPrice;
            _virtualStopPrice = _highWaterMark * (1 - stopPercent);
            _lowWaterMark = 0m;
            PersistTrailingStopState();
            if (_isDriftPosition && _settings.DriftTrailingStopPercent > 0)
            {
                _logger.LogInformation("[DRIFT ENTRY] Trailing stop {Stop:P2} (drift={Drift:P2}, phase={Phase:P2}, max applied)",
                    stopPercent, _settings.DriftTrailingStopPercent, _settings.TrailingStopPercent);
            }
            else if (_isTrendRescuePosition)
            {
                _logger.LogInformation("[TREND RESCUE] Entry with wider trailing stop ({Stop:P2} vs normal {Normal:P2})",
                    stopPercent, _settings.TrailingStopPercent);
            }
        }
    }

    private async Task EnsureBearPositionAsync(MarketRegime regime, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.BearSymbol))
        {
            _logger.LogDebug("[TRADER] No BEAR symbol configured - staying in cash");
            return;
        }

        // Check if already in position
        if (_state.CurrentPosition == _settings.BearSymbol && _state.CurrentShares > 0)
        {
            _logger.LogDebug("[HOLD] Already in BEAR ({Symbol})", _settings.BearSymbol);
            return;
        }

        // End-of-day entry cutoff: don't enter new positions too close to MARKET_CLOSE
        if (_settings.LastEntryMinutesBeforeClose > 0)
        {
            var easternForCutoff = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
            var cutoffTime = new TimeSpan(15, 58, 0) - TimeSpan.FromMinutes((double)_settings.LastEntryMinutesBeforeClose);
            if (easternForCutoff.TimeOfDay >= cutoffTime)
            {
                _logger.LogInformation("[TRADER] EOD entry cutoff: {Time:HH:mm:ss} ET >= cutoff {Cutoff} — skipping BEAR entry",
                    easternForCutoff, cutoffTime);
                return;
            }
        }

        // Liquidate opposite position first
        if (_state.CurrentPosition == _settings.BullSymbol && _state.CurrentShares > 0)
        {
            // Direction switch cooldown: prevent rapid BULL→BEAR whipsaw
            var cooldown = GetEffectiveDirectionSwitchCooldown();
            if (cooldown > 0 && _lastDirectionSwitchTime.HasValue)
            {
                var elapsed = (_currentUtc - _lastDirectionSwitchTime.Value).TotalSeconds;
                if (elapsed < cooldown)
                {
                    _logger.LogInformation("[TRADER] Direction switch cooldown active ({Elapsed:F0}s < {Cooldown}s) — holding BULL",
                        elapsed, cooldown);
                    return;
                }
            }
            
            _logger.LogInformation("[TRADER] Switching from BULL to BEAR - liquidating {Symbol} (at {Time:HH:mm:ss})", _settings.BullSymbol, _currentUtc);
            if (!await LiquidateCurrentPositionAsync(ct, regime.BullPrice))
            {
                // STRICT ENFORCEMENT: Failed liquidation triggers safe mode
                throw new TradingException(
                    $"Failed to liquidate BULL position ({_settings.BullSymbol}). " +
                    "Cannot open BEAR position with existing position.");
            }
            _lastDirectionSwitchTime = _currentUtc;
        }

        // SAFEGUARD: Verify no opposite position exists on broker before buying
        // Note: BullSymbol is guaranteed non-null by Validate() call in constructor
        if (!await VerifyNoOppositePositionAsync(_settings.BullSymbol!, ct))
        {
            return; // Safeguard blocked the buy
        }

        // Buy BEAR position
        if (IsBuyCooldownActive("BEAR"))
            return;
        
        await BuyPositionAsync(_settings.BearSymbol, ct, regime.BearPrice);

        // Reset trailing stop for new position
        if (_state.CurrentPosition == _settings.BearSymbol)
        {
            _isTrendRescuePosition = regime.IsTrendRescueEntry;
            _isDriftPosition = regime.IsDriftEntry;
            decimal stopPercent;
            if (_isDriftPosition && _settings.DriftTrailingStopPercent > 0)
                stopPercent = Math.Max(_settings.DriftTrailingStopPercent, _settings.TrailingStopPercent);
            else if (_isTrendRescuePosition && _settings.TrendRescueTrailingStopPercent > 0)
                stopPercent = _settings.TrendRescueTrailingStopPercent;
            else
                stopPercent = _settings.TrailingStopPercent;
            _lowWaterMark = regime.BenchmarkPrice;
            _etfHighWaterMark = regime.BearPrice ?? regime.BenchmarkPrice;
            _virtualStopPrice = _lowWaterMark * (1 + stopPercent);
            _highWaterMark = 0m;
            PersistTrailingStopState();
            if (_isDriftPosition && _settings.DriftTrailingStopPercent > 0)
            {
                _logger.LogInformation("[DRIFT ENTRY] BEAR trailing stop {Stop:P2} (drift={Drift:P2}, phase={Phase:P2}, max applied)",
                    stopPercent, _settings.DriftTrailingStopPercent, _settings.TrailingStopPercent);
            }
        }
    }

    private async Task HandleNeutralAsync(MarketRegime regime, CancellationToken ct)
    {
        // DYNAMIC EXIT: Determine effective wait time based on trend strength
        double currentAbsSlope = Math.Abs((double)regime.Slope);
        int effectiveWaitSeconds;
        string exitMode;

        if (currentAbsSlope >= _settings.ExitStrategy.TrendConfidenceThreshold)
        {
            // MODE: Trend/Grind (Be Patient - strong momentum may resume)
            effectiveWaitSeconds = _settings.ExitStrategy.TrendWaitSeconds;
            exitMode = "TREND (Patient)";
        }
        else
        {
            // MODE: Chop/Scalp (Be Strict - weak momentum, exit quickly)
            effectiveWaitSeconds = _settings.ExitStrategy.ScalpWaitSeconds;
            exitMode = "SCALP (Strict)";
        }

        // RESONANCE ADJUSTMENT: Auto-tune patience based on the market's rhythm.
        // If the market oscillates on a regular cycle (low stability = consistent sine wave),
        // cap wait time at 80% of the half-cycle to exit before the reversal.
        if (regime.CyclePeriodSeconds > 30 && regime.CycleStability < 20
            && effectiveWaitSeconds > 0)
        {
            int cycleCap = (int)(regime.CyclePeriodSeconds * 0.8);

            if (cycleCap < effectiveWaitSeconds)
            {
                _logger.LogInformation(
                    "[RHYTHM] Auto-Tuning Patience: Market Cycle is {Cycle:F0}s. Lowering Wait from {Cfg}s to {New}s.",
                    regime.CyclePeriodSeconds, effectiveWaitSeconds, cycleCap);
                effectiveWaitSeconds = cycleCap;
                exitMode += " → CYCLE-CAPPED";
            }
        }

        // Determine relevant price for potential liquidation
        decimal? liquidationPrice = null;
        if (_state.CurrentPosition == _settings.BullSymbol)
            liquidationPrice = regime.BullPrice;
        else if (_state.CurrentPosition == _settings.BearSymbol)
            liquidationPrice = regime.BearPrice;

        // If stopped out (trailing stop triggered), liquidate IMMEDIATELY — regardless of wait settings.
        // This MUST be checked before the effectiveWaitSeconds early return to prevent
        // TrendWaitSeconds=-1 from suppressing stop-triggered liquidation.
        if (_isStoppedOut && _state.CurrentPosition != null && _state.CurrentShares > 0)
        {
            _logger.LogInformation("[TRADER] Stop-out active - liquidating immediately");
            await EnsureNeutralAsync("TRAILING_STOP", ct, liquidationPrice);
            return;
        }

        // effectiveWaitSeconds == -1 means "hold through neutral" (disabled)
        if (effectiveWaitSeconds < 0)
        {
            return; // Hold current position
        }

        if (_neutralDetectionTime == null)
        {
            _neutralDetectionTime = _currentUtc;
            return;
        }

        var elapsed = (_currentUtc - _neutralDetectionTime.Value).TotalSeconds;
        if (elapsed >= effectiveWaitSeconds)
        {
            if (_state.CurrentPosition != null && _state.CurrentShares > 0)
            {
                // ---------------------------------------------------------------
                // UNDERWATER PATIENCE: Don't liquidate losing positions on timeout.
                // NEUTRAL means "undecided", not "reversed". Give red trades time
                // to recover. The Trailing Stop / Ratchet Stop protects downside.
                // ---------------------------------------------------------------
                if (_settings.ExitStrategy.HoldNeutralIfUnderwater && _state.AverageEntryPrice is > 0)
                {
                    // Get current ETF price for P/L calculation
                    decimal currentEtfPrice = 0m;
                    if (_state.CurrentPosition == _settings.BullSymbol && regime.BullPrice.HasValue)
                        currentEtfPrice = regime.BullPrice.Value;
                    else if (_state.CurrentPosition == _settings.BearSymbol && regime.BearPrice.HasValue)
                        currentEtfPrice = regime.BearPrice.Value;
                    else
                        currentEtfPrice = await _broker.GetLatestPriceAsync(_state.CurrentPosition!, ct);
                    
                    decimal unrealizedPL = (currentEtfPrice - _state.AverageEntryPrice.Value) * _state.CurrentShares;
                    
                    if (unrealizedPL < 0)
                    {
                        // Log periodically (every ~10s based on status interval) to avoid spam
                        if ((_currentUtc - _lastStatusLogTime).TotalSeconds >= _settings.StatusLogIntervalSeconds)
                        {
                            _logger.LogInformation(
                                "[PATIENCE] Neutral timeout reached ({ExitMode}, {Elapsed:F0}s), but position is underwater (${PL:N2}). Holding for stop or reversal.",
                                exitMode, elapsed, unrealizedPL);
                        }
                        return; // Skip liquidation — let stop loss or signal flip handle it
                    }
                }

                _logger.LogInformation(
                    "[TRADER] NEUTRAL Timeout ({ExitMode}). |Slope|: {AbsSlope:F6} vs Threshold: {Threshold:F6}. Waited {Elapsed:F1}s >= {Wait}s",
                    exitMode, currentAbsSlope, _settings.ExitStrategy.TrendConfidenceThreshold, elapsed, effectiveWaitSeconds);
                await EnsureNeutralAsync($"NEUTRAL timeout ({exitMode})", ct, liquidationPrice);
            }
        }
    }

    private async Task HandleMarketCloseAsync(MarketRegime regime, CancellationToken ct)
    {
        if (_state.CurrentPosition != null && _state.CurrentShares > 0)
        {
            _logger.LogInformation("[TRADER] Market closing - liquidating position");
            
            decimal? price = null;
            if (_state.CurrentPosition == _settings.BullSymbol) price = regime.BullPrice;
            else if (_state.CurrentPosition == _settings.BearSymbol) price = regime.BearPrice;
            
            await EnsureNeutralAsync("MARKET_CLOSE", ct, price);
        }
        
        // Signal to watchdog that we're intentionally done receiving data until next session
        _marketSessionEnded = true;
    }

    private async Task EnsureNeutralAsync(string reason, CancellationToken ct, decimal? knownPrice = null)
    {
        if (_state.CurrentPosition == null || _state.CurrentShares <= 0)
        {
            _logger.LogDebug("[TRADER] Already in CASH for {Reason}", reason);
            return;
        }

        _logger.LogInformation("[TRADER] {Reason} - liquidating to CASH", reason);
        await LiquidateCurrentPositionAsync(ct, knownPrice);
    }

    private async Task<bool> LiquidateCurrentPositionAsync(CancellationToken ct, decimal? knownPrice = null)
    {
        var symbol = _state.CurrentPosition;
        var shares = _state.CurrentShares;

        if (string.IsNullOrEmpty(symbol) || shares <= 0)
        {
            return true; // Nothing to liquidate
        }

        // CRITICAL: Cancel all open orders for this symbol BEFORE attempting liquidation
        // This prevents "self-collision" where stale orders lock shares or trigger wash-trade detection
        try
        {
            var cancelledCount = await _broker.CancelAllOpenOrdersAsync(symbol, ct);
            if (cancelledCount > 0)
            {
                _logger.LogWarning("[SELL] Cancelled {Count} stale orders for {Symbol} before liquidation", cancelledCount, symbol);
                // Give broker 50ms to fully process the cancellations and free shares
                await Task.Delay(50, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SELL] Failed to cancel open orders before liquidation - proceeding anyway");
        }

        var quotePrice = knownPrice ?? await _broker.GetLatestPriceAsync(symbol);
        var estimatedProceeds = shares * quotePrice;
        var remainingShares = shares;
        decimal totalProceeds = 0m;

        _logger.LogInformation("[SELL] Liquidating {Shares} shares of {Symbol} @ ~${Price:N2}",
            shares, symbol, quotePrice);

        // PHASE 1: Try IOC if enabled
        if (_settings.UseIocOrders)
        {
            var limitPrice = System.Math.Round(quotePrice - (_settings.IocLimitOffsetCents / 100m), 2);
            var result = await _iocExecutor.ExecuteAsync(
                symbol,
                remainingShares,
                BotOrderSide.Sell,
                limitPrice,
                _settings.IocRetryStepCents,
                _settings.IocMaxRetries,
                _settings.IocMaxDeviationPercent);

            if (result.FilledQty > 0)
            {
                totalProceeds += result.TotalProceeds;
                remainingShares -= result.FilledQty;
                
                _logger.LogInformation("[FILL] IOC Sell: {Filled}/{Total} @ ${Price:N4}",
                    result.FilledQty, shares, result.AvgPrice);
                
                // Update state with partial progress using Split Allocation
                await _stateLock.WaitAsync();
                try
                {
                    // Calculate cost basis for sold shares (proportional to total position)
                    var costBasisPerShare = _state.AverageEntryPrice ?? quotePrice;
                    var soldCostBasis = result.FilledQty * costBasisPerShare;
                    
                    // Apply split allocation for profit distribution
                    ApplySplitAllocation(result.TotalProceeds, soldCostBasis);
                    
                    _state.CurrentShares = remainingShares;
                    _stateManager.Save(_state);
                }
                finally
                {
                    _stateLock.Release();
                }
                
                if (remainingShares <= 0)
                {
                    // Complete liquidation
                    await _stateLock.WaitAsync();
                    try
                    {
                        _state.CurrentPosition = null;
                        _state.CurrentShares = 0;
                        _state.AverageEntryPrice = null;
                        ClearTrailingStopState();
                        _stateManager.Save(_state);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    return true;
                }
                
                // Check if remaining is within tolerance
                if (remainingShares <= _settings.IocRemainingSharesTolerance)
                {
                    _logger.LogWarning("[FILL] IOC partial with {Remaining} shares within tolerance. Queuing for cleanup.", remainingShares);
                    await _stateLock.WaitAsync();
                    try
                    {
                        _state.OrphanedShares = new OrphanedPosition
                        {
                            Symbol = symbol,
                            Shares = remainingShares,
                            CreatedAt = DateTime.UtcNow.ToString("o")
                        };
                        _state.CurrentPosition = null;
                        _state.CurrentShares = 0;
                        ClearTrailingStopState();
                        _stateManager.Save(_state);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    return true;
                }
                
                // Still have significant shares - fall through to market order
                _logger.LogWarning("[FILL] IOC partial with {Remaining} shares remaining. Falling back to market order.", remainingShares);
            }
            else
            {
                _logger.LogWarning("[FILL] IOC Sell failed completely. Falling back to market order.");
            }
        }

        // PHASE 2: Market order for remaining shares (fallback or primary if IOC disabled)
        if (remainingShares <= 0)
        {
            return true; // Already fully liquidated
        }
        
        int attempt = 0;
        const int MaxAttempts = 3;

        while (attempt < MaxAttempts)
        {
            attempt++;
            
            var request = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = remainingShares,
                Side = BotOrderSide.Sell,
                Type = BotOrderType.Market,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = _settings.GenerateClientOrderId(),
                HintPrice = quotePrice
            };

            try
            {
                var order = await _broker.SubmitOrderAsync(request, ct); // Pass CT if available
                _logger.LogInformation("[SELL] Market order submitted for {Qty} shares: {OrderId}", remainingShares, order.OrderId);

                // Wait for fill
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(500, ct);
                    var filledOrder = await _broker.GetOrderAsync(order.OrderId, ct);

                    if (filledOrder.Status == BotOrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                    {
                        var marketProceeds = filledOrder.FilledQuantity * filledOrder.AverageFillPrice.Value;
                        totalProceeds += marketProceeds;
                        
                        await _stateLock.WaitAsync();
                        try
                        {
                            // Calculate cost basis for sold shares
                            var costBasisPerShare = _state.AverageEntryPrice ?? quotePrice;
                            var soldCostBasis = filledOrder.FilledQuantity * costBasisPerShare;
                            
                            // Apply split allocation for profit distribution
                            ApplySplitAllocation(marketProceeds, soldCostBasis);
                            
                            _state.CurrentPosition = null;
                            _state.CurrentShares = 0;
                            _state.AverageEntryPrice = null;
                            ClearTrailingStopState();
                            _stateManager.Save(_state);
                        }
                        finally
                        {
                            _stateLock.Release();
                        }
                        
                        _logger.LogInformation("[FILL] Market sell confirmed: {Qty} @ ${Price:N4}",
                            filledOrder.FilledQuantity, filledOrder.AverageFillPrice.Value);
                        return true;
                    }

                    if (filledOrder.Status == BotOrderStatus.Canceled ||
                        filledOrder.Status == BotOrderStatus.Rejected)
                    {
                        // Check for partial fills before giving up
                        if (filledOrder.FilledQuantity > 0 && filledOrder.AverageFillPrice.HasValue)
                        {
                            var partialProceeds = filledOrder.FilledQuantity * filledOrder.AverageFillPrice.Value;
                            totalProceeds += partialProceeds;
                            
                            await _stateLock.WaitAsync();
                            try
                            {
                                var costBasisPerShare = _state.AverageEntryPrice ?? quotePrice;
                                var soldCostBasis = filledOrder.FilledQuantity * costBasisPerShare;
                                ApplySplitAllocation(partialProceeds, soldCostBasis);
                                _state.CurrentShares -= filledOrder.FilledQuantity;
                                
                                _logger.LogWarning("[FILL] Market sell {Status} with partial fill: {Qty} @ ${Price:N4}. Remaining: {Remaining}",
                                    filledOrder.Status, filledOrder.FilledQuantity, filledOrder.AverageFillPrice.Value, _state.CurrentShares);
                                    
                                if (_state.CurrentShares <= 0)
                                {
                                    _state.CurrentPosition = null;
                                    _state.CurrentShares = 0;
                                    _state.AverageEntryPrice = null;
                                    ClearTrailingStopState();
                                }
                                _stateManager.Save(_state);
                            }
                            finally
                            {
                                _stateLock.Release();
                            }
                        }
                        else
                        {
                            _logger.LogError("[FILL] Market sell order {Status}. Retry might be needed.", filledOrder.Status);
                        }
                        return false; 
                    }
                }

                _logger.LogWarning("[FILL] Market sell fill confirmation timeout");
                return false;
            }
            catch (Exception ex)
            {
                // RECOVERY: Check for Insufficient Quantity error (Sync Mismatch)
                var isQtyError = ex.Message.Contains("insufficient qty", StringComparison.OrdinalIgnoreCase) 
                                 || (ex.InnerException?.Message.Contains("insufficient qty", StringComparison.OrdinalIgnoreCase) ?? false);
                
                // PHANTOM POSITION: "position intent mismatch" means broker has NO position
                // but we're trying to sell_to_close. This is a critical sync error.
                var isPhantomError = ex.Message.Contains("position intent mismatch", StringComparison.OrdinalIgnoreCase)
                                     || (ex.InnerException?.Message.Contains("position intent mismatch", StringComparison.OrdinalIgnoreCase) ?? false);
                
                if (isPhantomError)
                {
                    _logger.LogCritical("[SELL] PHANTOM POSITION DETECTED: Broker says no position exists to sell. " +
                        "Local state shows {Symbol} x {Shares} but broker has nothing. Reconciling with P/L.",
                        symbol, remainingShares);
                    
                    // Try to get current market price for best-effort P/L estimate
                    decimal estimatedFillPrice;
                    try { estimatedFillPrice = await _broker.GetLatestPriceAsync(symbol); }
                    catch { estimatedFillPrice = _state.AverageEntryPrice ?? quotePrice; }
                    
                    await _stateLock.WaitAsync();
                    try
                    {
                        var phantomCostBasis = remainingShares * (_state.AverageEntryPrice ?? quotePrice);
                        var phantomProceeds = remainingShares * estimatedFillPrice;
                        
                        // Use ApplySplitAllocation to properly track P/L
                        ApplySplitAllocation(phantomProceeds, phantomCostBasis);
                        
                        _logger.LogWarning("[SYNC] Reconciled phantom shares: est. proceeds ${Proceeds:N2} vs cost ${Cost:N2} (using market price ${Price:N4}).",
                            phantomProceeds, phantomCostBasis, estimatedFillPrice);
                        
                        _state.CurrentPosition = null;
                        _state.CurrentShares = 0;
                        _state.AverageEntryPrice = null;
                        ClearTrailingStopState();
                        _stateManager.Save(_state);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    
                    // Return true because position is effectively liquidated (it never existed)
                    return true;
                }

                if (isQtyError && attempt < MaxAttempts)
                {
                     _logger.LogWarning("[SELL] Order rejected due to Insufficient Quantity. Resyncing with broker...");
                     try 
                     {
                         var position = await _broker.GetPositionAsync(symbol, ct);
                         var actualQty = position?.Quantity ?? 0;
                         
                         _logger.LogWarning("[SELL] Sync Result: Bot thought {Bot}, Broker has {Broker}. Adjusting...", remainingShares, actualQty);
                         
                         // GHOST SHARE FIX: If broker has MORE shares than we thought, 
                         // those are "ghost shares" from orders that filled but we didn't track.
                         // We must account for their cost to prevent profit hallucination.
                         await _stateLock.WaitAsync();
                         try 
                         { 
                             if (actualQty > remainingShares)
                             {
                                 var ghostShares = actualQty - remainingShares;
                                 
                                 // Use broker's AverageEntryPrice as the source of truth
                                 // This is what we actually paid, not what we thought we paid
                                 var brokerAvgPrice = position?.AverageEntryPrice ?? quotePrice;
                                 
                                 _logger.LogWarning("[SYNC] GHOST SHARE FIX: Found {GhostQty} untracked shares. " +
                                     "Using broker's avg entry price ${BrokerAvg:N4} as cost basis.",
                                     ghostShares, brokerAvgPrice);
                                 
                                 // Recalculate blended average entry price
                                 // Old cost: remainingShares * _state.AverageEntryPrice
                                 // Ghost cost: ghostShares * brokerAvgPrice (assume current price if no broker data)
                                 var oldCostBasis = remainingShares * (_state.AverageEntryPrice ?? quotePrice);
                                 var ghostCostBasis = ghostShares * brokerAvgPrice;
                                 var newTotalCost = oldCostBasis + ghostCostBasis;
                                 var newAvgPrice = actualQty > 0 ? newTotalCost / actualQty : quotePrice;
                                 
                                 // CRITICAL: Deduct the ghost share cost from available cash
                                 // This prevents the "hallucinated profit" when we sell
                                 var cashBeforeDeduction = _state.AvailableCash;
                                 _state.AvailableCash -= ghostCostBasis;
                                 _state.AverageEntryPrice = newAvgPrice;
                                 
                                 _logger.LogWarning("[SYNC] Adjusted AvailableCash by -${Cost:N2} for ghost shares. " +
                                     "New AvgEntryPrice: ${AvgPrice:N4}", ghostCostBasis, newAvgPrice);
                                 
                                 // Warn if deduction resulted in negative cash - indicates deeper sync issue
                                 if (_state.AvailableCash < 0)
                                 {
                                     _logger.LogWarning(
                                         "[SYNC] ⚠️ Warning: Ghost share deduction resulted in negative cash (${Cash:F2}). " +
                                         "Tried to deduct ${GhostCost:F2} from ${CashBefore:F2}. Account accounting may be desynchronized.",
                                         _state.AvailableCash, ghostCostBasis, cashBeforeDeduction);
                                 }
                             }
                             else if (actualQty < remainingShares)
                             {
                                 // MISSING SHARES FIX: Broker has FEWER shares than tracked.
                                 // These shares were sold (likely by a previous untracked fill).
                                 // Use current market price as best-effort P/L estimate.
                                 var missingQty = remainingShares - actualQty;
                                 
                                 var missingCostBasis = missingQty * (_state.AverageEntryPrice ?? quotePrice);
                                 var missingProceeds = missingQty * quotePrice; // quotePrice is current market
                                 
                                 // Use ApplySplitAllocation for proper P/L tracking
                                 ApplySplitAllocation(missingProceeds, missingCostBasis);
                                 
                                 _logger.LogWarning(
                                     "[SYNC] MISSING SHARE FIX: Broker has {Missing} fewer shares than tracked. " +
                                     "Est. proceeds ${Proceeds:N2} vs cost ${Cost:N2} (market @ ${Price:N4}).", 
                                     missingQty, missingProceeds, missingCostBasis, quotePrice);
                                 
                                 // Update Average Entry Price if we still have a position
                                 if (actualQty > 0)
                                 {
                                     var brokerAvgPrice = position?.AverageEntryPrice ?? _state.AverageEntryPrice ?? quotePrice;
                                     _state.AverageEntryPrice = brokerAvgPrice;
                                     _logger.LogWarning("[SYNC] Updated AvgEntryPrice to broker's: ${AvgPrice:N4}", brokerAvgPrice);
                                 }
                             }
                             
                             _state.CurrentShares = actualQty; 
                             _stateManager.Save(_state); 
                         } 
                         finally { _stateLock.Release(); }
                         
                         remainingShares = actualQty;
                         
                         if (remainingShares <= 0)
                         {
                             _logger.LogInformation("[SELL] After sync, position is empty. Liquidation successful.");
                             return true;
                         }
                         
                         // Loop continues with new quantity
                         continue;
                     }
                     catch (Exception syncEx)
                     {
                         _logger.LogError(syncEx, "[SELL] Failed to resync during recovery.");
                     }
                }

                _logger.LogError(ex, "[SELL] Failed to submit market order (Attempt {Att}/{Max})", attempt, MaxAttempts);
                if (attempt == MaxAttempts) return false;
            }
        }
        
        return false;
    }

    private async Task BuyPositionAsync(string symbol, CancellationToken ct, decimal? knownPrice = null)
    {
        // HYBRID PROFIT MANAGEMENT: Calculate buying power using Split Allocation Model
        // BuyingPower = StartingAmount + (RealizedSessionPnL × ReinvestmentPercent)
        // Bank (AccumulatedLeftover) is always OFF LIMITS for position sizing.
        var reinvestableProfit = _state.RealizedSessionPnL * _settings.ProfitReinvestmentPercent;
        var investableAmount = _settings.StartingAmount + reinvestableProfit;
        
        // Cap at available cash (can't spend what we don't have)
        investableAmount = System.Math.Min(investableAmount, _state.AvailableCash);
        
        // Save original cash for rollback on failure
        var originalAvailableCash = _state.AvailableCash;

        if (investableAmount <= 0)
        {
            _logger.LogWarning("[BUY] No investable cash. Avail: ${Avail:N2} | Investable: ${Inv:N2} | SessionPnL: ${PnL:N2}", 
                _state.AvailableCash, investableAmount, _state.RealizedSessionPnL);
            return;
        }

        var basePrice = knownPrice ?? await _broker.GetLatestPriceAsync(symbol);
        decimal limitPrice;
        decimal effectivePrice;

        if (_settings.UseIocOrders)
        {
            limitPrice = System.Math.Round(basePrice + (_settings.IocLimitOffsetCents / 100m), 2);
            effectivePrice = limitPrice;
        }
        else if (_settings.UseMarketableLimits)
        {
            limitPrice = System.Math.Round(basePrice * (1m + _settings.MaxSlippagePercent), 2);
            effectivePrice = limitPrice;
        }
        else
        {
            limitPrice = 0m;
            effectivePrice = basePrice;
        }

        var quantity = (long)(investableAmount / effectivePrice);
        if (quantity <= 0)
        {
            _logger.LogWarning("[BUY] Insufficient cash for 1 share of {Symbol} @ ${Price:N2}", symbol, effectivePrice);
            return;
        }

        _logger.LogInformation("[BUY] {Symbol} x {Qty} @ ~${Price:N2} (Inv: ${Invest:N2} of ${Avail:N2})", 
            symbol, quantity, basePrice, investableAmount, _state.AvailableCash);

        if (_settings.UseIocOrders)
        {
            var result = await _iocExecutor.ExecuteAsync(
                symbol,
                quantity,
                BotOrderSide.Buy,
                limitPrice,
                _settings.IocRetryStepCents,
                _settings.IocMaxRetries,
                _settings.IocMaxDeviationPercent);

            if (result.FilledQty > 0)
            {
                var actualCost = result.TotalProceeds;
                
                await _stateLock.WaitAsync();
                try
                {
                    // BUY LOGIC: Simply deduct actual cost from AvailableCash.
                    // NEVER touch AccumulatedLeftover (Bank) during a buy - it's sacred.
                    // The Bank is only modified during profit realization (sells).
                    
                    _state.AvailableCash = originalAvailableCash - actualCost;

                    _state.CurrentPosition = symbol;
                    _state.CurrentShares = result.FilledQty;
                    _state.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
                    _state.AverageEntryPrice = result.AvgPrice; 
                    _stateManager.Save(_state);
                }
                finally
                {
                    _stateLock.Release();
                }
                
                _logger.LogInformation("[FILL] IOC Buy complete: {Qty} @ ${Price:N4}",
                    result.FilledQty, result.AvgPrice);
                
                // Cleanup orphans
                await CleanupOrphanedSharesAsync(ct);
                
                // IF FULL FILL -> Return
                if (result.FilledQty >= quantity - _settings.IocRemainingSharesTolerance)
                {
                    ResetBuyCooldown();
                    return;
                }
                
                // IF PARTIAL -> Fall through to Market Order logic for the remainder
                _logger.LogWarning("[FILL] IOC Partial Buy ({Filled}/{Total}). Falling through to Market Order for remainder.",
                    result.FilledQty, quantity);
                    
                // Adjust quantity pending for market order
                quantity -= result.FilledQty;
                // DO NOT RETURN
            }
            else
            {
                _logger.LogWarning("[FILL] IOC Buy failed. Falling back to market order.");
            }
        }

        // Market order fallback - use pending order pattern instead of Task.Run
        var request = new BotOrderRequest
        {
            Symbol = symbol,
            Quantity = quantity,
            Side = BotOrderSide.Buy,
            Type = _settings.UseMarketableLimits ? BotOrderType.Limit : BotOrderType.Market,
            TimeInForce = BotTimeInForce.Day,
            ClientOrderId = _settings.GenerateClientOrderId(),
            LimitPrice = _settings.UseMarketableLimits ? limitPrice : null,
            HintPrice = basePrice
        };

        try
        {
            var order = await _broker.SubmitOrderAsync(request);
            _logger.LogInformation("[BUY] Order submitted: {OrderId}", order.OrderId);

            // Update state with estimate (thread-safe)
            await _stateLock.WaitAsync();
            try
            {
                // FIX: Calculate leftover dynamically to capture any savings from IOC partial fills
                // availableCash holds (Total - SpentOnIOC). We commit (quantity * effectivePrice).
                // Any difference is "savings" or "change" that should go to AccumulatedLeftover.
                var committedForOrder = quantity * effectivePrice;
                var surplus = _state.AvailableCash - committedForOrder;

                // FIX: "Surplus" (Unspent Change) must NOT go to AccumulatedLeftover (Bank) unless it is truly profit.
                // We keep it in AvailableCash. Although AvailableCash is "technically" usable for trading,
                // the bot will NOT initiate a new trade while CurrentPosition is set.
                // This prevents "Change" from getting stuck in the Bank during loss recovery.
                
                _state.AvailableCash = surplus;
                // AccumulatedLeftover remains untouched

                _state.CurrentPosition = symbol;
                // FIX: Do NOT overwrite CurrentShares with pending quantity. 
                // If we have IOC shares, we must keep them. 
                // If we have 0 shares, it stays 0 until fill.
                // _state.CurrentShares = quantity; 
                
                _state.LastTradeTimestamp = DateTime.UtcNow.ToString("o");
                _stateManager.Save(_state);
            }
            finally
            {
                _stateLock.Release();
            }

            // Set pending order for main loop to track (NO Task.Run!)
            _pendingOrderId = order.OrderId;
            _pendingOrderSymbol = symbol;
            _pendingOrderQuantity = quantity;
            _pendingOrderBasePrice = basePrice;
            _pendingOrderEffectivePrice = effectivePrice;
            _pendingOrderSubmitTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BUY] Failed to submit order");
            
            // Activate buy cooldown to prevent rapid-fire retries
            ActivateBuyCooldown(symbol == _settings.BearSymbol ? "BEAR" : "BULL");
            
            // Rollback state (thread-safe)
            await _stateLock.WaitAsync();
            try
            {
                _state.AvailableCash = originalAvailableCash; // Restore cash to pre-order state
                _state.CurrentPosition = null;
                _state.CurrentShares = 0;
                _stateManager.Save(_state);
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    private void PersistTrailingStopState()
    {
        _stateLock.Wait();
        try
        {
            _state.HighWaterMark = _highWaterMark > 0 ? _highWaterMark : null;
            _state.LowWaterMark = _lowWaterMark > 0 ? _lowWaterMark : null;
            _state.TrailingStopValue = _virtualStopPrice > 0 ? _virtualStopPrice : null;
            _state.IsStoppedOut = _isStoppedOut;
            _state.StoppedOutDirection = _isStoppedOut ? _stoppedOutDirection : null;
            _state.WashoutLevel = _isStoppedOut ? _washoutLevel : null;
            _state.StopoutTimestamp = _stopoutTime?.ToString("o");
            _stateManager.Save(_state);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void ClearTrailingStopState()
    {
        _state.HighWaterMark = null;
        _state.LowWaterMark = null;
        _state.TrailingStopValue = null;
        // Reset in-memory trailing stop fields to prevent stale ratchet/stop calculations
        _highWaterMark = 0m;
        _lowWaterMark = 0m;
        _etfHighWaterMark = 0m;
        _virtualStopPrice = 0m;
        _lastRatchetTierTrigger = -1m;
        _isTrendRescuePosition = false;
        _isDriftPosition = false;
        // Keep IsStoppedOut for washout latch
    }
    
    /// <summary>
    /// Applies the Split Allocation Model for profit distribution.
    /// When a position closes with profit:
    /// - (1 - ReinvestmentPercent) goes to Bank (AccumulatedLeftover)
    /// - ReinvestmentPercent stays available for compounding
    /// Updates RealizedSessionPnL for buying power calculation.
    /// </summary>
    /// <param name="proceeds">Total proceeds from the sale</param>
    /// <param name="costBasis">Original cost basis of shares sold</param>
    private void ApplySplitAllocation(decimal proceeds, decimal costBasis)
    {
        var profit = proceeds - costBasis;
        
        // Always credit proceeds to AvailableCash first
        _state.AvailableCash += proceeds;
        
        if (profit > 0)
        {
            // Update session P/L for buying power calculation
            _state.RealizedSessionPnL += profit;
            
            // Calculate how much to bank (protect) vs reinvest (compound)
            var amountToBanked = profit * (1m - _settings.ProfitReinvestmentPercent);
            
            // Move banked portion from AvailableCash to AccumulatedLeftover
            _state.AccumulatedLeftover += amountToBanked;
            _state.AvailableCash -= amountToBanked;
            
            _logger.LogInformation(
                "[PROFIT] Realized: +${Profit:N2} | Compounded: ${Reinv:N2} | SessionPnL: ${Session:N2}",
                profit, 
                profit - amountToBanked,
                _state.RealizedSessionPnL);
            
            // Check Daily Profit Target (post-trade mode)
            var effectiveTarget = _settings.EffectiveDailyProfitTarget;
            if (effectiveTarget > 0 && _state.RealizedSessionPnL >= effectiveTarget && _state.HaltReason == HaltReason.None && !_dailyTargetArmed)
            {
                if (_settings.DailyProfitTargetTrailingStopPercent > 0)
                {
                    // Trailing stop mode: arm the stop but keep trading
                    _dailyTargetArmed = true;
                    _dailyTargetPeakPnL = _state.RealizedSessionPnL;
                    _dailyTargetStopLevel = _state.RealizedSessionPnL * (1m - _settings.DailyProfitTargetTrailingStopPercent / 100m);
                    _state.DailyTargetArmed = true;
                    _state.DailyTargetPeakPnL = _dailyTargetPeakPnL;
                    _state.DailyTargetStopLevel = _dailyTargetStopLevel;
                    _logger.LogInformation(
                        "[DAILY TARGET] ★ Session P/L ${PnL:N2} reached target ${Target:N2}. Trailing stop ARMED at ${Stop:N2} ({Pct}% trail). Continuing to trade.",
                        _state.RealizedSessionPnL, effectiveTarget, _dailyTargetStopLevel, _settings.DailyProfitTargetTrailingStopPercent);
                }
                else
                {
                    // Legacy mode: immediate stop
                    SetHaltReason(HaltReason.ProfitTarget, "Daily profit target reached (post-trade, legacy)");
                    _logger.LogInformation(
                        "[DAILY TARGET] ★ Session P/L ${PnL:N2} reached target ${Target:N2}. No more trades today.",
                        _state.RealizedSessionPnL, effectiveTarget);
                }
            }
        }
        else if (profit < 0)
        {
            // Loss: track in session P/L (affects buying power negatively)
            _state.RealizedSessionPnL += profit;
            _logger.LogWarning("[LOSS] Realized: ${Profit:N2} | SessionPnL: ${Session:N2}",
                profit, _state.RealizedSessionPnL);
        }
        
        // Check Daily Loss Limit (applies after both wins and losses — session P/L might be negative overall)
        var effectiveLossLimit = _settings.EffectiveDailyLossLimit;
        if (effectiveLossLimit > 0 && _state.RealizedSessionPnL <= -effectiveLossLimit && _state.HaltReason == HaltReason.None)
        {
            SetHaltReason(HaltReason.LossLimit, "Daily loss limit breached");
            _logger.LogWarning(
                "[DAILY LOSS LIMIT] ★ Session P/L ${PnL:N2} breached loss limit -${Limit:N2}. No more trades today.",
                _state.RealizedSessionPnL, effectiveLossLimit);
        }
        
        // Non-blocking equity check after every realized P/L event
        FireEquityCheck($"SELL profit={profit:+0.00;-0.00}");
    }
    
    /// <summary>
    /// Checks if position should be trimmed and executes the trim.
    /// Trimming sells a portion of a winning position when momentum fades.
    /// 
    /// Conditions for trim:
    /// 1. Unrealized P/L % > TrimTriggerPercent (P/L Gate)
    /// 2. Slope < TrimSlopeThreshold (Momentum fading - Technical Key)
    /// 3. Time since last trim > TrimCooldownSeconds (Cooldown)
    /// 4. Position is profitable (Wash Sale protection)
    /// </summary>
    private async Task CheckAndExecuteTrimAsync(MarketRegime regime, CancellationToken ct)
    {
        // Get current ETF price
        decimal currentTickerPrice = 0m;
        if (_state.CurrentPosition == _settings.BullSymbol)
            currentTickerPrice = regime.BullPrice ?? 0m;
        else if (_state.CurrentPosition == _settings.BearSymbol)
            currentTickerPrice = regime.BearPrice ?? 0m;
        
        if (currentTickerPrice <= 0 || !_state.AverageEntryPrice.HasValue)
            return;
        
        // Calculate unrealized P/L
        var costBasis = _state.CurrentShares * _state.AverageEntryPrice.Value;
        var currentValue = _state.CurrentShares * currentTickerPrice;
        var unrealizedPL = currentValue - costBasis;
        var unrealizedPLPercent = costBasis > 0 ? unrealizedPL / costBasis : 0m;
        
        // WASH SALE PROTECTION: Only trim profitable positions
        if (unrealizedPL <= 0)
            return;
        
        // P/L GATE: Check if profit threshold met
        if (unrealizedPLPercent < _settings.TrimTriggerPercent)
            return;
        
        // TECHNICAL KEY: Check if momentum is fading (slope below threshold)
        if (regime.Slope >= _settings.TrimSlopeThreshold)
            return;
        
        // COOLDOWN: Check if enough time has passed since last trim
        if (_state.LastTrimTime.HasValue)
        {
            var secondsSinceLastTrim = (DateTime.UtcNow - _state.LastTrimTime.Value).TotalSeconds;
            if (secondsSinceLastTrim < _settings.TrimCooldownSeconds)
                return;
        }
        
        // All conditions met - execute trim
        var sharesToSell = (long)System.Math.Floor(_state.CurrentShares * _settings.TrimRatio);
        if (sharesToSell <= 0)
        {
            _logger.LogDebug("[TRIM] Position too small to trim ({Shares} shares)", _state.CurrentShares);
            return;
        }
        
        _logger.LogInformation(
            "[TRIM] Conditions met: PnL +{PnLPct:P2} > {Threshold:P2} | Slope {Slope:F8} < {SlopeThresh:F8} | Selling {Shares} of {Total}",
            unrealizedPLPercent, _settings.TrimTriggerPercent,
            regime.Slope, _settings.TrimSlopeThreshold,
            sharesToSell, _state.CurrentShares);
        
        await TrimPositionAsync(sharesToSell, currentTickerPrice, ct);
    }
    
    /// <summary>
    /// Executes a trim by selling a portion of the current position.
    /// Uses market orders for reliability. Accepts partial fills.
    /// </summary>
    private async Task TrimPositionAsync(long sharesToSell, decimal knownPrice, CancellationToken ct)
    {
        var symbol = _state.CurrentPosition;
        if (string.IsNullOrEmpty(symbol))
            return;
        
        var request = new BotOrderRequest
        {
            Symbol = symbol,
            Quantity = sharesToSell,
            Side = BotOrderSide.Sell,
            Type = BotOrderType.Market,
            TimeInForce = BotTimeInForce.Day,
            ClientOrderId = _settings.GenerateClientOrderId(),
            HintPrice = knownPrice
        };
        
        try
        {
            var order = await _broker.SubmitOrderAsync(request, ct);
            _logger.LogInformation("[TRIM] Order submitted: Selling {Qty} shares of {Symbol}", sharesToSell, symbol);
            
            // Wait for fill (with timeout)
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500, ct);
                var filledOrder = await _broker.GetOrderAsync(order.OrderId, ct);
                
                if (filledOrder.Status == BotOrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                {
                    var proceeds = filledOrder.FilledQuantity * filledOrder.AverageFillPrice.Value;
                    var soldCostBasis = filledOrder.FilledQuantity * (_state.AverageEntryPrice ?? knownPrice);
                    
                    await _stateLock.WaitAsync();
                    try
                    {
                        // Apply split allocation for profit distribution
                        ApplySplitAllocation(proceeds, soldCostBasis);
                        
                        // Update position
                        _state.CurrentShares -= filledOrder.FilledQuantity;
                        _state.LastTrimTime = DateTime.UtcNow;
                        
                        if (_state.CurrentShares <= 0)
                        {
                            _state.CurrentPosition = null;
                            _state.CurrentShares = 0;
                            _state.AverageEntryPrice = null;
                            ClearTrailingStopState();
                        }
                        
                        _stateManager.Save(_state);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    
                    _logger.LogInformation("[TRIM] Complete: Sold {Qty} @ ${Price:N4} | Remaining: {Remaining} shares",
                        filledOrder.FilledQuantity, filledOrder.AverageFillPrice.Value, _state.CurrentShares);
                    return;
                }
                
                // Accept partial fills
                if (filledOrder.FilledQuantity > 0 && filledOrder.AverageFillPrice.HasValue)
                {
                    var proceeds = filledOrder.FilledQuantity * filledOrder.AverageFillPrice.Value;
                    var soldCostBasis = filledOrder.FilledQuantity * (_state.AverageEntryPrice ?? knownPrice);
                    
                    await _stateLock.WaitAsync();
                    try
                    {
                        ApplySplitAllocation(proceeds, soldCostBasis);
                        _state.CurrentShares -= filledOrder.FilledQuantity;
                        _state.LastTrimTime = DateTime.UtcNow;
                        _stateManager.Save(_state);
                    }
                    finally
                    {
                        _stateLock.Release();
                    }
                    
                    _logger.LogInformation("[TRIM] Partial: Sold {Qty} @ ${Price:N4} | Remaining: {Remaining} shares",
                        filledOrder.FilledQuantity, filledOrder.AverageFillPrice.Value, _state.CurrentShares);
                    return; // Accept partial and move on (trim is an optimization, not critical)
                }
                
                if (filledOrder.Status == BotOrderStatus.Canceled || filledOrder.Status == BotOrderStatus.Rejected)
                {
                    _logger.LogWarning("[TRIM] Order {Status}. Trim aborted.", filledOrder.Status);
                    return;
                }
            }
            
            _logger.LogWarning("[TRIM] Order did not fill in time. Will retry on next tick if conditions still met.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TRIM] Failed to execute trim order");
        }
    }

    /// <summary>
    /// Cleanup orphaned shares from previous partial liquidation.
    /// Called after a successful position switch to sell remaining shares.
    /// </summary>
    private async Task CleanupOrphanedSharesAsync(CancellationToken ct)
    {
        if (_state.OrphanedShares == null || _state.OrphanedShares.Shares <= 0)
        {
            return; // No orphans to clean up
        }
        
        var orphan = _state.OrphanedShares;
        _logger.LogInformation("[ORPHAN] Cleaning up {Shares} orphaned share(s) of {Symbol}...", 
            orphan.Shares, orphan.Symbol);
            
        // SAFEGUARD: Verify we actually hold these shares before selling
        try 
        {
            var pos = await _broker.GetPositionAsync(orphan.Symbol, ct);
            if (pos == null || pos.Value.Quantity == 0)
            {
                 _logger.LogWarning("[ORPHAN] Broker reports no position for {Symbol}. Clearing phantom orphan state.", orphan.Symbol);
                 await _stateLock.WaitAsync(ct);
                 try {
                     _state.OrphanedShares = null;
                     _stateManager.Save(_state);
                 } finally {
                     _stateLock.Release();
                 }
                 return;
            }
            
            // If we have FEWER shares than orphan state says, adjust state?
            if (pos.Value.Quantity < orphan.Shares)
            {
                _logger.LogWarning("[ORPHAN] Adjusting orphan qty from {State} to {Broker} to match broker.", orphan.Shares, pos.Value.Quantity);
                orphan.Shares = pos.Value.Quantity; 
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "[ORPHAN] Failed to verify broker position. Skipping cleanup.");
             return;
        }
        
        try
        {
            var orphanPrice = await _broker.GetLatestPriceAsync(orphan.Symbol);
            var limitPrice = System.Math.Round(orphanPrice - (_settings.IocLimitOffsetCents / 100m), 2);
            
            // Use IOC with extra retries for cleanup
            var result = await _iocExecutor.ExecuteAsync(
                orphan.Symbol,
                orphan.Shares,
                BotOrderSide.Sell,
                limitPrice,
                _settings.IocRetryStepCents,
                _settings.IocMaxRetries + 2,
                _settings.IocMaxDeviationPercent * 2);
            
            if (result.FilledQty > 0)
            {
                await _stateLock.WaitAsync();
                try
                {
                    // Orphan cleanup: Track P/L using average entry price as best-effort cost basis.
                    // If no cost basis available, use fill price (break-even).
                    var costBasisPerShare = _state.AverageEntryPrice ?? result.AvgPrice;
                    var orphanCostBasis = result.FilledQty * costBasisPerShare;
                    ApplySplitAllocation(result.TotalProceeds, orphanCostBasis);
                    
                    if (result.FilledQty >= orphan.Shares)
                    {
                        _state.OrphanedShares = null;
                        _stateManager.Save(_state);
                        _logger.LogInformation("[ORPHAN] Cleanup complete: Sold {Qty} @ ${Price:N4} (+${Proceeds:N2})",
                            result.FilledQty, result.AvgPrice, result.TotalProceeds);
                        return;
                    }
                    else
                    {
                        _state.OrphanedShares.Shares = orphan.Shares - result.FilledQty;
                        _stateManager.Save(_state);
                        _logger.LogWarning("[ORPHAN] Partial cleanup: Sold {Filled}. {Remaining} share(s) still orphaned.",
                            result.FilledQty, _state.OrphanedShares.Shares);
                    }
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            else
            {
                // IOC failed - try market order as fallback
                _logger.LogWarning("[ORPHAN] IOC cleanup failed. Attempting market order...");
                
                var request = new BotOrderRequest
                {
                    Symbol = orphan.Symbol,
                    Quantity = orphan.Shares,
                    Side = BotOrderSide.Sell,
                    Type = BotOrderType.Market,
                    TimeInForce = BotTimeInForce.Day,
                    ClientOrderId = _settings.GenerateClientOrderId(),
                    HintPrice = orphanPrice
                };
                
                try
                {
                    var order = await _broker.SubmitOrderAsync(request);
                    _logger.LogInformation("[ORPHAN] Market order submitted: {OrderId}", order.OrderId);
                    
                    for (int i = 0; i < 10; i++)
                    {
                        await Task.Delay(500, ct);
                        var filledOrder = await _broker.GetOrderAsync(order.OrderId);
                        
                        if (filledOrder.Status == BotOrderStatus.Filled && filledOrder.AverageFillPrice.HasValue)
                        {
                            var proceeds = filledOrder.FilledQuantity * filledOrder.AverageFillPrice.Value;
                            var costBasisPerShare = _state.AverageEntryPrice ?? filledOrder.AverageFillPrice.Value;
                            var orphanCostBasis = filledOrder.FilledQuantity * costBasisPerShare;
                            
                            await _stateLock.WaitAsync();
                            try
                            {
                                ApplySplitAllocation(proceeds, orphanCostBasis);
                                _state.OrphanedShares = null;
                                _stateManager.Save(_state);
                            }
                            finally
                            {
                                _stateLock.Release();
                            }
                            
                            _logger.LogInformation("[ORPHAN] Market cleanup complete: Sold {Qty} @ ${Price:N4} (+${Proceeds:N2})",
                                filledOrder.FilledQuantity, filledOrder.AverageFillPrice.Value, proceeds);
                            return;
                        }
                    }
                    
                    _logger.LogWarning("[ORPHAN] Market order did not fill in time. Orphan remains.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ORPHAN] Market order failed");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ORPHAN] Cleanup failed. Will retry on next cycle.");
        }
    }

    private async Task GracefulShutdownAsync()
    {
        _logger.LogInformation("[TRADER] GracefulShutdownAsync entered.");
        
        // In replay mode (BypassMarketHoursCheck=true), the SimulatedBroker handles
        // everything synchronously — skip the complex broker-query paths that can hang.
        if (_settings.BypassMarketHoursCheck)
        {
            // Just liquidate via the normal path (SimBroker fills instantly)
            if (!string.IsNullOrEmpty(_state.CurrentPosition) && _state.CurrentShares > 0)
            {
                _logger.LogInformation("[TRADER] Replay shutdown - liquidating remaining position");
                await LiquidateCurrentPositionAsync(CancellationToken.None);
            }
            
            await _stateLock.WaitAsync();
            try
            {
                _stateManager.Save(_state);
            }
            finally
            {
                _stateLock.Release();
            }
            
            await LogFinalSummaryAsync();
            _logger.LogInformation("[TRADER] GracefulShutdownAsync completed (replay path).");
            return;
        }
        
        // CRITICAL: End-of-day liquidation MUST happen, even in repair/safe mode.
        // If our local state is unreliable, query the broker directly for positions.
        
        if (_repairModeTriggered || _isSafeMode)
        {
            _logger.LogWarning("[TRADER] Graceful shutdown in {Mode} mode - querying broker for actual positions to liquidate.",
                _repairModeTriggered ? "REPAIR" : "SAFE");
            
            try
            {
                // Query broker directly - don't trust local state
                BotPosition? bullPos = null;
                BotPosition? bearPos = null;
                
                if (!string.IsNullOrEmpty(_settings.BullSymbol))
                {
                    bullPos = await _broker.GetPositionAsync(_settings.BullSymbol);
                }
                
                if (!string.IsNullOrEmpty(_settings.BearSymbol))
                {
                    bearPos = await _broker.GetPositionAsync(_settings.BearSymbol);
                }
                
                if (bullPos.HasValue && bullPos.Value.Quantity > 0)
                {
                    _logger.LogWarning("[TRADER] Found {Qty} shares of {Symbol} at broker. Emergency liquidating.",
                        bullPos.Value.Quantity, _settings.BullSymbol);
                    await EmergencyLiquidateAsync(_settings.BullSymbol!, bullPos.Value.Quantity);
                }
                
                if (bearPos.HasValue && bearPos.Value.Quantity > 0)
                {
                    _logger.LogWarning("[TRADER] Found {Qty} shares of {Symbol} at broker. Emergency liquidating.",
                        bearPos.Value.Quantity, _settings.BearSymbol);
                    await EmergencyLiquidateAsync(_settings.BearSymbol!, bearPos.Value.Quantity);
                }
                
                var bullQty = bullPos?.Quantity ?? 0;
                var bearQty = bearPos?.Quantity ?? 0;
                if (bullQty == 0 && bearQty == 0)
                {
                    _logger.LogInformation("[TRADER] Graceful shutdown - broker confirms no positions. Already flat.");
                }
                
                // CRITICAL FIX: Clear local state to match broker reality after emergency liquidation
                await _stateLock.WaitAsync();
                try
                {
                    _state.CurrentPosition = null;
                    _state.CurrentShares = 0;
                    ClearTrailingStopState();
                    _stateManager.Save(_state);
                    _logger.LogInformation("[TRADER] Final state saved to disk after emergency liquidation.");
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TRADER] CRITICAL: Failed to query/liquidate broker positions during shutdown!");
            }
            
            await LogFinalSummaryAsync();
            return;
        }

        if (!string.IsNullOrEmpty(_state.CurrentPosition) && _state.CurrentShares > 0)
        {
            _logger.LogInformation("[TRADER] Graceful shutdown - liquidating position");
            await LiquidateCurrentPositionAsync(CancellationToken.None);
        }
        else
        {
            _logger.LogInformation("[TRADER] Graceful shutdown - already in cash");
        }
        
        // CRITICAL FIX: Force final state save to disk
        // This ensures state is persisted even if LiquidateCurrentPositionAsync had edge cases
        await _stateLock.WaitAsync();
        try
        {
            _stateManager.Save(_state);
            _logger.LogInformation("[TRADER] Final state saved to disk.");
        }
        finally
        {
            _stateLock.Release();
        }

        // Log final summary
        await LogFinalSummaryAsync();
        _logger.LogInformation("[TRADER] GracefulShutdownAsync completed (live path).");
    }
    
    /// <summary>
    /// Emergency liquidation - bypasses normal state management, directly sells at broker.
    /// Used during shutdown when local state may be unreliable.
    /// </summary>
    private async Task EmergencyLiquidateAsync(string symbol, long quantity)
    {
        try
        {
            // Best-effort price hint for SimBroker (not worth failing over)
            decimal? emergencyHintPrice = null;
            try { emergencyHintPrice = await _broker.GetLatestPriceAsync(symbol); } catch { /* best effort */ }
            
            var request = new BotOrderRequest
            {
                Symbol = symbol,
                Quantity = quantity,
                Side = BotOrderSide.Sell,
                Type = BotOrderType.Market,
                TimeInForce = BotTimeInForce.Day,
                ClientOrderId = _settings.GenerateClientOrderId(),
                HintPrice = emergencyHintPrice
            };
            
            var order = await _broker.SubmitOrderAsync(request);
            _logger.LogWarning("[EMERGENCY] Market sell submitted for {Qty} shares of {Symbol}: {OrderId}",
                quantity, symbol, order.OrderId);
            
            // Wait briefly for fill confirmation
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                var filled = await _broker.GetOrderAsync(order.OrderId);
                if (filled.Status == BotOrderStatus.Filled)
                {
                    _logger.LogInformation("[EMERGENCY] Liquidation complete: {Qty} @ ${Price:N4}",
                        filled.FilledQuantity, filled.AverageFillPrice);
                    return;
                }
            }
            
            _logger.LogWarning("[EMERGENCY] Order may not have filled completely. Check broker manually.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EMERGENCY] Failed to liquidate {Symbol}. MANUAL INTERVENTION REQUIRED!", symbol);
        }
    }
    
    private async Task LogStatusAsync(MarketRegime regime, string signal, CancellationToken ct)
    {
        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
        var price = regime.BenchmarkPrice;
        
        // Calculate current balance (cash + position value using actual ETF price)
        var cashBalance = _state.AvailableCash + _state.AccumulatedLeftover;
        var positionValue = 0m;
        if (_state.CurrentShares > 0 && !string.IsNullOrEmpty(_state.CurrentPosition))
        {
            // Get actual ETF price, not benchmark price
            decimal etfPrice = 0m;
            if (_state.CurrentPosition == _settings.BullSymbol && regime.BullPrice.HasValue)
                etfPrice = regime.BullPrice.Value;
            else if (_state.CurrentPosition == _settings.BearSymbol && regime.BearPrice.HasValue)
                etfPrice = regime.BearPrice.Value;
            else
                etfPrice = await _broker.GetLatestPriceAsync(_state.CurrentPosition);
                
            positionValue = _state.CurrentShares * etfPrice;
        }
        var totalBalance = cashBalance + positionValue;
        
        // Calculate P/L
        var dayStartBalance = _state.DayStartBalance > 0 ? _state.DayStartBalance : _state.StartingAmount;
        var dailyPL = totalBalance - dayStartBalance;
        var dailyPLPercent = dayStartBalance > 0 ? (dailyPL / dayStartBalance) * 100 : 0;
        
        // Calculate Deployed Capital (Cost Basis) and Unrealized P/L
        decimal deployedCapital = 0m;
        decimal unrealizedPL = 0m;
        if (_state.CurrentShares > 0 && _state.AverageEntryPrice.HasValue)
        {
            deployedCapital = _state.CurrentShares * _state.AverageEntryPrice.Value;
            unrealizedPL = positionValue - deployedCapital;
        }

        // Position info
        var posInfo = _state.CurrentPosition != null && _state.CurrentShares > 0
            ? $"{_state.CurrentPosition} x{_state.CurrentShares}"
            : "CASH";
        
        // Build status line
        var plSign = dailyPL >= 0 ? "+" : "-";
        var unPlSign = unrealizedPL >= 0 ? "+" : "-";
        var slippageInfo = _cumulativeSlippage != 0 ? $" | Slip: {(_cumulativeSlippage >= 0 ? "+" : "")}{_cumulativeSlippage:N2}" : "";
        
        // Calculate reinvested capital (compounding amount)
        var reinvestedCapital = _state.RealizedSessionPnL * _settings.ProfitReinvestmentPercent;
        
        // Format: [Time] QQQ: $P | SMA: $S | Signal | Pos | Depl: $D | Avail: $A | Bank: $B | Reinv: $R | Eq: $E | ...
        var phaseTag = _timeRuleApplier?.ActivePhaseName is { } pn ? $" [{pn}]" : "";
        
        _logger.LogInformation(
            "[{Time}]{Phase} {Symbol}: ${Price:N2} | SMA: ${Sma:N2} | {Signal} | {Position} | Depl: ${Deployed:N2} | Avail: ${Avail:N2} | Bank: ${Bank:N2} | Reinv: ${Reinv:N2} | Eq: ${Equity:N2} | Run: {UnPlSign}${UnPl:N2} | Day: {PlSign}${DailyPL:N2} ({PlPercent:N2}%){Slippage}",
            easternNow.ToString("HH:mm:ss"),
            phaseTag,
            _settings.BenchmarkSymbol,
            price,
            regime.SmaValue,
            signal,
            posInfo,
            deployedCapital,
            _state.AvailableCash,
            _state.AccumulatedLeftover,
            reinvestedCapital,
            totalBalance,
            unPlSign,
            System.Math.Abs(unrealizedPL),
            plSign,
            System.Math.Abs(dailyPL),
            dailyPLPercent,
            slippageInfo);
    }
    
    /// <summary>
    /// Background watchdog that monitors the data stream for staleness.
    /// Detects real stream disconnections (no ticks arriving) vs. strategic non-trading
    /// (ticks arriving but bot choosing not to trade — which is correct behavior).
    /// Only runs in live mode — replay streams are inherently connected.
    /// </summary>
    private async Task RunStreamWatchdogAsync(CancellationToken ct)
    {
        var lastCriticalAlertUtc = DateTime.MinValue;
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.StreamWatchdogIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            
            var gapSeconds = (DateTime.UtcNow - _lastTickReceivedUtc).TotalSeconds;
            
            // Don't alert until we've received our first tick — before market open, no data is expected
            // Also don't alert after MARKET_CLOSE — we're intentionally waiting for next session
            if (!_hasReceivedFirstTick || _marketSessionEnded)
                continue;
            
            if (gapSeconds >= _settings.StreamStaleCriticalSeconds)
            {
                // Repeat critical alerts every watchdog interval
                if ((DateTime.UtcNow - lastCriticalAlertUtc).TotalSeconds >= _settings.StreamWatchdogIntervalSeconds)
                {
                    _logger.LogCritical(
                        "[STREAM DISCONNECTED] No market data received for {Gap:N0}s. " +
                        "Last tick: {LastTick:HH:mm:ss} ET. Check data source connection.",
                        gapSeconds,
                        TimeZoneInfo.ConvertTimeFromUtc(_lastTickReceivedUtc, _easternZone));
                    lastCriticalAlertUtc = DateTime.UtcNow;
                }
            }
            else if (gapSeconds >= _settings.StreamStaleWarnSeconds)
            {
                _logger.LogWarning(
                    "[STREAM STALE] No market data for {Gap:N0}s. Last tick: {LastTick:HH:mm:ss} ET.",
                    gapSeconds,
                    TimeZoneInfo.ConvertTimeFromUtc(_lastTickReceivedUtc, _easternZone));
            }
        }
    }
    
    private async Task LogFinalSummaryAsync()
    {
        var cashBalance = _state.AvailableCash + _state.AccumulatedLeftover;
        var totalPL = cashBalance - _state.StartingAmount;
        var totalPLPercent = _state.StartingAmount > 0 ? (totalPL / _state.StartingAmount) * 100 : 0;
        
        var dayStartBalance = _state.DayStartBalance > 0 ? _state.DayStartBalance : _state.StartingAmount;
        var dailyPL = cashBalance - dayStartBalance;
        var dailyPLPercent = dayStartBalance > 0 ? (dailyPL / dayStartBalance) * 100 : 0;
        
        _logger.LogInformation("=== Trading Session Summary ===");
        _logger.LogInformation("  Starting Amount:  ${Starting:N2}", _state.StartingAmount);
        _logger.LogInformation("  Day Start:        ${DayStart:N2}", dayStartBalance);
        _logger.LogInformation("  Final Balance:    ${Balance:N2}", cashBalance);
        _logger.LogInformation("  Daily P/L:        {Sign}${PL:N2} ({Percent:N2}%)", 
            dailyPL >= 0 ? "+" : "-", System.Math.Abs(dailyPL), dailyPLPercent);
        _logger.LogInformation("  Total P/L:        {Sign}${PL:N2} ({Percent:N2}%)", 
            totalPL >= 0 ? "+" : "-", System.Math.Abs(totalPL), totalPLPercent);
        
        if (_cumulativeSlippage != 0)
        {
            _logger.LogInformation("  Cumulative Slip:  {Sign}${Slip:N2}", 
                _cumulativeSlippage >= 0 ? "+" : "-", System.Math.Abs(_cumulativeSlippage));
        }
        
        // Broker equity reconciliation — compare bot's P/L with broker's actual equity
        try
        {
            // Use GetEquityAsync for actual broker equity (cash + positions)
            // NOT GetBuyingPowerAsync, which returns margin-adjusted buying power on many brokers
            var brokerEquity = await _broker.GetEquityAsync();
            
            // Get position value for display breakdown
            var brokerPositionValue = 0m;
            var positions = await _broker.GetAllPositionsAsync();
            foreach (var pos in positions)
            {
                brokerPositionValue += pos.MarketValue ?? (pos.Quantity * (pos.CurrentPrice ?? pos.AverageEntryPrice));
            }
            
            // Derive cash from equity - positions (for display only)
            var brokerCash = brokerEquity - brokerPositionValue;
            
            _logger.LogInformation("  --- Broker Reconciliation ---");
            _logger.LogInformation("  Broker Cash:      ${Cash:N2}", brokerCash);
            _logger.LogInformation("  Broker Positions: ${Pos:N2}", brokerPositionValue);
            _logger.LogInformation("  Broker Equity:    ${Equity:N2}", brokerEquity);
            
            // Calculate delta using broker's actual day-start equity if available (more accurate)
            var brokerBaseline = _state.BrokerDayStartEquity ?? dayStartBalance;
            var brokerDailyPL = brokerEquity - brokerBaseline;
            var delta = dailyPL - brokerDailyPL;
            
            _logger.LogInformation("  Broker Day Start: ${DayStart:N2}{Source}",
                brokerBaseline,
                _state.BrokerDayStartEquity.HasValue ? " (broker)" : " (bot estimate)");
            _logger.LogInformation("  Broker Daily P/L: {Sign}${PL:N2}",
                brokerDailyPL >= 0 ? "+" : "-",
                System.Math.Abs(brokerDailyPL));
            
            if (System.Math.Abs(delta) > 0.01m)
            {
                _logger.LogWarning("  ⚠ DESYNC DELTA:   {Sign}${Delta:N2} (bot - broker)",
                    delta >= 0 ? "+" : "-", System.Math.Abs(delta));
            }
            else
            {
                _logger.LogInformation("  ✓ Bot and broker P/L are in sync.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "  Broker reconciliation unavailable (expected in replay if SimBroker has no positions).");
        }
    }
    
    /// <summary>
    /// Update cumulative slippage tracking.
    /// </summary>
    public void AddSlippage(decimal slippage)
    {
        lock (_slippageLock)
        {
            _cumulativeSlippage += slippage;
        }
    }
    
    /// <summary>
    /// Fire-and-forget broker equity check. Logs the delta between bot's internal equity
    /// and the broker's actual equity. This is diagnostic — it never blocks trading.
    /// </summary>
    private void FireEquityCheck(string trigger)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var brokerEquity = await _broker.GetEquityAsync();
                var botEquity = _state.AvailableCash + _state.AccumulatedLeftover;
                // Include unrealized position value if we hold shares
                if (_state.CurrentShares > 0 && _state.AverageEntryPrice.HasValue)
                {
                    try
                    {
                        var price = await _broker.GetLatestPriceAsync(_state.CurrentPosition!);
                        botEquity += _state.CurrentShares * price;
                    }
                    catch { /* best effort */ }
                }
                
                var delta = botEquity - brokerEquity;
                
                if (System.Math.Abs(delta) > 0.50m)
                {
                    _logger.LogWarning("[EQUITY CHECK] {Trigger} | Bot: ${BotEq:N2} | Broker: ${BrokerEq:N2} | Delta: {Sign}${Delta:N2}",
                        trigger, botEquity, brokerEquity,
                        delta >= 0 ? "+" : "-", System.Math.Abs(delta));
                }
                else
                {
                    _logger.LogInformation("[EQUITY CHECK] {Trigger} | Bot: ${BotEq:N2} | Broker: ${BrokerEq:N2} | ✓ In sync",
                        trigger, botEquity, brokerEquity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[EQUITY CHECK] Failed ({Trigger})", trigger);
            }
        });
    }
    
    /// <summary>
    /// Sets the halt reason, persists state immediately, and optionally arms PH Resume.
    /// Central method for all halt transitions to ensure consistent state management.
    /// </summary>
    private void SetHaltReason(HaltReason reason, string context)
    {
        _state.HaltReason = reason;
        
        // Arm PH Resume if: profit target halt + feature enabled + before Power Hour
        if (reason == HaltReason.ProfitTarget && _settings.ResumeInPowerHour)
        {
            var easternNow = TimeZoneInfo.ConvertTimeFromUtc(_currentUtc, _easternZone);
            // Only arm if before 14:00 ET (Power Hour start)
            // If target fires during or after PH, standard halt applies
            if (easternNow.TimeOfDay < new TimeSpan(14, 0, 0))
            {
                _state.PhResumeArmed = true;
                _logger.LogInformation(
                    "[PH RESUME] Daily target fired at {Time:HH:mm:ss}. PH resume armed — will resume trading at 14:00.",
                    easternNow);
            }
        }
        
        // Force immediate flush for critical halt transitions
        _stateManager.Save(_state, forceImmediate: true);
    }
    
    /// <summary>
    /// Checks if PH Resume should activate on a phase transition to Power Hour.
    /// Called after TimeRuleApplier detects a phase change.
    /// </summary>
    private void CheckPhResume()
    {
        if (!_state.PhResumeArmed) return;
        
        var currentPhase = _timeRuleApplier?.ActivePhaseName;
        
        // Only resume when transitioning INTO Power Hour
        if (currentPhase != "Power Hour") return;
        
        // Clear halt state — resume trading
        _state.HaltReason = HaltReason.None;
        _state.PhResumeArmed = false;
        
        // Reset daily target tracking (don't re-arm on PH gains)
        _dailyTargetArmed = false;
        _dailyTargetPeakPnL = 0m;
        _dailyTargetStopLevel = 0m;
        _state.DailyTargetArmed = false;
        _state.DailyTargetPeakPnL = null;
        _state.DailyTargetStopLevel = null;
        
        // Disable daily profit target for the PH session
        // (settings are mutated in-place — same pattern as TimeRuleApplier)
        _settings.DailyProfitTargetPercent = 0m;
        _settings.DailyProfitTarget = 0m;
        
        // Persist immediately
        _stateManager.Save(_state, forceImmediate: true);
        
        _logger.LogInformation(
            "╔══════════════════════════════════════════════════════╗");
        _logger.LogInformation(
            "║  ★ PH RESUME: Resuming trading for Power Hour       ║");
        _logger.LogInformation(
            "║    Daily target DISABLED for PH session              ║");
        _logger.LogInformation(
            "╚══════════════════════════════════════════════════════╝");
    }
}

/// <summary>
/// Exception thrown when a critical trading operation fails.
/// Used to trigger Safe Mode in the TraderEngine.
/// </summary>
public class TradingException : Exception
{
    public TradingException(string message) : base(message) { }
    public TradingException(string message, Exception innerException) : base(message, innerException) { }
}
