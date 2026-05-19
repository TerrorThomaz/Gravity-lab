# Gravity-gen2

Counter-trend crypto short strategy optimised by a Genetic Algorithm. Trained on Bybit USDT perpetuals.

## Strategy

The strategy identifies pumps using a combination of:
- **ADX regime gate** — only enters shorts during trending markets (ADX > threshold)
- **RSI overbought** (≥65) followed by a fade below the threshold
- **Break of Structure** — a lower high forms after the pump peak
- **EMA extension** — the pump high was extended above the EMA
- ATR-scaled trailing stop and optional DCA levels

Three modes are available:

| Command | What it does |
|---|---|
| `dotnet run -- train` | GA training on 1yr WIF data, seeds from last saved genotype. Saves `best_genotype.json`. |
| `dotnet run -- backtest` | 1yr out-of-sample backtest on 14 coins using the frozen genotype. |
| `dotnet run -- papertrade` | Live regime + open position state per coin using recent candles. |

## Results (latest backtest — 74 days, 14 coins)

```
Total return:    +8.43%
WinRate:         68.9%
Sharpe:          22.98
Sortino:         32.99
Calmar:           7.63
Profit Factor:    5.50
Max DD:           0.06%
Max Consec L:         7
Trades/day:        34.9
```

All 14 coins generalise from WIF-only training (Sharpe 4.35–8.28 across coins).

## Setup

### Requirements
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Bybit account (no API key needed for data fetching — public endpoints)

### Configuration

Copy `secrets.json.example` to `secrets.json` and fill in your Bybit API credentials if you intend to place live orders (not required for train/backtest/papertrade signal display):

```json
{
  "ApiKey": "YOUR_BYBIT_API_KEY",
  "ApiSecret": "YOUR_BYBIT_API_SECRET"
}
```

### Run

```bash
# First time — train on 1yr WIF data (~2 minutes)
dotnet run -- train

# Backtest the trained genotype on 14 coins (~15 minutes)
dotnet run -- backtest

# Check live signals
dotnet run -- papertrade
```

## Architecture

| File | Role |
|---|---|
| `Genotype.cs` | 14-gene parameter set including ADX regime genes |
| `Simulator.cs` | `GetUnifiedReturns()` — full-candle regime-switching simulator; KPI metrics |
| `GA.cs` | Genetic Algorithm with walk-forward cross-validation; accepts seed genotype |
| `PumpSegmenter.cs` | Legacy pump-window segmenter (kept for reference) |
| `Program.cs` | CLI entry point for train / backtest / papertrade modes |
| `best_genotype.json` | Frozen genotype from last training run |

## Coins

Training: WIFUSDT (1yr, ~106k candles)

Backtest: WIFUSDT, SOLUSDT, MEMEUSDT, ATOMUSDT, DOGEUSDT, 1000BONKUSDT, XRPUSDT, ETHUSDT, AVAXUSDT, BNBUSDT, LINKUSDT, ADAUSDT, 1000PEPEUSDT, 1000FLOKIUSDT
