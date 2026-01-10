class CommandLineOverrides
{
    public string? BullTicker { get; set; }
    public string? BearTicker { get; set; }
    public string? BenchmarkTicker { get; set; }
    public bool BullOnlyMode { get; set; }
    public bool HasOverrides { get; set; }
    public bool UseBtcEarlyTrading { get; set; }
    public int? NeutralWaitSecondsOverride { get; set; }
    public decimal? MinChopAbsoluteOverride { get; set; }
    public bool WatchBtc { get; set; }
    public string? BotIdOverride { get; set; }
    public bool MonitorSlippage { get; set; }
    public decimal? TrailingStopPercentOverride { get; set; }
    public bool UseMarketableLimits { get; set; }
    public decimal? MaxSlippagePercentOverride { get; set; }
    // Low-latency mode flags
    public bool LowLatencyMode { get; set; }
    public bool UseIocOrders { get; set; }
}
