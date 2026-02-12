
// ============================================================================
// CONFIGURATION CLASSES
// ============================================================================
class TradingSettings
{
    public string BotId { get; set; } = "main"; // Unique identifier for this bot instance
    public int PollingIntervalSeconds { get; set; } = 1;
    public string BullSymbol { get; set; } = "TQQQ";
    public string? BearSymbol { get; set; } = "SQQQ";
    public string BenchmarkSymbol { get; set; } = "QQQ";
    public string CryptoBenchmarkSymbol { get; set; } = "BTC/USD";
    public int SMAWindowSeconds { get; set; } = 60; // Total time window for rolling average
    public decimal ChopThresholdPercent { get; set; } = 0.0015m;
    public decimal MinChopAbsolute { get; set; } = 0.02m; // Absolute floor for hysteresis (tick-aware)
    public bool SlidingBand { get; set; } = false; // When true, band slides based on position high/low
    public decimal SlidingBandFactor { get; set; } = 0.5m; // Exit threshold: BULL exits at (high - width*factor), BEAR exits at (low + width*factor)
    
    // DYNAMIC EXIT STRATEGY (Hybrid Scalp/Trend Mode)
    // Switches between fast exit (scalp) and slow exit (trend) based on slope strength
    public DynamicExitConfig ExitStrategy { get; set; } = new();
    
    public decimal StartingAmount { get; set; } = 10000m;
    public bool BullOnlyMode { get; set; } = false;
    public int BearEntryConfirmationTicks { get; set; } = 0; // 0 = instant BEAR (legacy), >0 = ticks of sustained negative velocity required
    public decimal DailyProfitTarget { get; set; } = 0m; // 0 = disabled, >0 = stop trading after hitting this session P/L
    public decimal DailyProfitTargetPercent { get; set; } = 0m; // 0 = disabled, e.g. 2.0 = 2% of StartingAmount
    public bool DailyProfitTargetRealtime { get; set; } = false; // true = check realized+unrealized on every tick
    public bool UseBtcEarlyTrading { get; set; } = false; // Use BTC/USD as early trading weathervane
    public bool WatchBtc { get; set; } = false; // Use BTC as tie-breaker during NEUTRAL
    public bool MonitorSlippage { get; set; } = false; // Track and log slippage per trade
    public decimal TrailingStopPercent { get; set; } = 0.0m; // 0 = disabled, e.g. 0.002 = 0.2%
    public int StopLossCooldownSeconds { get; set; } = 10; // Washout latch duration
    public bool UseMarketableLimits { get; set; } = false; // Use limit orders instead of market orders
    public decimal MaxSlippagePercent { get; set; } = 0.002m; // 0.2% max slippage for limit orders
    public decimal MaxChaseDeviationPercent { get; set; } = 0.003m; // 0.3% max price move before aborting entry chase
    
    // TAKE PROFIT SETTINGS
    public decimal TakeProfitAmount { get; set; } = 0m; // Fixed dollar amount profit target (0 = disabled)
    
    // LOW-LATENCY MODE SETTINGS
    public bool LowLatencyMode { get; set; } = false;     // Enable channel-based reactive pipeline
    public bool UseIocOrders { get; set; } = false;       // Use IOC limit orders ("sniper mode")
    public decimal IocLimitOffsetCents { get; set; } = 1m; // Offset above ask (buy) or below bid (sell)
    public int IocMaxRetries { get; set; } = 5;           // Max retries before fallback to market order
    public decimal IocRetryStepCents { get; set; } = 1m;  // Price step per retry (cents)
    public decimal IocMaxDeviationPercent { get; set; } = 0.005m; // Max price chase before stopping (0.5%)
    public int IocRemainingSharesTolerance { get; set; } = 2; // Max remaining shares to treat as "good enough" liquidation
    public int KeepAlivePingSeconds { get; set; } = 5;    // HTTP connection keep-alive ping interval
    public int WarmUpIterations { get; set; } = 10000;    // JIT warm-up iterations before market open
    
    // Derived: Calculate queue size dynamically from window and interval
    public int SMALength => Math.Max(1, SMAWindowSeconds / PollingIntervalSeconds);
    
    // Generate a client order ID with bot prefix for order tracking
    public string GenerateClientOrderId() => $"qqqBot-{BotId}-{Guid.NewGuid():N}";
}
/// <summary>
/// Configuration for dynamic exit strategy that switches between Scalp and Trend modes
/// based on the strength of the current trend (absolute slope value).
/// </summary>
class DynamicExitConfig
{
    /// <summary>
    /// How long to wait in Neutral if the trend is weak (Chop/Scalp mode).
    /// Recommended: 0 seconds (immediate exit in choppy markets).
    /// Set to -1 to hold through neutral (disabled).
    /// </summary>
    public int ScalpWaitSeconds { get; set; } = 0;

    /// <summary>
    /// How long to wait in Neutral if the trend is strong (Trend mode).
    /// Recommended: 120 seconds (patience for momentum to resume).
    /// Set to -1 to hold through neutral (disabled).
    /// </summary>
    public int TrendWaitSeconds { get; set; } = 120;

    /// <summary>
    /// The absolute slope value required to trigger "Trend Mode".
    /// When |slope| >= this threshold, use TrendWaitSeconds; otherwise use ScalpWaitSeconds.
    /// Recommended: 0.00015 (adjust based on your instrument volatility).
    /// </summary>
    public double TrendConfidenceThreshold { get; set; } = 0.00015;
}