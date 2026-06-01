# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build -c Release

# Run modes — swing trading (1h setup + 15m entry/exit)
dotnet run -- train        # Swing GA: 26 coins, 1h/15m dual-TF, ~3yr history (~10 min)
dotnet run -- backtest     # Swing backtest: 45 coins, val 20%, 1h/15m dual-TF
dotnet run -- papertrade   # Live swing signals, refreshes every 4h

# Discord bot (requires .env)
.venv/bin/python discord_bot.py
```

No test suite. Validation is done by running `backtest` after any change to `SwingSimulator.cs` or `SwingGenotype.cs`.

## Architecture

### Strategy flow

`SwingSimulator.cs` is the single source of truth for all simulation. It operates on a dual-timeframe setup: 1h candles for regime/setup detection, 15m candles for precise entry/exit execution.

Two strategies, regime-gated:
- **Swing Short** (strong uptrend extended): RSI overbought fade + Break of Structure (lower high) on 1h, ADX ≥ threshold, price > EMA. Entry triggered on 15m confirmation.
- **Swing Long** (moderate uptrend): RSI bounces from oversold zone on 1h while ADX ≥ threshold×0.6 and price > EMA.

Exit uses three layers: hard ATR stop · fixed ATR profit target · trailing ATR stop (armed after `TrailingActivationAtrMult` × ATR move). `MaxHoldCandles` forces close after N 1h bars regardless.

All ATR multiples are in absolute price units (14-period ATR at entry), not percentage — naturally scales to each coin's volatility.

`GetSwingReturns` → used by GA fitness and backtest  
`GetSwingTradeState` → used by papertrade current-state display

### SwingGenotype (13 genes)

All parameters the GA evolves. Key ones:
- `EmaPeriod` / `AdxPeriod` / `AdxThreshold` — regime gate sensitivity
- `RsiOverbought` — short entry sensitivity
- `StopLossAtrMult` — hard stop width (capped at 2.0× ATR)
- `TakeProfitAtrMult` — fixed profit target
- `TrailingActivationAtrMult` / `TrailingStopAtrMult` — trailing stop trigger and width
- `MaxHoldCandles` — max bars before forced exit

### GA fitness

`SwingGA.cs` uses 5-fold walk-forward cross-validation on 1h training candles. Score = `mean_fold_sharpe − 0.5 × std_fold_sharpe`. Minimum 3 trades per fold (daily-equivalent data produces fewer trades than 5m). Final elite rescored on held-out val candles.

Seed auto-screen: if a previous genotype exists with positive fitness, only coins where the seed shows positive expectancy on training data are passed to the GA — prevents diluting the fitness gradient with coins that have no edge.

### Candle fetching

`FetchFifteenMinCandlesCached(symbol, batches)` — fetches 15m candles with disk cache (`candle_cache/{symbol}_15m.csv`). `batches: 113` ≈ 3.2yr. Incremental: only fetches new candles since last cache write.

`FetchSwingCandles(symbol, batches)` — live 4h candles for papertrade warmup (no cache needed; `batches: 1` ≈ 166d is enough for indicator warmup).

### Discord bot

`discord_bot.py` manages a single long-running dotnet subprocess (`papertrade`). It:
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` headers in stdout
- Posts a formatted summary to the output channel each cycle
- Forwards text messages in the command channel to `claude -p` for live code edits, then restarts the process
- Slash commands (`/backtest`, `/train`, etc.) run one-shot via `dotnet exec bin/Release/net10.0/Gravity-gen2.dll <mode>`

Required `.env`:
```
DISCORD_TOKEN=
DISCORD_OUTPUT_CHANNEL=
DISCORD_COMMAND_CHANNEL=
DISCORD_GUILD_ID=          # guild-specific sync (instant); omit for global (up to 1h)
GRAVITY_MODE=              # papertrade (default)
```

### Saved files

- `swing_best_genotype.json` — output of `train`, used by `backtest` + `papertrade`
