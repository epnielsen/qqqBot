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

- [ ] **Fix `DynamicExit_HighSlope_UsesTrendTimeout` test flakiness**
  This test in `MarketBlocks.Bots.Tests/TraderEngineTests.cs` passes when run in isolation but frequently fails when run alongside other tests (e.g., full `dotnet test`). Likely caused by shared static state or timing sensitivity. Observed across multiple sessions (2026-02-12). Needs investigation into test isolation — possibly shared `TradingSettings`, `FileLoggerProvider.ClockOverride`, or `TimeRuleApplier` state leaking between tests.

## Settings Re-Optimization

- [ ] **Re-optimize all trading settings with corrected replay infrastructure**
  Now that HintPrice, per-caller HWM, bounded channel deadlock, and daily target trailing stop are fixed, all tunable parameters need to be re-optimized against multi-day replay data. Priority areas:
  - **Open Volatility (OV) phase parameters**: MinVelocityThreshold, SMAWindowSeconds, ChopThresholdPercent, MinChopAbsolute, TrendWindowSeconds — these were tuned before the replay fixes and may be sub-optimal.
  - **Trailing stop & exit parameters**: TrailingStopPercent, ScalpWaitSeconds, TrendWaitSeconds, DynamicStopLoss tiers.
  - **DailyProfitTargetTrailingStopPercent**: Currently 0.3%. Analysis of Feb 11 shows this is near-optimal for that day (captured +$135.94 near the $156.38 peak; widening to 5% collapsed to -$113.63 as the market reversed). Needs validation across more trading days to confirm 0.3% isn't too tight for volatile/choppy sessions.
  - **Trim parameters**: TrimTriggerPercent, TrimSlopeThreshold, TrimCooldownSeconds, TrimRatio.
  - **IOC parameters**: IocLimitOffsetCents, IocRetryStepCents, IocMaxRetries, IocMaxDeviationPercent.
  - Methodology: run parameter sweeps via replay across all available dates (Feb 6, 9, 10, 11+) and select values that maximize aggregate P/L.
  - **NOTE**: Not enough time tonight (2026-02-11). Schedule for a future session.
