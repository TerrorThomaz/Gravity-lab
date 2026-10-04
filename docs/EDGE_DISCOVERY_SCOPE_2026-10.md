# Edge discovery: scope (2026-10-03, DRAFT, not pre-registered yet)

**Question.** Within a market state we can identify honestly, do some rule-based setups win more
often than their costs require? Answer it in a way that can come out "no".

**The approach in one line:** a fixed rule engine opens shadow positions on every candidate setup.
Each position is labelled by fixed exits and logged with the indicator values and market state at
entry. Then we measure which rule × state cells beat their break-even win rate. The analysis
compares winners with losers *within the full candidate population*, never winners alone. It is
pooled hierarchically and judged against nulls and a sealed holdout.

**Honest prior: most likely outcome is NO.**
- 22 price signals carried real information (IC t ≈ 37), but too little to beat costs.
- A GBM meta-labeler and the shared P(win) model on the same signals did not beat random gates.

What is new here, and the only reason to expect a different answer:
1. **Structure.** Features are rules and relations, not raw values.
2. **A market state built to be stable.** It is causal, has a consistent identity across refits,
   and is multi-asset.
3. **Positioning data.** OI and the long/short ratio come from the backfill; book depth and
   liquidations come forward from the recorder.
4. **The label changes from "did this trade win" to "how did price move after a grid fill".**
   That separates the entry, setup and exit questions, which every earlier test bundled together.

**Update, 2026-10-03: (3) is null.** `positioning_test.py` on 79k OOS and 112k BacktestCoins
trades gives 0 of 78 univariate tests at BH q < 0.05, and a walk-forward P(win) AUC of
0.529 / 0.524 against label-permuted nulls of 0.532 / 0.527 (p = 0.95 on both). Hourly OI and the
L/S ratio say nothing about strategy outcomes. They stay in as context; nothing is expected from
them.

---

## 0. Why the naive version fails (design constraints)

| trap | what it does | constraint |
|---|---|---|
| **Outcome conditioning** ("look at what the winners had") | P(x \| win) alone finds whatever is common, e.g. "most winners had ADX < 25" because most *bars* do | Always use the ratio P(x \| win) / P(x \| loss) over **every** candidate, and compare against the base rate |
| **Censored feedback** ("let the model decide, then record") | It only learns from setups it already liked, so its blind spots never get labelled | **Every candidate** from the rule engine is shadow-traded and labelled. The model scores; it never chooses what gets recorded |
| **Hindsight exits** | "Winner" defined by the best exit in hindsight | Fixed exits (ATR barriers plus a time limit), set before the first run |
| **Overlap / fake n** | 80 coins fire in the same flush hour: 80 labels, about 1 independent event | Day-clustered statistics, uniqueness weights (López de Prado ch. 4), purge plus an embargo ≥ label horizon |
| **Collinearity double count** | RSI, Stoch and %B all say "oversold", so it is counted three times | Rule cells as features, and partial pooling instead of naive Bayes |
| **Win rate ≠ money** | 70% wins at small TP and big SL loses money | Symmetric barriers, so the break-even p is explicit: p* = (SL + cost)/(TP + SL) |
| **State fitted on the future** | Baum-Welch on all history: states "know" how the regime ended | Walk-forward refits with a forward filter only (see §2) |
| **Researcher degrees of freedom** | Look, tweak, rerun | Everything in §1–§5 is frozen in a pre-registration commit before Phase 3 touches labels |

---

## 1. Candidate engine: every grid fill, no exit (revised 2026-10-03)

**Thesis being tested** (the user's): every strategy is a gated grid. RipShort, FadeShort, DipLong
and the rest all enter at a price a grid rung could have held. What separates them from Grid is
*setup detection* (when to have the rung out) and *closing* (when to leave). So the entry is
generic; the **movement after it** is where profit is or isn't. The unit of study is therefore the
post-fill path, not a trade with an exit.

**Rungs, placed everywhere, no setup gate.** At the close of every 1h bar t, for every coin,
rest limit orders at `close_t ∓ k·ATR14_t` for k ∈ {1, 2, 3}. Long rungs go below the close, short
rungs above.
- Each order lives for one bar. It fills if bar t+1 trades through it: `low ≤ level − 5bp` (the
  `THROUGH` lesson from `dip_dca_research`).
- The fill price is `min(open, level)` for a long and `max(open, level)` for a short. A gap through
  the level fills at the open.
- **Never on bar t itself.** This is the same-bar fix (`fillOnArmBar: false`).
- A fill is therefore an event "price moved k ATR inside the next hour". It is rare by construction,
  so there is no need to arm "every bar" artificially.
- One event per coin × side × bar, keeping the deepest k that filled, with k recorded.

**Label: the whole forward path, no exit.** For a fill at price P₀ and time τ₀, record the signed
log return at every hour h = 1…72:

    r(h) = side · ln(P(τ₀+h) / P₀),   also MFE(h) = max r over [0,h],  MAE(h) = min r over [0,h]

All three are expressed in units of σ·√h, where σ is the coin's trailing 1h vol at τ₀. Under a
random walk, r(h)/σ√h has mean 0 at every h, so this **time normalisation** puts every horizon,
coin and volatility on one null scale.

**"The return it could have made", made honest.** MFE alone is always positive, even on a random
walk, because a maximum of noise is positive. So the quantity under test is never MFE in isolation.
It is:
1. **The drift curve** `m(h) = E[r(h)]` per cell, and its **derivative** `dm/dh`: the rate at which
   the move pays. Grid's known edge should appear as `dm/dh > 0` for a few hours after a long fill
   in a market flush, then flattening. The point where `dm/dh` falls to the cost per hour is the
   natural holding horizon for that cell. That is the exit question, answered by the data rather
   than set in advance.
2. **Excess over the null**, with MFE and MAE measured against the same quantity at:
   - random times on the same coin in the same state (R0);
   - the same rungs on block-shuffled bars.

   The edge is the gap, never the level.

**The thesis check (cheap, do it first).** Map every trade of the 7 strategies (honest-fill dumps,
`all_trades_*.csv`) to a grid fill on the same coin within ±1 bar and the same side. Then report:
- **coverage:** the share of each strategy's entries that a rung could have filled, and at which k;
- **attribution:** each strategy's realised return minus `r(h_exit)` of the matched fill. This is
  what its exit added or cost.
- **setup value:** `m(h)` of the strategy-matched fills minus `m(h)` of all fills in the same cell.
  This is what its setup detection added.

If coverage is high and the exit attribution explains the losses, the thesis holds and the strategies
reduce to (gate, exit) pairs over one candidate set. If coverage is low, the strategies are not
grids and the thesis is wrong. Either answer is informative.

**Universes and split.**

| block | role | data |
|---|---|---|
| A: train | everything up to 2025-06-30 | BacktestCoins + OosCoins |
| B: sealed holdout | 2025-07-01 → 2026-10-03 | not read until §5 is frozen; evaluated **once** |
| C: forward | from the pre-registration commit on | the only truly clean data, see note |

**Honesty note on B.** The rules, the model and the HMM never see B. But we humans have seen
Grid's and others' results over that window in earlier research, so B is "clean for this model",
not "clean for us". **C is the real test.** B tells us whether C is worth waiting for.

**Overlap.** Flush hours fill rungs on dozens of coins at once, and those are about one event.
Every statistic is reported day-clustered as well as raw. Paths overlap in time within a coin, so
model weights use uniqueness (López de Prado ch. 4).

## 2. Market state: a more rigorous HMM

The existing model, and what is wrong with it for this use:
- `HmmAnnotator` is fitted once on BTC features over all history. It is only filter-causal.
- In earlier work its states disagreed between universes (s1: −0.053%/day vs +0.023%).
- `MetaLabel.HmmSummaries` refits walk-forward but averages states into 4 numbers, so identity is
  lost.

**Inputs: market-wide, daily, causal.** Each is a z-score against its trailing 365d.
1. PC1 index return over 1d and 7d (point-in-time covariance, as in `factor_hmm.py`).
2. Realised volatility of the PC1 index (7d).
3. Breadth: share of coins above EMA50.
4. Dispersion: cross-sectional std of 1d returns.
5. Correlation: PC1 variance share over trailing 30d (crowding; rises in crashes).
6. Mean funding across the universe.
7. Aggregate OI change over 7d (from the backfill, available from T + 5 min only).

BTC is not an input on its own; it is inside PC1 at its covariance weight.

**Fitting.**
- Gaussian HMM with full covariance (7 dims × daily ≈ 1,800 obs is enough), K ∈ {3, 4, 5}.
- K is chosen **once**, on block A, by held-out log-likelihood over the last 20% of A. That choice
  is frozen before labels are joined.
- Walk-forward refit every quarter on an expanding window. The forward filter only produces
  probabilities.

**Stable identity across refits.**
- Match each refit's states to the previous refit's by Hungarian assignment on the distance between
  emission means.
- Report the matching cost. A refit whose best match moves a state by more than 1σ on any input is
  flagged, and the state counts as unstable.

**Validation gate. The HMM passes only if all four hold. Failing it ends §2, not the project: the
fallback is plain bucketed inputs (vol tercile × breadth tercile).**
1. Expected state duration ≥ 5 days. No flickering.
2. States are stable across refits (the matching above), and the same model run on BacktestCoins
   versus OosCoins gives states with the same ordering on vol and breadth.
3. States separate something the HMM did not see: next-7d realised volatility differs between
   states with p < 0.01 in both halves of A.
4. Placebo: an HMM fitted on block-shuffled inputs (30d blocks) fails gates 1–3.

The model then uses **state probabilities, not the argmax**. Each candidate carries the vector π
and a "decided" flag (max π ≥ 0.7). Undecided bars form their own bucket.

---

## 3. Logged per fill

```
fill_id, symbol, side, k, fill_bar, fill_px, sigma_1h,
z_path[1..72] (r(h)/σ√h), mfe_z[1..72], mae_z[1..72], funding paid over h (per h),
π_1..π_K, decided,
context at the arming bar: ATR ratio, distance to EMA50/200 in ATR, ADX, BB width, RSI,
         residual z (4h, 24h), family-vs-own trend, funding bps, OI Δ24h z, L/S z (lag-safe),
         market-state inputs (§2), how many coins filled a rung in the same hour (flush breadth)
strategy_match: which of the 7 strategies' setups were true at the arming bar
```

The strategies' setup rules become **gates over fills** (boolean context), not candidate generators.
This makes "setup detection" directly measurable as a difference in `m(h)`.

## 4. Analysis, in order, on block A only

**4a. Drift map (the movement, per cell).**
- For each cell = side × k × state (× setup gate): plot `m(h)`, `dm/dh`, and the MFE/MAE bands in
  σ√h units, with R0 and shuffled-bar curves on the same plot.
- A cell is interesting only if its `m(h)` beats both nulls by more than costs at some h, in both
  halves of A.
- Also report the likelihood ratios below, taking "win" as `r(h*) > cost` at the cell's horizon h*.

**4a′. Descriptive likelihood ratios (your attribution idea, made safe).**
- For each rule × state cell: n (raw and day-clustered), win rate, mean net return, and
  LR = P(cell | win) / P(cell | loss).
- Reported against R0 random entries in the same cell. A cell is only interesting if it beats
  both p* and R0.
- Benjamini-Hochberg across cells.
- This step decides nothing and fits nothing. It is the map.

**4b. Hierarchical Bayesian model (the decision model).**
- Logistic regression on win, with partial pooling:
  `logit p = α + α_rule + α_side·rule + α_state·rule + β·context`
- The α's are drawn from N(0, τ²) per group with half-normal τ priors.
- β has a horseshoe prior: most context features presumably do nothing, and each one has to earn
  its weight.
- Fit with numpy (Laplace approximation, or Gibbs with Pólya-Gamma augmentation). No new
  dependency.
- Refit walk-forward each quarter: train only on trades that **exited** ≥ 1 day before the quarter,
  purge plus embargo, uniqueness-weighted likelihood.
- Output per candidate: posterior mean p̂ and its 10th percentile p̂₁₀.

**4c. Usage rule (fixed now).**
- Take a candidate only if p̂₁₀ > p*, i.e. confident that it beats break-even.
- Size proportional to (p̂ − p*), capped.
- Run it through `PortfolioReplay` caps plus the crowding cap at 0.5.

**4d. Nulls. All are required, and all must be beaten.**
1. Label permutation within rule (base rates kept): 20 full refits.
2. Random same-fraction gate within rule: 1,000 draws.
3. R0 random entries put through the same model.
4. State placebo: the model refitted with π from the shuffled-input HMM.
5. Instrument check on a synthetic random walk: the whole pipeline must find nothing. On a planted
   edge in one rule × state cell it must find exactly that cell. Same rule as
   `RandomWalkNullTests`: the instrument is checked before the result.

---

## 5. Pass / fail, written before the run

| stage | pass | if fail |
|---|---|---|
| Phase 0 instrument | random walk: `m(h)` ≈ 0 and z-path mean 0 at all h for every k; planted mean reversion → positive `dm/dh` recovered at the planted horizon | fix the engine, don't proceed |
| §2 HMM gate | 4 criteria above | fall back to bucketed state, continue |
| 4a map | ≥ 1 cell beats p* **and** R0 at BH q < 0.1, both halves of A | STOP: no rule × state edge exists in this data |
| 4b/4c walk-forward on A | book Sharpe > 1, day-clustered t > 3, beats all 5 nulls (p < 0.01), survives removing best 3 months | STOP |
| **B, sealed, one shot** | net positive, day-clustered t > 2, same sign in both halves of B, DSR > 0.95 with all trials of this project counted | STOP. Do not tune and retry on B: it is spent |
| C, forward 6–9 months | shadow book on live data: t > 2, Sharpe > 1 | retire |

**Trial budget for this project:** the k ∈ {1,2,3} rung set, 2 sides, 7 setup gates, plus K
selection (3), plus 1 model, plus 1 usage rule, plus the holding-horizon choice per cell (1 per cell
that passes 4a). Horizons are picked on A from `dm/dh` and frozen before B.
Recorded in a project ledger and added to `ga_trials.json` for the DSR. Any change after 4a sees
labels is a new trial and is logged as one.

---

## 6. Phases and cost

| phase | work | depends on |
|---|---|---|
| P0 | grid-fill engine + path labeller + synthetic null/planted test, then the thesis check (strategy trades → fills) | — |
| P1 | market-state inputs + walk-forward HMM + identity matching + gate | positioning backfill (done soon) |
| P2 | label blocks A and B (B written to a file, not read) | P0 |
| **Pre-registration commit**: this document with every "TBD" filled in, plus hashes of code and B | | P0–P2 |
| P3 | 4a map | P2 |
| P4 | 4b model + 4c + 4d nulls | P3 passes |
| P5 | B, one shot | P4 passes |
| P6 | forward shadow logger in papertrade (rules + model frozen) | P5 passes |

**Language.** Python (`research/`), reusing `market_neutral_research` (universe, factor residuals),
`anomaly_fade_bounce.residuals` and `positioning_test` (lag-safe joins).
- The rules are re-implemented in Python from textbook definitions.
- One cross-check: R1's candidates must match the C# textbook Grid's arming bars on a sample of
  coins.

---

## 7. What this cannot tell us

- **Whether the strategies' own exits** (trailing, asymmetric TP) add edge. That is a later phase,
  on cells that pass.
- **Anything about coins that died.** 37 delisted OosCoins are missing; a short-side survivor bias.
- **Order-book and liquidation effects.** Only block C will have them.
- **The value of "it decides when a winner occurs" beyond the 7 rules.** A model free to invent
  setups is the unrestricted search we chose against: its hypothesis space is unbounded and cannot
  be charged for.

---

## Phase 0 results (2026-10-03, block A only, `research/grid_paths.py`)

**Instrument.** `--selftest` passes: on a random walk there is no drift and fills are never
optimistic; planted reversion is found and disappears on shuffled bars; a fill never depends on
later bars.

It also caught a flaw in the statistic itself. "Average per day, then t across days" weights each
day by 1/fills, and the fill count depends on later prices that same day. That produced t ≈ +2 on
**both** sides of a pure random walk. The fix is a fill-weighted mean with a week-clustered,
lag-1 Newey-West SE. Null t-stats then come out at sd ≈ 1.0–1.15, so read |t| < 3 as noise.
`trade_log_edge.py`'s day-clustered t should be checked for the same weighting.

**Drift map: generic fills carry ~nothing beyond the coin's drift.**
- k=1/2 (OOS 337k, BT 502k fills): excess over same-month drift |e| ≤ 0.1%, t < 2.5 at every h.
- k=3 (a 3-ATR move inside an hour): short excess reaches +1.4% at 72h OOS. But the shuffled-bar
  null shows +0.4–0.5% too: the month baseline still contains the fill bar's own jump. Measured
  against the null it is about t 2.9 (OOS short), and ~0–1.4 everywhere else. This is the
  `j4big` reversion again, not a new finding.
- m(1) ≈ +0.2–0.6%: the fill bar closes above a long fill. The shuffled bars show the same, so it
  is a property of bar shape (buying wicks), not of serial structure. Whether real limit orders
  get those fills is a fill-realism question for 1m data.

**Thesis check.**
- (a) **Entries:** the nearest rung (±6h, usually 1–2h *earlier*) is 0.9–2.2% better than the
  strategy's entry on the long/fade strategies, in both universes. Coverage is only slightly above
  time-shifted placebos (e.g. 82% vs 74%), so "a rung was near" is close to automatic.
  **Exits:**
  - roughly neutral for DipLong (+0.13–0.15), SwingLong (+0.10), FadeLong (0) and Grid (0);
  - FadeShort's exit costs ~0.45%/trade against a time-hold in both universes.

  So the strategies behave like late rungs that pay ~1–2% for confirmation. But a rung held to the
  same exit only wins (+0.75 to +1.65% gross) on the dips that later confirmed, which is hindsight.
  Across all dips the drift map says ~0. **The information that a dip will bounce arrives with the
  bounce.**
- (b) **Setup value** (causal: first rung after the setup, against a time-shifted null), OOS / BT:
  - **FadeShort +0.18 / +0.20% at 24h, t 2.7 / 3.2** (12h: 2.0 / 3.8). The only positive gate, and
    it replicates. It is small next to the 0.125% cost.
  - **SwingLong −0.41 / −0.41% at 24h, t −5.5 / −5.7.** A replicated *anti*-gate: after a bullish
    divergence + BoS, dips keep falling.
  - Grid's own setup: −0.08 to −0.14, t −0.8 to −1.9 (no value). DipLong, FadeLong, GridShort:
    |t| ≤ 2.2.

**Next, as scoped:** gate rungs on each setup's **context** only (trend, RSI band, divergence
present), which is known before the dip, not on its trigger (BoS), which is the confirmation. That
is the causal form of "every strategy is a gated grid". Also verify the m(1) wick gain on 1m bars.

## Context gates + 1m wick check (2026-10-03, block A, `research/context_gate.py`, `research/wick_1m.py`)

**Context gates** (7 textbook gates on the arming bar, fixed before the run). Value is gated
fills minus all fills of the same side/k/day, at h12, OOS / BT:

| gate | value h12 [t] | beats 20 shifts | net@12 | post-fill vs coin drift | post-fill vs market (hedged) |
|---|---|---|---|---|---|
| grid_L (ADX<20, long) | −0.12 [−5.2] / −0.17 [−6.7] | no | +0.07 / +0.03 | ~0 | ~0 |
| grid_S | −0.10 / −0.05 | no | +0.04 / +0.01 | ~0 | ~0 |
| dip | +0.09 [2.1] / +0.04 [1.4] | no | | ~0 | ~0 |
| rip | +0.11 [3.2] / +0.13 [4.0] | no (p 0.10) | | −0.09 / −0.11 | +0.05 [2.9] / +0.04 [2.7] |
| fade_s | +0.64 [8.8] / +0.58 [14.6] | yes | +0.54 / +0.31 | +0.23 [1.8] / +0.16 [1.8] | +0.11 [1.6] / +0.06 [1.7] |
| swing | +0.86 [10.7] / +1.02 [8.5] | yes | +0.64 / +0.85 | +0.17 [1.0] / +0.32 [1.9] | +0.06 [1.2] / +0.03 [0.7] |
| fade_l | +0.71 [7.3] / +0.65 [8.5] | yes | +0.05 / +0.18 | −0.04 / +0.01 | +0.05 [1.6] / +0.02 [0.5] |

- **Honest bookkeeping.** fade_s, swing and fade_l pass the criterion as pre-registered.
- **The diagnostics run AFTER that undercut it.** The value is relative to other fills of the same
  day. Net of the equal-weight market over the same window, every gate is ≤ 0.11% (t < 3). Against
  the coin's own drift, t < 2.
- **What it actually is.** The divergence gates fire at intraday turns of the whole market, so
  "better than the day's other fills" is market timing measured against fills that have not
  happened yet. It is not a coin-level signal, and not tradable as measured. Volatility scale was
  ruled out (gated/all σ ≈ 1).
- **Grid's own ADX<20 screen selects WORSE fills** (t −5 to −7, both universes).
- **Lesson for the pre-registration:** the baseline must be the market-hedged or drift-adjusted
  move, never same-day fills.

**1m wick check** (300 block-A fills per cell, 1m candles fetched per fill hour, no drift read):
- Every hourly fill reproduces at 1m.
- 69–89% are "through" fills: a minute closed beyond the level, so a resting order was certainly
  reached.
- The rest are wick-only. Those carry +0.8 to +2.0% of fill-hour gain each, which is exactly where
  a real queue is least likely to fill.

| cell | r1 all (hourly model) | r1 through only (conservative) |
|---|---|---|
| long k1 / k2 / k3 | +0.21 / +0.57–0.74 / +0.30–0.40 | **+0.08–0.13 / +0.30–0.44 / −0.17 to −0.21** |
| short k1 / k2 / k3 | +0.23–0.27 / +0.46–0.60 / +0.01–0.19 | **+0.15–0.18 / +0.23–0.44 / −0.16 to −0.43** |
| gates fade_s / swing / fade_l | +0.45–0.62 | +0.19–0.43 |

- The model's m(1) is roughly half fill-model optimism. A real part survives at k1–k2 (wick
  reversion inside the hour).
- k3 fills that stay through lose.
- Liquidity beyond the level: median $30k–250k (OOS) and $0.2–1.8M (BT) per fill hour.
- **Rule from here:** fill realism is bracketed between the touch and through models, and any
  result must survive the through model.

**Annealing: not triggered.**
- No gate shows a coin-level or market-hedged effect that survives, so there is no indicator value
  to optimise.
- The one effect that survives the conservative fill model is mechanical: the within-hour reversion
  after a 1–2 ATR rung fill, +0.1–0.4% gross against a 0.125% round trip. Searching it would mean
  searching the rung depth k and the exit timing.
- **If pursued:** first a queue-aware fill model (a fill only if the volume beyond the level exceeds
  a size threshold), then a walk-forward search over a predeclared (k, exit-minute) grid inside
  block A, with every evaluation counted as a trial. B is evaluated once.

## Scope boundaries (agreed 2026-10-03)

- **Completeness.** Rungs k=1-3 plus k=0 (a market entry at every bar) contain every single-coin
  directional entry at hourly resolution. Any such strategy is then a gate × an exit over that set.
  The gate space is unbounded, which is why gates are pre-registered and charged as trials.
- **DCA is not a candidate type.** It is a long-only layer, considered only on top of a strategy
  that has already passed on its own (the dip-DCA studies lost because averaging down amplified
  edgeless entries).
- **Relative-value trades (pairs, carry, xcarry) are out of scope.** A spread position is not
  described by one coin's path after a fill. They stay in `scripts/market_neutral_research.py`.

## CORRECTION (2026-10-03): per-rung fills; the "wick gain" was a labelling artefact

**What was wrong.** `grid_paths.fills` kept one event per bar, labelled by the DEEPEST rung that
filled. So "k=2" meant "reached 2 ATR but not 3 ATR this hour", a condition on the bar's full
extreme. On real bars that selects hours that reversed. It produced a fake fill-bar gain (m(1)
+0.2–0.6%) and a fake k=2 edge (+0.3–0.6%/trade under every queue model, both universes).

On a fat-tailed random walk it shows no gain (k2 m(1) −0.15/−0.21), which is why the selftest
missed it. The bias needs real bar shapes. Fixed: one row per rung reached, pinned by a nesting
invariant in `--selftest`.

**Void:**
- the m(1) wick-gain numbers above;
- the 1m wick check table (sampled from deepest-rung labels);
- the k=2 rows of the first queue-fill run.

**Rerun, per rung, block A:**
- **Drift map: unchanged.** Excess over drift is ~0 at k1/k2. k3 is positive but at or below the
  shuffled null.
- **m(1) per rung:** k1 −0.03 to −0.07%. There is no wick gain for a real 1-ATR rung.
- **Setup value: unchanged.**
  - FadeShort: +0.14 / +0.18 at 12h, t 2.4 / 3.6.
  - SwingLong: an anti-gate, −0.29 / −0.31 at 12h, t −4.6 / −5.5.
  - Grid: ~0.
- **Context gates:** relative values are similar, but the net level at 12h is now mostly
  NEGATIVE:

  | gate | net@12 (OOS / BT) |
  |---|---|
  | grid_L | −0.17 / −0.21 |
  | grid_S | −0.21 / −0.26 |
  | fade_s | +0.09 / −0.13 |
  | fade_l | −0.57 / −0.36 |
  | swing | +0.29 / +0.50 (n 3.4k / 5.4k; vs drift +0.22 [1.2] / +0.32 [2.1]; market-hedged ≈ 0) |

  **No gate passes.**
- **Queue-aware fills + walk-forward search (`research/queue_fill.py`, 48k 1m windows):**
  - Fill rates at Q = $25k are 50–74% (OOS) and 78–90% (BT).
  - Per-rung net per trade is negative at every k and exit time, even under the optimistic touch
    model (k1 −0.1 to −0.24%). Under the $25k queue it is −0.15 to −0.5%, worse than random market
    entries: filled rungs are adversely selected.
  - Walk-forward chained out-of-year: long −0.36 / +0.05, short +0.07 / −0.44 (OOS / BT), t ≤ 1.1
    or negative.
  - **No (k, exit) cell survives. Annealing has nothing to optimise.**

**Standing conclusion of the grid-fill framework.**
- Over every single-coin entry at hourly resolution (rungs plus k=0), price after the entry moves
  with the coin's drift.
- The strategies' setups add at most ~+0.15–0.2% (FadeShort), below cost, or subtract (SwingLong).
- Their triggers buy the confirmation at a 1–2% worse price.
- What remains real across this whole programme is market-level timing (Grid's flush rebound,
  FadeShort's market call) and residual reversion (j4big). The next information has to come from
  outside candles.

## Open search over every 15m entry (2026-10-04, `research/discover15.py`, block A, touch fills)

**Setup.**
- 2.0M candidates: rungs k=1–3 per rung, plus k=0 market entries, both sides.
- 29 causal features.
- A numpy GBM per side, trained on BacktestCoins, quarterly walk-forward with a purge.
- OosCoins are scored by the same models and never trained on.
- Rule: trade if predicted net > 0.

**Primary result, H = 16 (4h). FAILS.**

| univ | side | all-candidate net | selected net [t] | nulls ≥ real | hedged |
|---|---|---|---|---|---|
| BT | long | −0.152 | −0.003 [0.0] | 0/10 | −0.138 [−5.3] |
| BT | short | −0.188 | −0.081 [−1.5] | 0/10 | −0.150 [−9.5] |
| OOS | long | −0.176 | −0.111 [−1.2] | 2/10 | −0.176 [−6.0] |
| OOS | short | −0.151 | +0.003 [0.1] | 4/10 | −0.050 [−2.3] |

**Reading.**
- **There is real information:** out-of-sample rank IC is +0.044, positive in 91% of quarter-sides. Selection lifts the mean by ~0.1–0.15%/trade over random, and on BacktestCoins it beats all 10 permutation nulls.
- **The information doesn't reach the cost line,** and it weakens on unseen coins.
- **Hedged, it loses.** What it knows is market direction, and it leans on market state and volatility: BB width, market 24h return, rung depth, market vol, dispersion. The importances are tiny.
- **"Outside every strategy gate"** performs the same as inside. So the setups the strategies didn't find are no more profitable than the ones they did.

**Secondary horizons (reported only).**
- H = 4: OosCoins short shows +0.226 [4.3], both halves positive, hedged +0.227 [4.5]. But the trained universe has the same cell at −0.066 [−1.3], from only 2% selected. It fails the both-universes rule and is treated as noise among 12 cells.
- H = 48: nothing significant.

**A consistent sub-pattern, not a pass.** Model-selected deep rungs (k3) are positive in most cells:
- long H16: +0.27 BT / +0.05 OOS;
- long H48: +0.55 / +0.37.

This echoes the per-rung touch results (k3 long at long holds, t ≤ 1.7). If pursued, it is ONE new pre-registered trial: "k3 long rungs, model-gated, held 12h". It must be judged on block B once, because it was found by looking here.

## Positive control: market trend (2026-10-04, `research/trend_control.py`, block A)

**The user's challenge.** No edge found means the machinery is broken, because buying dips in bulls
and shorting bears should pay.

**Test.** A textbook rule:
- signal = the sign of the equal-weight market's 30-day return;
- daily entries on every coin;
- holds of 12h through 14d.

**The effect is real and grows with horizon.**
- TREND net per trade at 12h: −0.13 / −0.15 (cost-dominated).
- At 1d: ~0.
- At 3d: +0.45 / +0.41.
- At 7d: **+1.11 [1.8] / +1.37 [2.1]**, positive in both halves, and it beats always-long and
  always-short.
- k2 rungs WITH the trend vs AGAINST it:
  - 12h: +0.21 / +0.28 vs −0.34 / −0.26;
  - 7d: +1.12 / +1.71 vs −1.31 / −1.01.
- Short rips in bear are positive in all halves. Long dips in bull lose in the first half (2021
  tops) and win in the second.

**Why the machinery missed it. These are three blind spots, not a broken engine:**
1. Horizons stopped at 12h. At ≤12h the trend is about the size of costs.
2. discover15's longest feature lookback was 4 days (`mz384`). **It had no regime-scale trend
   feature at all.**
3. Phase 0 "excess over the coin's same-month drift" subtracted the trend by construction.

**Caveats.**
- The t-stats stay around 2 because the sample is a handful of bull/bear regimes, not thousands of
  trades.
- Funding is not charged: longs in bull pay, which could cost 0.2–1% per week.
- Survivorship flatters longs.

**Fix (next).** Rerun the open search on 1h candidates with:
- regime-scale features: market and coin 7d / 30d / 90d trend, distance to the 30d high;
- horizons 1d / 3d / 7d, with real funding charged.

## Regime-scale open search (2026-10-04, `research/discover1h.py`, per-side and `--pooled`)

**Setup.**
- 1h candidates, 0.98M in total.
- 31 features, including 7d / 30d / 90d market and coin trends, drawdown, breadth and funding.
- Holds of 1d / 3d (primary) / 7d, with real funding charged.
- The trend rule is built in as a positive control.

**Result: FAILS, and fails the positive control in both versions.**

| | BT long | BT short | OOS long | OOS short |
|---|---|---|---|---|
| per-side: model-selected net, 72h | −0.01 | −0.02 | −0.66 | +0.04 |
| pooled, side-signed: model-selected net, 72h | −0.08 | −0.14 | −0.62 | +0.16 |
| trend rule, same candidates, 72h | +0.27 | +0.45 | +0.19 | +0.75 [1.7] |
| trend rule, 168h | +0.39 | +1.47 [1.7] | +0.42 | +2.01 [2.2] |

**Out-of-sample IC:** +0.044 per-side, +0.008 pooled. The models lean on breadth, market drawdown,
market vol and funding.

**Why the learner cannot recover the rule.** The trend rule's by-year results flip sign:
- BT long with-trend at 72h: 2020 +1.10, 2021 −0.49, 2022 −2.67, 2023 +1.42, 2024 +0.52,
  2025 −0.31.
- OOS short: 2020 −3.77, then +1.17 / +1.42 / 0 / 0 / +1.51.

The rule is positive on the five-year average, but a walk-forward learner sees only one or two
regimes at a time and has no basis to trust it. **This is a sample limit (a handful of independent
regimes), not an engine bug.** No flexible learner fixes it. The machinery check that matters
passed: the data does contain the effect, and the control measures it.

**Implication for method.** Regime-scale effects cannot be *discovered* from 5 years of data. They
can only be *tested* as low-dimensional, theory-backed rules (time-series momentum is documented
across asset classes and in crypto), one trial at a time, and their evidence is capped near
t ≈ 2 per universe. The way to more evidence is more regimes (block B, forward data), not more
search.

**Recurring sub-pattern.** Model-selected 3-ATR long rungs are positive in every run and universe
(72h: +1.20 / +0.33 pooled, +0.42 / +0.39 per-side). It was found by looking, so it is a trial for
block B, not evidence.

**Candidates for ONE-SHOT block-B tests, to be pre-registered before reading B:**
1. TREND sleeve: market 30d sign, all coins, daily, 7d hold, real funding.
2. Grid dips gated by trend: k2 rungs only with the 30d trend, 12h–3d hold.
3. k3 long rungs held 72h.

## PRE-REGISTRATION: block B one-shot tests (written 2026-10-04, before any block-B read)

Block B = trades entered from 2025-07-01 to the end of the cache (2026-10-02). Lookback data from
before 2025-07-01 may be used for signals. Trades whose exit falls past the end of the data are
dropped. Both universes (BacktestCoins, OosCoins) are evaluated separately, and BOTH must pass.
Code: `research/blockB_test.py`, committed together with this section. **Run once. Nothing is
tuned afterwards.**

**Trial 1: TREND sleeve.**
- signal = the sign of the equal-weight universe market's trailing 720h log return, at the close
  of the 00:00 UTC hourly bar;
- every coin, every day: side = signal, entry at the next open (taker), exit at the close 168h
  later (taker);
- net = side · log return − 0.21% − funding;
- funding = real per-symbol settlements (a long pays a positive rate); the 0.01%/8h floor applies
  either way where data is missing, including after a symbol's last cached settlement.
- Baselines: the same entries always long, and always short.
- **PASS:** mean net > 0 AND mean net > max(always-long, always-short), in both universes.
  Week-clustered t is reported but not gated: 15 months is about 65 weeks and a few regimes, so
  power is low by construction.

**Trial 2: Grid dips gated by trend.**
- one-bar k=2 rungs on 1h bars (`grid_paths.fills`: close_t ∓ 2·ATR14, per rung, touch fill,
  maker entry), arming bar in block B;
- kept only if the rung's side equals the trend signal at the arming bar (the same 720h market
  sign, evaluated hourly);
- exit at the close 24h after the fill bar (PRIMARY; 12h and 72h reported), taker;
- net = side · ln(exit / fill px) − 0.125% − funding (as above).
- **PASS:** with-trend mean net > 0 at 24h AND (with-trend − against-trend) > 0 at 24h, in both
  universes.

**Trials charged:** 2. If either passes, the next evidence is forward or papertrade data, not more
of B.

## BLOCK B RESULT (run once, 2026-10-04, pre-registration 3bda0da): BOTH FAIL

**Trial 1, TREND sleeve, 7d hold.**

| | trend | always-long | always-short | long share | verdict |
|---|---|---|---|---|---|
| BT | −0.06% [−0.1] | −1.35 | +0.93 | 34% | FAIL |
| OOS | +0.21% [+0.3] | −1.59 | +1.17 | 28% | FAIL |

Block B was a persistent alt downtrend, and always-short beats the sleeve. The 30d signal
whipsawed into longs at the wrong times:
- 2025-07: −6.5 / −6.0;
- 2026-08: −5.6 / −6.8;
- 2026-09 recovered (+11.9 / +9.1).

**Trial 2, k2 dips gated by trend, 24h (primary).**

| | with trend | against trend | diff | verdict |
|---|---|---|---|---|
| BT | −0.42% [−1.4] | −0.36 | −0.06 | FAIL |
| OOS | −0.23% [−1.0] | −0.33 | +0.11 | FAIL |

At 72h (reported only), with-trend is −0.10 / +0.23 against −0.36 / −0.31: the right sign, but
not significant.

**Reading.**
- The block-A trend effect (t ~2, sign-flipping by year) did not carry into 15 more months.
- That is consistent with the "few regimes" diagnosis: a real but weak, regime-dependent effect,
  or none.
- The persistent negative alt drift that made always-short win in B was NOT a trial, and is not
  claimed.
- Block B is now spent for these two hypotheses. Further evidence on them can come only from
  forward data.

## PRE-REGISTRATION: cross-sectional momentum (written 2026-10-04, before any run)

**Source.** Liu, Tsyvinski & Wu (2022, *Journal of Finance*): weekly long-short on past-return
quintiles is one of the three crypto factors. It is market-neutral, so it is independent of the
market-level effects that failed above. Code: `research/csmom.py`, committed with this section.

**Rule.**
- Rebalance at the close of the Monday 00:00 UTC hourly bar. Trade at the next open (taker), hold
  to the next rebalance's open.
- Eligible coins: a valid price over the full look-back, and NOT in the bottom 20% of trailing-30d
  quote volume among the coins with prices that week.
- Rank by the past log return over L = 3 weeks (PRIMARY; 1, 2 and 4 weeks reported only).
- Long the top quintile, short the bottom quintile, equal weight, dollar-neutral (+0.5 / −0.5
  gross).
- Costs: taker 0.105% per side on turnover Σ|Δw|.
- Funding: real per coin (longs pay positive rates, shorts receive); the floor applies on |w| where
  data is missing.
- A coin with no price at exit is marked at its last price that week.

**Block A test** (weeks before 2025-07-01), both universes. **PASS requires all of:**
- mean weekly net > 0 with t ≥ 2 (weekly, NW lag 1), in BOTH universes;
- both halves of A positive in both universes;
- beats ≥ 95% of 500 random-ranking placebos (the same weekly structure with shuffled ranks), in
  both universes.

The t ≥ 2 bar (not 3) applies because this is ONE trial of a published, theory-backed factor; see
the method note above.

**Block B.** Run ONCE, and only if block A passes: the same code from 2025-07-01. PASS = mean net
> 0 in both universes (t reported).

**Trials charged:** 1.

## RESULT: cross-sectional momentum, block A, FAIL (2026-10-04, pre-registration 9146b2c)

| | mean / wk [t] | Sharpe | maxDD | halves | placebo rank | verdict |
|---|---|---|---|---|---|---|
| BT, L=3w | +0.38% [1.35] | +0.70 | −19% | +0.04 / +0.72 | beats 99.6% | fails t ≥ 2 |
| OOS, L=3w | **−0.58% [−1.60]** | −0.89 | −79% | −0.94 / −0.23 | beats 5.4% | FAIL |

The other look-backs (reported only) show the same split: BT positive at 2–3w (Sharpe ~0.7), OOS
negative at every L.

**Reading.**
- **On the liquid BacktestCoins the factor is there in rank terms** (above 99.6% of random
  rankings, β ≈ 0). It is not significant over 210 weeks: 2021 −18%/yr, then +17 / +41 / +30 / +10.
- **On the smaller OosCoins it REVERSES**, mostly through the losers leg: past losers rose
  +0.60%/wk, a −79% drawdown, and 2022 was −79%/yr. That matches the residual reversal seen in
  `j4big`.
- **Caveat, not an excuse:** the OOS universe is survivors only (37 delisted coins missing). That
  biases a short-losers leg against itself, so OOS understates momentum by an unknown amount.

Pre-registered verdict: FAIL. Block B is not run. A liquid-only momentum variant would be a new
trial, and it was suggested by this result, so it would need forward data.

## FROZEN FORWARD SPEC: per-coin trend + ATR trailing stop + vol sizing (2026-10-04)

The full spec is in the docstring of `research/trend_forward.py` and is frozen at the commit that
adds it. In short:
- 6h bars, the top-20 coins by 30d quote volume (refreshed monthly);
- a 20-day Donchian breakout to enter, long or short;
- a 2.5×ATR14 trailing stop on closes to exit;
- weight min(10%, 1% / daily vol);
- taker costs and real funding.

The parameters come from the cited papers, not from our data.

**Evidence = forward rows only,** appended by `--shadow` to `data/forward/trend/book.csv`, one
pass per 6h bar.

**Review:**
- 6 months: report only.
- 12 months: PASS = net > 0, Sharpe ≥ 0.75, both halves positive.
- Kill switch: drawdown > 25%.

`--backtest` is information only.

**Informational backtest of the frozen spec (run after the freeze, 40377fa).**

| | ann | vol | Sharpe | maxDD |
|---|---|---|---|---|
| Block A | −10.2% | 76% | −0.13 | −93% |
| Block B | +21.5% | 58% | +0.37 | −53% |

- 1,871 trades, 39% winners.
- **Longs +2.31% mean gross, shorts −0.73%.**
- By year: 2025 −68%, 2026 +36%.

**Reading.**
- The literature design does not show an edge on our universe before the holdout.
- The sizing rule (1% daily vol PER position, up to 20 correlated coins) gives a 60–76% book vol.
  That only scales risk; it does not change the Sharpe.
- The long/short asymmetry matches the papers' 70/30 long tilt. Survivorship flatters longs here.
- No change is made to the frozen spec. Whether to run it forward is a decision, recorded below.

## Rule-path detector + logistic dynamic grid (2026-10-04, `research/rulepath.py`): FAIL

**Setup.**
- 1.01M rung fills, each run through 5 path exits.
- 30 per-rung-type logistic models, fitted on discovery (< 2024).
- Validated as a managed book, 2024-01 → 2025-06.

**Two artefacts were caught before the result.**
- A crash on final-bar stops.
- A **look-ahead in the cap**: tie-breaking same-bar fills by exit bar let quick winners take the
  slots, so STATIC printed Sharpe +6.4. The arrival order is now outcome-blind.

**Derived rules (discovery coefficients), consistent across exits.**
- Long rungs win more when price is above EMA50 but below EMA200, RSI is low and breadth is high.
- k3 long rungs add the coin's 7d strength.
- Short rungs win more when RSI is high and breadth is low.

That is "buy dips in strength, sell rips in weakness", rediscovered without being told.

**Validation book** (Sharpe on daily P&L; DSR at N = 32):

| | ALWAYS | GATED | STATIC (k1 TPSL1) | RANDOM | permuted-label nulls (ALWAYS) |
|---|---|---|---|---|---|
| BT | **+0.91** [t 1.1], +31%/yr | +0.75 | −1.51, −43%/yr | +1.06 | 0.59–1.49 → beats 2/5 |
| OOS | **+1.49** [t 1.8], +54%/yr | +1.02 | −2.83, −89%/yr | −0.16 | 0.64–1.28 → beats 5/5 |

**Reading.**
- The static 1-ATR take-profit grid is a heavy loser.
- Choosing the rung type per bar turns it positive. **But most of that comes from the STRUCTURE of
  the choice, not the indicators.** Permuted-label models (which keep each type's base win rate
  and its W̄ / L̄) reach Sharpe 0.6–1.5 by preferring deeper rungs and longer exits. In BT, random
  rung types do as well.
- The indicator conditioning adds value only on OosCoins (beats 5/5 nulls, t 1.8). It fails the
  t ≥ 3 bar and the "beats all nulls in both universes" rule.
- The rules are real enough to rediscover. They aren't strong enough to beat a good structural
  default by a margin these 18 months can confirm.

**Lead, not evidence:** "always-on grid that prefers deep rungs and longer exits" (what the nulls
converge to). It was found by looking, so it needs forward data.

**STRUCT vs ALWAYS** (2026-10-04, `research/struct_vs_dyn.py`; INFORMATIONAL, because validation
had already been seen).

STRUCT = per side, the best discovery rung type:
- long: k3 + TIME24 (discovery mean +0.28%, 55% wins);
- short: k3 + TRAIL30 (+0.10%).

| | STRUCT | ALWAYS (logistic) | STATIC k1 TPSL1 | ALWAYS − STRUCT |
|---|---|---|---|---|
| BT | Sharpe +0.50, +15%/yr | +0.91 | −1.51 | +0.52 [t 0.64] |
| OOS | Sharpe +0.72, +23%/yr | +1.49 | −2.83 | +0.96 [t 1.18] |

**Reading.**
- The structural default alone turns the grid from a heavy loser into a positive book.
- The indicator rules add a consistent but non-significant ~+0.5–1.0 Sharpe on top.

**C# port.** The current Grid genes cannot express it (step ≤ 2 ATR, no time-only or trailing
exit, rungs rest for a whole session). It needs a simulator mode, plus a `RandomWalkNullTests`
entry.
