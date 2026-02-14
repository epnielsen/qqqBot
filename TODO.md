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

## Trailing Stop on Leveraged ETFs

- [ ] **Evaluate whether trailing stops should be set on ETF prices rather than benchmark**
  Currently, trailing stops are computed on the benchmark (QQQ) price. The actual positions are in 3x leveraged ETFs (TQQQ/SQQQ), so a 0.25% stop on QQQ translates to roughly 0.75% on the ETF — the stop percentage doesn't directly correspond to the P/L protection it provides.
  - Investigate whether setting stops on the ETF price directly would give more intuitive and accurate P/L protection
  - If stops remain on benchmark, consider whether `TrailingStopPercent` should be documented/tuned as "benchmark percent" (and the effective ETF impact noted)
  - The ratchet/DynamicStopLoss tiers use ETF-based profit % but the stop itself is benchmark-based — this mixed domain could lead to unexpected behavior
  - May be addressed during the Settings Re-Optimization phase

## Phase Profit Target

- [ ] **Implement per-phase profit targets that stop trading within a phase but allow next phase to trade**
  **Motivation (2026-02-14 analysis)**: Current `DailyProfitTarget` only fires on realized session P/L and applies globally (stops all trading for the day). Analysis showed that on Feb 9, OV phase made +$62 but continued Base phase trading eroded it to +$16 (–$46 given back). Feb 10 similarly: OV +$36, full day only +$19. A phase-level target could preserve OV gains on weak days while allowing further trading on strong days.

  **Quantified opportunity**: Theoretical max across 5 days (best of OV-only vs full-day per day) = +$566 vs current +$549. However, more sophisticated variants could unlock more:
  - Phase-level trailing stop on unrealized equity (not just realized P/L)
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
