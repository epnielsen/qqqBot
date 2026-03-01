using qqqBot;

namespace qqqBot.Tests;

/// <summary>
/// Tests for CycleTracker ("Rhythm Detector").
/// Verifies zero-crossing detection, half-cycle measurement,
/// noise filtering, stability metrics, and rolling window behaviour.
/// </summary>
public class CycleTrackerTests
{
    // =========================================================================
    // Cold Start — not enough data
    // =========================================================================

    [Fact]
    public void GetMetrics_ReturnsZero_WhenNoDataFed()
    {
        var tracker = new CycleTracker();

        var (avg, stability) = tracker.GetMetrics();

        Assert.Equal(0, avg);
        Assert.Equal(0, stability);
    }

    [Fact]
    public void GetMetrics_ReturnsZero_WhenFewerThanThreeCrossings()
    {
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        // Only 2 crossings → only 2 half-cycle measurements
        tracker.AddSlope(0.01m, t);                          // Init: positive
        tracker.AddSlope(-0.01m, t.AddSeconds(60));          // Flip #1
        tracker.AddSlope(0.01m, t.AddSeconds(120));          // Flip #2

        var (avg, stability) = tracker.GetMetrics();

        Assert.Equal(0, avg);
        Assert.Equal(0, stability);
    }

    // =========================================================================
    // Happy Path — regular oscillation
    // =========================================================================

    [Fact]
    public void GetMetrics_DetectsRegularCycle_When120sOscillation()
    {
        // Simulate a market oscillating with perfectly regular 120s half-cycles
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        // +120s → -120s → +120s → ... (4 flips = 4 half-cycle measurements)
        tracker.AddSlope(0.01m, t);                          // Init
        tracker.AddSlope(-0.01m, t.AddSeconds(120));         // Flip 1 → 120s
        tracker.AddSlope(0.01m, t.AddSeconds(240));          // Flip 2 → 120s
        tracker.AddSlope(-0.01m, t.AddSeconds(360));         // Flip 3 → 120s
        tracker.AddSlope(0.01m, t.AddSeconds(480));          // Flip 4 → 120s

        var (avg, stability) = tracker.GetMetrics();

        Assert.Equal(120, avg, precision: 1);
        Assert.Equal(0, stability, precision: 1);  // Perfect rhythm → zero std dev
    }

    [Fact]
    public void GetMetrics_DetectsIrregularCycle_WithHighStability()
    {
        // Simulate a chaotic market with varying half-cycle durations
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        // Durations: 30s, 180s, 60s, 120s — all over the place
        tracker.AddSlope(0.01m, t);
        tracker.AddSlope(-0.01m, t.AddSeconds(30));           // 30s
        tracker.AddSlope(0.01m, t.AddSeconds(210));           // 180s
        tracker.AddSlope(-0.01m, t.AddSeconds(270));          // 60s
        tracker.AddSlope(0.01m, t.AddSeconds(390));           // 120s

        var (avg, stability) = tracker.GetMetrics();

        // Average of {30, 180, 60, 120} = 97.5
        Assert.Equal(97.5, avg, precision: 1);
        // Std dev should be high (chaotic) — not zero
        Assert.True(stability > 50, $"Expected high stability value for chaotic cycle, got {stability:F1}");
    }

    // =========================================================================
    // Noise Filtering — micro-flips are ignored
    // =========================================================================

    [Fact]
    public void AddSlope_IgnoresMicroFlips_ShorterThanMinDuration()
    {
        // A slope that flips after only 5 seconds is noise, not a real cycle
        var tracker = new CycleTracker(historySize: 10, minDurationSeconds: 10);
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        tracker.AddSlope(0.01m, t);
        tracker.AddSlope(-0.01m, t.AddSeconds(5));   // 5s < 10s threshold → filtered
        tracker.AddSlope(0.01m, t.AddSeconds(10));    // 5s < 10s threshold → filtered
        tracker.AddSlope(-0.01m, t.AddSeconds(15));   // 5s < 10s threshold → filtered

        var (avg, _) = tracker.GetMetrics();

        Assert.Equal(0, avg); // All were too short; nothing recorded
    }

    [Fact]
    public void AddSlope_AcceptsFlips_LongerThanMinDuration()
    {
        var tracker = new CycleTracker(historySize: 10, minDurationSeconds: 10);
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        // Feed a mix: some noise, some real
        tracker.AddSlope(0.01m, t);
        tracker.AddSlope(-0.01m, t.AddSeconds(5));    // 5s → noise (filtered)
        // After filtering, _lastCrossingTime resets to t+5, _lastSign = -1
        tracker.AddSlope(0.01m, t.AddSeconds(60));    // 55s from last crossing → kept
        tracker.AddSlope(-0.01m, t.AddSeconds(120));  // 60s → kept
        tracker.AddSlope(0.01m, t.AddSeconds(180));   // 60s → kept

        var (avg, _) = tracker.GetMetrics();

        // After the micro-flip noise reset, we have real durations
        Assert.True(avg > 0, "Expected non-zero average after real crossings");
    }

    // =========================================================================
    // Zero slope is ignored
    // =========================================================================

    [Fact]
    public void AddSlope_IgnoresZeroSlope()
    {
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        tracker.AddSlope(0m, t);                       // Zero → skipped
        tracker.AddSlope(0m, t.AddSeconds(30));        // Zero → skipped
        tracker.AddSlope(0.01m, t.AddSeconds(60));     // First real init

        var (avg, _) = tracker.GetMetrics();

        Assert.Equal(0, avg); // Only one data point, not enough for metrics
    }

    // =========================================================================
    // Rolling Window — history cap
    // =========================================================================

    [Fact]
    public void AddSlope_RespectHistorySize_OldestEvicted()
    {
        // History size = 3, so only the last 3 half-cycles should influence metrics
        var tracker = new CycleTracker(historySize: 3, minDurationSeconds: 5);
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        // Feed 5 crossings → 5 half-cycle durations → oldest 2 should be evicted
        // Durations: 30s, 30s, 60s, 60s, 60s
        tracker.AddSlope(0.01m, t);
        tracker.AddSlope(-0.01m, t.AddSeconds(30));    // 30s
        tracker.AddSlope(0.01m, t.AddSeconds(60));     // 30s
        tracker.AddSlope(-0.01m, t.AddSeconds(120));   // 60s
        tracker.AddSlope(0.01m, t.AddSeconds(180));    // 60s
        tracker.AddSlope(-0.01m, t.AddSeconds(240));   // 60s

        var (avg, stability) = tracker.GetMetrics();

        // Only the last 3 ({60, 60, 60}) should be kept → avg=60, stdDev=0
        Assert.Equal(60, avg, precision: 1);
        Assert.Equal(0, stability, precision: 1);
    }

    // =========================================================================
    // Sustained direction — no flip = no new measurement
    // =========================================================================

    [Fact]
    public void AddSlope_NoNewMeasurement_WhenSlopeSustainsSameDirection()
    {
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 14, 0, 0, DateTimeKind.Utc);

        // Slope stays positive for a long time — no crossing
        tracker.AddSlope(0.01m, t);
        tracker.AddSlope(0.02m, t.AddSeconds(30));
        tracker.AddSlope(0.03m, t.AddSeconds(60));
        tracker.AddSlope(0.04m, t.AddSeconds(90));
        tracker.AddSlope(0.05m, t.AddSeconds(120));

        var (avg, _) = tracker.GetMetrics();

        Assert.Equal(0, avg); // No crossings → no data
    }

    // =========================================================================
    // Afternoon Lull Simulation
    // =========================================================================

    [Fact]
    public void Scenario_AfternoonLull_DetectsRegular2MinuteOscillation()
    {
        // Simulate the afternoon algorithmic oscillation:
        // Slope alternates between +0.005 and -0.005 every ~2 minutes
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 18, 30, 0, DateTimeKind.Utc); // 1:30 PM ET

        // 10 flips at exactly 120s intervals
        decimal slope = 0.005m;
        for (int i = 0; i <= 10; i++)
        {
            tracker.AddSlope(slope, t.AddSeconds(i * 120));
            slope = -slope; // Flip every 120s
        }

        var (avg, stability) = tracker.GetMetrics();

        Assert.Equal(120, avg, precision: 1);
        Assert.Equal(0, stability, precision: 1); // Perfect sine wave
    }

    [Fact]
    public void Scenario_ResonanceAdjustment_CycleShouldCapWaitTime()
    {
        // Verify the LOGIC that the Trader would use:
        // If TrendWaitSeconds = 180 and CyclePeriod = 120, the cap should be 96 (120 * 0.8)
        var tracker = new CycleTracker();
        var t = new DateTime(2026, 2, 6, 18, 30, 0, DateTimeKind.Utc);

        // Build a 120s rhythm (need >= 3 measurements)
        tracker.AddSlope(0.01m, t);
        tracker.AddSlope(-0.01m, t.AddSeconds(120));
        tracker.AddSlope(0.01m, t.AddSeconds(240));
        tracker.AddSlope(-0.01m, t.AddSeconds(360));

        var (cycleSeconds, stability) = tracker.GetMetrics();

        // Simulated Trader logic
        int configuredWait = 180;
        int effectiveWait = configuredWait;

        if (cycleSeconds > 30 && stability < 20)
        {
            int cycleCap = (int)(cycleSeconds * 0.8);
            if (cycleCap < effectiveWait)
                effectiveWait = cycleCap;
        }

        Assert.Equal(96, effectiveWait); // 120 * 0.8 = 96 < 180
    }
}
