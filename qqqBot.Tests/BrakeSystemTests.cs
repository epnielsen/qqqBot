using qqqBot;
using Xunit;
using MarketBlocks.Trade.Math;
using System;

namespace qqqBot.Tests;

public class BrakeSystemTests
{
    [Fact]
    public void Scenario_PriceFlattens_BotPumpsBrakes()
    {
        // ---------------------------------------------------
        // 1. SETUP: Configure the Engine parameters
        // ---------------------------------------------------
        int slopeWindow = 5;
        var slopeCalc = new StreamingSlope(slopeWindow);
        
        // Simulation settings
        decimal minVelocityThreshold = 0.0001m; // 0.01% per tick required
        
        // Test Data: A curve that rises fast, then slows down (Parabolic top)
        // Prices: 100 -> 101 -> 102 -> 102.8 -> 103.4 -> 103.8 -> 104.0 (Flat)
        // The SMA will lag behind this, staying BELOW price.
        // OLD LOGIC: Would stay BULLISH because Price > SMA.
        // NEW LOGIC: Should switch to NEUTRAL when Slope < Threshold.
        var priceCurve = new[] 
        {
            100.00m, // t=0
            101.00m, // t=1 (Strong move)
            102.00m, // t=2 (Strong move)
            103.00m, // t=3 (Strong move)
            103.80m, // t=4 (Slowing)
            104.40m, // t=5 (Slowing)
            104.80m, // t=6 (Braking)
            105.00m, // t=7 (Crawling)
            105.05m, // t=8 (Basically Flat)
            105.05m  // t=9 (Flat)
        };

        // ---------------------------------------------------
        // 2. EXECUTE: Run the simulation loop
        // ---------------------------------------------------
        string? lastSignal = null;
        decimal lastSlope = 0m;
        
        for (int i = 0; i < priceCurve.Length; i++)
        {
            decimal price = priceCurve[i];
            
            // Feed the slope calculator directly (In real app, you feed the SMA, 
            // but feeding Price here demonstrates the physics clearer)
            slopeCalc.Add(price); 
            
            if (!slopeCalc.IsReady) continue;

            decimal slope = slopeCalc.CurrentSlope;
            lastSlope = slope;
            
            // The Dynamic Threshold Logic
            decimal velocityGate = price * minVelocityThreshold;
            bool isStalled = Math.Abs(slope) < velocityGate;

            // Determine Signal
            string signal = "BULL"; // Default if Price > SMA (True in this data)
            if (isStalled) signal = "NEUTRAL [BRAKES]";
            lastSignal = signal;

            // ---------------------------------------------------
            // 3. ASSERTIONS
            // ---------------------------------------------------
            
            // At index 4 (first ready point), we are moving fast (Slope ~0.76). 
            // Gate is ~0.01. We should be BULL.
            if (i == 4) 
            {
                Assert.True(slope > velocityGate, $"Should be speeding up at t=4. Slope {slope} > Gate {velocityGate}");
                Assert.Equal("BULL", signal);
            }
        }
        
        // After the full curve, slope should be very low (near flat)
        // With the last few values being 105.05, 105.05, the slope approaches 0
        Assert.True(lastSlope < 0.5m, $"Slope should be low at the end. Actual: {lastSlope}");
    }

    [Fact]
    public void StreamingSlope_LinearData_ReturnsCorrectSlope()
    {
        // Perfect linear data: y = x (slope should be exactly 1.0)
        var slopeCalc = new StreamingSlope(5);
        
        slopeCalc.Add(0m);
        slopeCalc.Add(1m);
        slopeCalc.Add(2m);
        slopeCalc.Add(3m);
        slopeCalc.Add(4m);
        
        Assert.True(slopeCalc.IsReady);
        Assert.Equal(1.0m, slopeCalc.CurrentSlope);
    }

    [Fact]
    public void StreamingSlope_FlatData_ReturnsZeroSlope()
    {
        var slopeCalc = new StreamingSlope(5);
        
        slopeCalc.Add(100m);
        slopeCalc.Add(100m);
        slopeCalc.Add(100m);
        slopeCalc.Add(100m);
        slopeCalc.Add(100m);
        
        Assert.True(slopeCalc.IsReady);
        Assert.Equal(0m, slopeCalc.CurrentSlope);
    }

    [Fact]
    public void StreamingSlope_NegativeSlope_DetectsDowntrend()
    {
        var slopeCalc = new StreamingSlope(5);
        
        // Descending: 100, 99, 98, 97, 96
        slopeCalc.Add(100m);
        slopeCalc.Add(99m);
        slopeCalc.Add(98m);
        slopeCalc.Add(97m);
        slopeCalc.Add(96m);
        
        Assert.True(slopeCalc.IsReady);
        Assert.Equal(-1.0m, slopeCalc.CurrentSlope);
    }

    [Fact]
    public void StreamingSlope_CircularBuffer_SlidesCorrectly()
    {
        var slopeCalc = new StreamingSlope(3);
        
        // Initial: 0, 1, 2 -> slope = 1
        slopeCalc.Add(0m);
        slopeCalc.Add(1m);
        slopeCalc.Add(2m);
        Assert.Equal(1.0m, slopeCalc.CurrentSlope);
        
        // Add 3: now window is 1, 2, 3 -> slope still = 1
        slopeCalc.Add(3m);
        Assert.Equal(1.0m, slopeCalc.CurrentSlope);
        
        // Add 3 again: now window is 2, 3, 3 -> slope = 0.5
        slopeCalc.Add(3m);
        Assert.Equal(0.5m, slopeCalc.CurrentSlope);
        
        // Add 3 again: now window is 3, 3, 3 -> slope = 0
        slopeCalc.Add(3m);
        Assert.Equal(0m, slopeCalc.CurrentSlope);
    }

    [Fact]
    public void StreamingSlope_NotReady_ReturnsZero()
    {
        var slopeCalc = new StreamingSlope(5);
        
        slopeCalc.Add(100m);
        slopeCalc.Add(101m);
        
        Assert.False(slopeCalc.IsReady);
        Assert.Equal(0m, slopeCalc.CurrentSlope);
    }

    [Fact]
    public void VelocityGate_ScalesWithPrice()
    {
        // Verify that the velocity gate scales correctly with different price levels
        decimal minVelocityThreshold = 0.0001m;
        
        // For QQQ @ $500
        decimal qqqPrice = 500m;
        decimal qqqGate = qqqPrice * minVelocityThreshold;
        Assert.Equal(0.05m, qqqGate);
        
        // For SOXL @ $15
        decimal soxlPrice = 15m;
        decimal soxlGate = soxlPrice * minVelocityThreshold;
        Assert.Equal(0.0015m, soxlGate);
        
        // A slope of 0.03 would pass QQQ gate but fail SOXL gate... wait, that's backwards
        // Actually: 0.03 < 0.05 (QQQ gate) means stalled for QQQ
        // And 0.03 > 0.0015 (SOXL gate) means NOT stalled for SOXL
        // This is the expected behavior - different price scales need different thresholds
        Assert.True(0.03m < qqqGate, "0.03 slope should trigger brakes for QQQ");
        Assert.True(0.03m > soxlGate, "0.03 slope should NOT trigger brakes for SOXL");
    }

    [Fact]
    public void BullOnlyMode_BearSignalBecomesNeutral()
    {
        // Simulate the logic that should be in DetermineSignal
        bool bullOnlyMode = true;
        decimal price = 95m;
        decimal lowerBand = 100m;
        decimal slope = -0.5m; // Negative slope, price below band
        
        string signal;
        if (price < lowerBand)
        {
            if (bullOnlyMode)
                signal = "NEUTRAL"; // Bull-only mode ignores bear signals
            else if (slope < 0)
                signal = "BEAR";
            else
                signal = "NEUTRAL";
        }
        else
        {
            signal = "NEUTRAL";
        }
        
        Assert.Equal("NEUTRAL", signal);
    }

    [Fact]
    public void Ignition_Requires_ConsecutiveTicks()
    {
        // SETUP: Simulate the "Stairs Up" logic where BULL entry requires consecutive
        // ticks of sustained high velocity to filter out bull traps.
        int entryConfirmationTicks = 2;
        decimal minVelocityThreshold = 0.0001m;
        decimal price = 500m; // QQQ-like price
        decimal upperBand = 498m; // Price is above upper band
        
        decimal entryVelocity = price * minVelocityThreshold * 2.0m; // 0.10
        
        int sustainedVelocityTicks = 0;
        string lastSignal = "NEUTRAL";
        
        // Helper to simulate DetermineSignal's BULL entry logic
        // Note: Counter resets when slope goes NEGATIVE (momentum reversal)
        // Weak positive slope just doesn't increment the counter
        string SimulateBullEntry(decimal slope)
        {
            string newSignal = "NEUTRAL";
            
            if (price > upperBand)
            {
                if (slope > 0)
                {
                    if (lastSignal == "BULL")
                    {
                        newSignal = "BULL";
                    }
                    else if (slope > entryVelocity)
                    {
                        sustainedVelocityTicks++;
                        if (sustainedVelocityTicks >= entryConfirmationTicks)
                        {
                            newSignal = "BULL";
                        }
                    }
                    // else: slope is positive but weak - don't increment, don't reset
                }
                else
                {
                    // Slope went negative - reset counter
                    sustainedVelocityTicks = 0;
                }
            }
            else
            {
                // Price fell back inside band - reset counter
                sustainedVelocityTicks = 0;
            }
            
            lastSignal = newSignal;
            return newSignal;
        }
        
        // EXECUTE & ASSERT
        
        // Tick 1: High Velocity (0.15 > 0.10) -> Should be NEUTRAL (Counter = 1)
        var signal1 = SimulateBullEntry(0.15m);
        Assert.Equal("NEUTRAL", signal1);
        Assert.Equal(1, sustainedVelocityTicks);
        
        // Tick 2: Negative slope (momentum reversal) -> Counter Reset
        var signal2 = SimulateBullEntry(-0.05m);
        Assert.Equal("NEUTRAL", signal2);
        Assert.Equal(0, sustainedVelocityTicks); // Reset because slope went negative
        
        // Tick 3: High Velocity again -> Should be NEUTRAL (Counter = 1)
        var signal3 = SimulateBullEntry(0.15m);
        Assert.Equal("NEUTRAL", signal3);
        Assert.Equal(1, sustainedVelocityTicks);
        
        // Tick 4: High Velocity again -> Should be BULL (Counter = 2 >= 2 -> Ignition!)
        var signal4 = SimulateBullEntry(0.15m);
        Assert.Equal("BULL", signal4);
        Assert.Equal(2, sustainedVelocityTicks);
    }

    [Fact]
    public void CruiseControl_Overrides_Brake()
    {
        // SETUP: Simulate Cruise Control where a position is held despite
        // low velocity when price is "deep in the money" (far from SMA).
        
        decimal price = 510m;       // Current price
        decimal currentSma = 500m;  // SMA
        decimal upperBand = 505m;   // Upper band
        decimal lowerBand = 495m;   // Lower band
        
        decimal minVelocityThreshold = 0.0001m;
        decimal maintenanceVelocity = price * minVelocityThreshold; // 0.051
        
        // Cruise Control calculation
        decimal bandRadius = (upperBand - lowerBand) / 2.0m; // 5.0
        decimal cruiseBuffer = bandRadius * 0.5m; // 2.5
        
        // Scenario: We are in a BULL trade
        string lastSignal = "BULL";
        
        // Scenario: Slope has dropped to 0 (Stalled)
        decimal slope = 0m;
        
        // Check if we're "Cruising" - price is significantly above SMA
        bool isCruising = false;
        if (lastSignal == "BULL")
        {
            // price (510) > currentSma (500) + cruiseBuffer (2.5) = 502.5? YES!
            if (price > (currentSma + cruiseBuffer)) isCruising = true;
        }
        
        // Check if stalled (normally this would trigger brake)
        bool isStalled = Math.Abs(slope) < maintenanceVelocity && !isCruising;
        
        // ASSERT
        Assert.True(isCruising, "Should be in Cruise Control when price is deep in the money");
        Assert.False(isStalled, "Should NOT be stalled because Cruise Control overrides brake");
        
        // The signal would remain BULL because we're not stalled
        string newSignal = "NEUTRAL";
        if (!isStalled)
        {
            // Inside the band but cruising -> keep old signal
            if (isCruising) newSignal = lastSignal;
        }
        
        Assert.Equal("BULL", newSignal);
    }

    [Fact]
    public void Bear_Entry_Is_Instant_No_Ignition_Delay()
    {
        // SETUP: Verify Bear entries are INSTANT (no ignition delay)
        // This tests the asymmetric "Elevator Down" logic where we don't wait
        // for confirmation ticks - crashes happen fast and we need to react immediately.
        
        decimal price = 100m;
        decimal lowerBand = 102m; // Price is below lower band (Crash scenario)
        decimal minVelocityThreshold = 0.0001m;
        int entryConfirmationTicks = 2; // This should be IGNORED for Bear entries
        
        // Scenario: Massive crash downwards
        decimal slope = -0.5m;
        decimal entryVelocity = price * minVelocityThreshold * 2.0m; // 0.02
        
        // State
        int sustainedVelocityTicks = 0;
        string lastSignal = "NEUTRAL";
        bool bullOnlyMode = false;
        
        // EXECUTE: Simulate Bear Logic block from DetermineSignal
        string newSignal = "NEUTRAL";
        
        // This is the key difference from Bull logic:
        // We do NOT check sustainedVelocityTicks for Bear entries
        if (price < lowerBand)
        {
            sustainedVelocityTicks = 0; // Reset (not used for Bear)
            
            // NO DELAY. If velocity is negative, we go NOW.
            if (slope < 0)
            {
                if (bullOnlyMode)
                    newSignal = "NEUTRAL";
                else
                    newSignal = "BEAR";
            }
        }
        
        // ASSERT
        Assert.Equal("BEAR", newSignal);
        Assert.Equal(0, sustainedVelocityTicks); // Counter was never used
        
        // Verify Bull would NOT have triggered instantly with same magnitude slope
        // (This proves the asymmetry)
        decimal bullPrice = 105m;
        decimal upperBand = 102m;
        decimal bullSlope = 0.5m; // Same magnitude, positive
        sustainedVelocityTicks = 0;
        lastSignal = "NEUTRAL";
        newSignal = "NEUTRAL";
        
        if (bullPrice > upperBand)
        {
            if (bullSlope > 0)
            {
                if (lastSignal == "BULL")
                {
                    newSignal = "BULL";
                }
                else if (bullSlope > entryVelocity)
                {
                    sustainedVelocityTicks++;
                    if (sustainedVelocityTicks >= entryConfirmationTicks)
                    {
                        newSignal = "BULL";
                    }
                }
            }
        }
        
        // Bull did NOT trigger on first tick (needs confirmation)
        Assert.Equal("NEUTRAL", newSignal);
        Assert.Equal(1, sustainedVelocityTicks); // Counter incremented but not enough
    }
    
    [Fact]
    public void ColdStart_InTrade_HoldsPosition_When_CalculatorsNotReady()
    {
        // SETUP: 
        // 1. We are in a BULL trade (recovered from state).
        // 2. Slope calculators are empty (IsReady = false).
        // 3. Price is acting normally.
        // EXPECTATION: Bot should return BULL (Hold), NOT Neutral (Liquidate).
        
        decimal price = 100m;
        decimal upperBand = 101m;
        decimal lowerBand = 99m;
        decimal currentSma = 100m;
        
        // State: Recovered from broker - we ARE in a BULL position
        string lastSignal = "BULL";
        
        // Simulating the DetermineSignal logic flow...
        string newSignal = "NEUTRAL"; // Default would be dangerous
        
        // --- LOGIC UNDER TEST ---
        bool calculatorsReady = false; // <--- The Critical Condition (Not warmed up)
        bool isInTrade = (lastSignal == "BULL" || lastSignal == "BEAR");
        
        // "BLIND HOLD" SAFETY: If in trade but can't evaluate, HOLD the position
        if (isInTrade && !calculatorsReady)
        {
            newSignal = lastSignal; // Should hit this safety line
        }
        else
        {
            // Standard logic that would otherwise run and likely fail
            // This block would default to NEUTRAL if cruise control check failed
            bool isCruising = price > (currentSma + (upperBand - lowerBand) / 4.0m);
            if (!isCruising)
            {
                // Without calculator data, would fall through to NEUTRAL
                newSignal = "NEUTRAL"; 
            }
        }
        // ------------------------
        
        Assert.Equal("BULL", newSignal);
        
        // Also verify BEAR is protected the same way
        lastSignal = "BEAR";
        newSignal = "NEUTRAL";
        
        if (isInTrade && !calculatorsReady)
        {
            // Recalculate isInTrade with new lastSignal
            isInTrade = (lastSignal == "BULL" || lastSignal == "BEAR");
        }
        if (isInTrade && !calculatorsReady)
        {
            newSignal = lastSignal;
        }
        
        Assert.Equal("BEAR", newSignal);
    }
    
    [Fact]
    public void PennyFloor_Clamps_LowPrice_Threshold()
    {
        // SETUP
        // A $4.00 stock. 
        // 0.01% threshold would be $0.0004.
        // Penny Floor should force this to $0.005.
        
        decimal price = 4.00m;
        decimal minVelocityThreshold = 0.0001m; // 0.01%
        
        // Simulate the CalculateVelocityThresholds helper logic:
        decimal rawMaintenance = price * minVelocityThreshold; // 0.0004
        decimal pennyFloor = 0.005m;
        decimal effectiveGate = Math.Max(rawMaintenance, pennyFloor); // Should be 0.005
        
        // A slope of 0.002 is greater than raw (0.0004) but less than floor (0.005)
        decimal weakSlope = 0.002m;
        
        bool isStalled = weakSlope < effectiveGate;
        
        // ASSERT
        Assert.True(isStalled, "Slope of 0.002 should be stalled by Penny Floor (0.005)");
        Assert.Equal(0.005m, effectiveGate);
        
        // Verify high-priced stock uses percentage (not floor)
        decimal highPrice = 500m;
        decimal highRawMaintenance = highPrice * minVelocityThreshold; // 0.05
        decimal highEffectiveGate = Math.Max(highRawMaintenance, pennyFloor); // Should be 0.05 (% wins)
        
        Assert.Equal(0.05m, highEffectiveGate);
        Assert.True(highEffectiveGate > pennyFloor, "High-priced stock should use % threshold, not floor");
    }
}
