# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build -c Release

# Run modes
dotnet run -- train         # GA on WIF only (fast, ~2 min)
dotnet run -- trainmulti    # GA on 5 diverse coins (robust, ~5 min)
dotnet run -- backtest      # 1yr out-of-sample on 14 coins (~15 min)
dotnet run -- papertrade    # live signals, refreshes every 5 min
dotnet run -- livetrain     # 20-genotype population on rolling live data, evolves hourly
dotnet run -- status        # portfolio P&L with fees (0.21%/trade) + reinvestment

# Discord bot (requires .env)
.venv/bin/python discord_bot.py
```

No test suite. Validation is done by running `backtest` after any change to `Simulator.cs` or `Genotype.cs`.

## Architecture

### Strategy flow

`RunUnified` (in `Simulator.cs`) is the single source of truth for all simulation. It runs the full candle series with a regime gate:
- **TrendingUp** (ADX ≥ threshold AND price > EMA): pump-short strategy fires
- **Ranging / TrendingDown**: flat — no trades

The pump-short entry requires all of: RSI overbought then fading, Break of Structure (lower high), EMA extension, volume spike. Exit uses ATR-scaled trailing stop with optional DCA and break-even arm.

`GetUnifiedReturns` → used by GA fitness, backtest, livetrain  
`GetUnifiedTradeState` → used by papertrade and livetrain current-state display

### Genotype (13 genes)

All parameters the GA evolves. Key ones:
- `RegimeAdxPeriod` / `RegimeAdxThreshold` — controls how aggressively the regime gate filters
- `RsiOverbought` — entry sensitivity (floor at 65)
- `GridStepAtrMult` — trailing stop width (×ATR%)
- `DcaTriggerAtrMult` — DCA and recovery leg trigger distance
- `MaxDcaLevels` — structural parameter, more stable across retraining

`TimeframeBlend` blends 5m and 15m RSI/EMA; converges to 1.0 (pure 15m) in practice.

`GenotypeDto` in `Program.cs` handles JSON serialization. Old saved files with extra fields (e.g. removed `PumpThresholdIdx`) load cleanly — `System.Text.Json` ignores unknown properties by default.

### GA fitness

`GA.cs` `Fitness()` uses 5-fold walk-forward cross-validation on training candles. Score = `mean_fold_sharpe − 0.5 × std_fold_sharpe`. Penalises variance across folds, not just raw performance. Final elite is rescored on held-out val candles.

`LiveTrainer.cs` uses a different fitness: `mean_sharpe_across_14_coins − 0.4 × cross_coin_std`. This penalises genotypes that work on one coin only.

### Candle fetching

`FetchCandles(symbol, batches)` — each batch fetches up to 1000 Bybit 5m candles, walking backwards in time. Rate-limit aware with exponential backoff. `batches: 106` ≈ 1yr; `batches: 6` ≈ 20d; `batches: 3` ≈ 10d.

### Discord bot

`discord_bot.py` manages a single long-running dotnet subprocess (`papertrade` or `livetrain`). It:
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` / `LIVE TRAIN` headers in stdout
- Posts a formatted summary to the output channel each cycle
- Forwards text messages in the command channel to `claude -p` for live code edits, then restarts the process
- Slash commands (`/status`, `/backtest`, `/train`, etc.) run one-shot via `dotnet exec bin/Release/net10.0/Gravity-gen2.dll <mode>` to avoid build-lock conflicts with the running process

Required `.env`:
```
DISCORD_TOKEN=
DISCORD_OUTPUT_CHANNEL=
DISCORD_COMMAND_CHANNEL=
DISCORD_GUILD_ID=          # guild-specific sync (instant); omit for global (up to 1h)
GRAVITY_MODE=              # papertrade (default) or livetrain
```

### Saved files

- `best_genotype.json` — output of `train` / `trainmulti`, used by backtest + papertrade + status
- `live_best_genotype.json` — written by `livetrain` when session best exceeds saved fitness; `status` prefers this file if present
