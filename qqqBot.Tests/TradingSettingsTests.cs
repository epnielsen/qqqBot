using qqqBot;

namespace qqqBot.Tests;

public class TradingSettingsTests
{
    [Fact]
    public void NewTradingSettings_HasDefaultValues()
    {
        var settings = new TradingSettings();

        Assert.Equal("main", settings.BotId);
        Assert.Equal(1, settings.PollingIntervalSeconds);
        Assert.Null(settings.BullSymbol);  // No default - must be explicitly configured
        Assert.Null(settings.BearSymbol);  // No default - must be explicitly configured
        Assert.Equal("QQQ", settings.BenchmarkSymbol);
        Assert.Equal("BTC/USD", settings.CryptoBenchmarkSymbol);
        Assert.Equal(60, settings.SMAWindowSeconds);
        Assert.Equal(0.0015m, settings.ChopThresholdPercent);
        Assert.Equal(0.02m, settings.MinChopAbsolute);
        Assert.False(settings.SlidingBand);
        Assert.False(settings.LowLatencyMode);
        Assert.False(settings.UseIocOrders);
    }

    [Fact]
    public void TradingSettings_CanSetAllProperties()
    {
        var settings = new TradingSettings
        {
            BotId = "test-bot",
            PollingIntervalSeconds = 5,
            BullSymbol = "UPRO",
            BearSymbol = "SPXS",
            BenchmarkSymbol = "SPY",
            CryptoBenchmarkSymbol = "ETH/USD",
            SMAWindowSeconds = 120,
            ChopThresholdPercent = 0.002m,
            MinChopAbsolute = 0.05m,
            SlidingBand = true,
            SlidingBandFactor = 0.75m,
            ExitStrategy = new DynamicExitConfig 
            { 
                ScalpWaitSeconds = 30,
                TrendWaitSeconds = 120,
                TrendConfidenceThreshold = 0.0002
            },
            WatchBtc = true,
            MonitorSlippage = true,
            TrailingStopPercent = 0.02m,
            StopLossCooldownSeconds = 30,
            StartingAmount = 25000m,
            UseMarketableLimits = true,
            MaxSlippagePercent = 0.003m,
            MaxChaseDeviationPercent = 0.005m,
            LowLatencyMode = true,
            UseIocOrders = true,
            IocLimitOffsetCents = 2m,
            IocMaxRetries = 10,
            IocRetryStepCents = 2m,
            IocMaxDeviationPercent = 0.01m,
            IocRemainingSharesTolerance = 5,
            KeepAlivePingSeconds = 10,
            WarmUpIterations = 5000,
            StatusLogIntervalSeconds = 10
        };

        Assert.Equal("test-bot", settings.BotId);
        Assert.Equal(5, settings.PollingIntervalSeconds);
        Assert.Equal("UPRO", settings.BullSymbol);
        Assert.Equal("SPXS", settings.BearSymbol);
        Assert.Equal("SPY", settings.BenchmarkSymbol);
        Assert.Equal("ETH/USD", settings.CryptoBenchmarkSymbol);
        Assert.Equal(120, settings.SMAWindowSeconds);
        Assert.Equal(0.002m, settings.ChopThresholdPercent);
        Assert.Equal(0.05m, settings.MinChopAbsolute);
        Assert.True(settings.SlidingBand);
        Assert.Equal(0.75m, settings.SlidingBandFactor);
        Assert.Equal(30, settings.ExitStrategy.ScalpWaitSeconds);
        Assert.Equal(120, settings.ExitStrategy.TrendWaitSeconds);
        Assert.Equal(0.0002, settings.ExitStrategy.TrendConfidenceThreshold);
        Assert.True(settings.WatchBtc);
        Assert.True(settings.MonitorSlippage);
        Assert.Equal(0.02m, settings.TrailingStopPercent);
        Assert.Equal(30, settings.StopLossCooldownSeconds);
        Assert.Equal(25000m, settings.StartingAmount);
        Assert.True(settings.UseMarketableLimits);
        Assert.Equal(0.003m, settings.MaxSlippagePercent);
        Assert.Equal(0.005m, settings.MaxChaseDeviationPercent);
        Assert.True(settings.LowLatencyMode);
        Assert.True(settings.UseIocOrders);
        Assert.Equal(2m, settings.IocLimitOffsetCents);
        Assert.Equal(10, settings.IocMaxRetries);
        Assert.Equal(2m, settings.IocRetryStepCents);
        Assert.Equal(0.01m, settings.IocMaxDeviationPercent);
        Assert.Equal(5, settings.IocRemainingSharesTolerance);
        Assert.Equal(10, settings.KeepAlivePingSeconds);
        Assert.Equal(5000, settings.WarmUpIterations);
        Assert.Equal(10, settings.StatusLogIntervalSeconds);
    }

    [Fact]
    public void SMALength_CalculatedFromWindowAndPolling()
    {
        var settings = new TradingSettings
        {
            SMAWindowSeconds = 120,
            PollingIntervalSeconds = 2
        };

        Assert.Equal(60, settings.SMALength);
    }

    [Fact]
    public void SMALength_MinimumIsOne()
    {
        var settings = new TradingSettings
        {
            SMAWindowSeconds = 0,
            PollingIntervalSeconds = 1
        };

        Assert.Equal(1, settings.SMALength);
    }

    [Fact]
    public void GenerateClientOrderId_ReturnsUniqueIds()
    {
        var settings = new TradingSettings { BotId = "test" };

        var id1 = settings.GenerateClientOrderId();
        var id2 = settings.GenerateClientOrderId();

        Assert.NotEqual(id1, id2);
        Assert.Contains("test", id1);
        Assert.Contains("test", id2);
    }

    [Fact]
    public void GenerateClientOrderId_IncludesBotId()
    {
        var settings = new TradingSettings { BotId = "mybot" };

        var id = settings.GenerateClientOrderId();

        Assert.StartsWith("qqqBot-mybot-", id);
    }

    [Fact]
    public void BullOnlyMode_DefaultsToFalse()
    {
        var settings = new TradingSettings();

        Assert.False(settings.BullOnlyMode);
    }

    [Fact]
    public void UseBtcEarlyTrading_DefaultsToFalse()
    {
        var settings = new TradingSettings();

        Assert.False(settings.UseBtcEarlyTrading);
    }

    [Fact]
    public void EffectiveDailyProfitTarget_DollarValueTakesPrecedence()
    {
        var settings = new TradingSettings
        {
            StartingAmount = 10000m,
            DailyProfitTarget = 200m,
            DailyProfitTargetPercent = 5m // would be $500, but dollar takes precedence
        };

        Assert.Equal(200m, settings.EffectiveDailyProfitTarget);
    }

    [Fact]
    public void EffectiveDailyProfitTarget_FallsBackToPercent()
    {
        var settings = new TradingSettings
        {
            StartingAmount = 10000m,
            DailyProfitTarget = 0m,
            DailyProfitTargetPercent = 2m // 2% of $10,000 = $200
        };

        Assert.Equal(200m, settings.EffectiveDailyProfitTarget);
    }

    [Fact]
    public void EffectiveDailyProfitTarget_BothZero_ReturnsZero()
    {
        var settings = new TradingSettings
        {
            StartingAmount = 10000m,
            DailyProfitTarget = 0m,
            DailyProfitTargetPercent = 0m
        };

        Assert.Equal(0m, settings.EffectiveDailyProfitTarget);
    }
}
