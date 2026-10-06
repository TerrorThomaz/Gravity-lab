# PRE-REGISTRATION: residual reversion with a trailing, pooled λ (2026-10-06)

Written before any outcome of this design is read. Frozen at the commit that adds this file together
with `research/resid_lambda.py`. Its selftest passed before the freeze. The selftest also forced two
design choices before any real data was touched (see "Instrument").

## Why

Residual reversion (each coin's move net of $ + top-3 covariance PCs) is the most consistent
near-miss in the repo, at about +0.05% per trade across four studies. But:
- **It decays.** The 4h autocorrelation went from −0.051 to −0.013 across block A, and j4big went from
  +45% in 2022 to +1% in 2026.
- **Per-coin speed does not persist.** Half→half rank correlation is −0.13, sign agreement 38%, and a
  rolling 30-day per-coin estimate has an sd 5× the effect.

So λ (signal horizon and holding period) is estimated:
- **on a trailing window,** to track the decay;
- **pooled by liquidity quartile × covariance family,** never per coin.

This is the user's design (2026-10-06).

## Rule (all fixed now)

- **Residual.** Hourly, from `anomaly_fade_bounce.residuals`. z_h = the h-hour cumulative residual
  divided by its trailing 720h sd, strictly before.
- **Event.** |z_h| ≥ 2. Trade **against** the residual's sign: enter at the next hour's open (taker),
  exit at the close H hours later. Unhedged coin, with real funding on the coin leg.
- **Cells.** Refit on the first hour of each month from the trailing 90 days:
  - liquidity quartile from median quote volume;
  - covariance family = k-means (k = 4) on the coins' loadings on the top-3 PCs of the trailing
    return covariance;
  - 16 cells in all.
- **λ, refit weekly (Mondays 00:00 UTC).** For each cell and each (h ∈ {2, 4, 8, 24}, H ∈ {2, 4, 8,
  24}), take the mean of −sign(z)·(next-H residual) − cost over the trailing 90 days. Rules for that
  mean:
  - only labels that ended before the week (purge);
  - events thinned to non-overlapping ones (t mod max(h, H) = 0).
- **Which combination trades.** The best combination is traded next week only if:
  - its trailing **t ≥ 2.5**, and
  - it has ≥ 100 events;
  - **thin cells fall back** to their liquidity quartile, then to the whole universe;
  - otherwise the cell stands aside.
- **Book.** One position per coin, ≤ 20 live, 5% per position, arrival order.
- **Costs (round trip).** Fee 0.11% + 2 × the measured half-spread by liquidity quartile (recorder,
  Oct 2026: 4.2 / 2.9 / 1.8 / 0.6 bp), i.e. 0.194 / 0.168 / 0.146 / 0.122%. The repo's flat 0.21% is
  reported too.

## Arms

| arm | λ pooled over | role |
|---|---|---|
| **cell** | liquidity × family (fallbacks above) | **PRIMARY** |
| liq | liquidity quartile only | does the family add anything? |
| global | the whole universe | does any grouping add anything? |
| fixed | none: h = 4, H = 4, always on | textbook baseline |

**Null:** the primary's trades taken at the same coin 1–30 days earlier or later (20 time-shifts).

## Data and honesty

- **Whole history, both universes,** walk-forward by construction: every estimate uses only data
  before it is used.
- The **design** was informed by seen data. Residual reversion was studied on all of it, including
  block B through `j4big_book`. So this is not a clean holdout. The forward period is the real test.

## PASS (primary, in BOTH universes)

1. mean net > 0 with week-clustered t ≥ 3;
2. positive in both halves;
3. **positive over the last 12 months** (the decay test);
4. beats ≥ 19 of 20 time-shifts.

The grouping hypothesis (the user's) is reported separately as cell vs liq vs global. It is not a
pass condition.

- **If PASS:** report-only forward shadow logging. Nothing goes live.
- **If FAIL:** the residual-reversion family is closed, tuned to its best pooled form.

## Instrument (selftest)

The selftest uses synthetic residuals with reversion planted in one liquidity quartile, plus pure
noise.
- **Final version:** 100% of trades land in the planted quartile (+0.26% net). On noise the planner
  stands aside: 86 trades in 600 days.
- **It forced two choices before the freeze:**
  - **The t ≥ 2.5 gate.** Without it, best-of-16 on noise cleared the cost line in every quartile.
  - **Non-overlapping events.** Overlapping hourly events inflate the trailing t several-fold.

## Trials

1 primary + 3 comparison arms = **4**.

## RESULT (run once, 2026-10-06, pre-registration 22d7af6): FAIL

Log: `reports/resid_lambda_run.txt`. Net % per trade [week-clustered t], recorder-calibrated costs:

| arm | OosCoins | BacktestCoins |
|---|---|---|
| **PRIMARY liq × family** | +0.049 [+0.47], halves +0.16 / −0.06, **last 12m −0.171** (n 533), 20/20 shifts | +0.003 [+0.02], halves +0.09 / −0.09, last 12m +0.54 (n 45), 20/20 shifts |
| liquidity only | −0.334 [−1.24], n 559 | +0.047 [+0.38], n 1,612, last 12m: stood aside |
| global | +0.057 [+0.50], n 2,205 | +0.170 [+0.59], n 855, last 12m: stood aside |
| fixed textbook (h = 4, H = 4) | **−0.081 [−5.06]**, n 37,312 | **−0.107 [−6.94]**, n 57,996 |

**Verdict: FAIL.** t ≈ 0 in both universes, and the OOS last 12 months are negative.

**What the self-calibration did buy:**
- **The textbook rule loses heavily** (t −5 / −7), its cost paid on every event. The trailing-λ
  planner turns that into ≈ 0.
- **It learned to stand aside.** On BacktestCoins it traded only 45 times in the last 12 months, and
  the simpler arms not at all. That is the decay detector working: it found nothing worth the cost
  and stopped. It avoids losses; it does not make money.
- **The timing is real.** It beats all 20 time-shifts in both universes (the null medians are
  −0.17 / −0.18, because random-time trades pay the cost and get nothing). But the edge over the
  cost is ≈ 0.
- **Grouping:** liquidity × family ≈ global on OOS (+0.049 vs +0.057), and liquidity-only is worse.
  The family split adds nothing measurable.

**The residual-reversion family is closed.** Pairs, `resid`, `j4big`, the Grid selector (unproven),
structure fades and this trial all measure the same small effect, and it has decayed below cost.
