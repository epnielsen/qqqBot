# qqqBot — Future Tasks

## Replay / Simulation

- [x] **Improve SimulatedBroker fill realism**
  ~~The simulated broker fills orders at the next recorded tick price, which can gap significantly from the decision-tick price.~~
  **DONE (2026-02-11)**: Added `HintPrice` field to `BotOrderRequest`. TraderEngine sets it to the decision-tick price at all 6 order creation sites. SimBroker priority: `HintPrice > LimitPrice > _latestPrices`. 4-day replay P/L improved from -$105.38 to -$34.69.

- [x] **Make market data directory configurable**
  ~~Recorded data stored under `bin/Debug/net10.0/data/` — destroyed by `dotnet clean`.~~
  **DONE (2026-02-13)**: Added `MarketDataDirectory` setting to `appsettings.json` (under `TradingBot`). Default: `C:\dev\TradeEcosystem\data\market`. Wired through all three consumers: ProgramRefactored (replay mode), MarketDataRecorder (live recording), HistoricalDataFetcher (--fetch-history). Existing data files copied to the new location.

## Deterministic Replay

- [x] **Serialize analyst→trader tick processing for deterministic replays**
  ~~Replay results are currently non-deterministic across runs.~~ **DONE (2026-02-12)**: Both channels are now bounded(1) in replay mode — regime channel (analyst→trader) and price channel (replay source→analyst). This creates a serialized pipeline where each tick is fully processed through analyst→trader before the next enters. ClockOverride now advances on the analyst's consumer side (not the replay source's producer side), preventing clock desync during trader delays. Live mode unchanged (unbounded channels, real-time parallelism).

- [x] **Skip Brownian bridge interpolation for recorded tick data**
  ~~Replay of recorded trading days injected ~60K synthetic ticks via Brownian bridge, distorting SMA calculations and shifting signals by ~2 minutes vs live. This caused a $425 P/L gap (replay -$254 vs live +$171).~~ **DONE (2026-02-12)**: Added `skipInterpolation` flag to `ReplayMarketDataSource` with auto-detection via `IsHighResolutionData()` — samples the first 100 rows of the CSV and skips interpolation when avg tick gap < 10s. Recorded data (avg gap ~3s) now replays raw; historical API data (60s bars) still uses the bridge. Gap closed from $425 to ~$23.

- [x] **Peak/trough P/L watermarks in replay summary**
  **DONE (2026-02-12)**: `SimulatedBroker.UpdatePrice` now tracks real-time equity (cash + unrealized) on every tick and records the high/low watermarks with timestamps. Displayed in the replay summary as Peak P/L and Trough P/L with Eastern time.

- [x] **Clean replay shutdown at session end**
  ~~Replay hung for 30s after 16:00 ET because `ProcessMarketDataLoop` returned but `SubscribeAsync` was still blocked writing to the bounded(1) channel.~~ **DONE (2026-02-12)**: AnalystEngine now completes the price channel writer at session end in replay mode. ReplayMDS catches `ChannelClosedException` and exits cleanly. No more timeout or "HANGING" warnings.

## Flaky Test

- [x] **Fix `DynamicExit_HighSlope_UsesTrendTimeout` test flakiness**
  ~~This test in `MarketBlocks.Bots.Tests/TraderEngineTests.cs` passes when run in isolation but frequently fails when run alongside other tests (e.g., full `dotnet test`).~~
  **DONE (2026-02-13)**: Root cause was that `liquidationTriggered` was set on **any** sell order, including the graceful shutdown liquidation that runs after `cts.Cancel()`. When tests run in parallel under load, the engine's `GracefulShutdownAsync` would always liquidate the open position, triggering a false positive. Fixed by adopting the same `isCancelled` guard pattern used by the companion tests (`DynamicExit_NegativeHighSlope_UsesTrendTimeout`, etc.) — only sell orders that occur *before* the test sets `isCancelled = true` are counted as failures.

## Settings Re-Optimization

- [x] **Re-optimize all trading settings with corrected replay infrastructure**
  **DONE (2026-02-14)**: Full phase-by-phase systematic optimization using automated sweep harness across 5 replay dates (Feb 9-13). Swept 200+ configurations. Results: **-$436 → +$503 = +$939 improvement. All 5 days profitable. Trade count 77→32.**
  - **Base phase**: Vel 0.000008→0.000015, TrendWindow 1800→5400, Trail 0.25%→0.2%, TrendWait 120→180, TrimRatio 50%→75%
  - **OV phase**: Vel 0.000025→0.000015, Trail 0.3%→0.5%
  - **PH phase**: No changes (phase is inert — only 2 carryover trades across 5 days)
  - **Cross-cutting**: All already optimal (DailyTarget=1.5% confirmed critical safeguard)
  - See EXPERIMENTS.md "Session: 2026-02-14" for full sweep data, per-day breakdowns, insights
  - **IOC parameters**: IocLimitOffsetCents, IocRetryStepCents, IocMaxRetries, IocMaxDeviationPercent.
  - Methodology: run parameter sweeps via replay across all available dates (Feb 6, 9, 10, 11+) and select values that maximize aggregate P/L.
  - **NOTE**: Not enough time tonight (2026-02-11). Schedule for a future session.
  - **⚠️ HIGH PRIORITY** — This is the most impactful outstanding item. Should be done immediately after the Feb 13 bug fixes are implemented.

## Tradier Migration (Gradual)

- [ ] **Gradually migrate from Alpaca Paper Trading to Tradier live account**
  The bot currently runs entirely on the Alpaca API targeting an Alpaca Paper Trading account. The intended production destination is Tradier with real money. This will be a **gradual, incremental migration** — swapping one component at a time while the rest remain on Alpaca Paper Trading.

  **Phase 1 — Tradier market data, Alpaca paper orders**
  - Replace Alpaca WebSocket stream with Tradier streaming market data (SIP-quality).
  - Implement `TradierMarketDataSource`.
  - Orders and account state continue on **Alpaca Paper Trading only**.
  - Validate that signals and P/L from Tradier data are comparable to Alpaca data via replay.
  - Note: Tradier does not provide streaming data in their sandbox — this phase requires a live Tradier account with market data subscription.

  **Phase 2 — Full Tradier with safeguards**
  - Switch order execution, account info, equity checks, and position tracking to Tradier simultaneously (these are tightly coupled — orders must match the account they're placed on).
  - Since Tradier has no paper trading/sandbox with streaming, go live on Tradier but with aggressive safeguards:
    - Very small position sizes (e.g., 1 share)
    - Tight daily loss limit
    - Manual monitoring during initial sessions
  - Validate fills, latency, equity reconciliation, and overall behavior against the Alpaca paper baseline.

  **Phase 3 — Tradier full production**
  - Scale up to normal position sizes after confidence is established.
  - Remove Alpaca dependency entirely.

  **Safety guard**: Throughout Phase 1, enforce that **only Alpaca Paper Trading** orders can be placed. Add an explicit safety check (e.g., verify Alpaca base URL is `paper-api.alpaca.markets`, or add a `PaperTradingOnly` config flag) that hard-blocks any accidental live order placement. This guard should only be removed as a deliberate, reviewed step when transitioning to Phase 2.

## Barcoding / Chopping Detection

- [ ] **Research and implement barcoding/chopping mitigation**
  Detect sideways "barcoding" price action before losses mount and mitigate by exiting near center.
  - Consecutive NEUTRAL signal counting
  - Volatility compression detection (narrowing range over lookback window)
  - Range-bound pattern recognition
  - Lookback function to detect barcoding early
  - Mitigation: drop to NEUTRAL, attempt exit near center to minimize loss
  - Needs research into what's actually detectable from the data we have before implementation

  **MACD attempt (2026-02-15)**: A boolean MACD momentum layer with three roles (Trend Boost, Exit Accelerator, Entry Gate) was implemented and tested across ~215 configs / ~350+ replay runs. **No MACD configuration matched or exceeded the no-MACD baseline** (+$608.90, 38 trades). The Entry Gate was completely inert at all settings; Trend Boost was harmful (+15 bad trades); Accel-Only at Wait=90s came closest (-$10.63). Root cause: boolean role overrides competing over shared flags cannot provide nuanced momentum assessment. The velocity filter at 0.000015 already prevents barcoding more effectively. Full code and sweep results preserved on branch `feature/macd_addition_and_tests`. See EXPERIMENTS.md "Session: 2026-02-15 — MACD Momentum Layer Evaluation".
  - Remaining sub-items (NEUTRAL counting, volatility compression, range-bound patterns) are still worth exploring independently of MACD.
  - A fundamentally different MACD approach (weighted momentum scoring with normalized histogram) could also be revisited — see branch for architectural analysis.

## Trailing Stop on Leveraged ETFs

- [x] **Evaluate whether trailing stops should be set on ETF prices rather than benchmark**
  **INVESTIGATED (2026-02-15)**: Empirical analysis of QQQ→TQQQ/SQQQ price response lag across 5 replay dates (Feb 9-13) using `etf_lag_analysis.py`. **Conclusion: current benchmark-based stops are fine for QQQ/TQQQ/SQQQ. No code changes needed.**

  **Architecture reviewed**:
  - Signal generation: QQQ only (AnalystEngine)
  - Trailing stop trigger & distance: QQQ benchmark price (TraderEngine.EvaluateTrailingStop)
  - DynamicStop tier triggers: ETF profit % (mixed domain)
  - P/L calculation: ETF price
  - `_etfHighWaterMark` exists to partially bridge the domain mismatch

  **Empirical findings — 3 analyses across 5 days:**

  *Part 1 — Rolling leverage ratio*: 3x leverage ratio only holds reliably at 60s+ timescales. Sub-10s moves show enormous variance (median 1.0–2.1 at 5s windows vs 2.86–3.02 at 60s).

  *Part 2 — Price response lag (impulse-response)*: At instant QQQ completes a >0.03% move, TQQQ has achieved 85–94% of expected 3x (median). 17–23% of moves never reach 90% within 30s. Real lag exists at tick level.

  *Part 3 — Stop-trigger scenario (THE MONEY QUESTION)*: When QQQ drops 0.2% from HWM, TQQQ_drop/QQQ_drop ratio is 2.9–3.0x median — very close to theoretical 3x. At 0.5%, even tighter (2.95–3.06x). **By the time a trailing stop fires, ETFs have already caught up.**

  **Key conclusions**: Switching to ETF-based stops would make negligible difference for QQQ/TQQQ/SQQQ. The mixed domain architecture works correctly in practice. **This does NOT extend to less-liquid pairs** — see RKLB TODO below.

## Leveraged ETF Lag for Non-QQQ Symbols (RKLB/RKLX/RKLZ)

- [ ] **Investigate price response lag for less-liquid leveraged ETF pairs**
  The QQQ/TQQQ/SQQQ analysis (2026-02-15) showed negligible lag at stop-trigger timescales because QQQ is extremely liquid and TQQQ/SQQQ are among the most-traded ETFs.

  For future symbols like **RKLB** (Rocket Lab) with leveraged ETFs **RKLX** (bull) / **RKLZ** (bear), the situation may be **much worse**:
  - RKLB is far less liquid than QQQ — wider spreads, thinner order books
  - RKLX/RKLZ are niche leveraged products with very low volume
  - Price response lag could be seconds to minutes rather than sub-second
  - The leverage ratio may deviate significantly from 2x during fast moves
  - Benchmark-based trailing stops could fire long before the ETF reflects the move (or vice versa)

  **When to investigate**: Before deploying MarketBlocks on any non-QQQ symbol pair. This requires:
  1. Collect simultaneous tick data for RKLB + RKLX + RKLZ
  2. Rerun `etf_lag_analysis.py` adapted for the 2x leverage ratio
  3. If lag is significant (stop-trigger ratio deviates >10% from 2.0), consider:
     - ETF-based trailing stops instead of benchmark-based
     - A lag-adjusted stop threshold
     - Real-time leverage ratio monitoring to detect when ETFs are "stale"
  4. May also need to evaluate whether signal generation should use ETF prices directly for illiquid pairs

## Phase Profit Target

- [ ] **Implement per-phase profit targets that stop trading within a phase but allow next phase to trade**

## PH Resume Mode

- [x] **Implement PH Resume Mode in TraderEngine**
  **DONE (2026-02-17)**: Implemented on `feature/ph-resume-mode` branches (both repos). `HaltReason` enum replaces volatile `_dailyTargetReached` bool (fixes crash-safety gap). `SetHaltReason()` centralizes halt transitions and arms PH Resume when profit target fires before 14:00 ET. `CheckPhResume()` clears halt on PH phase transition, resets daily target state, and disables `DailyProfitTargetPercent` for the PH session. 7 new unit tests. Feature gated by `ResumeInPowerHour: false` (default off). Replay validation pending.

- [x] **Switch PH TimeRule overrides from OV-lite to Base settings**
  **DONE (2026-02-17)**: PH TimeRule overrides emptied in `appsettings.json` — PH now inherits Base settings. OV-lite was confirmed inert (0 trades across all tested dates, even in trending markets). Done as part of PH Resume Mode implementation on `feature/ph-resume-mode` branch.

## Analyst Phase Reset

- [x] **Implement AnalystPhaseResetMode (None/Cold/Partial)**
  **DONE (2026-02-18)**: Added `AnalystPhaseResetMode` enum and `ColdResetIndicators()`/`PartialResetIndicators()` methods to AnalystEngine. Phase reset fires only at PH entry (`currentPhase == "Power Hour"` guard). 9 new tests, all passing. On `feature/ph-resume-mode` branch (both repos).

- [x] **Run 5-config replay matrix comparing reset modes × stop widths**
  **DONE (2026-02-18)**: Results across Feb 9-13:
  - Config A (None, Base 0.2%): +$421.45 (baseline)
  - Config B (None, Wider 0.35%): **+$483.55** (best simple option)
  - Config C (Cold, Base 0.2%): +$481.74
  - Config D (Partial 120s, Base 0.2%): +$404.65 (worst — rejected)
  - Config E (Cold, Wider 0.35%): **+$496.05** (best overall)
  Wider PH stops consistently beneficial. Cold reset mixed (helps choppy days, hurts trending). Partial reset clearly harmful. See EXPERIMENTS.md "Session: 2026-02-18" for full analysis.

- [ ] **Remove Partial reset mode** — Dead code. Clearly worse than both None and Cold across all test days. Remove `Partial = 2` from enum, delete `PartialResetIndicators()` and `SeedFromTail()` helpers, update tests.

- [x] **Decide final PH config and apply**
  **DECIDED (2026-02-18)**: Keep PH Resume **dormant** (`ResumeInPowerHour=false`, `AnalystPhaseResetMode=None`). The $608.90 baseline WITHOUT PH Resume outperforms EVERY PH Resume config (best was $496.05 = -$113). The daily profit target stopping trading for the day is *protecting* morning gains — PH Resume gives back profits on every day it activates. Regression verified: dormant feature still produces exact $608.90. All code stays for potential future re-evaluation with different PH strategies.

## Choppy Session Strategy (Future Research)

- [ ] **Research fundamentally different strategies for choppy/afternoon sessions**
  The PH Resume experiment proved that the current trend/momentum bot **cannot profitably trade choppy sessions** — not with any combination of settings, stop widths, or analyst reset modes. The daily profit target stopping for the day is the correct behavior for this bot architecture.

  Potential approaches worth researching (these are NOT settings tweaks — they require different strategy logic):
  - **Mean-reversion**: Trade against moves in range-bound conditions (buy dips, sell rips within a band)
  - **Range-bound detection + sit-out**: Detect chop early and refuse to trade until conditions change
  - **Volatility regime switching**: Use realized vol to switch between trend-following and mean-reversion
  - **Reduced position sizing**: Trade PH with smaller positions to limit downside while capturing rare trends
  - **Options-based approaches**: Use spreads that benefit from range-bound conditions (iron condors, etc.)

  This is a significant architectural undertaking — the current AnalystEngine is fundamentally a trend/momentum detector. A choppy-session strategy would likely need a separate engine or a multi-strategy framework.

## PH Data Collection

- [ ] **Collect Feb 20 data** (actual 3rd-Friday monthly OpEx) — critical test date for OpEx PH hypothesis
- [ ] **Collect additional Friday data** over coming weeks to build larger PH sample

  **Quantified opportunity**: Theoretical max across 5 days (best of OV-only vs full-day per day) = +$566 vs current +$549. However, more sophisticated variants could unlock more:
  - Phase-level trailing stop on equity within that phase (like the daily target but scoped to a phase)
  - Reduce position size or tighten stops after a profitable phase rather than full stop
  - Phase carryover: if OV made +$60, start Base with a tighter daily trail to protect it

  **Proposed settings**:
  ```json
  "PhaseProfitTarget": {
    "Enabled": true,
    "OpenVolatility": {
      "TargetPercent": 0.5,
      "TrailingStopPercent": 0.2,
      "Action": "StopPhase"
    },
    "Base": { ... },
    "PowerHour": { ... }
  }
  ```

  **Implementation considerations**:
  - Needs to work with existing `TimeRuleApplier` phase switching
  - Must track per-phase realized P/L separately from session P/L
  - "StopPhase" = go flat and skip remaining phase time, resume at next phase
  - "TightenStops" = reduce `TrailingStopPercent` by X% to protect gains
  - Should interact properly with existing `DailyProfitTarget` (daily takes precedence)
  - Key challenge: unrealized P/L tracking requires equity watermark per phase, not just realized

## Mean Reversion Strategy (Implemented, Dormant)

- [x] **Implement MR infrastructure for choppy session PH trading**
  **DONE (2026-02-16)**: Full MR pipeline implemented across MarketBlocks + qqqBot. StrategyMode enum, BB/CHOP indicators, MR_LONG/MR_SHORT/MR_FLAT signals, %B configurable thresholds, candle-based BB feeding. All dormant by default. See EXPERIMENTS.md "Session: 2026-02-16" for full details.

- [x] **Fix cascading re-entry after MR hard stop**
  **DONE (2026-02-17)**: Added `_mrHardStopCooldown` flag to TraderEngine. After hard stop, MR entry signals are ignored until MR_FLAT resets the cycle. Impact: Config A Feb 12 went from 161 trades/-$2,551 to 13 trades/-$132.

- [x] **Fix trailing stop and trim interference with MR**
  **DONE (2026-02-17)**: Gated trailing stop and trim logic on `regime.ActiveStrategy != StrategyMode.MeanReversion`. MR uses its own exit (%B midline) and loss protection (hard stop). Minimal P/L impact but structurally correct.

- [x] **Comprehensive MR parameter sweep (14+ configs)**
  **DONE (2026-02-17)**: Tested BB windows 20/30/60, multipliers 2.0/2.5/3.0, entries 0.1-0.2, stops 0/0.3/0.5/0.9%, exits 0.4/0.5, PH-cold and full-day pre-warmed modes. **Every configuration loses money.** Best PH-only: -$441 (BB20,3.0). Best full-day MR contribution: -$147 (BB60,3.0). Root cause: BB bandwidth on 1-min candles ($5-20 profit potential) is smaller than round-trip execution costs (~$16 IOC slippage). See EXPERIMENTS.md for full results table.

- [x] **Implement Research AI recommendations (5-min candles + RSI + ATR stops)**
  **DONE (2026-02-19)**: Added StreamingRSI (Wilder's smoothing), ATR-based dynamic stops on QQQ benchmark, RSI(14) entry confirmation filter (oversold<30, overbought>70), 5-min candle aggregation for all MR indicators. 5 new settings, 10 files changed. Sweep R-V (5 configs × 5 dates): Best MR contribution = -$14.21 (90%+ improvement vs A-Q sweep). RSI filter validated as critical (saves $136 in losses). MR fires extremely rarely on 5-min candles — only 2 of 5 days show any activity. **Still net negative — MR remains dormant.** See EXPERIMENTS.md "Session: 2026-02-19".

- [x] **PH Resume + MR investigation**
  **DONE (2026-02-19)**: Tested Config W (PH Resume + MR) vs Config X (PH Resume + Trend) on target-fire days (Feb 11-13). **PH Resume + Trend is catastrophic** (-$189 vs no resume). **PH Resume + MR is net positive** (+$16.64). MR advantage over Trend in PH: $206. MR is the only safe strategy for PH Resume. See EXPERIMENTS.md "Session: PH Resume + MR Investigation".

- [x] **Test Research AI parameter tuning recommendations**
  **DONE (2026-02-16)**: Tested BB(10), mult 1.5/1.8, RSI 35/65 in 5 configs (Y/Z/AA/AB) × 5 dates = 25 runs. **Every recommendation lost money vs Config W**: Y=-$22, Z/AA=-$244, AB=-$206. RSI relaxation (35/65) is the primary destructive factor — enters on premature reversals that haven't bottomed. BB multiplier change (1.5 vs 1.8) has literally zero impact. BB window shrink (20→10) marginally hurts. **Config W (conservative BB20, RSI 30/70, PH Resume) confirmed as best.** See EXPERIMENTS.md "Session: Research AI Tuning Recommendations Sweep".

- [ ] **Fix CHOP override phase-awareness**
  CHOP override currently applies globally across all phases. When enabled, it overrides BaseDefaultStrategy during Base phase, destroying good Trend performance. Should only override within the designated phase (e.g., only override during PH when PhDefaultStrategy=MR). Fix in `AnalystEngine.DetermineStrategyMode()`.

- [x] **Separate BB candle interval from CHOP** *(prerequisite for viable MR)*
  **DONE (2026-02-19)**: `ChopCandleSeconds` now controls aggregation for BB, CHOP, ATR, and RSI together (all share same 5-min candle). Tested in sweep R-V. The wider bands did solve the bandwidth-vs-execution-cost problem, but 5-min candles produce too few signals (BB %B rarely reaches 0.1/0.9 extremes).

- [ ] **Reduce MR execution costs**
  Use market/limit orders instead of IOC for MR entries/exits — MR doesn't need latency-sensitive execution. Or add a BB bandwidth filter (only trade when bandwidth > minimum threshold ensuring profit > cost).

- [ ] **MR + trend slope confirmation**
  Require both BB %B entry signal AND short-term slope alignment (e.g., MR_LONG only when slope is turning up) to reduce counter-trend entries.

  **Key data from analysis**:
  | Day | OV P/L | OV Peak | Base P/L | Full Day | Opportunity |
  |-----|--------|---------|----------|----------|-------------|
  | Feb 9 | +$62 | +$81 | $0 | +$16 | +$46 if stopped after OV |
  | Feb 10 | +$36 | +$38 | +$3 | +$19 | +$17 if stopped after OV |
  | Feb 11 | +$47 | +$56 | +$150 | +$162 | $0 (full day better) |
  | Feb 12 | +$74 | +$81 | +$135 | +$127 | $0 (full day better) |
  | Feb 13 | +$179 | +$182 | -$156 | +$179 | $0 (daily target saved it) |

## Bidirectional Mean Reversion (Research)

- [ ] **Explore bidirectional MR: trade momentum toward BB extremes, not just contrarian**
  Current MR is contrarian-only: MR_LONG at low %B (oversold), MR_SHORT at high %B (overbought). Alternative approach: trade *with* momentum toward BB extremes (SQQQ while dropping toward lower band, TQQQ on bounce from lower band). This would capture the directional move rather than bet on reversal. MR_SHORT already exists and works (BullOnlyMode=false by default). Research on a separate branch (`feature/bidirectional-mr`). Key questions:
  - Does entering with momentum reduce counter-trend risk?
  - Can slope direction + BB %B gradient identify "approaching extreme" entries?
  - How does this interact with the existing MR exit (%B midline)?
  - Does this produce positive expectancy on Feb 9-13 replay data?

## Broker Timeout During OV (2026-02-18)

- [x] **FIX: Market orders timing out during OV opening**
  **FIXED (2026-02-20)**: Feb 17 and Feb 18 both showed identical pattern: plain Market/Day orders placed in the first ~30-60s of OV sat in `Accepted` state for >10s and timed out (`PendingOrderTimeoutSeconds=10`). Root cause: OV config had `UseMarketableLimits=false, UseIocOrders=false`, forcing plain Market orders that Alpaca's opening auction can hold. Fix: changed OV TimeRule overrides to `UseMarketableLimits=true, UseIocOrders=true` (same as Base phase). IOC orders have explicit fill-or-kill semantics that work better during volatile opens.

## Bugs Found (2026-02-20)

- [x] **FIX: TrendRescue deadlock in DetermineSignal()**
  **FIXED (2026-02-20)**: v7 combined fix with maintenance velocity floor + 5x confirmation + 0.5% wider stop (no ratchet) for trendRescue positions. Feb 17 improved from -$22 to +$8. Zero regression on Feb 9-13. See EXPERIMENTS.md "Session: Combined TrendRescue + Wider Stop Fix".

- [ ] **FIX: `--override` CLI flag silently ignored**
  `CommandLineOverrides.cs` has no handler for `--override KEY=VALUE`. All parameter sweeps using this flag produced identical results because overrides were never applied. Need to add generic override handling or document the correct alternative (alternate appsettings files can already be passed on the command line).

## Trailing Stop Adaptation (2026-02-20)

- [x] **Investigate trailing stop behavior during gradual/trendRescue trends**
  **FIXED (2026-02-20)**: Added `TrendRescueTrailingStopPercent` setting (Base: 0.005 = 0.5%). TrendRescue positions use this wider stop with DynamicStopLoss ratchet skipped. Prevents churn cycle on gradual uptrends. See EXPERIMENTS.md v7 results.

- [ ] **Add CycleTracker toggle setting**
  CycleTracker audited 2026-02-20: clean, single influence point with mandatory logging. Not a priority.

## SimulatedBroker Realism (2026-02-17)

- [ ] **SimulatedBroker: Add fill latency + stochastic fill failure**
  Current `SimulatedBroker.SubmitOrderAsync` fills every order instantly. This makes the entire `ProcessPendingOrderAsync` pathway (10s timeout, cancel, reconcile, buy cooldown backoff) dead code in replay. Feb 17 showed a $77.28 live-vs-replay gap caused entirely by a SQQQ order that timed out in live but filled instantly in replay. Enhancement: return `Status = New` initially, resolve to `Filled` after configurable delay, with seeded-RNG probability of fill failure (especially during OV phase where standard limit orders often timeout). Preserves determinism via seed.

## Drift Mode & Displacement Re-Entry (2026-02-18)

- [x] **Implement Drift Mode (velocity-independent entry)**
  **DONE (2026-02-18)**: Added TimeInZone counter for sustained price-above/below-SMA entry. Requires BOTH duration (60 ticks) AND magnitude (≥0.2% displacement from SMA). Per-direction one-shot prevents cycling. Sticky `_isDriftEntry` hold bypasses velocity exit. Settings: `DriftModeEnabled`, `DriftModeConsecutiveTicks`, `DriftModeMinDisplacementPercent`. 7-day result: +$272 improvement (+$531→+$803), Feb 18 fix: -$88→+$157.

- [x] **Implement Displacement Re-Entry (infrastructure)**
  **DONE (2026-02-18)**: Infrastructure built and plumbed. Records `_lastNeutralTransitionPrice` when directional signal exits to NEUTRAL. Can re-enter when price displaces ≥`DisplacementReentryPercent` from that price. **Left disabled** (`DisplacementReentryEnabled: false`) — caused regressions in testing.

- [x] **Remove noisy slope override from DetermineSignal**
  **DONE (2026-02-18)**: Deleted the `_shortTrendSlope` override block that forced `isBullTrend`/`isBearTrend` during warmup. Was proven harmful in adaptive trend experiments.

- [ ] **Tune Drift Mode thresholds further**
  Current defaults (60 ticks, 0.2% displacement) are sweep-validated across 7 days but could benefit from fine-tuning. Consider: different thresholds per phase (OV vs Base), shorter tick window with higher displacement requirement, or longer window with lower displacement.

- [ ] **Evaluate Displacement Re-Entry with different thresholds**
  Global displacement re-entry causes regressions. Consider: OV-only enabling, ATR-scaled threshold instead of fixed %, or combined with drift mode counters.
