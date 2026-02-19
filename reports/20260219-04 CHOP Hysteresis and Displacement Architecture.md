# CHOP Hysteresis, PH Trend Params, Displacement Architecture — Session 4 Report

**Date**: February 19, 2026 (Session 4)  
**Predecessor**: [20260219-03 Regime-Validated Displacement Improvements.md](20260219-03%20Regime-Validated%20Displacement%20Improvements.md)  
**Result**: Infrastructure implemented, P/L-neutral (features dormant at safe thresholds). Daily loss limit investigated — net negative.

---

## Research AI Recommendations — Status

Three action items from the Research AI's evaluation of session 3:

1. **CHOP Hysteresis (Schmitt trigger)** — ✅ Implemented. Enter Trend Rescue at CHOP < lower threshold, stay until CHOP > exit threshold. Prevents mode-switching flicker.
2. **PH-Specific Trend Parameters** — ✅ Implemented via TimeRules. `MinVelocityThreshold: 0.000030` (2× Base), `TrendWindowSeconds: 1800` (30 min vs 90 min Base).
3. **Unlock Displacement During MR** — ❌ Rejected after testing. Displacement MR interrupt caused -$82.31 regression. Architecture pivoted (see below).

---

## Implementation Details

### 1. CHOP Hysteresis — Schmitt Trigger (6-File Pipeline)

**New setting**: `ChopTrendExitThreshold` (added to full pipeline: TradingSettings×2, TimeBasedRule, TimeRuleApplier×5 sections, ProgramRefactored×2).

**New field**: `_trendRescueActive` bool in AnalystEngine — Schmitt trigger state.

```csharp
// Schmitt trigger hysteresis in DetermineStrategyMode()
if (chopValue < _settings.ChopLowerThreshold)
    _trendRescueActive = true;   // Activate Trend Rescue
if (chopValue > _settings.ChopTrendExitThreshold)
    _trendRescueActive = false;  // Deactivate only above exit threshold

if (_trendRescueActive)
    return StrategyMode.Trend;   // Stay in Trend until CHOP > exit
```

- Reset in both `ColdReset()` and `PartialReset()` to prevent stale state across phases.
- Eliminates the CHOP oscillation problem from session 3 where CHOP hovering near 38.2 caused rapid MR↔Trend flipping.

### 2. PH-Specific Trend Parameters (Config Only)

Added Power Hour TimeRules overrides:

```json
{
  "Name": "Power Hour",
  "StartTime": "14:00",
  "EndTime": "16:00",
  "Overrides": {
    "MinVelocityThreshold": 0.000030,
    "TrendWindowSeconds": 1800
  }
}
```

When Trend Rescue activates during PH, the Trend strategy now uses PH-appropriate parameters instead of the Base-phase-tuned values that caused poor entries in session 3.

### 3. Displacement Architecture Pivot — MR Guard Kept

**What the Research AI recommended**: Remove the `_activeStrategy != MeanReversion` guard from displacement, add a "displacement interrupt" in ProcessTick that overrides MR signals when displacement fires during PH.

**What we tried**: Removed MR guard, added interrupt logic in ProcessTick so displacement re-entry signals override MR exit signals.

**Result**: -$82.31 regression over 8 days. Displacement entered trades during choppy MR conditions. The trend signals that triggered entry didn't match the mean-reverting market reality — entries were poor quality, and the interrupt kept positions alive against MR exit logic.

**What we did instead**: Kept the MR guard. Displacement unlocks *naturally* via CHOP hysteresis:
1. When CHOP drops below threshold → Trend Rescue activates → `_activeStrategy` becomes `Trend`
2. MR guard (`_activeStrategy != MeanReversion`) now passes
3. Displacement fires with proper trend management (not fighting MR exits)

This is architecturally cleaner — displacement and Trend Rescue are coupled through the same CHOP signal, ensuring displacement only fires when the market is genuinely trending.

**Additional safety**: Added BBW gate to scramble (one-shot displacement reset). Scramble now requires `BBW > SMA(BBW)` in addition to existing slope/CHOP checks.

---

## Why the Displacement System Is Effectively Dormant

**Critical finding across 4 sessions**: The displacement re-entry system has correct infrastructure but zero opportunities to activate in our 8-day dataset (Feb 9–13, 17–19). Here's why:

1. **All displacement opportunities occur during Power Hour.** Stop-outs during Base phase produce price displacements that only become large enough to trigger re-entry during PH — after the phase boundary.

2. **PH runs Mean Reversion by default.** `PhDefaultStrategy = MeanReversion`. Displacement requires `_activeStrategy != MeanReversion` to fire.

3. **CHOP never drops below 25 in our dataset.** PH CHOP ranges between 25–40 across all 8 days. With `ChopLowerThreshold = 25`, Trend Rescue never activates, `_activeStrategy` stays `MeanReversion`, and displacement remains gated.

4. **Lowering the threshold causes regressions.** At `ChopLowerThreshold = 30`, Trend Rescue activates on some days but produces net-negative results (-$77.65 over 8 days). The affected days (especially Feb 12: -$81.20) lose more than what's gained elsewhere.

**The displacement system is insurance for rare strong-trend PH days** — when CHOP drops below 25 for a sustained period, indicating a genuine end-of-day trend run. These days don't appear in our current dataset. The infrastructure is correct and ready, but we cannot tune or validate it without data containing such events.

---

## Daily Loss Limit Investigation

Separately investigated whether `DailyLossLimitPercent` could mitigate Feb 19's -$196.49 loss.

### Sweep Results (8-Day Total)

| Loss Limit | 8-Day P/L | Feb 17 | Feb 19 | vs Baseline |
|---|---|---|---|---|
| **0% (disabled)** | **$627.15** | +$89.22 | -$196.49 | — |
| 0.5% ($50) | $488.95 | -$142.04 | -$103.43 | -$138.20 |
| 0.75% ($75) | $488.95 | -$142.04 | -$103.43 | -$138.20 |
| 1.0% ($100) | $488.95 | -$142.04 | -$103.43 | -$138.20 |
| 1.25% ($125) | $425.32 | -$142.04 | -$167.06 | -$201.83 |
| 1.5% ($150) | $409.00 | -$158.36 | -$167.06 | -$218.15 |
| 2.0% ($200) | $630.70 | +$92.77 | -$196.49 | +$3.55 |

**Why it fails**: Feb 17 is a V-shaped day — trough of -$172.64 at 09:43, then recovers to +$89.22 by close. Any loss limit ≤1.5% halts trading during the dip, preventing the +$231 recovery. The $93 saved on Feb 19 doesn't compensate for the $231 destroyed on Feb 17.

**Decision**: Keep `DailyLossLimitPercent: 0` (disabled). The strategy's core strength is recovery from intraday drawdowns; a daily stop cuts that off.

---

## 8-Day Replay Results

| Configuration | 8-Day P/L | vs Baseline |
|---|---|---|
| **Baseline (features dormant)** | **$627.15** | — |
| All features ON (CHOP=30) | $473.72 | -$153.43 |
| Displacement MR interrupt only | $544.84 | -$82.31 |
| CHOP hysteresis (30) + PH params | $549.50 | -$77.65 |
| **CHOP hysteresis (25) + PH params** | **$627.15** | **$0 (dormant/safe)** |

### Per-Day Breakdown (Baseline)

| Date | P/L | Notes |
|---|---|---|
| Feb 9 | +$5.55 | Quiet, small gain |
| Feb 10 | +$12.92 | Quiet, small gain |
| Feb 11 | +$196.96 | Strong trend day |
| Feb 12 | +$186.92 | Strong trend day |
| Feb 13 | +$174.99 | Strong trend day |
| Feb 17 | +$89.22 | V-shaped: -$172 trough → recovery |
| Feb 18 | +$157.08 | Good trend day |
| Feb 19 | -$196.49 | Losing day (trough -$199 at 12:42) |
| **Total** | **$627.15** | |

---

## Current Production Settings

```json
{
  "ChopOverrideEnabled": true,
  "ChopLowerThreshold": 25,
  "ChopTrendExitThreshold": 45,
  "ChopUpperThreshold": 61.8,
  "DisplacementReentryEnabled": true,
  "DisplacementMinSlope": 0.0002,
  "DailyLossLimit": 0,
  "DailyLossLimitPercent": 0,
  "PH TimeRule Overrides": {
    "MinVelocityThreshold": 0.000030,
    "TrendWindowSeconds": 1800
  }
}
```

All features are infrastructure-complete and safe/dormant at current thresholds.

---

## Files Modified (This Session)

**MarketBlocks** (4 files):
- `MarketBlocks.Bots/Domain/TradingSettings.cs` — Added `ChopTrendExitThreshold`
- `MarketBlocks.Bots/Services/AnalystEngine.cs` — `_trendRescueActive` Schmitt trigger, BBW gate on scramble, reset in ColdReset/PartialReset
- `MarketBlocks.Bots/Services/TimeRuleApplier.cs` — `ChopTrendExitThreshold` in all 5 sections
- `MarketBlocks.Bots/Domain/TimeBasedRule.cs` — `ChopTrendExitThreshold?` in TradingSettingsOverrides

**qqqBot** (3 files):
- `qqqBot/TradingSettings.cs` — Added `ChopTrendExitThreshold`
- `qqqBot/ProgramRefactored.cs` — `ChopTrendExitThreshold` in BuildTradingSettings + ParseOverrides
- `qqqBot/appsettings.json` — `ChopTrendExitThreshold: 45`, `ChopLowerThreshold: 25`, PH overrides

---

## Questions for Research AI

### Q1: Displacement Activation — What Are We Waiting For?

The displacement system is correctly built but dormant because PH CHOP never drops below 25 in our 8-day dataset. We need guidance on:

- **Is CHOP < 25 during PH realistic?** Should we fetch more historical data (January, December) to find genuine PH trend days? Or is CHOP < 25 during a 2-hour window inherently rare?
- **Should the threshold be higher?** At CHOP = 30, Trend Rescue activates but regresses P/L by -$77.65. Is this a parameter tuning problem (the PH Trend parameters need further optimization) or a dataset problem (we need more days to average out the variance)?
- **Alternative approach**: Should we abandon CHOP-gated displacement and instead look for a different mechanism to capture end-of-day trend continuations?

### Q2: Feb 19 Loss Anatomy — Any Structural Fix?

Feb 19 loses -$196.49 regardless of any feature combination tested. The trade sequence:
1. +$8.22 (09:32–09:36)
2. -$111.65 (09:36–09:42) ← single large loss, SessionPnL → -$103.43
3. -$6.85, +$23.46 trim, +$2.72, -$10.88 (choppy)
4. -$72.08 (13:07–13:13) ← second large loss
5. -$10.80, +$17.55, +$6.70, -$42.88 (afternoon chop)

Peak: +$52.06 at 09:36. Trough: -$199.46 at 12:42.

Daily loss limit doesn't help (V-shaped recovery days like Feb 17 are destroyed). Is there a structural change that could reduce the severity of days like this without hurting winners? For example:
- Tighter OV-phase stops (the -$111.65 loss at 09:36–09:42 is during Open Volatility)
- Position sizing reduction after first large loss
- Widening the CHOP/velocity thresholds to reduce false entries on choppy days

### Q3: Dataset Size for Reliable Tuning

We now have 8 days of replay data (Feb 9–13, 17–19). This is a small sample — single-day outliers (Feb 12, Feb 19) swing the total by $200+. 

- **How many days of data** do we need before parameter optimization becomes reliable rather than overfit?
- Should we weight recent days more heavily, or treat all days equally?
- Are there specific market condition types (high-vol, low-vol, trend, range) we should ensure are represented in our test set?

### Q4: Next Priority — What Should We Work On?

Given that displacement is dormant and daily loss limits are counterproductive, what's the highest-impact area for the next session? Candidates:
- **Feb 19 loss reduction** (largest single drag on the portfolio)
- **OV phase re-optimization** (the -$111.65 OV loss on Feb 19 suggests OV parameters may need tightening)
- **Expanding the dataset** (fetch more historical data for broader validation)
- **TrimRatio re-evaluation** (currently 0.75, trimming 75% of position — verify this is optimal on 8 days)
- **Something else entirely?**
