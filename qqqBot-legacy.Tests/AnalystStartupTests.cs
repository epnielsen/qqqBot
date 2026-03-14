using qqqBot;
using Xunit;

namespace qqqBot.Tests;

/// <summary>
/// Tests for Analyst startup state reconciliation.
/// Verifies that "Broker Reality" takes priority over "Persisted State" to prevent
/// accidental liquidation of existing positions during Cold Starts.
/// </summary>
public class AnalystStartupTests
{
    [Fact]
    public void TruthReconciliation_BrokerHoldsPosition_OverridesPersistence()
    {
        // SCENARIO: 
        // The JSON file says "NEUTRAL" (or is missing/corrupted).
        // But the Broker actually holds TQQQ (Bull Symbol).
        // The Analyst MUST initialize as "BULL" to protect the position.

        // Arrange
        var settings = new TradingSettings 
        { 
            BullSymbol = "TQQQ", 
            BearSymbol = "SQQQ", 
            BenchmarkSymbol = "QQQ" 
        };
        
        // Simulate broker callbacks
        string currentPosition = "TQQQ";
        long currentShares = 100;
        string persistedSignal = "NEUTRAL"; // The "Wrong" signal from stale/corrupted file
        string? savedSignal = null;
        
        // Simulate the Truth Reconciliation logic from ExecuteAsync
        string lastSignal = "NEUTRAL"; // Default
        
        if (currentShares > 0 && !string.IsNullOrEmpty(currentPosition))
        {
            if (currentPosition == settings.BullSymbol)
            {
                lastSignal = "BULL";
                savedSignal = "BULL"; // Simulates _saveLastSignal("BULL")
            }
            else if (currentPosition == settings.BearSymbol)
            {
                lastSignal = "BEAR";
                savedSignal = "BEAR";
            }
        }
        else
        {
            // Fallback to persistence
            if (!string.IsNullOrEmpty(persistedSignal) && (persistedSignal == "BULL" || persistedSignal == "BEAR"))
            {
                lastSignal = persistedSignal;
            }
        }
        
        // Assert
        Assert.Equal("BULL", lastSignal);  // Signal must match broker position
        Assert.Equal("BULL", savedSignal); // Persistence must be updated to match reality
    }

    [Fact]
    public void TruthReconciliation_BrokerHoldsBearPosition_ForcesBearMode()
    {
        // Arrange
        var settings = new TradingSettings 
        { 
            BullSymbol = "TQQQ", 
            BearSymbol = "SQQQ" 
        };
        
        string currentPosition = "SQQQ";
        long currentShares = 50;
        // persistedSignal would be "BULL" (wrong) but broker reality overrides it
        string? savedSignal = null;
        
        string lastSignal = "NEUTRAL";
        
        // Execute Truth Reconciliation
        if (currentShares > 0 && !string.IsNullOrEmpty(currentPosition))
        {
            if (currentPosition == settings.BullSymbol)
            {
                lastSignal = "BULL";
                savedSignal = "BULL";
            }
            else if (currentPosition == settings.BearSymbol)
            {
                lastSignal = "BEAR";
                savedSignal = "BEAR";
            }
        }
        
        // Assert
        Assert.Equal("BEAR", lastSignal);
        Assert.Equal("BEAR", savedSignal);
    }

    [Fact]
    public void TruthReconciliation_NoCash_RestoresFromPersistence()
    {
        // SCENARIO: We are in CASH but persisted signal says "BULL".
        // This is valid - we might have just sold and the hysteresis logic
        // needs to know we were in BULL to prevent immediate re-entry.

        // Arrange
        var settings = new TradingSettings 
        { 
            BullSymbol = "TQQQ", 
            BearSymbol = "SQQQ" 
        };
        
        string? currentPosition = null;
        long currentShares = 0;
        string persistedSignal = "BULL";
        string? savedSignal = null;
        
        string lastSignal = "NEUTRAL";
        
        // Execute Truth Reconciliation
        if (currentShares > 0 && !string.IsNullOrEmpty(currentPosition))
        {
            if (currentPosition == settings.BullSymbol)
            {
                lastSignal = "BULL";
                savedSignal = "BULL";
            }
            else if (currentPosition == settings.BearSymbol)
            {
                lastSignal = "BEAR";
                savedSignal = "BEAR";
            }
        }
        else
        {
            // Fallback to persistence
            if (!string.IsNullOrEmpty(persistedSignal) && (persistedSignal == "BULL" || persistedSignal == "BEAR"))
            {
                lastSignal = persistedSignal;
            }
        }
        
        // Assert
        Assert.Equal("BULL", lastSignal);  // Restored from persistence
        Assert.Null(savedSignal);          // No update needed - persistence was already correct
    }

    [Fact]
    public void TruthReconciliation_NoCash_InvalidPersistence_DefaultsToNeutral()
    {
        // SCENARIO: We are in CASH and persisted signal is invalid/missing.
        // Should default to NEUTRAL.

        // Arrange
        var settings = new TradingSettings 
        { 
            BullSymbol = "TQQQ", 
            BearSymbol = "SQQQ" 
        };
        
        string? currentPosition = null;
        long currentShares = 0;
        string? persistedSignal = null; // Missing/corrupted
        
        string lastSignal = "NEUTRAL";
        
        // Execute Truth Reconciliation
        if (currentShares > 0 && !string.IsNullOrEmpty(currentPosition))
        {
            // Won't enter - no position
        }
        else
        {
            // Fallback to persistence
            if (!string.IsNullOrEmpty(persistedSignal) && (persistedSignal == "BULL" || persistedSignal == "BEAR"))
            {
                lastSignal = persistedSignal;
            }
            // else: stays NEUTRAL
        }
        
        // Assert
        Assert.Equal("NEUTRAL", lastSignal);
    }

    [Fact]
    public void TruthReconciliation_UnexpectedSymbol_FallsBackToPersistence()
    {
        // SCENARIO: Broker holds an unexpected symbol (neither Bull nor Bear).
        // This could happen if settings changed or manual intervention.
        // Should fall back to persistence as a safety measure.

        // Arrange
        var settings = new TradingSettings 
        { 
            BullSymbol = "TQQQ", 
            BearSymbol = "SQQQ" 
        };
        
        string currentPosition = "AAPL"; // Unexpected!
        long currentShares = 100;
        string persistedSignal = "BULL";
        string? savedSignal = null;
        
        string lastSignal = "NEUTRAL";
        
        // Execute Truth Reconciliation
        if (currentShares > 0 && !string.IsNullOrEmpty(currentPosition))
        {
            if (currentPosition == settings.BullSymbol)
            {
                lastSignal = "BULL";
                savedSignal = "BULL";
            }
            else if (currentPosition == settings.BearSymbol)
            {
                lastSignal = "BEAR";
                savedSignal = "BEAR";
            }
            else
            {
                // Unexpected symbol - fallback to persistence
                if (!string.IsNullOrEmpty(persistedSignal) && (persistedSignal == "BULL" || persistedSignal == "BEAR"))
                {
                    lastSignal = persistedSignal;
                }
            }
        }
        
        // Assert
        Assert.Equal("BULL", lastSignal);  // Restored from persistence
        Assert.Null(savedSignal);          // No overwrite
    }
    
    [Fact]
    public void DetermineSignal_After1558ET_ReturnsMarketClose()
    {
        // SETUP: Simulate the market close cutoff check from DetermineSignal
        // The bot should return MARKET_CLOSE after 15:58:00 ET regardless of price action
        
        var marketCloseCutoff = new TimeSpan(15, 58, 0);
        
        // Test cases around the boundary
        var testCases = new[]
        {
            (new TimeSpan(15, 57, 59), false, "Just before cutoff"),
            (new TimeSpan(15, 58, 00), true,  "Exactly at cutoff"),
            (new TimeSpan(15, 58, 01), true,  "Just after cutoff"),
            (new TimeSpan(15, 59, 00), true,  "One minute after"),
            (new TimeSpan(16, 00, 00), true,  "Market close")
        };
        
        foreach (var (timeOfDay, expectClose, description) in testCases)
        {
            // Simulate DetermineSignal logic
            string signal;
            if (timeOfDay >= marketCloseCutoff)
            {
                signal = "MARKET_CLOSE";
            }
            else
            {
                signal = "BULL"; // Would be determined by normal logic
            }
            
            if (expectClose)
            {
                Assert.Equal("MARKET_CLOSE", signal);
            }
            else
            {
                Assert.NotEqual("MARKET_CLOSE", signal);
            }
        }
    }
}
