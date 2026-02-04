public class CommandLineOverrides
{
    public string? BullTicker { get; set; }
    public string? BearTicker { get; set; }
    public string? BenchmarkTicker { get; set; }
    public bool BullOnlyMode { get; set; }
    public bool HasOverrides { get; set; }
    public bool UseBtcEarlyTrading { get; set; }
    public int? ScalpWaitSecondsOverride { get; set; }  // For chop/scalp mode neutral timeout
    public int? TrendWaitSecondsOverride { get; set; }  // For trend mode neutral timeout
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
    
    public static CommandLineOverrides? Parse(string[] args)
    {
        var overrides = new CommandLineOverrides();
        
        foreach (var arg in args)
        {
            if (arg.StartsWith("-bull=", StringComparison.OrdinalIgnoreCase))
            {
                overrides.BullTicker = arg.Substring("-bull=".Length).Trim().ToUpperInvariant();
            }
            else if (arg.StartsWith("-bear=", StringComparison.OrdinalIgnoreCase))
            {
                overrides.BearTicker = arg.Substring("-bear=".Length).Trim().ToUpperInvariant();
            }
            else if (arg.StartsWith("-benchmark=", StringComparison.OrdinalIgnoreCase))
            {
                overrides.BenchmarkTicker = arg.Substring("-benchmark=".Length).Trim().ToUpperInvariant();
            }
            else if (arg.Equals("-usebtc", StringComparison.OrdinalIgnoreCase))
            {
                overrides.UseBtcEarlyTrading = true;
            }
            else if (arg.StartsWith("-scalpwait=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("-scalpwait=".Length).Trim();
                if (int.TryParse(value, out var seconds) && seconds >= -1)
                {
                    overrides.ScalpWaitSecondsOverride = seconds;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] -scalpwait must be an integer >= -1. Got: {value}");
                    return null;
                }
            }
            else if (arg.StartsWith("-trendwait=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("-trendwait=".Length).Trim();
                if (int.TryParse(value, out var seconds) && seconds >= -1)
                {
                    overrides.TrendWaitSecondsOverride = seconds;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] -trendwait must be an integer >= -1. Got: {value}");
                    return null;
                }
            }
            else if (arg.Equals("-watchbtc", StringComparison.OrdinalIgnoreCase))
            {
                overrides.WatchBtc = true;
            }
            else if (arg.StartsWith("-minchop=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("-minchop=".Length).Trim();
                if (decimal.TryParse(value, out var dollars) && dollars >= 0)
                {
                    overrides.MinChopAbsoluteOverride = dollars;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] -minchop must be a non-negative number. Got: {value}");
                    return null;
                }
            }
            else if (arg.StartsWith("-botid=", StringComparison.OrdinalIgnoreCase))
            {
                overrides.BotIdOverride = arg.Substring("-botid=".Length).Trim();
            }
            else if (arg.Equals("-monitor", StringComparison.OrdinalIgnoreCase))
            {
                overrides.MonitorSlippage = true;
            }
            else if (arg.StartsWith("-trail=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("-trail=".Length).Trim();
                if (decimal.TryParse(value, out var pct) && pct >= 0)
                {
                    // Convert user input (e.g., 0.2 for 0.2%) to decimal (0.002)
                    overrides.TrailingStopPercentOverride = pct / 100m;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] -trail must be a non-negative number (percent). Got: {value}");
                    return null;
                }
            }
            else if (arg.Equals("-limit", StringComparison.OrdinalIgnoreCase))
            {
                overrides.UseMarketableLimits = true;
            }
            else if (arg.StartsWith("-maxslip=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("-maxslip=".Length).Trim();
                if (decimal.TryParse(value, out var pct) && pct >= 0)
                {
                    // Convert user input (e.g., 0.2 for 0.2%) to decimal (0.002)
                    overrides.MaxSlippagePercentOverride = pct / 100m;
                }
                else
                {
                    Console.Error.WriteLine($"[ERROR] -maxslip must be a non-negative number (percent). Got: {value}");
                    return null;
                }
            }
            else if (arg.Equals("-lowlatency", StringComparison.OrdinalIgnoreCase))
            {
                overrides.LowLatencyMode = true;
            }
            else if (arg.Equals("-ioc", StringComparison.OrdinalIgnoreCase))
            {
                overrides.UseIocOrders = true;
            }
            // NOTE: -takeprofit has been replaced by the Hybrid Profit Management System.
            // Use ProfitReinvestmentPercent and Trimming settings in appsettings.json instead.
        }
        
        // Validation: -bear may not be specified without -bull
        if (!string.IsNullOrEmpty(overrides.BearTicker) && string.IsNullOrEmpty(overrides.BullTicker))
        {
            Console.Error.WriteLine("[ERROR] -bear may not be specified without -bull. Please specify -bull=TICKER or remove -bear.");
            return null;
        }
        
        // If only -bull is specified, use bull ticker as both benchmark and bull (neutral/bear -> cash)
        if (!string.IsNullOrEmpty(overrides.BullTicker) && string.IsNullOrEmpty(overrides.BearTicker))
        {
            overrides.BenchmarkTicker ??= overrides.BullTicker;
            overrides.BullOnlyMode = true;
        }
        
        // If only -benchmark is specified, use it as both benchmark and bull (neutral/bear -> cash)
        if (!string.IsNullOrEmpty(overrides.BenchmarkTicker) && string.IsNullOrEmpty(overrides.BullTicker))
        {
            overrides.BullTicker = overrides.BenchmarkTicker;
            overrides.BullOnlyMode = true;
        }
        
        overrides.HasOverrides = !string.IsNullOrEmpty(overrides.BullTicker) || 
                                 !string.IsNullOrEmpty(overrides.BearTicker) || 
                                 !string.IsNullOrEmpty(overrides.BenchmarkTicker);
        
        return overrides;
    }
}
