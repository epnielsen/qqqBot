# qqqBot — Future Tasks

## Replay / Simulation

- [x] **Improve SimulatedBroker fill realism**
  ~~The simulated broker fills orders at the next recorded tick price, which can gap significantly from the decision-tick price.~~
  **DONE (2026-02-11)**: Added `HintPrice` field to `BotOrderRequest`. TraderEngine sets it to the decision-tick price at all 6 order creation sites. SimBroker priority: `HintPrice > LimitPrice > _latestPrices`. 4-day replay P/L improved from -$105.38 to -$34.69.

## Deterministic Replay

- [ ] **Serialize analyst→trader tick processing for deterministic replays**
  Replay results are currently non-deterministic across runs. The analyst and trader engines consume market ticks from bounded channels on separate threads. At `speed=0`, the OS thread scheduler determines which engine processes a given tick first, causing different signal/trade timing each run. Observed variance: identical replay date can produce +$135.94 (4 trades, daily target triggered) or -$47.56 (12 trades, target never reached) depending on scheduling luck. The TimeRuleApplier global-max-time fix eliminated the *settings corruption* source of non-determinism, but thread-scheduling variance remains. Fix: in replay mode only, process ticks through a single-threaded pipeline (analyst produces signal → trader acts on it) rather than parallel consumers, so every run produces identical results. Not needed for live trading where real-time parallelism is correct.

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
