using System.Threading.Channels;
using MarketBlocks.Trade.Domain;
using MarketBlocks.Trade.Interfaces;
using MarketBlocks.Trade.Math;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Analyst Engine - Producer in the Producer/Consumer pattern.
/// Subscribes to market data, calculates SMA/bands, and emits MarketRegime signals.
/// </summary>
public class AnalystEngine : BackgroundService
{
    private readonly ILogger<AnalystEngine> _logger;
    private readonly TradingSettings _settings;
    private readonly IAnalystMarketDataSource _marketSource;
    private readonly Channel<MarketRegime> _regimeChannel;
    private readonly Channel<PriceTick> _priceChannel;
    private IncrementalSma _benchmarkSma;
    private IncrementalSma _cryptoSma;
    private StreamingSlope _smaSlopeCalc;
    private StreamingSlope _exitSlopeCalc; // Slow slope for holding (2x window)
    private readonly TimeZoneInfo _easternZone;
    
    // Historical data source for hydration (Cold Start)
    private readonly IMarketDataSource? _historicalDataSource;
    
    // Fallback adapter for hydration (FMP) when primary fails (Alpaca SIP restriction)
    private readonly IMarketDataAdapter? _fallbackDataAdapter;
    
    // Trend SMA for Hybrid Engine (long-term trend detection)
    private IncrementalSma _trendSma;
    
    // Short-term trend slope for adaptive trend detection during warmup
    private StreamingSlope _shortTrendSlope;
    
    // Time-based phase switching (auto config override)
    private readonly TimeRuleApplier? _timeRuleApplier;
    
    // Sliding Bands feature: needs position awareness (optional feature)
    private readonly Func<string?> _getCurrentPosition;
    private readonly Func<long> _getCurrentShares;
    
    // Signal state persistence (separation of concerns - Analyst owns its own state)
    private readonly Func<string?> _getLastSignal;
    private readonly Action<string> _saveLastSignal;
    
    // Replay clock: invoked when each tick is read from the price channel (before processing).
    // In replay mode, this is where ClockOverride should advance — at the consumer (analyst),
    // not the producer (ReplayMarketDataSource) — so the clock can't race ahead of processing.
    private readonly Action<DateTime>? _onTickProcessed;
    
    // Internal state
    private decimal _latestBenchmarkPrice;
    private decimal _latestCryptoPrice;
    private decimal? _latestBullPrice;
    private decimal? _latestBearPrice;
    private decimal _slidingBandHigh;
    private decimal _slidingBandLow;
    private string _lastSignal = "NEUTRAL"; // State tracking for Hysteresis
    private int _sustainedVelocityTicks = 0; // Trap filter: ticks of sustained velocity for BULL entry
    private int _sustainedBearTicks = 0; // Trap filter: ticks of sustained velocity for BEAR entry (mirrors BULL confirmation)
    private bool _currentSignalIsTrendRescue; // Tracks whether current BULL signal was initiated via trendRescue
    
    // Drift Mode: sustained price position above/below SMA for velocity-independent entry
    private int _consecutiveTicksAboveSma;
    private int _consecutiveTicksBelowSma;
    private bool _isDriftEntry; // true when current position was initiated by drift mode
    private bool _bullDriftConsumedThisPhase; // true after BULL drift fires — prevents re-entry
    private bool _bearDriftConsumedThisPhase; // true after BEAR drift fires — prevents re-entry
    
    // Displacement Re-Entry: track price at last directional→NEUTRAL transition
    private decimal? _lastNeutralTransitionPrice;
    private bool _isDisplacementReentry; // true when current position was initiated by displacement re-entry
    private bool _displacementConsumedThisPhase; // one-shot per phase — prevents cascading re-entries
    
    // BBW (Bollinger Band Width) SMA for volatility expansion detection
    // Tracks rolling average of BBW to compare current BBW against recent average
    private IncrementalSma? _bbwSma;
    
    // Displacement slope: price velocity check to filter drift from drive (StreamingSlope of candle close)
    private StreamingSlope? _displacementSlope;
    
    // Volatility "Weather Station" - rolling 60s candle tracker
    private readonly VolatilityTracker _volatilityTracker = new(60);
    
    // Cycle "Rhythm Detector" - zero-crossing tracker for slope oscillation
    private readonly CycleTracker _cycleTracker = new();
    
    // Mean-reversion indicators (Bollinger Bands + Choppiness Index + ATR + RSI)
    private StreamingBollingerBands? _bollingerBands;
    private ChoppinessIndex? _chopIndex;
    private StreamingATR? _mrAtr;    // ATR for dynamic MR stop-loss (computed on benchmark candles)
    private StreamingRSI? _mrRsi;   // RSI for MR entry confirmation (prevents catching falling knives)
    
    // Candle aggregation for MR indicators (needs OHLC candles, not raw ticks)
    private decimal _chopCandleHigh;
    private decimal _chopCandleLow;
    private decimal _chopCandleClose;
    private DateTime _chopCandleStart;
    private bool _chopCandleActive;
    
    // Current active strategy mode (determined per-tick by DetermineStrategyMode)
    private StrategyMode _activeStrategy = StrategyMode.Trend;
    // Schmitt trigger: true when CHOP dropped below ChopLowerThreshold; stays true until CHOP > ChopTrendExitThreshold
    private bool _trendRescueActive;
    // Track last MR signal for hysteresis (mirrors _lastSignal for trend)
    private string _lastMrSignal = "MR_FLAT";

    public ChannelReader<MarketRegime> RegimeChannel => _regimeChannel.Reader;
    
    public AnalystEngine(
        ILogger<AnalystEngine> logger,
        TradingSettings settings,
        IAnalystMarketDataSource marketSource,
        IMarketDataSource? historicalDataSource,
        IMarketDataAdapter? fallbackDataAdapter,
        Func<string?> getCurrentPosition,
        Func<long> getCurrentShares,
        Func<string?> getLastSignal,
        Action<string> saveLastSignal,
        TimeRuleApplier? timeRuleApplier = null,
        Action<DateTime>? onTickProcessed = null)
    {
        _logger = logger;
        _settings = settings;
        _marketSource = marketSource;
        _historicalDataSource = historicalDataSource;
        _fallbackDataAdapter = fallbackDataAdapter;
        _getCurrentPosition = getCurrentPosition;
        _getCurrentShares = getCurrentShares;
        _getLastSignal = getLastSignal;
        _saveLastSignal = saveLastSignal;
        _timeRuleApplier = timeRuleApplier;
        _onTickProcessed = onTickProcessed;
        
        // Replay mode: bounded(1) channels create a serialized pipeline.
        // The analyst can't buffer regimes ahead of the trader, and the replay source
        // can't buffer ticks ahead of the analyst. This ensures deterministic replay:
        // each tick is fully processed (analyst → trader) before the next one enters.
        // Live mode: unbounded channels — real-time parallelism is correct.
        if (_settings.BypassMarketHoursCheck)
        {
            _regimeChannel = Channel.CreateBounded<MarketRegime>(new BoundedChannelOptions(1)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _priceChannel = Channel.CreateBounded<PriceTick>(new BoundedChannelOptions(1)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        }
        else
        {
            _regimeChannel = Channel.CreateUnbounded<MarketRegime>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true
            });
            _priceChannel = Channel.CreateUnbounded<PriceTick>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true
            });
        }
        
        // Initialize SMAs
        _benchmarkSma = new IncrementalSma(_settings.SMALength);
        _cryptoSma = new IncrementalSma(_settings.SMALength);
        
        // Initialize Slope Calculators for Two-Slope System
        _smaSlopeCalc = new StreamingSlope(_settings.SlopeWindowSize);        // Fast (Entry)
        _exitSlopeCalc = new StreamingSlope(_settings.SlopeWindowSize * 2);   // Slow (Exit) - 2x smoothing
        
        // Initialize Trend SMA for Hybrid Engine (Velocity + Trend)
        var trendSmaLength = System.Math.Max(1, _settings.TrendWindowSeconds / _settings.PollingIntervalSeconds);
        _trendSma = new IncrementalSma(trendSmaLength);
        
        // Initialize short-term trend slope for adaptive trend detection
        _shortTrendSlope = new StreamingSlope(_settings.ShortTrendSlopeWindow);
        
        // Initialize Mean-Reversion indicators (BB + CHOP + ATR + RSI)
        // These are always created but only used when the active strategy is MeanReversion.
        _bollingerBands = new StreamingBollingerBands(_settings.BollingerWindow, _settings.BollingerMultiplier);
        _chopIndex = new ChoppinessIndex(_settings.ChopPeriod);
        _mrAtr = new StreamingATR(_settings.ChopPeriod); // ATR shares CHOP's lookback period
        _mrRsi = new StreamingRSI(_settings.MrRsiPeriod);
        
        // BBW SMA for displacement re-entry volatility expansion detection
        if (_settings.DisplacementBbwLookback > 0)
            _bbwSma = new IncrementalSma(_settings.DisplacementBbwLookback);
        
        // Displacement slope for price velocity check (filters drift from drive)
        if (_settings.DisplacementSlopeWindow > 0)
            _displacementSlope = new StreamingSlope(_settings.DisplacementSlopeWindow);
        
        // Eastern timezone for market hours (cross-platform: Windows + Linux/Docker)
        _easternZone = EasternTimeZone.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ANALYST] Starting signal generation engine...");
        
        // --- Restore signal state (Truth Reconciliation) ---
        // Priority 1: Broker Reality. If we hold shares, we MUST be in that regime to enable Cruise Control.
        // Priority 2: Persisted State. If we are in Cash, we restore the last known signal logic.
        // This prevents accidental liquidation when the state file is missing/corrupted/stale.
        
        var currentPosition = _getCurrentPosition();
        var currentShares = _getCurrentShares();
        var persistedSignal = _getLastSignal();
        
        if (currentShares > 0 && !string.IsNullOrEmpty(currentPosition))
        {
            if (currentPosition == _settings.BullSymbol)
            {
                _lastSignal = "BULL";
                _logger.LogWarning("[ANALYST] State Sync: Forced BULL mode based on existing {Shares} shares of {Symbol}", currentShares, currentPosition);
                // Update persistence to match reality
                _saveLastSignal("BULL"); 
            }
            else if (currentPosition == _settings.BearSymbol)
            {
                _lastSignal = "BEAR";
                _logger.LogWarning("[ANALYST] State Sync: Forced BEAR mode based on existing {Shares} shares of {Symbol}", currentShares, currentPosition);
                _saveLastSignal("BEAR");
            }
            else
            {
                // Holding something else (unexpected symbol)? Log warning and fallback to persistence
                _logger.LogWarning("[ANALYST] Unexpected position {Symbol} ({Shares} shares) - not Bull or Bear symbol. Using persisted state.", currentPosition, currentShares);
                RestoreFromPersistence(persistedSignal);
            }
        }
        else
        {
            // We are in Cash. Restore last signal to handle re-entry logic/hysteresis correctly.
            RestoreFromPersistence(persistedSignal);
        }

        // Local helper to avoid code duplication
        void RestoreFromPersistence(string? signal)
        {
            if (!string.IsNullOrEmpty(signal) && (signal == "BULL" || signal == "BEAR"))
            {
                _lastSignal = signal;
                _logger.LogInformation("[ANALYST] State Restored: {Signal} from persistence file.", signal);
            }
            else
            {
                _logger.LogInformation("[ANALYST] Starting fresh with NEUTRAL state (no valid persisted signal).");
            }
        }
        // ----------------------------------------------------------------
        
        // --- Wait for market hours FIRST, then hydrate ---
        // This ensures pre-market hydration captures the morning "pump/dive" (9:00-9:25 AM)
        // instead of stale data from whenever the bot was started
        await WaitForMarketOpenAsync(stoppingToken);
        
        // --- Hydrate indicators from historical data (Hot Start) ---
        // Now we hydrate with fresh data right before trading begins
        try
        {
            await HydrateSmaAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HYDRATE] Failed to hydrate indicators. Falling back to cold start.");
        }
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for market hours if reconnecting after market close or overnight
                // (First iteration already waited above, but this handles reconnections)
                await WaitForMarketOpenAsync(stoppingToken);
                
                // Connect to market data
                await _marketSource.ConnectAsync(stoppingToken);
                
                // Collect subscriptions
                var subscriptions = new List<AnalystSubscription>
                {
                    new(_settings.BenchmarkSymbol, true)
                };

                // Subscribe to Bull ticker
                if (!string.IsNullOrEmpty(_settings.BullSymbol))
                {
                    subscriptions.Add(new(_settings.BullSymbol, false));
                    _logger.LogInformation("[ANALYST] Adding subscription: {Symbol}", _settings.BullSymbol);
                }
                
                // Subscribe to Bear ticker
                if (!string.IsNullOrEmpty(_settings.BearSymbol))
                {
                    subscriptions.Add(new(_settings.BearSymbol, false));
                    _logger.LogInformation("[ANALYST] Adding subscription: {Symbol}", _settings.BearSymbol);
                }

                // Optionally subscribe to crypto
                if (_settings.WatchBtc || _settings.UseBtcEarlyTrading)
                {
                    subscriptions.Add(new(_settings.CryptoBenchmarkSymbol, false));
                    _logger.LogInformation("[ANALYST] Adding subscription: {Symbol}", _settings.CryptoBenchmarkSymbol);
                }

                // In replay mode (bounded channel), SubscribeAsync writes all ticks
                // synchronously and blocks when the channel is full. We must run
                // the writer (Subscribe) and reader (ProcessMarketData) concurrently
                // so the reader drains the channel while the writer fills it.
                // In live mode (unbounded channel), SubscribeAsync returns quickly
                // after setting up the event-based subscription, so WhenAll is harmless.
                var subscribeTask = _marketSource.SubscribeAsync(subscriptions, _priceChannel.Writer, stoppingToken);
                _logger.LogInformation("[ANALYST] Subscribed to {Count} symbols", subscriptions.Count);
                
                var processTask = ProcessMarketDataLoop(stoppingToken);
                await Task.WhenAll(subscribeTask, processTask);
                
                // In replay mode, data exhaustion is the normal exit — don't retry
                if (_settings.BypassMarketHoursCheck)
                    break;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[ANALYST] Shutdown requested.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ANALYST] Error in signal generation loop");
                // Avoid spin loop on error
                await Task.Delay(5000, stoppingToken);
            }
            finally
            {
                await _marketSource.DisconnectAsync();
                _logger.LogInformation("[ANALYST] Disconnected from market source.");
            }
        }
        
        _regimeChannel.Writer.Complete();
        _logger.LogInformation("[ANALYST] Engine stopped.");
    }
    
    private async Task ProcessMarketDataLoop(CancellationToken stoppingToken)
    {
        await foreach (var tick in _priceChannel.Reader.ReadAllAsync(stoppingToken))
        {
            // In replay mode, advance the clock here (consumer side) so it stays
            // in sync with what's actually being processed, not what's been buffered.
            _onTickProcessed?.Invoke(tick.TimestampUtc);
            
            var regime = ProcessTick(tick);
            if (regime != null)
            {
                await _regimeChannel.Writer.WriteAsync(regime, stoppingToken);
                
                // Check if we need to close session
                if (regime.Signal == "MARKET_CLOSE")
                {
                    var utcRef = _settings.BypassMarketHoursCheck ? tick.TimestampUtc : DateTime.UtcNow;
                    var now = TimeZoneInfo.ConvertTimeFromUtc(utcRef, _easternZone);
                    // Standard close is 4:00 PM (16:00). We signal MARKET_CLOSE slightly before.
                    // If we pass 4:00 PM, we disconnect to reset for next day.
                    if (now.TimeOfDay >= new TimeSpan(16, 0, 0))
                    {
                        if (_settings.BypassMarketHoursCheck)
                        {
                            _logger.LogInformation("[ANALYST] Session ended (16:00 ET). Ending replay.");
                            // Complete the price channel so the SubscribeAsync writer unblocks
                            // from the bounded channel and both tasks can finish cleanly.
                            _priceChannel.Writer.TryComplete();
                        }
                        else
                        {
                            _logger.LogInformation("[ANALYST] Session ended (16:00 ET). Restarting loop to wait for next session.");
                        }
                        return;
                    }
                }
            }
        }
        
        // Channel completed (all data consumed). In replay mode this is the normal exit path.
        if (_settings.BypassMarketHoursCheck)
        {
            _logger.LogInformation("[ANALYST] Data source exhausted. Ending replay.");
        }
    }

    private MarketRegime? ProcessTick(PriceTick tick)
    {
        // In replay/backtest mode, derive the "current" Eastern time from the tick's own timestamp
        // so that market-hours checks, phase switching, and MARKET_CLOSE signals work correctly.
        var utcReference = _settings.BypassMarketHoursCheck ? tick.TimestampUtc : DateTime.UtcNow;
        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(utcReference, _easternZone);
        
        // Check market hours (skipped in replay mode — CSV data is already filtered)
        if (!_settings.BypassMarketHoursCheck && !IsMarketOpen(easternNow))
        {
            return null; // Don't emit signals when market is closed
        }
        
        // --- Time-Based Phase Switching ---
        // Check if we need to transition to a new market phase and reconfigure indicators
        if (_timeRuleApplier != null)
        {
            var snapshot = _timeRuleApplier.SnapshotCurrentSettings();
            if (_timeRuleApplier.CheckAndApply(easternNow.TimeOfDay, "analyst"))
            {
                // Phase changed — check if indicator calculators need rebuilding
                if (_timeRuleApplier.IndicatorSettingsChanged(snapshot))
                {
                    ReconfigureIndicators();
                }
                
                // Analyst phase reset (if configured) — only on PH entry, not OV→Base
                var currentPhase = _timeRuleApplier.ActivePhaseName ?? "Base Config";
                if (currentPhase == "Power Hour" && _settings.AnalystPhaseResetMode != AnalystPhaseResetMode.None)
                {
                    switch (_settings.AnalystPhaseResetMode)
                    {
                        case AnalystPhaseResetMode.Cold:
                            ColdResetIndicators(currentPhase);
                            break;
                        case AnalystPhaseResetMode.Partial:
                            PartialResetIndicators(currentPhase, _settings.AnalystPhaseResetSeconds);
                            break;
                    }
                }
            }
        }
        
        // Track latest prices
        if (tick.IsBenchmark)
        {
            _latestBenchmarkPrice = tick.Price;
            
            // Feed the Weather Station (rolling 60s volatility candle)
            _volatilityTracker.AddTick(tick.Price, tick.TimestampUtc);
            
            // Feed Bollinger Bands + Choppiness Index on candle closes
            // (BB and CHOP share the same candle aggregation interval)
            FeedChopCandle(tick.Price, tick.TimestampUtc);
        }
        else if (tick.Symbol == _settings.BullSymbol)
        {
            _latestBullPrice = tick.Price;
            return null;
        }
        else if (tick.Symbol == _settings.BearSymbol)
        {
            _latestBearPrice = tick.Price;
            return null;
        }
        else
        {
            _latestCryptoPrice = tick.Price;
        }
        
        // Determine if we should use crypto or benchmark based on time
        var earlyTradingEnd = new TimeSpan(9, 55, 0);
        var usesCrypto = _settings.UseBtcEarlyTrading && easternNow.TimeOfDay < earlyTradingEnd;
        
        // Skip ticks that aren't primary for current mode
        if (usesCrypto && tick.IsBenchmark) return null;
        if (!usesCrypto && !tick.IsBenchmark) return null;
        
        var currentPrice = tick.Price;
        
        // O(1) SMA calculation
        var currentSma = tick.IsBenchmark 
            ? _benchmarkSma.Add(currentPrice)
            : _cryptoSma.Add(currentPrice);
        
        // Feed Trend SMA (for Hybrid Engine long-term trend detection)
        if (tick.IsBenchmark)
        {
            _trendSma.Add(currentPrice);
        }
        
        // --- Feed SMA to Slope Calculators (Two-Slope System) ---
        decimal currentSlope = 0m;
        decimal exitSlope = 0m;
        
        if (tick.IsBenchmark) // Only calculate slope on the primary signal source
        {
            // Feed the Fast Slope (Entry)
            _smaSlopeCalc.Add(currentSma);
            currentSlope = _smaSlopeCalc.CurrentSlope;
            
            // Feed the Slow Slope (Exit)
            _exitSlopeCalc.Add(currentSma);
            exitSlope = _exitSlopeCalc.CurrentSlope;
            
            // Feed the Short-Term Trend Slope (for adaptive warmup detection)
            _shortTrendSlope.Add(currentSma);
        }
        
        // Calculate bands
        var (upperBand, lowerBand, bandCenter) = CalculateBands(currentPrice, currentSma, tick.IsBenchmark);
        
        // Determine signal (Two-Slope Hysteresis + Cruise Control)
        var signal = DetermineSignal(currentPrice, upperBand, lowerBand, currentSlope, exitSlope, currentSma, easternNow);
        var originalSignal = signal; // Track for BTC override detection
        
        // --- STRATEGY MODE DETERMINATION ---
        // Check if we should use MeanReversion instead of Trend for this tick.
        // Phase default + optional CHOP dynamic override.
        _activeStrategy = DetermineStrategyMode(easternNow);
        
        // If MR is active, replace signal with MR signal
        if (_activeStrategy == StrategyMode.MeanReversion)
        {
            signal = DetermineMeanReversionSignal(currentPrice, easternNow);
        }
        
        // Apply BTC nudge if in neutral (only for Trend strategy — MR has its own signal logic)
        if (_activeStrategy == StrategyMode.Trend && _settings.WatchBtc && signal == "NEUTRAL" && _cryptoSma.IsFull)
        {
            var btcSma = _cryptoSma.CurrentAverage;
            var btcUpperBand = btcSma * (1 + _settings.ChopThresholdPercent);
            var btcLowerBand = btcSma * (1 - _settings.ChopThresholdPercent);
            
            if (_latestCryptoPrice > btcUpperBand)
                signal = "BULL";
            else if (_latestCryptoPrice < btcLowerBand)
                signal = "BEAR";
            
            // FIX: If BTC changed the signal, update internal state so Cruise Control works next tick
            if (signal != originalSignal)
            {
                _lastSignal = signal;
                _saveLastSignal(signal);
                _logger.LogInformation("[ANALYST] BTC Nudge override: {Old} -> {New}", originalSignal, signal);
            }
        }
        
        // Calculate dynamic velocity thresholds for logging using helper
        var (maintenanceVelocity, entryVelocity) = CalculateVelocityThresholds(currentPrice);
        bool isInTrade = _lastSignal == "BULL" || _lastSignal == "BEAR";
        decimal activeSlope = isInTrade ? exitSlope : currentSlope;
        decimal activeGate = isInTrade ? maintenanceVelocity : entryVelocity;
        
        // --- IMPROVED LOGGING LOGIC ---
        bool calculatorsReady = _smaSlopeCalc.IsReady && _exitSlopeCalc.IsReady;
        
        // Reset trendRescue flag when not in BULL (only meaningful while BULL is active)
        if (signal != "BULL")
            _currentSignalIsTrendRescue = false;
        
        string reason;
        
        // Mean-reversion mode has its own reason logic
        if (_activeStrategy == StrategyMode.MeanReversion)
        {
            var pctB = _bollingerBands?.PercentB;
            var chopVal = _chopIndex?.CurrentValue;
            var rsiVal = _mrRsi?.IsReady == true ? _mrRsi.CurrentValue : (decimal?)null;
            var atrVal = _mrAtr?.IsReady == true ? _mrAtr.CurrentValue : (decimal?)null;
            reason = signal switch
            {
                "MR_LONG" => $"MR: %B={pctB:F3} < {_settings.MrEntryLowPctB} (Buy at lower BB) | RSI={rsiVal:F1} | ATR={atrVal:F4} | CHOP={chopVal:F1}",
                "MR_SHORT" => $"MR: %B={pctB:F3} > {_settings.MrEntryHighPctB} (Sell at upper BB) | RSI={rsiVal:F1} | ATR={atrVal:F4} | CHOP={chopVal:F1}",
                "MR_FLAT" => $"MR: %B={pctB:F3} near mid-band (exit) | RSI={rsiVal:F1} | ATR={atrVal:F4} | CHOP={chopVal:F1}",
                "MARKET_CLOSE" => "Market closing at 3:58 PM ET",
                _ => $"MR: %B={pctB:F3} | RSI={rsiVal:F1} | ATR={atrVal:F4} | CHOP={chopVal:F1}"
            };
        }
        // Priority 1: Explain Blind Hold (Warm-up)
        else if (isInTrade && !calculatorsReady)
        {
            // Show progress using exit slope calc (larger window, required for holding)
            int currentCount = _benchmarkSma.Count;
            int required = _settings.SlopeWindowSize * 2;
            reason = $"Holding Position (Warming up calculators: {currentCount}/{required} ticks)";
        }
        // Priority 2: Explain Ignition Wait
        else if (signal == "NEUTRAL" && _sustainedVelocityTicks > 0)
        {
            reason = $"Waiting for Ignition Confirmation ({_sustainedVelocityTicks}/{_settings.EntryConfirmationTicks}) - Slope: {activeSlope:F4}";
        }
        // Priority 2b: Explain Bear Confirmation Wait
        else if (signal == "NEUTRAL" && _sustainedBearTicks > 0)
        {
            reason = $"Waiting for Bear Confirmation ({_sustainedBearTicks}/{_settings.BearEntryConfirmationTicks}) - Slope: {activeSlope:F4}";
        }
        // Priority 3: Standard Logic
        else
        {
            reason = signal switch
            {
                "BULL" => $"Price {currentPrice:N2} > Upper (Slope {activeSlope:F4} > {activeGate:F4})",
                "BEAR" => $"Price {currentPrice:N2} < Lower (Slope {activeSlope:F4} < -{activeGate:F4})",
                "NEUTRAL" => GetNeutralReason(currentPrice, upperBand, lowerBand, activeSlope, activeGate, isInTrade),
                "MARKET_CLOSE" => "Market closing at 3:58 PM ET",
                _ => "Unknown"
            };
        }
        
        // Get current volatility "weather" from rolling candle tracker
        var (candleHigh, candleLow, _, volPercent) = _volatilityTracker.GetMetrics();
        
        // Feed Cycle Tracker (Rhythm Detector) — zero-crossing detection on the fast slope
        _cycleTracker.AddSlope(currentSlope, utcReference);
        var (cycleSeconds, cycleStability) = _cycleTracker.GetMetrics();
        
        return new MarketRegime(
            Signal: signal,
            BenchmarkPrice: currentPrice,
            SmaValue: currentSma,
            Slope: currentSlope,
            UpperBand: upperBand,
            LowerBand: lowerBand,
            TimestampUtc: utcReference,
            Reason: reason,
            BullPrice: _latestBullPrice,
            BearPrice: _latestBearPrice,
            CandleHigh: candleHigh,
            CandleLow: candleLow,
            VolatilityPercent: volPercent,
            CyclePeriodSeconds: cycleSeconds,
            CycleStability: cycleStability,
            ActiveStrategy: _activeStrategy,
            PercentB: _bollingerBands?.IsReady == true ? (decimal?)_bollingerBands.PercentB : null,
            BollingerUpper: _bollingerBands?.IsReady == true ? (decimal?)_bollingerBands.UpperBand : null,
            BollingerMiddle: _bollingerBands?.IsReady == true ? (decimal?)_bollingerBands.MiddleBand : null,
            BollingerLower: _bollingerBands?.IsReady == true ? (decimal?)_bollingerBands.LowerBand : null,
            ChopIndex: _chopIndex?.IsReady == true ? (decimal?)_chopIndex.CurrentValue : null,
            Rsi: _mrRsi?.IsReady == true ? (decimal?)_mrRsi.CurrentValue : null,
            Atr: _mrAtr?.IsReady == true ? (decimal?)_mrAtr.CurrentValue : null,
            IsTrendRescueEntry: _currentSignalIsTrendRescue,
            IsDriftEntry: _isDriftEntry,
            IsDisplacementReentry: _isDisplacementReentry
        );
    }
    
    private string GetNeutralReason(decimal price, decimal upperBand, decimal lowerBand, decimal slope, decimal velocityGate, bool isInTrade)
    {
        bool calculatorsReady = _smaSlopeCalc.IsReady && _exitSlopeCalc.IsReady;
        if (calculatorsReady && System.Math.Abs(slope) < velocityGate) 
            return $"Velocity Stalled ({(isInTrade ? "Exit" : "Entry")} Slope {slope:F4} < {velocityGate:F4})";
        if (price <= upperBand && price >= lowerBand) 
            return $"Price {price:N2} in band [{lowerBand:N2}-{upperBand:N2}]";
        if (_settings.BullOnlyMode && price < lowerBand) 
            return "Bear Signal ignored (Bull Mode Only)";
        return "Momentum divergence";
    }
    
    /// <summary>
    /// Calculate velocity thresholds with Penny Floor protection.
    /// PENNY FLOOR: Minimum 0.5 cents/min absolute velocity prevents
    /// 1-cent moves on low-priced stocks from triggering false signals.
    /// </summary>
    private (decimal maintenance, decimal entry) CalculateVelocityThresholds(decimal price)
    {
        decimal rawMaintenance = price * _settings.MinVelocityThreshold;

        // OLD: decimal minAbsolute = 0.005m; // Too high for QQQ drift ($0.30/min)
        // NEW: Lower to 0.1 cents ($0.06/min)
        decimal minAbsolute = 0.001m;
        
        decimal effectiveMaintenance = System.Math.Max(rawMaintenance, minAbsolute);
        decimal effectiveEntry = effectiveMaintenance * _settings.EntryVelocityMultiplier;
        
        return (effectiveMaintenance, effectiveEntry);
    }

    private (decimal upper, decimal lower, decimal center) CalculateBands(decimal price, decimal sma, bool isBenchmark)
    {
        // Calculate standard width
        var percentageWidth = sma * _settings.ChopThresholdPercent;
        var effectiveWidth = System.Math.Max(percentageWidth, _settings.MinChopAbsolute);
        
        if (_settings.SlidingBand && isBenchmark)
        {
            var currentPosition = _getCurrentPosition();
            var currentShares = _getCurrentShares();
            
            bool inBull = currentPosition == _settings.BullSymbol && currentShares > 0;
            bool inBear = currentPosition == _settings.BearSymbol && currentShares > 0;
            
            if (inBull)
            {
                // Track high - band slides up
                if (price > _slidingBandHigh || _slidingBandHigh == 0m)
                    _slidingBandHigh = price;
                
                var upper = _slidingBandHigh + (effectiveWidth * _settings.SlidingBandFactor);
                var lower = _slidingBandHigh - (effectiveWidth * _settings.SlidingBandFactor);
                return (upper, lower, _slidingBandHigh);
            }
            else if (inBear)
            {
                // Track low - band slides down
                if (price < _slidingBandLow || _slidingBandLow == 0m)
                    _slidingBandLow = price;
                
                var upper = _slidingBandLow + (effectiveWidth * _settings.SlidingBandFactor);
                var lower = _slidingBandLow - (effectiveWidth * _settings.SlidingBandFactor);
                return (upper, lower, _slidingBandLow);
            }
            else
            {
                // Reset sliding values when neutral
                _slidingBandHigh = 0m;
                _slidingBandLow = 0m;
            }
        }
        
        // Standard SMA-based bands
        return (sma + effectiveWidth, sma - effectiveWidth, sma);
    }

    private string DetermineSignal(
        decimal price, 
        decimal upperBand, 
        decimal lowerBand, 
        decimal entrySlope,  // Fast Slope (for entering trades)
        decimal exitSlope,   // Slow Slope (for holding trades)
        decimal currentSma,  // SMA for Cruise Control calculation
        DateTime easternNow)
    {
        var timeOfDay = easternNow.TimeOfDay;
        var marketCloseCutoff = new TimeSpan(15, 58, 0);
        if (timeOfDay >= marketCloseCutoff) return "MARKET_CLOSE";

        // --- CONFIGURATION ---
        var (maintenanceVelocity, entryVelocity) = CalculateVelocityThresholds(price);
        
        // --- TREND ANALYSIS (HYBRID ENGINE) ---
        // Use 30-minute SMA to detect long-term trend direction
        decimal trendSma = _trendSma.CurrentAverage;
        decimal trendWidth = trendSma * _settings.ChopThresholdPercent;
        decimal effectiveTrendWidth = System.Math.Max(trendWidth, _settings.MinChopAbsolute * 2.0m);
        
        // ADAPTIVE TREND WINDOW: During warmup (buffer not full), scale the trend width
        // proportionally by FillRatio so the trend check isn't impossibly strict with
        // insufficient data. At 48% fill, the band is 48% as wide — proportionally easier
        // to detect a real trend. As data accumulates, threshold naturally widens to full.
        if (_settings.EnableAdaptiveTrendWindow && !_trendSma.IsFull)
        {
            effectiveTrendWidth *= _trendSma.FillRatio;
        }
        
        bool isBullTrend = price > (trendSma + effectiveTrendWidth);
        bool isBearTrend = price < (trendSma - effectiveTrendWidth);
        
        // --- DRIFT MODE: update consecutive-ticks-above/below-SMA counters ---
        if (price > currentSma)
        {
            _consecutiveTicksAboveSma++;
            _consecutiveTicksBelowSma = 0;
        }
        else if (price < currentSma)
        {
            _consecutiveTicksBelowSma++;
            _consecutiveTicksAboveSma = 0;
        }
        else
        {
            _consecutiveTicksAboveSma = 0;
            _consecutiveTicksBelowSma = 0;
        }

        // --- CRUISE CONTROL LOGIC ---
        // Calculate the width of the channel from Center to Edge
        decimal bandRadius = (upperBand - lowerBand) / 2.0m;
        
        // Define "Safe Zone" as being in the outer 50% of the band
        // If we are this deep in the money, we can tolerate low velocity (drift)
        decimal cruiseBuffer = bandRadius * 0.5m; 
        
        bool isCruising = false;
        if (_lastSignal == "BULL")
        {
            // Hold if Price is significantly above SMA
            if (price > (currentSma + cruiseBuffer)) isCruising = true;
            // HYBRID: Also hold if we're in a Bull Trend with positive slope
            else if (isBullTrend && exitSlope > 0) isCruising = true;
        }
        else if (_lastSignal == "BEAR")
        {
            // Hold if Price is significantly below SMA
            if (price < (currentSma - cruiseBuffer)) isCruising = true;
            // HYBRID: Also hold if we're in a Bear Trend with negative slope
            else if (isBearTrend && exitSlope < 0) isCruising = true;
        }

        // --- STATE MANAGEMENT ---
        bool isInTrade = _lastSignal == "BULL" || _lastSignal == "BEAR";
        decimal activeSlope = isInTrade ? exitSlope : entrySlope;
        decimal activeGate = isInTrade ? maintenanceVelocity : entryVelocity;

        // --- BRAKE CHECK ---
        bool calculatorsReady = _smaSlopeCalc.IsReady && _exitSlopeCalc.IsReady;
        
        // SAFETY: "Blind Hold" - If we are in a trade but lack sufficient data to evaluate 
        // exit criteria, we MUST hold the position.
        if (isInTrade && !calculatorsReady)
        {
            return _lastSignal;
        }
        
        // --- HYBRID UNLOCK: Trend Rescue ---
        // If velocity check fails but we're in a strong trend direction, allow entry
        bool slopeFailed = System.Math.Abs(activeSlope) < activeGate;
        bool trendRescue = false;
        
        if (slopeFailed && !isInTrade)
        {
            // Trend can rescue a weak velocity signal for new entries
            if (activeSlope > 0 && isBullTrend) trendRescue = true;
            if (activeSlope < 0 && isBearTrend) trendRescue = true;
        }
        
        bool isStalled = calculatorsReady && slopeFailed && !isCruising && !trendRescue;

        string newSignal = "NEUTRAL";
        
        // DRIFT POSITION MAINTENANCE: if we entered via drift mode, maintain signal
        // while price stays on the correct side of SMA (bypass velocity gate)
        if (_isDriftEntry && isInTrade)
        {
            if (_lastSignal == "BULL" && price >= currentSma)
                newSignal = "BULL";
            else if (_lastSignal == "BEAR" && price <= currentSma)
                newSignal = "BEAR";
            else
            {
                _isDriftEntry = false; // price crossed SMA — revert to normal velocity logic
                _logger.LogInformation("[DRIFT] Price crossed SMA — drift hold released, reverting to velocity logic");
            }
        }

        if (newSignal == "NEUTRAL" && !isStalled)
        {
            // --- BULL LOGIC (STAIRS UP - DELAYED) ---
            if (price > upperBand)
            {
                if (activeSlope > 0) 
                {
                    // Trend Rescue: allow trendRescue to enter independently when slope is
                    // above maintenance velocity but below entryVelocity. Only during Base/PH
                    // (after 10:13 ET) to avoid OV whipsaw. Requires 5x confirmation ticks.
                    // The maintenanceVelocity floor filters out weak/noisy false signals.
                    bool trendRescueEntry = trendRescue 
                        && timeOfDay >= new TimeSpan(10, 13, 0)
                        && activeSlope > maintenanceVelocity;
                    
                    // ALREADY Bull? Stay Bull.
                    if (_lastSignal == "BULL")
                    {
                        newSignal = "BULL";
                    }
                    // NEW Entry? Require Persistence OR Trend Rescue
                    else if (activeSlope > entryVelocity || trendRescueEntry)
                    {
                        _sustainedVelocityTicks++;
                        int requiredTicks = (activeSlope > entryVelocity)
                            ? _settings.EntryConfirmationTicks
                            : _settings.EntryConfirmationTicks * 5;
                        if (_sustainedVelocityTicks >= requiredTicks)
                        {
                            newSignal = "BULL";
                            // Track whether this entry was via trendRescue (slope below entry velocity)
                            _currentSignalIsTrendRescue = (activeSlope <= entryVelocity) && trendRescueEntry;
                        }
                        else
                        {
                            // Waiting for confirmation...
                            newSignal = "NEUTRAL"; 
                        }
                    }
                }
                else { _sustainedVelocityTicks = 0; } 
            }
            
            // --- BEAR LOGIC (with optional confirmation via BearEntryConfirmationTicks) ---
            else if (price < lowerBand)
            {
                _sustainedVelocityTicks = 0; 
                
                if (activeSlope < 0) 
                {
                    if (_settings.BullOnlyMode)
                    {
                        newSignal = "NEUTRAL";
                        _sustainedBearTicks = 0;
                    }
                    // ALREADY Bear? Stay Bear (no re-confirmation needed).
                    else if (_lastSignal == "BEAR")
                    {
                        newSignal = "BEAR";
                    }
                    // NEW Bear entry with confirmation requirement?
                    else if (_settings.BearEntryConfirmationTicks > 0)
                    {
                        _sustainedBearTicks++;
                        if (_sustainedBearTicks >= _settings.BearEntryConfirmationTicks)
                        {
                            newSignal = "BEAR";
                        }
                        else
                        {
                            // Waiting for bear confirmation...
                            newSignal = "NEUTRAL";
                        }
                    }
                    else
                    {
                        // Legacy instant BEAR (BearEntryConfirmationTicks = 0)
                        newSignal = "BEAR"; 
                    }
                }
                else { _sustainedBearTicks = 0; }
            }
            else 
            {
                // Inside bands
                _sustainedVelocityTicks = 0;
                _sustainedBearTicks = 0;
                if (isInTrade && isCruising) newSignal = _lastSignal;
            }
        }
        else
        {
            // Stalled
            _sustainedVelocityTicks = 0;
            _sustainedBearTicks = 0;
        }
        
        // --- DRIFT MODE: velocity-independent entry for sustained moves ---
        // Requires BOTH duration (consecutive ticks above/below SMA) AND magnitude (displacement % from SMA)
        // Per-direction one-shot: BULL drift doesn't block BEAR drift and vice versa
        if (_settings.DriftModeEnabled && !isInTrade && newSignal == "NEUTRAL")
        {
            decimal smaDisplacementPct = currentSma > 0 ? (price - currentSma) / currentSma : 0;
            
            // Dynamic threshold: ATR-based raises the bar in high volatility, fixed percent is the floor
            decimal driftThreshold;
            string thresholdSource;
            if (_settings.DriftModeAtrMultiplier > 0 && _mrAtr?.IsReady == true && currentSma > 0)
            {
                decimal atrThreshold = _settings.DriftModeAtrMultiplier * _mrAtr.CurrentValue / currentSma;
                driftThreshold = Math.Max(_settings.DriftModeMinDisplacementPercent, atrThreshold);
                thresholdSource = $"max(Fixed {_settings.DriftModeMinDisplacementPercent:P3}, ATR {atrThreshold:P3})={driftThreshold:P3}";
            }
            else
            {
                driftThreshold = _settings.DriftModeMinDisplacementPercent;
                thresholdSource = $"Fixed({driftThreshold:P3})";
            }
            
            if (_consecutiveTicksAboveSma >= _settings.DriftModeConsecutiveTicks 
                && smaDisplacementPct >= driftThreshold
                && !_bullDriftConsumedThisPhase)
            {
                newSignal = "BULL";
                _currentSignalIsTrendRescue = false;
                _isDriftEntry = true;
                _bullDriftConsumedThisPhase = true;
                _consecutiveTicksAboveSma = 0;
                _logger.LogInformation("[DRIFT] Price above SMA for {Ticks}+ ticks, displacement +{Disp:P3} >= {Threshold} — BULL entry",
                    _settings.DriftModeConsecutiveTicks, smaDisplacementPct, thresholdSource);
            }
            else if (_consecutiveTicksBelowSma >= _settings.DriftModeConsecutiveTicks 
                && smaDisplacementPct <= -driftThreshold
                && !_settings.BullOnlyMode && !_bearDriftConsumedThisPhase)
            {
                newSignal = "BEAR";
                _currentSignalIsTrendRescue = false;
                _isDriftEntry = true;
                _bearDriftConsumedThisPhase = true;
                _consecutiveTicksBelowSma = 0;
                _logger.LogInformation("[DRIFT] Price below SMA for {Ticks}+ ticks, displacement {Disp:P3} <= -{Threshold} — BEAR entry",
                    _settings.DriftModeConsecutiveTicks, smaDisplacementPct, thresholdSource);
            }
        }
        
        // --- DISPLACEMENT RE-ENTRY: regime-validated re-entry after stop-out ---
        // Trigger: price displaced significantly from stop-out (ATR-based or fixed %)
        // Validation (AND logic): market must be trending (low CHOP) AND volatility expanding (BBW > SMA(BBW))
        // Slope filter: blocks entries when price velocity is flat (filters drift from drive)
        // One-shot per phase: prevents cascading re-entries (same pattern as drift mode)
        // Only fires in Trend mode — during MR, CHOP hysteresis must first activate Trend Rescue
        if (_settings.DisplacementReentryEnabled && !isInTrade && newSignal == "NEUTRAL"
            && _lastNeutralTransitionPrice.HasValue && !_displacementConsumedThisPhase
            && _activeStrategy != StrategyMode.MeanReversion)
        {
            decimal stopOutPrice = _lastNeutralTransitionPrice.Value;
            decimal absDisplacement = Math.Abs(price - stopOutPrice);
            decimal displacementPct = stopOutPrice > 0 ? (price - stopOutPrice) / stopOutPrice : 0;
            
            // Displacement threshold: prefer ATR-based, fall back to fixed percentage
            decimal displacementThreshold;
            string thresholdSource;
            if (_settings.DisplacementAtrMultiplier > 0 && _mrAtr?.IsReady == true)
            {
                displacementThreshold = _settings.DisplacementAtrMultiplier * _mrAtr.CurrentValue;
                thresholdSource = $"{_settings.DisplacementAtrMultiplier:F1}×ATR={displacementThreshold:F4}";
            }
            else
            {
                displacementThreshold = stopOutPrice * _settings.DisplacementReentryPercent;
                thresholdSource = $"Fixed({_settings.DisplacementReentryPercent:P2})={displacementThreshold:F4}";
            }
            
            if (absDisplacement > displacementThreshold)
            {
                // Regime validation (AND logic): CHOP < threshold AND BBW > SMA(BBW)
                // Changed from OR per Research AI: prevents shakeout entries in high-volatility chop
                bool chopValid = _settings.DisplacementChopThreshold <= 0; // pass if CHOP gating disabled
                bool bbwValid = _bbwSma == null; // pass if BBW gating disabled
                bool slopeValid = true; // pass if slope filter disabled or not ready
                string regimeReason = "";
                
                if (_settings.DisplacementChopThreshold > 0 && _chopIndex?.IsReady == true)
                {
                    chopValid = _chopIndex.CurrentValue < _settings.DisplacementChopThreshold;
                    regimeReason += $"CHOP={_chopIndex.CurrentValue:F1}(<{_settings.DisplacementChopThreshold:F0}={chopValid})";
                }
                
                if (_bbwSma != null && _bollingerBands?.IsReady == true && _bbwSma.IsFull)
                {
                    decimal currentBbw = _bollingerBands.Bandwidth;
                    decimal avgBbw = _bbwSma.CurrentAverage;
                    bbwValid = currentBbw > avgBbw;
                    if (regimeReason.Length > 0) regimeReason += " | ";
                    regimeReason += $"BBW={currentBbw:F4}(>{avgBbw:F4}={bbwValid})";
                }
                
                // Slope filter: price velocity must confirm re-entry direction (filters lazy drift)
                if (_settings.DisplacementMinSlope > 0 && _displacementSlope?.IsReady == true)
                {
                    decimal normalizedSlope = _displacementSlope.CurrentSlope / price;
                    // Directional: slope must match displacement direction
                    if (displacementPct > 0) // BULL candidate
                        slopeValid = normalizedSlope >= _settings.DisplacementMinSlope;
                    else // BEAR candidate
                        slopeValid = -normalizedSlope >= _settings.DisplacementMinSlope;
                    if (regimeReason.Length > 0) regimeReason += " | ";
                    regimeReason += $"Slope={normalizedSlope:E2}(>={_settings.DisplacementMinSlope:E2}={slopeValid})";
                }
                
                // AND logic: ALL enabled gates must pass (CHOP + BBW + Slope)
                // During warmup, displacement is blocked until all enabled indicators are ready
                bool regimeValidated = chopValid && bbwValid && slopeValid;
                bool indicatorsReady = true;
                if (_settings.DisplacementChopThreshold > 0 && _chopIndex?.IsReady != true)
                    indicatorsReady = false;
                if (_bbwSma != null && !(_bollingerBands?.IsReady == true && _bbwSma.IsFull))
                    indicatorsReady = false;
                
                if (regimeValidated)
                {
                    if (displacementPct > 0)
                    {
                        newSignal = "BULL";
                        _currentSignalIsTrendRescue = false;
                        _isDisplacementReentry = true;
                        _displacementConsumedThisPhase = true;
                        _lastNeutralTransitionPrice = null; // consumed — don't re-trigger
                        _logger.LogInformation("[DISPLACEMENT] BULL re-entry: {StopPrice:F2}→{Price:F2} (disp={DispAbs:F4}>{Threshold}), regime: {Regime}",
                            stopOutPrice, price, absDisplacement, thresholdSource, regimeReason);
                    }
                    else if (!_settings.BullOnlyMode)
                    {
                        newSignal = "BEAR";
                        _currentSignalIsTrendRescue = false;
                        _isDisplacementReentry = true;
                        _displacementConsumedThisPhase = true;
                        _lastNeutralTransitionPrice = null;
                        _logger.LogInformation("[DISPLACEMENT] BEAR re-entry: {StopPrice:F2}→{Price:F2} (disp={DispAbs:F4}>{Threshold}), regime: {Regime}",
                            stopOutPrice, price, absDisplacement, thresholdSource, regimeReason);
                    }
                }
                else if (!indicatorsReady)
                {
                    _logger.LogDebug("[DISPLACEMENT] Blocked: displacement {DispAbs:F4}>{Threshold} but indicators not ready (CHOP/BBW warming up)",
                        absDisplacement, thresholdSource);
                }
                else
                {
                    _logger.LogDebug("[DISPLACEMENT] Rejected: displacement {DispAbs:F4}>{Threshold} but regime invalid: {Regime}",
                        absDisplacement, thresholdSource, regimeReason);
                }
            }
        }
        
        // --- SCRAMBLE: One-shot reset when momentum re-accelerates after consumed displacement ---
        // If the market enters a high-momentum state after a stop-out, the one-shot safety lock becomes
        // a hindrance. Reset _displacementConsumedThisPhase to allow a second re-entry when:
        //   1. Slope velocity doubles (normalizedSlope > 2× DisplacementMinSlope)
        //   2. CHOP confirms extremely efficient trend (< 35)
        //   3. BBW confirms volatility expansion (BBW > SMA(BBW)) — prevents scramble in contracting vol
        // This differentiates a "second wind" from a whipsaw.
        // Only fires in Trend mode — during MR, CHOP hysteresis must first activate Trend Rescue
        if (_settings.DisplacementReentryEnabled && _displacementConsumedThisPhase
            && _activeStrategy != StrategyMode.MeanReversion
            && _settings.DisplacementMinSlope > 0 && _displacementSlope?.IsReady == true
            && _chopIndex?.IsReady == true)
        {
            decimal scrambleSlope = Math.Abs(_displacementSlope.CurrentSlope / price);
            bool bbwExpanding = _bbwSma == null; // pass if BBW gating disabled
            if (_bbwSma != null && _bollingerBands?.IsReady == true && _bbwSma.IsFull)
            {
                bbwExpanding = _bollingerBands.Bandwidth > _bbwSma.CurrentAverage;
            }
            if (scrambleSlope > 2.0m * _settings.DisplacementMinSlope
                && _chopIndex.CurrentValue < 35m
                && bbwExpanding)
            {
                _displacementConsumedThisPhase = false;
                _logger.LogInformation("[SCRAMBLE] One-shot reset: |slope|={Slope:E2} > 2×{Threshold:E2}, CHOP={Chop:F1} < 35, BBW expanding",
                    scrambleSlope, _settings.DisplacementMinSlope, _chopIndex.CurrentValue);
            }
        }
        
        // --- Track displacement reference: record price when transitioning from directional to NEUTRAL ---
        if (newSignal == "NEUTRAL" && (_lastSignal == "BULL" || _lastSignal == "BEAR"))
        {
            _lastNeutralTransitionPrice = price;
            _isDisplacementReentry = false; // Clear flag when position exits
        }
        else if (newSignal == "BULL" || newSignal == "BEAR")
        {
            // Clear displacement tracking when entering a position
            _lastNeutralTransitionPrice = null;
        }
        
        // Persist state for next tick's hysteresis
        // Also save to persistence if signal changed (for restart recovery)
        if (newSignal != _lastSignal)
        {
            _saveLastSignal(newSignal);
        }
        _lastSignal = newSignal;
        
        return newSignal;
    }

    private bool IsMarketOpen(DateTime easternNow)
    {
        // Regular market hours: 9:30 AM - 4:00 PM ET
        // IOC orders and most trading activity only work during these hours
        var marketOpen = new TimeSpan(9, 30, 0);
        var marketClose = new TimeSpan(16, 0, 0);
        var timeOfDay = easternNow.TimeOfDay;
        
        // Skip weekends
        if (easternNow.DayOfWeek == DayOfWeek.Saturday || easternNow.DayOfWeek == DayOfWeek.Sunday)
            return false;
        
        return timeOfDay >= marketOpen && timeOfDay < marketClose;
    }

    private async Task WaitForMarketOpenAsync(CancellationToken ct)
    {
        // Replay / backtest mode: skip the wall-clock gate entirely
        if (_settings.BypassMarketHoursCheck)
            return;

        var checkInterval = TimeSpan.FromMinutes(10);

        while (!ct.IsCancellationRequested)
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _easternZone);
            var isWeekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
            
            // Define today's session boundaries (9:25 AM to 4:00 PM ET)
            var todayPreStart = now.Date.AddHours(9).AddMinutes(25);
            var todayClose = now.Date.AddHours(16);
            
            // We run if it's a weekday AND we are within the operating window
            var shouldRun = !isWeekend && now >= todayPreStart && now <= todayClose;
            
            if (shouldRun)
            {
                _logger.LogInformation("[ANALYST] Market is OPEN (or pre-market ready). Initializing streams.");
                return;
            }
            
            // Calculate next start time
            DateTime nextStart = todayPreStart;
            
            if (isWeekend || now > todayClose)
            {
                // Market closed for today, aim for tomorrow 9:25 AM
                nextStart = todayPreStart.AddDays(1);
                
                // Skip weekends
                while (nextStart.DayOfWeek == DayOfWeek.Saturday || nextStart.DayOfWeek == DayOfWeek.Sunday)
                {
                    nextStart = nextStart.AddDays(1);
                }
            }
            // else if (now < todayPreStart) -> target is todayPreStart
            
            _logger.LogInformation("[ANALYST] Waiting for market session. Current: {Now:MM-dd HH:mm} ET | Target: {Target:MM-dd HH:mm} ET",
                now, nextStart);
                
            // Wait logic
            var timeUntilStart = nextStart - now;
            
            // Wait for interval, or until start time if it's sooner
            var actualDelay = timeUntilStart < checkInterval && timeUntilStart > TimeSpan.Zero 
                ? timeUntilStart 
                : checkInterval;
            
            // Safety clamp
            if (actualDelay <= TimeSpan.Zero) actualDelay = TimeSpan.FromSeconds(5);

            await Task.Delay(actualDelay, ct);
        }
    }

    /// <summary>
    /// Determines the active strategy mode based on the current market phase and optional CHOP override.
    /// Phase defaults: Base uses BaseDefaultStrategy, Power Hour uses PhDefaultStrategy.
    /// CHOP override (PH only): Schmitt trigger hysteresis to prevent mode-switching flickering.
    ///   Enter Trend Rescue when CHOP &lt; ChopLowerThreshold (e.g. 30)
    ///   Stay in Trend Rescue until CHOP &gt; ChopTrendExitThreshold (e.g. 45)
    ///   CHOP &gt; ChopUpperThreshold → MeanReversion
    /// Only applied during Power Hour to prevent destabilizing Base phase trend-following.
    /// </summary>
    private StrategyMode DetermineStrategyMode(DateTime easternNow)
    {
        // Determine phase default strategy
        var currentPhase = _timeRuleApplier?.ActivePhaseName;
        var phaseDefault = currentPhase == "Power Hour" 
            ? _settings.PhDefaultStrategy 
            : _settings.BaseDefaultStrategy;
        
        // CHOP dynamic override — Power Hour only (Trend Rescue with Schmitt trigger)
        if (_settings.ChopOverrideEnabled && currentPhase == "Power Hour"
            && _chopIndex != null && _chopIndex.IsReady)
        {
            var chopValue = _chopIndex.CurrentValue;
            
            // Schmitt trigger hysteresis for Trend Rescue
            if (_trendRescueActive)
            {
                // Already in Trend Rescue — stay until CHOP rises above exit threshold
                if (chopValue > _settings.ChopTrendExitThreshold)
                {
                    _trendRescueActive = false;
                    _logger.LogInformation("[CHOP HYSTERESIS] Trend Rescue OFF: CHOP={Chop:F1} > exit threshold {Exit:F0}",
                        chopValue, _settings.ChopTrendExitThreshold);
                    // Fall through to check other thresholds below
                }
                else
                {
                    return StrategyMode.Trend; // Stay in Trend Rescue
                }
            }
            
            // Not in Trend Rescue (or just exited) — check entry/MR thresholds
            if (chopValue > _settings.ChopUpperThreshold)
                return StrategyMode.MeanReversion; // Very choppy → MR
            if (chopValue < _settings.ChopLowerThreshold)
            {
                _trendRescueActive = true;
                _logger.LogInformation("[CHOP HYSTERESIS] Trend Rescue ON: CHOP={Chop:F1} < entry threshold {Entry:F0}",
                    chopValue, _settings.ChopLowerThreshold);
                return StrategyMode.Trend; // Strong trend → Trend Rescue
            }
            // Between thresholds → use phase default (MR)
        }
        
        return phaseDefault;
    }
    
    /// <summary>
    /// Generates a mean-reversion signal based on Bollinger Bands %B position.
    /// 
    /// Logic:
    ///   %B &lt; 0.2  → MR_LONG  (price near/below lower band → buy for reversion to mean)
    ///   %B &gt; 0.8  → MR_SHORT (price near/above upper band → sell for reversion to mean)
    ///   Otherwise  → MR_FLAT  (price near midline → no MR trade / exit existing)
    ///   
    /// Hysteresis: once in MR_LONG, stay until %B &gt; 0.5 (midline). Same for MR_SHORT.
    /// </summary>
    private string DetermineMeanReversionSignal(decimal price, DateTime easternNow)
    {
        var timeOfDay = easternNow.TimeOfDay;
        var marketCloseCutoff = new TimeSpan(15, 58, 0);
        if (timeOfDay >= marketCloseCutoff) return "MARKET_CLOSE";
        
        // Need BB to be ready for MR signals
        if (_bollingerBands == null || !_bollingerBands.IsReady)
        {
            return "MR_FLAT"; // Not ready → stay flat
        }
        
        var percentB = _bollingerBands.PercentB;
        
        string newSignal;
        
        // Hysteresis: maintain position until midline crossover
        if (_lastMrSignal == "MR_LONG")
        {
            // Stay LONG until %B crosses above exit threshold (mean reversion achieved)
            newSignal = percentB > _settings.MrExitPctB ? "MR_FLAT" : "MR_LONG";
        }
        else if (_lastMrSignal == "MR_SHORT")
        {
            // Stay SHORT until %B crosses below exit threshold (mean reversion achieved)
            newSignal = percentB < _settings.MrExitPctB ? "MR_FLAT" : "MR_SHORT";
        }
        else
        {
            // Not in a MR trade — look for entry signals
            // RSI confirmation: prevents catching falling knives (MR_LONG during crash)
            // or shorting breakouts (MR_SHORT during Power Hour surge)
            var rsiValue = _mrRsi?.IsReady == true ? _mrRsi.CurrentValue : 50m;
            var rsiConfirmsLong = !_settings.MrRequireRsi || rsiValue < _settings.MrRsiOversold;
            var rsiConfirmsShort = !_settings.MrRequireRsi || rsiValue > _settings.MrRsiOverbought;
            
            if (percentB < _settings.MrEntryLowPctB && rsiConfirmsLong)
                newSignal = "MR_LONG";  // Price near lower band + RSI oversold → buy
            else if (percentB > _settings.MrEntryHighPctB && rsiConfirmsShort)
                newSignal = "MR_SHORT"; // Price near upper band + RSI overbought → sell
            else
                newSignal = "MR_FLAT";  // Inside bands or RSI not confirming → no trade
        }
        
        if (newSignal != _lastMrSignal)
        {
            _saveLastSignal(newSignal);
        }
        _lastMrSignal = newSignal;
        
        return newSignal;
    }
    
    /// <summary>
    /// Aggregates raw ticks into candles for the Choppiness Index.
    /// CHOP/ATR need OHLC candle data, not individual ticks.
    /// Each candle spans ChopCandleSeconds (default 60s).
    /// </summary>
    private void FeedChopCandle(decimal price, DateTime timestampUtc)
    {
        if (_chopIndex == null) return;
        
        if (!_chopCandleActive)
        {
            // Start a new candle
            _chopCandleHigh = price;
            _chopCandleLow = price;
            _chopCandleClose = price;
            _chopCandleStart = timestampUtc;
            _chopCandleActive = true;
            return;
        }
        
        // Update running candle
        if (price > _chopCandleHigh) _chopCandleHigh = price;
        if (price < _chopCandleLow) _chopCandleLow = price;
        _chopCandleClose = price;
        
        // Check if candle period has elapsed
        var elapsed = (timestampUtc - _chopCandleStart).TotalSeconds;
        if (elapsed >= _settings.ChopCandleSeconds)
        {
            // Complete candle — feed to CHOP, BB, ATR, RSI, and BBW SMA
            _chopIndex.Add(_chopCandleHigh, _chopCandleLow, _chopCandleClose);
            _bollingerBands?.Add(_chopCandleClose);
            _mrAtr?.Add(_chopCandleHigh, _chopCandleLow, _chopCandleClose);
            _mrRsi?.Add(_chopCandleClose);
            
            // Feed BBW SMA for displacement re-entry volatility expansion detection
            if (_bbwSma != null && _bollingerBands?.IsReady == true)
                _bbwSma.Add(_bollingerBands.Bandwidth);
            
            // Feed displacement slope with candle close for price velocity detection
            _displacementSlope?.Add(_chopCandleClose);
            
            // Start next candle
            _chopCandleHigh = price;
            _chopCandleLow = price;
            _chopCandleClose = price;
            _chopCandleStart = timestampUtc;
        }
    }

    /// <summary>
    /// Reconfigures indicator calculators when a time-based phase transition changes window sizes.
    /// Creates new calculator instances with the updated sizes and seeds them from the old instances' data.
    /// This achieves near-zero warm-up time — the new calculators are immediately ready to trade.
    /// </summary>
    private void ReconfigureIndicators()
    {
        var newSmaLength = _settings.SMALength;
        var newSlopeWindow = _settings.SlopeWindowSize;
        var newTrendLength = System.Math.Max(1, _settings.TrendWindowSeconds / _settings.PollingIntervalSeconds);
        
        _logger.LogInformation(
            "[PHASE] Reconfiguring indicators: SMA {OldSma}→{NewSma}, Slope {OldSlope}→{NewSlope}, Trend {OldTrend}→{NewTrend}, ShortTrend {OldShortTrend}→{NewShortTrend}",
            _benchmarkSma.Capacity, newSmaLength,
            _smaSlopeCalc.WindowSize, newSlopeWindow,
            _trendSma.Capacity, newTrendLength,
            _shortTrendSlope.WindowSize, _settings.ShortTrendSlopeWindow);
        
        // --- Benchmark SMA ---
        if (_benchmarkSma.Capacity != newSmaLength)
        {
            var oldValues = _benchmarkSma.GetValues();
            _benchmarkSma = new IncrementalSma(newSmaLength);
            _benchmarkSma.Seed(oldValues);
        }
        
        // --- Crypto SMA (same window as benchmark) ---
        if (_cryptoSma.Capacity != newSmaLength)
        {
            var oldValues = _cryptoSma.GetValues();
            _cryptoSma = new IncrementalSma(newSmaLength);
            _cryptoSma.Seed(oldValues);
        }
        
        // --- Fast Slope (Entry) ---
        if (_smaSlopeCalc.WindowSize != newSlopeWindow)
        {
            var oldValues = _smaSlopeCalc.GetValues();
            _smaSlopeCalc = new StreamingSlope(newSlopeWindow);
            _smaSlopeCalc.Seed(oldValues);
        }
        
        // --- Slow Slope (Exit) — always 2x the fast slope window ---
        var newExitSlopeWindow = newSlopeWindow * 2;
        if (_exitSlopeCalc.WindowSize != newExitSlopeWindow)
        {
            var oldValues = _exitSlopeCalc.GetValues();
            _exitSlopeCalc = new StreamingSlope(newExitSlopeWindow);
            _exitSlopeCalc.Seed(oldValues);
        }
        
        // --- Trend SMA ---
        if (_trendSma.Capacity != newTrendLength)
        {
            var oldValues = _trendSma.GetValues();
            _trendSma = new IncrementalSma(newTrendLength);
            _trendSma.Seed(oldValues);
        }
        
        // --- Short-Term Trend Slope ---
        if (_shortTrendSlope.WindowSize != _settings.ShortTrendSlopeWindow)
        {
            var oldValues = _shortTrendSlope.GetValues();
            _shortTrendSlope = new StreamingSlope(_settings.ShortTrendSlopeWindow);
            _shortTrendSlope.Seed(oldValues);
        }
        
        _logger.LogInformation(
            "[PHASE] Reconfiguration complete. SMA: {SmaCount}/{SmaCapacity}, Slope ready: {SlopeReady}, Trend: {TrendCount}/{TrendCapacity}",
            _benchmarkSma.Count, _benchmarkSma.Capacity,
            _smaSlopeCalc.IsReady,
            _trendSma.Count, _trendSma.Capacity);
        
        // --- Bollinger Bands ---
        if (_bollingerBands == null || _bollingerBands.Period != _settings.BollingerWindow 
            || _bollingerBands.Multiplier != _settings.BollingerMultiplier)
        {
            _bollingerBands = new StreamingBollingerBands(_settings.BollingerWindow, _settings.BollingerMultiplier);
            _logger.LogInformation("[PHASE] BB reconfigured: Window={Window}, Mult={Mult}", 
                _settings.BollingerWindow, _settings.BollingerMultiplier);
        }
        
        // --- Choppiness Index ---
        if (_chopIndex == null || _chopIndex.Period != _settings.ChopPeriod)
        {
            _chopIndex = new ChoppinessIndex(_settings.ChopPeriod);
            ResetChopCandle();
            _logger.LogInformation("[PHASE] CHOP reconfigured: Period={Period}, CandleSec={Candle}", 
                _settings.ChopPeriod, _settings.ChopCandleSeconds);
        }
        
        // --- ATR (shares CHOP period, reconfigure if CHOP period changed) ---
        if (_mrAtr == null || _mrAtr.Period != _settings.ChopPeriod)
        {
            _mrAtr = new StreamingATR(_settings.ChopPeriod);
            _logger.LogInformation("[PHASE] ATR reconfigured: Period={Period}", _settings.ChopPeriod);
        }
        
        // --- RSI ---
        if (_mrRsi == null || _mrRsi.Period != _settings.MrRsiPeriod)
        {
            _mrRsi = new StreamingRSI(_settings.MrRsiPeriod);
            _logger.LogInformation("[PHASE] RSI reconfigured: Period={Period}", _settings.MrRsiPeriod);
        }
    }

    /// <summary>
    /// Cold reset: clears ALL indicator history. Equivalent to a fresh bot startup.
    /// SMA, slope, and trend start empty — warmup required before signals are meaningful.
    /// Also resets signal state (velocity ticks, last signal, sliding bands) so the
    /// analyst evaluates the new phase with no bias from the previous session.
    /// </summary>
    internal void ColdResetIndicators(string phaseName)
    {
        var newSmaLength = _settings.SMALength;
        var newSlopeWindow = _settings.SlopeWindowSize;
        var newTrendLength = System.Math.Max(1, _settings.TrendWindowSeconds / _settings.PollingIntervalSeconds);
        
        // Create fresh empty calculators
        _benchmarkSma = new IncrementalSma(newSmaLength);
        _cryptoSma = new IncrementalSma(newSmaLength);
        _smaSlopeCalc = new StreamingSlope(newSlopeWindow);
        _exitSlopeCalc = new StreamingSlope(newSlopeWindow * 2);
        _trendSma = new IncrementalSma(newTrendLength);
        _shortTrendSlope = new StreamingSlope(_settings.ShortTrendSlopeWindow);
        
        // Reset signal state — force fresh evaluation
        _sustainedVelocityTicks = 0;
        _sustainedBearTicks = 0;
        _consecutiveTicksAboveSma = 0;
        _consecutiveTicksBelowSma = 0;
        _isDriftEntry = false;
        _isDisplacementReentry = false;
        _displacementConsumedThisPhase = false;
        _bullDriftConsumedThisPhase = false;
        _bearDriftConsumedThisPhase = false;
        _lastNeutralTransitionPrice = null;
        if (_settings.DisplacementSlopeWindow > 0)
            _displacementSlope = new StreamingSlope(_settings.DisplacementSlopeWindow);
        _lastSignal = "NEUTRAL";
        _lastMrSignal = "MR_FLAT";
        _activeStrategy = StrategyMode.Trend;
        _trendRescueActive = false;
        ResetSlidingBands();
        
        // Reset MR indicators
        _bollingerBands = new StreamingBollingerBands(_settings.BollingerWindow, _settings.BollingerMultiplier);
        _chopIndex = new ChoppinessIndex(_settings.ChopPeriod);
        _mrAtr = new StreamingATR(_settings.ChopPeriod);
        _mrRsi = new StreamingRSI(_settings.MrRsiPeriod);
        ResetChopCandle();
        
        _logger.LogInformation(
            "╔══════════════════════════════════════════════════════╗");
        _logger.LogInformation(
            "║  ★ ANALYST COLD RESET for {PhaseName}               ║", phaseName);
        _logger.LogInformation(
            "║    All indicators cleared — warmup required          ║");
        _logger.LogInformation(
            "╚══════════════════════════════════════════════════════╝");
        _logger.LogInformation(
            "[PHASE] Cold reset complete. SMA: 0/{SmaCapacity}, Slope ready: False, Trend: 0/{TrendCapacity}",
            newSmaLength, newTrendLength);
    }

    /// <summary>
    /// Partial reset: retains only the last N seconds of indicator history.
    /// Provides a middle ground between full carry-forward (stale morning data)
    /// and cold start (extended warmup). Signal state is also reset.
    /// </summary>
    internal void PartialResetIndicators(string phaseName, int historySeconds)
    {
        var newSmaLength = _settings.SMALength;
        var newSlopeWindow = _settings.SlopeWindowSize;
        var newTrendLength = System.Math.Max(1, _settings.TrendWindowSeconds / _settings.PollingIntervalSeconds);
        var keepDataPoints = System.Math.Max(1, historySeconds / _settings.PollingIntervalSeconds);
        
        // --- Benchmark SMA: keep last N data points ---
        var oldSmaValues = _benchmarkSma.GetValues();
        _benchmarkSma = new IncrementalSma(newSmaLength);
        SeedFromTail(_benchmarkSma, oldSmaValues, keepDataPoints);
        
        // --- Crypto SMA ---
        var oldCryptoValues = _cryptoSma.GetValues();
        _cryptoSma = new IncrementalSma(newSmaLength);
        SeedFromTail(_cryptoSma, oldCryptoValues, keepDataPoints);
        
        // --- Fast Slope (Entry) ---
        var oldSlopeValues = _smaSlopeCalc.GetValues();
        _smaSlopeCalc = new StreamingSlope(newSlopeWindow);
        SeedFromTail(_smaSlopeCalc, oldSlopeValues, keepDataPoints);
        
        // --- Slow Slope (Exit) ---
        var oldExitValues = _exitSlopeCalc.GetValues();
        _exitSlopeCalc = new StreamingSlope(newSlopeWindow * 2);
        SeedFromTail(_exitSlopeCalc, oldExitValues, keepDataPoints);
        
        // --- Trend SMA ---
        var oldTrendValues = _trendSma.GetValues();
        _trendSma = new IncrementalSma(newTrendLength);
        SeedFromTail(_trendSma, oldTrendValues, keepDataPoints);
        
        // --- Short-Term Trend Slope ---
        var oldShortTrendValues = _shortTrendSlope.GetValues();
        _shortTrendSlope = new StreamingSlope(_settings.ShortTrendSlopeWindow);
        SeedFromTail(_shortTrendSlope, oldShortTrendValues, keepDataPoints);
        
        // Reset signal state — force fresh evaluation
        _sustainedVelocityTicks = 0;
        _sustainedBearTicks = 0;
        _consecutiveTicksAboveSma = 0;
        _consecutiveTicksBelowSma = 0;
        _isDriftEntry = false;
        _isDisplacementReentry = false;
        _displacementConsumedThisPhase = false;
        _bullDriftConsumedThisPhase = false;
        _bearDriftConsumedThisPhase = false;
        _lastNeutralTransitionPrice = null;
        if (_displacementSlope != null)
        {
            var oldDispSlopeValues = _displacementSlope.GetValues();
            _displacementSlope = new StreamingSlope(_settings.DisplacementSlopeWindow > 0 ? _settings.DisplacementSlopeWindow : 10);
            SeedFromTail(_displacementSlope, oldDispSlopeValues, keepDataPoints);
        }
        _lastSignal = "NEUTRAL";
        _lastMrSignal = "MR_FLAT";
        _activeStrategy = StrategyMode.Trend;
        _trendRescueActive = false;
        ResetSlidingBands();
        
        // Reset MR indicators (no partial seed for BB/CHOP — they rebuild from ticks)
        _bollingerBands = new StreamingBollingerBands(_settings.BollingerWindow, _settings.BollingerMultiplier);
        _chopIndex = new ChoppinessIndex(_settings.ChopPeriod);
        _mrAtr = new StreamingATR(_settings.ChopPeriod);
        _mrRsi = new StreamingRSI(_settings.MrRsiPeriod);
        ResetChopCandle();
        
        _logger.LogInformation(
            "╔══════════════════════════════════════════════════════╗");
        _logger.LogInformation(
            "║  ★ ANALYST PARTIAL RESET for {PhaseName}             ║", phaseName);
        _logger.LogInformation(
            "║    Retained last {Seconds}s of history                ║", historySeconds);
        _logger.LogInformation(
            "╚══════════════════════════════════════════════════════╝");
        _logger.LogInformation(
            "[PHASE] Partial reset complete. SMA: {SmaCount}/{SmaCapacity}, Slope ready: {SlopeReady}, Trend: {TrendCount}/{TrendCapacity}",
            _benchmarkSma.Count, _benchmarkSma.Capacity,
            _smaSlopeCalc.IsReady,
            _trendSma.Count, _trendSma.Capacity);
    }

    /// <summary>
    /// Seeds a calculator with only the tail (last N values) from a chronological value list.
    /// </summary>
    private static void SeedFromTail(IncrementalSma sma, IReadOnlyList<decimal> values, int keepCount)
    {
        var skip = System.Math.Max(0, values.Count - keepCount);
        for (int i = skip; i < values.Count; i++)
            sma.Add(values[i]);
    }

    /// <summary>
    /// Seeds a slope calculator with only the tail (last N values) from a chronological value list.
    /// </summary>
    private static void SeedFromTail(StreamingSlope slope, IReadOnlyList<decimal> values, int keepCount)
    {
        var skip = System.Math.Max(0, values.Count - keepCount);
        for (int i = skip; i < values.Count; i++)
            slope.Add(values[i]);
    }

    public async Task SeedSmaAsync(IEnumerable<decimal> historicalPrices)
    {
        foreach (var price in historicalPrices)
        {
            // 1. Update the SMAs (both fast and trend)
            var currentSma = _benchmarkSma.Add(price);
            _trendSma.Add(price);
            
            // 2. Update the Slope Calculators (Feed SMA, not raw price)
            // This ensures the bot is ready to trade immediately after seeding
            _smaSlopeCalc.Add(currentSma);
            _exitSlopeCalc.Add(currentSma);
        }
        
        _logger.LogInformation(
            "[ANALYST] Seeding Complete. SMA Count: {SmaCount}, Trend SMA: {TrendSma:N2}, Slope Ready: {SlopeReady}", 
            _benchmarkSma.Count, 
            _trendSma.CurrentAverage,
            _smaSlopeCalc.IsReady);
            
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Hydrates indicators from historical data on startup (Hot Start).
    /// This allows the bot to trade immediately without waiting for indicators to warm up.
    /// Tries primary source (Alpaca) first, falls back to FMP if SIP-restricted.
    /// </summary>
    private async Task HydrateSmaAsync(CancellationToken ct)
    {
        if (_historicalDataSource == null && _fallbackDataAdapter == null)
        {
            _logger.LogWarning("[HYDRATE] No historical data source configured. Starting cold.");
            return;
        }
        
        // Calculate required history (Trend Window is the largest constraint)
        int requiredSeconds = System.Math.Max(_settings.SMAWindowSeconds, _settings.TrendWindowSeconds);
        int lookbackMinutes = (int)System.Math.Ceiling(requiredSeconds / 60.0 * 1.5); // 1.5x safety buffer
        lookbackMinutes = System.Math.Max(lookbackMinutes, 20); // Minimum 20m
        
        _logger.LogInformation("[HYDRATE] Fetching {Mins} minutes of history for {Symbol}...", 
            lookbackMinutes, _settings.BenchmarkSymbol);

        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-lookbackMinutes);
        
        IReadOnlyList<Ohlcv>? bars = null;
        
        // Try primary source (Alpaca IEX) first with extended hours for pre-market data
        if (_historicalDataSource != null)
        {
            try
            {
                // Request extended hours data to capture pre-market "pump" or "dive"
                // This ensures the trendline reflects morning activity, not yesterday's stale close
                bars = await _historicalDataSource.GetHistoricalBarsAsync(
                    _settings.BenchmarkSymbol, start, end, CandleResolution.OneMinute, 
                    includeExtendedHours: true, // Enable pre-market hydration
                    ct
                );
                _logger.LogDebug("[HYDRATE] Primary source (IEX+ExtHours) returned {Count} bars", bars.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[HYDRATE] Primary source failed: {Message}. Trying fallback...", ex.Message);
            }
        }
        
        // Fallback to FMP if primary failed or returned insufficient data
        if ((bars == null || bars.Count < 2) && _fallbackDataAdapter != null)
        {
            try
            {
                // FMP uses 5-minute resolution (1-min not available at all subscription tiers)
                _logger.LogInformation("[HYDRATE] Using FMP fallback for historical data (5min bars, {Mins}m lookback)...", lookbackMinutes);
                bars = await _fallbackDataAdapter.GetCandlesAsync(
                    _settings.BenchmarkSymbol, CandleResolution.FiveMinute, lookbackMinutes, ct
                );
                _logger.LogDebug("[HYDRATE] FMP fallback returned {Count} 5-min bars", bars.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HYDRATE] FMP fallback also failed. Starting cold.");
                return;
            }
        }
        
        if (bars == null || bars.Count < 2)
        {
            _logger.LogWarning("[HYDRATE] Insufficient data ({Count} bars). Starting cold.", bars?.Count ?? 0);
            return;
        }
        
        // FMP returns newest first, ensure chronological order
        var orderedBars = bars.OrderBy(b => b.Timestamp).ToList();

        _logger.LogInformation("[HYDRATE] Interpolating {Count} bars to {Interval}s ticks...", 
            orderedBars.Count, _settings.PollingIntervalSeconds);

        // Linear Interpolation: Convert 1-minute bars to 1-second ticks
        var interpolatedPrices = new List<decimal>();
        for (int i = 0; i < orderedBars.Count - 1; i++)
        {
            var startPrice = orderedBars[i].Close;
            var endPrice = orderedBars[i + 1].Close;
            var timeDiff = (orderedBars[i + 1].Timestamp - orderedBars[i].Timestamp).TotalSeconds;
            
            if (timeDiff <= 0) continue;
            
            int steps = (int)(timeDiff / _settings.PollingIntervalSeconds);
            if (steps <= 0) steps = 1;
            
            decimal stepSize = (endPrice - startPrice) / steps;
            
            for (int s = 0; s < steps; s++)
                interpolatedPrices.Add(startPrice + (stepSize * s));
        }
        interpolatedPrices.Add(orderedBars.Last().Close);

        await SeedSmaAsync(interpolatedPrices);
        _logger.LogInformation("[HYDRATE] Engine HOT. Trend SMA: {Trend:N2}, Fast SMA: {Fast:N2}", 
            _trendSma.CurrentAverage, _benchmarkSma.CurrentAverage);
    }

    /// <summary>
    /// Reset sliding band tracking (call when position changes externally).
    /// </summary>
    public void ResetSlidingBands()
    {
        _slidingBandHigh = 0m;
        _slidingBandLow = 0m;
    }
    
    /// <summary>
    /// Reset the candle aggregation state for the Choppiness Index.
    /// Called during phase resets and indicator reconfiguration.
    /// </summary>
    private void ResetChopCandle()
    {
        _chopCandleHigh = 0;
        _chopCandleLow = 0;
        _chopCandleClose = 0;
        _chopCandleStart = DateTime.MinValue;
        _chopCandleActive = false;
    }
}
