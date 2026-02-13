# QQQ Trading Bot

A .NET 10 Alpaca Paper Trading Bot implementing a **Hybrid Engine** strategy with velocity detection, trend following, and intelligent profit management via the **Split Allocation Model**.

## ⚠️ PAPER TRADING ONLY

This bot is designed **exclusively for paper trading**. It includes multiple safety checks to reject live trading API keys.

## Features

- **Hybrid Engine**: Combines fast velocity detection with 30-minute trend baseline for "Trend Rescue" entries
- **Stop & Reverse**: Automatically switches between TQQQ (bullish) and SQQQ (bearish) positions
- **Trailing Stops**: Configurable trailing stop-loss with washout latch to prevent whipsaw re-entry
- **Split Allocation Model**: Configurable profit reinvestment for compounding with protected "Bank" for secured profits
- **Position Trimming**: Automatically trims winning positions when momentum fades (locks in gains while riding trends)
- **Low-Latency Mode**: IOC (Immediate-or-Cancel) orders with retry logic for fast execution
- **Hot Start Hydration**: Fetches historical data on startup for immediate trading (no warm-up period)
- **Market Hours Awareness**: Only trades during market hours (9:30 AM - 4:00 PM ET)
- **Secure Credential Storage**: Uses .NET User Secrets for API key storage
- **State Persistence**: Survives restarts with full position and profit tracking
- **Replay Mode**: Deterministic replay of recorded trading days for strategy tuning and validation

## Prerequisites

- .NET 10 SDK
- Alpaca **Paper Trading** account (get one at [alpaca.markets](https://alpaca.markets))
- Optional: FMP API key for historical data fallback

## Setup

### 1. Configure API Keys

Run the setup mode to securely store your credentials:

```bash
dotnet run -- --setup
```

You will be prompted for:
- **Alpaca API Key**: Must start with `PK` (paper trading key)
- **Alpaca API Secret**: Your Alpaca paper trading secret
- **FMP API Key** (optional): For historical data fallback when Alpaca SIP is restricted

### 2. Configure Trading Parameters

Edit `appsettings.json` to customize the bot behavior:

```json
{
  "TradingBot": {
    "BotId": "main",
    "PollingIntervalSeconds": 1,
    "BullSymbol": "TQQQ",
    "BearSymbol": "SQQQ",
    "BenchmarkSymbol": "QQQ",
    "CryptoBenchmarkSymbol": "BTC/USD",
    
    "MinVelocityThreshold": 0.000001,  
    "SMAWindowSeconds": 120,
    "SlopeWindowSize": 15,             
    "ChopThresholdPercent": 0.0005,    
    "MinChopAbsolute": 0.02,
    "TrendWindowSeconds": 1800,

    "SlidingBand": false,
    "SlidingBandFactor": 0.75,
    "NeutralWaitSeconds": 0,
    "WatchBtc": false,
    "MonitorSlippage": true,
    "TrailingStopPercent": 0.002,
    "StopLossCooldownSeconds": 10,
    "UseMarketableLimits": true,
    "MaxSlippagePercent": 0.002,
    "MaxChaseDeviationPercent": 0.003,
    
    "LowLatencyMode": true,
    "UseIocOrders": true,
    "IocLimitOffsetCents": 1,
    "IocMaxRetries": 5,
    "IocRetryStepCents": 1,
    "IocMaxDeviationPercent": 0.005,
    "IocRemainingSharesTolerance": 2,
    "KeepAlivePingSeconds": 5,
    "WarmUpIterations": 10000,

    "StatusLogIntervalSeconds": 5,

    "ProfitReinvestmentPercent": 0.5,
    "EnableTrimming": true,
    "TrimTriggerPercent": 0.015,
    "TrimRatio": 0.33,
    "TrimSlopeThreshold": 0.000005,
    "TrimCooldownSeconds": 120,

    "StartingAmount": 10000
  }
}
```

### Configuration Reference

#### Core Settings
| Parameter | Description | Default |
|-----------|-------------|---------|
| `BotId` | Unique identifier for this bot instance | "main" |
| `LogDirectory` | Directory for log files | `C:\dev\TradeEcosystem\logs\qqqbot` |
| `MarketDataDirectory` | Directory for recorded/historical market data CSVs | `C:\dev\TradeEcosystem\data\market` |
| `PollingIntervalSeconds` | How often to poll for price updates | 1 |
| `BullSymbol` | ETF to buy in bullish trend | TQQQ |
| `BearSymbol` | ETF to buy in bearish trend | SQQQ |
| `BenchmarkSymbol` | ETF used to determine trend | QQQ |
| `StartingAmount` | Fixed dollar amount to trade with | 10000 |

#### Hybrid Engine (Signal Detection)
| Parameter | Description | Default |
|-----------|-------------|---------|
| `SMAWindowSeconds` | Short-term SMA window (seconds) | 120 |
| `TrendWindowSeconds` | Long-term trend baseline (30 min) | 1800 |
| `SlopeWindowSize` | Number of SMA values for slope calculation | 15 |
| `MinVelocityThreshold` | Minimum slope to hold position | 0.000001 |
| `ChopThresholdPercent` | Hysteresis band width (percent) | 0.0005 |
| `MinChopAbsolute` | Minimum hysteresis in dollars | 0.02 |

#### Risk Management
| Parameter | Description | Default |
|-----------|-------------|---------|
| `TrailingStopPercent` | Trailing stop-loss (0 = disabled) | 0.002 (0.2%) |
| `StopLossCooldownSeconds` | Washout latch duration | 10 |
| `MaxSlippagePercent` | Max acceptable slippage | 0.002 |
| `MaxChaseDeviationPercent` | Max price chase before abort | 0.003 |

#### Profit Management (Split Allocation Model)
| Parameter | Description | Default |
|-----------|-------------|---------|
| `ProfitReinvestmentPercent` | Portion of profits to reinvest (0.0-1.0) | 0.5 |
| `EnableTrimming` | Enable automatic position trimming | true |
| `TrimTriggerPercent` | Min unrealized P/L % to trigger trim | 0.015 (1.5%) |
| `TrimRatio` | Fraction of position to sell when trimming | 0.33 (33%) |
| `TrimSlopeThreshold` | Slope must be below this to trim (momentum fading) | 0.000005 |
| `TrimCooldownSeconds` | Minimum seconds between trims | 120 |

#### Low-Latency Mode
| Parameter | Description | Default |
|-----------|-------------|---------|
| `LowLatencyMode` | Enable channel-based reactive pipeline | true |
| `UseIocOrders` | Use IOC limit orders ("sniper mode") | true |
| `IocLimitOffsetCents` | Offset above ask (buy) or below bid (sell) | 1 |
| `IocMaxRetries` | Max retries before fallback to market order | 5 |
| `IocRetryStepCents` | Price step per retry | 1 |
| `IocMaxDeviationPercent` | Max price chase before stopping | 0.005 |

## Trading State

The bot maintains a `trading_state.json` file to persist state across restarts:

```json
{
  "AvailableCash": 10500.00,
  "AccumulatedLeftover": 750.00,
  "IsInitialized": true,
  "LastTradeTimestamp": "2026-01-29T15:30:00Z",
  "CurrentPosition": "TQQQ",
  "CurrentShares": 125,
  "AverageEntryPrice": 82.50,
  "StartingAmount": 10000.00,
  "DayStartBalance": 10000.00,
  "DayStartDate": "2026-01-29",
  "RealizedSessionPnL": 500.00,
  "CurrentTradingDay": "2026-01-29",
  "LastTrimTime": "2026-01-29T14:15:00Z",
  "HighWaterMark": 83.25,
  "TrailingStopValue": 83.08,
  "IsStoppedOut": false,
  "LastAnalystSignal": "BULL"
}
```

### State Fields

#### Cash Management
| Field | Description |
|-------|-------------|
| `AvailableCash` | Working capital available for trading |
| `AccumulatedLeftover` | **Bank** - Protected profits (never used for position sizing) |
| `StartingAmount` | Initial capital configuration |
| `DayStartBalance` | Balance at start of trading day (for daily P/L) |

#### Position Tracking
| Field | Description |
|-------|-------------|
| `CurrentPosition` | Symbol currently held (TQQQ, SQQQ, or null) |
| `CurrentShares` | Number of shares held |
| `AverageEntryPrice` | Cost basis per share |

#### Profit Management
| Field | Description |
|-------|-------------|
| `RealizedSessionPnL` | Realized P/L for current trading day (resets daily) |
| `CurrentTradingDay` | ISO date for day-reset detection |
| `LastTrimTime` | When last trim occurred (cooldown tracking) |

#### Trailing Stop State
| Field | Description |
|-------|-------------|
| `HighWaterMark` | Highest price since entry (for trailing stop) |
| `TrailingStopValue` | Current stop price |
| `IsStoppedOut` | Whether stop-loss was triggered |
| `StoppedOutDirection` | Signal direction when stopped out |
| `WashoutLevel` | Re-entry threshold after stop-out |

**Note**: To reset and start fresh, delete `trading_state.json` or set `IsInitialized` to `false`.

## Replay Mode

Replay recorded trading days to validate strategy changes and tune settings without risking real (paper) money.

### Basic Usage

```bash
# Full day replay at max speed
dotnet run -- --mode=replay --date=20260212 --speed=0

# Segment replay (Eastern time)
dotnet run -- --mode=replay --date=20260212 --speed=0 --start-time=09:30 --end-time=10:30

# Replay at 10x real-time speed
dotnet run -- --mode=replay --date=20260212 --speed=10
```

### How It Works

- **Deterministic pipeline**: Both channels are bounded(1) in replay mode, creating a strict serialized pipeline where each tick fully processes through analyst→trader before the next enters
- **Auto-detection**: `IsHighResolutionData()` samples the first 100 CSV rows. Recorded tick data (avg gap <10s) replays raw; historical API data (60s bars) uses Brownian bridge interpolation for tick expansion
- **SimulatedBroker**: Fills orders with configurable slippage, tracks positions, and computes equity in real-time
- **Replay logs**: Written to a configurable external directory (set `ReplayLogDirectory` in `appsettings.json`)

### Summary Output

After each replay, a summary is printed:

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

### Verifying Determinism

Run the same replay 2-3 times and confirm identical P/L, trade count, and watermarks. The serialized pipeline guarantees identical results for the same input data and settings.

### Replay CLI Options

| Option | Description |
|--------|-------------|
| `--mode=replay` | Enable replay mode |
| `--date=YYYYMMDD` | Date to replay |
| `--speed=N` | Playback speed (0 = max, 1 = real-time) |
| `--start-time=HH:MM` | Start time filter (Eastern) |
| `--end-time=HH:MM` | End time filter (Eastern) |

## Running the Bot

```bash
dotnet run
```

The bot will:
1. Connect to Alpaca Paper Trading
2. Hydrate indicators from historical data (hot start)
3. Wait for market hours (9:30 AM - 4:00 PM ET)
4. Poll prices and calculate signals
5. Execute trades using the Hybrid Engine strategy
6. Manage profits via Split Allocation Model

### Command Line Overrides

```bash
# Trade a different bull ticker (neutral/bear signals go to cash)
dotnet run -- -bull=UPRO

# Trade with a different benchmark
dotnet run -- -benchmark=SPY

# Full override: custom bull and bear tickers
dotnet run -- -bull=UPRO -bear=SPXU -benchmark=SPY

# Enable BTC/USD early trading weathervane
dotnet run -- -usebtc

# Enable BTC correlation (neutral nudge)
dotnet run -- -watchbtc -neutralwait=60

# Enable trailing stop via CLI
dotnet run -- -trail=0.2

# Enable low-latency IOC orders
dotnet run -- -lowlatency -ioc

# Enable slippage monitoring
dotnet run -- -monitor

# Use a different bot ID (for multiple instances)
dotnet run -- -botid=rklb
```

| Option | Description |
|--------|-------------|
| `-bull=TICKER` | Override the bull ETF symbol |
| `-bear=TICKER` | Override the bear ETF symbol (requires -bull) |
| `-benchmark=TICKER` | Override the benchmark symbol |
| `-botid=ID` | Override the bot instance ID |
| `-usebtc` | Enable BTC/USD early trading (9:30-9:55 AM) |
| `-watchbtc` | Enable BTC correlation to nudge NEUTRAL signals |
| `-neutralwait=SECONDS` | Override neutral wait time |
| `-trail=PERCENT` | Set trailing stop (e.g., 0.2 for 0.2%) |
| `-minchop=DOLLARS` | Override minimum chop threshold |
| `-limit` | Enable marketable limit orders |
| `-maxslip=PERCENT` | Set max slippage percent |
| `-lowlatency` | Enable low-latency mode |
| `-ioc` | Enable IOC sniper orders |
| `-monitor` | Enable slippage monitoring |

## Strategy Logic

### Hybrid Engine

The bot uses a **Hybrid Engine** combining fast velocity detection with trend confirmation:

```
VELOCITY DETECTION (Fast):
  Slope = Change in SMA over SlopeWindowSize ticks
  IF Slope > MinVelocityThreshold → Strong upward momentum
  IF Slope < -MinVelocityThreshold → Strong downward momentum

TREND BASELINE (Slow):
  TrendSMA = 30-minute rolling average
  IF Price > TrendSMA → Long-term bullish
  IF Price < TrendSMA → Long-term bearish

SIGNAL GENERATION:
  BULL = Velocity UP or "Trend Rescue" (Price > TrendSMA despite weak velocity)
  BEAR = Velocity DOWN or (Price < TrendSMA with weak velocity)
  NEUTRAL = Neither condition met
```

### Split Allocation Model

When a position is closed with profit:

```
Profit = Proceeds - CostBasis

BankedAmount = Profit × (1 - ProfitReinvestmentPercent)
ReinvestedAmount = Profit × ProfitReinvestmentPercent

Bank (AccumulatedLeftover) += BankedAmount  # Protected forever
AvailableCash += Proceeds - BankedAmount    # Available for trading

BuyingPower = StartingAmount + (RealizedSessionPnL × ProfitReinvestmentPercent)
```

### Position Trimming

Trimming sells a portion of a winning position when momentum fades:

```
IF EnableTrimming AND UnrealizedPnL% > TrimTriggerPercent
   AND Slope < TrimSlopeThreshold (momentum fading)
   AND TimeSinceLastTrim > TrimCooldownSeconds
   AND UnrealizedPnL > 0 (wash sale protection)
THEN:
   Sell TrimRatio of position
   Apply Split Allocation to proceeds
```

## Example Output

```
[10:30:05] QQQ: $485.23 | SMA: $484.50 | BULL | TQQQ x1200 | Depl: $9900.00 | Avail: $100.00 | Bank: $500.00 | Reinv: $250.00 | Eq: $10600.00 | Run: +$200.00 | Day: +$350.00 (3.50%)
```

| Field | Description |
|-------|-------------|
| `Depl` | Deployed capital (cost basis of current position) |
| `Avail` | Available cash for trading |
| `Bank` | Protected profits (AccumulatedLeftover) |
| `Reinv` | Reinvestable profit (RealizedSessionPnL × ReinvestmentPercent) |
| `Eq` | Total equity (cash + position value) |
| `Run` | Unrealized P/L on current position |
| `Day` | Daily realized P/L |

## Project Structure

```
qqqbot/
├── Program.cs              # Entry point and setup
├── ProgramRefactored.cs    # Main bot orchestration with DI
├── CommandLineOverrides.cs # CLI argument parsing
├── TradingSettings.cs      # Configuration model
├── TradingState.cs         # Persisted state model
├── TrailingStopEngine.cs   # Trailing stop-loss logic
├── SimulatedBroker.cs      # Fake broker for replay mode
├── ReplayMarketDataSource.cs # CSV replay with auto-detect interpolation
├── appsettings.json        # Configuration file
├── trading_state.json      # Runtime state (generated)
├── EXPERIMENTS.md           # Tuning experiment history (read before changing settings)
├── TODO.md                  # Outstanding tasks and bugs
└── README.md               # This file
```

## Dependencies

- `MarketBlocks.*` - Trading infrastructure library (Analysis, Bots, Trade, Infrastructure)
- `Alpaca.Markets` - Alpaca Trading API SDK
- `Refit` - REST API client for FMP fallback
- `Microsoft.Extensions.*` - Configuration, DI, Hosting

## Safety Features

1. **API Key Validation**: Rejects any key not starting with "PK" (paper trading)
2. **Paper Environment Only**: Connects exclusively to Alpaca Paper environment
3. **State Persistence**: Survives crashes and restarts
4. **Trailing Stops**: Automatic loss protection
5. **Washout Latch**: Prevents whipsaw re-entry after stop-out
6. **Bank Protection**: Profits in Bank are never risked on new positions

## Disclaimer

This bot is for educational purposes only. Trading involves risk. Past performance does not guarantee future results. Always use paper trading to test strategies before risking real money.
