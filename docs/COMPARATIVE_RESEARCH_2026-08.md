# Gravity-gen2 — Comparative Research Pass

**Date:** 2026-08-08 · **Commit reviewed:** `5e18f79` (branch `claude/adversarial-code-review-gcl35k`)
**Method:** four parallel research agents (web + source), findings then re-verified against the repo by the orchestrator.

---

## 0. Three framing corrections

Before the gap tables. Each of these was stated as a premise, each is contradicted by the code, and each changes what "fix this" means. All three are **verified directly**, not agent-reported.

### 0.1 There is no island model

`_migrationInterval` appears in 10 GA files. In every one it gates only a `Console.WriteLine`:

```csharp
if (_verbose && (gen + 1) % _migrationInterval == 0)     // DipLongGA.cs:224
    Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First()}{tag}");
```

`grep` for `List<List<`, `subPop`, `deme`, `migrate`, `topology` across `src/` and `commands/` returns **zero** structural hits. `eliteIsland` is `population.Take(_eliteCount)` — the top-N slice of one panmictic population, recomputed from scratch every generation.

This matters because inter-deme migration is *the* canonical defence against premature convergence. The system is named as though it has that defence and has never had it.

### 0.2 Calmar is not in the fitness function

`FitnessConfig.cs` sets `CalmarW = 0.0` and `PfW = 0.0`. `FoldScoreHelper.cs:106-107` computes `calmar` and `pfStat` and then multiplies them by zero. The live risk-adjustment terms are **Sharpe (gated) and Sortino only**.

Calmar *is* live in exactly one place — `RegimeRouterGA.cs:337` returns `calmar * coverageRatio`. So the router optimises Calmar while the six strategies feeding it do not.

### 0.3 The "5-fold walk-forward CV" has no test fold

`DipLongGA.cs:139-176` and its five siblings iterate `f = 0..k-1` and aggregate **all k folds** into one fitness number. There is no train-on-4 / test-on-1 step. Every fold is selection signal.

The folds impose a *dispersion penalty*, which is useful, but it is not cross-validation and it produces no out-of-sample estimate. A consequence: the `EmbargoPct = 0.05` gap separates two in-sample folds, so it is not preventing leakage — it is discarding 5% of bars.

---

## 1. Summary gap table

Priority is by expected effect on real capital, not by tidiness. **V** = verified directly by the orchestrator; **A** = agent-reported, not independently re-checked.

| # | Area | Gap | Status | Pri |
|---|---|---|---|---|
| G1 | Crypto | Funding sign **inverted** for all 3 long strategies; live in `papertrade` | Missing | **P0** V |
| G2 | Risk | `DdEntryGatePct` dimensionally inert — gate needs 613% drawdown to fire | Missing | **P0** V |
| G3 | Risk | `oosbacktest` sizes positions from the returns it then measures | Missing | **P0** V |
| G4 | GA | Seeded init makes 63/80 individuals byte-identical at gen 0 | Missing | **P0** V |
| G5 | GA + Stats | Post-GA TPE optimises a *different objective*, accepted on a cross-scale comparison | Missing | **P0** V |
| G6 | Stats | PBO test fed coins, not configurations — cannot see the ~10⁵-config search | Partial | **P0** A |
| G7 | Stats | DSR gate cannot fail: `n` is a pooled correlated trade count | Partial | **P0** A |
| G8 | Stats | Trade count passed as `candleCount` → hidden √n bonus in fitness | Missing | **P0** V |
| G9 | Stats | Fold aggregator non-monotone; 1 surviving fold scored verbatim | Partial | **P0** V |
| G10 | Risk | `SizeMult`, `VolatilityWeightedRotator`, `RankedPortfolioSim` computed, never sized on | Partial | P1 V |
| G11 | Risk | DynamicGuard absent from `combinedbacktest` / `oosbacktest` | Partial | P1 A |
| G12 | Crypto | Real funding never reaches any backtest or any GA | Missing | P1 V |
| G13 | Crypto | Cost model split across two layers; GA trains on 0.185pp, report says 0.285pp | Partial | P1 A |
| G14 | GA | Selection pressure k=4 ⇒ takeover in ~4.2 generations of a 150-gen budget | Partial | P1 A |
| G15 | GA | Stagnation "boost" raises probability, never step size; latches permanently | Partial | P1 V |
| G16 | GA | Unseeded RNG — no training run is reproducible | Missing | P1 V |
| G17 | Stats | `HolmBonferroni` implemented, unit-tested, **never called** | Missing | P1 V |
| G18 | Stats | 3 of 6 GAs pick the champion by argmax over 15 elites on the val set | Partial | P1 A |
| G19 | Stats | `TailRatioBonus` pays for one huge winner (tailN = 1 at fold sizes) | Missing | P1 V |
| G20 | Risk | P&L booked at entry time → reported max drawdown understated | Partial | P1 A |
| G21 | Crypto | Funding interval hardcoded 8h; BTC rate proxied to ~190 symbols | Missing | P1 A |
| G22 | Stats | Four GAs inline their own fitness — 6 config weights inert for them | Missing | P1 A |
| G23 | GA | Crossover cannot interpolate; no `cxpb`; no diversity metric anywhere | Missing | P2 A |
| G24 | Crypto | No liquidation model — **non-binding at 0.30× gross**, guard only | Missing | P2 V |
| G25 | Crypto | Universe is a today-snapshot (MATIC+POL both listed; AGIX post-merger) | Missing | P2 V |
| G26 | Risk | No risk-of-ruin or ruin Monte Carlo; CVaR never allocates capital | Missing | P2 V |
| G27 | Docs | CoevolveGA docs describe co-training; code freezes all strategies | Missing | P2 A |

**The cross-cutting theme.** G1, G2, G5, G10, G11, G17, G27 and §0.1–0.3 are all the same failure mode: *a mechanism that is named, documented, and computed, but not connected*. That pattern is more dangerous than an absent feature, because it passes review. Seven of nine P0/P1 items in the risk and stats sections are of this kind.

---

## 2. Risk management

### What the field does

- **Risk is normalised by stop distance, not applied as raw notional.** Jesse computes `size = ((risk_pct/100 × capital) / |entry − stop|) × entry`, and its `kelly_criterion` output is meant to feed *through* that, not to be used as a notional fraction. — [jesse/utils.py](https://raw.githubusercontent.com/jesse-ai/jesse/master/jesse/utils.py)
- **Drawdown control is an event-driven halt layer, separate from sizing.** freqtrade ships `MaxDrawdown`, `StoplossGuard`, `CooldownPeriod`, `LowProfitPairs` — binary lockouts with an explicit recovery clock, not continuous size scalars. — [protections.md](https://raw.githubusercontent.com/freqtrade/freqtrade/develop/docs/includes/protections.md)
- **Capital allocation is reserve-aware and bounded.** freqtrade's `tradable_balance_ratio` (default 0.99) holds back a reserve; total engaged capital is a stated quantity. — [configuration.md](https://raw.githubusercontent.com/freqtrade/freqtrade/develop/docs/configuration.md)
- **Volatility targeting scales exposure by inverse realised variance.** — [Moreira & Muir, *J. Finance* 2017](https://onlinelibrary.wiley.com/doi/abs/10.1111/jofi.12513) *(domain blocked from sandbox; search-index attested)*
- **Institutional stacks separate sensitivity from tail measures.** gs-quant's `risk/measures.py` defines no VaR or ES at all — tail risk lives in separate scenario contexts. — [gs-quant measures.py](https://github.com/goldmansachs/gs-quant/blob/master/gs_quant/risk/measures.py)

### What Gravity-gen2 does

`Simulator.ComputeConfidence` (`Simulator.cs:20-32`) computes half-Kelly and returns it clamped to [0,1]. That value is used **directly as a fraction of equity**: `desiredFrac = min(conf × kellyMultiplier, maxPositionFrac)` (`Simulator.cs:125`). Caps: `MaxTotalExposurePct = 0.30`, `MaxDirectionalConcurrent = 20`, per-position 0.05. Drawdown brake `ddScale = max(0.20, 1 − currentDd/0.15)` is live in every capped sim.

### Alignment

| Practice | Status | Note |
|---|---|---|
| Fixed-fractional cap | Aligned | 0.05/position + 30% total headroom, always on |
| Fractional Kelly | Partial | Formula correct, but consumed as *notional* — not divided by stop distance as Jesse does |
| Kelly estimated out-of-sample | **Missing** | `OosBacktest.cs:227` uses the window it measures; `CombinedBacktest.cs:303` does it correctly |
| ATR/stop-normalised sizing | Missing | The `RiskCapFrac` overload implements it but only `BacktestCommands` reaches it |
| Volatility targeting | Partial | DynamicGuard's ATR tiers are a coarse BTC-wide proxy, 4 of 8 strategy labels |
| Drawdown throttle | Aligned | `ddScale` taper, never overridden off |
| Drawdown **halt** w/ recovery clock | Missing | The only halt-style control is G2, which cannot fire |
| Risk parity / correlation-aware | Missing | First-come-first-served on entry timestamp |
| Risk of ruin | Missing | `grep -rni ruin` → one comment |

### Gaps

**G2 (P0) — `DdEntryGatePct` is inert across its entire search space.**
`Simulator.cs:287` tests `currentDd > ddLongEntryGatePct` where `currentDd = (peak−balance)/peak`, a **fraction** in [0,1]. But `DynamicGuardGenotype.cs:36` bounds the gene to `{2, 15}` and the trained value is **6.128** — i.e. the gate demands a 613% drawdown. The disabled default is 15 (1500%). `ToString()` prints `DdGate=613%` in every training log. The doc comment says "portfolio DD fraction … (2–15)", self-contradictory.
*Fix:* divide by 100 at the call sites, or change `Bounds` to `{0.02, 0.15}` and the default to `1.0`. **Then retrain DynamicGuard** — its other 14 genes were fitted alongside a free-riding no-op.

**G3 (P0) — `oosbacktest` sizes from the data it measures.**
`OosBacktest.cs:227`: `conf = ComputeConfidence(vRet)` where `vRet` is that coin's full OOS series, and the same `conf` rides into `allTradesForExposure` (`:535`) as the position fraction. Same at `:267, :311, :356, :401, :446, :636, :676`. `CombinedBacktest.cs:303` gets this right (uses train-window `tRet`). Reported OOS CAGR and max-DD are both optimistic by an unquantified amount.
*Fix:* derive `conf` from a leading slice only, mirroring `CombinedBacktest.cs:290-303`.

**G10 (P1) — Three documented risk controls never touch position size.**
`RegimeRouter.SizeMult` is computed at `RegimeRouter.cs:271-286` and declared "confidence-scaled position multiplier" — its only consumer outside the router is a JSON status field at `PapertradeCommands.cs:624`. Backtests use `session.IsActive(...)` boolean gating only. CLAUDE.md's claim that the early-bear ramp "scales FadeShort/RipShort sizing" is not delivered by any code path. `VolatilityWeightedRotator` is wired into `papertrade` but its output goes to console and JSON, never to size — and its trained genotype has `AtrWeight: 4.6e-05` against `RegimeWeight: 0.840`, so the ATR signal contributes 0.005% of the blend. `RankedPortfolioSim` is wired only into `GridCommands.cs:445`.
*Fix:* multiply `conf` by `routing.SizeMult` in the trade-list construction, or delete the field and the doc claim.

**G11 (P1) — DynamicGuard is absent from the two commands CLAUDE.md tells you to run.**
`grep -rn "DynamicGuardSession" commands/` returns no hits in `CombinedBacktest.cs` or `OosBacktest.cs`. `FullTest.cs:692-695` reimplements it inline. CLAUDE.md's "Applied in live papertrade and all backtests" is false on both counts — in `papertrade`, `GetMult` is called at `:248` purely to print `"Guard: STRESS ×0.42"`.

**G20 (P1) — P&L is booked at entry time.** `Simulator.cs:132/206/317` apply the full trade return at *entry*, while exposure is reserved until `entryTime + hold` (48–72h). A cohort of correlated positions has its losses realised at staggered entry stamps rather than together at exit, so peak-to-trough is smoother than reality and `ddScale` reacts to P&L that has not happened yet.

**G26 (P2) — No ruin estimate.** The machinery exists: `SimulateExposureCappedWithCurve` returns a full equity curve. Resampling the capped trade list through it would give P(DD > 25%) / P(DD > 40%) — the number that actually says whether the 0.30 cap and 15% brake are calibrated.

---

## 3. Genetic algorithm design

### What the field does

- **Island models are defined by real second-order structure** — μ demes, a topology graph, interval τ, migration rate, emigrant/immigrant policies. Lässig & Sudholt construct functions where migration gives polynomial runtime and islands *without* migration need exponential. — [Soft Computing 2013](https://link.springer.com/article/10.1007/s00500-013-0991-0)
- **Topology density trades diversity against speed.** Sparse rings preserve diversity; dense topologies homogenise. — [Ruciński, Izzo & Biscani, *Parallel Computing* 36(10-11), 2010](https://www.sciencedirect.com/science/article/abs/pii/S0167819110000487)
- **Selection pressure is quantified by takeover time**, τ ≈ (ln N + ln ln N)/ln k. — [Goldberg & Deb 1991, FOGA](https://www.cse.unr.edu/~sushil/class/gas/papers/Select.pdf)
- **DEAP** keeps one population but exposes swappable operators, including arithmetic blenders `cxBlend`/`cxSimulatedBinaryBounded` for real-valued genes and `selLexicase` as an explicit diversity-preserving selector. — [algorithms.py](https://raw.githubusercontent.com/DEAP/deap/master/deap/algorithms.py), [crossover.py](https://raw.githubusercontent.com/DEAP/deap/master/deap/tools/crossover.py)
- **PyGAD** defaults to `keep_elitism=1` and ships `mutation_type="adaptive"` and `stop_criteria=saturate_N` as first-class. — [pygad.py](https://raw.githubusercontent.com/ahmedfgad/GeneticAlgorithmPython/master/pygad/pygad.py)

*Sourcing note: DEAP and PyGAD were read directly from source. The academic citations were surfaced via search index; the sandbox egress proxy blocks arxiv/sciencedirect/springer, so those are attributed as reported, not verified against full text.*

### What Gravity-gen2 does

One panmictic population. Production sizes: 80 × 150 for the six main strategies, 60 × 100 for Grid/GridShort. **Elitism is the hardcoded `Take(5)` = 6.25%** (`DipLongGA.cs:248`) — `_eliteCount` (15) only sizes the reporting slice and the BO seed set, so raising it changes nothing about the search. Selection is `TournamentSelect(pop, k = 4)` (`DipLongGA.cs:409`), except SwingLong which uses truncation (`SwingLongGA.cs:200`) and DynamicGuard which is a (8+32) ES with no crossover (`DynamicGuardGA.cs:38`). Crossover is discrete per-gene `Pick` at probability 1.0 — never interpolates. Mutation is fixed-step creep with a decaying *rate* and a constant *step*.

### Alignment

| Practice | Status | Note |
|---|---|---|
| Island / multi-deme structure | **Missing** | §0.1 |
| Migration topology / rate / policy | Missing | No second population exists |
| Tournament selection | Aligned | k=4, matches DEAP/PyGAD semantics |
| Selection pressure calibrated to budget | Missing | τ≈4.2 gens vs a 150-gen budget; never measured |
| Consistent operator across siblings | Partial | 6 tournament, 1 truncation, 1 ES |
| Elitism as a tuned knob | Missing | Ratio hardcoded; the knob that *looks* like it does nothing |
| Blending crossover | Missing | Discrete `Pick` only, on 14 real-valued genes |
| Adaptive mutation | Partial | Rate anneals; **step size never does** |
| Restart / cataclysm on stagnation | Missing | G15 |
| Sharing / crowding / clearing | Missing | Zero occurrences |
| Any diversity metric | Missing | Only `meanFitness` written to JSON, never read back |
| Reproducible RNG | Missing | G16 |
| Parallel evaluation safety | Aligned | `Parallel.ForEach` bodies are RNG-free; not a nondeterminism source |

### Gaps

**G4 (P0) — Seeded initialisation destroys 79% of the population before generation 0.**
`DipLongGenotype.cs:53`: `T Seed<T>(T random, T seeded) => seed == null ? random : seeded;` — when a seed is present, **every gene returns the seeded value** and the `rng` draw is evaluated and discarded. So `Random(rng, seed)` returns an exact copy. Then `DipLongGA.cs:191-203` builds 80 copies, replaces index 0 with the clamped seed and indices 1–16 with 25%-rate creep mutants, leaving **indices 17–79 as 63 byte-identical duplicates**. Distinct starting points ≤ 17; duplicate fraction 78.75%.

Systemic — same pattern in `FadeShortGenotype.cs:45,180,261`, `FadeLongGenotype.cs:47`, `SwingLongGenotype.cs:102,168,190`, `RipShortGenotype.cs:51,212,281`, `GridGenotype.cs:54`. And seeding is the *normal* path: every train command seeds from the saved genotype when its fitness > 0 (`LongTrainCommands.cs:386`).

`RegimeRouterGenotype.cs:91-92` is the one correct implementation in the repo:
```csharp
if (seed != null && rng.NextDouble() < 0.3) return seed.Mutate(rng, 0.5);
return new() { /* fully random */ };
```
*Fix:* adopt that pattern everywhere. *Effect:* this is the difference between a GA and a 150-generation hill-climb from the previous genotype — and it is the concrete mechanism behind CLAUDE.md's "a rerun can land in a worse basin."

**G14 (P1) — Selection pressure is the binding constraint, not budget.**
At N=80, k=4: takeover τ ≈ 4.2 generations; 68.4% of parents come from the top quartile, 6.25% from the bottom half. With crossover probability 1.0 and both parents converged by ~gen 5–10, the remaining ~140 generations are a 5-elite fixed-step creep walk — roughly 97% of 12,000 evaluations spent on descendants of one individual.

**Directly answering "bigger population or more generations?": neither.** More generations is near-worthless — diversity is gone by gen 10 and `baseMutRate` is 0.054 by gen 149. Bigger population is the only knob that buys basin diversity, *but it is neutralised while G4 stands*, since 78.75% of any population size is duplicates. Correct order: **G4 → lower k / duplicate rejection → raise population → generations last.**

**G15 (P1) — The stagnation boost cannot escape a basin and latches on.**
`DipLongGA.cs:211-212` doubles the per-gene *probability* on `stagnantGens >= 15`; the step stays at a hardcoded `scale` (~±7% of range, `DipLongGenotype.cs:97-106`). A doubled rate on a fixed creep cannot cross a basin wider than the creep. And since elitism makes `bestFitnessSeen` monotone and `stagnantGens` resets only on strict improvement, once it fires it stays on.
*Fix:* CHC-style cataclysm — keep top 5, reinitialise the other 75 from `Random(rng, seed: null)`, reset `stagnantGens`.

**G16 (P1) — No training run is reproducible.** `private readonly Random _rng = new();` unseeded in all 10 strategy/router GAs, with no seed parameter on any constructor. `DynamicGuardGA.cs:13` is the only seeded one. Record the seed in the genotype DTO.

**G23 (P2) — Crossover cannot interpolate.** All 14 genes are real- or integer-valued with explicit `Bounds`, but offspring values are always verbatim parent values. P2 rather than P1 only because it is a no-op while the population is converged — it pays off after G4 and G14.

**G27 (P2) — CoevolveGA docs contradict the code.** CLAUDE.md describes a 4-cycle loop with FadeLong and DipLong co-training. `CoevolveGA.cs:47` runs `RedQueenRounds = 8`, and its own header at `:19` says "Strategies never change" — only Router and Guard evolve; strategy seeds pass through unmodified at `:130`.

---

## 4. Statistical / fitness evaluation

### What the field does

- **Deflated Sharpe Ratio** corrects the observed Sharpe for non-normality *and for selection bias from the number of trials N*, with E[max SR] ≈ (1−γ)Φ⁻¹(1−1/N) + γΦ⁻¹(1−1/(Ne)). The paper is explicit that the *effective* number of trials is not the literal backtest count when trials are correlated. — [Bailey & López de Prado, SSRN 2460551](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2460551)
- **PBO via CSCV** partitions a T×**N-configuration** matrix into S time-blocks, enumerates C(S,S/2) splits, takes the IS-argmax *configuration* and records its OOS rank. The units ranked are alternative configurations over a *common* time partition. — [SSRN 2326253](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2326253)
- **Minimum Backtest Length**: the more configurations tried, the higher the probability the backtest is overfit; overfit strategies tend to produce *negative* OOS performance when the series has memory. — [*Notices of the AMS* 61(5)](https://www.ams.org/notices/201405/rnoti-p458.pdf)
- **Multiple-testing haircuts are non-linear** — marginal Sharpes are destroyed, high ones only dented; the flat-50% rule of thumb is "a serious mistake." — [Harvey & Liu, JPM 2015](https://people.duke.edu/~charvey/backtesting/)
- **Purging removes training observations whose labels overlap the test set in time**; embargo drops a trailing block. CPCV yields many backtest paths and empirically lower PBO than walk-forward. — [Purged cross-validation](https://en.wikipedia.org/wiki/Purged_cross-validation), [quantinsti](https://blog.quantinsti.com/cross-validation-embargo-purging-combinatorial/)
- **Objective failure modes**: Sortino's downside deviation is estimated from a subsample so its SE is much larger than Sharpe's at equal n; Calmar depends on max drawdown, a single extreme order statistic capturing depth but not duration. — [Calmar ratio explained](https://www.quantt.co.uk/resources/calmar-ratio-explained)

### Multiple-testing exposure, quantified

| Stage | Arithmetic | Evals |
|---|---|---|
| FadeShort universal + TPE | 80×150 + 60 | 12,060 |
| + per-cluster GAs | 60×100 × ~3 | ~18,000 |
| DipLong / FadeLong / SwingLong / RipShort | | ~24,000 |
| Grid, Router, Guard, ExpandingWindow | | ~15,000 |
| **One full retrain** | | **≈ 70,000** |
| `coevolvetrain` | 8 rounds × 3 | **+ ≈ 112,000** |

Against this, `FullTest.cs:1300` hardcodes `nTrials = 1000` with the comment "GA pop × gens ≈ 1000" — **off by 1–2 orders of magnitude**. And none of it counts operator-level selection (keeping the better of several retrains), which is another uncounted argmax layer.

### Alignment

| Practice | Status | Note |
|---|---|---|
| DSR implemented | Partial | Correct impl at `StatisticalTests.cs:92-122`; a second, unit-inconsistent one at `StrategyStats.cs:172-182` is what reaches the frontend |
| Trial count reflects the search | **Missing** | 1,000 hardcoded vs ~70,000 actual |
| Effective-trial adjustment | Missing | No correction for GA trial correlation or 93 co-moving coins |
| PBO / CSCV | Missing in substance | G6 |
| Purging | Missing | Nothing anywhere |
| Embargo | Partial | Applied consistently, but separates two in-sample folds (§0.3) |
| Walk-forward validation | Missing (mislabelled) | §0.3; `ExpandingWindowValidation` is the only real WF and is report-only |
| Held-out kept out of selection | Partial | Clean in FadeShort/RipShort/Grid; violated in DipLong/FadeLong/SwingLong |
| Non-normality correction | **Aligned** | `StatisticalTests.cs:98-108` implements the BLP PSR denominator correctly |
| Multiple-testing correction | Missing | G17 |
| White's Reality Check | Partial | Correct block bootstrap, but draws indices *per model* — destroys the cross-model dependence the max-statistic null needs (`StatisticalTests.cs:211-232`) |
| Single canonical fitness | Missing | G22 |
| Fold-aggregation monotonicity | Missing | G9 |

### Gaps

**G5 (P0) — The post-GA TPE stage optimises a different objective.** *(Found independently by two agents.)*
`TrainCommands.cs:206-221`, `LongTrainCommands.cs:399-412 / 606 / 1030 / 1164`. The `evaluate` lambda returns `ts.Average()` — raw mean per-trade return over the whole train span, with **no folds, no `MinTradesPerFold`, no drawdown/quality/retention terms, no regime gate**. A genotype taking 3 lucky trades scores arbitrarily high. Then:
```csharp
if (boGeno.Fitness > best.Fitness) { best = boGeno; }
```
compares that scalar (~0.3–1.0) against a canonical fold-aggregated score (10–10,000). Whether TPE overwrites the GA winner is decided by the accidental relative magnitude of two unrelated numbers. Additionally, seeded with one observation, `BayesianOptimizer.cs:30-31` returns uniform-random points while `history.Count < 12`, so 11 of 60 iterations are random over the full bounds box.
*Fix:* pass the GA's own `Fitness` delegate (the *inner* BO at `DipLongGA.cs:271` already does this), or delete the pass.

**G6 (P0) — PBO is fed coins, not configurations.**
`ProbabilityOfBacktestOverfitting` is called with one return list *per coin* under a **single fixed genotype**. CSCV requires N alternative configurations. As wired, the "IS-optimal config" is the IS-best *coin*, so it measures cross-sectional momentum, not overfitting. It has no access to any alternative genotype and **cannot see the ~10⁵-configuration search at all.** Secondary: blocks are cut per-config by trade count (`:146-148`), not on a common calendar partition.

**G7 (P0) — The DSR gate cannot fail.** `PrintReport` pools trades across coins and `CombinedBacktest.cs:1338` across all six strategies. At n ≈ 20,000 pooled trades, √(n−1) ≈ 140 swamps the selection hurdle and DSR prints 1.000 for any positive Sharpe at any T. The trades are not independent — up to 20 same-direction positions on co-moving perps. Applying n_eff = n/(1+(m−1)ρ): at m=20, ρ=0.3, n_eff drops from 5,000 to 746 and DSR falls to 0.011. **The verdict flips from significant to not-significant entirely on a correction the code does not make.**

**G8 (P0) — Trade count is passed as `candleCount`.**
`FoldScoreHelper.cs:104-105` calls `Simulator.SharpeRatio(returns, n)` with `n = returns.Count`, into a parameter documented as a 5-minute candle count. The function returns `mean/std × √(candleCount/288)`, so the fold's "Sharpe" is scaled by √(trade count): **0.295× at 25 trades, 0.932× at 250** — a second frequency bonus stacked on the explicit `freqBonus`. The same function is called with genuine candle counts elsewhere (`OosBacktest.cs:1086`), so two incompatible unit conventions coexist.

This also biases `ExpandingWindowValidation`: IS and OOS Sharpe each carry the factor with n_is ≈ 2–5× n_oos, so a strategy generalising *perfectly* measures efficiency √(1/2)…√(1/5), mean **0.558**, against an overfit threshold of 0.5. The flag sits inside its own bias band.

**G9 (P0) — Fold aggregator is non-monotone, and rewards concentrating into one window.**
With f = mean − c·σ_pop, ∂f/∂s₁ = (1/k)(1 − c·z₁), so it *decreases* in the best fold whenever z₁ > 1/c. Max attainable z is √(k−1), so at k=3, c=0.75 it inverts (needs z > 1.33, max 1.41); at c=2.0 the inversion region is z > 0.5, which covers most ordinary configurations. Concretely at c=0.75, k=3: folds (10,10,30) → 9.596; (10,10,40) → **9.393**. Improving the best fold by 33% lowers fitness.

Separately, `AggregateFoldScores` returns a **single surviving fold's score verbatim**, so a genotype trading only in fold 3 scores 30.0 while one trading consistently at (20,25,30,35,40) scores 24.70. **Concentrating all activity into the single most favourable market window is strictly dominant.** For the bear-gated strategies this is exactly the failure mode CLAUDE.md already documents.

*Recommended fix (answering the question I posed):* the standard monotone formulation is a convex combination with a coherent risk measure — `λ·CVaR_{1/k}({s_f}) + (1−λ)·mean({s_f})`, λ ≈ 0.4. Both terms are monotone non-decreasing in every fold score, so it is monotone **by construction for any k and λ**, while still penalising dispersion because CVaR overweights the worst folds. Minimum viable alternative: `mean − c·std/√k` with the clamp ceiling lowered from 2.0 to ~1.1 — which is also the statistically correct form, since σ (not σ/√k) has been overstating the uncertainty in the mean by √k throughout. Either way, add a coverage penalty (`× survivingFolds/k`).

**G17 (P1) — Holm-Bonferroni is written, unit-tested, and never called.** Six strategies each get an independent `MonteCarloTest` p-value judged against α=0.05 in isolation; FWER across six is ≈ 0.26. The function exists at `StatisticalTests.cs:311-342`. This is the cheapest correct item on the entire list.

**G19 (P1) — `TailRatioBonus` pays for one huge winner.** `tailN = max(1, n/20)`; at `MinTradesPerFold` = 20–25 that is **1**, so the "5th/95th percentile ratio" is literally best-single-trade / worst-single-trade, and it is an **uncapped bonus** with `TailRatioW = 0.2` live. The objective currently rewards lottery-ticket outcomes — the opposite of what a tail term should do. `CVaRPenalty` has the same n problem (cutoff = 1 trade at n=25): sign and floor are now correct, but the estimator is unusable at these fold sizes. Gate both at n ≥ 100.

**G22 (P1) — Four GAs inline their own fitness.** `RipShortGA.cs:95-119`, `GridGA.cs:60-85`, `GridShortGA.cs:60-85`, `AccumulationGridGA.cs:55-71` don't call `FoldScoreHelper.Canonical`. So the six config weights are inert for them, `CVaRPenalty`/`TailRatioBonus` never apply, `statBonusCeiling` is 1.0 vs 1.5 elsewhere, and GridGA uses a materially different shape (wrMult slope 0.5 vs 3.0, ddDiv ×20 vs ×10, no quality/retention). `CoevolveGA` and `RegimeRouterGA` then combine outputs of these incomparable scales.

---

## 5. Crypto-futures-specific

### What the field does

- **Funding = premium index + clamped interest, charged on notional, only if open at the settlement tick.** Binance: `F = P + clamp(interest − P, ±0.05%)`, interest 0.01%/8h, capped ±3%. — [Binance FAQ](https://www.binance.com/en/amp/support/faq/360033525031+), [Bybit funding](https://www.bybit.com/en/help-center/article/Introduction-to-Funding-Rate)
- **Funding is structurally positive, and not always 8-hourly.** Q3-2025: positive **>92% of the time** across major venues. 2021 bull: +0.03% to +0.08%/8h. Interval is per-symbol — many alt perps 4h or 2h — and Bybit **auto-switches to hourly** when the rate hits its cap. — [BitMEX 2025Q3 Derivatives Report](https://www.bitmex.com/blog/2025q3-derivatives-report), [Bybit dynamic settlement](https://www.prnewswire.com/news-releases/bybit-launches-dynamic-settlement-frequency-system-for-perpetual-contracts-302598179.html)
- **Liquidation is mark-price-triggered and tiered**, and a stop-loss on last-traded price can be jumped by it. — [Binance liquidation price](https://www.binance.com/en/support/faq/how-to-calculate-liquidation-price-of-usd%E2%93%A2-m-futures-contracts-b3c689c1f50a44cabb3a84e663b81d93), [Bybit mark price](https://www.bybit.com/en/help-center/article/Mark-Price-Calculation-Perpetual-Expiry-Contracts)
- **Below liquidation sit insurance funds and ADL.** On 2025-10-10, ~$19bn liquidated in hours; Hyperliquid ADL'd **~$705M of winning trader PnL in 12 minutes**. — [Binance ADL](https://www.binance.com/en/support/faq/what-is-auto-deleveraging-adl-and-how-does-it-work-360033525471?hl=en), [FTI: Crypto Crash Oct 2025](https://www.fticonsulting.com/insights/articles/crypto-crash-october-2025-leverage-met-liquidity)
- **Impact is concave in size**: `I(Q) = Y·σ·√(Q/V)`. A flat bps constant is the degenerate case — it carries no σ term, so it cannot widen in the stressed regimes where σ triples. — [Bouchaud, square-root law](https://bouchaud.substack.com/p/the-square-root-law-of-market-impact), [arXiv 2311.18283](https://arxiv.org/pdf/2311.18283)
- **Survivorship bias inflates crypto backtests 50–200%.** — [CoinAPI](https://www.coinapi.io/blog/how-to-eliminate-survivorship-bias-in-crypto-backtesting)

### Gaps

**G1 (P0) — Funding sign inverted for all three long strategies. [FIXED]**
Six byte-identical private copies of `FundingPnl` previously existed in `DipLongSimulator.cs:249`, `FadeLongSimulator.cs:246`, `SwingSimulator.cs:672`, `RipShortSimulator.cs:452`, `FadeShortSimulator.cs:253`, and `GridShortSimulator.cs:66`. The three long copies were copy-pasted from the short version without flipping the sign, booking funding income for a cost they were actually paying:
```csharp
pnl += funding.GetRate(t) * 100.0;   // correct for a SHORT; a LONG pays
```
**Resolution:** All six copies have been deleted and consolidated into a single `FundingRateSession.PnlPct(entry, exit, session, isLong)` method that applies the correct sign rule: a positive rate charges longs and credits shorts. The consolidation has eliminated the duplication that was the root cause of the bug.

**G12 (P1) — Real funding never reaches any backtest or any GA.** `grep -c -i funding` returns **0** on `CombinedBacktest.cs`, `OosBacktest.cs`, `BacktestCommands.cs`, `TrainCommands.cs` and `LongTrainCommands.cs`. Every GA passes `funding: null` explicitly. So every committed genotype was selected under the flat fallback.

**How wrong is the fallback?** Using committed `MaxHoldCandles`:

| Strategy | Hold | Model charges | Real @ floor | Real @ bull +0.05%/8h | Error |
|---|---|---|---|---|---|
| DipLong (long) | 30h | −0.038pp | −0.038pp | −0.188pp | **−0.150pp understated** |
| SwingLong (long) | 60h | −0.075pp | −0.075pp | −0.375pp | **−0.300pp understated** |
| FadeLong (long) | 65h | −0.081pp | −0.081pp | −0.406pp | **−0.325pp understated** |
| RipShort (short) | 52h | −0.065pp | **+0.065pp** | — | +0.130pp over-charged |
| FadeShort (short) | 110h | −0.138pp | **+0.138pp** | — | +0.275pp over-charged |

The longs are understated *exactly where it matters* — DipLong and SwingLong are bull-regime-gated by construction, which is the regime where funding historically ran +0.03% to +0.08%/8h. At the mid-range the miss is **0.15–0.33pp per trade, i.e. 53–115% of the entire modelled round-trip cost of 0.285pp.** Funding is a second transaction cost of comparable size, anti-correlated with the regime these strategies trade.

The shorts' "pessimism" is not uniform either: in a crowded-short bear (−0.05%/8h, exactly where `IsCrowdedShort` fires), a 52h RipShort really pays 0.325pp against a modelled 0.065pp — **5× optimistic in the one scenario the codebase already flags as dangerous.**

Also: the fallback prorates continuously (`−0.01 × heldHours/8`) while reality charges whole intervals. A 3-hour trade straddling 16:00 UTC pays a full interval in reality and 0.00375pp in the model.

**G13 (P1) — Cost model split across two layers, with a train/report mismatch.** Fees are *not* double-counted (`FeeExchange` is simulator-only), but slippage effectively is: an ATR-proportional estimate inside the trade return *plus* a flat 10bps at the portfolio level, from two independent assumptions with no single place stating the total. Worse, `grep -rln slippageBps src/strategies/` returns nothing — **GAs optimise against 0.185pp while backtests report 0.285pp, a 54% gap.** Every committed genotype was selected under the cheaper cost. Separately, DipLong/FadeLong/FadeShort charge `SlipK×atrPct` *once* while RipShort/SwingLong charge it *twice*, contradicting the shared "each side" comment at `SwingSimulator.cs:28`.

**G24 (P2) — No liquidation model, and this is currently fine.**
`grep -rni "leverage|liquidat|maintenance|margin"` returns 6 hits, all false positives. But gross notional ≤ 0.30 × equity, so **maximum gross leverage is 0.30×**. At 1× on allocated EUR, liquidation needs a ~100% adverse move; the widest modelled stop (RipShort, ~7.2% at 3% h4 ATR) is ~14× closer. Leverage at which liquidation would precede the stop:

| Strategy | Stop distance | Liq binds above |
|---|---|---|
| RipShort | ~7.2% | ~13× |
| SwingLong | ~6.0% | ~15× |
| FadeLong | ~3.1% | ~28× |
| DipLong | ~2.5% | ~33× |
| FadeShort | ~0.9% | ~70× |

Gross leverage would need to rise ~40× before this changed a single number. **Correctly deprioritised.** The only change worth making is a *guard*: assert `maxTotalExposurePct ≤ 1.0` and comment at `Config.cs:18` that 0.30 is what makes the missing model sound. The hazard is silent — raise that constant to chase returns and every backtest keeps reporting clean stop-outs where reality would liquidate.

Two related notes that are *not* about leverage: the 30% cap is enforced **at entry only** (`openPos` freezes `EurAllocated` and is never re-marked), so live notional drifts above 30% as winners run. And when headroom is exhausted a trade still enters with `posEur = 0` while counting toward trade count, win rate and PF — so those statistics include trades that deployed no capital.

**G25 (P2) — Universe is a today-snapshot.** `Config.cs:36-66` lists both `MATICUSDT` and `POLUSDT` as if independent; `OosCoins` contains `AGIXUSDT` (merged into FET mid-2024), plus `KLAYUSDT`, `WAVESUSDT`, `SPELLUSDT`. Symbols that lost their perp listing mid-sample never appear at all. Minimum fix: record each symbol's first/last cached candle and warn when history ends before the backtest window does.

---

## 6. Recommended order

1. **G1** funding sign — live in `papertrade`, one-line-per-file, no retrain needed to stop the bleeding.
2. **G4** seeded init — everything else about GA quality is downstream of this.
3. **G5** TPE objective mismatch — decides what lands in `genotypes/`.
4. **G9** fold aggregator — changes the objective, so it must land before any retrain.
5. **G2** DdEntryGatePct + **G3** OOS Kelly — both invalidate current numbers.
6. **G8** Sharpe units, **G17** Holm-Bonferroni, **G19** tail gates — cheap, and they make the existing reports mean something.
7. Then retrain everything, and only then look at G6/G7 (PBO/DSR), which are the tests that tell you whether the retrain was real.

**A caution on sequencing.** G5, G9 and G22 all change the selection objective. Every genotype in `genotypes/` is already flagged in CLAUDE.md as selected under a broken fitness; these findings extend that to the remaining strategies. Retrain once, after the objective is settled — not between each fix.

---

## 7. Verification status

Verified directly against the source by the orchestrator: §0.1, §0.2, §0.3, G1, G2, G3, G4, G8, G9, G10, G15, G16, G17, G19, G24, G25, G26, and the GA sizing / exposure-cap figures.

Agent-reported and **not** independently re-checked: G6, G7, G11, G13, G14, G18, G20, G21, G22, G23, G27, and the numeric tables in §4 (multiple-testing counts) and §5 (funding magnitudes, liquidation thresholds).

No claim here was validated by running the system — this container has no .NET SDK and no `candle_cache/`. Several agents' academic citations were surfaced via search index rather than fetched, because the sandbox egress proxy blocks arxiv, sciencedirect, springer and PMC; those are marked at the point of use.
