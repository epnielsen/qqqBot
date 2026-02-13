# Copilot Instructions for qqqBot Workspace

## Critical Files — Read Before Making Changes

### EXPERIMENTS.md (root)
**Purpose**: Persistent memory of all tuning experiments, code changes, settings history, and replay test results across AI sessions.

- **Read this file at the start of every session** that involves settings tuning, strategy changes, or replay analysis
- **Update this file** after:
  - Any settings change in `appsettings.json` (even small tweaks)
  - Any code change that affects signal generation, trade execution, or replay infrastructure
  - Running replay tests — log the date, settings, P/L, trade count, and observations
  - Discovering bugs or gotchas — add to Known Issues section
  - Trying an approach that didn't work — add to Failed Experiments section
- **Session Log**: Add a new session entry at the bottom with date, context, changes made, and results
- The goal is that a future AI session can read EXPERIMENTS.md and understand the full history of what was tried, what worked, and what didn't

### TODO.md (root)
**Purpose**: Tracks outstanding tasks, bugs, and future work items.

- **Update this file** when:
  - A task is completed (mark `[x]` with date and brief description of what was done)
  - A new bug or task is discovered during the session
  - Work is deferred or put "on the back burner" — add it here so it's not forgotten
- Check TODO.md at the start of sessions to see what's pending

### README.md (root)
**Purpose**: User-facing documentation of features, configuration, and usage.

- **Update this file** when:
  - New command-line options are added
  - New settings are added to `appsettings.json`
  - The project structure changes
  - New features are added that users need to know about
- Keep the Configuration Reference tables in sync with actual settings

## Architecture Quick Reference

- **Repos**: `qqqBot` (bot app), `MarketBlocks` (shared library — separate repo in same workspace)
- **Pipeline**: `ReplayMarketDataSource` / `StreamingAnalystDataSource` → `Channel<PriceTick>` → `AnalystEngine` → `Channel<MarketRegime>` → `TraderEngine`
- **Replay mode**: Both channels bounded(1) for deterministic serialized pipeline. Clock advances on consumer side.
- **Live mode**: Unbounded channels for real-time parallelism
- **Phases**: Open Volatility (09:30–09:50), Base (09:50–14:00), Power Hour (14:00–16:00)
- **TradingSettings.cs exists in BOTH repos** — must stay in sync. Also update `ProgramRefactored.cs` (`BuildTradingSettings` + `ParseOverrides`) and `TimeRuleApplier.cs` for any new setting.

## Replay System

```bash
# Full day replay at max speed
dotnet run -- --mode=replay --date=YYYYMMDD --speed=0

# Segment replay
dotnet run -- --mode=replay --date=YYYYMMDD --speed=0 --start-time=09:30 --end-time=10:30

# Logs go to external directory (configurable via ReplayLogDirectory in appsettings.json)
```

- Recorded tick data (avg gap <10s) automatically skips Brownian bridge interpolation
- Historical API data (60s bars) uses Brownian bridge for tick expansion
- Replay summary includes peak/trough P/L with timestamps
- Always verify determinism: run 2-3 times and confirm identical P/L

## Testing

```bash
# qqqBot tests (63 tests)
cd qqqBot && dotnet test

# MarketBlocks tests (~530 tests, 1 pre-existing flaky: DynamicExit_HighSlope_UsesTrendTimeout)
cd MarketBlocks && dotnet test
```

## Common Gotchas

1. **Build cache**: After modifying MarketBlocks code, run `dotnet clean` before building qqqBot
2. **TradingSettings sync**: New settings must be added to BOTH `qqqBot/TradingSettings.cs` AND `MarketBlocks.Bots/Domain/TradingSettings.cs`
3. **Config loading**: New settings must also be loaded in `ProgramRefactored.cs` (`BuildTradingSettings` + `ParseOverrides`)
4. **TimeRuleApplier**: New per-phase settings must be added to snapshot/restore/apply logic
5. **Never change more than 1-2 settings at a time** without replay validation
