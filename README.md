# QQQ Trading Bot

A minimal .NET 10 Alpaca Paper Trading Bot that implements a simple SMA-based Stop & Reverse strategy between TQQQ (Bull ETF) and SQQQ (Bear ETF).

## ⚠️ PAPER TRADING ONLY

This bot is designed **exclusively for paper trading**. It includes multiple safety checks to reject live trading API keys.

## Features

- **SMA-based Strategy**: Uses a 20-period Simple Moving Average on QQQ to determine market trend
- **Stop & Reverse**: Automatically switches between TQQQ (bullish) and SQQQ (bearish) positions
- **Market Hours Awareness**: Only trades during market hours (9:30 AM - 4:00 PM ET)
- **Secure Credential Storage**: Uses .NET User Secrets for API key storage
- **Paper Trading Enforcement**: Validates API keys start with "PK" (paper trading prefix)

## Prerequisites

- .NET 10 SDK
- Alpaca **Paper Trading** account (get one at [alpaca.markets](https://alpaca.markets))

## Setup

### 1. Configure API Keys

Run the setup mode to securely store your Alpaca Paper Trading credentials:

```bash
dotnet run -- --setup
```

You will be prompted for:
- **API Key**: Must start with `PK` (paper trading key)
- **API Secret**: Your Alpaca paper trading secret

### 2. Configure Trading Parameters (Optional)

Edit `appsettings.json` to customize:

```json
{
  "TradingBot": {
    "PollingIntervalSeconds": 60,
    "BullSymbol": "TQQQ",
    "BearSymbol": "SQQQ",
    "BenchmarkSymbol": "QQQ",
    "SMALength": 20,
    "StartingAmount": 10000.00
  }
}
```

| Parameter | Description | Default |
|-----------|-------------|---------|
| `PollingIntervalSeconds` | How often to check the market | 60 |
| `BullSymbol` | ETF to buy in bullish trend | TQQQ |
| `BearSymbol` | ETF to buy in bearish trend | SQQQ |
| `BenchmarkSymbol` | ETF used to determine trend | QQQ |
| `SMALength` | Number of 1-minute bars for SMA | 20 |
| `StartingAmount` | Fixed dollar amount to trade with | 10000.00 |

## Trading State

The bot maintains a `trading_state.json` file to track:

```json
{
  "AvailableCash": 0,
  "AccumulatedLeftover": 45.23,
  "IsInitialized": true,
  "LastTradeTimestamp": "2026-01-06T15:30:00Z",
  "CurrentPosition": "TQQQ",
  "CurrentShares": 125
}
```

| Field | Description |
|-------|-------------|
| `AvailableCash` | Cash available for the next purchase |
| `AccumulatedLeftover` | Cash remaining after buying max shares (accumulates over time) |
| `IsInitialized` | Whether the bot has been initialized with starting amount |
| `LastTradeTimestamp` | When the last trade was executed |
| `CurrentPosition` | Symbol currently held (TQQQ or SQQQ) |
| `CurrentShares` | Number of shares held |

**Note**: To reset and start fresh with the starting amount, delete `trading_state.json` or set `IsInitialized` to `false`.

## Running the Bot

```bash
dotnet run
```

The bot will:
1. Connect to Alpaca Paper Trading
2. Initialize with the configured `StartingAmount` (first run only)
3. Wait for market hours (9:30 AM - 4:00 PM ET)
4. Poll QQQ price every 60 seconds
5. Calculate SMA and determine trend
6. Execute trades using the fixed amount strategy

## Strategy Logic

```
IF QQQ Price > SMA(20):
    Trend = BULLISH
    Target = TQQQ
    
IF QQQ Price < SMA(20):
    Trend = BEARISH
    Target = SQQQ

Position Management:
- If holding opposite position → Liquidate and buy target
- If holding nothing → Buy target
- If holding target → Hold
```

## Example Output

```
[2026-01-06 10:30:00] === QQQ Trading Bot Starting ===
[2026-01-06 10:30:01] Configuration loaded:
[2026-01-06 10:30:01]   Benchmark: QQQ
[2026-01-06 10:30:01]   Bull ETF: TQQQ
[2026-01-06 10:30:01]   Bear ETF: SQQQ
[2026-01-06 10:30:01]   SMA Length: 20
[2026-01-06 10:30:01]   Polling Interval: 60s

[2026-01-06 10:30:02] Connected to Alpaca Paper Trading
[2026-01-06 10:30:02]   Account ID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
[2026-01-06 10:30:02]   Buying Power: $100,000.00
[2026-01-06 10:30:02]   Portfolio Value: $100,000.00

[2026-01-06 10:30:02] === Trading Bot Active ===

[2026-01-06 10:30:05] --- Polling at 10:30:05 ET ---
[2026-01-06 10:30:05] QQQ: $485.23 | SMA(20): $484.50
[2026-01-06 10:30:05] Trend: BULLISH 📈 -> Target: TQQQ
[2026-01-06 10:30:06] [BUY] TQQQ x 1200 @ ~$78.50
[2026-01-06 10:30:06] Order submitted: abc123...
```

## Project Structure

```
qqqbot/
├── Program.cs           # Main bot logic
├── appsettings.json     # Configuration
├── qqqbot.csproj        # Project file
└── README.md            # This file
```

## Safety Features

1. **API Key Validation at Setup**: Rejects any key not starting with "PK"
2. **Runtime Validation**: Re-checks API key on every startup
3. **Paper Environment Only**: Connects exclusively to `Environments.Paper`
4. **Error Handling**: Catches and logs errors without crashing

## Dependencies

- `Alpaca.Markets` - Alpaca Trading API SDK
- `Microsoft.Extensions.Configuration.Json` - JSON configuration
- `Microsoft.Extensions.Configuration.UserSecrets` - Secure credential storage
- `Microsoft.Extensions.Configuration.Binder` - Configuration binding

## Disclaimer

This bot is for educational purposes only. Trading involves risk. Past performance does not guarantee future results. Always use paper trading to test strategies before risking real money.
