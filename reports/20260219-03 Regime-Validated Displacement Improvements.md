# Regime-Validated Displacement — Dynamic Regime Switching, Slope Activation, Scramble

**Date**: February 19, 2026 (Session 3)  
**Predecessor**: [20260219-02 Regime-Validated Displacement Improvements.md](20260219-02%20Regime-Validated%20Displacement%20Improvements.md)

---

## Research AI Recommendations Implemented

Three action items from the Research AI's evaluation of session 2:

1. **Dynamic Regime Switching (Trend Rescue)**: Enable `ChopOverrideEnabled` so CHOP < 38.2 during Power Hour switches from MR → Trend, allowing displacement re-entry during end-of-day trend runs.
2. **Slope Activation**: Set `DisplacementMinSlope = 0.0002` to activate the velocity filter (infrastructure was already built in session 2).
3. **Scramble (One-Shot Reset)**: Reset `_displacementConsumedThisPhase` when `|normalizedSlope| > 2× threshold && CHOP < 35` — differentiates a "second wind" from a whipsaw, allowing a second re-entry.

---

## Implementation

### 1. Dynamic Regime Switching — `DetermineStrategyMode()` (Code Change + Config)

The existing `DetermineStrategyMode()` method already had bidirectional CHOP override logic:
- CHOP > 61.8 → force MeanReversion
- CHOP < 38.2 → force Trend

**Problem discovered**: This override was global — it applied to Base phase too. When CHOP > 61.8 during Base phase, it switched profitable trend trades to MR, destroying P/L.

**Fix**: Added `currentPhase == "Power Hour"` guard so CHOP override only applies during PH. Base phase always uses `BaseDefaultStrategy` (Trend).

```csharp
// CHOP dynamic override — Power Hour only (Trend Rescue)
if (_settings.ChopOverrideEnabled && currentPhase == "Power Hour"
    && _chopIndex != null && _chopIndex.IsReady)
{
    var chopValue = _chopIndex.CurrentValue;
    if (chopValue > _settings.ChopUpperThreshold)
        return StrategyMode.MeanReversion; // Choppy → MR
    if (chopValue < _settings.ChopLowerThreshold)
        return StrategyMode.Trend; // Strong trend → Trend Rescue
}
```

Config: `ChopOverrideEnabled = true` → tested → **reverted to `false`** (see results below).

### 2. Slope Activation (Config Only)

Set `DisplacementMinSlope = 0.0002` in `appsettings.json`. No code changes — infrastructure built in session 2.

Currently dormant because displacement is MR-suppressed on all 7 test days.

### 3. Scramble — One-Shot Reset (Code Change)

Added after the displacement block in `DetermineSignal()`:

```csharp
// Scramble: unlock one-shot when momentum re-accelerates
if (_settings.DisplacementReentryEnabled && _displacementConsumedThisPhase
    && _activeStrategy != StrategyMode.MeanReversion
    && _settings.DisplacementMinSlope > 0 && _displacementSlope?.IsReady == true
    && _chopIndex?.IsReady == true)
{
    decimal scrambleSlope = Math.Abs(_displacementSlope.CurrentSlope / price);
    if (scrambleSlope > 2.0m * _settings.DisplacementMinSlope
        && _chopIndex.CurrentValue < 35m)
    {
        _displacementConsumedThisPhase = false;
        // [SCRAMBLE] log
    }
}
```

**Guard conditions**: MR-suppressed (won't fire during PH/MR), requires MinSlope > 0 (won't activate if slope filter disabled), requires CHOP < 35 (extremely efficient trend only).

Currently dormant — same reason as slope: no displacement fires during Trend phases in dataset.

---

## Replay Results

### Test 1: Global CHOP Override (`ChopOverrideEnabled = true`, all phases)

| Date | Baseline | Global Override | Delta |
|---|---|---|---|
| Feb 9 | +$5.55 | -$51.56 | -$57.11 |
| Feb 10 | +$12.92 | -$49.74 | -$62.66 |
| Feb 11 | +$196.96 | +$196.96 | $0.00 |
| Feb 12 | +$186.92 | +$105.72 | -$81.20 |
| Feb 13 | +$174.99 | +$174.28 | -$0.71 |
| Feb 17 | +$89.22 | +$13.62 | -$75.60 |
| Feb 18 | +$157.08 | -$41.74 | -$198.82 |
| **Total** | **$823.64** | **$347.54** | **-$476.10** |

**Root cause**: CHOP > 61.8 during Base phase switched profitable trend trades to MR. Base-phase trend logic should never be overridden.

### Test 2: PH-Only CHOP Override (code fixed to restrict to Power Hour)

| Date | Baseline | PH-Only Override | Delta |
|---|---|---|---|
| Feb 9 | +$5.55 | -$51.56 | -$57.11 |
| Feb 10 | +$12.92 | +$12.92 | $0.00 |
| Feb 11 | +$196.96 | +$196.96 | $0.00 |
| Feb 12 | +$186.92 | +$105.72 | -$81.20 |
| Feb 13 | +$174.99 | +$174.28 | -$0.71 |
| Feb 17 | +$89.22 | +$77.10 | -$12.12 |
| Feb 18 | +$157.08 | -$41.74 | -$198.82 |
| **Total** | **$823.64** | **$473.68** | **-$349.96** |

**Root cause**: Even PH-only, CHOP oscillates around 38.2 frequently enough to cause harmful MR→Trend switches. The Trend strategy's Base-tuned parameters (velocity thresholds, SMA window, slope gates) are mismatched for PH dynamics.

### Final: ChopOverrideEnabled = false (reverted)

| Date | P/L | Notes |
|---|---|---|
| Feb 9 | +$5.55 | Baseline match |
| Feb 10 | +$12.92 | Baseline match |
| Feb 11 | +$196.96 | Baseline match |
| Feb 12 | +$186.92 | Baseline match |
| Feb 13 | +$174.99 | Baseline match |
| Feb 17 | +$89.22 | Baseline match |
| Feb 18 | +$157.08 | Baseline match |
| **Total** | **$823.64** | **= Baseline** |

---

## Key Findings

### 1. CHOP Override Causes Mode-Switching Whipsaws
PH CHOP readings on this dataset oscillate around 38.2, causing rapid MR↔Trend flipping. Each switch disrupts whichever strategy was in progress — MR positions get abandoned at unfavorable %B levels, and Trend entries use Base-tuned parameters that misread PH volatility.

**Feb 9 anatomy**: CHOP dips below 38.2 during PH → Trend mode activates → velocity/SMA logic enters BULL during a directionless period → stops out for -$57 vs baseline.

**Feb 18 anatomy**: Similar pattern. CHOP oscillation during PH causes repeated strategy switches. Net result: -$199 vs baseline.

### 2. The Trend Strategy Needs PH-Specific Parameters
The core issue isn't the CHOP override concept — it's that the Trend strategy reuses Base-phase-optimized settings during PH:
- **Velocity thresholds**: Tuned for Base phase volatility (0.000015). PH has different characteristics.
- **SMA window**: 5400s (90 min) is too long for PH's 2-hour window.
- **Slope gates**: Entry/exit slopes calibrated for Base phase price dynamics.

When the bot switches to "Trend" during PH, it applies these mismatched parameters and generates poor entries/exits.

### 3. All Infrastructure Is Correctly Built
Despite the P/L-neutral result, the session produced correct architectural pieces:
- `DetermineStrategyMode()` now restricts CHOP override to PH only
- Scramble logic is gated behind `!MR && MinSlope > 0 && CHOP < 35`
- Slope filter at 0.0002 is ready and will activate when displacement fires during Trend phases
- All features are independently testable when prerequisites are met

### 4. CHOP Threshold 38.2 Is Too High for Trend Rescue
The Fibonacci-derived 38.2 threshold was designed for general regime classification, not for the specific "Trend Rescue" use case. PH CHOP hovers near this value on normal (non-trending) days, causing false positives.

### 5. Displacement, Slope, and Scramble Remain Dormant
All displacement opportunities in the 7-day dataset occur during MR phases. With `ChopOverrideEnabled = false`, these are suppressed. The slope filter (0.0002) and scramble logic have no opportunities to activate. They're correctly gated and waiting for either:
- A genuine PH trend day (CHOP stays low throughout PH)
- ChopOverrideEnabled to be turned on with better parameters

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
  "DisplacementMinSlope": 0.0002,
  "ChopOverrideEnabled": false,
  "ChopLowerThreshold": 38.2,
  "ChopUpperThreshold": 61.8,
  "PhDefaultStrategy": "MeanReversion",
  "BaseDefaultStrategy": "Trend"
}
```

---

## Files Modified (This Session)

**MarketBlocks** (1 file):
- `MarketBlocks.Bots/Services/AnalystEngine.cs` — `DetermineStrategyMode()` PH-only guard; Scramble one-shot reset block

**qqqBot** (1 file):
- `qqqBot/appsettings.json` — `DisplacementMinSlope: 0 → 0.0002`, `ChopOverrideEnabled: false → true → false` (tested and reverted)

---

## Questions for Research AI

### Q1: PH-Specific Trend Parameters for Trend Rescue
The CHOP override fails because Trend logic reuses Base-phase parameters. Two possible approaches:

**Option A — TimeRules Override**: Add a "Power Hour Trend" TimeRules entry with PH-specific velocity, SMA window, slope gates. When `ChopOverrideEnabled` flips to Trend during PH, these parameters would be active.

**Option B — Separate Trend Rescue Strategy**: Create a third `StrategyMode.TrendRescue` that uses its own signal logic — simpler than full Trend (maybe just slope + displacement, no velocity/SMA hysteresis). Less disruption risk.

Which approach is preferred? Are there specific PH Trend parameters you'd recommend as starting points?

### Q2: CHOP Threshold Tightening
38.2 causes too many false switches. Should we:
- **Lower to 25-30** (only truly strong trends trigger rescue)?
- **Add hysteresis** (e.g., enter Trend at CHOP < 30, stay until CHOP > 45)?
- **Add duration requirement** (CHOP must stay below threshold for N candles before switching)?

### Q3: The Fundamental Displacement Problem
All displacement opportunities in the 7-day dataset occur during Power Hour (MR phase). This means displacement re-entry is architecturally blocked regardless of slope/scramble/CHOP override settings.

Is displacement fundamentally mismatched with this bot's structure? The bot's Base-phase trend trades stop out, then price moves during PH — but PH is MR territory. Possible solutions:
- **Extend Base phase** to overlap early PH (e.g., Base runs until 14:30 instead of 14:00)?
- **Phase-aware displacement**: Fire displacement into Base's stop-loss price even during PH, but use MR entry logic instead of Trend entry logic?
- **Accept dormancy**: Displacement is insurance for rare trend days. Keep infrastructure, don't force activation on range-bound data.

### Q4: Scramble Threshold Validation
The scramble uses `|slope| > 2× MinSlope && CHOP < 35`. With `MinSlope = 0.0002`:
- Scramble triggers when `|normalizedSlope| > 0.0004` AND `CHOP < 35`
- Is 2× the right multiplier, or should it be higher (3×, 4×) for safety?
- Should scramble also require BBW expansion (matching the displacement AND gate)?

### Q5: Dataset Limitations
7 days (Feb 9-18) may not contain a single genuine PH trend day. Before investing in PH Trend parameters, should we:
- Fetch more historical data (January, late December)?
- Specifically search for days where CHOP stayed below 35 during PH?
- Run a "CHOP profiler" across available data to characterize PH CHOP distributions?
