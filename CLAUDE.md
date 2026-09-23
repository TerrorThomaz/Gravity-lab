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
dotnet run -- lowvoltrain        # Low-vol variant genotypes (ATR ratio < 0.8)
dotnet run -- routertrain        # RegimeRouter GA: train routing thresholds + duration gates
dotnet run -- hmmtrain           # Gaussian HMM on BTC regime features (Baum-Welch); saves regime_hmm_genotype.json
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
dotnet run -- edgetest           # THE rigour command: walk-forward gate, portfolio Sharpe off a
                                 # daily equity curve, deflated Sharpe/PBO/White's RC, per-strategy
                                 # leave-one-out acceptance gate. Exits non-zero on a strictly
                                 # dominated strategy. Read this before trusting any other number.

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
                  BayesianOptimizer, StrategyPipeline (shared backtest bootstrap: variant
                  loading/selection, parallel candle fetch, regime/funding/guard session
                  builds — the reproducer-of-record consumed by the backtest commands)
                  StrategyAllocator (Ledoit-Wolf covariance → ERC/inverse-vol riskScale,
                  SIMFAM family clustering + cap, correlation-load haircut; mean-normalised)
  regime/         RegimeClassifier, RegimeRouter, RegimeRouterGA, RegimeRouterGenotype,
                  HiddenMarkovModel (Baum-Welch + HmmAnnotator), HmmGenotype
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
docs/legacy/      Archived: DrawdownGuard*, ExitModifier*, Scenario*, StressTestCommands, test.cs
docs/             README.md, REGIME_ARCHITECTURE.md, superpowers/plans+specs

Gravity-gen2.Tests/
  core/           Indicators, IndicatorLibrary, FitnessConfig, FoldScoreStatistics,
                  FundingRateSession, SlippageModel, CandleValidation, MonteCarlo,
                  HolmFamily, StatisticalTests, TradeEnricher, VariantRouter,
                  ExpandingWindowValidation, OosSizingSplit, RefinementObjective,
                  StrategyAllocatorTests
  regime/         RegimeClassifierTests, RegimeRouterGenotypeDtoTests,
                  HiddenMarkovModelTests, RegimeRouterHmmTests
  guard/          DynamicGuardTests
  strategies/     FoldScoreCapTests, PerCoinFoldAlignmentTests, RipShortExitOverrideTests,
                  GridMaeTests, FadeShortMaeTests
  (added 2026-09-23) core/SharpeHonestyTests, FitnessLandscapeTests, FitnessPathRiskTests,
                  GaTrialCounterTests, MarkToMarketPathTests;
                  regime/RegimeRouterContractTests, RollingStrategyGateTests
```

**The suite is FLAKY under parallel collections — a DIFFERENT test fails on each full run and all
pass in isolation.** Observed on `ExecContextConformanceTests`, `GuardedPortfolioTests`,
`FundingSessionSetTests`, `GaReproducibilityTests`, `SymbolCrowdingCapTests`. Cause is shared
mutable statics across collections — `SwingLongSimulator.BtcRegimeProbe` is a known one (the
`walkforward` command sets it and clears it in a `finally` precisely because a leak would gate every
later run in the process). Re-run before believing a single red result; fix before trusting CI.

## Architecture

### Strategy suite — 3 LIVE, 4 disabled (as of 2026-09-23)

**Only FadeShort, Grid and GridShort have genotypes on disk.** FadeLong, DipLong, RipShort and
SwingLong are disabled (`genotypes/<name>_genotype.json.DISABLED_<date>`); every loader treats a
missing genotype as "skip", so they simply do not trade. They were retired on `edgetest` evidence:
each was a NEGATIVE contributor out-of-sample, and DipLong/RipShort lost money *in their own home
regime* on n≈4,000-5,000 (DipLong PF 0.95 in Bull, RipShort PF 0.98 in Bear) — not window bad luck.
Removing them moved the book's deflated Sharpe 0.003 → 0.119 → 0.790. The table below still
describes all eight because the code paths remain; treat the four as documentation, not as live.

### Strategy suite (8 defined)

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

Several strategies additionally have low-vol (ATR ratio < 0.8) variant genotypes, selected by `VariantRouter.Select`, which picks the **narrowest ATR band** containing the current ratio — fitness plays no part in that choice. Note that `StrategyActivation` also carries `*LowVolActive` / `*HighVolActive` flags and `RegimeRouterGA.StrategyKind` has matching entries — **nothing calls `IsActive` with those kinds**, so the vol variants are chosen by `VariantRouter`, not by the router.

**High-vol variants are RETIRED** (2026-08; files and trainer in `docs/legacy/`). A genotype tuned to trade *harder* when ATR spikes works directly against `DynamicGuard`, whose entire purpose is to cut exposure under exactly that condition, and high vol is where being wrong costs most. High-vol bars are now served by the base genotype with the guard in charge. `fade_short_hival_genotype.json` went with them: despite its "high-value screen" name its band was `[1.5, 9999]`, so it only ever activated in the high-vol regime.

**If high-vol is ever reintroduced**, the variant must be *more selective* than the base, never more aggressive — the failure mode was a genotype with free rein in exactly the regime the guard exists to throttle. Two admission criteria, both with machinery already half in place:

- **High confidence in the move.** `RegimeClassifier` already emits a 0–1 confidence alongside the regime, but `VariantRouter.Select(atr, barIndex, variants)` takes *only* the ATR array — confidence never reaches variant selection. Gating would mean widening that signature, not inventing a signal.
- **Funding not crowded against the trade.** `FundingRateSession.IsCrowdedLong` / `IsCrowdedShort` already exist as boolean gates (`CrowdedLongThreshold = +0.08%/8h`, `CrowdedShortThreshold = −0.05%/8h`). Crowded funding in a high-vol regime is the squeeze setup — that is the tail risk that makes high-vol trading expensive, so it should veto entry rather than merely be priced.

Precondition before any of that: **fix the `AtrLow`/`AtrHigh` serialization first.** Reintroducing the files without it silently recreates the shadowing described below, and the symptom (a base genotype that is simply never selected) is invisible in every report.

Two bugs made this urgent rather than cosmetic. `HighVolTrainCommands` never serialized `AtrLow`/`AtrHigh`, so every `*_highvol_genotype.json` was written with the DTO defaults `[0, 9999]` — the *base* band. Since `VariantRouter` breaks width ties by array order and the glob puts `highvol` before the appended default, **the high-vol genotype silently shadowed the retrained base genotype at every ATR level**, and a genotype fit only on ATR>1.5 segments was being served across the whole range. `FullTest.cs`'s `label == "highvol"` branch is now simply unreachable. The `BoundsHighVol` / `RandomHighVol` / `MutateHighVol` operators remain in the genotype classes — dead but harmless, and covered by tests; removing them is a separate, larger change.

Exit uses four layers: hard ATR stop · fixed ATR profit target · trailing ATR stop (armed after `TrailingActivationAtrMult` × ATR move) · `MaxHoldCandles` forced close. RipShort's stop/target are wick-triggered (intrabar high/low, not close) since bear-rally squeezes are its dominant tail risk.

**Funding.** All eight strategies now price perp funding through `FundingRateSession.PnlPct` (real rate when the funding cache covers the trade, else the interest-rate floor charged on the same discrete 8h ticks). The grid family was the last holdout — `GridSimulator` and `AccumulationGridSimulator` booked **zero** funding on every position, a systematic cost advantage that biased every Grid-vs-other comparison in Grid's favour. Both now take a `FundingRateSession?` and charge with `isLong: true` (grid entries are buy-limits below the anchor, so a positive rate is a cost). Passing `funding: null` does **not** mean "no funding" — the fallback branch still charges the interest-rate floor.

Real per-symbol funding also reaches the backtests now: the previous call sites fetched BTCUSDT only and applied that one rate series to ~190 symbols. Unknown symbols resolve to null (floor fallback) rather than borrowing another symbol's rates. Off-grid symbols (4h settlement, capped hourly windows) are surfaced by name with a `CostUndercountFactor`.

Grid has an EMA slope gate: skips entries when 20-bar EMA slope < −0.5% (prevents buying dips in confirmed downtrends).

FadeLong uses the router's `BearMinBars`/`BearMinConf` confirmed-bear gate (bounds [24, 200] bars). **RipShort does not share it** — it has its own `RipShortBearMinBars`/`RipShortBearMinConf` pair (bounds [50, 500] / [0.10, 0.80]), split off because a since-disabled FadeLong was dragging the shared threshold away from RipShort's optimum. Only FadeLong (a bounce/reversal play) carries into the early-bull transition window (`EarlyBullBearCarry`); RipShort is with-trend and switches off the moment the regime tips toward Bull.

**The router does not size anything in legacy mode.** It returns boolean activation flags only. There is no early-bear sizing ramp: the `EarlyBearFromBullMult`/`EarlyBearFromRangingMult` genes and the `StrategyActivation.SizeMult` they fed were deleted — nothing consumed the multiplier (every backtest gates via `RegimeRouterSession.IsActive`; papertrade only printed it) and the confidence scaling had never been validated. The four surviving transition genes (`TransitionSizeMult`, `EarlyBullFromBearMult`, `EarlyBullFromRangingMult`, `EarlyBullBearCarry`) are read as `> 0` ON/OFF switches at routing time; their magnitude is used in exactly one place, `RegimeRouterGA.FilterActive`, which scales the trade's capital fraction while scoring candidate routers during router training. Position sizing at execution time lives in `PortfolioReplay` and `Simulator.SimulatePortfolioExposureCapped`, not in the router. **HMM mode changes this:** with a favorability genotype, `RegimeRouterSession.SizeGate` returns the continuous weight and backtests multiply it into trade `Conf`, so the router's conviction scales size (see "HMM regime router" below).

### RegimeClassifier + RegimeRouter

`src/regime/RegimeClassifier.cs` — multi-signal ensemble classifier producing Bull/Bear/Ranging/HighVol + confidence (0–1).
Signals: EMA stack (weight 3), EMA50 slope (1.5), ADX (1), 20-bar momentum (0.5), ATR vol ratio (0.5), low-vol+flat-slope ranging confirmation (1.5).
`ClassifySeriesWithDuration` is O(n) with per-bar duration counter (how many consecutive bars in current regime).

`src/regime/RegimeRouter.cs` — BTC-anchored router (ETH as secondary confirmer, configurable blend weight).
Returns `StrategyActivation`: per-strategy **boolean** flags plus `Regime`, `Confidence` and `AtrRatio`.
Two overloads: rule-based fallback and trained-genotype path (`RegimeRouter.Route(btcH1, routerGeno, ethH1)`), plus an HMM overload (`Route(btcH1, routerGeno, hmmGeno, ethH1, atrRatio)`) that fills `StrategyActivation.Weights`.
Legacy (threshold) gating delegates to `RegimeRouter.ComputeActivation`; HMM gating delegates to `RegimeRouter.ComputeWeights`.

**`IsActive` and `SizeGate` share ONE resolver (`RegimeRouter.Resolve`, fixed 2026-09-23).** They
used to be independent paths: with a SIMFAM gate attached `IsActive` routed through the gate while
`SizeGate` fell through to the legacy thresholds and returned **0**, so a trade the gate admitted
was sized at zero notional — it took a portfolio slot, entered the reported book, and contributed
no P&L. **56-71% of the rows** in the committed report CSVs were in that state. Two further fixes
in the same pass: size is now the conviction weight (monotone), replacing a `w < 0.5 → 1.0` rule
that made size peak at the router's LOWEST conviction and paid the GA to drive favorability under
0.5; and hysteresis/min-hold are precomputed once over the bar series in time order, because they
were mutating per call on a session shared across every coin, so the same trade routed differently
depending on which coin was evaluated first. `RegimeRouterContractTests` pins all three as
properties.

`RegimeRouterSession` — pre-computed O(1) per-trade lookup for backtests. Build once, call `IsActive(kind, time)`. In HMM mode also exposes `Weight(kind, time)` (continuous [0,1]) and `SizeGate(kind, time)` (0 when gated off, else the HMM weight; 1.0 in legacy mode) — backtests multiply `SizeGate` into trade `Conf` so the router's conviction scales size.

### HMM regime router (favorability matrix)

A Gaussian Hidden Markov Model replaces the argmax-discrete regime with a **probability vector** over latent states, and a GA-evolved **favorability matrix** turns that vector into continuous per-strategy weights — so routing is no longer constrained to Bull/Bear semantics. Two artifacts, trained separately:

- `src/regime/HiddenMarkovModel.cs` — diagonal-covariance Gaussian HMM, hand-rolled Baum-Welch (scaled forward-backward, multi-restart). `HmmAnnotator.Annotate(h1, geno)` runs the causal forward filter over the same 9 normalized features as `RegimeClassifier.Features` and returns `RegimeBar[]` with a trailing `HmmProbs` probability vector per bar. States are latent; `HmmGenotype.LabelStates` maps each to a `MarketRegime` post-hoc **for display only** — routing reads the probabilities, never the label. Train with `hmmtrain` → `genotypes/regime_hmm_genotype.json`.
- Favorability genes on `RegimeRouterGenotype`: `Favorability[8×6]` (strategy × state, padded to `MaxHmmStates=6`), `Biases[8]`, `HmmStatesN ∈ [3,6]`. `ComputeWeights` dots `probs · F[s]`, subtracts the bias, and passes through `sigmoid(6·x)` → `weight ∈ [0,1]`. BtcStress shock override floors the three short strategies at 0.9, same as legacy.

Mode selection is automatic and backward-compatible: a router genotype with no `Favorability` (old JSON) or `GRAVITY_HMM=0` runs the exact legacy threshold path bit-identically. With an HMM genotype present, `RegimeRouterGA.Run` auto-detects hmmMode from the series, evolves the favorability matrix, and skips BO (48+ dims is hopeless for TPE). Training gates trades at `weight > 0.5` (exactly matching serve) and scales `frac` by the weight; a **retention factor** penalises genotypes that extinguish a whole strategy across the window, and the **diversification multiplier** normalises `EffectiveBets` by the strategies *available in the pool* (not just survivors) so concentration isn't free — without those two the GA collapses to 1–2 strategies.

Sizing composition at execution: `finalSize = base × routerWeight × riskScale × guardMult`. `StrategyAllocator` (`src/core/StrategyAllocator.cs`) supplies `riskScale` — Ledoit-Wolf-shrunk covariance → ERC/inverse-vol, SIMFAM single-linkage family clustering with a family cap, correlation-load haircut, mean-normalised to 1.0 (redistributes only). Wire it via the `hmmrisk` sizing case in CombinedBacktest or `GRAVITY_HMM_SIZE=1`.

### Red-Queen coevolution (Router ↔ Guard only)

`src/coevolve/CoevolveGA.cs` — **8 rounds** (`RedQueenRounds = 8`). Router and DynamicGuard evolve in parallel each round, **and then the four regime-gated strategies re-adapt to the router that just evolved** before the trade lists are rebuilt for the next round.

The strategies used to be frozen at their seeds, which reproduced the exact train-then-gate misalignment this class exists to remove. The cost was measurable: strategy GAs score *every* trade, then the router discards **54–57%** of them (DipLong 1221→531, SwingLong 2087→963). DipLong's ungated held-out PF is **0.64** against a router-gated val PF of **2.44** — the edge lives in the gating, and the strategy was never allowed to see it.

The hook this needed already existed and was simply never wired: every strategy GA takes a `Func<DateTime, double>? tradeGate` and **no caller passed one**. It weights by `t.Time`, which every simulator records as the *exit* bar — the same field `CombinedBacktest` gates on, so train and serve agree by construction (both share the entry-vs-exit approximation).

The gate is **soft** (`GateFloor = 0.10`), not 0/1. A hard gate would drop out-of-window trades entirely, and a fold falling under `MinTradesPerFold` now enters the aggregate at `ThinFoldScore = -5.0` — so an over-eager router in an early round could starve a strategy into a score it can never climb out of. Strategies retrain at `RouterGens / 2` generations per round, so `coevolvetrain` is now substantially slower than the router-and-guard-only version.

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

where sharpe/sortino are the *per-trade*, non-time-normalised variants (`PerTradeSharpe`/`PerTradeSortino` — deliberately not `Simulator.SharpeRatio`, which would smuggle in a second frequency term)

**`PerTradeSharpe` used to return 0 below profit factor 1.3 — a CLIFF, fixed 2026-09-23.** It now
ramps over `[SharpePfRampLo = 1.0, SharpePfRampHi = 1.3]`. This mattered more than its size
suggests: `SharpeW` defaults to 0.5 while `PfW` and `CalmarW` default to **0.0**, making it the
largest *live* statistical weight, and every strategy in the walk-forward book sits at PF 1.00-1.20
— so the heaviest stat term was contributing a constant across the entire band being ranked. Third
instance of the same class (tail-ratio step at n=100, `Simulator.SharpeRatio`'s PF floor, this).
`FitnessLandscapeTests` now guards it generically: every GA-consumed statistic must be continuous,
monotone and gradient-bearing across PF 1.0-3.0. **Any new statistic added to `Canonical` gets a
case there** — a guard clause returning a constant fails it.

**`maePct`: intra-hold drawdown now reaches fitness.** `Canonical` and `CanonicalRegime` take an
opt-in per-trade maximum adverse excursion; the balance walk dips to each trade's trough before
settling it, so the drawdown term prices time at risk instead of only the settled return. Null or
mismatched-length is a bit-identical no-op. Wired for **Grid, GridShort and FadeShort** (opt-in
`maeOut` lists on their simulators, filtered in lockstep with the trade gate and the regime-sustain
filter). Why it exists: a path-blind retrain pushed Grid's `MaxHoldCandles` 53 → **164**, tripling
time at risk for a score that could not register it; under the path-aware term the same GA chose
**30**., the six `FitnessConfig` term weights are floored at 0 and are bit-for-bit no-ops at 1.0, and both tail terms **ramp** over `[TailRampLo = 40, TailRampHi = MinTailSampleSize = 100]` returns via `w(n) = clamp((n − 40)/60, 0, 1)` — neutral at 40 (the 5% bucket holds 2 observations), fully on at 100 (5 observations). They were previously a hard gate at 100, which was a cliff the GA was paid to sit under: deleting one *winning* trade at n=100 raised the fold score 35.8%; it now costs 4.2%. `TailRatioBonus` is also capped (`MaxRrMult`-style) rather than unbounded.

`qualityMult`'s payoff-ratio term is the bounded hyperbola `1.6·rr/(rr + 1.5)`, anchored so `rrMult(2.5) == 1.0` exactly. The old linear ramp floored at 0, which made `qualityMult` zero for any fold with `rr < 1.0` — and since `base` is a pure product, the whole fold score collapsed to exactly 0.0 with no gradient back toward `rr = 1`. `retentionMult` penalises giving back gains at fold end (pushes toward tight trailing, not peak capture).

**Aggregation across folds** — `FoldScoreHelper.AggregateFoldScores`:

```
scores[]  = the k scored folds, then one ThinFoldScore entry per attempted-but-thin fold
            (CONSTANT LENGTH = attemptedFolds)
fitness   = λ·CVaR_α(scores) + (1 − λ)·mean(scores)
```

`CVaR_α` is the mean of the worst `ceil(α·k)` folds (`α = CVaRFoldAlpha = 0.4`); `λ` runs from `LambdaThick = 0.4` to `LambdaThin = 0.8` as the sample thins and depends only on per-fold *trade counts*, never on the scores. Zero surviving folds returns `DeadFoldFitness = -1000.0`.

**There is no coverage factor, and folds are never dropped.** A fold that never reached `MinTradesPerFold` enters the vector at `ThinFoldScore = -5.0` instead of vanishing, so "withdraw from your worst window" is not a move in the search space at all. The earlier `combined · coverage` haircut did *not* close that exploit: coverage is a **bounded linear** penalty while the gain from deleting a bad fold is **unbounded**, so at k=5 with four folds at g, dropping the fifth won whenever it scored below `0.375·g` — at g=30 a genotype was better off discarding a fold that scored **+11**, a profitable one. The `-5.0` floor sits below `Canonical`'s minimum of `-2.0`, which is why "produce no trades here" no longer beats "trade and lose here" (the old `-1.0` sentinel sat *above* the losing band — that was the original inversion).

Both terms are monotone non-decreasing in every fold score, so the sum is monotone **by construction** for any k. This replaced `mean − stdMult·std`, which was non-monotone: folds (10,10,30) scored 1.817 and (10,10,40) scored −2.274. Measured `d(fitness)/d(fold score)` is now **+0.196** at k=2 with 1 floored fold, where the old form measured **−0.38**.

**This is not cross-validation.** `k` defaults to 5 and folds are cut per coin on that coin's own array (`PerCoinFoldRange`), but **there is no held-out test fold** — every fold from 0..k−1 is scored and every one of them feeds the fitness the GA selects on. It is a dispersion/robustness penalty across time slices, and it produces **no out-of-sample estimate**. Likewise `FitnessConfig.EmbargoPct` (default 0.05) trims the leading 5% off each fold after the first, which separates two folds that are *both in-sample* — it prevents no leakage into any held-out set, because there isn't one. Genuine OOS evidence comes only from `oosbacktest` / `fulltest` on never-trained coins.

### DynamicGuard

`src/guard/DynamicGuardSession.cs` — per-strategy drawdown guard, trained by `DynamicGuardGA` on the portfolio trade distribution. Supersedes the older DrawdownGuard and ExitModifier (archived in `docs/legacy/`).

**Where it is actually applied:**
- `combinedbacktest`, `oosbacktest`: applied via the shared `src/core/GuardedPortfolio.cs` (`TryLoad` → `Apply` → `PrintComparison`). Guarded and unguarded are reported **side by side**; the headline row stays unguarded. That is deliberate — the guard had never been applied here, so swapping the headline would be indistinguishable from a regression. `GuardedPortfolio` also carries the six simulator-level knobs (`DdEntryGatePct`, conf loss caps, profit protection) that `FullTest`'s old local lambda hand-threaded as loose locals and never passed through.
- `allcoinsbacktest`, `backtest`, `gridbacktest`: **not applied** — those numbers are unguarded.
- `fulltest`: applied, but still through its own local `ToSimGuarded` helper rather than the shared `GuardedPortfolio` (known handoff). Only touches the strategies in `DynamicGuardSession.IsGuarded` — `grid`, `gridshort`, `diplong`, `swing_long`.
- `papertrade`: **reporting only.** `guardSession.GetMult(...)` feeds a `Guard: STRESS ×N` console line, the rotator's safety score (which itself only prints and lands in JSON), and a `guard.mult` JSON status field. No signal is sized, filtered or suppressed by it.
- `dynamicguardtrain`: applied, since that is the training objective.

### Reported statistics — two traps

**`Simulator.SharpeRatio` is NOT an annualised Sharpe.** It is a per-trade `mean/std` scaled by
`sqrt(candleCount / 288)`, which is why `oosbacktest` prints 10.95. The scale factor depends on how
many candles the book spans, so the same edge prints a different number on a longer history. A real
portfolio Sharpe has to come off a daily equity curve — `edgetest` does that and reports ~1-3.

It also **used to return exactly 0 whenever profit factor < 1.3**, so a losing book and a flat one
both printed `0.00` and no report could tell them apart. Fixed 2026-09-23: it now returns the true
value including negatives, and `ScreenedSharpeRatio` keeps the old floor for any caller that wants
"profitable enough to quote" (none of the 63 call sites did). `SortinoRatio` is clamped both ways.

### Portfolio cap

`src/core/PortfolioReplay.cs` — filters combined trade list by per-strategy concurrent count before EUR exposure simulation. `DefaultCaps`: `fade_short`=10, `swing`=10, `swing_long`=8, `diplong`=8, `fadelong`=8, `ripshort`=8, `grid`=12, `gridshort`=12, `accumgrid`=12. A strategy label missing from that dictionary warns once and falls back to `int.MaxValue` (uncapped) — keep labels in sync when adding a strategy. Directional cap (`Config.MaxDirectionalConcurrent = 20`) limits total same-direction concurrent positions across all strategies to prevent correlated exposure clustering; a label with no known direction is counted against *both* directional caps.

**Per-strategy caps bind before the directional cap, and measuring without them is a trap.**
`edgetest` originally replayed with only the global 20-slot budget and no `DefaultCaps`, which let
FadeShort hold 8,919 slots on arrival order alone and starve 1,108 Grid + 1,248 GridShort trades.
That made a *measurement artifact* look like a defective strategy: the acceptance gate rejected
FadeShort, and wiring `DefaultCaps` in moved the book from CAGR 9.1%/Sharpe 1.43/DSR 0.125 to CAGR
11.4%/Sharpe 2.62/DSR 0.941. Any new harness that replays trades must apply per-strategy caps first.

**Crowding cap (`src/core/SymbolCrowdingCap.cs`) — OFF by default.** The directional cap counts heads: 20 open longs are 20 open longs whether they sit in one co-movement family or five. `GRAVITY_CROWDING=<double>` charges the directional budget for correlation instead, via `EffectiveSlots(n, rho, strength) = n·(1 + (n−1)·rho)^strength` — the variance inflation of n equally-sized positions at mean pairwise correlation `rho`. Two properties are load-bearing: it is an **exact no-op at strength 0** (`Math.Pow(x, 0) == 1`, plus an early return, so the pre-existing headcount path is bit-for-bit unchanged), and it is **one-sided** — the inflation factor is floored at 1, so it can only ever *reject* a trade the headcount admitted, never admit one it rejected. That asymmetry is deliberate: crypto correlations converge toward 1 in exactly the drawdowns this exists to survive, so an estimate made on average conditions understates crowding when it matters and must never be trusted in the loosening direction. Negative correlation therefore buys no extra slots. Wired into `combinedbacktest`, `oosbacktest`, `allcoinsbacktest` and `fulltest` (all four of its books: val, OOS, and both no-router variants), each building its own matrix because the `asOf` cutoff differs per book. The correlation is estimated **strictly before the first trade in the book** (`asOf`), because a cap fitted on the window it filters would be choosing which clusters to avoid already knowing how they turned out. Unknown symbols are charged at correlation 1.0. `fulltest`'s four trade books previously carried no symbol — they now do (`(Time, Return, Conf, Strategy, Symbol)`), which is what lets the cap see anything there at all. Mean pairwise correlation is used rather than `CovarianceMatrix.EffectiveBets`: the two agree exactly for an equicorrelation block (`n/EffectiveBets == 1 + (n−1)·rho`), and a 20×20 eigendecomposition per trade is both over-parameterised at ~1,170 daily observations and O(n³) in the hot path.

`dotnet run -- symbolcov` reports the inputs this cap runs on — effective bets for the whole universe and for `Config.OosCoins` alone, PC1's share of variance, stress-vs-calm correlations, and deterministic co-movement families. Read it before choosing a strength.

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
| `docs/legacy/genotypes/swing_best_genotype_{liquid,mid}.json` | **RETIRED 2026-09-23** — see below |
| `genotypes/regime_router_genotype.json` | RegimeRouter (threshold genes + optional HMM favorability/biases) |
| `genotypes/regime_hmm_genotype.json` | Gaussian HMM (transition matrix, per-state Gaussian emissions, post-hoc state labels) |
| `genotypes/dynamic_guard_genotype.json` | DynamicGuard |
| `genotypes/grid_short_genotype.json` | GridShort |
| `genotypes/accumulation_grid_genotype.json` | AccumulationGrid (Bull + Bear sub-objects) |
| `genotypes/vol_rotator_genotype.json` | VolatilityWeightedRotator |
| `genotypes/{fade_short,dip_long,swing_long,rip_short}_lowvol_genotype.json` | low-vol variants |
| `genotypes/{drawdown_guard,exit_modifier}_genotype.json` | archived (`docs/legacy/`), not loaded by any live path |
| `docs/legacy/genotypes/*_highvol_genotype.json`, `fade_short_hival_genotype.json` | **retired** — see the high-vol note above |

**Caution — a GA retrain overwrites the genotype in place and GA runs are non-deterministic.** A rerun can land in a worse basin and silently regress an already-validated genotype (observed: RipShort held-out PF dropped from profitable to 0.95, overfit flag tripped, after a routine retrain). Every file above is now tracked in git, so `git checkout genotypes/<file>` *can* recover the last committed version — but only what was committed. Commit or `cp` aside any genotype you care about before retraining.

**Caution — the four regime-gated genotypes committed in `15f8b2b` were selected under a broken fitness.** `dip_long_genotype.json`, `swing_long_genotype.json`, `fade_long_genotype.json` and `rip_short_genotype.json` were retrained in the same commit that introduced the absolute-index fold bug (fold boundaries computed in BTC's full-history index space, then applied to per-coin arrays). Coins shorter than a fold's start index were dropped from that fold entirely, leaving folds empty; empty folds returned the constant `-1.0` sentinel, which inverted the `mean − stdMult·std` aggregation. Measured `d(fitness)/d(fold score)` was **−0.38 with 2 dead folds and −0.60 with 4** — the GA was selecting *against* performance. FadeLong and RipShort were worst affected (bear-window-filtered train arrays of a few thousand bars against a ~28k-bar BTC index ⇒ effectively one live fold, permanently inverted).

The fold logic is now fixed (per-coin fold boundaries via `FoldScoreHelper.PerCoinFoldRange`; thin folds enter the constant-length vector at `ThinFoldScore = -5.0` rather than being dropped) and the `mean − stdMult·std` aggregator described above no longer exists — it was replaced by the monotone CVaR/mean blend documented in "GA fitness". But **these genotypes predate the fix and have not been reselected under the corrected objective.** Retrain all four before any live use, and compare against `git show 15f8b2b^:genotypes/<file>` on OOS before accepting the new ones. The backtest figures quoted in the `15f8b2b` commit message (CAGR 25.26%, DipLong PF=3.28, RipShort PF=5.34) were produced from these genotypes and additionally assumed zero slippage — see below.

**Slippage is charged at TRADE level, not at the portfolio layer.** `Config.SlippageBps = 10.0` is the sole magnitude authority; every slippage figure is that constant times a dimensionless shape. This matters because it is what GA selection sees: previously `grep -rln slippageBps src/strategies/` returned **nothing**, so the GA optimised against ~0.185pp of round-trip cost while every report printed ~0.285pp — a 54% gap between the objective and the published number, and every genotype in `genotypes/` was chosen under the cheaper one. At the 3% reference ATR the round-trip charge is bit-for-bit what the portfolio layer used to apply: the charge **moved**, it was not invented. Stop-gap degradation stays separate (a different event, zero on every non-stop exit).

There is no longer a `slippageBps` parameter to forget: it was deleted outright from the four portfolio entry points and all 46 call sites, so a site that tries to pass one now **fails to compile** rather than handing a number to something that discards it. `Simulator.FeeRoundTrip = 0.21` went with it (dead, and sat next to the live `TradeCosts.FeeRoundTripPct = 0.11` — two fee constants differing by exactly 2x is the shape of a double-count). Any result recorded before 2026-08 assumed **zero** slippage and is not comparable.

### Held-out validation: time-embargo + regime-stratification

Plain "held-out coin" validation (different symbol, same calendar window as training) doesn't test time-generalization — it tests symbol-generalization, and crypto's cross-sectional correlation (BTC/alts co-move) lets a regime-timing overfit "generalize" across correlated coins without being a real edge. Two fixes layered on top of the original held-out check, both report-only (never used for GA selection):

- **Time embargo** (`TrainCommands.cs` FadeShort, `LongTrainCommands.cs` RipShort): restrict held-out coins to dates after every training coin's own fit window ends. FadeShort has embargo headroom (global 87.5% train/val split leaves a trailing slice free). **RipShort does not** — its per-coin val window is carved from that coin's own most-recent bear block (`CandleFetcher.FindLastRegimeBlock`), so at least one coin's training data typically already extends to the present, collapsing the embargo cutoff to "today" with zero trades to report. Fixing this would mean reserving a fixed trailing slice *before* the per-coin bear-block extraction runs.
- **Regime stratification** (`RegimeBarLookup.TagRegimes` in `RegimeClassifier.cs`): tags each held-out trade with the BTC regime active at its entry time, so results are bucketed per regime instead of blended into one number. (The *reporting* use in `TrainCommands.cs` / `LongTrainCommands.cs` is report-only. `TagRegimes` itself is not: `FadeLongGA` and `DipLongGA` also call it inside fitness to drive the `RegimeDiversityW` term — default 0.2 — on their non-fold `useValidation || folds <= 1` branch.) A single blended window can be net-Bull or net-Bear, which silently favors whichever strategy direction matches it. Buckets under 20 trades print "insufficient data" instead of a fabricated stat — thin regimes (Ranging's longest contiguous run is ~41 h1 bars) genuinely can't support a held-out claim.

### Rigour: `edgetest`, the trial ledger, and the rolling gate

**`edgetest` is the only command whose numbers survive scrutiny.** Everything else reports
per-trade statistics on an in-sample-gated book. It reports, for each of RAW / static-gate /
rolling-gate / coin-screen / combined / a random-gate null control: per-trade PF and mean, then
CAGR, annualised Sharpe, maxDD, Calmar and deflated Sharpe off a **daily marked-to-market equity
curve**. Read the null control — at one point the gate's entire drawdown benefit was reproducible
by dropping the same *fraction* of trades at random.

**`RollingStrategyGate` (`src/regime/RollingStrategyGate.cs`)** replaces
`genotypes/strategy_family_gate.json` for measurement. The static gate fits (strategy, regime)
profit factors over the whole training set and then gates those same trades on the result, with
`FamilyConfidence = in-sample PF / 3` sizing the position — nothing downstream of it is
out-of-sample. The rolling gate re-fits every 30d on the trailing 180d of trades that had already
**closed** (entry time picks the bucket, exit time gates availability), so no decision sees its own
outcome. It is also the decay detector: a dead strategy routes itself off with no retrain.
`BySymbol` does the same per coin — and measured, per-coin selection has **no** out-of-sample power
here (PF 1.01 vs 1.00 raw), which is most of what `oosbacktest`'s in-sample coin screen was buying.

**`GaTrialCounter` (`src/core/GaTrialCounter.cs`) — the deflated Sharpe is decided by a number
nobody used to record.** `DeflatedSharpeRatio` subtracts `E[max SR]`, the Sharpe a search of T
trials produces from strategies with NO edge. The same book scores DSR 0.971 at T=1,000 and 0.770
at T=100,000. One `Record` per candidate evaluation at each of the 9 GA fitness entry points,
merged into `genotypes/ga_trials.json` at process exit. **Trials ACCUMULATE and the ledger merges
rather than replaces** — a genotype surviving five retrains was selected from all five passes, and
resetting would launder away the multiple-testing burden a retrain just added. A missing ledger
falls back to a deliberately LARGE default, because "we did not measure the search" must deflate,
never inflate. Measured: `gridtrain` = 11,110 trials/run; a `train` run was 29,234 before the
per-cluster stage was retired.

**Per-cluster (`CoinCluster`) genotypes are RETIRED 2026-09-23.** `CoinClusterHelper.Classify`
buckets on median h1 ATR% — **"Liquid" means LOW VOLATILITY, not liquidity** — so these were
ATR-band variants, the same family as the high-vol genotypes retired in 2026-08. The training stage
spent ~62% of a `train` run's evaluations on them, deflating the base genotype's Sharpe to pay for
variants no out-of-sample report ever loaded (only `backtest`, `LongTrainCommands` and the live
Hyperliquid path read them). Files moved to `docs/legacy/genotypes/`; every reader already falls
back to the universal genotype when the file is absent, so the reader code is dead but harmless.
**Do not regenerate them** — they would silently shadow the base genotype again.

**`MarkToMarket` marks against real prices when given a path.** Linear accrual carries a position
as a straight line from 0 to its final return, which hides every intra-hold drawdown and inflates
any Sharpe taken off the curve — worst for the longest holds, i.e. the grid family. Supplying
`Position.UnrealisedPct` marks at the actual price series; omitting it is bit-identical to the old
behaviour (`Simulator.cs:428` still does). Switching `edgetest` to real paths cut the book's Sharpe
2.29 → 1.34 and its DSR 0.790 → 0.097. CAGR was unchanged, as it must be — only the path moved.

**Acceptance gate.** `edgetest` runs leave-one-out per strategy and prints ΔSharpe/ΔDSR/ΔCAGR plus
the absolute book *without* it, and the slot-crowding decomposition. Verdicts are three-way:
REJECT only when a strategy is worse on **both** return and risk (strictly dominated, exit code 1);
TRADE-OFF when it helps one and costs the other — a risk-appetite call the gate deliberately does
not price for you; accept when it helps both. It was binary at first and flagged FadeShort as a
defect when FadeShort adds +2.0pp CAGR; a gate that cries wolf gets ignored, which is how
`VERDICT A edge=A robustness=A` became meaningless.

**Current standing (2026-09-23, 3 live strategies, 57,516 recorded trials):**

| book | CAGR | annSharpe | maxDD | Calmar | DSR |
|---|---|---|---|---|---|
| with FadeShort | 11.4% | 2.62 | 3.9% | 2.96 | 0.941 |
| without FadeShort | 9.4% | 6.38 | 0.5% | — | — |

Grade **B** (6/7 criteria). Deflated Sharpe 0.941 against a 0.95 bar is the sole failure and is a
coin-flip distinction on a threshold chosen by hand; the trial count is a **lower bound** because
pre-instrumentation history is unrecoverable, so treat it as "borderline", not "passing". FadeShort
is kept deliberately: +2.0pp CAGR for 7.8x the drawdown. Two caveats that no statistic prices: the
roster was chosen by looking at OOS results on this window (selection on the test set), and without
FadeShort the book is two correlated grid variants.

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

### Hyperliquid live trading (testnet)

`dotnet run -- hyperliquid-papertrade` — distinct from plain `papertrade` (pure simulation, no order
placement). Architecture: `src/core/HyperliquidClient.cs` (C#, HTTP) → `bot/hyperliquid_server.py`
(FastAPI bridge, holds `HYPERLIQUID_PRIVATE_KEY`, does EIP-712 signing) → Hyperliquid's own API.
Currently runs against **testnet** (`HYPERLIQUID_TESTNET=1`) — no real money at risk regardless of
account balance shown. `discord_bot.py`'s `LIVE_MODES` set and watchdog cover this mode identically
to `papertrade` (cycle summaries, trade/killswitch alerts, auto-restart on crash or hang).

Runs the same 6 strategies `papertrade` does (FadeShort, Grid, SwingLong, DipLong, FadeLong,
RipShort — GridShort/AccumGrid aren't wired into either live path yet, a pre-existing gap).
Shadow mode: every strategy is evaluated every cycle regardless of router gating and recorded with
`routed: true/false`, but only `routed==true` signals ever reach `PlaceOrderAsync`.

**Position sizing is flat, not risk-adjusted, and ignores each genotype's own `PositionSizePct`
gene entirely.** Every strategy sizes at the same flat USD amount (`HYPERLIQUID_USD_SIZE` env var,
default 1000) × the DynamicGuard multiplier — `PositionSizePct` is recorded as metadata (`sizeBase`
in the position JSON) but never consumes it for the real order size. **`PositionSizePct` should be
removed from the strategy genotypes once risk-adjustable sizing (e.g. volatility-target or
Kelly-based, sized against actual account equity) replaces this flat model** — keeping a gene the
GA still optimizes but live trading ignores is exactly the kind of drift that made the router's
`SizeMult` dead weight before it was deleted (see "The router does not size anything" above).

Diagnostics (read-only, no orders): `hyperliquid-lookback [hours]` scans real recent history
through every strategy's actual trade-generation function and reports both the raw shadow set and
which of those the router would have gated on. `hyperliquid-griddiag` prints candle-data health
(length, staleness, recency) plus Grid's raw ADX/BB-width/EMA-slope values against the live
genotype's thresholds, coin by coin — built to verify "no signal fired" against real numbers
instead of trusting the pipeline blind.

**Caution — `HyperliquidClient.FetchOhlcvPaginatedAsync` returned stale data for its entire
lifetime until 2026-08-20.** Pagination accumulates in whole 5000-candle chunks that rarely divide
evenly into the requested total, so the pool almost always overshoots; sorting ascending then
`.Take(totalCandles)` grabbed the *oldest* candles from that overshoot and silently discarded the
most recent ones — every signal, regime call, and would-be order price was computed against
history lagging real time by \~10+ days. Fixed to `.TakeLast(totalCandles)`. Also fixed in the same
pass: `PlaceOrderAsync`/`CancelOrderAsync` sent the raw exchange-agnostic symbol (e.g. `"BTCUSDT"`)
with zero normalization — the bridge's order endpoint expects the bare Hyperliquid name (`"BTC"`)
and does no stripping of its own, so every order this pipeline ever attempted would have been
rejected; and Hyperliquid's `"k"`-prefix meme-coin names (`kBONK`/`kPEPE`/`kSHIB`) were being
re-uppercased server-side in `hyperliquid_server.py`, mangling them to `"KBONK"` etc. No real orders
were ever placed before these fixes landed, so nothing was lost — the system was blind, not wrong.

**The bridge wallet has zero balance and cannot currently place real orders.** `marginValue: 0.0`
via `/api/user/info`. Hyperliquid's testnet faucet requires the *same* address to have already
received ≥$5 real USDC on Arbitrum mainnet (confirmed directly: `claimDrip` on
`api.hyperliquid-testnet.xyz/info` returns `"user ... does not exist on mainnet"`) — this wallet has
no mainnet history, so funding it means a real deposit on mainnet from this exact address, a
decision for a human with the funds/keys, not something to automate. Until that happens, treat
`hyperliquid-papertrade` as percentage-tracking only: every signal (shadow or router-gated) already
gets a `pnl` percentage written to `live_state.json`/`live_journal.json` regardless of whether the
order would fill, so the pipeline's evaluative value doesn't depend on the account being funded —
only the "did a real order actually happen" question does, and the honest answer to that is no.

**Fees/slippage on the live `pnl` field**: Hyperliquid's real published base-tier fees are 0.015%
maker / 0.045% taker — cheaper than the Bybit-calibrated `TradeCosts.FeeRoundTripPct = 0.11` every
backtest in this repo assumes, and a different exchange entirely. `HyperliquidPaperTrade.cs` charges
a flat `HlFeeRoundTripPct = 0.09` (worst case, both legs taker) against every `pnl` figure it writes
— applied in full up front rather than split entry/exit, a deliberate simplification for a single
running percentage. **Slippage doesn't apply the way the backtest model prices it**: every order
here is `isLimit: true` (GTC), so there's no market-order price degradation on the fill — the real
equivalent risk is fill-or-no-fill (price runs past the entry before the resting order gets touched),
which isn't priced or tracked anywhere yet.
