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
- **Phases**: Open Volatility (09:30–10:13), Base (10:13–14:00), Power Hour (14:00–16:00)
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

**As of: 2026-02-14 (Systematic Re-Optimization + OV Extension Session)**

> **NOTE**: Full phase-by-phase re-optimization using corrected replay infrastructure.
> Swept ~200+ configs across 5 dates (Feb 9-13). Result: -$436→+$503 (+$939 improvement).
> Base settings significantly changed. OV settings partially changed. PH unchanged.
> OV window extended from 09:50→10:13 (+$60 improvement, $549→$609).

### Base Config (10:13–14:00, also default for unspecified times)

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

### Open Volatility (09:30–10:13)

> **NOTE**: Window extended from 09:30–09:50 to 09:30–10:13 (+$60 improvement).
> Aggressive OV settings capture early-morning momentum that conservative Base settings miss.
> Velocity lowered to match base. Trail widened for volatile open.

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

**Final optimized totals (before OV extension)**: +$549.03 (40 trades) vs original baseline -$436.23 (77 trades) = **+$985 improvement**

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

**Conclusion**: T1.75_Tr0.3 ($549) confirmed optimal for existing daily target mechanism. Capturing Feb 9/10 unrealized peaks requires a fundamentally different approach.

### OV Window Extension Experiment

**Hypothesis**: Instead of trying to discriminate good/bad continuation days via regime signals (which showed no distinguishing features at the 09:50 transition), extend the more aggressive OV settings past 09:50 to capture early-morning momentum.

**OV→Base transition state analysis** (5 days):
- Feb 9: BULL, holding TQQQ, +$72 realized — then Base erodes to +$16
- Feb 10: NEUTRAL, CASH, +$36 — then Base erodes to +$19
- Feb 11: NEUTRAL, CASH, +$47 — then Base adds +$115 (good!)
- Feb 12: BEAR, holding SQQQ, +$49 — then Base adds +$124 (good!)
- Feb 13: NEUTRAL, CASH, +$179 — daily target already saves it

**No regime signal discriminates good from bad days**: Feb 10 (BAD) and Feb 11 (GOOD) are both NEUTRAL/CASH with nearly identical QQQ-SMA spreads.

**OV extension sweep** (12 end times tested, then fine-tuned to minute resolution):

| OV End Time | Total P/L | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 |
|-------------|-----------|-------|--------|--------|--------|--------|
| 09:50 (prev) | $549 | $16 | $19 | $162 | $173 | $179 |
| 10:05 | $566 | $14 | $19 | $188 | $165 | $179 |
| 10:07 | $567 | $14 | $48 | $175 | $151 | $179 |
| 10:10 | $589 | $14 | $48 | $175 | $172 | $179 |
| **10:13** | **$609** | $14 | **$46** | **$197** | $172 | $179 |
| **10:14** | **$609** | $14 | $46 | $197 | $172 | $179 |
| 10:15 | $600 | $14 | $46 | $197 | $164 | $179 |
| 10:16 | $599 | $14 | $37 | $197 | $171 | $179 |
| 10:20 | $597 | $14 | $37 | $197 | $170 | $179 |
| 10:30 | $581 | $14 | $24 | $197 | $167 | $179 |
| 11:00 | $533 | -$66 | $48 | $197 | $174 | $179 |
| 14:00 (all day) | $600 | $13 | $36 | $197 | $175 | $179 |

**Key findings**:
1. **10:13 is the sweet spot**: +$609 (+$60 over previous, +10.9%)
2. Feb 10 jumps $19→$46 — aggressive OV settings catch a move at ~10:07 that Base misses
3. Feb 11 jumps $162→$197 — OV-style trading captures additional early trend
4. Feb 12 drops $173→$172 (-$1) — essentially flat
5. Feb 9 drops $16→$14 (-$2) — essentially flat
6. Past 10:16, returns degrade as aggressive settings overtrade quieter conditions
7. At 11:00, Feb 9 collapses to -$66 (aggressive OV settings keep trading a reversal)

**Setting applied**: OV EndTime 09:50 → **10:13** (+$60 improvement)

**Final optimized totals**: +$608.90 (38 trades) vs original baseline -$436.23 (77 trades) = **+$1,045 improvement**

---

### Continuous Phase P/L Breakdown (Feb 14 session)

**Context**: Previous phase analysis used isolated runs per phase. This uses full-day continuous replays, analyzing which phase generated the profit from log timestamps. Cross-boundary position effects included.

| Day | OV P/L | Base P/L | PH P/L | Full Day | Notes |
|-----|--------|----------|--------|----------|-------|
| Feb 9 | +$14 | $0 | $0 | +$14.16 | All OV. Target didn't fire. |
| Feb 10 | +$36 | +$10 | $0 | +$46.24 | Target didn't fire. Base contributes small +$10. |
| Feb 11 | +$197 | $0 | $0 | +$196.96 | **Daily target fires during OV** (~09:12). Bot stops. |
| Feb 12 | +$18 | +$155 | $0 | +$172.36 | **Target fires at ~10:16**. OV holding SQQQ (-$33 unrealized), Base converts to +$155 realized. |
| Feb 13 | +$179 | $0 | $0 | +$179.18 | **Target fires during OV** (~08:45). Bot stops. |

**Key insight**: The system is fundamentally an **"OV morning strategy + daily target cap"**:
- Daily target fires during OV on 3/5 days (Feb 11, 12, 13)
- Base phase only matters on Feb 10 (+$10) and Feb 12 (+$155 from position carry)
- Power Hour contributes exactly $0 on ALL 5 days

### Daily Target ON vs OFF Test (Feb 11 & Feb 13)

**Hypothesis**: Since the daily target fires during OV on Feb 11 and Feb 13 (strong days), does removing the target let those days earn more?

| Day | Target ON | Target OFF | OV (OFF) | Base (OFF) | PH (OFF) | Delta |
|-----|-----------|------------|----------|------------|----------|-------|
| Feb 11 | **+$197** | -$133 | +$211 | **-$344** | $0 | **-$330** |
| Feb 13 | **+$179** | +$104 | +$175 | -$72 | $0 | **-$76** |

**Conclusion**: Daily target is **CRITICAL**. Without it:
- Feb 11: OV earns +$211 but Base phase destroys -$344 → net -$133 (vs +$197 with target)
- Feb 13: OV earns +$175 but Base erodes -$72 → net +$104 (vs +$179 with target)
- The target prevents Base from destroying OV gains. Non-negotiable.

### Power Hour Settings Sweep (isolated 14:00–16:00)

**Context**: PH contributes $0 on all 5 days. Tested whether different PH settings could produce any returns.

**Method**: 16 configs tested in isolated PH segment (14:00–16:00, daily target OFF).

| Config | Total P/L | Trades | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 |
|--------|-----------|--------|-------|--------|--------|--------|--------|
| CURRENT_PH | -$26 | 2 | $0 | $0 | $0 | -$26 | $0 |
| **BASE_SETTINGS_IN_PH** | **+$117** | **24** | $0 | $0 | $0 | -$4 | **+$121** |
| OV_SETTINGS_IN_PH | $0 | 0 | $0 | $0 | $0 | $0 | $0 |
| LOW_VEL_8 | -$26 | 2 | $0 | $0 | $0 | -$26 | $0 |
| TRAIL_01 | -$10 | 2 | $0 | $0 | $0 | -$10 | $0 |
| WIDER_CHOP (0.002+) | $0 | 0 | — | — | — | — | — |

**Key findings**:
1. PH is a wasteland on 4/5 days — only Feb 12 and Feb 13 see any trades
2. Only BASE_SETTINGS_IN_PH shows net positive (+$117), driven entirely by Feb 13 (+$121)
3. Current PH settings (OV-lite) are too aggressive for the quieter afternoon
4. Since daily target stops the bot before PH on most days, PH settings are academic
5. OV settings in PH = zero trades (too aggressive, nothing qualifies)

### Power Hour Resume Experiment ⭐

**Hypothesis**: Instead of stopping for the entire day when the daily target fires, pause until Power Hour (14:00) and then resume trading with **base settings** (no daily target for the PH portion). This could add "free" upside on days where the afternoon moves.

**Method**: For each day, run full-day replay with current settings (target ON). If target fired, also run an isolated PH segment (14:00–16:00) with base settings and no daily target. Combined P/L = morning session + PH session.

| Date | Morning P/L | Target? | Fire Time | PH P/L | PH Trades | Combined | Delta |
|------|-------------|---------|-----------|--------|-----------|----------|-------|
| Feb 9 | +$14.16 | no | — | — | — | $14.16 | $0 |
| Feb 10 | +$46.24 | no | — | — | — | $46.24 | $0 |
| Feb 11 | +$196.96 | YES | 09:12 | $0.00 | 2 | $196.96 | $0 |
| Feb 12 | +$172.36 | YES | 10:16 | -$4.50 | 11 | $167.86 | **-$4.50** |
| Feb 13 | +$179.18 | YES | 08:45 | **+$117.02** | 17 | **$296.20** | **+$117.02** |

**Totals**:
- **Current (stop for day)**: $608.90
- **Resume in PH**: $721.42
- **Delta**: +$112.52 (+18.5%)

**Analysis**:
1. **Feb 13 is the star**: +$117 extra from PH with base settings (17 trades, strong afternoon trend)
2. **Feb 11**: PH trades (2) but nets exactly $0 — harmless
3. **Feb 12**: PH loses -$4.50 across 11 trades — small acceptable drag
4. **Feb 9/10**: Target didn't fire, no change (PH already included in full day and produced $0)
5. **Downside risk is small** (-$4.50 worst case) vs **upside significant** (+$117 best case)
6. Net across 5 days: +$112.52 improvement

**Conclusion**: PH resume with base settings is a **promising strategy for forward-testing**. On this week's data:
- 3 days target fired → PH resume tested
- 1 day benefited significantly (Feb 13: +$117)
- 1 day small loss (Feb 12: -$4.50)
- 1 day break-even (Feb 11: $0)
- Net improvement: +$112 (+18.5%)

**Caveats**: Only tested on 5 days. Feb 13's +$121 PH performance may be anomalous. Need broader date coverage to validate. The feature would require code changes to implement (currently no "pause then resume" mechanism in TraderEngine).

**Implementation note**: This would require a new feature in the bot — "PH Resume Mode":
- When daily target fires, go to CASH and stop trading
- At 14:00, reset the daily target state and resume with base settings
- Optionally: apply a separate PH-specific profit target
- Config: `ResumeInPowerHour: true/false`

**Script**: `ph-resume-test.ps1` — uses `-config` file with base settings in PH overrides, DailyProfitTargetPercent=0

---

### Session Summary — 2026-02-14 (Phase Analysis & PH Resume)

**Date**: February 14, 2026
**Branch**: `experiment/regime-analysis` (at `eda7e1a` pre-session)

**Context**: Following yesterday's OV extension optimization ($549→$609), investigated the profit distribution across phases and whether non-OV phases contribute anything.

**Experiments conducted**:
1. ✅ Continuous phase P/L breakdown — revealed system is "OV + daily target"
2. ✅ Daily target ON vs OFF on strong OV days — target is CRITICAL (-$330/-$76 without)
3. ✅ PH settings sweep (16 configs) — PH is dead with current settings, base-in-PH +$117
4. ✅ **PH Resume experiment** — stop on target, resume in PH: **+$112 improvement** ($609→$721)

**Key conclusions**:
- The system works: aggressive OV morning trading + daily profit target cap
- Base phase matters only for Feb 12 position carry (+$155) and Feb 10 (+$10)
- Power Hour is dead weight under current strategy (daily target fires before PH)
- **Resuming in PH with base settings adds +$112/+18.5%** — promising for forward-testing
- **No settings changes applied** this session — research only
- Total verified: $608.90 (current) or $721.42 (with hypothetical PH resume)

**Scripts created**:
| Script | Purpose |
|--------|---------|
| `phase-continuous2.ps1` | Continuous full-day replay with per-phase P/L |
| `target-off-test.ps1` | Feb 11/13 with daily target ON vs OFF |
| `ph-sweep2.ps1` | 16-config PH settings sweep (isolated 14:00-16:00) |
| `ph-resume-test.ps1` | PH resume experiment (morning target + PH restart) |

---

## Session: 2026-02-15 — ETF Price Response Lag Investigation

**Context**: Investigated the TODO item "Evaluate whether trailing stops should be set on ETF prices rather than benchmark." The concern was that leveraged ETFs (TQQQ/SQQQ) might lag QQQ directional moves, causing benchmark-based trailing stops to fire at times when ETF P/L doesn't match expectations.

**Important clarification**: This is NOT tick-timing alignment — it's **price response lag**. When QQQ makes a directional move, how long until TQQQ/SQQQ fully reflect the expected 3x response? This is impulse-response analysis.

### Architecture Review

Before measuring, reviewed the code to understand the domain boundaries:
- **Signal generation**: QQQ only (`AnalystEngine.ProcessTick` at L349)
- **Trailing stop trigger & distance**: QQQ benchmark (`TraderEngine.EvaluateTrailingStop` at L1694)
- **DynamicStop tier triggers**: ETF profit % (mixed domain)
- **P/L calculation**: ETF price
- **`_etfHighWaterMark`**: Exists at L1753 to partially bridge the domain mismatch

### Analysis Tool: `etf_lag_analysis.py`

Created Python script (`qqqBot/etf_lag_analysis.py`) with three analyses run across all 5 replay dates (Feb 9-13):

**Part 1 — Rolling Leverage Ratio** (QQQ % move vs ETF % move over same time window):
Sample ~500 QQQ tick pairs per window size, compute ETF/QQQ return ratio.

| Window | TQQQ/QQQ Median (range across days) | MAE from 3.0 |
|--------|--------------------------------------|--------------|
| 5s | 1.0–2.1 | 2.4–2.6 |
| 10s | 2.0–2.5 | 2.1–2.3 |
| 30s | 2.6–2.8 | 1.6–1.9 |
| 60s | 2.86–3.02 | 1.4–1.6 |

**Insight**: 3x leverage only holds reliably at 60s+. Sub-10s moves have enormous variance.

**Part 2 — Price Response Lag** (impulse-response):
Detect QQQ moves >0.03% over 10s, then measure how much TQQQ has achieved at move completion:

| Date | Moves | TQQQ Achieved% (median) | p10 | p90 | Immediate 90%+ | Within 5s | Never/30s |
|------|-------|-------------------------|-----|-----|-----------------|-----------|-----------|
| Feb 9 | 81 | 94.4% | 51.5% | 119.3% | 43/81 | 57/81 | 17/81 |
| Feb 10 | 111 | 90.3% | 38.5% | 123.4% | 57/111 | 84/111 | 19/111 |
| Feb 11 | 156 | 85.1% | 20.0% | 129.4% | 72/156 | 100/156 | 34/156 |
| Feb 12 | 201 | 88.9% | 30.8% | 131.8% | 95/201 | 143/201 | 33/201 |
| Feb 13 | 269 | 86.5% | 17.0% | 127.8% | 121/269 | 176/269 | 63/269 |

**Insight**: Real lag exists on fast moves — ETFs need seconds to catch up. ~15-22% of moves never achieve 90% of expected 3x within 30s at the tick level.

**Part 3 — Stop-Trigger Scenario** (THE KEY FINDING):
When QQQ drops 0.2% from HWM, what is TQQQ_drop / QQQ_drop ratio?

| Date | n(0.2%) | Mean | Median | Min | Max | n(0.5%) | Mean | Median |
|------|---------|------|--------|-----|-----|---------|------|--------|
| Feb 9 | 7 | 2.950 | 2.889 | 2.744 | 3.154 | 0 | — | — |
| Feb 10 | 13 | 2.933 | 2.973 | 2.570 | 3.132 | 3 | 2.966 | 2.953 |
| Feb 11 | 24 | 2.929 | 3.001 | 2.428 | 3.287 | 4 | 3.007 | 3.032 |
| Feb 12 | 30 | 2.992 | 3.011 | 2.543 | 3.378 | 6 | 3.047 | 3.061 |
| Feb 13 | 29 | 2.913 | 2.928 | 2.266 | 3.451 | 5 | 3.007 | 2.960 |

### Conclusions

1. **At the 0.2% trailing stop level**: TQQQ/QQQ drop ratio is 2.9–3.0 median — very close to ideal 3.0
2. **At the 0.5% level**: Even tighter (2.95–3.06)
3. **By the time a stop fires (sustained 0.2-0.5% drop), ETFs have already caught up** — the tick-level lag is irrelevant
4. **Switching to ETF-based stops would make negligible difference for QQQ/TQQQ/SQQQ**
5. The mixed domain architecture (QQQ stops + ETF tier triggers) works correctly in practice
6. **This does NOT extend to less-liquid pairs** — added separate TODO for RKLB/RKLX/RKLZ

### Decision: No Code Changes

The current benchmark-based trailing stop approach is validated for QQQ/TQQQ/SQQQ. The TODO is marked complete. Trailing stop TODO updated with full empirical data.

### Files Created/Modified
| File | Action |
|------|--------|
| `qqqBot/etf_lag_analysis.py` | Created — Python analysis script |
| `TODO.md` | Trailing stop TODO marked complete with findings; RKLB TODO added |
| `EXPERIMENTS.md` | This session entry |

---

## Session: 2026-02-15 — MACD Momentum Layer Evaluation (Failed)

**Context**: Barcoding remains the bot's main weakness (Feb 13: 35 trades, -$628). Current defenses (velocity gate, chop bands, entry confirmation) are indirect. Investigated MACD as a complementary momentum indicator for barcoding detection and trend confirmation.

**Full implementation and test results**: See branch `feature/macd_addition_and_tests` (both qqqBot and MarketBlocks repos) for complete MACD code, sweep scripts, and detailed CSV results.

### Implementation Summary

MACD fed raw benchmark price (not SMA) with three independently-toggleable roles:
1. **Trend Confidence Boost** — MACD histogram confirms direction → rescue entries that fail velocity gate
2. **Exit Accelerator** — histogram flips against position → shorten neutral timeout for faster exits
3. **Entry Gate** — histogram near zero (flatline) → block new entries (direct barcoding defense)

Disabled by default (`Macd.Enabled = false`). Per-phase overridable. Alternate config `appsettings.macd.json`. CLI flag `--macd`.

Changes spanned both repos: `IncrementalEma.cs`, `MacdCalculator.cs`, `MacdConfig` class, `AnalystEngine`, `TraderEngine`, `TimeRuleApplier`, plus qqqBot wiring (`TradingSettings`, `ProgramRefactored`, `CommandLineOverrides`). All tests passed (554 MarketBlocks + 63 qqqBot). Zero behavioral change when disabled.

### Sweep Results Summary (~215 configs, ~350+ replay runs)

**Baseline**: No-MACD = **+$608.90** (38 trades, 5 days Feb 9-13). This is the target.

| Sweep Group | Configs | Result |
|-------------|---------|--------|
| Role isolation (7) | Full (5d) | Gate completely inert ($0 delta). Boost harmful (-$161, +15 trades). Accel mildly harmful (-$43). |
| Dead zone — Gate-ONLY (6) | Base (5d) | Zero trades blocked at any dead zone level |
| Dead zone — Boost+Gate (6) | Base (5d) | Best DZ=0.12 still -$66 vs no-MACD |
| Boost threshold (7) | Full (5d) | Best at 0.12, still -$100 vs no-MACD |
| Accelerator wait (8) | Full (5d) | Best at 90s = -$10.63 vs no-MACD (closest approach) |
| Gate-handoff Vel×DZ (42) | Base (2d) | Gate inert at all velocity × dead zone combos |
| Period grid F×S×Sig (52) | Full (2d) | ALL 52 identical to no-MACD |
| Threshold combo DZ×BT×AW (48) | Full (2d) | ALL 48 identical to no-MACD |
| Rebase Vel/TW/SMA + MACD (27) | Base (2d) | No-MACD #1; all MACD configs worse |
| Phase-tune OV (12) | OV (2d) | ALL 12 identical to no-MACD |

### Root Cause Analysis

The three boolean roles compete over the same `trendRescue`/`isStalled` flags:
- **Gate** checks absolute histogram value, but histogram scale varies wildly by market regime — the values at entry decision time never fall within blocking range
- **Boost** rescues entries that velocity correctly blocked, adding bad trades
- **Accel** shortens timeouts marginally but always generates 2 extra trades vs baseline
- The velocity filter at 0.000015 is already a more effective barcoding prevention mechanism than any MACD configuration

### Conclusion

**MACD disabled. No configuration tested matches the no-MACD baseline.** This specific boolean-role implementation is architecturally flawed — binary overrides competing over the same flags cannot provide the nuanced momentum assessment needed.

**This does NOT rule out MACD entirely.** A fundamentally different approach (e.g., weighted momentum scoring with normalized histogram, histogram slope, and range compression for barcoding detection) could potentially succeed. See the `feature/macd_addition_and_tests` branch for the full implementation, `macd-sweep.ps1` harness, and detailed CSV results that informed this conclusion.

**No settings changes applied.**

---

## Session: 2026-02-16 — OpEx Friday PH Investigation

**Context**: The PH Resume experiment (2026-02-14) showed Feb 13 (Friday) had outstanding PH performance (+$117 from 17 trades with Base settings). Hypothesis: weekly OpEx pinning on Fridays creates exploitable PH trends due to options gamma exposure driving directional moves into close.

**Method**: Compare PH behavior on two Fridays with different data resolutions:
- **Feb 6** (Friday): Low-resolution 60s bar data (390 bars), uses Brownian bridge interpolation (~1 tick/sec, seed = `_replayDate.DayNumber ^ StableHash(symbol)`)
- **Feb 13** (Friday): High-resolution recorded tick data (11,014 ticks), replays raw

Three tests per date:
- **(A)** Isolated PH (14:00–16:00) with current PH settings (OV-lite: SMA=120, Trail=0.15%, TrendWait=60, ChopThreshold=0.0015)
- **(B)** Isolated PH (14:00–16:00) with Base settings (SMA=180, Trail=0.2%, TrendWait=180, TrendWindow=5400)
- **(C)** Full PH-resume strategy: morning w/target → if fired, resume PH w/Base settings, no target

Plus a determinism check: re-run Feb 6 PH Base to confirm Brownian bridge produces identical results.

**Branch**: `experiment/opex-friday-ph`
**Script**: `opex-ph-test.ps1`

### Results

**Test A — Isolated PH with CURRENT PH settings (OV-lite):**

| Date | Resolution | P/L | Trades | Peak | Trough |
|------|-----------|------|--------|------|--------|
| Feb 6 | LOW-RES | $0.00 | 0 | N/A | N/A |
| Feb 13 | HIGH-RES | $0.00 | 0 | N/A | N/A |

**Test B — Isolated PH with BASE settings:**

| Date | Resolution | P/L | Trades | Peak | Trough |
|------|-----------|------|--------|------|--------|
| Feb 6 | LOW-RES | $0.00 | 0 | N/A | N/A |
| Feb 13 | HIGH-RES | **+$117.02** | 17 | N/A | — |

**Test C — PH-Resume strategy (morning w/target → resume PH w/Base, no target):**

| Date | Resolution | Full-Day P/L | FD Trades | Target? | PH P/L | PH Trades | Combined | Delta |
|------|-----------|-------------|-----------|---------|--------|-----------|----------|-------|
| Feb 6 | LOW-RES | -$46.24 | 4 | no | — | — | -$46.24 | $0 |
| Feb 13 | HIGH-RES | +$179.18 | 6 | YES (09:45) | **+$117.02** | 17 | **$296.20** | +$117.02 |

**Determinism check**: PASSED — Feb 6 PH Base returned $0.00 on both runs (trivially deterministic since 0 trades).

### PH Price Action Comparison

| | Feb 6 (LOW-RES) | Feb 13 (HIGH-RES) |
|---|---|---|
| Data points in PH | 120 bars (→ ~7,200 bridge ticks) | 3,642 raw ticks |
| PH open price | $607.55 | $605.21 |
| PH close price | $609.59 | $601.86 |
| PH high | $611.30 | $605.80 |
| PH low | $607.55 | $600.14 |
| Net PH move | +$2.04 (+0.34%) | -$3.35 (-0.55%) |
| PH range | $3.75 (0.617%) | $5.66 (0.943%) |
| Price character | Meandering up, no clear trend | Sustained downtrend |
| Regime detected | NEUTRAL throughout | Trending (17 trades) |

### Analysis

1. **Hypothesis WEAKENED**: Feb 6 (Friday, weekly OpEx) showed zero PH trending behavior. The market was choppy/directionless during PH with no trades under any settings configuration. Weekly OpEx alone does NOT guarantee PH trends.

2. **Current PH settings (OV-lite) are inert**: Zero trades on BOTH Fridays, including Feb 13 which clearly trended. The ChopThreshold=0.0015 and/or SMA=120 combination blocks all entries during PH. This confirms the Feb 14 finding that PH settings should be Base, not OV-lite.

3. **Feb 6 vs Feb 13 difference is market-driven, not data-driven**:
   - Feb 6 PH had only 0.617% range with no directional bias — genuinely choppy
   - Feb 13 PH had 0.943% range with a sustained $5.66 downtrend — clearly trending
   - The Brownian bridge processed the full 120 PH bars correctly (verified in logs: data ran 14:00–15:59 ET)
   - Feb 6 was a losing day overall (-$46.24, 4 trades) — weak market conditions

4. **Brownian bridge assessment**: Adequate for this test. Deterministic, processed all PH data, and the 0-trade result aligns with the genuinely choppy price action — not a bridge artifact. However, the determinism check was trivial (both runs had 0 trades). A proper bridge stress test would require a date where bridge data actually produces trades.

5. **Calendar context**:
   - Feb 6: Regular Friday (2 weeks before monthly OpEx)
   - Feb 13: Friday before monthly OpEx week (Feb 20 is 3rd-Friday OpEx)
   - Both have weekly options expiration, but only Feb 13 showed PH trends
   - Feb 13's PH behavior may relate to monthly OpEx proximity rather than weekly

6. **Replay log timezone note**: The replay logger timestamps show a ~1 hour offset from the bracket display times (e.g., log says `14:00:02`, bracket shows `[15:00:00]`). This appears to be an EST vs EDT conversion issue in the logging pipeline. P/L and trade counts are unaffected; the target fire time displays as `08:45:56` in the log but was actually ~09:45 ET (during OV phase).

### Conclusions

- **Weekly OpEx pinning is NOT sufficient** to explain Feb 13's PH trends (Feb 6 is a counter-example)
- **Monthly OpEx proximity** remains a plausible factor — need Feb 20 (actual OpEx Friday) data to test
- **Need more Friday data points**:, especially Feb 20 (actual monthly OpEx) and future Fridays
- **Current PH settings confirmed dead** — OV-lite produces 0 trades even when the market trends
- **PH Resume strategy confirmed** — still +$117 on Feb 13, $0 impact on Feb 6 (acceptable)
- **No settings changes applied** — research only

### Next Steps

1. **Collect Feb 20 data** (actual 3rd-Friday monthly OpEx) — this is the critical test date
2. **Collect more Friday data** over coming weeks to build a larger sample
3. **Revisit PH settings**: Consider permanently switching PH TimeRule to Base settings (OV-lite is provably inert)
4. **Implement PH Resume Mode** in TraderEngine (currently only testable via two-replay-run script)
5. If Feb 20 shows PH trending: strengthen monthly-OpEx hypothesis; if not: likely just market conditions

**Files created/modified**:

| File | Change |
|------|--------|
| `opex-ph-test.ps1` | New script — OpEx Friday PH comparison (Feb 6 vs Feb 13) |
| `EXPERIMENTS.md` | This session entry |
| `TODO.md` | Added PH Resume Mode implementation TODO |

---

## Session: 2026-02-17 — PH Resume Mode Implementation

### Context

Implemented PH Resume Mode in TraderEngine based on 5-day experiment data showing +$112.52 (+18.5%) improvement. When the daily profit target fires before Power Hour, the engine now arms a resume flag, goes flat, and resumes trading at 14:00 ET with Base settings (daily target disabled for PH session).

### Changes Made

**MarketBlocks (feature/ph-resume-mode branch off master)**:

| File | Change |
|------|--------|
| `MarketBlocks.Bots/Domain/HaltReason.cs` | **NEW** — `enum HaltReason { None, ProfitTarget, LossLimit }` |
| `MarketBlocks.Bots/Domain/TradingState.cs` | Added `HaltReason` and `PhResumeArmed` persisted fields |
| `MarketBlocks.Bots/Domain/TradingSettings.cs` | Added `ResumeInPowerHour` (bool, default false) |
| `MarketBlocks.Bots/Services/TraderEngine.cs` | **Major refactor**: Replaced all 13 `_dailyTargetReached` (volatile bool) references with `_state.HaltReason` (persisted enum). Added `SetHaltReason()` helper (centralizes halt + PH Resume arming + state persistence). Added `CheckPhResume()` method (clears halt, resets daily target, disables DailyProfitTargetPercent for PH). Wired into TimeRuleApplier phase transition detection. Added `_previousPhaseName` for phase tracking. |
| `MarketBlocks.Bots.Tests/TraderEngine_PhResumeTests.cs` | **NEW** — 7 tests: resume on PH transition, loss limit no resume, feature disabled stays halted, target during PH no resume, normal PH trading unaffected, state persistence verified, daily target disabled after resume |

**qqqBot (feature/ph-resume-mode branch off main)**:

| File | Change |
|------|--------|
| `qqqBot/TradingSettings.cs` | Added `ResumeInPowerHour` property (sync with MarketBlocks) |
| `qqqBot/ProgramRefactored.cs` | Wired `ResumeInPowerHour` in `BuildTradingSettings` via `configuration.GetValue()` |
| `qqqBot/appsettings.json` | Added `ResumeInPowerHour: false` setting. **Switched PH TimeRule overrides from OV-lite to empty** (PH now uses Base settings — OV-lite was provably inert, producing 0 trades even in trending markets). |

### Design Decisions

1. **HaltReason enum** replaces volatile `_dailyTargetReached` bool — fixes crash-safety gap (halt state was not persisted before) and enables distinguishing profit target vs loss limit halts.
2. **Combined PH settings approach** — PH TimeRule overrides emptied so PH inherits Base settings. Simpler than maintaining separate PH-tuned settings when Base is already optimized.
3. **Phase profit target deferred** — added as separate TODO item. Current implementation uses session-wide daily target with PH exemption (target disabled during PH session).
4. **`SetHaltReason()` centralizes all halt transitions** — ensures consistent state persistence (`forceImmediate: true`) and PH Resume arming logic in one place.
5. **`CheckPhResume()` runs on phase transitions only** — triggered by TimeRuleApplier phase change detection, not on every tick.

### Test Results

- MarketBlocks: 145 tests passed (7 new PH Resume + 138 existing), 0 failures
- qqqBot: 63 tests passed, 0 failures
- **Replay validation pending** — run with `ResumeInPowerHour=true` before going live

### Known Limitations

- Daily target is fully disabled during PH session (no re-arming). A future "phase profit target" could add PH-specific limits.
- `DailyProfitTargetPercent` is mutated in-place for PH (set to 0). This is the same pattern TimeRuleApplier uses and is restored on day reset.
- Feature defaults to `false` — must be explicitly enabled in appsettings.json.

### Next Steps

1. Run 5-day replay with `ResumeInPowerHour=true` and compare against prior $608.90 baseline and $721.42 script-stitched result
2. Verify determinism (run 2-3 times, confirm identical P/L)
3. If results match expectations, enable for live trading
4. Collect Feb 20 data (monthly OpEx Friday) for further PH analysis

---

## Session: 2026-02-18 — PH Resume Validation + Analyst Phase Reset Experiment

### Context

Validated PH Resume Mode native replay (+$421.45) against script-stitched result (+$721.42). Investigated the $300 gap and discovered the entire divergence was caused by **analyst freshness**: the script started a cold analyst at 14:00 (empty SMA/slope/trend buffers), while native mode carries 4.5 hours of indicator history forward into PH. Both used identical Base stops (0.2%).

Implemented `AnalystPhaseResetMode` with three modes (None/Cold/Partial) to test whether resetting analyst indicators at PH entry improves results. Ran a 5-config replay matrix.

### Diagnosis: Script-Stitched $721.42 Advantage

The two-replay-run script (`fullday-test.ps1`) spawned a completely new process at 14:00. This meant:
- **SMA buffers**: Empty (need SMAWindowSeconds ticks to warm up)
- **Slope calculator**: Empty (needs SlopeWindowSize ticks after SMA fills)
- **Trend SMA**: Empty (needs TrendWindowSeconds=5400s = 90 mins from cold — never fills in 2-hour PH!)
- **Sliding bands**: Reset to initial state

A "cold" analyst generates NEUTRAL for the first ~3-6 minutes (warmup), then makes decisions based only on PH price action — no carry-over from earlier choppy conditions. This is a **structural advantage** that makes the script result unrealistically optimistic (the missing trend SMA means trend-wait is effectively disabled).

### Changes Made

**MarketBlocks (feature/ph-resume-mode branch)**:

| File | Change |
|------|--------|
| `MarketBlocks.Bots/Domain/AnalystPhaseResetMode.cs` | **NEW** — `enum AnalystPhaseResetMode { None = 0, Cold = 1, Partial = 2 }` |
| `MarketBlocks.Bots/Domain/TradingSettings.cs` | Added `AnalystPhaseResetMode` (enum, default None) and `AnalystPhaseResetSeconds` (int, default 120) |
| `MarketBlocks.Bots/Services/AnalystEngine.cs` | **Major additions**: `ColdResetIndicators()` (creates fresh empty calculators), `PartialResetIndicators()` (keeps last N seconds of data), `SeedFromTail()` helpers for IncrementalSma and StreamingSlope. Phase transition handler in ProcessTick fires reset **only at PH entry** (`currentPhase == "Power Hour"` guard). |
| `MarketBlocks.Bots.csproj` | Added `InternalsVisibleTo` for test assembly access |
| `MarketBlocks.Bots.Tests/AnalystEngine_PhaseResetTests.cs` | **NEW** — 9 tests: ColdReset clears state, ColdReset correct capacities, PartialReset retains last N, PartialReset with excess keeps all, PartialReset resets signal, ModeNone preserves state, PartialReset truncates trend, enum parsing (6 inline), invalid input throws |

**qqqBot (feature/ph-resume-mode branch)**:

| File | Change |
|------|--------|
| `qqqBot/TradingSettings.cs` | Added `AnalystPhaseResetMode` (string) and `AnalystPhaseResetSeconds` (int) |
| `qqqBot/ProgramRefactored.cs` | Wired `Enum.Parse<AnalystPhaseResetMode>` and `AnalystPhaseResetSeconds` in BuildTradingSettings |
| `qqqBot/appsettings.json` | Added `AnalystPhaseResetMode` and `AnalystPhaseResetSeconds` settings (defaults: "None", 120) |

### Bug Found & Fixed

**Reset firing on ALL phase transitions**: Initial implementation triggered Cold/Partial reset on every phase change (OV→Base, Base→PH). This destroyed 43 minutes of indicator history at OV→Base transition on Feb 9/10, ruining results for days that don't even use PH Resume. Fixed by adding `currentPhase == "Power Hour"` guard — reset only fires on Base→PH transition.

### 5-Config Replay Matrix (Feb 9-13, 2026)

| Config | Reset Mode | PH Stops | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | **Total** |
|--------|-----------|----------|-------|--------|--------|--------|--------|-----------|
| A | None | Base 0.2% | $14.16/4t | $46.24/7t | $196.96/6t | -$4.71/27t | $166.80/15t | **+$421.45** |
| B | None | Wider 0.35% | $14.16/4t | $46.24/7t | $196.96/6t | $28.37/23t | $197.82/13t | **+$483.55** |
| C | Cold | Base 0.2% | $14.16/4t | $46.24/7t | $188.20/8t | $90.00/23t | $143.14/19t | **+$481.74** |
| D | Partial 120s | Base 0.2% | $14.16/4t | $46.24/7t | $188.20/8t | $12.91/25t | $143.14/19t | **+$404.65** |
| E | Cold | Wider 0.35% | $14.16/4t | $46.24/7t | $188.20/8t | $73.29/21t | $174.16/17t | **+$496.05** |

Notes:
- Feb 9/10: No PH Resume trades (target not hit before 14:00), so all configs match on these days ✓
- Feb 11: Cold reset loses $8.76 vs carry-forward (both modes). Trend SMA carries useful history that cold loses.
- Feb 12: Cold reset is the biggest winner (+$90 vs -$4.71 baseline). Wider stops also help ($28.37). But combination (E: $73.29) is worse than cold alone (C: $90.00) — wider stops let losing PH trades run longer.
- Feb 13: Wider stops dominate ($197.82 vs $166.80). Cold reset hurts (-$23.66). Combination partially recovers.
- **Partial reset (D) is the clear loser** at +$404.65, even worse than baseline. 120s of stale history is worse than either fresh start or full history.

### Analysis & Conclusions

1. **Wider PH stops (+$62 over baseline)**: Consistently beneficial. Prevents premature stop-outs in PH's wider swings. Simple appsettings change, no code complexity.

2. **Cold analyst reset (+$60 over baseline)**: Mixed. Huge win on choppy-to-PH days (Feb 12: +$95), loss on trending days (Feb 11: -$9, Feb 13: -$24). The missing Trend SMA (90-min warmup can't fill in 2-hour PH) effectively disables trend-wait, which cuts both ways.

3. **Partial reset (REJECTED)**: Worst overall. 120s of stale data is uniquely bad — too little for meaningful indicators, but enough to carry forward stale bias. Delete this mode.

4. **Combined Cold+Wider (Config E, +$496.05)**: Best total, but not by a lot over simpler Config B (+$483.55). The two mechanisms don't synergize well — they partially offset each other's benefits (Fig 12: cold alone $90 vs cold+wider $73).

5. **None of the configs approach $721.42** — confirms the script result was unrealistic (missing trend SMA = permanently disabled trend-wait filter).

### Recommendation

**Config B (None + Wider 0.35% PH stops)** is the safest choice:
- +$483.55 total, only $12.50 behind Config E
- Zero code complexity (appsettings-only change)
- Consistent improvement across all PH-active days
- Cold reset adds code complexity with inconsistent payoff

However, if more data shows choppy days are common, Cold reset could be valuable. **Keep the Cold reset code but default to None** — revisit after collecting more replay data (especially Feb 20 OpEx Friday).

### Settings Restored

appsettings.json restored to safe defaults: `ResumeInPowerHour=false`, `AnalystPhaseResetMode="None"`, PH overrides empty.

### Test Results

- MarketBlocks: 549 passed (9 new analyst reset tests + 7 PH Resume + existing), 1 skipped (known flaky), 0 failures
- qqqBot: 63 passed, 0 failures

### Next Steps

1. **Decide which config to ship** — likely B (wider PH stops only) or E (cold + wider) based on user preference
2. Apply chosen config to appsettings.json
3. Remove Partial reset mode if not keeping it (dead code)
4. Commit both repos on feature/ph-resume-mode branches
5. Collect more replay data (especially Feb 20) to validate with larger sample
6. Consider whether wider PH stops should use the same Base DynamicStopLossTiers or custom PH tiers

### Post-Matrix Reassessment

**Critical realization**: The $608.90 baseline (WITHOUT PH Resume) outperforms every PH Resume config:

| Config | PH Resume? | Total | vs $608.90 |
|--------|-----------|-------|------------|
| No PH Resume (baseline) | No | **$608.90** | — |
| E (Cold + Wider, best) | Yes | $496.05 | **-$112.85** |
| B (None + Wider) | Yes | $483.55 | -$125.35 |
| C (Cold + Base) | Yes | $481.74 | -$127.16 |
| A (None + Base) | Yes | $421.45 | -$187.45 |
| D (Partial + Base) | Yes | $404.65 | -$204.25 |

**Root cause**: The daily profit target stopping trading for the day is *protecting* morning gains from afternoon chop/reversals. PH Resume gives back profits on every day it activates:
- Feb 11: $197→$188 (-$9)
- Feb 12: $172→$73 (-$99, catastrophic — morning gains eroded by PH whipsaw)
- Feb 13: $179→$174 (-$5)

The original $721.42 script result that motivated PH Resume was inflated by a cold analyst artifact (disabled trend-wait filter). The real signal: **trend/momentum strategies lose money in afternoon chop**.

**Decision**: Keep PH Resume feature dormant (`ResumeInPowerHour=false`). All code stays but defaults to off.

**Regression verified**: 5-day replay with dormant feature = **$608.90** (exact match, zero regressions).

**Insight for future work**: PH trading needs an entirely different strategy — not settings tweaks on a trend/momentum bot. Choppy sessions require a fundamentally different approach (mean-reversion, range-bound strategies, or simply sitting out).

---

### Session: 2026-02-16 — Mean Reversion Strategy Implementation & Testing

**Context**: Following the Session 15 insight that PH needs a fundamentally different strategy, we implemented a complete Mean Reversion (MR) infrastructure and ran systematic replay tests.

#### Code Changes (Phase 0)

**New file**: `MarketBlocks.Bots/Domain/StrategyMode.cs` — enum `Trend=0, MeanReversion=1`

**Modified files** (7 files across both repos):
- `MarketBlocks.Bots/Domain/TradingSettings.cs` — 13 new MR settings: `BaseDefaultStrategy`, `PhDefaultStrategy`, `ChopOverrideEnabled`, `ChopUpperThreshold` (61.8), `ChopLowerThreshold` (38.2), `BollingerWindow` (20), `BollingerMultiplier` (2.0), `ChopPeriod` (14), `ChopCandleSeconds` (60), `MeanRevStopPercent` (0.3%), `MrEntryLowPctB` (0.2), `MrEntryHighPctB` (0.8), `MrExitPctB` (0.5)
- `MarketBlocks.Bots/Domain/TimeBasedRule.cs` — All 13 as nullable overrides in `TradingSettingsOverrides`
- `MarketBlocks.Bots/Domain/MarketRegime.cs` — Extended with `ActiveStrategy`, `PercentB`, `BollingerUpper/Middle/Lower`, `ChopIndex`
- `MarketBlocks.Bots/Services/AnalystEngine.cs` — BB/CHOP indicators, `DetermineStrategyMode()`, `DetermineMeanReversionSignal()`, `FeedChopCandle()` (candle aggregation for both CHOP and BB)
- `MarketBlocks.Bots/Services/TraderEngine.cs` — MR_LONG/MR_SHORT/MR_FLAT signal dispatch, MR hard stop
- `MarketBlocks.Bots/Services/TimeRuleApplier.cs` — All 13 settings wired through Snapshot, Restore, Apply, Log, IndicatorSettingsChanged, SettingsSnapshot
- `MarketBlocks.Trade/Core/Math/StreamingBollingerBands.cs` — Added public `Multiplier` property

**qqqBot wiring**: `TradingSettings.cs` (13 mirrored), `ProgramRefactored.cs` (BuildTradingSettings + ParseOverrides)

**Bug fix during testing**: BB was originally fed every raw tick (`_bollingerBands.Add(tick.Price)`), making BB(20) = 20 ticks ≈ 20 seconds lookback. Bands were impossibly tight, causing 460 trades and -$5K loss in 2 hours. Fixed to feed BB on candle closes (same as CHOP), so BB(20) = 20 candle closes = 20 minutes lookback.

**Build**: 0 errors both solutions. **Tests**: 325 pass (42+159+61+63). **Regression**: $608.90 exact match (MR dormant by default).

#### Phase 1 Results — PH Segment Sweep (14:00-16:00 only, cold start)

BB on candle closes. All configs deeply negative due to cold-start (BB needs 20 minutes to warm up):

| Config | Entry Low/High | Stop | Total P/L | Trades | Notes |
|--------|---------------|------|-----------|--------|-------|
| A | 0.2 / 0.8 | 0.3% | -$2,550.84 | 161 | Default thresholds |
| B | 0.1 / 0.9 | 0.3% | -$2,200.20 | 132 | Tighter entry reduces trades |
| C | 0.2 / 0.8 | 0.5% | -$1,707.22 | 100 | Wider stop reduces trades more |

Per-day detail (Config A): Feb 9=-$395, Feb 10=-$400, Feb 11=-$323, Feb 12=-$736, Feb 13=-$696

**Conclusion**: Cold-start segment replays are not viable for BB-based MR. BB needs pre-warming.

#### Phase 2 Results — CHOP Override (PH segment, cold start)

| Config | CHOP | Total P/L | Trades | Notes |
|--------|------|-----------|--------|-------|
| D | Off | -$2,563.88 | 161 | Control (≈ Config A) |
| E | On (61.8/38.2) | -$3,069.17 | 186 | **Worse** — cold CHOP can't filter effectively |

**Conclusion**: CHOP on cold start adds noise, doesn't help.

#### Phase 3 Results — Full-Day Replays (BB/CHOP pre-warmed from open)

Baseline: **$608.90** (38 trades)

| Config | CHOP | Total P/L | Trades | Delta vs Baseline | Notes |
|--------|------|-----------|--------|-------------------|-------|
| F | Off | -$377.49 | 102 | **-$986.39** | MR PH only |
| G | On | -$1,623.93 | 186 | **-$2,232.83** | CHOP overrides Base phase too |

Per-day detail (Config F vs Baseline):
| Date | Baseline | Config F | Delta | Notes |
|------|----------|----------|-------|-------|
| Feb 9 | +$14.16 (4t) | -$530.39 (36t) | -$544.55 | MR fires in PH, loses big |
| Feb 10 | +$46.24 (7t) | -$395.60 (39t) | -$441.84 | MR fires in PH, loses big |
| Feb 11 | +$196.96 (6t) | +$196.96 (6t) | $0.00 | Daily target hit before PH |
| Feb 12 | +$172.36 (15t) | +$172.36 (15t) | $0.00 | Daily target hit before PH |
| Feb 13 | +$179.18 (6t) | +$179.18 (6t) | $0.00 | Daily target hit before PH |

**Key findings**:
1. **Daily profit target masks MR**: On 3/5 days, the daily target fires before PH. MR only activates on the 2 weakest days.
2. **MR on trending PH = catastrophe**: Feb 9/10 PH was trending. MR fought the trend and lost ~$500/day.
3. **CHOP override is phase-unaware**: When enabled, CHOP overrides during ALL phases (including Base), destroying good Base performance. Design flaw — CHOP should only override within the designated phase.
4. **BB warm-up is critical**: Cold-start segment replays are invalid for BB-based strategies. BB(20) on 60s candles needs 20 minutes to become ready.

#### Failed Experiments
- Raw tick BB feeding (460 trades, -$5K in 2h)
- PH segment replays with cold indicators
- CHOP global override (destroys Base phase performance)

#### Decisions
- **MR infrastructure code is preserved** — dormant by default (`BaseDefaultStrategy=Trend`, `PhDefaultStrategy=Trend`, `ChopOverrideEnabled=false`)
- **All sweep configs preserved** in `qqqBot/sweep_configs/` for future reference
- **Regression verified**: $608.90 exact match with dormant MR

#### Future Work Ideas
1. **CHOP phase-gating**: Make CHOP override phase-aware (only override during PH, not Base/OV)
2. **Longer BB window**: BB(50) or BB(100) on 60s candles for broader bands that reduce whipsawing
3. **Market regime pre-filter**: Only enable MR on days where early session CHOP indicates choppy character
4. **MR + trend confirmation**: Require both BB entry signal AND trend slope alignment (e.g., MR_LONG only when short-term slope is also turning up)
5. **Test on known choppy days**: The 5-day sample (Feb 9-13) may have been trending. Need to specifically find/record choppy days for better MR testing

---

### Session: MR Root Cause Investigation & Bug Fixes (continued)
**Date**: 2026-02-13 (continued from previous session)
**Context**: User corrected earlier analysis — PH IS choppy (not trending). These 5 days historically identified as choppy. MR should theoretically work. Root cause investigation required.

#### Bugs Found & Fixed

**Bug 1: Cascading re-entry after hard stop (CRITICAL)**
- **Root cause**: After hard stop fires and exits to CASH, AnalystEngine's `_lastMrSignal` stays "MR_LONG" (hysteresis holds until %B > 0.5). TraderEngine sees MR_LONG + CASH → immediately re-enters. Price continues against → another stop → re-enter → repeat. Trades 5→6→7→8 on Feb 12 were 6-11 seconds apart.
- **Fix**: Added `_mrHardStopCooldown` flag in TraderEngine. After hard stop, MR_LONG/MR_SHORT signals are ignored until MR_FLAT resets the cycle (i.e., %B must cross midline before re-entering). 
- **Impact**: Config A Feb 12 went from 161 trades / -$2,551 to 13 trades / -$132 (92% trade reduction, 95% loss reduction)
- **File**: `MarketBlocks.Bots/Services/TraderEngine.cs`

**Bug 2: Trailing stop overriding MR signals**
- **Root cause**: `EvaluateTrailingStop()` runs before MR signal dispatch and can return `("NEUTRAL", false)`, forcing exit regardless of MR's own logic. MR has its own exit (MR_FLAT at midline) and loss protection (hard stop).
- **Fix**: Gated trailing stop with `regime.ActiveStrategy != StrategyMode.MeanReversion`
- **File**: `MarketBlocks.Bots/Services/TraderEngine.cs`

**Bug 3: Trim logic destroying MR positions**
- **Root cause**: Trim fires at +0.25% profit, selling 75% of position. Trade 1 on Feb 12 was trimmed from 139→35 shares. MR needs full position until %B exits at midline.
- **Fix**: Added `regime.ActiveStrategy != StrategyMode.MeanReversion` guard to trim check.
- **File**: `MarketBlocks.Bots/Services/TraderEngine.cs`

**Regression**: All 3 fixes verified — $608.90 / 38 trades exact match (MR dormant by default).

#### Comprehensive Sweep Results (Post-Fixes)

**PH Segment Replays (14:00-16:00, cold start — BB warms up during PH)**

| Config | BB | Entry | Exit | Stop | Total P/L | Trades | Avg/Trade |
|--------|-----|-------|------|------|-----------|--------|-----------|
| A | 20,2.0 | 0.2/0.8 | 0.5 | 0.3% | -$921 | 58 | -$15.88 |
| B | 20,2.0 | 0.1/0.9 | 0.5 | 0.3% | -$713 | 42 | -$16.98 |
| H | 20,2.0 | 0.2/0.8 | 0.5 | 0.9% | -$1,028 | 58 | -$17.72 |
| I | 20,2.5 | 0.15/0.85 | 0.5 | 0.9% | -$780 | 38 | -$20.53 |
| J | 30,2.0 | 0.1/0.9 | 0.5 | 0.9% | -$704 | 26 | -$27.08 |
| K | 30,2.0 | 0.1/0.9 | 0.5 | none | -$694 | 26 | -$26.70 |
| L | 30,2.0 | 0.1/0.9 | 0.4 | 0.9% | -$735 | 32 | -$22.97 |
| **M** | **20,3.0** | **0.1/0.9** | **0.5** | **none** | **-$441** | **20** | **-$22.07** |
| N | 30,3.0 | 0.1/0.9 | 0.5 | none | -$478 | 18 | -$26.54 |

**Full-Day Replays (09:30-16:00, BB pre-warmed from morning) — baseline $608.90**

| Config | BB | Entry | Exit | Stop | Total P/L | MR Contrib | Trades |
|--------|-----|-------|------|------|-----------|------------|--------|
| O | 60,2.0 | 0.1/0.9 | 0.5 | none | +$362 | **-$247** | 48 |
| P | 60,3.0 | 0.1/0.9 | 0.5 | none | +$462 | **-$147** | 42 |
| Q | 20,3.0 | 0.1/0.9 | 0.5 | none | +$427 | **-$182** | 50 |

*MR Contrib = (Full-day P/L) - (Baseline $608.90). Negative = MR destroys existing trend engine profits.*

#### Key Insight: BB Bandwidth vs Execution Costs

The fundamental problem is that BB bands on 1-minute candles are too narrow relative to execution costs:

1. **BB(20,2.0) bandwidth**: ~$0.40 on $604 QQQ = 0.066%. Entry at %B=0.1 → exit at %B=0.5 = ~$0.12 QQQ move → ~$0.029/share TQQQ profit → ~$5.80 on 200 shares
2. **Execution costs**: IOC offset $0.08 × 200 shares = $16 per round trip (buy + sell)
3. **Profit < costs**: The expected profit per MR trade is SMALLER than execution costs
4. Wider BB multiplier (3.0) helps but doesn't overcome the fundamental cost structure
5. The SMA adapts too quickly on 1-min candles — the "mean" shifts before reversion completes

**Why all configs lose every day**: The BB bandwidth on 1-min candles during a 2-hour PH window produces profit targets of ~$5-20 per trade, while each round-trip costs ~$16 in IOC slippage alone. This is a structural impossibility.

#### Decisions
- **3 bug fixes committed**: Cooldown, trailing stop bypass, trim bypass for MR. All structurally correct.
- **MR remains dormant** (`PhDefaultStrategy=Trend`) — no profitable settings found
- **Regression preserved**: $608.90 exact match

#### Next Steps for MR Viability
1. **Separate BB candle interval from CHOP**: Allow BB to use 5-min or 15-min candle aggregation while CHOP stays on 60s. This would produce much wider bands (~3-5x wider) where profit exceeds costs.
2. **Remove IOC slippage for MR**: Use market/limit orders instead of IOC for MR entries/exits, since MR doesn't need latency-sensitive execution.
3. **BB bandwidth filter**: Only enter MR trades when bandwidth exceeds a minimum threshold (ensures profit potential > cost).
4. **Market-on-close exit**: For MR trades still open at 15:55, exit at market close rather than forcing MR_FLAT.

---

## Session: 2026-02-19 — Research AI Recommendations: 5-min Candles + RSI + ATR Stops

**Context**: Prior session (A-Q sweep) showed all MR configs on 1-min candles lose money — BB bandwidth (~$0.40) too narrow vs IOC execution costs (~$16 round-trip). Consulted a Research AI ("Adaptive Architectures for Systematic Trading and Regime Detection") for specific parameter recommendations.

### Research AI Recommendations Implemented

1. **5-min candles** (`ChopCandleSeconds=300`) — BB, CHOP, ATR, RSI all share same aggregation
2. **ATR-based stops** (`MrAtrStopMultiplier=2.0`) — replaces fixed % stop, computed on QQQ benchmark, dynamic protection
3. **RSI confirmation REQUIRED** (`MrRequireRsi=true`, RSI(14), oversold<30, overbought>70) — filters bad entries
4. **Shared candle interval** — BB and CHOP both on 5-min to avoid mixed-resolution confusion

### Code Changes

| File | Change |
|------|--------|
| `StreamingRSI.cs` (NEW) | Wilder's smoothing RSI, 140 lines, same pattern as StreamingATR |
| `MarketRegime.cs` | Added `Rsi` and `Atr` nullable decimal fields |
| `TradingSettings.cs` (both repos) | 5 new settings: `MrAtrStopMultiplier`, `MrRequireRsi`, `MrRsiPeriod`, `MrRsiOversold`, `MrRsiOverbought` |
| `TimeBasedRule.cs` | 5 matching nullable overrides in `TradingSettingsOverrides` |
| `AnalystEngine.cs` | `_mrAtr`/`_mrRsi` fields, candle feeding, RSI filter in `DetermineMeanReversionSignal`, MarketRegime population, reconfig/reset |
| `TraderEngine.cs` | `_mrEntryBenchmarkPrice`/`_mrEntryIsLong`, ATR-based stop (benchmark space), benchmark tracking |
| `TimeRuleApplier.cs` | Snapshot/Restore/Apply/Log/IndicatorSettingsChanged for 5 new settings |
| `ProgramRefactored.cs` | Config loading for 5 new settings in `BuildTradingSettings` + `ParseOverrides` |

### Verification
- Build: 0 errors both solutions
- Tests: 575 pass (159 Bots + 61 Trade + 63 qqqBot + rest)
- Regression: **$608.90 / 38 trades** exact match (MR dormant baseline)

### Sweep 3 — Configs R through V (5-min candle + RSI + ATR)

**Key constraint**: BB(20) on 5-min candles = 20 × 300s = 100 min warmup. PH-cold useless. All configs full-day pre-warmed.

| Config | Variable | Key Difference |
|--------|----------|----------------|
| **R** | Research AI baseline | 5min, BB20/2.0, entry 0.1/0.9, ATR 2.0×, RSI required, PH-only MR |
| **S** | + CHOP override | `ChopOverrideEnabled=true` (MR can fire during Base if choppy) |
| **T** | RSI ablation | `MrRequireRsi=false` (measure RSI's filtering contribution) |
| **U** | Wider ATR stop | `MrAtrStopMultiplier=3.0` (more breathing room) |
| **V** | Wider entry | `MrEntryLowPctB=0.2, MrEntryHighPctB=0.8` (more trade signals) |

### Results (Feb 9-13 2026, full-day replays)

| Config | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | **Total P/L** | Trades | **MR Contrib** |
|--------|-------|--------|--------|--------|--------|---------------|--------|----------------|
| **R** | $33.27/6 | $12.92/9 | $196.96/6 | $172.36/15 | $179.18/6 | **$594.69** | 42 | **-$14.21** |
| **S** | $14.16/4 | -$49.74/9 | $196.96/6 | $172.36/15 | $179.18/6 | **$512.92** | 40 | **-$95.98** |
| **T** | -$27.75/8 | -$62.40/9 | $196.96/6 | $172.36/15 | $179.18/6 | **$458.35** | 44 | **-$150.55** |
| **U** | $33.27/6 | $12.92/9 | $196.96/6 | $172.36/15 | $179.18/6 | **$594.69** | 42 | **-$14.21** |
| **V** | $33.27/6 | $12.92/9 | $196.96/6 | $172.36/15 | $179.18/6 | **$594.69** | 42 | **-$14.21** |
| *Baseline* | — | — | — | — | — | *$608.90* | *38* | *$0.00* |

### Analysis

**Major finding — MR fires extremely rarely with 5-min candles:**
- Feb 11, 12, 13: ALL configs produce **identical** results to baseline. MR generates ZERO signals on these 3 days.
- Feb 9 and 10 are the only days with any MR differentiation.
- Configs R, U, V are **perfectly identical** — ATR multiplier (2.0 vs 3.0) and entry bands (0.1/0.9 vs 0.2/0.8) make zero difference because the few MR trades that fire don't trigger ATR stops and the wider entry bands don't capture additional signals.

**RSI filter is working and critical:**
- Config T (no RSI) vs R: -$150.55 vs -$14.21 MR contribution. RSI prevents **$136 in losses**.
- Feb 10 detail: Without RSI, MR_LONG fires at 15:07 → 50 min underwater position → -$62.40. With RSI, MR_LONG delayed to 15:57 (RSI finally < 30) → smaller late loss → +$12.92. RSI saved $75 on a single day.

**CHOP override during Base hurts badly:**
- Config S vs R: -$95.98 vs -$14.21. Enabling MR during Base (when CHOP > 61.8) destroys $82 in value.
- Feb 10: S loses -$49.74 vs R's +$12.92. CHOP override switches profitable Base trend trades to losing MR trades.

**Why MR fires so rarely on 5-min candles:**
1. BB(20) on 5-min candles has ~2.2× wider bandwidth than 1-min (√5 scaling). %B rarely reaches 0.1/0.9 extremes.
2. RSI(14) at 30/70 thresholds requires genuine oversold/overbought confirmation, further filtering rare %B extremes.
3. On "good" days (Feb 11, 13), daily profit target ($175) hit before PH → trading stops → MR never gets a chance.
4. On Feb 12, 15 trend trades generate $172.36 (close to target) — trend strategy dominates, no MR signals.

**Improvement vs prior sweeps (A-Q):**
- Prior sweeps (1-min candles): MR contribution ranged from **-$147 to -$921**. Catastrophic.
- This sweep (5-min + RSI): Best configs at **-$14.21**. A 90%+ improvement. The 5-min candle width + RSI filter has nearly eliminated MR damage.
- But still slightly net negative — MR adds 4 extra trades across 5 days, losing ~$3.55/trade.

### Decisions
- **MR remains dormant** (`PhDefaultStrategy=Trend`) — still not profitable
- **RSI filter validated** — essential if MR is ever enabled
- **CHOP override during Base disabled** — clearly harmful
- **5-min candle approach validated** — dramatically reduces MR damage vs 1-min

### Root Cause: MR Is Solving the Wrong Problem on These Dates

The 5-day sample (Feb 9-13) averages $121.78/day with trend-only strategy. MR is designed for "lost" PH days where price chops sideways. But on 3 of 5 days, the profit target fires before PH. On the 2 remaining days (Feb 9-10), the trend strategy still produces positive P/L ($33-46 range before MR interference). There are no truly "choppy PH disaster" days in this sample to test MR against.

### Next Steps
1. **Need data from genuinely choppy PH days** — Feb 9-13 may not include days where PH trend trading loses money
2. **Consider BB window reduction** — BB(10) or BB(15) on 5-min for faster warmup and tighter bands
3. **Consider RSI relaxation** — RSI 35/65 instead of 30/70 to allow more MR trades
4. **Consider 3-min candles** — Compromise between 1-min (too narrow) and 5-min (too few signals)
5. **Bandwidth minimum filter** — Only enter when BB bandwidth > some threshold ensuring profit potential > cost

---

## Session: PH Resume + MR Investigation (continuation)

**Context**: R-V sweep showed profit target fires before PH on 3/5 days (Feb 11-13), so MR never activates. Question: would PH Resume + MR add value on those days?

### Configs Tested
- **Config W**: Config R + `ResumeInPowerHour=true` + `PhDefaultStrategy=MeanReversion` — MR in PH after target fires
- **Config X**: Same as W but `PhDefaultStrategy=Trend` — trend-following in PH after target fires (control)

### Results: 3 Target-Fire Days (Feb 11-13)

| Date | No Resume (R) | PH Resume + MR (W) | PH Resume + Trend (X) |
|------|---------------|---------------------|------------------------|
| Feb 11 | $196.96 / 6t | $196.96 / 6t (MR_FLAT) | $196.96 / 6t (no PH trades) |
| Feb 12 | $172.36 / 15t | **$186.92 / 17t** | **-$4.71 / 27t** |
| Feb 13 | $179.18 / 6t | **$181.26 / 8t** | **$166.80 / 15t** |
| **Subtotal** | **$548.50** | **$565.14** | **$359.05** |

Non-target days (Feb 9-10): No change — PH Resume doesn't fire, results identical to Config R.

### 5-Day Totals

| Config | Total P/L | Trades | vs Baseline |
|--------|-----------|--------|-------------|
| R (no PH Resume) | $594.69 | 42 | — |
| W (PH Resume + MR) | $611.33 | 46 | **+$16.64** |
| X (PH Resume + Trend) | **$405.37** | 57 | **-$189.32** |

### Key Findings

1. **PH Resume + Trend is catastrophic**: Trend-following during PH on target-fire days destroyed $189 of gains. Feb 12 alone went from +$172 to -$5 — entire day's profit plus more wiped out.
2. **PH Resume + MR is net positive**: +$16.64 incremental gain. MR correctly identifies choppy conditions and trades conservatively.
3. **MR advantage over Trend in PH**: $206 difference ($565 vs $359). MR exists to protect against exactly this choppy-PH scenario.
4. **Feb 11 was truly flat**: Both strategies stayed in cash — PH had no actionable entry signals.
5. **MR fires late (15:51+)**: RSI + BB(20) on 5-min candles is conservative — only enters after significant band penetration late in session.
6. **This answers "Next Step #1"**: We found the choppy PH days — they're the target-fire days where trend would resume and get destroyed.

### Implications

- **MR + PH Resume is the correct combined strategy**: Use MR (not trend) when resuming in PH after profit target fires
- The old question "need data from genuinely choppy PH days" is answered: **Feb 12-13 are exactly that** — trend loses money, MR preserves/adds gains
- MR's value isn't in replacing trend-following during normal PH (where it can subtract $14) — it's in **protecting profits during PH on high-volatility/choppy days**
- Consider making PH Resume + MR the default configuration for days where daily target fires early

### Open Tuning Questions
1. ~~Could MR fire earlier with relaxed RSI (35/65) or smaller BB window (15)?~~ → **Answered below** (no — relaxation is destructive)
2. Would 3-min candles give more MR signals without the 1-min noise problem?
3. Should PH Resume automatically select MR regardless of PhDefaultStrategy?

---

## Session: Research AI Tuning Recommendations Sweep (2026-02-16)

**Context**: Research AI recommended: BB(10), mult 1.5-1.8, RSI 35/65 to address "late fire" (15:51+) issue. Hypothesis: shorter lookback + relaxed filters = earlier signals = more upside.

### Configs Tested (all with PH Resume=true)

| Config | BB Window | BB Mult | RSI | Change vs W |
|--------|-----------|---------|-----|-------------|
| **W** (baseline) | 20 | 2.0 | 30/70 | — |
| **Y** | 10 | 2.0 | 30/70 | BB window only |
| **Z** | 10 | 1.5 | 35/65 | Full aggressive |
| **AA** | 10 | 1.8 | 35/65 | Moderate |
| **AB** | 20 | 2.0 | 35/65 | RSI only |

### Full Results (5 dates × 5 configs = 25 runs)

| Config | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | **Total** | **vs W** |
|--------|-------|--------|--------|--------|--------|-----------|----------|
| **W** | $33.27 | $12.92 | $196.96 | $186.92 | $181.26 | **$611.33** | — |
| **Y** | $11.22 | $12.92 | $196.96 | $186.92 | $181.26 | $589.28 | -$22 |
| **Z** | -$15.24 | -$37.61 | $213.13 | $116.47 | $90.60 | $367.35 | **-$244** |
| **AA** | -$15.24 | -$37.61 | $213.13 | $116.47 | $90.60 | $367.35 | **-$244** |
| **AB** | $6.81 | -$37.61 | $229.30 | $116.47 | $90.60 | $405.57 | **-$206** |

### Isolation Analysis

**BB multiplier (1.5 vs 1.8)**: Zero impact. Z and AA are **bitwise identical** across all 5 dates. The multiplier is not a binding constraint — %B extremes are driven by BB window and price action, not std dev width.

**BB window (10 vs 20)**: Minor impact. Y lost $22 vs W, all from Feb 9 (-$22.05); Feb 11-13 identical. BB(10) makes bands more reactive → marginal worse entries on non-target days. The "warmup lag" thesis is wrong — BB(20) on 5-min is warm (54 candles) by PH start at 14:00.

**RSI relaxation (35/65 vs 30/70)**: **Dominant destructive factor**. Every config with RSI 35/65 lost $200+ vs W:
- Feb 10: -$50 (new MR entries in PH hit stop losses)
- Feb 12: -$70 (entered MR_LONG 5 min early at 15:46 — caught false bottom, run P/L -$45 vs W's +$14)
- Feb 13: -$91 (similar premature entry, price hadn't actually bottomed)
- Feb 11: +$32 (the one win — unlocked a trade W's strict RSI blocked)
- Net: +$32 - $50 - $70 - $91 = **-$179 from RSI relaxation**

### Why the Research AI's Diagnosis Was Wrong

1. **"100-minute warmup lag" is not the issue**: Indicators run all day (full-day mode, not PH-cold). By 14:00, BB(20) has 270 min of data. The "late fire" at 15:51 isn't warmup lag — price genuinely doesn't reach %B 0.1/0.9 until then.

2. **"Catching shallow reversals" backfires**: RSI 35/65 enters on marginal oversold/overbought that hasn't fully developed. On Feb 12, the "shallow" RSI ~35 entry at 15:46 caught a false bottom that continued lower. The "deep" RSI <30 entry at 15:51 (Config W) caught the actual reversal. Strict RSI = better fill quality.

3. **"More signals = more upside" is wrong for MR**: In MR, trade quality matters more than quantity. One clean reversion from a genuine extreme (RSI <30 + %B <0.1) outperforms multiple noisy entries from marginal conditions.

### Conclusion

**Config W is definitively the best MR configuration.** The conservative RSI 30/70 filter correctly rejects premature MR entries. The "late fire" problem is not actually a problem — it's the bot waiting for high-quality setups. The research AI's thesis that faster/more-permissive parameters would unlock upside is empirically falsified.

### Updated Tuning Priorities
1. ~~BB window shrink~~ → Tested, hurts marginally
2. ~~RSI relaxation~~ → Tested, destructive (-$179 to -$206)
3. ~~BB multiplier reduction~~ → Tested, zero impact
4. 3-min candles — untested, could provide more data points while keeping strict filters
5. Slope confirmation for MR entries — untested, could improve entry timing without relaxing RSI

---

## Production Deployment: Config W (2026-02-16)

**Changes to `appsettings.json`:**
- `ResumeInPowerHour`: `false` → `true`
- Added MR settings block: `PhDefaultStrategy=MeanReversion`, `ChopCandleSeconds=300`, `BollingerWindow=20`, `BollingerMultiplier=2.0`, `MrEntryLowPctB=0.1`, `MrEntryHighPctB=0.9`, `MrExitPctB=0.5`, `MeanRevStopPercent=0.003`, `MrAtrStopMultiplier=2.0`, `MrRequireRsi=true`, `MrRsiPeriod=14`, `MrRsiOversold=30`, `MrRsiOverbought=70`, `ChopOverrideEnabled=false`

**Regression verified**: Feb 12 replay = $186.92 / 17 trades (exact Config W match)

**Expected behavior**:
- OV + Base: Unchanged trend-following (MR indicators warm up silently)
- PH (no target fired): MR signals during PH; typically MR_FLAT (no trades) unless extreme %B reached
- PH (after target fired): PH Resume activates → MR protects profits instead of trend whipsaws
- 5-day backtest: $611.33 / 46 trades (+$3 vs no-resume baseline)

---

## Session: Feb 17 Missed Uptrend Investigation (2026-02-20)

### Context
Feb 17, 2026: QQQ rose ~$7.56 (+1.27%) from $593.63→$601.19 between 10:40-11:13 ET. The bot was in CASH the entire time, missing significant TQQQ upside. Additionally, smaller missed opportunities at 11:26 ET and 12:37-12:45 ET.

### Baseline Replay (Feb 17)
- **Result**: -$21.93 P/L, 15 trades
- Peak: +$75.72 at 09:56 ET, Trough: -$173.66 at 09:46 ET
- Bot went to CASH at 10:44 ET and **never re-entered for the rest of the day**
- All 15 trades were OV or early Base phase; none captured the 10:40-11:13 uptrend

### Root Cause #1: TrendRescue Deadlock Bug (CONFIRMED)
**Location**: `AnalystEngine.cs`, `DetermineSignal()`, line ~773 (original)

**Original code**:
```csharp
else if (activeSlope > entryVelocity ||
         (trendRescue && _sustainedVelocityTicks >= _settings.EntryConfirmationTicks))
```

**Problem**: Classic chicken-and-egg deadlock. `_sustainedVelocityTicks` only increments INSIDE this block, but the trendRescue path requires `_sustainedVelocityTicks >= EntryConfirmationTicks` to ENTER the block. When `activeSlope` is below `entryVelocity`, trendRescue can never fire.

**Math on Feb 17 uptrend**:
- maintenance velocity = $595 × 0.000015 = $0.00893/tick
- entry velocity = $0.00893 × 2.0 = $0.01785/tick
- Actual SMA slope during 10:44-11:13 uptrend ≈ $0.0175/tick
- Shortfall: $0.0003 (1.7%) — slope was JUST below the entry threshold
- TrendRescue was designed to catch exactly this case, but deadlock prevented it

### Root Cause #2: Trailing Stop Too Aggressive for Gradual Trends
**Location**: `TraderEngine.cs`, `EvaluateTrailingStop()`, line ~1829

**Problem**: Even if trendRescue correctly enters BULL, the 0.2% trailing stop on QQQ benchmark ($1.19 distance at $596) ejects positions within minutes during gradual uptrends. The DynamicStopLoss ratchet further tightens stops as profit grows, making it even worse.

**Evidence**: Fix v6 (trendRescue working) produced 41 trades on Feb 17 — the bot correctly entered BULL during Base phase uptrend via trendRescue, but every entry was immediately stopped out by the trailing stop, creating an enter→stop→enter→stop churn cycle.

### Root Cause #3: `--override` CLI Flag Is Broken
**Location**: `CommandLineOverrides.cs`

The `--override KEY=VALUE` flag is silently ignored — there is no handler for it in `Parse()`. All earlier parameter sweeps (TrendWindowSeconds, MinVelocityThreshold) using `--override` produced identical results because the overrides were never applied. Must modify `appsettings.json` directly for parameter changes.

### CycleTracker Audit
Zero `[RHYTHM]` log lines on Feb 17 replay. CycleTracker confirmed **NOT a factor** in the missed uptrend.

### Fix Iteration Results

All fixes cross-validated on Feb 9-13 + Feb 17. Build: `dotnet clean && dotnet build` both repos between each.

**Note**: Feb 9-13 baseline = $611.33 (Config W production settings, see "Production Deployment: Config W" above). The "Total" column below is the **6-day** sum including Feb 17.

| Fix | Description | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | Feb 17 | 6-Day Total |
|-----|------------|-------|--------|--------|--------|--------|--------|-------------|
| **Baseline** | Config W (production) | +$33 (6tr) | +$13 (9tr) | +$197 (6tr) | +$187 (17tr) | +$181 (8tr) | -$22 (15tr) | **+$589** |
| v1 | trendRescue, no gate | +$83 (15) | -$63 (13) | -$395 (35) | +$187 (17) | -$527 (49) | +$169 (6) | -$546 |
| v3 | trendRescue, 3x confirm | +$22 (15) | -$67 (13) | +$197 (6) | +$187 (17) | -$509 (47) | +$185 (6) | +$16 |
| v4 | trendRescue, 5x confirm | +$66 (9) | -$67 (13) | +$197 (6) | +$187 (17) | -$570 (45) | -$244 (41) | -$431 |
| v5 | trendRescue, 10x+min20 | +$33 (6) | +$26 (11) | +$197 (6) | +$187 (17) | +$181 (8) | -$150 (37) | +$475 |
| v6 | Base-only, 3x confirm | +$34 (13) | -$84 (11) | +$197 (6) | +$187 (17) | +$181 (8) | -$198 (41) | +$317 |
| EVM=1.9 | Lower velocity multiplier | +$33 (6) | +$13 (9) | +$197 (6) | +$187 (17) | +$181 (8) | -$191 (21) | +$420 |

### Key Insight
v1 and v3's good Feb 17 results (+$169/+$185 with only 6 trades) came from changed **OV behavior** (earlier BULL switch, daily target hit by 09:55 ET), NOT from capturing the Base phase uptrend. The 10:44-11:13 ET uptrend was never successfully captured by ANY fix iteration because:

1. Fixes that allow trendRescue during OV: change OV entry timing → earlier daily target → stops trading before the uptrend even starts (appears to "fix" Feb 17 but is coincidental and wildly regresses other days)
2. Fixes restricted to Base phase (v6): correctly enter BULL via trendRescue during uptrend, but trailing stop ejects every position within minutes → 41 trades, massive churn loss

**Solving the missed uptrend requires addressing BOTH root causes simultaneously:**
1. Fix the trendRescue deadlock (entry problem)
2. Widen or adapt the trailing stop for trendRescue-initiated entries during gradual trends (exit problem)

### Infrastructure Added
- **EntryVelocityMultiplier** setting: configurable multiplier for entry velocity = maintenance × multiplier (was hardcoded 2.0). Added to:
  - `MarketBlocks.Bots/Domain/TradingSettings.cs` (property, default 2.0m)
  - `MarketBlocks.Bots/Domain/TimeBasedRule.cs` (nullable override)
  - `MarketBlocks.Bots/Services/TimeRuleApplier.cs` (snapshot/restore/apply/changes/SettingsSnapshot)
  - `qqqBot/ProgramRefactored.cs` (loading, phase config, logging)
  - All tests pass (159 Bots tests, 63 qqqBot tests)

### Code State After Session
- **Fix v6 currently applied** to AnalystEngine.cs (phase-restricted trendRescue, 3x confirmation, Base/PH only)
- **EntryVelocityMultiplier infrastructure** in both repos
- No changes committed

### Open Questions for Next Session
1. Should we implement a "trend hold" mode that widens trailing stop for trendRescue-initiated entries?
2. Should we disable the DynamicStopLoss ratchet tightening during trendRescue entries?
3. Alternative: signal-based exit only (no trailing stop) for trendRescue entries?
4. The `--override` CLI bug needs fixing for efficient parameter sweeps
5. Smaller missed trades at 11:26 and 12:37-12:45 ET not yet investigated

---

## Session: Combined TrendRescue + Wider Stop Fix (2026-02-20, continued)

### Approach
Combined entry fix (trendRescue deadlock) with exit fix (Option A: wider trailing stop, no ratchet).

### v7: Maintenance Velocity Floor + 5x Confirmation + 0.5% Wider Stop (WINNER)

**Entry changes** (AnalystEngine.cs):
1. Fixed trendRescue deadlock: trendRescue ticks now accumulate independently of `activeSlope > entryVelocity`
2. Phase restriction: trendRescue only fires during Base/PH (after 10:13 ET)
3. Minimum slope floor: `activeSlope > maintenanceVelocity` required (was just `> 0`). Filters out weak/noisy trends.
4. Higher confirmation: 5x normal EntryConfirmationTicks (= 10 ticks). Prevents false positives on choppy days.
5. `IsTrendRescueEntry` flag propagated through `MarketRegime` record to TraderEngine.

**Exit changes** (TraderEngine.cs):
1. `_isTrendRescuePosition` tracked per position
2. When `TrendRescueTrailingStopPercent > 0`, trendRescue positions use that as trailing stop distance
3. DynamicStopLoss ratchet is SKIPPED for trendRescue positions (ratchet causes immediate churn on gradual trends)
4. Non-trendRescue positions: completely unchanged behavior

**Settings** (appsettings.json):
- `TrendRescueTrailingStopPercent`: 0.005 (0.5%) — only applies to trendRescue entries

### v7 Results

| Date | Baseline | v7 | Delta |
|------|----------|-----|-------|
| Feb 9 | +$33 (6tr) | +$33 (6tr) | = |
| Feb 10 | +$13 (9tr) | +$13 (9tr) | = |
| Feb 11 | +$197 (6tr) | +$197 (6tr) | = |
| Feb 12 | +$187 (17tr) | +$187 (17tr) | = |
| Feb 13 | +$181 (8tr) | +$181 (8tr) | = |
| **5-day (Feb 9-13)** | **+$611** | **+$611** | **=** |
| Feb 17 | -$22 (15tr) | **+$8** (23tr) | **+$30** |
| **6-day total** | **+$589** | **+$619** | **+$30** |

**Zero regression on all 5 other days. Feb 17 turns from losing (-$22) to profitable (+$8).**

- 4 trendRescue entries on Feb 17 (confirmed via [TREND RESCUE] log lines)
- 0 trendRescue entries on Feb 10 (maintenance velocity floor correctly filters it)
- Feb 9, 11, 12, 13: no trendRescue entries (identical trade counts and P/L)
- All tests pass: 159 Bots + 63 qqqBot

### Earlier v7 Iterations (Rejected)

| Config | Feb 10 | Feb 17 | Issue |
|--------|--------|--------|-------|
| 3x confirm, no floor, 0.5% stop | -$118 (11tr) | -$10 (27tr) | False trendRescue on Feb 10, $132 loss |
| 3x confirm, no floor, no wide stop | -$84 (11tr) | -$198 (41tr) | Entry alone regresses Feb 10 + churn on Feb 17 |

**Key insight**: The maintenance velocity floor (`activeSlope > maintenanceVelocity`) is the critical filter that prevents false trendRescue entries. Without it, even with high confirmation ticks, trendRescue fires on weak slopes that don't sustain.

### CycleTracker Deep Audit (2026-02-20)

Comprehensive code audit found CycleTracker has exactly **one** influence point: `HandleNeutralAsync()` in TraderEngine, where it can reduce the neutral timeout (`effectiveWaitSeconds`). This reduction:
1. Only fires when 3 conditions are all met (cycle > 30s, stability < 20s std, and cap < configured wait)
2. Always logs `[RHYTHM]` when it fires — cannot act silently
3. Does not modify signals, bands, slopes, stops, or entry decisions

**Assessment**: CycleTracker is clean. No `[RHYTHM]` = zero effect on trading decisions.

### Code Changes (Committed State)

**MarketBlocks** (4 files):
- `MarketBlocks.Bots/Domain/MarketRegime.cs`: Added `bool IsTrendRescueEntry = false`
- `MarketBlocks.Bots/Domain/TradingSettings.cs`: Added `EntryVelocityMultiplier` (2.0 default), `TrendRescueTrailingStopPercent` (0 default)
- `MarketBlocks.Bots/Domain/TimeBasedRule.cs`: Added nullable overrides for both
- `MarketBlocks.Bots/Services/TimeRuleApplier.cs`: Added to all 5 sections
- `MarketBlocks.Bots/Services/AnalystEngine.cs`: Fixed trendRescue deadlock, added IsTrendRescueEntry propagation, `_currentSignalIsTrendRescue` tracking
- `MarketBlocks.Bots/Services/TraderEngine.cs`: Added `_isTrendRescuePosition`, wider stop for trendRescue, reset on position close and latch clear

**qqqBot** (2 files):
- `qqqBot/ProgramRefactored.cs`: Added loading/logging for both new settings
- `qqqBot/TradingSettings.cs`: Added `TrendRescueTrailingStopPercent`
- `qqqBot/appsettings.json`: Added `TrendRescueTrailingStopPercent: 0.005`

---

## Session: Feb 17 Live vs Replay Divergence Investigation (2026-02-17)

### Context
After applying v7 (TrendRescue + wider stop), Feb 17 replay shows +$8.16 but live trading returned +$85.44 — a **$77.28 gap**. Prior divergence investigations found real bugs (Feb 12: $425 gap from Brownian bridge), so this was worth investigating.

### Methodology
1. Extracted complete trade logs from live (`qqqbot_20260217.log`, +$85.44, 9 trades)
2. Ran v7 replay (`--mode=replay --date=20260217 --speed=0`, +$8.16, 23 trades)
3. Side-by-side trade comparison with timestamps
4. Audited `SimulatedBroker.cs` fill model
5. Reviewed `TraderEngine.cs` pending order timeout / buy cooldown pathways

### Root Cause: SimulatedBroker Instant Fill Model

The entire divergence cascades from **one event at 09:31 ET**:

| | Live (Alpaca) | Replay (SimulatedBroker) |
|---|---|---|
| 09:31 BEAR signal | SQQQ buy submitted | SQQQ buy submitted |
| Fill result | **Timed out after 10s** (no fill) | **Filled instantly** |
| Consequence | Cash rolled back, 15s buy cooldown | Entered SQQQ, lost **-$142.04** on trailing stop |
| Next entry | TQQQ at 09:37:29 (after 3 more fill failures) | TQQQ at different time/price |

**SimulatedBroker structural limitations:**
- `SubmitOrderAsync` (L103-224): Every valid order fills instantly with `Status = Filled`
- `CancelOrderAsync` (L231-235): Always returns `false` (nothing to cancel)
- No `PendingNew/Accepted` state ever reached
- The entire `ProcessPendingOrderAsync` pathway in TraderEngine (10s timeout, cancel, reconcile, buy cooldown backoff) is **dead code in replay**
- Buy cooldown (15s/30s/60s escalating) never activates

**Secondary divergence**: v7 TREND RESCUE trades (4 entries in replay) don't exist in live (live ran pre-v7 code). These contributed ~+$24 net for replay.

**Cascading effect**: Different P/L from trade 1 → different capital → different share counts → different subsequent trade outcomes. Makes simple arithmetic reconciliation impossible.

### Decision
**Accepted as inherent** (Option 1). Fill failures are non-deterministic (market depth, latency, queue position). The gap is fully explained by the SimulatedBroker's instant fill model vs real-world order timeouts.

### Future Enhancement (Backlog)
A combination of fill latency simulation + stochastic fill failure could narrow the gap while preserving determinism via seeded RNG. This would:
- Exercise the `ProcessPendingOrderAsync` pathway in replay
- Model OV-phase fill failures (where standard limit orders often timeout)
- Remain deterministic for regression testing (seeded random generator)
- Not yet implemented — added to TODO.md

### Historical Divergence Summary

| Date | Gap | Root Cause | Status |
|------|-----|------------|--------|
| Feb 12 | $425 | Brownian bridge synthetic ticks | Fixed |
| Feb 11 | $48 | IOC partial fills + latency | Documented (expected) |
| **Feb 17** | **$77** | **SimulatedBroker instant fill** | **Documented (expected)** |

---

## Session: 2026-02-18 — Adaptive Trend & EOD Cutoff

### Context
Feb 18 live trading: bot went BEAR at 09:30 (2x SQQQ buy timeouts, broker errors), then stayed NEUTRAL-CASH from 09:31 to 13:42 while QQQ rallied from $601 to $607. Ended at -$17.04 (3 trades). Suspected "Opening Blindness" — trend SMA unreliable during OV→Base warmup.

### Changes Implemented

**Block 1: Adaptive Trend Detection (AnalystEngine)**
- Added `EnableAdaptiveTrendWindow` (bool, default true → later set false)
- Added `ShortTrendSlopeWindow` (int, 90) and `ShortTrendSlopeThreshold` (decimal, 0.00002)
- Two mechanisms in `DetermineSignal()`:
  1. **FillRatio scaling**: `effectiveTrendWidth *= _trendSma.FillRatio` during warmup — makes trend detection proportionally easier
  2. **Slope override**: Forces `isBullTrend`/`isBearTrend` when `_shortTrendSlope.CurrentSlope` exceeds threshold — re-enables TrendRescue/CruiseControl during warmup
- Added `_shortTrendSlope` (StreamingSlope) field, constructor init, ProcessTick feed, ReconfigureIndicators resize+seed, ColdResetIndicators clear, PartialResetIndicators seed
- Full TimeRuleApplier plumbing (all 7 sections), ProgramRefactored loading, TimeBasedRule overrides

**Block 2: End-of-Day Entry Cutoff (TraderEngine)**
- Added `LastEntryMinutesBeforeClose` (decimal, default 0 — disabled unless set in appsettings.json)
- `EnsureBullPositionAsync` and `EnsureBearPositionAsync`: early-return when ET time ≥ `15:58 - LastEntryMinutesBeforeClose`
- appsettings.json sets `LastEntryMinutesBeforeClose: 2` (cutoff at 15:56 ET)

**Block 3: Broker Timeout Fix (appsettings.json)**
- Changed OV overrides: `UseMarketableLimits: false, UseIocOrders: false` → `true, true`
- **REVERTED**: This caused massive regression in replay — SimulatedBroker fills IOC instantly but the order flow changes. OV Market orders have much lower simulated slippage.
- **Root cause preserved**: Market orders during first ~60s of OV sit in `Accepted` state >10s on Alpaca. IOC would fix live but harms replay determinism. Left as `false, false` in config; fix is a live-only concern that needs SimulatedBroker latency modeling first.

**Block 4: TODO.md Updates**
- Added "Bidirectional MR Research" item (separate branch exploration)
- Added "Broker Timeout During OV" item (documented as identified, workaround noted)

### Replay Sweep Results

**Adaptive Trend — All Variants Tested on Feb 9-13 + 17-18:**

| Config | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | Feb 17 | Feb 18 | Total |
|--------|-------|--------|--------|--------|--------|--------|--------|-------|
| **Baseline (adaptive off)** | +$33 (6) | +$13 (9) | +$197 (6) | +$187 (17) | +$181 (8) | +$8 (23) | -$88 (6) | **+$531** |
| FillRatio only, OV gated | +$33 (6) | +$4 (11) | +$197 (6) | +$187 (17) | +$181 (8) | — | — | **+$603** |
| Both mechanisms, OV gated | +$48 (9) | -$73 (13) | +$197 (6) | +$187 (17) | +$181 (8) | +$37 (35) | -$88 (6) | **+$489** |
| Both mechanisms, everywhere | -$72 (15) | — | — | — | — | — | — | **heavily negative** |
| Split OV: Early (09:30-09:50 off) + Late (09:50-10:13 on) | -$2 (10) | +$13 (9) | +$197 (6) | +$179 (17) | +$181 (8) | +$8 (23) | -$88 (6) | **+$488** |

### Key Findings

1. **Feb 18 Opening Blindness is an OV-phase problem, NOT a Base-warmup problem.** The QQQ rally (601→607) happened during OV (09:50-10:05). By Base start (10:13), QQQ was already at 607-608 and no longer trending. Adaptive trend detection during Base warmup makes zero difference on Feb 18.

2. **Slope override is the main damage source.** It forces false `isBullTrend`/`isBearTrend` based on noisy 90-tick slopes, enabling bad TrendRescue entries that get stopped out. Feb 10 regression: -$86 from slope override alone.

3. **FillRatio scaling is near-neutral.** Only marginal effect (-$8 on 5-day total), not enough to justify the added complexity.

4. **OV gating helps but doesn't eliminate harm.** Disabling adaptive during OV and only running during Base warmup reduces damage but the slope override still causes whipsaws during Base transition.

5. **The adaptive approach as designed doesn't work.** The Feb 18 problem requires a fundamentally different approach — likely one that operates during OV itself (where the rally happened), not during the post-OV warmup period the adaptive was designed for.

### Decision
- `EnableAdaptiveTrendWindow: false` in appsettings.json (feature disabled)
- All infrastructure code committed and available for future tuning
- OV `UseIocOrders` left as `false` (original value) — IOC during OV is a live-only concern that needs SimBroker latency modeling
- `LastEntryMinutesBeforeClose: 2` enabled in appsettings.json (prevents entries after 15:56 ET)

### Baseline Established
Current 5-day baseline (Feb 9-13) with v7 TrendRescue + adaptive off: **+$611.33** (46 trades)
Prior baseline reference: +$608.90 (without v7 TrendRescue changes)

---

## Session 2026-02-18 (continued) — Drift Mode & Displacement Re-Entry

### Context
Research AI consultation identified the fundamental architectural gap: qqqBot has no velocity-independent entry path. Gradual price drifts where SMA tracks price don't breach Bollinger Bands, so the velocity gate blocks entries even when the trend is clear from price position alone. Feb 18 missed a 1% QQQ rally ($601→$607) during OV because of this gap.

Research AI proposed 3 changes:
1. **Remove noisy slope override** — `_shortTrendSlope` (90-tick) override was proven harmful  
2. **Drift Mode (TimeInZone counter)** — enter when price sustains above/below SMA for N ticks  
3. **Displacement Re-Entry** — re-enter after directional→NEUTRAL transition when price moves significantly

### Changes Made

#### 1. Slope Override Removed
- Deleted the `_shortTrendSlope` override block from `DetermineSignal` (was gated by `EnableAdaptiveTrendWindow`)
- FillRatio scaling kept (near-neutral, harmless)
- `_shortTrendSlope` field/init/feed kept to avoid plumbing churn (dead code when `EnableAdaptiveTrendWindow=false`)

#### 2. Drift Mode Implemented
New fields in AnalystEngine:
- `_consecutiveTicksAboveSma` / `_consecutiveTicksBelowSma` — counters updated every tick
- `_isDriftEntry` — flag for drift position maintenance (bypass velocity exit)
- `_bullDriftConsumedThisPhase` / `_bearDriftConsumedThisPhase` — per-direction one-shot flags

Drift mode logic in `DetermineSignal`:
- **Counter update**: Before velocity logic, price compared to `currentSma`
- **Position maintenance**: If `_isDriftEntry && isInTrade`, maintain signal while price stays on correct side of SMA (bypasses velocity gate exit)
- **Entry path**: After main signal logic, if `!isInTrade && newSignal == "NEUTRAL"`:
  - Check duration: consecutive ticks ≥ `DriftModeConsecutiveTicks`
  - Check magnitude: displacement from SMA ≥ `DriftModeMinDisplacementPercent`
  - Check one-shot: per-direction consumed flag not set
  - If all pass → override to BULL or BEAR
- **Exit**: Position releases when price crosses SMA; `_isDriftEntry = false`

New settings:
- `DriftModeEnabled` (bool, default false) — globally enable/disable
- `DriftModeConsecutiveTicks` (int, default 60) — ~3min at ~3s/tick
- `DriftModeMinDisplacementPercent` (decimal, default 0.002) — 0.2% minimum distance from SMA

#### 3. Displacement Re-Entry Implemented (disabled)
New field: `_lastNeutralTransitionPrice` — records price when signal transitions from BULL/BEAR to NEUTRAL.

Logic: If `!isInTrade && newSignal == "NEUTRAL"` and `_lastNeutralTransitionPrice` has value:
- Compute displacement % from recorded price
- If > `DisplacementReentryPercent` → BULL re-entry
- If < -`DisplacementReentryPercent` → BEAR re-entry

New settings:
- `DisplacementReentryEnabled` (bool, default false) — left disabled (caused regressions in testing)
- `DisplacementReentryPercent` (decimal, default 0.005) — 0.5% displacement threshold

#### 4. Plumbing
All new settings added to: MarketBlocks TradingSettings, qqqBot TradingSettings, TimeBasedRule, TimeRuleApplier (Snapshot/Restore/Apply/Log/SettingsSnapshot), ProgramRefactored (BuildTradingSettings + ParseOverrides), appsettings.json.

Reset logic in ColdReset and PartialReset: counters, flags, and displacement price all cleared.

### Development Iterations

**Iteration 1: Naive drift (global, 60 ticks, no filters)**
- Feb 18: +$149 ✓ (captured OV rally!)
- Feb 9: -$458 ✗ (53 trades, oscillation every 2 ticks — BEAR→NEUTRAL→BEAR cycling)
- Fix needed: oscillation prevention

**Iteration 2: Added `_isDriftEntry` sticky hold + counter reset**
- Fixed 2-tick oscillation — drift holds until price crosses SMA
- Still cycling: enter→hold→SMA cross→exit→60 ticks→re-enter (15+ entries per OV)
- Feb 9: -$458 still (drift cycling throughout Base/PH)

**Iteration 3: OV-only phase gating**
- DriftModeEnabled=false global, true in OV override
- Feb 9: +$4 (improved but -$29 from baseline)
- Feb 18: +$149 ✓ BUT...
- Feb 11/12/13: massive regressions (37 trades each — displacement re-entry was still enabled)

**Iteration 4: OV-only + displacement disabled**
- Standard displacement ReEntry disabled, drift OV-only
- Still too many trades from drift cycling within OV

**Iteration 5: One-shot per phase**
- `_driftConsumedThisPhase` prevents re-entry after first drift
- Feb 18: Wrong direction first — BEAR consumed the one-shot before BULL could catch the rally

**Iteration 6: Per-direction one-shot**
- Separate `_bullDriftConsumedThisPhase` / `_bearDriftConsumedThisPhase`
- Better but still not effective on Feb 18 (-$84)

**Iteration 7: Displacement filter (FINAL)**
- Added `DriftModeMinDisplacementPercent` (0.2%) — price must be ≥0.2% from SMA when ticks threshold met
- **Globally enabled** (not OV-gated) — the displacement filter naturally prevents entries on low-volatility/choppy days
- Per-direction one-shot preserved

### Final Replay Results (Drift Mode with displacement filter)

| Date | Baseline P/L | Drift P/L | Delta | Base Trades | Drift Trades |
|------|-------------|-----------|-------|-------------|--------------|
| Feb 9 | +$33 | +$33 | $0 | 6 | 6 |
| Feb 10 | +$13 | +$13 | $0 | 9 | 9 |
| Feb 11 | +$197 | +$197 | $0 | 6 | 6 |
| Feb 12 | +$187 | +$187 | $0 | 17 | 17 |
| Feb 13 | +$181 | +$175 | -$6 | 8 | 8 |
| Feb 17 | +$8 | +$41 | +$33 | 23 | 25 |
| **Feb 18** | **-$88** | **+$157** | **+$245** | 6 | 4 |
| **7-day Total** | **+$531** | **+$803** | **+$272** | — | — |

### Key Findings

1. **Displacement filter is the breakthrough.** Duration-only drift (consecutive ticks) generates too many false entries. Adding a magnitude filter (price must be ≥0.2% from SMA) makes drift selective enough to be net positive.

2. **Zero regression on 4 of 7 days.** The displacement filter prevents drift from firing at all on choppy/rangebound days (Feb 9, 10, 11, 12). Only fires on days with genuine sustained displacement from SMA.

3. **Feb 18 fix works.** Target day went from -$88 to +$157 — drift entered BULL during OV rally and held through the move. Trade count actually decreased (6→4) because drift captured the move in fewer, larger trades.

4. **Displacement Re-Entry too noisy.** Enabled it caused regressions on every day. Left infrastructure in place but disabled (`DisplacementReentryEnabled: false`).

5. **Drift maintenance is essential.** Without `_isDriftEntry` sticky hold, the velocity gate immediately exits drift positions on the next tick (stalled→NEUTRAL). The maintenance block bypasses velocity logic while price stays on correct side of SMA.

### Current Settings (after Drift Mode session)
```json
"DriftModeEnabled": true,
"DriftModeConsecutiveTicks": 60,
"DriftModeMinDisplacementPercent": 0.002,
"DisplacementReentryEnabled": false,
"DisplacementReentryPercent": 0.005,
"EnableAdaptiveTrendWindow": false
```

---

## Session 2026-02-18 (continued) — ATR-Dynamic Drift Threshold & Drift Trailing Stop

### Context
Research AI consultation proposed 4 improvements to Drift Mode. After feasibility assessment, two were selected:
1. **#1: ATR-dynamic drift displacement threshold** — scale displacement threshold with intraday volatility so the bar rises automatically on high-vol days
2. **#3: ATR trailing stop for drift entries** — use a wider stop (like TrendRescue) for drift entries since they catch gradual moves that need more breathing room

Also researched: **Tradier streaming API volume data** for future CVD-gated Displacement Re-Entry.

### Changes Implemented

#### 1. ATR-Dynamic Drift Threshold (AnalystEngine)

**Design evolution:**
- **Initial approach**: Replace fixed threshold with `K × ATR / price`. At K=1.0, ATR=$0.59, price=$614 → dynamic threshold = 0.097%. This was **half** the fixed 0.2%, admitting noise entries.
- **Feb 9 regression**: -$93 swing (+$33→-$60) because 0.097% threshold let drift fire on marginal price displacement that wasn't a real trend.
- **Final design**: `max(fixed, K × ATR / price)` — ATR only RAISES the displacement bar in high-volatility conditions, never lowers below the proven fixed floor.

**Implementation** (AnalystEngine.cs, drift threshold computation):
```csharp
decimal atrThreshold = _settings.DriftModeAtrMultiplier * _mrAtr.CurrentValue / currentSma;
driftThreshold = Math.Max(_settings.DriftModeMinDisplacementPercent, atrThreshold);
```

Log format: `max(Fixed 0.200%, ATR 0.097%)=0.200%` — shows which side of the max won.

**New setting**: `DriftModeAtrMultiplier` (decimal, default 0m). 0 = use fixed threshold only. >0 = use `max(fixed, K × ATR / price)`. Currently set to 1.0.

**Result**: Neutral on current 7-day data (ATR threshold never exceeds fixed 0.2% on any tested day). Infrastructure is ready for high-vol days where ATR will automatically raise the bar.

#### 2. Drift Trailing Stop (TraderEngine)

**Design**: Drift entries use a separate, wider trailing stop that bypasses the DynamicStopLoss ratchet — identical pattern to TrendRescue trailing stop. Priority chain in `EvaluateTrailingStop`:
1. If `_isDriftPosition && DriftTrailingStopPercent > 0` → use drift stop
2. Else if `_isTrendRescuePosition && TrendRescueTrailingStopPercent > 0` → use TrendRescue stop
3. Else → normal TrailingStopPercent with ratchet

**New field**: `_isDriftPosition` (bool) — set when entering on a drift signal, cleared at all 3 position-clear locations.

**New MarketRegime field**: `IsDriftEntry` (bool) — propagated from AnalystEngine to TraderEngine via the regime channel.

**New setting**: `DriftTrailingStopPercent` (decimal, default 0m). 0 = use normal TrailingStopPercent.

**Bug fix discovered**: BEAR entry setup at ~L2208 never set `_isTrendRescuePosition` — only used `_settings.TrailingStopPercent`. Fixed by adding full drift/rescue priority logic matching the BULL entry pattern.

#### 3. Full Plumbing (10 files changed)

| File | Changes |
|------|---------|
| `MarketRegime.cs` | Added `bool IsDriftEntry = false` parameter |
| `TradingSettings.cs` (MarketBlocks) | Added `DriftModeAtrMultiplier`, `DriftTrailingStopPercent` |
| `TradingSettings.cs` (qqqBot) | Mirror of above |
| `TimeBasedRule.cs` | Added nullable override properties |
| `TimeRuleApplier.cs` | Updated all 5 sections: snapshot, restore, apply, log, SettingsSnapshot |
| `ProgramRefactored.cs` | BuildTradingSettings + ParseOverrides |
| `AnalystEngine.cs` | max() threshold, IsDriftEntry propagation |
| `TraderEngine.cs` | _isDriftPosition field, EvaluateTrailingStop, BULL/BEAR entry, 3 resets |
| `appsettings.json` | New settings added |

### Replay Sweep Results

**Baseline**: $803.55 (Drift Mode ON, new features OFF = DriftTrailingStopPercent=0, DriftModeAtrMultiplier=1.0 with max() = neutral)

**DriftTrailingStopPercent sweep** (7 dates: Feb 9-13, 17, 18):

| DriftStop | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | Feb 17 | Feb 18 | Total | Delta |
|-----------|-------|--------|--------|--------|--------|--------|--------|-------|-------|
| 0% (baseline) | +$33.27 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$41.41 | +$157.08 | **$803.55** | — |
| 0.25% | +$23.37 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$14.06 | +$157.08 | $766.30 | -$37 |
| **0.30%** | +$5.55 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$82.98 | +$157.08 | $817.40 | +$14 |
| **0.35%** | **+$5.55** | **+$12.92** | **+$196.96** | **+$186.92** | **+$174.99** | **+$89.22** | **+$157.08** | **$823.64** | **+$20** |
| 0.40% | +$5.55 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$74.52 | +$157.08 | $808.94 | +$5 |
| 0.50% | +$5.55 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$44.34 | +$157.08 | $778.76 | -$25 |
| 0.80% | +$5.55 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$44.34 | +$157.08 | $778.76 | -$25 |

**Observations:**
- Only **2 of 7 days** are affected by drift trailing stop (Feb 9 and Feb 17). All other days show identical P/L regardless of setting.
- **Feb 9 regression**: Any stop ≥0.3% causes -$28 (binary — same result at 0.3%, 0.35%, 0.4%, 0.5%, 0.8%). A wider stop lets a bad drift entry hold too long.
- **Feb 17 improvement**: Smoothly increases from +$14 at 0.25% to peak at +$89 at 0.35%, then declines. The wider stop lets a good drift entry ride through a pullback.
- **0.35% is the optimum**: Feb 17 gain (+$48) outweighs Feb 9 loss (-$28). Net: +$20 improvement.
- **0.35% is between normal (0.2%) and TrendRescue (0.5%)**: Makes sense — drift entries are less confident than TrendRescue but more confident than normal velocity entries.

**ATR threshold test** (replace fixed with K×ATR/price, no max()):

| Config | Feb 9 | Total | Notes |
|--------|-------|-------|-------|
| ATR replaces fixed, DriftStop=0.8% | -$59.83 | $629.22 | ATR threshold too low (0.097%) |
| max() + DriftStop=0.8% | +$5.55 | $694.60 | Fixed floor restored |
| max() + DriftStop=0% | +$33.27 | $803.55 | Identical to baseline (ATR neutral) |

The ATR threshold at K=1.0 produces 0.097% on current data — always below the 0.2% fixed floor. The max() design makes it invisible on normal-vol days but will automatically protect on high-vol days when ATR spikes.

### Tradier Streaming Volume Research

Fetched Tradier streaming API documentation. Key findings for future CVD implementation:

- **Trade events**: Include `size` (per-trade volume in shares) and `cvol` (cumulative session volume). Direct feed of individual trade prints.
- **Timesale events**: Include `size` + `bid` + `ask` at time of trade. This enables CVD computation by comparing trade price to bid/ask midpoint.
- **CVD computation**: `if (trade_price > midpoint) → CVD += size; else CVD -= size` — standard tick-level CVD.
- **Implication**: Tradier migration (Phase 1 — market data) is prerequisite for CVD-gated Displacement Re-Entry. Alpaca streaming does not provide per-trade volume or bid/ask context needed for CVD.

### Key Findings

1. **max() design is critical for ATR threshold.** Replacing fixed threshold with ATR alone halves the bar on normal days (0.097% vs 0.2%). The asymmetric `max()` preserves the proven floor while adding upside protection for volatile days.

2. **0.35% drift trailing stop is the sweet spot.** Wider than normal (0.2%) to give gradual moves breathing room. Narrower than TrendRescue (0.5%) because drift entries are less directionally confident. Net +$20 improvement across 7 days.

3. **Two-day overfitting risk.** Only Feb 9 and Feb 17 are affected by drift stop changes. The 0.35% value is optimal on this sample but should be monitored on future data.

4. **BEAR entry had missing TrendRescue handling.** Fixed as part of this implementation — BEAR entry setup now has full drift/rescue/normal stop priority logic matching BULL.

5. **Drift trailing stop must use max(drift, phase) — not drift alone.** Initial implementation used `DriftTrailingStopPercent` directly. During OV where `TrailingStopPercent=0.50%`, the drift stop of 0.35% was actually *tighter* — the "wider stop" label was wrong. Fixed by applying `Math.Max(DriftTrailingStopPercent, TrailingStopPercent)` at all 5 usage sites (EvaluateTrailingStop, BULL entry, BEAR entry). Log now shows `(drift=0.35%, phase=0.50%, max applied)`.

6. **Feb 9 $5.55 cliff explained: DynamicStopLoss ratchet skip.** The drift/TrendRescue code path intentionally skips the DynamicStopLoss ratchet (which tightens the stop as profit grows: 0.3%→0.15%, 0.5%→0.10%, 0.8%→0.08%). In the baseline (DriftTrailingStopPercent=0), the drift entry falls through to the normal code path where the ratchet runs, locks in profit, and fires the stop at $50.49 (+$43.56). With DriftTrailingStopPercent>0, the ratchet is skipped, the wide stop doesn't fire, and the dynamic exit timer (ScalpWait/TrendWait) fires at $50.34 (+$15.84) — $28 worse. This is a design tradeoff, not a bug: ratchet skip prevents churn on gradual moves (Feb 17: +$48) at the cost of not locking in profits on reversals (Feb 9: -$28). Net is still +$20.

### Current Settings
```json
"DriftModeEnabled": true,
"DriftModeConsecutiveTicks": 60,
"DriftModeMinDisplacementPercent": 0.002,
"DriftModeAtrMultiplier": 1.0,
"DriftTrailingStopPercent": 0.0035,
"DisplacementReentryEnabled": true,
"DisplacementReentryPercent": 0.005,
"DisplacementAtrMultiplier": 2.0,
"DisplacementChopThreshold": 40,
"DisplacementBbwLookback": 20,
"EnableAdaptiveTrendWindow": false
```

---

### Session: Feb 19, 2026 — Regime-Validated Displacement Re-Entry

**Context**: Research AI proposed using price-derived proxies for conviction (CHOP + BBW) to bypass the lack of volume data from Alpaca streaming. The old displacement re-entry was a blind percentage check. Upgraded to ATR-based displacement + regime validation.

**Changes Made**:
1. **ATR-based displacement threshold**: `Abs(price - stopOutPrice) > DisplacementAtrMultiplier × ATR` (falls back to fixed `DisplacementReentryPercent` when ATR unavailable)
2. **Regime validation gate**: Displacement only fires if `CHOP(14) < DisplacementChopThreshold` (trending) **OR** `BBW > SMA(BBW, DisplacementBbwLookback)` (volatility expanding)
3. **BBW SMA tracking**: `IncrementalSma` fed from `StreamingBollingerBands.Bandwidth` on each candle completion
4. **One-shot guard**: `_displacementConsumedThisPhase` prevents cascading re-entries (same pattern as drift mode)
5. **Indicators-required**: Displacement blocked during first ~7 min while CHOP/BBW warm up (no bypass of regime validation)
6. **`IsDisplacementReentry` flag** added to `MarketRegime` record for TraderEngine awareness

**Files modified** (both repos):
- `TradingSettings.cs` (both) — `DisplacementAtrMultiplier`, `DisplacementChopThreshold`, `DisplacementBbwLookback`
- `TimeBasedRule.cs` — nullable overrides for new settings
- `MarketRegime.cs` — `IsDisplacementReentry` field
- `AnalystEngine.cs` — BBW SMA init/feed, regime-validated displacement logic, one-shot guard, indicators-required, reset locations
- `TimeRuleApplier.cs` — all 5 sections (snapshot, restore, apply, log, SettingsSnapshot)
- `ProgramRefactored.cs` — BuildTradingSettings + ParseOverrides
- `appsettings.json` — new settings

**7-Day Replay Results** (Feb 9-13, 17, 18):

| Config | Feb 9 | Feb 10 | Feb 11 | Feb 12 | Feb 13 | Feb 17 | Feb 18 | Total | vs Baseline |
|--------|-------|--------|--------|--------|--------|--------|--------|-------|-------------|
| Baseline (off) | +$5.55 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$89.22 | +$157.08 | **$823.64** | — |
| Enabled (CHOP<50, bypass) | -$95.65 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$89.22 | +$157.08 | $722.44 | -$101 |
| Enabled (CHOP<40, one-shot, bypass) | +$7.49 | +$12.92 | +$196.96 | +$186.92 | +$174.99 | +$89.22 | +$157.08 | $825.58 | +$2 |
| **Enabled (CHOP<40, one-shot, require indicators)** | **+$9.43** | **+$12.92** | **+$196.96** | **+$186.92** | **+$174.99** | **+$89.22** | **+$157.08** | **$827.52** | **+$4** |

**CHOP Threshold Sweep** (with one-shot + indicators-required):

| CHOP Threshold | Total |
|----------------|-------|
| <30 | $827.52 |
| <35 | $827.52 |
| <38 | $827.52 |
| <40 | $827.52 |
| <45 | $827.52 |
| <50 | $827.52 |
| <55 | $827.52 |

**ATR Multiplier Sweep** (with CHOP<40, one-shot, indicators-required):

| ATR× | Total |
|------|-------|
| 1.5 | $827.52 |
| 2.0 | $827.52 |
| 2.5 | $827.52 |
| 3.0 | $827.52 |
| 4.0 | $827.52 |

**Key Findings**:

1. **One-shot guard is critical.** Without it, stop-out → displacement → stop-out → displacement cascades destroy P/L. Feb 9 went from +$5.55 to -$95.65 without the guard. With one-shot: +$9.43.

2. **Indicators-required prevents unvalidated entries.** The early-phase entries (before CHOP/BBW warm up after ~14 candles) bypass regime validation. Blocking these gave a further +$2 improvement on Feb 9.

3. **CHOP and ATR multiplier are invariant on this dataset.** BBW expansion (`BBW > SMA(BBW)`) alone validates all entries that fire. The OR logic makes CHOP redundant when BBW passes. This is likely due to limited test data (7 days) — keep both gates for robustness.

4. **Displacement trades are mostly P/L-neutral.** Fires on 6/7 days but only changes P/L on Feb 9 (+$3.88). On other days the entry/exit prices are near-breakeven. This means the feature is safe (harmless when wrong) but modestly helpful.

5. **Feb 9 winning trade anatomy**: CHOP=37.3 (trending, < 40), displacement 2.90 > 2.0×ATR=2.87, bought TQQQ at $51.32, sold at $51.34, profit +$3.88.

6. **Research AI was right about CHOP < 40.** Feb 9 triggers with validated CHOP=37.3 and the Feb 9 entries at CHOP=43-49 (which cascaded under the old code) would have been correctly rejected by CHOP<40 + one-shot.