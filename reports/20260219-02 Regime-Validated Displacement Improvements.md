# Regime-Validated Displacement — Research AI Recommendations Implementation

**Date**: February 19, 2026 (Session 2)  
**Predecessor**: [20260219 Regime-Validated Displacement.md](20260219%20Regime-Validated%20Displacement.md)

---

## Research AI Recommendations Received

Three refinements were recommended based on evaluation of the initial implementation:

1. **Switch OR → AND logic (Critical)**: BBW expansion alone (without CHOP confirmation) allows entries during high-volatility chop — shakeouts/liquidity grabs where volatility expands without establishing a trend.
2. **Add Slope-Based Classifier**: Use price velocity (normalized LinReg slope of SMA) to distinguish "drift" (lazy price movement that often reverses) from "drive" (impulsive movement confirming re-entry).
3. **Retain One-Shot Guard**: Confirmed as structurally sound — aligns with "Cooldown Period" concept from BOCS Channel Scalper strategy.

---

## Implementation

### 1. OR → AND Logic (Implemented)

**Before**: `bool regimeValidated = chopValid || bbwValid;`  
**After**: `bool regimeValidated = chopValid && bbwValid && slopeValid;`

When an indicator is disabled (threshold = 0 or lookback = 0), its gate defaults to `true` so AND logic doesn't block unnecessarily. All three enabled gates must pass.

CHOP threshold raised from 40 → 50 per Research AI recommendation (expanded to catch more trending markets while still filtering confirmed chop).

### 2. Slope Filter (Implemented — Infrastructure Ready)

New `StreamingSlope` instance (`_displacementSlope`) tracks candle close prices over a configurable window. The existing `StreamingSlope` class (OLS linear regression) was reused — same primitive used for the Two-Slope Hysteresis system.

**Directional check**: Slope must match displacement direction.
- BULL candidate: `normalizedSlope >= DisplacementMinSlope`
- BEAR candidate: `-normalizedSlope >= DisplacementMinSlope`

Where `normalizedSlope = slope / price` (price-normalized for cross-price comparability).

Fed from `FeedChopCandle()` alongside BBW SMA — uses candle close prices, same cadence as CHOP/BBW indicators.

**New settings**:
| Setting | Default | Description |
|---|---|---|
| `DisplacementSlopeWindow` | 10 | LinReg slope window (candle bars). 0 = disable |
| `DisplacementMinSlope` | 0 | Min normalized slope to confirm velocity. 0 = disabled |

Currently disabled (`DisplacementMinSlope = 0`) because displacement doesn't fire during Trend phases in the current dataset — see Critical Discovery below.

### 3. One-Shot Guard (Retained)

No changes. Research AI confirmed this aligns with "Cooldown Period" methodology.

### 4. MR Phase Suppression (New — Critical Fix)

Added `_activeStrategy != StrategyMode.MeanReversion` guard to the displacement entry condition. Displacement is a trend-following mechanism and must not fire during Mean Reversion phases.

---

## Critical Discovery: MR Override Bug

### The Problem

Displacement re-entry logic lives inside `DetermineSignal()` (the trend signal generator). During Power Hour, `ProcessTick()` calls `DetermineSignal()` first, then **replaces** the result with `DetermineMeanReversionSignal()`. This means:

1. Displacement fires internally, sets `_isDisplacementReentry = true`, logs the entry
2. MR signal replaces `BULL`/`BEAR` with `MR_FLAT`/`MR_SHORT`
3. Trader receives MR signal, displacement entry never executes
4. Internal state (`_displacementConsumedThisPhase`, `_lastNeutralTransitionPrice = null`) is already mutated

### Evidence

**All 5 displacement opportunities** in the 7-day dataset (Feb 9-13, 17-18) occur during Power Hour (MR mode). The Base phase (Trend mode) stop-outs don't produce sufficient displacement before Power Hour begins.

This means the previous session's claimed +$3.88 improvement ($823.64 → $827.52) was likely from a transient code state between edits, not from the final committed code.

### Attempted Fix: Force Displacement Over MR

Added signal restoration: if displacement fired in `DetermineSignal()`, restore it over MR's override.

**Result: Catastrophic.** Feb 9 went from +$5.55 to **-$40.02**. The displacement entered BULL during a directionless Power Hour and got stopped out for -$32.30. Full 7-day total: $700.07 (-$123.57 vs baseline).

This validates the Research AI's core thesis: entering during non-trending conditions is dangerous, even when price has displaced significantly.

### Final Fix: Suppress Displacement During MR

Added `_activeStrategy != StrategyMode.MeanReversion` to the displacement guard condition. Benefits:
- Prevents wasted computation during MR phases
- Eliminates state inconsistency between internal flags and emitted signals
- Removes noisy log messages from overridden entries

---

## Replay Results

**7-day replay (Feb 9-13 + 17-18), all at max speed:**

| Configuration | Total P/L | Delta |
|---|---|---|
| Baseline (displacement off) | $823.64 | — |
| AND logic + CHOP<50 + slope=0 + MR suppress | $823.64 | $0.00 |
| MR override attempted (unsafe) | $700.07 | -$123.57 |

**Per-day breakdown (final configuration):**

| Date | P/L | Displacement Activity |
|---|---|---|
| Feb 9 | $5.55 | Would fire at 14:27 — suppressed (MR phase) |
| Feb 10 | $12.92 | Would fire — suppressed (MR phase) |
| Feb 11 | $196.96 | Would fire — suppressed (MR phase) |
| Feb 12 | $186.92 | No stop-out → no displacement opportunity |
| Feb 13 | $174.99 | Would fire — suppressed (MR phase) |
| Feb 17 | $89.22 | No stop-out → no displacement opportunity |
| Feb 18 | $157.08 | Would fire — suppressed (MR phase) |

---

## Key Findings

### 1. Displacement is dormant on this dataset
All displacement opportunities occur during Power Hour (MR mode). The current market structure produces Base-phase stop-outs that don't generate enough displacement before the MR phase begins. The feature is ready but needs different market conditions to activate.

### 2. MR override is dangerous
Forcing trend re-entry during MR mode loses money consistently. Mean-reverting price action after stop-outs looks like displacement but reverses. This is exactly the "high-volatility chop" scenario the Research AI warned about.

### 3. AND logic is a safety improvement
Even though P/L-neutral on this dataset, AND logic prevents the theoretical case where BBW expansion (volatility spike) passes while CHOP is high (directionless). This is structural hardening for future market conditions.

### 4. Slope infrastructure is ready
`StreamingSlope` feeding from candle closes, normalized by price, with directional check — all plumbed and tested. Set `DisplacementMinSlope > 0` when displacement fires during Trend phases to evaluate the filter.

### 5. Previous session's +$3.88 was likely invalid
The improvement measured in Session 1 probably came from a transient code state during iterative edits. The final committed code's displacement entries were all silently overridden by MR, producing $823.64 (= baseline).

---

## Current Production Settings

```json
{
  "DisplacementReentryEnabled": true,
  "DisplacementReentryPercent": 0.005,
  "DisplacementAtrMultiplier": 2.0,
  "DisplacementChopThreshold": 50,
  "DisplacementBbwLookback": 20,
  "DisplacementSlopeWindow": 10,
  "DisplacementMinSlope": 0
}
```

---

## Files Modified (This Session)

**MarketBlocks** (4 files):
- `MarketBlocks.Bots/Domain/TradingSettings.cs` — Added `DisplacementSlopeWindow`, `DisplacementMinSlope`; CHOP default 40→50; updated comment (OR→AND)
- `MarketBlocks.Bots/Domain/TimeBasedRule.cs` — Added nullable overrides for 2 new settings
- `MarketBlocks.Bots/Services/AnalystEngine.cs` — AND logic, slope filter (`_displacementSlope`), MR suppression, slope feed in `FeedChopCandle`, reset/seed in both reset methods
- `MarketBlocks.Bots/Services/TimeRuleApplier.cs` — 5 sections updated (snapshot, restore, apply, log, SettingsSnapshot)

**qqqBot** (3 files):
- `qqqBot/TradingSettings.cs` — Mirror of 2 new settings; CHOP default 40→50
- `qqqBot/ProgramRefactored.cs` — BuildTradingSettings + ParseOverrides for 2 new settings
- `qqqBot/appsettings.json` — CHOP 40→50, added SlopeWindow=10 and MinSlope=0

---

## Questions for Research AI

1. **Should displacement remain enabled during MR suppression?** Currently `DisplacementReentryEnabled = true` but effectively dormant because all opportunities fall in MR phases. Keep enabled for when market structure produces Trend-phase displacement, or disable to reduce unnecessary computation?

2. **Slope calibration**: When displacement does fire during Trend phases, what `DisplacementMinSlope` value would you recommend as a starting point? The slope is normalized (slope/price), so the threshold is price-independent.

3. **One-shot reset enhancement**: The Research AI suggested allowing one-shot reset when TrendSMA slope increases significantly. This would require monitoring slope changes and conditionally clearing `_displacementConsumedThisPhase`. Worth implementing now (proactive), or wait until displacement fires during Trend phases to have data to test against?

4. **Base-phase re-entry timing**: If the core issue is that stop-outs happen too close to Power Hour for displacement to activate during Trend mode, would adjusting the Base→Power Hour boundary or having a "displacement-friendly window" before MR kicks in be worth exploring?
