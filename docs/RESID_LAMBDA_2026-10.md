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
