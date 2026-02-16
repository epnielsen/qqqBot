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

  **Key data from analysis**:
  | Day | OV P/L | OV Peak | Base P/L | Full Day | Opportunity |
  |-----|--------|---------|----------|----------|-------------|
  | Feb 9 | +$62 | +$81 | $0 | +$16 | +$46 if stopped after OV |
  | Feb 10 | +$36 | +$38 | +$3 | +$19 | +$17 if stopped after OV |
  | Feb 11 | +$47 | +$56 | +$150 | +$162 | $0 (full day better) |
  | Feb 12 | +$74 | +$81 | +$135 | +$127 | $0 (full day better) |
  | Feb 13 | +$179 | +$182 | -$156 | +$179 | $0 (daily target saved it) |
