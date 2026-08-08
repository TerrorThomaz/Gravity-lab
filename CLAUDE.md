# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Commands

```bash
# Build
dotnet build -c Release

# Strategy training
# "N coins" below = the Config.BacktestCoins universe (100 symbols); each command then
# applies its own liquidity/ATR screen, so fewer symbols actually reach the GA.
dotnet run -- train              # FadeShort GA: BacktestCoins, 5 folds, ~10 min
dotnet run -- gridtrain          # Grid GA: ranging-market long grid
dotnet run -- gridshorttrain     # GridShort GA: ranging-market short grid
dotnet run -- fadelongtrain      # FadeLong GA: bear-regime oversold bounce, restricted to BTC bear windows
dotnet run -- ripshorttrain      # RipShort GA: bear-regime relief-rally continuation short, restricted to BTC bear windows
dotnet run -- diplongtrain       # DipLong GA: bull-regime RSI dip + bullish BoS, regime-gated
dotnet run -- swinglongtrain     # SwingLong GA: bull-regime RSI bullish divergence + bullish BoS
dotnet run -- accumgridtrain     # AccumulationGrid GA (separate Bull and Bear genotypes)
dotnet run -- highvoltrain       # High-vol variant genotypes (ATR ratio > 1.5)
dotnet run -- lowvoltrain        # Low-vol variant genotypes (ATR ratio < 0.8)
dotnet run -- routertrain        # RegimeRouter GA: train routing thresholds + duration gates
dotnet run -- coevolvetrain      # Red-Queen coevolution of Router <-> DynamicGuard (8 rounds; strategies FROZEN)
dotnet run -- rotatortrain       # VolatilityWeightedRotator GA
dotnet run -- dynamicguardtrain  # DynamicGuard GA: portfolio drawdown guard
dotnet run -- retrain            # FadeShort retrain on unknown coins (inverted screen, anti-overfit)

# Backtesting
dotnet run -- backtest           # FadeShort backtest: BacktestCoins, val 20%, 1h+15m dual-TF
dotnet run -- gridbacktest       # Grid backtest: BacktestCoins, val 20%
dotnet run -- combinedbacktest   # All strategies combined, shared capital, router-gated
dotnet run -- rankedbacktest     # Ranked portfolio: top-N signals by quality score
dotnet run -- oosbacktest        # OOS backtest: never-seen coins, full history, all strategies
dotnet run -- allcoinsbacktest   # Portfolio sim: BacktestCoins (val 20%) + OOS coins
dotnet run -- yearlybreakdown    # Per-year portfolio returns (full history)
dotnet run -- fulltest           # Full statistical test with Kelly cap + VC analysis
dotnet run -- test               # Statistical edge validation

# Live
dotnet run -- papertrade         # Live signals (15m refresh — PapertradeCommands.RefreshSeconds = 900),
                                 # all strategies, router-gated

# Discord bot (requires .env)
.venv/bin/python bot/discord_bot.py

# Serve frontend + API server
python3 -m uvicorn bot.api_server:app --port 8080
# Then open: http://localhost:8080/Gravity%20Terminal.html
```

`dotnet test Gravity-gen2.Tests/` covers indicators, classifiers, statistics, fold/slippage/funding mechanics and a few strategy invariants — but **no end-to-end strategy behaviour**. Validation of any simulator change still means running `combinedbacktest` or `oosbacktest` and comparing; the unit tests will not catch a P&L regression.

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
                  (FadeShortSimulator actually lives in swing_long/SwingSimulator.cs)
    fade_long/    FadeLongGA, FadeLongGenotype, FadeLongSimulator
    rip_short/    RipShortGA, RipShortGenotype, RipShortSimulator
    dip_long/     DipLongGA, DipLongGenotype, DipLongSimulator
    swing_long/   SwingLongGA, SwingLongGenotype, SwingSimulator, FadeShortSimulator
    grid/         GridGA, GridGenotype, GridSimulator
    grid_short/   GridShortGA, GridShortGenotype, GridShortSimulator
    accumulation_grid/  AccumulationGridGA, AccumulationGridGenotype, AccumulationGridSimulator
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
  core/           Indicators, IndicatorLibrary, FitnessConfig, FoldScoreStatistics,
                  FundingRateSession, SlippageModel, CandleValidation, MonteCarlo,
                  HolmFamily, StatisticalTests, TradeEnricher, VariantRouter,
                  ExpandingWindowValidation, OosSizingSplit, RefinementObjective
  regime/         RegimeClassifierTests, RegimeRouterGenotypeDtoTests
  guard/          DynamicGuardTests
  strategies/     FoldScoreCapTests, PerCoinFoldAlignmentTests, RipShortExitOverrideTests
```

## Architecture

### Strategy suite (8 strategies)

Most strategies use a dual-timeframe setup: **1h candles** for regime/setup detection, **15m candles** for precise entry/exit execution. **The grid family is the exception — `GridSimulator.GetGridReturns` / `GetGridSessionReturns` take only an h1 span and never see 15m data.**

| Strategy | Regime | Direction | Timeframes | Entry Signal |
|----------|--------|-----------|-----------|--------------|
| **FadeShort** | Always-on (router-gated off in confirmed Bull) | Short | 1h + 15m | RSI bearish divergence + min rally + bearish BoS on 15m |
| **Grid** | Ranging | Long | 1h only | ADX low + BB compression, grid levels |
| **GridShort** | Ranging (shares Grid's router gate) | Short | 1h only | Mirror of Grid |
| **AccumulationGrid** | Confirmed Bull or Bear (separate genotypes) | Long | 1h only | EMA-anchored dynamic grid |
| **SwingLong** | Bull (`SwingLongActive`) | Long | 1h + 15m | RSI bullish divergence + min decline + bullish BoS on 15m |
| **DipLong** | Bull (`DipLongActive`) | Long | 1h + 15m | RSI dip (40–55) in established uptrend + bullish BoS on 15m |
| **FadeLong** | Bear (`FadeLongActive`) | Long | 1h + 15m | RSI bearish divergence at bottom + bullish BoS on 15m |
| **RipShort** | Bear (`RipShortActive`) | Short | 1h + 15m | RSI relief rally (≥40–60) in established downtrend + bearish BoS on 15m |

Several strategies additionally have low-vol (ATR ratio < 0.8) and high-vol (> 1.5) variant genotypes, selected by `VariantRouter.Select` in `oosbacktest`. Note that `StrategyActivation` also carries `*LowVolActive` / `*HighVolActive` flags and `RegimeRouterGA.StrategyKind` has matching entries — **nothing calls `IsActive` with those kinds**, so today the vol variants are chosen by `VariantRouter`, not by the router.

Exit uses four layers: hard ATR stop · fixed ATR profit target · trailing ATR stop (armed after `TrailingActivationAtrMult` × ATR move) · `MaxHoldCandles` forced close. RipShort's stop/target are wick-triggered (intrabar high/low, not close) since bear-rally squeezes are its dominant tail risk.

**Funding — known gap.** FadeShort, SwingLong, DipLong, FadeLong, RipShort and GridShort all price perp funding through `FundingRateSession.PnlPct` (real rate when the funding cache covers the trade, else the interest-rate floor charged on the same discrete 8h ticks). **Grid and AccumulationGrid do not.** Neither `src/strategies/grid/GridSimulator.cs` nor `src/strategies/accumulation_grid/AccumulationGridSimulator.cs` contains a funding term, and neither takes a `FundingRateSession` parameter, so both book **zero** funding on every position — a systematic cost advantage over the other strategies on holds up to Grid's `MaxHoldCandles` cap (bounds 24–200 h1 bars; the trained `grid_best_genotype.json` uses 77). Any Grid-vs-other-strategy comparison is biased in Grid's favour by that amount.

Grid has an EMA slope gate: skips entries when 20-bar EMA slope < −0.5% (prevents buying dips in confirmed downtrends).

FadeLong uses the router's `BearMinBars`/`BearMinConf` confirmed-bear gate (bounds [24, 200] bars). **RipShort does not share it** — it has its own `RipShortBearMinBars`/`RipShortBearMinConf` pair (bounds [50, 500] / [0.10, 0.80]), split off because a since-disabled FadeLong was dragging the shared threshold away from RipShort's optimum. Only FadeLong (a bounce/reversal play) carries into the early-bull transition window (`EarlyBullBearCarry`); RipShort is with-trend and switches off the moment the regime tips toward Bull.

**The router does not size anything.** It returns boolean activation flags only. There is no early-bear sizing ramp: the `EarlyBearFromBullMult`/`EarlyBearFromRangingMult` genes and the `StrategyActivation.SizeMult` they fed were deleted — nothing consumed the multiplier (every backtest gates via `RegimeRouterSession.IsActive`; papertrade only printed it) and the confidence scaling had never been validated. The four surviving transition genes (`TransitionSizeMult`, `EarlyBullFromBearMult`, `EarlyBullFromRangingMult`, `EarlyBullBearCarry`) are read as `> 0` ON/OFF switches at routing time; their magnitude is used in exactly one place, `RegimeRouterGA.FilterActive`, which scales the trade's capital fraction while scoring candidate routers during router training. Position sizing at execution time lives in `PortfolioReplay` and `Simulator.SimulatePortfolioExposureCapped`, not in the router.

### RegimeClassifier + RegimeRouter

`src/regime/RegimeClassifier.cs` — multi-signal ensemble classifier producing Bull/Bear/Ranging/HighVol + confidence (0–1).
Signals: EMA stack (weight 3), EMA50 slope (1.5), ADX (1), 20-bar momentum (0.5), ATR vol ratio (0.5), low-vol+flat-slope ranging confirmation (1.5).
`ClassifySeriesWithDuration` is O(n) with per-bar duration counter (how many consecutive bars in current regime).

`src/regime/RegimeRouter.cs` — BTC-anchored router (ETH as secondary confirmer, configurable blend weight).
Returns `StrategyActivation`: per-strategy **boolean** flags plus `Regime`, `Confidence` and `AtrRatio`. It carries no size multiplier — see the note at the end of the strategy-suite section.
Two overloads: rule-based fallback and trained-genotype path (`RegimeRouter.Route(btcH1, routerGeno, ethH1)`).
Both paths delegate their gating to `RegimeRouter.ComputeActivation`, the single source of truth shared with the backtest path.

`RegimeRouterSession` — pre-computed O(1) per-trade lookup for backtests. Build once, call `IsActive(kind, time)`.

### Red-Queen coevolution (Router ↔ Guard only)

`src/coevolve/CoevolveGA.cs` — **8 rounds** (`RedQueenRounds = 8`), and **only the Router and the DynamicGuard evolve**. FadeLong, DipLong, SwingLong and RipShort are *frozen at their seed genotypes*: their trade lists are built once before the loop and reused every round, and the seeds are returned in `CoevolveResult` unmodified. Each round the router trains on the raw strategy trade lists while the guard trains on the previous round's router-gated lists, so the two co-adapt against a fixed signal pool.

`src/core/BayesianOptimizer.cs` — TPE post-GA refinement (60 iterations). Applied after the FadeShort (both normal and LowVol paths), FadeLong, DipLong, SwingLong, RipShort, Grid, GridShort and RegimeRouter GA runs, and inside `DynamicGuardGA`.

### GA fitness

Two levels — do not conflate them.

**Per-fold score** — `FoldScoreHelper.Canonical` (and the `CanonicalRegime*` wrappers, which just pre-filter by regime-bar sustain). Early exits: fewer than `minTradesPerFold` returns → `-1.0`; profit factor < 1.0 → `pf - 2.0`; non-positive gain → `gain*100 - 0.5`. Otherwise

```
base  = gain*100 * GainW * wrMult * qualityMult * freqBonus / ddDiv * retentionMult
score = base * (1 + SharpeW·f(sharpe)) * (1 + CalmarW·f(calmar))
             * (1 + PfW·f(pf))        * (1 + SortinoW·f(sortino))
             * volWeight * CVaRPenalty * TailRatioBonus
```

where sharpe/sortino are the *per-trade*, non-time-normalised variants (`PerTradeSharpe`/`PerTradeSortino` — deliberately not `Simulator.SharpeRatio`, which would smuggle in a second frequency term), the six `FitnessConfig` term weights are floored at 0 and are bit-for-bit no-ops at 1.0, and both tail terms return the neutral 1.0 below `MinTailSampleSize = 100` returns. `retentionMult` penalises giving back gains at fold end (pushes toward tight trailing, not peak capture).

**Aggregation across folds** — `FoldScoreHelper.AggregateFoldScores`:

```
combined = λ·CVaR_α({fold scores}) + (1 − λ)·mean({fold scores})
fitness  = combined ≥ 0 ? combined · coverage : combined / coverage
coverage = survivingFolds / attemptedFolds        (capped at 1.0)
```

`CVaR_α` is the mean of the worst `ceil(α·k)` folds (`α = CVaRFoldAlpha = 0.4`); `λ` runs from `LambdaThick = 0.4` to `LambdaThin = 0.8` as the sample thins and depends only on per-fold *trade counts*, never on the scores. Zero surviving folds returns `DeadFoldFitness = -1000.0`. Folds that never reach `MinTradesPerFold` are excluded from the aggregate but still count toward `attemptedFolds`, so gating everything into one favourable window costs coverage. This replaced the old `mean − stdMult·std`, which was non-monotone (improving a good fold could *lower* fitness).

**This is not cross-validation.** `k` defaults to 5 and folds are cut per coin on that coin's own array (`PerCoinFoldRange`), but **there is no held-out test fold** — every fold from 0..k−1 is scored and every one of them feeds the fitness the GA selects on. It is a dispersion/robustness penalty across time slices, and it produces **no out-of-sample estimate**. Likewise `FitnessConfig.EmbargoPct` (default 0.05) trims the leading 5% off each fold after the first, which separates two folds that are *both in-sample* — it prevents no leakage into any held-out set, because there isn't one. Genuine OOS evidence comes only from `oosbacktest` / `fulltest` on never-trained coins.

### DynamicGuard

`src/guard/DynamicGuardSession.cs` — per-strategy drawdown guard, trained by `DynamicGuardGA` on the portfolio trade distribution. Supersedes the older DrawdownGuard and ExitModifier (archived in `legacy/`).

**Where it is actually applied — narrower than it looks:**
- `combinedbacktest`, `oosbacktest`, `allcoinsbacktest`, `backtest`, `gridbacktest`: **not applied at all.** `DynamicGuardSession` does not appear anywhere in `CombinedBacktest.cs`, `OosBacktest.cs` or `BacktestCommands.cs`, so the headline backtest numbers are *unguarded*.
- `fulltest`: applied, but through a local `ToSimGuarded` helper in `FullTest.cs`, and only as a **guarded-variant comparison** alongside the unguarded run. It also only touches the strategies in `DynamicGuardSession.IsGuarded` — `grid`, `gridshort`, `diplong`, `swing_long`.
- `papertrade`: **reporting only.** `guardSession.GetMult(...)` feeds a `Guard: STRESS ×N` console line, the rotator's safety score (which itself only prints and lands in JSON), and a `guard.mult` JSON status field. No signal is sized, filtered or suppressed by it.
- `dynamicguardtrain`: applied, since that is the training objective.

### Portfolio cap

`src/core/PortfolioReplay.cs` — filters combined trade list by per-strategy concurrent count before EUR exposure simulation. `DefaultCaps`: `fade_short`=10, `swing`=10, `swing_long`=8, `diplong`=8, `fadelong`=8, `ripshort`=8, `grid`=12, `gridshort`=12, `accumgrid`=12. A strategy label missing from that dictionary warns once and falls back to `int.MaxValue` (uncapped) — keep labels in sync when adding a strategy. Directional cap (`Config.MaxDirectionalConcurrent = 20`) limits total same-direction concurrent positions across all strategies to prevent correlated exposure clustering; a label with no known direction is counted against *both* directional caps.

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
| `genotypes/accumulation_grid_genotype.json` | AccumulationGrid (Bull + Bear sub-objects) |
| `genotypes/vol_rotator_genotype.json` | VolatilityWeightedRotator |
| `genotypes/{fade_short,dip_long,swing_long,rip_short}_lowvol_genotype.json` | low-vol variants |
| `genotypes/{fade_short,dip_long,swing_long,rip_short}_highvol_genotype.json` | high-vol variants |
| `genotypes/fade_short_hival_genotype.json` | FadeShort – high-value screen |
| `genotypes/{drawdown_guard,exit_modifier}_genotype.json` | archived (`legacy/`), not loaded by any live path |

**Caution — a GA retrain overwrites the genotype in place and GA runs are non-deterministic.** A rerun can land in a worse basin and silently regress an already-validated genotype (observed: RipShort held-out PF dropped from profitable to 0.95, overfit flag tripped, after a routine retrain). Every file above is now tracked in git, so `git checkout genotypes/<file>` *can* recover the last committed version — but only what was committed. Commit or `cp` aside any genotype you care about before retraining.

**Caution — the four regime-gated genotypes committed in `15f8b2b` were selected under a broken fitness.** `dip_long_genotype.json`, `swing_long_genotype.json`, `fade_long_genotype.json` and `rip_short_genotype.json` were retrained in the same commit that introduced the absolute-index fold bug (fold boundaries computed in BTC's full-history index space, then applied to per-coin arrays). Coins shorter than a fold's start index were dropped from that fold entirely, leaving folds empty; empty folds returned the constant `-1.0` sentinel, which inverted the `mean − stdMult·std` aggregation. Measured `d(fitness)/d(fold score)` was **−0.38 with 2 dead folds and −0.60 with 4** — the GA was selecting *against* performance. FadeLong and RipShort were worst affected (bear-window-filtered train arrays of a few thousand bars against a ~28k-bar BTC index ⇒ effectively one live fold, permanently inverted).

The fold logic is now fixed (per-coin fold boundaries via `FoldScoreHelper.PerCoinFoldRange`, thin folds excluded from the aggregate while still counting toward coverage) and the `mean − stdMult·std` aggregator described above no longer exists — it was replaced by the monotone CVaR/mean blend documented in "GA fitness". But **these genotypes predate the fix and have not been reselected under the corrected objective.** Retrain all four before any live use, and compare against `git show 15f8b2b^:genotypes/<file>` on OOS before accepting the new ones. The backtest figures quoted in the `15f8b2b` commit message (CAGR 25.26%, DipLong PF=3.28, RipShort PF=5.34) were produced from these genotypes and additionally assumed zero slippage — see below.

**Slippage is now applied at 10 bps.** `Config.SlippageBps = 10.0` (0.10% per trade) is passed to every production `Simulator.SimulatePortfolioExposure*` call, including inside `DynamicGuardGA` and `DynamicGuardTrainCommands` so the guard calibrates on the same net-of-slippage distribution the backtests report. Any result recorded before 2026-08 assumed **zero** slippage and is not comparable to current output. Note the `slippageBps` parameter **defaults to 0.0** — a new call site that forgets to pass `Config.SlippageBps` silently reverts to a zero-slippage simulation, so pass it explicitly.

### Held-out validation: time-embargo + regime-stratification

Plain "held-out coin" validation (different symbol, same calendar window as training) doesn't test time-generalization — it tests symbol-generalization, and crypto's cross-sectional correlation (BTC/alts co-move) lets a regime-timing overfit "generalize" across correlated coins without being a real edge. Two fixes layered on top of the original held-out check, both report-only (never used for GA selection):

- **Time embargo** (`TrainCommands.cs` FadeShort, `LongTrainCommands.cs` RipShort): restrict held-out coins to dates after every training coin's own fit window ends. FadeShort has embargo headroom (global 87.5% train/val split leaves a trailing slice free). **RipShort does not** — its per-coin val window is carved from that coin's own most-recent bear block (`CandleFetcher.FindLastRegimeBlock`), so at least one coin's training data typically already extends to the present, collapsing the embargo cutoff to "today" with zero trades to report. Fixing this would mean reserving a fixed trailing slice *before* the per-coin bear-block extraction runs.
- **Regime stratification** (`RegimeBarLookup.TagRegimes` in `RegimeClassifier.cs`): tags each held-out trade with the BTC regime active at its entry time, so results are bucketed per regime instead of blended into one number. (The *reporting* use in `TrainCommands.cs` / `LongTrainCommands.cs` is report-only. `TagRegimes` itself is not: `FadeLongGA` and `DipLongGA` also call it inside fitness to drive the `RegimeDiversityW` term — default 0.2 — on their non-fold `useValidation || folds <= 1` branch.) A single blended window can be net-Bull or net-Bear, which silently favors whichever strategy direction matches it. Buckets under 20 trades print "insufficient data" instead of a fabricated stat — thin regimes (Ranging's longest contiguous run is ~41 h1 bars) genuinely can't support a held-out claim.

### Discord bot

`bot/discord_bot.py` manages a single long-running dotnet subprocess whose mode is switchable at runtime (`papertrade` — the `GRAVITY_MODE` default — plus `livetrain`/`compare` and a separate autoevolve stream).
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` headers in stdout (the general matcher also accepts `LIVE TRAIN` / `COMPARE`)
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
