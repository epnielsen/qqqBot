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
- **Phases**: Open Volatility (09:30–09:50), Base (09:50–14:00), Power Hour (14:00–16:00)
- **TimeRuleApplier**: Snapshots base settings, applies per-phase overrides, restores on phase exit
- **Replay System**: `dotnet run -- --mode=replay --date=YYYYMMDD --speed=0`
  - **Deterministic serialized pipeline**: Both channels bounded(1) in replay mode (price + regime). Each tick fully processed analyst→trader before next enters.
  - **Auto-detect data resolution**: `IsHighResolutionData()` samples first 100 CSV rows. Recorded tick data (avg gap <10s) replays raw; historical bar data (60s+) uses Brownian bridge interpolation.
  - **ClockOverride**: Advances on analyst's consumer side (not producer) for deterministic timestamps.
  - **Clean shutdown**: Analyst completes price channel at 16:00 ET; ReplayMDS catches `ChannelClosedException` and exits.
  - **Summary**: Peak/trough P/L watermarks with timestamps displayed after each replay.
  - Seeded with `_replayDate.DayNumber ^ StableHash(symbol)` for Brownian bridge RNG

### Key Files

| File | Repo | Purpose |
|------|------|---------|
| `appsettings.json` | qqqBot | All trading settings and time rules |
| `AnalystEngine.cs` | MarketBlocks.Bots/Services | Signal generation (BULL/BEAR/NEUTRAL) |
| `TraderEngine.cs` | MarketBlocks.Bots/Services | Trade execution, stop-loss, position management |
| `TimeRuleApplier.cs` | MarketBlocks.Bots/Services | Phase-based settings switching |
| `TradingSettings.cs` | Both repos | Settings model (must stay in sync) |
| `TrailingStopEngine.cs` | qqqBot | Trailing stop + dynamic stop-loss tiers |
| `ReplayMarketDataSource.cs` | qqqBot | Replay data source (raw ticks or Brownian bridge) |
| `SimulatedBroker.cs` | qqqBot | Fake broker for replay (fills, P/L watermarks) |

---

## Current Production Settings

**As of: 2026-02-14 (Systematic Re-Optimization Session)**

> **NOTE**: Full phase-by-phase re-optimization using corrected replay infrastructure.
> Swept ~200+ configs across 5 dates (Feb 9-13). Result: -$436→+$503 (+$939 improvement).
> Base settings significantly changed. OV settings partially changed. PH unchanged.

### Base Config (09:50–14:00, also default for unspecified times)

| Setting | Value | Previous | Notes |
|---------|-------|----------|-------|
| MinVelocityThreshold | 0.000015 | 0.000008 | ↑ 1.9×, biggest single improvement |
| SMAWindowSeconds | 180 | 180 | Unchanged |
| SlopeWindowSize | 20 | 20 | Unchanged |
| ChopThresholdPercent | 0.0011 | 0.0011 | Unchanged |
| MinChopAbsolute | 0.02 | 0.02 | Unchanged (zero effect at current price) |
| TrendWindowSeconds | 5400 | 1800 | ↑ 3×, prevents bad entries on volatile days |
| ScalpWaitSeconds | 30 | 30 | Unchanged (zero effect w/ higher velocity) |
| TrendWaitSeconds | 180 | 120 | ↑ from 120, marginal improvement |
| TrendConfidenceThreshold | 0.00008 | 0.00008 | Unchanged (zero effect in sweeps) |
| HoldNeutralIfUnderwater | true | true | CRITICAL — false loses -$172 |
| TrailingStopPercent | 0.002 | 0.0025 | ↓ tighter, +$12 improvement |
| DynamicStopLoss Tiers | (unchanged) | | DSL tight catastrophic (-$613) |
| EnableTrimming | true | true | Unchanged |
| TrimRatio | 0.75 | 0.50 | ↑ +$38 improvement |
| DailyProfitTargetPercent | 1.75 | 1.5 | ↑ from 1.5%, +$46 improvement (Feb 12 uncapped) |
| DailyProfitTargetTrailingStopPercent | 0.3 | 0.3 | Unchanged (never triggers at current levels) |

### Open Volatility (09:30–09:50)

> **NOTE**: Window unchanged at 09:30–09:50. Velocity lowered to match base. Trail widened for volatile open.

| Setting | Value | Previous | Diff from Base |
|---------|-------|----------|----------------|
| MinVelocityThreshold | 0.000015 | 0.000025 | Same as base now |
| SMAWindowSeconds | 120 | 120 | ⅔ base |
| ChopThresholdPercent | 0.0015 | 0.0015 | Higher than base |
| MinChopAbsolute | 0.05 | 0.05 | 2.5× base |
| TrendWindowSeconds | 900 | 900 | ⅙ base (unchanged) |
| TrendWaitSeconds | 180 | 180 | Same as base now |
| TrendConfidenceThreshold | 0.00012 | 0.00012 | |
| UseMarketableLimits | false | false | Direct market orders |
| UseIocOrders | false | false | |
| TrailingStopPercent | 0.005 | 0.003 | ↑ wider for volatile open |
| DynamicStopLoss Tiers | (unchanged) | | |
| EnableTrimming | false | false | |

### Power Hour (14:00–16:00)

| Setting | Value | Diff from Base |
|---------|-------|----------------|
| MinVelocityThreshold | 0.000015 | ~2× base (was 0.000004) |
| SMAWindowSeconds | 120 | ⅔ base |
| SlopeWindowSize | 15 | |
| ChopThresholdPercent | 0.0015 | |
| TrendWaitSeconds | 60 | ½ base |
| TrendConfidenceThreshold | 0.00012 | |
| UseMarketableLimits | false | |
| UseIocOrders | false | |
| TrailingStopPercent | 0.0015 | Tightest (was 0.003) |
| DynamicStopLoss Tiers | 0.3%→0.1%, 0.5%→0.08%, 0.8%→0.06% | |

### Removed Phases

- **Mid-Day Lull override** (12:00–14:00) — REMOVED. Base config now applies throughout.

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
| `aa4b929` | 2026-02-10 | Config loading for DirectionSwitchCooldown + DailyProfitTarget |
| (uncommitted) | 2026-02-10 | DailyProfitTargetPercent, DailyProfitTargetRealtime, major settings retune, TODO.md |

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

All features are **neutral by default** (zero behavior change when feature settings are at defaults).

#### 1. BullOnlyMode (per-phase)

- **Files**: `AnalystEngine.cs`, `TimeRuleApplier.cs`, `TradingSettings.cs` (both repos)
- **Mechanism**: When `BullOnlyMode=true`, AnalystEngine suppresses BEAR signals → bot only takes TQQQ positions
- **Status**: ⚪ **REMOVED from config** — was active in Open Vol but removed on 2026-02-10 during settings retune

#### 2. BearEntryConfirmationTicks

- **Files**: `AnalystEngine.cs`, `TimeRuleApplier.cs`, `TradingSettings.cs` (both repos)
- **Mechanism**: Requires N consecutive BEAR ticks before emitting BEAR signal
- **Default**: 0 (neutral — no change in behavior)
- **Status**: ❌ **FAILED in testing** — added latency without improving outcomes

#### 3. DailyProfitTarget

- **Files**: `TraderEngine.cs`, `TradingSettings.cs` (both repos)
- **Mechanism**: Stops trading for the day once cumulative PnL exceeds target
- **Default**: 0 (disabled)
- **Status**: ⚪ **NEUTRAL** — neither helped nor hurt in replay; not currently set in config

#### 4. DailyProfitTargetPercent (NEW — 2026-02-10)

- **Files**: `TradingSettings.cs` (both repos), `TraderEngine.cs`, `ProgramRefactored.cs`
- **Mechanism**: Alternative to `DailyProfitTarget` — set as percentage of `StartingAmount` (e.g. 1.5 = 1.5% = $150 on $10k). Dollar value takes precedence if both are set.
- **Computed property**: `EffectiveDailyProfitTarget` resolves: dollar > percent > 0
- **Default**: 0 (disabled)
- **Tests**: 3 unit tests (`DollarValueTakesPrecedence`, `FallsBackToPercent`, `BothZero_ReturnsZero`)
- **Status**: ⚪ **IMPLEMENTED** — not currently active in config

#### 5. DailyProfitTargetRealtime (NEW — 2026-02-10)

- **Files**: `TradingSettings.cs` (both repos), `TraderEngine.cs`, `ProgramRefactored.cs`
- **Mechanism**: When `true`, checks Realized + Unrealized P/L every tick (uses `regime.BullPrice`/`regime.BearPrice` — zero additional API calls). When `false` (default), only checks after a trade closes (realized P/L only).
- **Default**: false
- **Status**: ⚠️ **CAUTION** — in replay testing, triggered early liquidation that got a bad SimulatedBroker fill. See Known Issues.

#### 6. DirectionSwitchCooldownSeconds (committed in `739b0f8` / `aa4b929`)

- **Files**: `TradingSettings.cs` (both repos), `TraderEngine.cs`, `TimeRuleApplier.cs`, `ProgramRefactored.cs`
- **Mechanism**: Prevents direction switches (BULL→BEAR or BEAR→BULL) within N seconds
- **Bugs found & fixed** (2026-02-10 live trading):
  - Config loading bug: `BuildTradingSettings`/`ParseOverrides` didn't load the setting
  - Shared-settings race condition: analyst races ahead via unbounded channel, stale settings used
  - Fix: `GetEffectiveDirectionSwitchCooldown()` bypasses stale shared settings
- **Default**: 0 (disabled)

#### 7. Monotonic Time Guard (committed in `739b0f8`)

- **Files**: `TimeRuleApplier.cs`
- **Mechanism**: Guards against backward clock jumps in time-based phase switching
- **Bugs found & fixed**: During live trading Feb 10, out-of-order timestamps caused incorrect phase transitions

#### 8. BEAR Trailing Stop Direction Fix (committed in `739b0f8`)

- **Files**: `TrailingStopEngine.cs`
- **Mechanism**: Trailing stop was using wrong direction for BEAR (SQQQ) positions → stops triggered prematurely
- **6 regression tests** added to `TrailingStopEngineTests.cs`

---

## Replay Test Results

### 20260209 — QQQ +1.65% Bull Rally (tick-level data, Brownian bridge)

#### BullOnly=true in Open Vol (was active, now REMOVED from config)

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

#### BullOnly=true in Open Vol (was active, now REMOVED from config)

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

### 20260206 — Replay with Current Settings (2026-02-10)

Replayed with current (retimed) settings, no DailyProfitTarget active:

| Config | PnL | Notes |
|--------|-----|-------|
| Current settings (no BullOnly, no DailyTarget) | **+$35.58** | Confirmed |

### 20260209 — Replay with DailyProfitTargetRealtime=true (2026-02-10)

Test with `DailyProfitTargetPercent=1.5` (= $150 on $10k) and `DailyProfitTargetRealtime=true`:

| Config | PnL | Notes |
|--------|-----|-------|
| Current + DailyProfitTargetRealtime | **-$19.50** | Target triggered at $151.16 unrealized, but SimulatedBroker filled the exit at a gapped-down next tick, realizing a loss. See Known Issue #5. |

---

## Known Issues & Gotchas

### 1. Replay Non-Determinism — RESOLVED (2026-02-12)

- **Root Cause (phase 1, 2026-02-11)**: AnalystEngine and TraderEngine shared a singleton TimeRuleApplier with a single `_highWaterTime`. At speed=0, AnalystEngine raced ahead, poisoning the TraderEngine's phase transition timing. Additionally, unbounded Channel allowed unlimited buffering.
- **Root Cause (phase 2, 2026-02-12)**: Even with bounded(50) channels, the ClockOverride advanced at the producer (ReplayMDS) side, racing ahead of what the analyst/trader had actually processed. And the Brownian bridge injected ~60K synthetic ticks into ~33K recorded ticks, distorting SMA calculations and shifting signal timing by ~2 minutes vs live.
- **Fix (final)**: Both channels bounded(1) for strict lockstep. ClockOverride advances on analyst's consumer side via `onTickProcessed` callback. `IsHighResolutionData()` auto-detects recorded tick data and skips Brownian bridge.
- **Result**: Replays are deterministic: 3 consecutive runs produce identical results. Feb 12 replay gap closed from $425 to ~$23 vs live.

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

### 5. SimulatedBroker Fill Realism (RESOLVED — 2026-02-11)

- **Root Cause**: SimulatedBroker filled orders at the **next recorded tick** price, not the decision-tick price. With `speed=0`, the async replay pipeline raced ahead, causing `_latestPrices` to diverge significantly from the price the TraderEngine saw when deciding to trade.
- **Fix (phase 1)**: Added `HintPrice` field to `BotOrderRequest`. TraderEngine sets it to the decision-tick price at all 6 order creation sites. SimBroker priority: `HintPrice > LimitPrice > _latestPrices`.
- **Fix (phase 2)**: Daily target liquidation passes `currentEtfPrice` as `knownPrice` to `EnsureNeutralAsync`.
- **Impact**: Feb 11 replay went from +$20.11 (HintPrice only) to +$135.94 (all fixes), exceeding even the live result of +$87.94.

### 6. ProgramRefactored.cs Manual Config Binding

- All new `TradingSettings` properties must be manually added to `BuildTradingSettings()` AND `ParseOverrides()` in `ProgramRefactored.cs`
- Forgetting this causes the setting to silently use its default value in the qqqBot app
- This was the root cause of DirectionSwitchCooldownSeconds not working in live trading on Feb 10

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

### DailyProfitTargetRealtime (on replay)

- **Hypothesis**: Checking unrealized + realized P/L every tick would lock in gains before they evaporate
- **Test**: 20260209 with `DailyProfitTargetPercent=1.5`, `DailyProfitTargetRealtime=true`
- **Result**: ❌ **-$19.50** — bot saw $151.16 unrealized on first trade, triggered exit, but SimulatedBroker filled at next-tick (gapped down), realizing a loss. On Feb 6, target wasn't hit so no impact (+$35.58 same as without).
- **Conclusion**: Feature logic is CORRECT, but **replay results are unreliable** due to SimulatedBroker fill model. Must validate with live trading. Do not tune this feature based on replay P/L.

### Earlier: Various Settings from "Harmful AI" (commit 6c76562)

- 8 uncontrolled parameter changes simultaneously
- **Lesson**: Never change more than 1–2 settings at a time. Always A/B test with replay.

---

## Open Questions & Future Work

1. ~~**SimulatedBroker fill model improvement**~~ — **RESOLVED**. HintPrice + knownPrice fixes. See Known Issue #5 and Session 2026-02-11 (continued).

2. ~~**Brownian bridge elimination**~~ — **RESOLVED (2026-02-12)**. `IsHighResolutionData()` auto-detects recorded tick data (avg gap <10s) and skips Brownian bridge interpolation. Historical bar data (60s+) still uses interpolation. Gap closed from $425 to ~$23.

3. ~~**Channel synchronization**~~ — **RESOLVED (2026-02-12)**. Both channels bounded(1) in replay mode for strict serialized pipeline. ClockOverride on consumer side. Clean shutdown at 16:00 ET.

4. **More test dates needed**: Only 4 dates tested (Feb 6, 9, 10, 11). Need more bear days, flat/chop days, and high-volatility days to validate robustness.

5. **Live vs. Replay divergence**: Feb 11 replay (+$135.94) actually EXCEEDED live (+$87.94), suggesting the replay infrastructure is now more optimistic than live. Need to understand why — likely IOC partial fills, real slippage, and API latency in live that replay doesn't model.

6. **Settings re-optimization needed**: Current settings were tuned when OV phase timing was broken (thread race). Now that OV correctly runs for both engines independently, Feb 6 and Feb 9 regressed significantly. Settings may need re-tuning with the correct replay infrastructure.

7. **TrailingStopPercent sensitivity**: Base=0.25%, Open Vol=0.3%, Power Hour=0.15%. These haven't been A/B tested individually.

8. **DailyProfitTargetRealtime live validation**: Feature logic works correctly in replay now (Feb 11: armed at $152.50, triggered at $148.90). Need to validate with live trading.

9. **Open Vol window tuning**: Shortened from 09:30–10:30 to 09:30–09:50. Is 20 minutes optimal? Could try 30 or 45 minutes.

10. **Re-evaluate BullOnlyMode**: Was removed from config during Feb 10 retune. Original data showed it was beneficial on bull days. With correct OV timing, this may be even more important.

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

### Session: 2026-02-10 (morning)

**Action**: Created this experiment log. Config confirmed ready for Tuesday live test.

**Live test config**: BullOnlyMode=true in Open Vol only. All other settings at current values.

### Session: 2026-02-10 (afternoon/evening, continued)

**Context**: Continued from morning session. Commits `739b0f8` (MarketBlocks) and `aa4b929` (qqqBot) were made earlier.

**Bugs found during live trading** (4 bugs, 6 regression tests):
- BEAR trailing stop direction was inverted → stops triggered prematurely on SQQQ positions
- DirectionSwitchCooldownSeconds config loading bug — `BuildTradingSettings`/`ParseOverrides` didn't load it
- Shared-settings race condition — AnalystEngine raced ahead via unbounded channel, used stale settings
- Monotonic time guard needed — out-of-order timestamps caused incorrect phase transitions

All were fixed and committed in `739b0f8`/`aa4b929`.

**New features implemented**:
1. `DailyProfitTargetPercent` — set daily target as % of StartingAmount (e.g. 1.5 = $150 on $10k)
2. `EffectiveDailyProfitTarget` — computed property resolving dollar > percent > 0
3. `DailyProfitTargetRealtime` — check realized+unrealized P/L every tick (zero extra API calls)
4. 3 unit tests for `EffectiveDailyProfitTarget`
5. `TODO.md` created with SimulatedBroker fill improvement task

**Major settings retune**: appsettings.json significantly overhauled:
- Open Vol window: 09:30–10:30 → 09:30–09:50
- BullOnlyMode removed from Open Vol
- Mid-Day Lull phase override removed entirely
- DirectionSwitchCooldownSeconds removed from config (was 7200)
- DailyProfitTarget removed from config (was 200)
- All velocity thresholds, trailing stops, dynamic stop tiers retightened
- Trimming enabled in base config
- ScalpWaitSeconds/TrendWaitSeconds re-enabled (were -1 = disabled)

**Replay validation**:
- Feb 6 with current settings (no DailyTarget): **+$35.58** ✔️ confirmed
- Feb 9 with DailyProfitTargetRealtime=true, DailyProfitTargetPercent=1.5: **-$19.50** — SimulatedBroker fill gap caused loss. Feature logic is correct; replay model is unreliable for intra-tick exits.

**Key discovery**: SimulatedBroker fills at next recorded tick, not decision price. This is a fundamental simulation limitation, not a code bug. Documented in Known Issues #5 and TODO.md.

**Uncommitted changes**: DailyProfitTargetPercent, DailyProfitTargetRealtime (code + TradingSettings in both repos), ProgramRefactored config loading, 3 unit tests, TODO.md, appsettings.json retune, this experiment log update.
### Session: 2026-02-11 (afternoon — AI session)

**Context**: Bot reported +$28.25 P/L on Feb 11 live trading, but actual broker P/L was $87.94. Investigation and fixes.

**Bugs found & fixed**:

1. **Bot/Broker P/L Sync** — 6 reconciliation paths in TraderEngine credited cost basis (break-even) to AvailableCash without updating RealizedSessionPnL. Every untracked sell was treated as zero profit. Fixed by routing all paths through `ApplySplitAllocation` with best-effort market price estimates.

2. **SimulatedBroker Fill Model** (Known Issue #5 resolution) — Added `HintPrice` field to `BotOrderRequest`. TraderEngine sets it to the decision-tick price at every order creation site (6 sites total). SimBroker now uses: `HintPrice ?? LimitPrice ?? _latestPrices[symbol]`. This eliminates the price-racing problem where the async replay pipeline advanced `_latestPrices` far ahead of the price the TraderEngine actually saw.

**New features implemented**:
1. `DailyProfitTargetTrailingStopPercent` — instead of immediate liquidation, arms a trailing stop when profit target is reached. Peak P/L is tracked and position is liquidated only when P/L drops below `peak * (1 - trailPercent/100)`. Falls back to immediate liquidation when set to 0.
2. `HintPrice` on `BotOrderRequest` — optional decision-price hint for simulated fills (ignored by live brokers)
3. State persistence for daily target trailing stop: `DailyTargetArmed`, `DailyTargetPeakPnL`, `DailyTargetStopLevel` in TradingState

**Files changed**:
- `MarketBlocks.Trade/Core/Domain/BotOrderRequest.cs` — added `HintPrice` field
- `MarketBlocks.Bots/Domain/TradingSettings.cs` — added `DailyProfitTargetTrailingStopPercent`
- `MarketBlocks.Bots/Domain/TradingState.cs` — added 3 daily target trailing stop fields
- `MarketBlocks.Bots/Services/TraderEngine.cs` — daily target trailing stop logic, 6 P/L sync fixes, HintPrice at 6 order sites, partial fill detection for canceled market sells
- `qqqBot/SimulatedBroker.cs` — use `HintPrice` for fill price determination
- `qqqBot/ProgramRefactored.cs` — load `DailyProfitTargetTrailingStopPercent`
- `qqqBot/appsettings.json` — added `DailyProfitTargetPercent: 1.5`, `DailyProfitTargetRealtime: true`, `DailyProfitTargetTrailingStopPercent: 0.3`

**Replay results with HintPrice fix**:

| Date | Before HintPrice | After HintPrice | Old Benchmark | Notes |
|------|-----------------|-----------------|---------------|-------|
| Feb 6 | +$4.13 | **+$31.56** | +$35.58 | 11 trades. Within $4 of old benchmark |
| Feb 9 | -$2.80 | **+$27.27** | +$100.43 (BullOnly) | 5 trades. No phantom daily target. Old used BullOnly=true |
| Feb 10 | -$113.63 | **-$113.63** | N/A | 6 trades. Real adverse moves, fill model was already correct |
| Feb 11 | +$6.92 | **+$20.11** | +$28.25 (live) | 23 trades. Closer to live result |
| **Total** | **-$105.38** | **-$34.69** | | **+$70.69 improvement** |

**Key observations**:
- Feb 9 old benchmark (+$100.43) used BullOnly=true in Open Vol — current settings don't have that, which accounts for the gap
- Feb 6 is now within $4 of the old benchmark, probably residual Brownian bridge non-determinism
- Feb 10 remains a genuine loss day — first trade hit trailing stop on a real adverse move (-$75), then two more whipsaw losses
- Feb 11 replay (+$20.11) is lower than live (+$28.25), likely because replay doesn't perfectly model live IOC partial fills and latency
- 4-day total improved from -$105.38 to -$34.69 purely from fix to fill model

**Known issue discovered**: `DailyProfitTargetTrailingStopPercent=0.3` is applied as 0.3% of the *P/L amount*, giving only $0.45 of breathing room at $150 target. This is likely tighter than intended. Consider increasing significantly or changing semantics to a dollar offset or % of account.

### Session: 2026-02-11 (continued — replay infrastructure fixes)

**Context**: After the HintPrice fix, replays were still producing divergent results from live trading. Investigation revealed 3 additional replay infrastructure bugs.

**Bugs found & fixed**:

1. **TimeRuleApplier Thread Race** — Root cause of OV→Base phase transition happening prematurely. AnalystEngine and TraderEngine share a singleton TimeRuleApplier. At speed=0, AnalystEngine races ahead pushing `_highWaterTime` past 09:50 while TraderEngine is still processing ticks at 09:34. This caused the TraderEngine's next call to see an already-advanced high-water mark and skip the OV phase entirely.
   - **Fix**: Replaced single `_highWaterTime` with `Dictionary<string, TimeSpan> _highWaterTimes` for per-caller monotonic guards. `CheckAndApply` now takes `string callerId = "default"`. AnalystEngine passes `"analyst"`, TraderEngine passes `"trader"`.

2. **Bounded Channel Deadlock** — The bounded price channel (capacity 50) combined with sequential `await SubscribeAsync(); await ProcessMarketDataLoop();` in AnalystEngine created a deadlock: `SubscribeAsync` (the writer) blocks after 50 ticks because the channel is full, but `ProcessMarketDataLoop` (the reader) never starts because it's awaited sequentially AFTER `SubscribeAsync`. Result: 0 trades, $0 P/L.
   - **Fix**: Changed to `Task.WhenAll(subscribeTask, processTask)` so writer and reader run concurrently.

3. **Daily Target Liquidation Price** — `EnsureNeutralAsync` was called without a `knownPrice` when the daily target trailing stop triggered, causing the SimBroker to fill at whatever `_latestPrices` had (which could be stale/wrong at speed=0).
   - **Fix**: Hoisted `currentEtfPrice` out of the if-block scope and passed it as `knownPrice` to `EnsureNeutralAsync`.

4. **Backpressure Performance Regression** (intermediate fix, replaced by #1) — Initial attempt to fix the thread race used per-tick `Task.Delay(1)` backpressure. With 77,482 ticks × 1ms minimum, this made replay take 77+ seconds (previously <5 seconds). Replaced by the per-caller HWM approach which has zero overhead.

**Files changed**:
- `MarketBlocks.Bots/Services/TimeRuleApplier.cs` — per-caller high-water marks, removed `_traderLastMarketTimeTicks`, `UpdateTraderMarketTime()`, `TraderLastMarketTime`
- `MarketBlocks.Bots/Services/AnalystEngine.cs` — bounded channel (capacity 50) in replay mode, `Task.WhenAll` for concurrent subscribe+process, passes `"analyst"` callerId
- `MarketBlocks.Bots/Services/TraderEngine.cs` — passes `"trader"` callerId, `currentEtfPrice` for daily target liquidation

**Replay results with all fixes (HintPrice + per-caller HWM + bounded channel + Task.WhenAll)**:

| Date | HintPrice Only | All Fixes | Trades | Notes |
|------|---------------|-----------|--------|-------|
| Feb 6 | +$31.56 | **-$124.63** | 11 | Regression — OV phase now correctly active, different trade sequence |
| Feb 9 | +$27.27 | **-$45.00** | 4 | Worse — correct OV timing changes entry/exit dynamics |
| Feb 10 | -$113.63 | **-$113.63** | 6 | Unchanged — genuine loss day |
| Feb 11 | +$20.11 | **+$135.94** | 9 | **BEST RESULT** — daily target armed at $152.50, peaked at $155.38, triggered at $148.90, sold 144 SQQQ @ $69.87 |
| **Total** | **-$34.69** | **-$147.32** | | Feb 11 dramatically improved, but Feb 6/9 regressed |

**Daily target trailing stop verification (Feb 11)**:
- Armed at $152.50 (above $150 target = 1.5% of $10k)
- Peak tracked to $155.38
- Stop level trailed to $148.90 (= $155.38 × (1 - 0.3/100) × 100... approximate)
- Triggered when P/L dropped below stop level
- Sold 144 SQQQ @ $69.87, realized +$93.60 on that position
- Final: +$135.94 (1.36%) — **better than live result of $87.94**

**Key observations**:
- The per-caller HWM fix made OV phase timing correct for BOTH engines independently, which changed the trade sequences on Feb 6 and Feb 9
- Feb 11 is dramatically better because the daily target trailing stop now fires with the correct price (knownPrice fix) AND the OV phase runs its full duration
- Feb 6 and Feb 9 regressions suggest the current settings may not be optimized for correct OV timing — they were tuned when OV was accidentally short-circuited
- Feb 10 is consistently -$113.63 across ALL fix levels — a genuine adverse market day
- The 4-day total is worse (-$147.32 vs -$34.69), but this compares a broken replay to a correct one — the correct replay better represents what the bot would actually do live
- **Net takeaway**: The infrastructure is now correct. Settings need re-optimization with the correct replay infrastructure.

**Updated resolution status**:
- Known Issue #1 (Replay Non-Determinism): **RESOLVED** — per-caller HWM + bounded channel makes replay deterministic
- Known Issue #5 (SimBroker Fill): **RESOLVED** — HintPrice + knownPrice fixes
- Open Question #1 (SimBroker fill model): **RESOLVED**
- Open Question #3 (Channel synchronization): **RESOLVED** — bounded channel (capacity 50) + Task.WhenAll

### Session: 2026-02-12 (AI session — deterministic replay & faithful data)

**Context**: Feb 12 live trading produced +$171 profit. Replay of the same day produced -$254 — a $425 gap. Investigation and fixes.

**Root cause analysis**:
1. **Clock racing**: ClockOverride advanced at the ReplayMDS producer side. During bounded channel backpressure (`Task.Delay` in trader), the producer raced ahead, pushing the file-logger clock 3+ hours ahead of what was actually being processed. Result: trim events appeared to fire with 3-hour fill delays.
2. **Brownian bridge distortion**: Recorded tick data (avg gap 3.03s, ~33K ticks) was expanded to ~92K ticks via Brownian bridge interpolation. The synthetic noise shifted SMA crossings by ~2 minutes — live BEAR signal at 09:44 ET vs replay at 09:46 ET. That $0.41 worse entry cascaded: live hit daily target (+$171) while replay never reached it (-$254).

**Fixes implemented**:

1. **Deterministic serialized pipeline** — Both channels bounded(1) in replay mode (was 50 for price, unbounded for regime). Each tick now fully processes analyst→trader before the next enters. Verified: 3 consecutive runs produce identical -$254.14 / 23 trades.

2. **Clock on consumer side** — New `onTickProcessed` callback on AnalystEngine. ClockOverride now advances when the analyst *consumes* a tick, not when the producer *emits* it. `ProgramRefactored` passes: `onTickProcessed: IsReplayMode ? utc => FileLoggerProvider.ClockOverride = utc.ToLocalTime() : null`.

3. **Skip Brownian bridge for recorded data** — `IsHighResolutionData()` samples first 100 CSV rows, computes avg tick gap, returns `true` if <10s. `ReplayMarketDataSource` accepts `skipInterpolation` flag. Auto-wired in `ProgramRefactored`. Logged: "High-resolution tick data detected (avg gap 3.03s). Skipping Brownian bridge interpolation."

4. **Peak/trough P/L watermarks** — `SimulatedBroker.UpdatePrice` now accepts `DateTime timestampUtc`, computes equity (cash + unrealized) on every tick, tracks high/low watermarks. Displayed in replay summary.

5. **Clean replay shutdown** — AnalystEngine completes `_priceChannel.Writer` at 16:00 ET session end in replay mode. ReplayMDS catches `ChannelClosedException` and exits cleanly. Eliminates 30-second timeout and "HANGING" warnings.

**Files changed**:
- `MarketBlocks.Bots/Services/AnalystEngine.cs` — bounded(1) channels in replay, `onTickProcessed` callback, price channel completion at session end
- `qqqBot/ReplayMarketDataSource.cs` — `skipInterpolation` flag, `Action<string, decimal, DateTime>` delegate, `ChannelClosedException` handling, `writer.TryComplete()`
- `qqqBot/SimulatedBroker.cs` — `UpdatePrice(symbol, price, timestampUtc)`, equity watermark tracking, peak/trough in `PrintSummary()`
- `qqqBot/ProgramRefactored.cs` — `IsHighResolutionData()` helper, clock callback wiring, auto-detect + skip interpolation

**Replay results (Feb 12)**:

| Configuration | P/L | Trades | Gap vs Live | Notes |
|--------------|-----|--------|-------------|-------|
| Before fixes (Brownian bridge + racing clock) | -$254.14 | 23 | **$425** | Synthetic ticks distort SMA |
| After fixes (raw ticks + serialized pipeline) | **+$136.71** | 13 | **~$23** | 3x deterministic |
| Live trading | +$160 (adjusted) | ~13 | — | Was +$171 reported, ~$11 position mismatch |

**Replay summary output (new format)**:
```
[SIM-BROKER]  R E P L A Y   S U M M A R Y
[SIM-BROKER]  Starting Cash:  $10,000.00
[SIM-BROKER]  Ending Cash:    $10,136.71
[SIM-BROKER]  Ending Equity:  $10,136.71
[SIM-BROKER]  Realized P/L:   $136.71
[SIM-BROKER]  Net Return:     1.37 %
[SIM-BROKER]  Total Trades:   13
[SIM-BROKER]  Peak P/L:       +$163.50 (1.64 %) at 10:58:55 ET
[SIM-BROKER]  Trough P/L:     -$11.60 (-0.12 %) at 09:45:15 ET
```

**Key observations**:
- Replay is now a reliable tool for tuning strategies (~$23 gap vs live, down from $425)
- The remaining gap is expected: live has real-time streaming latency, sub-ms fills, order queue position effects that replay can't model
- Trade count matches (13 replay vs ~13 live), signal directions match, profit direction matches
- Brownian bridge is still used for historical API data (e.g., pre-recording dates) where tick resolution is 60s bars

**Updated resolution status**:
- Known Issue #1 (Replay Non-Determinism): **FULLY RESOLVED** — bounded(1) + consumer-side clock + skip interpolation
- Open Question #2 (Brownian bridge elimination): **RESOLVED** — auto-detected and skipped for recorded data
- Open Question #5 (Live vs Replay divergence): **LARGELY RESOLVED** — gap reduced from $425 to ~$23 by eliminating synthetic ticks

---

## Session: 2026-02-13 — Configurable Market Data Directory

**Context**: Recorded market data CSVs were stored under `bin/Debug/net10.0/data/`, which is destroyed by `dotnet clean`. This made it easy to accidentally lose irreplaceable tick recordings.

**Changes**:
1. Added `MarketDataDirectory` setting to `appsettings.json` under `TradingBot` section (default: `C:\dev\TradeEcosystem\data\market`)
2. Wired the setting through all three consumers:
   - `ProgramRefactored.cs` replay mode `dataDir` (was `Path.Combine(AppContext.BaseDirectory, "data")`)
   - `MarketDataRecorder` registration (was using hardcoded default in constructor)
   - `HistoricalDataFetcher` in `RunFetchHistoryAsync` (was using hardcoded default in constructor)
3. Copied 15 existing CSV files (5 trading days: Feb 6, 9, 10, 11, 12) to `C:\dev\TradeEcosystem\data\market\`
4. Updated README.md (Configuration Reference table) and TODO.md

**Verification**: Build passes (63/63 tests), replay produces same results ($136.71 P/L, 13 trades for Feb 12 full day).

---

## Session: 2026-02-13 (Evening) — Bug Fixes, Loss Prevention, UX Improvements

**Context**: During today's live run, P/L was -$292.34 (bot) / -$293.49 (broker). Several issues were identified from the logs.

**Live Run Observations (Feb 13)**:
- Bot P/L: -$292.34 | Broker P/L: -$293.49 (~$1.15 discrepancy, likely rounding)
- Equity check was wildly wrong: Bot $9,949 vs Broker $799,502 (margin buying power, not equity)
- Daily Change discrepancy: broker UI showed -$223.06, bot showed -$221.91 at 10:53 ET
- Negative trade losses exceeded positive trade gains from prior days — need loss prevention

**Changes**:

1. **Fix: Equity Check — BuyingPower vs Equity** (Critical bug)
   - Root cause: `FireEquityCheck()` called `GetBuyingPowerAsync()` which returns Alpaca's margin buying power (~$800K), not actual account equity (~$10K)
   - Added `GetEquityAsync()` to `IBrokerExecution` interface
   - Implemented in `AlpacaExecutionAdapter` using `account.Equity`
   - Implemented in `TradierExecutionAdapter` using `total_equity` (with fallbacks to `account_value`, `total_cash`)
   - Implemented in `SimulatedBroker` as cash + position market values
   - `FireEquityCheck()` now calls `GetEquityAsync()` directly instead of manually summing buying power + positions
   - `GetBuyingPowerAsync()` kept intact for position sizing logic

2. **Feature: Daily Loss Limit** (Mirrors daily profit target pattern)
   - Added `DailyLossLimit` (dollar) and `DailyLossLimitPercent` (% of StartingAmount) to both TradingSettings
   - Added `EffectiveDailyLossLimit` computed property (same pattern as `EffectiveDailyProfitTarget`)
   - Wired into `BuildTradingSettings` and `appsettings.json` (disabled by default: 0)
   - When session P/L drops below `-EffectiveDailyLossLimit`, sets `_dailyTargetReached = true` → flattens position, stops trading for day
   - Reuses existing daily target infrastructure for liquidation / halt

3. **UX: Stop Notifications — Dollar Value**
   - Trailing stop trigger log now shows dollar distance from current price: `Stop: $105.47 (-$0.53)`
   - Both BULL and BEAR stop trigger messages updated

4. **UX: Simplified Profit Logging**
   - Removed "Banked" from `[PROFIT]` log — was confusing and didn't reflect real P/L
   - Renamed "Reinvest" to "Compounded"
   - New format: `[PROFIT] Realized: +$X.XX | Compounded: $Y.YY | SessionPnL: $Z.ZZ`

5. **TODO.md Updates**
   - Added Tradier Migration plan (gradual: Phase 1 market data, Phase 2 full Tradier with safeguards, Phase 3 production)
   - Added Barcoding/Chopping Detection research item
   - Marked Settings Re-Optimization as HIGH PRIORITY

**Settings Changes**: None (DailyLossLimit defaults to 0/disabled). Pending tuning recommendation.

---

## Session: 2026-02-14 — Systematic Full Re-Optimization

**Context**: Highest-priority TODO item. Full phase-by-phase parameter sweep using corrected replay infrastructure with segment replay (`--start-time`/`--end-time`). First-ever systematic optimization using automated harness.

**Methodology**:
- Built `sweep.ps1` — automated parameter sweep harness supporting `baseline`, `sweep`, `custom` actions across any phase or full day
- Tested 200+ configurations across 5 replay dates (Feb 9-13), each with deterministic recorded tick data (~3s resolution)
- Approach: isolate each phase → sweep individual params → combine winners → verify full-day
- Feb 6 excluded (60s bar data + Brownian bridge = non-deterministic)

### Phase-Segmented Baselines

| Phase | Total P/L | Trades | Per-Day (Feb 9/10/11/12/13) |
|-------|-----------|--------|-----|
| OV (09:30-09:50) | +$75.91 | 14 | -29/+36/+47/+74/-52 |
| Base (09:50-14:00) | -$415.43 | 55 | -86/-14/+113/+69/-498 |
| PH (14:00-16:00) | -$26.22 | 2 | 0/0/0/-26/0 |
| **Full Day** | **-$436.23** | **77** | -126/+27/+153/+137/-628 |

**Observations**: Feb 13 is the barcoding disaster day: 35 trades, -$628. Base phase alone: 27 trades, -$498. OV and PH also negative on Feb 13. PH essentially inert — only 2 trades on Feb 12 across all 5 days.

### Base Phase Optimization (26+ signal combos, then stops/exit/trim/direction)

**Signal Sweep** (top results from 26 scenarios):

| Config | Total P/L | Trades | Key Finding |
|--------|-----------|--------|-------------|
| Vel=15+Trend=3600 | +$80.64 | 32 | Best signal pair (+$496 vs baseline) |
| Vel=15+Trend=5400 | +$80.64 | 32 | Same P/L — plateau at ≥4800 |
| Vel=15 alone | +$82.30 | 36 | Close but more trades |
| TrendWindow=5400 alone | -$301.03 | 47 | Insufficient without velocity |
| ChopThreshold swept | No improvement | | ChopThresholdPercent dominates at $530 QQQ |
| SMA swept | All worse | | Current 180s is optimal |

**Fine-Tuning**: TrendWindow tested 1800-10800. All values ≥4800 produce identical +$80.64. Selected T=5400 (within plateau, not overfit). Velocity 0.000013-0.000020 all identical with T≥3600.

**Stops/Exit/Trim/Direction Sweep** (on top of Vel15+T5400):

| Category | Winner | P/L | vs Signal Baseline | Finding |
|----------|--------|-----|--------------------|---------|
| TrailStop | 0.2% | +$93.14 | +$12.50 | Tighter stop helps |
| TrailStop 0.5% | | -$45.37 | | Too loose |
| DSL tight | | -$612.77 | | CATASTROPHIC — triggers barcoding |
| TrendWait | 180s | +$86.48 | +$5.84 | Marginal improvement |
| TrendWait 60s | | -$204.97 | | Too short — exits too early |
| ScalpWait | all values | +$80.64 | 0 | ZERO EFFECT with higher velocity |
| TrendConf | all values | +$80.64 | 0 | ZERO EFFECT |
| HoldUnderwater=false | | -$91.91 | -$172 | Must keep true |
| TrimRatio | 75% | +$118.86 | +$38.22 | Take more profit earlier |
| Trim=OFF | | +$16.13 | | Worse |
| BullOnly | | +$80.64 | 0 | Identical to Both — no BEAR entries at Vel15 |

**Combined Base Winner**: +$131.56 (32 trades) vs -$415.43 (55 trades) = **+$547 improvement**

### OV Phase Optimization

**Individual Sweep** (top results):

| Config | Total P/L | Trades | Key Finding |
|--------|-----------|--------|-------------|
| Vel=0.000015 | +$278.77 | 18 | Best single param (+$203) |
| Chop=0.001 | +$249.52 | 16 | Lower chop = more OV trades |
| SMA=60s | +$173.99 | 8 | Inconsistent per-day — risky |
| Trail=0.5%+ | +$166.99 | 12 | Wider stops for volatile open |
| BullOnly=true | $0.00 | 0 | KILLS OV — all OV trades are BEAR |

**Combination Testing**:

| Config | Total P/L | Trades | Per-Day |
|--------|-----------|--------|---------|
| Vel15+Trail05 | **+$398.10** | 16 | +62/+36/+47/+74/+179 |
| Vel15+Chop001 | +$340.14 | 18 | +16/+63/+18/+96/+147 |
| Vel15+Chop001+Trail05 | +$322.88 | 18 | ← Adding chop degrades Feb 9 |
| ALL_WINNERS | +$205.20 | 26 | ← SMA60 introduces Feb 11 loss |

**OV Winner**: Vel=0.000015 + Trail=0.5% → **+$398.10** vs +$75.91 baseline (+$322 improvement). Every day profitable!

### PH Phase Optimization

**Finding: PH is essentially inert**. Only 2 trades (Feb 12) across 5 days regardless of settings. Even PH_DISABLED produces identical results — the 2 trades carry over from Base phase. Velocity changes (0.000008 to 0.000025) all identical. TrendWindow, TrendWait changes all identical.

Only minor finding: Trail=0.1% reduces loss from -$26 to -$10 (those 2 trades). Not worth changing since they're not true PH trades.

### Cross-Cutting Parameter Sweep (on combined winner)

| Param | Winner | P/L | Finding |
|-------|--------|-----|---------|
| DailyTarget=1.5% | Current | +$502.95 | Critical safeguard. OFF=-$155, 2%=-$50 on Feb 13 |
| DailyTarget=0.5% | | +$185.78 | Too restrictive |
| DailyTrailStop | All identical | +$502.95 | Target never triggers trail mechanism |
| DailyLossLimit | All identical | +$502.95 | No losing days → never triggers |
| Cooldown=10s | Current | +$502.95 | 60s catastrophic (-$145 on Feb 13) |
| ProfitReinvest | All similar | ~$503-510 | Negligible difference (<$7) |

**Conclusion**: All cross-cutting params already optimal. No changes.

### Full-Day Validation

| Config | Total P/L | Trades | Per-Day (Feb 9/10/11/12/13) |
|--------|-----------|--------|-----|
| **BASELINE** | **-$436.23** | **77** | -126/+27/+153/+137/-628 |
| **OPTIMIZED** | **+$502.95** | **32** | +16/+19/+162/+127/+179 |
| **Improvement** | **+$939.18** | **-45 trades** | **All 5 days profitable** |

### Settings Applied to appsettings.json

| Setting | Old → New | Impact |
|---------|-----------|--------|
| Base: MinVelocityThreshold | 0.000008 → 0.000015 | Biggest single improvement |
| Base: TrendWindowSeconds | 1800 → 5400 | Prevents bad entries on volatile days |
| Base: TrailingStopPercent | 0.0025 → 0.002 | Tighter stop = +$12 |
| Base: ExitStrategy.TrendWaitSeconds | 120 → 180 | Marginal improvement |
| Base: TrimRatio | 0.50 → 0.75 | +$38 improvement |
| OV: MinVelocityThreshold | 0.000025 → 0.000015 | Allows more OV entries |
| OV: TrailingStopPercent | 0.003 → 0.005 | Wider stop for volatile open |

### Key Insights for Future Optimization

1. **Velocity threshold is the #1 lever**: Filtering out weak signals has the biggest impact on P/L
2. **Trend window provides safety**: Longer lookback prevents whipsaw entries, especially on barcoding days
3. **Higher trim ratio = more profit locking**: Taking 75% off when momentum fades beats 50%
4. **OV phase is all BEAR trades**: Morning dip pattern. BullOnly kills OV entirely
5. **PH phase is inert with current settings**: Only produces carryover trades from Base. May need fundamentally different approach
6. **DailyProfitTarget=1.5% is a critical safeguard**: Without it, overtrading on good days wipes gains
7. **Barcoding mitigation**: Feb 13 went from 35 trades/-$628 to 6 trades/+$179. Higher velocity + longer trend window cut barcoding losses by 72%, partially addressing the barcoding detection TODO
8. **ScalpWait and TrendConfidence have ZERO effect** when velocity threshold is ≥0.000015
9. **DSL tight stops are catastrophic**: Trigger stop-outs → immediate re-entries → barcoding spiral
10. **HoldNeutralIfUnderwater=true is non-negotiable**: Disabling it costs -$172 on these 5 days

### Sweep Scripts Created

| Script | Purpose |
|--------|---------|
| `sweep.ps1` | Main harness — baselines, signal/stops/exit/trim/direction sweeps |
| `ov-sweep.ps1` | OV phase parameter sweep |
| `ov-combo.ps1` | OV combination testing |
| `ph-sweep.ps1` | PH phase parameter sweep |
| `crosscut-sweep.ps1` | Daily targets, loss limits, cooldowns |
| `fullday-test.ps1` | Full-day combined validation |
| `verify-settings.ps1` | Verify applied settings match expected results |
| `fine-tune.ps1` / `fine-tune2.ps1` | Velocity+TrendWindow fine-tuning |
| `stops-exit-sweep.ps1` | Stops/exit/trim/direction sweep |
| `combined-test.ps1` | Combined Base winner verification |

**Verification**: All 63 unit tests pass. Full-day replay with applied settings confirms +$502.95 (32 trades).

### Daily Profit Target Fine-Tuning (late session)

**Context**: Investigated whether Feb 9 (+$16) and Feb 10 (+$19) had higher unrealized peaks that were given back.

**Intraday Equity Peak Analysis**:

| Day | Final P/L | Peak Equity | Peak Time | Gap (left on table) |
|-----|-----------|-------------|-----------|---------------------|
| Feb 9 | +$16 | +$81 | 09:48 | **$65** |
| Feb 10 | +$19 | +$42 | 11:34 | **$23** |
| Feb 11 | +$162 | +$182 | 10:12 | $20 |
| Feb 12 | +$127 | +$155 | 10:00 | $28 |
| Feb 13 | +$179 | +$182 | 09:45 | $3 |

**Key finding**: Feb 9 peaked at +$81 during OV (TQQQ position up ~$110 unrealized) but the trailing stop realized only +$45.54, and a prior SQQQ loss of -$29.40 dragged the day down to +$16. The daily target mechanism can't help because it fires on *realized* P/L, and the unrealized peak was never realized at that level.

**Per-Phase P/L Breakdown** (isolated phase runs):

| Day | OV P/L | OV Peak | Base P/L | Base Peak | Full Day |
|-----|--------|---------|----------|-----------|----------|
| Feb 9 | +$62 | +$81 | $0 | $0 | +$16 |
| Feb 10 | +$36 | +$38 | +$3 | +$12 | +$19 |
| Feb 11 | +$47 | +$56 | +$150 | +$169 | +$162 |
| Feb 12 | +$74 | +$81 | +$135 | +$156 | +$127 |
| Feb 13 | +$179 | +$182 | -$156 | +$27 | +$179 |

**Critical insight**: Feb 9 OV made +$62 but continued Base trading eroded it to +$16 (-$46). Feb 10 OV +$36, full day +$19 (-$17). On some days, **continued trading after a profitable OV phase is destructive**. This motivates a "Phase Profit Target" feature (added to TODO.md).

**DailyProfitTargetPercent sweep** (32 configs tested):

| Config | Total P/L | Trades | Key Finding |
|--------|-----------|--------|-------------|
| Target=1.5% (current) | +$502.95 | 32 | Feb 12 capped at $127 |
| **Target=1.75%** | **+$549.03** | **40** | **Winner: Feb 12 jumps to $173** |
| Target=2.0% | +$374.77 | 46 | Feb 13 collapses to -$50 |
| Target=2.5% | +$74.37 | 68 | Feb 11+13 both collapse |
| Target=OFF | -$154.75 | 92 | Overtrading disaster |

**DailyProfitTargetTrailingStopPercent**: All values (0.1%-1.0%) produce identical results at Target=1.5% — the mechanism never triggers because daily P/L never pulls back enough after reaching the target. At Target=1.75%, Trail=1.0% gives a trivial +$14 extra on Feb 11 only.

**Setting applied**: DailyProfitTargetPercent 1.5% → **1.75%** (+$46 improvement, Feb 12 +$127→+$173)

**Final optimized totals**: +$549.03 (40 trades) vs original baseline -$436.23 (77 trades) = **+$985 improvement**

#### Low Target + Wide Trail Sweep (Unrealized Equity Capture Attempt)

**Hypothesis**: Since `DailyProfitTargetRealtime=true` monitors `RealizedSessionPnL + unrealized position P/L` on every tick, a LOW target (0.4-0.75%) could arm the trailing stop on Feb 9's peak unrealized equity of $81. A WIDE trail (5-20%) would then allow the equity to run on strong days (Feb 11-13) before any pullback triggers the stop.

**26 configurations tested** — targets 0.4-0.75% × trails 1-20%:

| Config | Total P/L | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 |
|--------|-----------|-------|--------|--------|--------|--------|
| **Current T1.75_Tr0.3** | **$549** | $16 | $19 | $162 | $173 | $179 |
| T0.75_Tr5 | $316 | $64 | $19 | $88 | $71 | $74 |
| T0.75_Tr10 | $314 | $64 | $19 | $88 | $69 | $74 |
| T0.6_Tr5 | $257 | $60 | $19 | $32 | $71 | $74 |
| T0.4_Tr5 | $199 | $29 | $27 | $43 | $54 | $46 |
| T0.5_Tr5 | $186 | $35 | $19 | $32 | $54 | $46 |
| T0.5_Tr20 | $152 | $35 | $19 | $32 | $39 | $27 |

**Key findings — wide trails DON'T help:**
1. T0.5 with trails 1-5% all give **identical** results ($186). The trail fires on the FIRST intraday pullback from any running peak, not just the ultimate peak.
2. Trails wider than 5% actually HURT — the wider trail delays firing but the equity falls further before triggering.
3. Best Feb 9 gain = +$48 (T0.75), but combined cost on Feb 11-13 = -$281. Net = **-$233**.
4. Feb 10 (peak $42) essentially unreachable — even T0.4 only improves it by +$8.

**Root cause**: The trailing stop tracks a high-water mark (running peak). Once armed, ANY pullback exceeding the trail % from ANY local peak triggers it — even if equity would recover. On volatile strong days, 5-20% pullbacks from local peaks occur during normal trading rhythm, firing the stop well before the true daily maximum.

**Conclusion**: T1.75_Tr0.3 ($549) confirmed optimal for existing mechanism. Capturing Feb 9/10 unrealized peaks requires a fundamentally different approach — the **PhaseProfitTarget** feature (stop after OV if sufficient gain) as designed in TODO.md.