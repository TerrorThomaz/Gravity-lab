# Rigour rework — September 2026

**One sentence:** the system's apparent edge was a simulator bug, an extensive statistical apparatus
failed to detect it, a ten-minute null control found it immediately, and what survives is a
measurement that says there is no demonstrated edge.

> **This document was rewritten on 2026-09-24.** Its first version reported grade A, 13.1% CAGR and
> Sharpe 6.00. Those numbers were void — produced by the same-bar fill defect described in §1. They
> are preserved in git history as an example of how confident a well-deflated wrong answer can look.

---

## 1. The headline: same-bar fill lookahead

`GridSimulator` armed a grid at bar `i` using `ema[i]` and `atr[i]` — both of which include
`close[i]`, information that only exists once bar `i` has ended — and then filled every rung that
`lows[i]` had reached **during that same bar**. Live, those limit orders are placed at the close of
bar `i` and can first fill at `i+1`. The simulator was buying dips it already knew had happened.

Holding everything else fixed:

| | same-bar fill | next-bar fill (honest) |
|---|---|---|
| Grid | Sharpe 4.15, CAGR 4.7% | **Sharpe 0.67, CAGR 0.3%** |
| GridShort | Sharpe 4.69, CAGR 4.5% | **Sharpe 0.35, CAGR 0.1%** |
| Best book | CAGR 27.0%, Sharpe 8.31, DSR 1.000 | **CAGR 1.6%, Sharpe 1.14, DSR 0.023** |
| Grade | B (6/7) | **F (5/7)** |

**The entire apparent edge of the two strategies that survived every other filter was this defect.**
Every backtest this repo has ever produced inherited it.

It is a named, measured defect class. Zhang, Li, Peng & Chen (2026), *When Alpha Disappears: A
One-Switch Benchmark for Decision-Time Leakage in Financial Backtests* (arXiv:2605.23959) toggle
exactly this convention — their `EXEC_OPEN` switch — and report leakage gains of **+5.41 to +21.65
Sharpe** against clean reference Sharpes of 0.44–0.68. Our 4.15 → 0.67 sits inside that range.

**Audit result: the defect is confined to the grid family.** All four dual-timeframe simulators take
`h1Ref = ih1 - 1` (the last fully-closed hourly bar), `h4Ref = h1Ref/4 - 1`, and enter at
`m15[nextBar].Open` — the decision strictly precedes the fill. The bitter irony is that **the four
strategies retired earlier in the session had correct fill modelling; the two that "worked" did
not**, and were being compared against an inflated benchmark throughout.

`AccumulationGridSimulator` has the same defect and is **not fixed**: its `active = true` lives
inside the fill block, so deferring the fill disables the strategy rather than correcting it. Not
live, not loaded by `edgetest`, documented in-file, and it must pass the null gate before revival.

---

## 2. Why none of the statistics caught it

Deflated Sharpe, probability of backtest overfitting (CSCV), White's Reality Check, walk-forward
gating, block bootstrap and trial counting all ran clean while the bug was live. They could not
have caught it: **every book they compared shared the same biased fill model.**

> Those methods answer *"given that this measurement is trustworthy, was the selection procedure
> honest?"* Nothing in that stack ever establishes the antecedent.
>
> **Null controls establish the antecedent. Statistics operate on it. Run them in that order.**

What actually found it: a random-anchor control built in ten minutes, which scored an impossible
Sharpe 14.8 — and impossible results are informative. That is now `RandomWalkNullTests`, the gate
described in §5.

---

## 3. Defects found and fixed

| # | Defect | Impact | Status |
|---|---|---|---|
| 1 | **Same-bar fill in the grid family** | Sharpe 4.15 → 0.67; voided every result in the repo | Fixed (`fillOnArmBar: false`) |
| 2 | `IsActive` used the SIMFAM gate, `SizeGate` fell through to legacy thresholds and returned **0** | **56–71%** of rows in committed report CSVs were zero-notional: took a slot, entered the book, earned nothing | Fixed — one `Resolve()` |
| 3 | `SizeGate`: `w < 0.50 → 1.0` — size peaked at the router's **lowest** conviction | Train and serve shared the rule, paying the GA to drive favorability under 0.5 and neutralise its own sizing layer | Fixed |
| 4 | `IsActive`/`Weight` mutated hysteresis state per call on a session shared across all coins | Same trade routed differently depending on which coin was evaluated first | Fixed — precomputed per bar |
| 5 | `Simulator.SharpeRatio` returned 0 below PF 1.3; not annualised | Losing books printed `0.00`, indistinguishable from flat | Fixed — `ScreenedSharpeRatio` keeps the old floor |
| 6 | `FoldScoreHelper.PerTradeSharpe` — same cliff, inside **GA selection** | `SharpeW=0.5` is the largest *live* stat weight (`PfW`/`CalmarW` default to 0) and every strategy sits at PF 1.00–1.20 | Fixed — ramps over [1.0, 1.3] |
| 7 | `MarkToMarket` accrued open P&L **linearly** | Hid intra-hold drawdown; inflated Sharpe most for long holds | Fixed — opt-in real price paths |
| 8 | GA fitness blind to intra-hold path risk | A path-blind retrain pushed Grid's `MaxHoldCandles` **53 → 164**; path-aware it chose **30** | Fixed — `maePct` |
| 9 | No count of candidate evaluations | DSR is decided by trial count and the number was a guess | Fixed — `GaTrialCounter` |
| 10 | Per-cluster GA stage: **62%** of a `train` run's trials on ATR-band variants no OOS report loaded | Deflated the base genotype to pay for unmeasured variants | Retired |
| 11 | `edgetest` ignored `PortfolioReplay.DefaultCaps` | **Our own harness bug.** One strategy held 8,919 slots on arrival order and starved 2,331 trades — an artifact that looked like a defective strategy | Fixed |
| 12 | `maePct` added to `CanonicalRegime` and then **ignored** | Introduced and caught in the same session | Fixed + test |
| 13 | Test suite flaky under parallel collections (`Console.Out`, `BtcRegimeProbe`, `GRAVITY_HMM`) | ~1 run in 3 failed on a different test each time; red results carried no information | Fixed — `ProcessGlobalCollection` |

---

## 4. Where the system actually stands

Honest measurement, never-trained coins, walk-forward gated, real price paths, next-bar fills:

```
strategy     per-trade Sharpe   portfolio annSharpe    CAGR    maxDD
Grid                   0.0399                 0.70     0.4%     0.7%
FadeShort              0.0157                 0.58     2.3%     5.3%
GridShort              0.0101                 0.35     0.1%     0.6%
```

**None of these is statistically distinguishable from zero.** Using Lo (2002),
`SE(SR) ≈ √((1+SR²/2)/T)` on ~3 years gives SE ≈ 0.6, so:

| | Sharpe | t |
|---|---|---|
| Grid | 0.70 | 1.1 |
| FadeShort | 0.58 | 0.93 |
| GridShort | 0.35 | 0.59 |

Clearing the t = 3 hurdle Harvey/Liu/Zhu argue a new factor requires would need **Sharpe ≈ 1.9** on
this sample. And that is *before* deflating for search.

**Retired, and confirmed by retraining from scratch under the corrected fitness with no seed:**

```
RipShort   Sharpe  0.11      SwingLong  Sharpe -0.16
DipLong    Sharpe -0.24      FadeLong   Sharpe -0.84
```

DipLong reaches PF 0.91 in Bull — its own home regime — on ~3,000 trades, where the standard error
on the mean trade is ~0.09pp. That is a measured negative expectancy, not a tuning problem. The
window was 48% Bull / 44% Bear, so it is not adverse-window bad luck either.

---

## 5. What was built

- **`edgetest`** — the only command whose numbers survive scrutiny. Reports RAW / static-gate /
  rolling-gate / coin-screen / risk-parity against a **random-gate null control**, on a daily
  marked-to-market equity curve, with a **fill-model sensitivity table** (a single cell is not a
  result; the table is).
- **`RandomWalkNullTests`** — the engine gate. On a driftless random walk every simulator must lose
  its costs; profit means the P&L came from the simulator. Includes a self-check that reintroduces
  the defect and asserts the gate still fires, framed as a one-switch **leakage gain**. **Add every
  new simulator to it.**
- **`RollingStrategyGate`** — refits every 30d on the trailing 180d of already-**closed** trades;
  also the decay detector.
- **`GaTrialCounter`** — trials accumulate and the ledger merges, so a retrain cannot launder away
  the multiple-testing burden it just added.
- **Acceptance gate** — leave-one-out per strategy, three-way verdict (REJECT only when strictly
  dominated), with slot-crowding decomposition.
- **`FitnessLandscapeTests`** — every GA-consumed statistic must be continuous, monotone and
  gradient-bearing across its live domain. Verified by reintroducing a cliff.

---

## 6. How do you find a genotype in a GA that is not noise?

This is the central methodological question the session exposed, and it deserves a direct answer.

### 6.1 The uncomfortable arithmetic, first

Bailey, Borwein, López de Prado & Zhu's **Minimum Backtest Length**: `MinBTL < 2·ln(N)/E[max SR]²`.
Inverted for our sample — ~3.2 years, `E[max SR] = 1` — the budget is **about 5 independent
configurations**. The ledger records **116,461** evaluations.

GA trials are highly correlated, so the *effective* N is far below the raw count. It is not 5.

And the cross-section does not rescue it. The design-effect asymptote is `n_eff → 1/ρ̄`: with mean
pairwise correlation ρ̄ ≈ 0.3 between concurrent positions, **no number of coins or trades ever buys
more than ~3.3 independent observations at a point in time.** Adding symbols does not increase the
sample. Only adding *time* does. Our own `SymbolCrowdingCap` uses this exact algebra.

**So the honest headline is: at this sample size a GA cannot reliably distinguish signal from noise,
and no post-hoc statistic repairs that.** Everything below reduces the damage; none of it eliminates
the constraint.

### 6.2 What genuinely helps, in order of value

**1. Shrink the search space. This dominates everything else.**
DSR penalises `log(N)`, so improving the search is nearly worthless while reducing the *number of
free parameters* is decisive. Falck, Rej & Thesmar found **signal complexity is an ex-ante predictor
of out-of-sample decay**, adding 15% of explanatory power on top of publication year. A 14-gene grid
genotype has a far better prior than a 199-parameter routing stack. **Fix structure by economic
argument; fit only what genuinely must be fitted.**

**2. Select the centroid of a basin, not the argmax.**
A genotype whose neighbours in parameter space all perform similarly is a plateau; a lone peak is
noise. Concretely: perturb every gene ±10% and ±20%, re-score, and require graceful degradation.
Rank candidates by *perturbed* fitness rather than peak fitness. This is the single most
GA-specific anti-noise test available and it is cheap — one extra evaluation sweep per finalist.
The intuition is that overfitting produces sharp optima because it is fitting individual trades.

**3. Outlier sensitivity — a validated ex-ante decay predictor.**
Recompute in-sample fitness with the top 1% and top 5% of winning trades deleted. A large drop
predicts out-of-sample decay (Falck/Rej/Thesmar). ~10 lines, and we have never run it. A genotype
whose edge lives in a handful of trades has no edge.

**4. Cross-sectional consistency.**
A real edge should appear independently across many coins. Report the fraction of coins with
positive expectancy, and re-score with the best *k* coins removed. A strategy carried by three
symbols is a story about those symbols.

**5. The null-control gate, before anything else.**
Now in place. It establishes that the measurement is trustworthy, which every statistic downstream
assumes and none verifies.

**6. Permutation test of the whole process (Masters).**
Permute log returns → reconstruct OHLC → **re-run the entire GA** → 1,000+ replications → p-value.
This is the only procedure that tests the *research process* rather than the strategy, and it
directly measures "would this GA have produced an equally good-looking genotype on noise?"
Expensive, and the honest answer to this section's question. Known blind spot: it cannot catch a
fill bug, because the bug is present in every permuted run — so it goes *after* the null controls,
never instead.

**7. A held-out block used exactly once, ever.**
The moment it is looked at twice it is training data. This is a discipline problem, not a technical
one, and it is the one most often lost.

**8. Report DSR with the real trial count** — the ledger now exists, so this is free.

**9. Keep a positive control in the battery.**
A signal with a known edge that must keep passing. Without it you cannot distinguish "the protocol
is rigorous" from "the protocol rejects everything".

### 6.3 The part that no technique fixes

**The fitness function is itself a multiple-testing surface.** We changed it twice in one session —
ramping the `PerTradeSharpe` cliff, adding the `maePct` path-risk term — and each change is a trial
that does not appear in any trial ledger. Likewise the choice of sizing scheme, window length, gate
threshold and strategy roster. Walk-forward fixes parameter circularity; it does nothing for
*design* circularity.

The only real defences are: pick from literature-defensible defaults and **freeze them**, prefer
decisions that depend on second moments (volatility, correlation — relatively stable and weakly
selected-on) over first moments (realised profitability — the dimension the search has already
exhausted), and treat every "we tried a variant" as a trial whether or not anything logged it.

### 6.4 The blunt recommendation

Use the GA for **parameters where an economic prior fixes the structure**, on **few genes**, and
select by **perturbed fitness plus outlier sensitivity plus cross-sectional consistency**, not by
peak fitness. Treat the result as a hypothesis requiring a disjoint sample to confirm.

Do **not** use a GA to discover structure — which rule, which regime, which combination. Zakamulin
(2018) showed that moving-average timing becomes statistically indistinguishable from buy-and-hold
once look-ahead in *rule selection* is removed, and that the historical positive results came from
choosing the rule with hindsight. **A GA over strategy structure is precisely that mechanism,
industrialised.**

---

## 7. What the literature says about this system

Five parallel literature reviews (routing, HMM, covariance sizing, strategy families, backtest
methodology) converged on findings that match our own measurements.

**Routing.** Our result — that the gate's benefit was exposure reduction, that a random gate
reproduced most of it, and that gate-ON trades were not better than gate-OFF — **is the modal result
in this literature.** Every positive regime-switching result either reduces to moving to cash in a
bad state, or fails to replicate out-of-sample. Ang & Bekaert's canonical result adds value "only
when the investor can move to cash". Bulla et al.'s replicated version is a **41% volatility cut for
+18.5 to +201bp** of return — most of the Sharpe gain is the denominator. No study was found showing
a multi-strategy router beating exposure-matched constant allocation out-of-sample net of costs.

*One correction to an earlier claim in this session:* our `t = −1.42` was reported as evidence the
gate has no selection skill. It is not. Detecting a plausibly-sized selection effect needs ~4,400
trades; at our sample a real effect would have produced |t| ≈ 1. **The test was underpowered in both
directions.** The honest position is that the gate is unfalsifiable at this sample size, and an
unfalsifiable component with ~56 fitted parameters should be assumed noise until shown otherwise.

**Architecturally, the router cannot select.** Its feature set is one symbol's regime state, shared
across every strategy and every trade in a window. It has no per-trade features, so it can only turn
whole windows on or off. Our measurement did not discover a defect; it discovered the design.

**HMM.** Volatility-state separation is the most replicated finding in the regime literature
(~80% likely to hold). Directional information: ~25%. *This* configuration: ~10–15%. The arithmetic
is decisive and needs no contested citation — 6 states × 9 collinear features = **143 parameters,
plus 56 GA parameters ≈ 199 fitted quantities**, against a 6×6 transition matrix estimated from
**~100–150 observed regime episodes**. K=3 on 2–3 features is defensible; K=6 on 9 is not. Our
annotator uses the causal *filtered* path, so the classic smoothed-probability lookahead appears
absent — but full-sample *parameter* fitting and feature-normalisation windows still need auditing.

**Sizing.** Errors in means are ~11× as costly as errors in variances (Chopra & Ziemba). 1/N is the
benchmark and is hard to beat (DeMiguel, Garlappi & Uppal). And decisively: distinguishing two
sizing schemes differing by 0.15 Sharpe at SR = 1 needs **~67 years** of data. **You cannot select a
sizing scheme empirically on this sample** — only reject ones that fail badly. Size on second
moments, never first.

**Strategy families.** Grid is **short gamma with the premium leg missing**: Fung & Hsieh established
trend-following ≈ long straddle, and a grid is the mechanical inverse, but an option seller is *paid*
for concavity and a grid trader is not. The only formal grid literature shows almost-sure ruin
unconstrained — our stops are why it does not blow up and also why CAGR is 0.3%; the constraint and
the profitability are the same dial. Note the asymmetry: **PF below 1 is informative, PF above 1 on
a short-gamma strategy is not**, because the tail may simply not have fired. Our PF 0.91 readings
are trustworthy; a PF 1.8 would not have been.

RSI divergence has no credible multiple-testing-corrected out-of-sample evidence in crypto.
**Break-of-structure has zero non-commercial sources** — every result sells an indicator, a course
or a funded account. And BoS is a *continuation* trigger bolted onto a *reversion* entry: opposite
payoff geometries, no theory, PF ≈ 0.9 measured.

**Realistic expectations.** A defensible out-of-sample Sharpe for a retail-cost systematic crypto
strategy on 1h bars is **0.3–0.8** — exactly where we are, and exactly the range that cannot be
distinguished from zero on three years of data. Our results are not broken; they are the textbook
outcome.

---

## 8. What comes next

### The one promising direction

**Perp–spot basis convergence** (He, Manela, Ross & von Wachter, arXiv:2212.06888). Retail fee tier,
net of fees *and* effective spreads, BTC: **Sharpe 3.27**. The naive statistical version — hourly
30-day rolling cointegration of perp on spot, enter at |z| > 1.96, exit at z → 0 — reports Sharpe
4.16 at zero cost and requires no structural model. It needs spot candles alongside our perp candles
and a rolling regression; `CandleFetcher` and `FundingRateSession` already exist. Caveat: deviations
decay ~22pp/year, so the edge is closing.

Second: **funding carry with a crash overlay**. We already ingest per-symbol funding, and
`IsCrowdedLong`/`IsCrowdedShort` are the right primitive currently attached to the wrong strategies.
The engineering that matters is not the signal but the ADL defence — the 10 Oct 2025 cascade
force-closed market makers' short perp legs and left them holding unhedged spot into a crash.

### Ordered work

1. **Validate on a disjoint sample** — a different calendar period, or coins that do not overlap the
   current 63. Every number here was measured on one sample that the roster was also *selected* on.
2. **Move production onto the rolling gate.** `combinedbacktest`, `oosbacktest` and `fulltest` still
   load the in-sample `strategy_family_gate.json`. **Every number outside `edgetest` remains
   contaminated.** Expect published figures to fall when this lands; that is the correction arriving.
3. **Exposure-matched control as the default report** (Fleming, Kirby & Ostdiek) — publish the gated
   book beside the ungated book *rescaled to the gated book's realised volatility*, plus the
   Grinblatt–Titman split of `E[w]·E[r]` (exposure) and `Σ Cov(w,r)` (timing skill). One line of
   arithmetic; it answers in one run what an underpowered t-test answered weakly.
4. **Outlier-sensitivity and perturbed-fitness diagnostics** (§6.2). Cheap, validated, never run.
5. **Evaluate the HMM standalone** at K=3 on 1–2 volatility features, against a one-parameter
   `atrRatio > θ` baseline. If the threshold matches it, delete the HMM.
6. **Fix `AccumulationGridSimulator`** before any revival, and add it to the null gate.
7. **Test the router against equal-weight-vol-targeted, exposure-matched.** If it cannot win,
   delete it — and the HMM, favorability matrix, coevolution loop and router GA go with it.

---

## 9. The lessons

**Statistical rigour applied to a biased simulator produces confident, well-deflated nonsense.**
Eight hours of escalating rigour — deflated Sharpe, PBO, White's Reality Check, walk-forward gating,
real mark-to-market paths, trial counting — could not see a defect worth 3.5 Sharpe points, because
every book shared the same fill model. A crude null control found it in ten minutes by scoring
*impossibly well*. **Impossible results are the most informative ones.**

**Test the instrument before the result.** Null controls establish the antecedent that every
statistic assumes.

**Guards written for reporting must not be copied into selection.** Three cliffs shipped this way
(tail-ratio step at n=100, `Simulator.SharpeRatio`'s PF floor, `PerTradeSharpe` inheriting it). Any
function a GA selects on must be continuous and monotone over its live domain, and that property
deserves a test of its own — the old tests pinned *values*, and a value assertion cannot notice that
a function has no gradient.

**A measurement harness that does not match the system it measures will invent defects as readily as
it hides them.** `edgetest` produced a confident, wrong rejection of FadeShort because it replayed
stricter than production.

**Selection rules must be fitted on a trailing window of already-closed trades** — gate, coin screen,
sizing weight alike — never on the window they filter.

**The information content of the data is the constraint, not the architecture.** Eight strategies, a
router, a guard, a rotator, an HMM, a coevolution loop and an allocator, fitted to ~3 years of a
market whose effective cross-sectional dimension is far below its symbol count. More machinery on
the same features will produce better in-sample numbers and no better out-of-sample ones.
