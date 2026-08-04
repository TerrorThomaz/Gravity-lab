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
dotnet run -- ripshorttrain      # RipShort GA: bear-regime relief-rally continuation short, restricted to BTC bear windows
dotnet run -- diplongtrain       # DipLong GA: bull-regime RSI dip + bullish BoS, regime-gated
dotnet run -- swingLongtrain     # SwingLong GA: bull-regime RSI bullish divergence + bullish BoS
dotnet run -- routertrain        # RegimeRouter GA: train routing thresholds + duration gates
dotnet run -- coevolvetrain      # Coevolve FadeLong + DipLong + Router together (4 cycles)
dotnet run -- dynamicguardtrain  # DynamicGuard GA: portfolio drawdown guard
dotnet run -- retrain            # FadeShort retrain on unknown coins (inverted screen, anti-overfit)

# Backtesting
dotnet run -- backtest           # FadeShort backtest: 93 coins, val 20%, 1h+15m dual-TF
dotnet run -- gridbacktest       # Grid backtest: 93 coins, val 20%
dotnet run -- combinedbacktest   # All strategies combined, shared capital, router-gated
dotnet run -- oosbacktest        # OOS backtest: never-seen coins, full history, all strategies
dotnet run -- allcoinsbacktest   # Portfolio sim: BacktestCoins (val 20%) + OOS coins
dotnet run -- yearlybreakdown    # Per-year portfolio returns (full history)
dotnet run -- fulltest           # Full statistical test with Kelly cap + VC analysis
dotnet run -- test               # Statistical edge validation

# Live
dotnet run -- papertrade         # Live signals (1h refresh), all strategies, router-gated

# Discord bot (requires .env)
.venv/bin/python bot/discord_bot.py

# Serve frontend + API server
python3 -m uvicorn bot.api_server:app --port 8080
# Then open: http://localhost:8080/Gravity%20Terminal.html
```

No test suite for strategies. Validation is done by running `combinedbacktest` or `oosbacktest` after any simulator change.
Unit tests for indicators and classifiers: `dotnet test Gravity-gen2.Tests/`

## Directory Layout

```
src/
  core/           Shared infrastructure: Config, Indicators, Simulator, CandleFetcher,
                  CoinCluster, Pumpsegmenter, FundingRateSession, PortfolioReplay,
                  RankedPortfolioSim, TradeEnricher, StatisticalTests, GenotypeDto,
                  BayesianOptimizer
  regime/         RegimeClassifier, RegimeRouter, RegimeRouterGA, RegimeRouterGenotype
  strategies/
    fade_short/   FadeShortGA, FadeShortGenotype
    fade_long/    FadeLongGA, FadeLongGenotype, FadeLongSimulator
    rip_short/    RipShortGA, RipShortGenotype, RipShortSimulator
    dip_long/     DipLongGA, DipLongGenotype, DipLongSimulator
    swing_long/   SwingLongGA, SwingLongGenotype, SwingSimulator
    grid/         GridGA, GridGenotype, GridSimulator
  guard/          DynamicGuardGA, DynamicGuardGenotype, DynamicGuardSession,
                  DynamicGuardTrainCommands
  coevolve/       CoevolveGA

commands/         Program (CLI entry), TrainCommands, LongTrainCommands, GridCommands,
                  BacktestCommands, PapertradeCommands, CombinedBacktest, OosBacktest,
                  FullTest

genotypes/        All trained parameter JSON files (see table below)
bot/              discord_bot.py, knowledge_bot.py, gather_genes.py, swing_autotrain.py, api_server.py
legacy/           Archived: DrawdownGuard*, ExitModifier*, Scenario*, StressTestCommands, test.cs
docs/             README.md, REGIME_ARCHITECTURE.md, superpowers/plans+specs

Gravity-gen2.Tests/
  core/           IndicatorsTests, SimulatorTests (planned)
  regime/         RegimeClassifierTests
  guard/          DynamicGuardTests (placeholder)
```

## Architecture

### Strategy suite (6 strategies)

All strategies share the same dual-timeframe setup: **1h candles** for regime/setup detection, **15m candles** for precise entry/exit execution.

| Strategy | Regime | Direction | Entry Signal |
|----------|--------|-----------|--------------|
| **FadeShort** | Always-on (router-gated off in confirmed Bull) | Short | RSI bearish divergence + min rally + bearish BoS on 15m |
| **Grid** | Ranging | Long | ADX low + BB compression, grid levels |
| **SwingLong** | Bull (`SwingLongActive`) | Long | RSI bullish divergence + min decline + bullish BoS on 15m |
| **DipLong** | Bull (`DipLongActive`) | Long | RSI dip (40–55) in established uptrend + bullish BoS on 15m |
| **FadeLong** | Bear (`FadeLongActive`) | Long | RSI bearish divergence at bottom + bullish BoS on 15m |
| **RipShort** | Bear (`RipShortActive`) | Short | RSI relief rally (≥40–60) in established downtrend + bearish BoS on 15m |

Exit uses three layers: hard ATR stop · fixed ATR profit target · trailing ATR stop (armed after `TrailingActivationAtrMult` × ATR move) · `MaxHoldCandles` forced close. RipShort's stop/target are wick-triggered (intrabar high/low, not close) since bear-rally squeezes are its dominant tail risk. All strategies account for perp funding (real rate via `FundingRateSession` when available, else a flat pessimistic −0.01%/8h) — shorts pay in bear regimes, longs pay in bull regimes.

Grid has an EMA slope gate: skips entries when 20-bar EMA slope < −0.5% (prevents buying dips in confirmed downtrends).

FadeLong and RipShort share the router's confirmed-bear gate (`BearMinBars`/`BearMinConf`, bounds [24, 200] bars), but only FadeLong (a bounce/reversal play) carries into the early-bull transition window (`EarlyBullBearCarry`) — RipShort is with-trend and switches off the moment the regime tips toward Bull. Early-bear ramp (`EarlyBearFromBullMult`/`EarlyBearFromRangingMult`) scales FadeShort/RipShort sizing during the first `BearMinBars` bars of a new bear regime.

### RegimeClassifier + RegimeRouter

`src/regime/RegimeClassifier.cs` — multi-signal ensemble classifier producing Bull/Bear/Ranging/HighVol + confidence (0–1).
Signals: EMA stack (weight 3), EMA50 slope (1.5), ADX (1), 20-bar momentum (0.5), ATR vol ratio (0.5), low-vol+flat-slope ranging confirmation (1.5).
`ClassifySeriesWithDuration` is O(n) with per-bar duration counter (how many consecutive bars in current regime).

`src/regime/RegimeRouter.cs` — BTC-anchored router (ETH as secondary confirmer, configurable blend weight).
Returns `StrategyActivation` with per-strategy flags + `SizeMult` (confidence-scaled position multiplier).
Two overloads: rule-based fallback and trained-genotype path (`RegimeRouter.Route(btcH1, routerGeno, ethH1)`).

`RegimeRouterSession` — pre-computed O(1) per-trade lookup for backtests. Build once, call `IsActive(kind, time)`.

### Cooperative coevolution

`src/coevolve/CoevolveGA.cs` — 4-cycle loop: FadeLong and DipLong train against the router's current gating (soft bear/bull confidence gradient), then the router retrains on the evolved trade lists. Avoids train-then-gate misalignment.
`src/core/BayesianOptimizer.cs` — TPE post-GA refinement (60 iterations). Applied after FadeShort, FadeLong, DipLong, SwingLong GA runs.

### GA fitness (all long strategies)

5-fold walk-forward CV. FoldScore = `gain × wrMult × qualityMult × freqBonus / ddDiv × retentionMult`.
`retentionMult` penalises giving back gains at fold end (pushes toward tight trailing, not peak capture).

### DynamicGuard

`src/guard/DynamicGuardSession.cs` — per-strategy drawdown guard. Trained by `DynamicGuardGA` on the portfolio trade distribution. Applied in live papertrade and all backtests. Supersedes the older DrawdownGuard and ExitModifier (archived in `legacy/`).

### Portfolio cap

`src/core/PortfolioReplay.cs` — filters combined trade list by per-strategy concurrent count before EUR exposure simulation. Default caps: FadeShort=10, SwingLong=8, DipLong=8, FadeLong=8, RipShort=8, Grid=12. Directional cap (`Config.MaxDirectionalConcurrent = 20`) limits total same-direction concurrent positions across all strategies to prevent correlated exposure clustering.

### Candle fetching

`src/core/CandleFetcher.cs` — `FetchFifteenMinCandlesCached(symbol, batches)` — 15m candles with disk cache (`candle_cache/{symbol}_15m.csv`). `batches: 113` ≈ 3.2yr. Incremental: only fetches new candles since last write.

### Saved genotype files (in genotypes/)

| File | Strategy |
|------|----------|
| `genotypes/fade_short_genotype.json` | FadeShort |
| `genotypes/grid_best_genotype.json` | Grid |
| `genotypes/fade_long_genotype.json` | FadeLong |
| `genotypes/rip_short_genotype.json` | RipShort |
| `genotypes/dip_long_genotype.json` | DipLong |
| `genotypes/swing_long_genotype.json` | SwingLong |
| `genotypes/swing_best_genotype.json` | SwingLong (best candidate) |
| `genotypes/swing_best_genotype_liquid.json` | SwingLong – liquid coins |
| `genotypes/swing_best_genotype_mid.json` | SwingLong – mid coins |
| `genotypes/regime_router_genotype.json` | RegimeRouter |
| `genotypes/dynamic_guard_genotype.json` | DynamicGuard |
| `genotypes/grid_short_genotype.json` | GridShort |

**Caution — untracked genotypes have no revert path.** As of 2026-07, `rip_short_genotype.json` and `grid_short_genotype.json` are not yet committed to git. Any GA retrain overwrites the file on disk, and `git checkout` can't recover the previous version since there's no commit to revert to. GA runs are non-deterministic — a rerun can land in a worse basin and silently regress an already-validated genotype (observed: RipShort held-out PF dropped from profitable to 0.95, overfit flag tripped, after a routine retrain). Before rerunning training on an untracked genotype you want to keep, `cp` it aside or commit it first.

### Held-out validation: time-embargo + regime-stratification

Plain "held-out coin" validation (different symbol, same calendar window as training) doesn't test time-generalization — it tests symbol-generalization, and crypto's cross-sectional correlation (BTC/alts co-move) lets a regime-timing overfit "generalize" across correlated coins without being a real edge. Two fixes layered on top of the original held-out check, both report-only (never used for GA selection):

- **Time embargo** (`TrainCommands.cs` FadeShort, `LongTrainCommands.cs` RipShort): restrict held-out coins to dates after every training coin's own fit window ends. FadeShort has embargo headroom (global 87.5% train/val split leaves a trailing slice free). **RipShort does not** — its per-coin val window is carved from that coin's own most-recent bear block (`CandleFetcher.FindLastRegimeBlock`), so at least one coin's training data typically already extends to the present, collapsing the embargo cutoff to "today" with zero trades to report. Fixing this would mean reserving a fixed trailing slice *before* the per-coin bear-block extraction runs.
- **Regime stratification** (`RegimeBarLookup.TagRegimes` in `RegimeClassifier.cs`): tags each held-out trade with the BTC regime active at its entry time, so results are bucketed per regime instead of blended into one number. A single blended window can be net-Bull or net-Bear, which silently favors whichever strategy direction matches it. Buckets under 20 trades print "insufficient data" instead of a fabricated stat — thin regimes (Ranging's longest contiguous run is ~41 h1 bars) genuinely can't support a held-out claim.

### Discord bot

`bot/discord_bot.py` manages a single long-running dotnet subprocess (`papertrade`).
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` headers in stdout
- Posts formatted summary to Discord each cycle
- Forwards text messages in command channel to `claude -p` for live code edits, then restarts

Required `.env` (stays at project root):
```
DISCORD_TOKEN=
DISCORD_OUTPUT_CHANNEL=
DISCORD_COMMAND_CHANNEL=
DISCORD_GUILD_ID=
GRAVITY_MODE=papertrade
```
