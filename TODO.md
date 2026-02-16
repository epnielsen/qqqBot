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

- [x] **Research and implement barcoding/chopping mitigation**
  **IMPLEMENTED (2026-02-15)**: Added MACD Momentum Layer with three independently-toggleable roles:
  1. **Trend Confidence Boost** — MACD histogram confirms direction, rescues entries that fail velocity gate
  2. **Exit Accelerator** — shortens neutral timeout when MACD histogram diverges from position direction
  3. **Entry Gate** — suppresses new entries when histogram flatlines (barcoding detection)
  
  Disabled by default (`Macd.Enabled = false`). Alternate config: `appsettings.macd.json`.
  CLI quick-enable: `--macd`. Per-phase overridable via TimeRules.
  
  **New files**: `IncrementalEma.cs`, `MacdCalculator.cs` (MarketBlocks.Trade.Math),
  `MacdConfig` class (TradingSettings), `appsettings.macd.json`.
  
  **Status**: Code complete, tests passing. **Tuning complete (2026-02-15): MACD provides no net benefit in current boolean implementation — see tuning results below and EXPERIMENTS.md Session 2026-02-15 (MACD Tuning).**
  - Remaining sub-items (NEUTRAL counting, volatility compression, range-bound patterns) may still be worth exploring independently of MACD.

  **Tuning Results Summary (2026-02-15)**:
  - **Baseline**: No-MACD = +$608.90 (38 trades, 5 days). MACD-default (all roles on) = +$405.15 (55 trades) → **-$203.75 worse**.
  - **Role isolation**: Gate-ONLY = +$608.90 (completely inert, blocks zero trades). Accel-ONLY = +$565.29 (-$43). Boost-ONLY = +$447.21 (+15 bad trades).
  - **Best isolated**: Accel-Only Wait=90s = +$598.27 (-$10.63 vs no-MACD). Best Boost+Gate DZ=0.12 = +$541.99.
  - **Gate-handoff sweep** (lower velocity to let Gate filter barcoding): Gate remains completely inert at ALL velocity × dead zone combinations tested (velocity 0.000004–0.000015, dead zone 0.01–0.12). The MACD histogram values at entry decision time simply don't fall within blocking range.
  - **Conclusion**: Current velocity filter (0.000015) is highly effective at preventing barcoding. MACD EntryGate cannot replicate this function because histogram scale doesn't match the entry decision context. MACD left disabled (`Macd.Enabled = false`).

## MACD Architecture Redesign — Weighted Momentum Score

- [ ] **Replace boolean MACD role overrides with a weighted momentum scoring system**
  **Discovered (2026-02-15)**: The three MACD roles (TrendBoost, EntryGate, ExitAccelerator) interact poorly because they make binary yes/no decisions that can override each other:

  **The Conflict (code: `AnalystEngine.cs` lines ~716-743)**:
  - **Role 1 (TrendBoost)**: If `slopeFailed` and `histogram > TrendBoostThreshold` → sets `trendRescue = true` (ENTER)
  - **Role 3 (EntryGate)**: If `|histogram| < EntryGateDeadZone` → forces `trendRescue = false` AND sets `isStalled = true` (BLOCK)
  - **Current precedence**: Gate runs after Boost and resets `trendRescue = false`, so Gate technically wins. But:
    - The threshold geometry creates a narrow "dead band" (histogram between DeadZone and BoostThreshold) where neither role fires — leaving the decision to velocity alone
    - The histogram scale at entry time rarely falls in the EntryGate dead zone range, making the Gate completely inert in practice (confirmed across 42 velocity × dead zone combinations)
    - The binary nature means there's no "partial confidence" — at any given tick, you're either 100% blocked or 100% allowed

  **Proposed Redesign — Momentum Score**:
  Replace the three boolean roles with a single **weighted momentum score** that influences entry/exit decisions mathematically:

  ```
  MomentumScore = w1 × NormalizedHistogram + w2 × HistogramSlope + w3 × SignalLineDirection
  ```

  Where:
  - `NormalizedHistogram` = histogram / recent_range (scaled to -1..+1 relative to recent behavior)
  - `HistogramSlope` = rate of change of histogram (detecting momentum *acceleration*)
  - `SignalLineDirection` = MACD line vs signal line convergence/divergence rate
  - Weights `w1, w2, w3` are tunable and can vary per phase

  **How it would be used**:
  - **Entry decision**: `MomentumScore` would be a continuous factor in the entry confidence calculation, not a binary gate. Higher score = more willingness to enter. Near-zero score = high reluctance but not absolute block.
  - **Exit decision**: Negative `MomentumScore` relative to position direction would continuously shorten the neutral timeout (proportional, not binary). Score of -0.8 might cut timeout to 20%, while -0.2 might only cut to 80%.
  - **Barcoding detection**: Instead of `|histogram| < deadZone`, use `abs(MomentumScore) < threshold` + `histogram_range_over_N_seconds < compressionThreshold`. This captures actual flatline behavior rather than just "histogram is small right now."

  **Key advantages over current implementation**:
  1. No boolean conflict between roles — single score incorporates all information
  2. Normalized histogram automatically adapts to different market regimes (high-vol days have bigger histograms)
  3. Histogram slope detects *transitions* (momentum building/decaying) rather than just current state
  4. Continuous influence avoids the "completely inert or completely blocking" failure mode
  5. Barcoding detection via range compression is fundamentally more robust than dead zone thresholding

  **Implementation scope**: Medium-large. Requires:
  - New `MacdMomentumScorer` class with histogram normalization window and slope calculation
  - Replace 3 role checks in `AnalystEngine.DetermineSignal` with score-based logic
  - Replace binary accelerator in `TraderEngine.HandleNeutralAsync` with proportional timeout
  - New settings: weights, normalization window, compression threshold
  - Extensive replay testing to tune weights
  - Backward-compatible: could coexist with current boolean roles behind a `MacdScoringMode: "boolean" | "weighted"` setting

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

  *Part 1 — Rolling leverage ratio (QQQ % move vs ETF % move over same time window)*:
  - 5s windows: TQQQ/QQQ median ratio 1.0–2.1 (wildly unreliable, far from 3x)
  - 10s windows: median 2.0–2.5 (still below 3x)
  - 30s windows: median 2.6–2.8 (approaching 3x)
  - 60s windows: median 2.86–3.02 (reliable 3x)
  - **Insight**: 3x leverage ratio only holds reliably at 60s+ timescales. Sub-10s moves show enormous variance.

  *Part 2 — Price response lag (impulse-response)*:
  - At instant QQQ completes a >0.03% move over 10s, TQQQ has achieved:
    - Median: 85–94% of expected 3x (varies by day)
    - p10: 17–52% (worst decile significantly lags)
    - Catch-up to 90%: 43–57% immediate, 55–71% within 5s, 75–83% within 30s, 17–23% never within 30s
  - **Insight**: Real lag exists on fast moves — ETFs need seconds to catch up. But this is at the tick level, not at the stop-trigger timescale.

  *Part 3 — Stop-trigger scenario (THE MONEY QUESTION)*:
  When QQQ drops 0.2% from HWM, TQQQ_drop / QQQ_drop ratio:
  | Date | n | Mean | Median | Min | Max |
  |------|---|------|--------|-----|-----|
  | Feb 9 | 7 | 2.950 | 2.889 | 2.744 | 3.154 |
  | Feb 10 | 13 | 2.933 | 2.973 | 2.570 | 3.132 |
  | Feb 11 | 24 | 2.929 | 3.001 | 2.428 | 3.287 |
  | Feb 12 | 30 | 2.992 | 3.011 | 2.543 | 3.378 |
  | Feb 13 | 29 | 2.913 | 2.928 | 2.266 | 3.451 |

  When QQQ drops 0.5% from HWM (larger move = even tighter):
  | Date | n | Mean | Median | Min | Max |
  |------|---|------|--------|-----|-----|
  | Feb 10 | 3 | 2.966 | 2.953 | 2.946 | 3.000 |
  | Feb 11 | 4 | 3.007 | 3.032 | 2.965 | 3.053 |
  | Feb 12 | 6 | 3.047 | 3.061 | 3.024 | 3.071 |
  | Feb 13 | 5 | 3.007 | 2.960 | 2.921 | 3.138 |

  **Key conclusions**:
  1. At the 0.2% stop level, TQQQ drop ratio is 2.9–3.0x median — very close to theoretical 3x
  2. At the 0.5% stop level, ratio is even tighter (2.95–3.06x)
  3. By the time a trailing stop fires (requires sustained movement to 0.2–0.5%), ETFs have already caught up
  4. Switching to ETF-based stops would make negligible difference for QQQ/TQQQ/SQQQ
  5. The mixed domain (QQQ stops + ETF tier triggers) works correctly in practice
  6. **This conclusion does NOT extend to less-liquid pairs** — see RKLB TODO below

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
     - A lag-adjusted stop threshold (e.g., stop at 0.15% QQQ instead of 0.2% to account for ETF overshoot)
     - Real-time leverage ratio monitoring to detect when ETFs are "stale"
  4. May also need to evaluate whether signal generation should use ETF prices directly for illiquid pairs

## Phase Profit Target

- [ ] **Implement per-phase profit targets that stop trading within a phase but allow next phase to trade**
  **Motivation (2026-02-14 analysis)**: Current `DailyProfitTarget` fires on combined equity (realized + unrealized) every tick when `DailyProfitTargetRealtime=true`, and applies globally — once triggered, it stops all trading for the day. The problem isn't *what* it measures, but that it's a single session-wide threshold with no phase awareness. Analysis showed that on Feb 9, OV phase made +$62 but continued Base phase trading eroded it to +$16 (–$46 given back). Feb 10 similarly: OV +$36, full day only +$19. On these days, session equity never reached the daily target ($175), so the daily trailing stop never armed — and there was no mechanism to protect the OV gains from erosion. A phase-level target could preserve OV gains on weak days while allowing further trading on strong days.

  **How the daily target actually works** (validated 2026-02-15):
  - `DailyProfitTargetRealtime=true`: checks `RealizedSessionPnL + (currentETFPrice - avgEntry) × shares` every tick
  - When combined P/L first reaches `StartingAmount × DailyProfitTargetPercent / 100` ($175): arms a trailing stop at `P/L × (1 - 0.3/100)`
  - Ratchets stop up on new equity peaks; triggers liquidation when equity drops below stop
  - Code: `TraderEngine.ProcessRegimeAsync` lines ~1535-1610 (MarketBlocks)

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
