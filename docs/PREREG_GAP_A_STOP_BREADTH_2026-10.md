# Pre-registration — Gap A: Grid stop-out breadth as a fast hedge / pause (2026-10-07)

Written and committed BEFORE any outcome was computed. Only the signal's own frequency was looked at
(stop timestamps, no returns) to pick K.

## Theory

Grid sells the market's moves on an hours scale (concave); its losses are range breaks — 7.5-8% of
sessions end on the hard stop / bail-out at ≈ −2.2% vs +0.37% for the rest. The only hedge in the book
(BTC+ETH trend, 30d/7d) is convex on a weeks scale, so it arrives after the damage. Breadth of strategy
state is the one information class that has shown timing power here (FadeShort breadth, z +3.9 vs
time-shifted copies). Hypothesis: stops clustering across coins = the market leaving its range, which
persists for hours, so (A1) new grid sessions armed right after are worse than average, and (A2) the
equal-weight basket keeps falling.

## Data

`reports/edgetest_raw_trades_{oos,backtest}.csv` from `edgetest` on this branch (GA Grid genotype, maker
costs, next-bar fills), new `stop` column = session ended on the hard stop or the bail-out
(`GridSimulator` `stopOut`). OosCoins primary, BacktestCoins secondary. Seen data: information only.

## Signal (fixed)

- A stop is KNOWN at `exit_time + 1h` (h1 bar open + 1 bar).
- Trigger when ≥ K distinct coins have a known stop in the trailing W = 6h. Episodes ≥ 24h apart
  (a trigger inside a live episode is ignored).
- **K = 2 primary**, K = 3 secondary (reported, not a second chance).

## A1 — pause

Drop every Grid session whose decision time (`entry_time + 1h`) falls in `[known, known + 24h)`.
PASS (both universes): mean return of the paused sessions < 0, AND below ≥ 95% of 200 random-episode
placebos (same episode count, uniform times, same 24h window), AND below ≥ 19 of 20 time-shifted copies
(shifts ±7…±70 days), AND removing them raises the Grid total in both halves of the sample.

## A2 — fast basket short

Short the equal-weight basket of the universe's coins (those in the dump, 15m cache) from the first
15m open at or after `known`, for 24h. Cost 0.21% round trip (taker, `TradeCosts` fee 0.11% + 2×5 bp
slippage); funding ignored (shorts usually receive it — conservative).
PASS (both universes): mean net > 0 with t ≥ 2 over episodes (non-overlapping by construction), AND
above ≥ 95% of 200 random-timing placebos, AND above ≥ 19 of 20 time-shifted copies, AND net > 0 in
both halves.

## Reported, not judged

K = 3; the Grid P&L that exits inside each episode window (does the hedge pay when Grid loses?);
sensitivity H ∈ {12, 48}h — not candidates.

Trials: 2 (A1, A2) at K = 2. Script: `research/grid_stop_breadth.py`.

## Results (run 2026-10-07, after the commit above) — FAIL, both actions, both universes

| K=2 (primary) | OosCoins | BacktestCoins |
|---|---|---|
| episodes | 105 | 118 |
| A1 paused sessions, mean vs kept | **+0.313% vs +0.162%** (paused are BETTER) | +0.050% vs +0.200% |
| A1 random / shifts / halves | below 15% of random, 4/20 shifts, +11.8/+57.4 | 88%, 19/20, −10.1/+27.7 |
| A2 short net (gross), t | +0.039% (+0.249%), t 0.11 | +0.316% (+0.526%), t 0.76 |
| A2 random / shifts / halves | above 78%, 11/20, −0.29/+0.23 | 96%, 18/20, −0.11/+0.56 |
| corr(short, Grid P&L in window) | −0.24 | −0.25 |

K=3 also fails everywhere. Read: (A1) on the primary universe the sessions armed right after a stop
cluster are Grid's BEST — the post-flush rebound is the edge, so pausing there removes it. (A2) the short
points the right way and pays when Grid loses (ρ −0.25), but a 24h basket move carries ~4.5% noise per
episode; ~110 episodes would need a ~0.9% mean for t 2. Low power on this sample, and it bleeds in the
first half in both universes (fails no-bleed regardless). Not a live candidate; a forward shadow could
accumulate episodes, but at ~22/yr it would take years.
