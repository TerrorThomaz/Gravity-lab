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

# Market-neutral research (Python, standalone — pip install -r scripts/requirements-research.txt)
python3 scripts/market_neutral_research.py selftest              # synthetic: finds planted edges, not fake ones
python3 scripts/market_neutral_research.py all                   # grid + pairs + carry + xcarry on BacktestCoins
python3 scripts/market_neutral_research.py baseline --exec maker # textbook params, no sweep: one trial per book
python3 scripts/market_neutral_research.py all --universe oos    # same on never-trained OosCoins
python3 scripts/market_neutral_research.py all --fetch           # fill candle_cache/ from Bybit first
python3 scripts/trade_log_edge.py reports/oos_trades.csv         # day-clustered t, train/val/test split

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

**Tests that mutate PROCESS-GLOBAL state must join `ProcessGlobalCollection`** (2026-09-23). The
suite used to be flaky — a different test failed on each full run, all passed in isolation, roughly
1 run in 3. Three shared resources, all process-wide, all mutated by tests xUnit was free to run
concurrently because they sat in different collections:

- **`Console.Out`** — the `Capture` helpers save/`SetOut`/restore, which interleaves destructively:
  B saves A's writer as "previous", then A restores and clobbers B's redirect mid-test, so B
  asserts on an empty string. The failure lands on whichever test was unlucky.
- **`SwingLongSimulator.BtcRegimeProbe`** — `Dispose()` clears it at CLASS teardown, which does
  nothing against a concurrent test. A victim routes SwingLong through a gated probe, gets zero
  trades, and fails far from the cause.
- **`GRAVITY_HMM` via `Environment.SetEnvironmentVariable`** — `RegimeRouter.HmmEnabled` reads it
  live, so while set, any concurrent test expecting HMM routing silently takes the legacy path.
  Widest blast radius, and it explains failures in classes that never touch Console.

Fixed with one `[CollectionDefinition(DisableParallelization = true)]` collection. That stops it
running alongside OTHER collections too, which is the property actually needed — the readers are
not enumerable, since `HmmEnabled` is consulted deep inside routing. Verified with 8 consecutive
clean full-suite runs against a prior ~1-in-3 failure rate. **Adding a test that touches
`Console.SetOut`, an environment variable, or any mutable static? Put its class in that
collection.**

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

### Execution modelling — the defect that voided everything, and the gate that catches it

**Same-bar fill lookahead, found and fixed 2026-09-24.** `GridSimulator` armed at bar `i` using
`ema[i]`/`atr[i]` — both of which include `close[i]` — and then filled every rung that `lows[i]` had
reached **during that same bar**. Live, those limit orders are placed at the close of bar `i` and
can first fill at `i+1`. The simulator was buying dips it already knew had happened.

**Measured cost: Grid Sharpe 4.15 → 0.67, CAGR 4.7% → 0.3%. GridShort 4.69 → 0.35.** The entire
apparent edge of the two best strategies was this. Every backtest this repo ever produced inherited
it. The same defect class is quantified in the literature — Zhang, Li, Peng & Chen (2026),
*A One-Switch Benchmark for Decision-Time Leakage* (arXiv:2605.23959) — at leakage gains of +5.41
to +21.65 Sharpe against clean references of 0.44–0.68.

Fixed by `fillOnArmBar: false` (now the default) in both grid simulators, with the arming
precondition relaxed so orders genuinely rest and an `everFilled` flag so an unfilled grid is not
abandoned on the next bar. `GetGridSessionReturnsSameBarFill` reproduces the old behaviour and
exists **only** so the null control can prove it still detects the defect.

**AUDIT (2026-09-24): the defect is confined to the grid family.** All four dual-timeframe
simulators take `h1Ref = ih1 - 1` (the last fully-closed hourly bar), `h4Ref = h1Ref/4 - 1`, and
enter at `m15[nextBar].Open` — the decision strictly precedes the fill. `AccumulationGridSimulator`
**still has the defect and is not fixed**: its `active = true` lives inside the fill block, so
deferring the fill disables the strategy rather than correcting it. It is not live and not loaded
by `edgetest`; it must pass the null gate before being revived.

**THE GATE: `RandomWalkNullTests`.** On a driftless random walk there is no edge, so every simulator
must lose approximately its costs. Anything that *profits* found it in the engine. This is the test
that would have caught the bug on day one, and it is the only test in the suite that checks the
**instrument** rather than the **result**. It includes a self-check that reintroduces the defect and
asserts the gate still fires — a null control that has never rejected anything is not a control.
**Add every new simulator to it.**

**Why none of the statistics caught it.** Deflated Sharpe, PBO, White's Reality Check, walk-forward
gating, block bootstrap and trial counting all ran clean, because every book compared shared the
same biased fill model. Those methods answer *"given that this measurement is trustworthy, was the
selection procedure honest?"* — nothing in that stack establishes the antecedent. **Null controls
establish the antecedent; statistics operate on it. Run them in that order.** `edgetest` now prints
a FILL-MODEL SENSITIVITY row: a single cell is not a result, the table is.

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

**Maker vs taker sides (2026-09-26).** `TradeCosts.RoundTripPct` takes `entryMaker`/`exitMaker`. A maker side is a resting limit order: it pays `MakerFeePct = 0.02` (Bybit non-VIP) and **no slippage**, because a limit fills at its own price. A stop is never maker (`exitMaker` is ignored when `isStop`). Both default to false, so every caller that doesn't pass them is bit-identical to before. Wired: grid-family rungs are maker entries and their take-profits maker exits; FadeShort's fixed target is a maker exit (its entry is a market order at the next open). Fill risk is **not** modelled, on purpose: per-strategy budgets are small against book depth, so a touched limit counts as filled. Before this, every limit fill paid taker plus slippage. The code said so itself ("limit-fill discount not modelled"), and that charged the grid ~0.14% per round trip against a real ~0.04%. Measured on `edgetest`: Grid next-bar mean/trade 0.087% → 0.164% (PF 1.17 → 1.34), rolling-gate book Sharpe 0.68 → 0.94, CAGR 2.7% → 3.8%, DSR 0.001 → 0.007. **The genotypes on disk were selected under all-taker costs** and have not been retrained against this.

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

### Market-neutral research (`scripts/market_neutral_research.py`)

Deliberately shares **no** code path with the directional suite (no GA, router, guard or HMM), so nothing here can be flattered by them. It mirrors only the cost constants (`FeeRoundTripPct`, `SlippageBps`, `FallbackIntervalPct` — keep in sync) and parses the symbol lists from `Config.cs`. Four books (grid below), walk-forward, parameters fixed in advance:

- **pairs** — Engle-Granger cointegration pairs selected on a 90d formation window, traded on the next 30d with the hedge ratio frozen. Control: random pairs from the same universe under the same rules.
- **carry** — cross-sectional funding carry, short high-funding / long low-funding, then `factor_neutral` projects the weights off the dollar direction and the top 3 eigenvectors of the universe's own trailing 30d return covariance. It has no BTC anchor: the hedge follows whatever co-movement dominates, and some hedge weight lands on coins outside the two legs. `factors=3` was fixed in advance; the sweep prints 1 and 5 to show it is not a knife-edge.
- **xcarry** — funding spread carried across highly correlated pairs (the correlation hedges the price move).

Both carry books use a **fixed-permutation** shuffled-funding control (signal keeps its persistence and turnover, loses its link to the funding actually received — a per-rebalance reshuffle would inflate the control's turnover ~13x and make it an unfair strawman). Signals act one bar late; funding is real per-symbol, floor-charged both ways when missing. The sensitivity grid rows are printed in full and are **not** candidates.

Per `docs/RIGOR_REWORK_2026-09.md` (null controls before statistics), `selftest` is the gate and runs first: on synthetic data with no planted cointegration / no persistent funding dispersion every book must lose roughly its costs, and with them planted it must find them. Execution is at the close of the bar *after* the signal bar, strictly later than the grid fix's next-bar fill.

**grid book** (`run_grid`): the textbook neutral futures grid, no trend filter and no time exit. It has 10 geometric levels per side over ±2σ of a 30-day move, each unit bought at P_i is sold at P_(i+1), it is flattened at market one step outside the range, and it redeploys. Intrabar fills walk O→L→H→C (up bar) or O→H→L→C (down bar). `selftest` measures that rule against exact sub-hour fill order on random walks rather than assuming it: the paired gap is +2.0 ± 3.3%/yr, so no edge comes from the fill model. Control: each coin's bars shuffled in time.

**Measured 2026-09-26, `baseline --exec maker`** (textbook parameters, no sweep, one trial each; net %/yr, t on weekly blocks):

| book | OosCoins (64 survivors) | BacktestCoins (89) | verdict |
|---|---|---|---|
| grid | −15.7 (control −6.7), t −1.6 | −4.2 (control −4.5), t −0.5 | **no edge**. 98% of grids end in a range break; on OOS alts the real grid is WORSE than shuffled bars, so they trend at this scale and a grid sits on the wrong side |
| pairs (half-life ≥ 24h) | +11.3 (random +4.9), t 1.96 | −7.4 (random −6.1), loses gross | **not robust**. Positive on one universe, negative before costs on the other |
| carry, BTC-beta hedge (retired) | +20.5 (shuffled −2.0), t 1.45, maxDD −41% | +13.3 (shuffled −3.4), t 1.50, maxDD −64% | superseded |
| **carry, covariance-PC hedge** | **+20.2 (shuffled −0.8), t 1.92, Sharpe 0.89, maxDD −29%** | **+13.3 (shuffled −2.1), t 2.13, Sharpe 0.87, maxDD −23%** | **the one lead**. Same return, a third less vol, drawdown halved, BTC beta −0.005/−0.004. The OOS price leg went −13.2 → −1.2%/yr. factors=1/5 give 17.5/13.8 and 15.8/13.1, so the result doesn't hinge on 3 |

**Rebalance band (`CarryParams.band`, run_book `band=`).** A symbol within 0.5% of capital of its target is not re-traded. Covariance-hedged carry sends ~37 orders/day, mostly hedge dust that falls below exchange minimums on a small budget. With the 0.5% band (fixed in advance, with its own control): orders 13.7k → 4.4k/yr, OOS +21.8%/yr t 2.07, BacktestCoins +13.5% t 2.17, so the result slightly *improves*. Carry's real sample is ~250 weekly blocks per universe and ~1,000 position openings; the order count is not the sample size.

**What the directional strategies actually earn** (`edgetest` now writes `reports/edgetest_raw_trades.csv`; `trade_log_edge.py --hedge` holds each trade against carry's covariance anchors):
- **FadeShort's edge is market timing, not coin selection.** During its holds the equal-weight market moves 0.74%/trade further in its favour than over random windows of the same length. Hedged, it loses (train-time mean −0.21%, PF 0.85, t_day −5.7). Its most recent 10% of trades is negative even unhedged (−0.49%, t_day −2.2). Per-coin shorts carry the market call plus idiosyncratic noise that is net negative.
- **Grid's edge is market-wide dip rebound on an hours scale** (median hold 2h from first fill), not coin-specific. The hedge legs lose 0.14%/trade because the market bounces during the hold, and hedged Grid is ~0. So never hedge the grid: the market component *is* the edge. A grid session's `entry_time` is its ARMING time, so any hedge or timing analysis must start at the first bar that reaches the fill price. Hedging from arming shorts the dip the grid then buys and fabricates a t_day of 16.
- **GridShort**: rejected by `edgetest` before and after the maker fix; nothing survives hedging.
- **FadeShort as one basket short (`fsmarket` book, 2026-09-26).** Signal = the fraction of live OosCoins where raw (ungated) FadeShort holds an open short at the bar close. Position = short that fraction of gross in an equal-weight top-40 liquid basket, one bar later, maker, re-traded on a 0.05 move or the daily basket refresh (all fixed in advance). Result: +12.8%/yr but t_weekly 0.93, Sharpe 0.36, vol 35%, maxDD −49%, lumpy years (2022 +43, 2023 −21, 2025 +59, last 10% −26%/yr). **The timing is real:** 20 time-shifted copies of the same signal give −9.8%/yr median (max +7.3), so the real one sits at z = +3.9. A constant short of the same average size loses −6.9%/yr to the basket's drift. On BTC alone the same signal loses (−6.2%), so the call is about alts. Verdict: **not significant as a book**. The drift of a survivor-only basket (37 delisted coins missing, which biases a short against itself) eats half the timing. It is better used as an input than as a standalone position.
- **The four retired strategies as grid signals: no** (`trade_log_edge.py --grid-signals`, 2026-09-26). Their `.DISABLED_` genotypes were re-enabled in a throwaway worktree only, to dump raw signals. Grid/GridShort sessions were split by each strategy's activity at arming. Across the 10 tests the largest |z| is 1.52 against a Bonferroni bar of 2.8. Four of the eight retired-strategy tests have the wrong sign (DipLong activity *hurts* Grid, z −1.20) and most flip between halves. Hedged, all four lose in every split (e.g. RipShort t_day −10.1, SwingLong −5.3). There is no coin-level edge, and unlike FadeShort they carry no usable market call. Keep them retired, as signals too.
- **Grid with textbook settings, no GA** (`genotypes/textbook/grid_best_genotype.json`, 2026-09-26). It sits in a subdirectory, so no loader globs it; to measure it, copy it over `grid_best_genotype.json` in a throwaway worktree. Settings: ADX<20 (Wilder), 3 rungs 0.75 ATR apart each selling one rung up, static anchor, stop one step past the last rung, every other filter at its loosest bound. `edgetest` on OosCoins with maker fees: 39,227 trades, PF 1.11, +0.052%/trade (the GA genotype: 6,148 at +0.164%). Day-clustered t: train-time 3.18, val −0.87, test 2.07. Acceptance gate: accept, ΔSharpe +0.36, identical to the GA genotype. Rolling-gate book Sharpe 0.95 vs 0.94. **The grid's edge does not come from the search:** a zero-trial textbook version contributes the same to the book. It trades far more often for less per trade, and val is its weak split.
- **Regime conditioning (`dotnet run -- hmmdump` → `reports/btc_regime_series.csv`; `scripts/regime_edge.py`).** Neither the legacy regime nor the HMM state helps Grid or carry through the fixed walk-forward gate (trade only while the current state's trailing-180d closed P&L is positive). GA Grid: every HMM state is positive in both halves except s0 (88 trades); the HMM gate beats only 6% of random same-fraction gates. Textbook Grid: HMM gate beats 84% of random but earns less in total (1,529 vs 2,030 always-on). Carry: best gate beats 68% of random. HMM states *disagree* between OosCoins and BacktestCoins (s1: −0.053%/day vs +0.023%), which is what an unstable conditioning looks like. The one consistent pattern: carry earns most on legacy-Bear days on both universes (t 1.5 / 2.8, positive in both halves), and HMM s2:Bear agrees (t 2.2 / 2.3). That pattern is not a tested gate. Caveat on any HMM result: Baum-Welch fitted the emissions on the whole history, so the states are only filter-causal, not fully out-of-sample.
- **HMM anchor: BTC vs covariance PC1 (`scripts/factor_hmm.py`, 2026-09-26).** The repo's HMM is ONE model on BTC's features, not per coin. `factor_hmm.py` fits the same kind of HMM twice with identical code and close-only features: once on BTC, once on a daily point-in-time PC1 index of the universe. Both run through `regime_edge.py --regimes ... --since 2021-01-13`. Result: **the covariance anchor does not rescue regime gating.** Gated-by-state total P&L vs always-on — Grid: C# BTC 804, py BTC 781, PC1 840 vs 1,007 always-on (beats-random 6% / 2% / 24%). Carry OOS: 87 / 95 / 72 vs 104. Carry BacktestCoins: 52 / 44 / 53 vs 72. No series beats always-on anywhere, and the beats-random ranks flip between universes. Why covariance helped the hedge but not this: the hedge removes co-movement, a variance problem that covariance answers directly. The gate has to forecast P&L from state, and neither book's P&L depends on state strongly or stably enough for any anchor to find.
- **FadeShort activity as a grid gate: not worth wiring.** With arming bucketed by FadeShort's open-trade count against its own trailing 30d, the grid is weakest in the high-activity third (+0.110%/trade vs +0.174%/+0.236%) but still profitable there (PF 1.21). The gap sits mostly in the first half of the data, and GridShort shows no pattern.

Under taker costs (the default) pairs on OOS was −4.6%/yr: its 172x/yr turnover makes execution the whole answer. Survivorship: 37 of the 101 OosCoins are delisted and Bybit returns no history for them, so the OOS universe is survivors only.

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
