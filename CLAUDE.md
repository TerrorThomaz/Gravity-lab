# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Commands

```bash
# Build
dotnet build -c Release

# Strategy training
dotnet run -- train              # FadeShort GA: 93 coins, 5-fold WFV, ~10 min
dotnet run -- gridtrain          # Grid GA: ranging-market long grid
dotnet run -- fadelongtrain      # FadeLong GA: bear-regime oversold bounce, restricted to BTC bear windows
dotnet run -- diplongtrain       # DipLong GA: bull-regime RSI dip + bullish BoS, regime-gated
dotnet run -- swingLongtrain     # SwingLong GA: bull-regime RSI bullish divergence + bullish BoS
dotnet run -- routertrain        # RegimeRouter GA: train routing thresholds + duration gates
dotnet run -- coevolvetrain      # Coevolve FadeLong + DipLong + Router together (4 cycles)
dotnet run -- retrain            # FadeShort retrain on unknown coins (inverted screen, anti-overfit)

# Backtesting
dotnet run -- backtest           # FadeShort backtest: 93 coins, val 20%, 1h+15m dual-TF
dotnet run -- gridbacktest       # Grid backtest: 93 coins, val 20%
dotnet run -- combinedbacktest   # All strategies combined, shared capital, router-gated
dotnet run -- oosbacktest        # OOS backtest: 28 never-seen coins, full history, all strategies
dotnet run -- allcoinsbacktest   # Portfolio sim: BacktestCoins (val 20%) + OOS coins
dotnet run -- yearlybreakdown    # Per-year portfolio returns (full history)
dotnet run -- test               # Statistical edge validation

# Live
dotnet run -- papertrade         # Live signals (1h refresh), all strategies, router-gated

# Discord bot (requires .env)
.venv/bin/python discord_bot.py
```

No test suite. Validation is done by running `combinedbacktest` or `oosbacktest` after any simulator change.

## Architecture

### Strategy suite (5 strategies)

All strategies share the same dual-timeframe setup: **1h candles** for regime/setup detection, **15m candles** for precise entry/exit execution.

| Strategy | Regime | Direction | Entry Signal |
|----------|--------|-----------|--------------|
| **FadeShort** | Always-on | Short | RSI bearish divergence + min rally + bearish BoS on 15m |
| **Grid** | Ranging | Long | ADX low + BB compression, grid levels |
| **SwingLong** | Bull (`DipLongActive`) | Long | RSI bullish divergence + min decline + bullish BoS on 15m |
| **DipLong** | Bull (`DipLongActive`) | Long | RSI dip (40–55) in established uptrend + bullish BoS on 15m |
| **FadeLong** | Bear (`FadeLongActive`) | Long | RSI bearish divergence at bottom + bullish BoS on 15m |

Exit uses three layers: hard ATR stop · fixed ATR profit target · trailing ATR stop (armed after `TrailingActivationAtrMult` × ATR move) · `MaxHoldCandles` forced close.

### RegimeClassifier + RegimeRouter

`RegimeClassifier.cs` — multi-signal ensemble classifier producing Bull/Bear/Ranging/HighVol + confidence (0–1).
Signals: EMA stack (weight 3), EMA50 slope (1.5), ADX (1), 20-bar momentum (0.5), ATR vol ratio (0.5).
`ClassifySeriesWithDuration` is O(n) with per-bar duration counter (how many consecutive bars in current regime).

`RegimeRouter.cs` — BTC-anchored router (ETH as secondary confirmer, configurable blend weight).
Returns `StrategyActivation` with per-strategy flags + `SizeMult` (confidence-scaled position multiplier).
Two overloads: rule-based fallback and trained-genotype path (`RegimeRouter.Route(btcH1, routerGeno, ethH1)`).

`RegimeRouterSession` — pre-computed O(1) per-trade lookup for backtests. Build once, call `IsActive(kind, time)`.

### Cooperative coevolution

`CoevolveGA.cs` — 4-cycle loop: FadeLong and DipLong train against the router's current gating (soft bear/bull confidence gradient), then the router retrains on the evolved trade lists. Avoids train-then-gate misalignment.
`BayesianOptimizer.cs` — TPE post-GA refinement (60 iterations). Applied after FadeShort, FadeLong, DipLong, SwingLong GA runs.

### GA fitness (all long strategies)

5-fold walk-forward CV. FoldScore = `gain × wrMult × qualityMult × freqBonus / ddDiv × retentionMult`.
`retentionMult` penalises giving back gains at fold end (pushes toward tight trailing, not peak capture).

### Portfolio cap

`PortfolioReplay.cs` — filters combined trade list by per-strategy concurrent count before EUR exposure simulation. Default caps: FadeShort=10, SwingLong=8, DipLong=8, FadeLong=8, Grid=12.

### Candle fetching

`FetchFifteenMinCandlesCached(symbol, batches)` — 15m candles with disk cache (`candle_cache/{symbol}_15m.csv`). `batches: 113` ≈ 3.2yr. Incremental: only fetches new candles since last write.
`FetchSwingCandles(symbol, batches)` — legacy 4h candle fetch (kept for compatibility).

### Saved genotype files

| File | Strategy |
|------|----------|
| `fade_short_genotype.json` | FadeShort |
| `grid_best_genotype.json` | Grid |
| `fade_long_genotype.json` | FadeLong |
| `dip_long_genotype.json` | DipLong |
| `swing_long_genotype.json` | SwingLong |
| `regime_router_genotype.json` | RegimeRouter |

### Discord bot

`discord_bot.py` manages a single long-running dotnet subprocess (`papertrade`).
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` headers in stdout
- Posts formatted summary to Discord each cycle
- Forwards text messages in command channel to `claude -p` for live code edits, then restarts

Required `.env`:
```
DISCORD_TOKEN=
DISCORD_OUTPUT_CHANNEL=
DISCORD_COMMAND_CHANNEL=
DISCORD_GUILD_ID=
GRAVITY_MODE=papertrade
```
