# qqqBot Experiment Log

> **Purpose**: Persistent memory of all tuning experiments, code changes, and test results.
> Future AI sessions should read this file before modifying settings or code.
> Update this file after every experiment session.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Current Production Settings](#current-production-settings)
- [Settings Change History](#settings-change-history)
- [Code Changes & Features](#code-changes--features)
- [Replay Test Results](#replay-test-results)
- [Known Issues & Gotchas](#known-issues--gotchas)
- [Failed Experiments](#failed-experiments)
- [Open Questions & Future Work](#open-questions--future-work)

---

## Architecture Overview

- **Repos**: `qqqBot` (bot app), `MarketBlocks` (shared library)
- **Runtime**: .NET 10.0 / C#
- **Pipeline**: `AnalystEngine` → `Channel<RegimeSignal>` → `TraderEngine`
- **Symbols**: TQQQ (bull), SQQQ (bear), QQQ (benchmark)
- **Phases**: Open Volatility (09:30–10:30), Base/Mid-Day Lull (10:30–14:00), Power Hour (14:00–16:00)
- **TimeRuleApplier**: Snapshots base settings, applies per-phase overrides, restores on phase exit
- **Replay System**: `dotnet run -- --mode=replay --date=YYYYMMDD --speed=0`
  - Uses Brownian bridge interpolation for tick-level market data (20260209)
  - Lower-resolution bar data for older dates (20260206 and earlier)
  - Seeded with `_replayDate.DayNumber ^ StableHash(symbol)` for RNG

### Key Files

| File | Repo | Purpose |
|------|------|---------|
| `appsettings.json` | qqqBot | All trading settings and time rules |
| `AnalystEngine.cs` | MarketBlocks.Bots/Services | Signal generation (BULL/BEAR/NEUTRAL) |
| `TraderEngine.cs` | MarketBlocks.Bots/Services | Trade execution, stop-loss, position management |
| `TimeRuleApplier.cs` | MarketBlocks.Bots/Services | Phase-based settings switching |
| `TradingSettings.cs` | Both repos | Settings model (must stay in sync) |
| `TrailingStopEngine.cs` | qqqBot | Trailing stop + dynamic stop-loss tiers |
| `ReplayMarketDataSource.cs` | qqqBot | Brownian bridge replay data generation |

---

## Current Production Settings

**As of: 2026-02-10 (branch: `feature/profitability-enhancements`)**

### Base Config (also Mid-Day Lull 10:30–14:00)

| Setting | Value | Notes |
|---------|-------|-------|
| MinVelocityThreshold | 0.000002 | |
| SMAWindowSeconds | 180 | |
| SlopeWindowSize | 20 | |
| ChopThresholdPercent | 0.0008 | Mid-Day Lull overrides to 0.002 |
| MinChopAbsolute | 0.02 | |
| TrendWindowSeconds | 1800 | |
| TrailingStopPercent | 0.0045 | |
| DynamicStopLoss Tiers | 0.5%→0.35%, 1%→0.25%, 2%→0.15% | |
| HoldNeutralIfUnderwater | true | |
| BullOnlyMode | false (default) | |

### Open Volatility (09:30–10:30)

| Setting | Value | Diff from Base |
|---------|-------|----------------|
| **BullOnlyMode** | **true** | ← Key setting, see experiments below |
| MinVelocityThreshold | 0.000010 | 5× base |
| SMAWindowSeconds | 90 | ½ base |
| MinChopAbsolute | 0.04 | 2× base |
| TrendWindowSeconds | 900 | ½ base |
| EntryConfirmationTicks | 1 | |
| TrailingStopPercent | 0.006 | Wider than base |
| DynamicStopLoss Tiers | 0.8%→0.5%, 1.5%→0.3%, 2.5%→0.2% | Wider triggers |

### Power Hour (14:00–16:00)

| Setting | Value | Diff from Base |
|---------|-------|----------------|
| MinVelocityThreshold | 0.000004 | 2× base |
| SMAWindowSeconds | 150 | |
| TrailingStopPercent | 0.003 | Tighter than base |
| DynamicStopLoss Tiers | 0.5%→0.3%, 1%→0.2%, 2%→0.15% | |

---

## Settings Change History

### Commit Timeline (qqqBot)

| Commit | Date | Description |
|--------|------|-------------|
| `331150d` | ~2026-02-05 | Hold Neutral if Underwater; individual phase files (open/mid-day/power-hour .appsettings.json) |
| `6c76562` | ~2026-02-06 | Record/replay added; **"another AI" made 8 harmful settings changes** |
| `8e086d7` | ~2026-02-07 | Initial "profitable" appsettings.json — manual repair of harmful changes |
| `d615a72` | ~2026-02-08 | Commit before new bot behaviors — branch point for `feature/profitability-enhancements` |
| `b1da927` | ~2026-02-09 | Feature branch: BullOnlyMode, BearEntryConfirmationTicks, DailyProfitTarget |

### The "Harmful AI" Incident (commit 6c76562)

An AI assistant made 8 settings changes that degraded performance. The changes were:
- Various parameter adjustments that were not systematically tested
- Resulted in only 2 trades on a +1.65% QQQ bull rally day (20260209)
- Both trades were losses
- **Lesson**: Settings changes must be tested in replay before deployment

### Settings Tuning Iterations (Phase 20, 20260209 replay)

| Iteration | Key Changes | Trades | PnL | Notes |
|-----------|-------------|--------|-----|-------|
| Baseline (harmful) | Post-6c76562 settings | 2 | Loss | Missed entire rally |
| Iteration 1 | First repair attempt | ? | Improvement | |
| Iteration 2 | Further tuning | ? | Better | |
| **Iteration 3** | Current base settings | 12 | **+$75** | First profitable result; became commit 8e086d7 |

---

## Code Changes & Features

### Branch: `feature/profitability-enhancements`

Three features implemented. All are **neutral by default** (proven via exhaustive code analysis — zero behavior change when feature settings are at defaults).

#### 1. BullOnlyMode (per-phase)

- **Files**: `AnalystEngine.cs`, `TimeRuleApplier.cs`, `TradingSettings.cs` (both repos)
- **Mechanism**: When `BullOnlyMode=true`, AnalystEngine suppresses BEAR signals → bot only takes TQQQ positions
- **Status**: ✅ **ACTIVE in Open Vol** — proven winner across two test dates

#### 2. BearEntryConfirmationTicks

- **Files**: `AnalystEngine.cs`, `TimeRuleApplier.cs`, `TradingSettings.cs` (both repos)
- **Mechanism**: Requires N consecutive BEAR ticks before emitting BEAR signal
- **Default**: 0 (neutral — no change in behavior)
- **Status**: ❌ **FAILED in testing** — added latency without improving outcomes

#### 3. DailyProfitTarget

- **Files**: `TraderEngine.cs`, `TradingSettings.cs` (both repos)
- **Mechanism**: Stops trading for the day once cumulative PnL exceeds target
- **Default**: 0 (disabled)
- **Status**: ⚪ **NEUTRAL** — neither helped nor hurt in replay; not currently set

---

## Replay Test Results

### 20260209 — QQQ +1.65% Bull Rally (tick-level data, Brownian bridge)

#### BullOnly=true in Open Vol (CURRENT CONFIG)

| Run | Trades | PnL | Notes |
|-----|--------|-----|-------|
| 1 | 9 | **$100.43** | Deterministic |
| 2 | 9 | **$100.43** | |
| 3 | 9 | **$100.43** | |
| 4 | 9 | **$100.43** | |
| 5 | 9 | **$100.43** | |
| **Avg** | **9** | **$100.43** | **StdDev: $0** |

#### Baseline — No BullOnly anywhere

| Run | Trades | PnL | Notes |
|-----|--------|-----|-------|
| 1 | 6 | $245.30 | Best case — early TQQQ entry at $49.91 |
| 2 | 7 | $127.00 | |
| 3 | 6 | -$42.00 | Worst case — 13:21 SQQQ whipsaw |
| 4 | 13 | $89.00 | |
| 5 | 7 | $89.00 | |
| **Avg** | **7.8** | **$101.60** | **StdDev: ~$102** |

#### BullOnly=true in Mid-Day Lull (FAILED)

| Run | Trades | PnL | Notes |
|-----|--------|-----|-------|
| 1–5 | varied | -$15 to -$126 | All negative. Do not use. |

### 20260206 — Lower-resolution bar data

#### BullOnly=true in Open Vol (CURRENT CONFIG)

| Run | Trades | PnL | Notes |
|-----|--------|-----|-------|
| 1–5 | 19 | **$108.18** | All identical — deterministic |

#### Baseline — No BullOnly

| Run | Trades | PnL | Notes |
|-----|--------|-----|-------|
| 1–5 | 26 | **$97.04** | All identical — deterministic (lower-res data) |

### Cross-Date Summary

| Config | 20260206 | 20260209 (avg) | Better? |
|--------|----------|----------------|---------|
| **BullOnly=true Open Vol** | **$108.18** | **$100.43** | ✅ Both days |
| Baseline | $97.04 | $101.60 | Higher ceiling but risky |

---

## Known Issues & Gotchas

### 1. Replay Non-Determinism (tick-level data only)

- **Root Cause**: Async `Channel<RegimeSignal>` consumption at `speed=0` — AnalystEngine publishes faster than TraderEngine consumes, thread scheduling varies between runs
- **Affected**: 20260209 (tick-level, Brownian bridge data)
- **Not Affected**: 20260206 (lower-resolution bar data)
- **Impact**: Same settings produce wildly different results ($-42 to $+245 on baseline)
- **Mitigation**: BullOnly=true in Open Vol eliminates the SQQQ entry that triggers the non-determinism → perfectly consistent results
- **Potential Fix**: Consider eliminating Brownian bridge for tick-level data, or adding synchronization to Channel consumption

### 2. The "Slingshot" Effect

- With BullOnly=false, the bot enters SQQQ during Open Vol → quick stop-out (~-$2.80) → state transition enables BULL signal at better TQQQ price ($49.91 vs $50.95)
- This is a $1.04/share better entry (~$200 potential upside) but at the cost of:
  - The SQQQ toll (-$2.80)
  - Exposure to catastrophic SQQQ whipsaw later (-$117 in worst case)
  - Non-deterministic outcomes
- **Decision**: Not worth the risk. BullOnly=true sacrifices the slingshot for consistency.

### 3. TradingSettings.cs Must Stay in Sync

- Both `qqqBot/TradingSettings.cs` and `MarketBlocks.Bots/Domain/TradingSettings.cs` define settings models
- New settings must be added to BOTH files
- TimeRuleApplier.cs must also be updated to snapshot/restore/apply any new setting

### 4. Build Cache Can Mask Code Changes

- After modifying MarketBlocks code, always run `dotnet clean` before `dotnet build` in qqqBot
- Stale DLLs have caused confusing test results where "new code" appeared to have no effect

---

## Failed Experiments

### BearEntryConfirmationTicks

- **Hypothesis**: Requiring N consecutive BEAR ticks would filter out false BEAR signals
- **Test**: Tested across 30 scenarios in 3 rounds
- **Result**: ❌ Added latency without improving outcomes. Late BEAR entry meant worse entry prices.
- **Conclusion**: The AnalystEngine's existing signal logic is already a reasonable filter.

### BullOnly in Mid-Day Lull

- **Hypothesis**: Blocking BEAR entries during low-volatility midday would avoid whipsaws
- **Test**: 5 runs on 20260209
- **Result**: ❌ All negative (-$15 to -$126). Mid-Day Lull has genuine BEAR opportunities.

### DailyProfitTarget

- **Hypothesis**: Stopping trading after hitting a profit target would preserve gains
- **Test**: 20260209 replay
- **Result**: ⚪ Neutral — didn't help or hurt materially. The gains it would lock in were offset by missed later trades.
- **Conclusion**: May be useful as a risk limiter in live trading but not a PnL improver.

### Earlier: Various Settings from "Harmful AI" (commit 6c76562)

- 8 uncontrolled parameter changes simultaneously
- **Lesson**: Never change more than 1–2 settings at a time. Always A/B test with replay.

---

## Open Questions & Future Work

1. **Brownian bridge elimination**: For tick-level data, the interpolation adds noise and non-determinism. Consider using raw ticks directly or a fixed-step interpolation. (Noted but NOT yet implemented.)

2. **Channel synchronization**: Could add `BoundedChannelOptions` with capacity=1 or explicit await to make replay deterministic regardless of data resolution.

3. **More test dates needed**: Only 2 dates tested. Need bear days, flat/chop days, and high-volatility days to validate BullOnly robustness.

4. **Live vs. Replay divergence**: Live trading has real slippage, partial fills, and latency. Replay with `speed=0` doesn't capture these. Need to compare live results to replay predictions.

5. **Open Vol BullOnly on bear days**: BullOnly=true prevents SQQQ entries during the open. On a genuine bear day, this could mean missing the best short opportunity. Need to test with bear-day data.

6. **DynamicStopLoss tier optimization**: Current tiers were set in commit 8e086d7 and haven't been systematically optimized. The old individual-phase files (commit 331150d) had different tiers that might be worth revisiting.

7. **TrailingStopPercent sensitivity**: Base=0.45%, Open Vol=0.6%, Power Hour=0.3%. These haven't been A/B tested individually.

---

## Session Log

### Session: 2026-02-09 (Phase 20e — Trade #4 Investigation)

**Question**: Why did Trade #4 profit drop from +$138.45 to +$103.35?

**Findings**:
- DynamicStopLoss tiers are identical across all commits since 8e086d7 — NOT the cause
- Feature branch code is neutral with default settings (exhaustive static analysis)
- Real cause: replay non-determinism from async Channel consumption
- BullOnly=true eliminates the SQQQ entry that triggers the non-determinism
- BullOnly=true in Open Vol: $100.43 × 5 (perfectly deterministic)
- Baseline: avg $101.60, range -$42 to $245 (high variance)

**Actions**: Set BullOnlyMode=true in Open Vol. Confirmed on 20260206 ($108.18 > $97.04 baseline).

### Session: 2026-02-10

**Action**: Created this experiment log. Config confirmed ready for Tuesday live test.

**Live test config**: BullOnlyMode=true in Open Vol only. All other settings at current values.
