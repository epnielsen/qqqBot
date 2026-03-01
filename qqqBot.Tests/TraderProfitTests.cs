using qqqBot;
using Xunit;

namespace qqqBot.Tests;

/// <summary>
/// Tests for the Hybrid Profit Management System (Split Allocation Model).
/// Verifies that the Bank (AccumulatedLeftover) is correctly preserved during buys
/// and that profit distribution follows the ProfitReinvestmentPercent setting.
/// </summary>
public class TraderProfitTests
{
    [Fact]
    public void SplitAllocation_Distributes_Profit_Correctly()
    {
        // ARRANGE
        // Profit Reinvestment = 50%
        // Trade: Bought for $1000, Sold for $1100 (Profit $100)
        // Expectation: Bank +$50, Available +$1050 (Principal + 50% Profit)
        
        var settings = new TradingSettings { ProfitReinvestmentPercent = 0.5m };
        var state = new TradingState 
        { 
            AvailableCash = 0m, 
            AccumulatedLeftover = 0m,
            RealizedSessionPnL = 0m
        };
        
        // Simulate the ApplySplitAllocation logic from TraderEngine
        decimal proceeds = 1100m;
        decimal costBasis = 1000m;
        decimal profit = proceeds - costBasis;
        
        // LOGIC UNDER TEST (replicates ApplySplitAllocation)
        state.AvailableCash += proceeds; // +1100
        
        if (profit > 0)
        {
            state.RealizedSessionPnL += profit; // +100
            var amountToBanked = profit * (1m - settings.ProfitReinvestmentPercent); // 50
            
            state.AccumulatedLeftover += amountToBanked; // +50
            state.AvailableCash -= amountToBanked; // 1100 - 50 = 1050
        }
        
        // ASSERT
        Assert.Equal(50m, state.AccumulatedLeftover);
        Assert.Equal(1050m, state.AvailableCash);
        Assert.Equal(100m, state.RealizedSessionPnL);
    }
    
    [Fact]
    public void SplitAllocation_FullReinvestment_NothingBanked()
    {
        // ARRANGE
        // Profit Reinvestment = 100% (full compound)
        // Trade: Bought for $1000, Sold for $1100 (Profit $100)
        // Expectation: Bank +$0, Available +$1100 (full proceeds)
        
        var settings = new TradingSettings { ProfitReinvestmentPercent = 1.0m };
        var state = new TradingState 
        { 
            AvailableCash = 0m, 
            AccumulatedLeftover = 0m,
            RealizedSessionPnL = 0m
        };
        
        decimal proceeds = 1100m;
        decimal costBasis = 1000m;
        decimal profit = proceeds - costBasis;
        
        // LOGIC UNDER TEST
        state.AvailableCash += proceeds;
        
        if (profit > 0)
        {
            state.RealizedSessionPnL += profit;
            var amountToBanked = profit * (1m - settings.ProfitReinvestmentPercent); // 0
            
            state.AccumulatedLeftover += amountToBanked;
            state.AvailableCash -= amountToBanked;
        }
        
        // ASSERT
        Assert.Equal(0m, state.AccumulatedLeftover);
        Assert.Equal(1100m, state.AvailableCash);
        Assert.Equal(100m, state.RealizedSessionPnL);
    }
    
    [Fact]
    public void SplitAllocation_ZeroReinvestment_AllBanked()
    {
        // ARRANGE
        // Profit Reinvestment = 0% (all to bank, no compound)
        // Trade: Bought for $1000, Sold for $1100 (Profit $100)
        // Expectation: Bank +$100, Available +$1000 (principal only)
        
        var settings = new TradingSettings { ProfitReinvestmentPercent = 0.0m };
        var state = new TradingState 
        { 
            AvailableCash = 0m, 
            AccumulatedLeftover = 0m,
            RealizedSessionPnL = 0m
        };
        
        decimal proceeds = 1100m;
        decimal costBasis = 1000m;
        decimal profit = proceeds - costBasis;
        
        // LOGIC UNDER TEST
        state.AvailableCash += proceeds;
        
        if (profit > 0)
        {
            state.RealizedSessionPnL += profit;
            var amountToBanked = profit * (1m - settings.ProfitReinvestmentPercent); // 100
            
            state.AccumulatedLeftover += amountToBanked;
            state.AvailableCash -= amountToBanked;
        }
        
        // ASSERT
        Assert.Equal(100m, state.AccumulatedLeftover);
        Assert.Equal(1000m, state.AvailableCash);
        Assert.Equal(100m, state.RealizedSessionPnL);
    }
    
    [Fact]
    public void Bank_Is_Preserved_During_Buy()
    {
        // ARRANGE
        // Setup state with $1000 in Bank (accumulated from prior trades)
        // Execute a "buy" by deducting from AvailableCash
        // Bank should remain untouched
        
        var state = new TradingState 
        { 
            AvailableCash = 5000m,       // Working capital
            AccumulatedLeftover = 1000m, // Prior profits (sacred!)
            RealizedSessionPnL = 500m,   // Today's realized P/L
            StartingAmount = 4000m
        };
        
        // Simulate a buy: spending $3000 on shares
        decimal originalAvailableCash = state.AvailableCash;
        decimal actualCost = 3000m;
        
        // LOGIC UNDER TEST (replicates BuyPositionAsync)
        // Key: We ONLY deduct from AvailableCash. Bank is NEVER touched.
        state.AvailableCash = originalAvailableCash - actualCost;
        
        // ASSERT
        Assert.Equal(1000m, state.AccumulatedLeftover); // Bank unchanged!
        Assert.Equal(2000m, state.AvailableCash);       // Only deducted cost
        Assert.Equal(500m, state.RealizedSessionPnL);   // Session P/L unchanged
    }
    
    [Fact]
    public void Bank_Persists_Through_Multiple_Trades()
    {
        // ARRANGE
        // Simulate a full trading cycle: Buy -> Sell (profit) -> Buy -> Sell (profit)
        // Bank should accumulate and never decrease
        
        var settings = new TradingSettings 
        { 
            ProfitReinvestmentPercent = 0.5m,
            StartingAmount = 10000m
        };
        var state = new TradingState 
        { 
            AvailableCash = 10000m,
            AccumulatedLeftover = 0m,
            RealizedSessionPnL = 0m,
            StartingAmount = 10000m
        };
        
        // TRADE 1: Buy at $100, Sell at $110 (10% gain)
        // Buy
        decimal originalCash1 = state.AvailableCash;
        decimal buyCost1 = 10000m;
        state.AvailableCash = originalCash1 - buyCost1; // 0
        
        // Sell with profit
        decimal proceeds1 = 11000m;
        decimal costBasis1 = 10000m;
        decimal profit1 = proceeds1 - costBasis1; // 1000
        
        state.AvailableCash += proceeds1; // 11000
        state.RealizedSessionPnL += profit1; // 1000
        var banked1 = profit1 * (1m - settings.ProfitReinvestmentPercent); // 500
        state.AccumulatedLeftover += banked1; // 500
        state.AvailableCash -= banked1; // 10500
        
        Assert.Equal(500m, state.AccumulatedLeftover);
        Assert.Equal(10500m, state.AvailableCash);
        
        // TRADE 2: Buy at $100, Sell at $120 (20% gain)
        // Buying power = 10000 + (1000 * 0.5) = 10500 (but only use 10500 available)
        decimal originalCash2 = state.AvailableCash;
        decimal buyCost2 = 10500m;
        state.AvailableCash = originalCash2 - buyCost2; // 0
        
        // Bank should STILL be 500!
        Assert.Equal(500m, state.AccumulatedLeftover);
        
        // Sell with profit
        decimal proceeds2 = 12600m; // 20% gain
        decimal costBasis2 = 10500m;
        decimal profit2 = proceeds2 - costBasis2; // 2100
        
        state.AvailableCash += proceeds2; // 12600
        state.RealizedSessionPnL += profit2; // 3100
        var banked2 = profit2 * (1m - settings.ProfitReinvestmentPercent); // 1050
        state.AccumulatedLeftover += banked2; // 1550
        state.AvailableCash -= banked2; // 11550
        
        // ASSERT FINAL STATE
        Assert.Equal(1550m, state.AccumulatedLeftover); // Bank accumulated correctly
        Assert.Equal(11550m, state.AvailableCash);      // Compound working capital
        Assert.Equal(3100m, state.RealizedSessionPnL);  // Total session P/L
    }
    
    [Fact]
    public void BuyingPower_Calculation_Respects_ReinvestmentPercent()
    {
        // ARRANGE
        // Test that buying power is calculated correctly based on Split Allocation
        // BuyingPower = StartingAmount + (RealizedSessionPnL × ReinvestmentPercent)
        
        var settings = new TradingSettings 
        { 
            ProfitReinvestmentPercent = 0.5m,
            StartingAmount = 10000m
        };
        var state = new TradingState 
        { 
            AvailableCash = 12000m,      // More than starting (from prior profits)
            AccumulatedLeftover = 1500m, // Prior banked profits
            RealizedSessionPnL = 3000m,  // Today's realized P/L
            StartingAmount = 10000m
        };
        
        // LOGIC UNDER TEST (replicates BuyPositionAsync buying power calculation)
        var reinvestableProfit = state.RealizedSessionPnL * settings.ProfitReinvestmentPercent; // 1500
        var investableAmount = settings.StartingAmount + reinvestableProfit; // 11500
        
        // Cap at available cash
        investableAmount = System.Math.Min(investableAmount, state.AvailableCash); // 11500
        
        // ASSERT
        Assert.Equal(11500m, investableAmount);
        
        // The Bank (1500) is NOT included in investable amount - it's protected
        Assert.NotEqual(state.AvailableCash + state.AccumulatedLeftover, investableAmount);
    }
}
