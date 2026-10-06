# PRE-REGISTRATION: one forecast, three uses (2026-10-06)

Written before any forecast is computed. Frozen at the commit that adds this file together with
`research/forecast_engine.py` and `research/forecast_uses.py`.

## Why

Every search found price information at IC ≈ 0.04–0.05 and judged it per candidate, as standalone
round trips that each pay full taker cost. Funds monetize ICs like that differently:
- by **sizing** trades that happen anyway;
- through **portfolios** that trade only changes;
- by **cross-sectional ranking**.

The user's proposals (2026-10-06):
- link the forecast to a **bet-size allocator**;
- use it as a **forecast over the market**;
- a spot variant, considered and deferred (below).

## Forecast (shared input; not itself a trial)

- **Engine:** LightGBM with gbm_np's spec.
- **Target:** every coin's next-4h log return, forecast every hour.
- **Refit:** quarterly from 12 months in.
- **Training:** BacktestCoins rows whose label ended before the quarter, sampled every 4h, at most 1.5M
  rows, target winsorised at 0.5/99.5%.
- **Forecasts:** both universes; OosCoins are never trained on.
- **Features:** discover15 + the multi-length indicator block, minus the ~0-importance families (adx14,
  vz, fund, rpos96, vwap96), minus rung depth and calendar (hour, dow).
- **Coverage:** the full history to 2026-10-06, walk-forward by construction.
- **Honesty:** the 2025-07 → 2026 stretch was seen in earlier Grid research, but no forecast model saw
  it before forecasting it.

## Uses (fixed now)

**1. ALLOCATOR.** Each Grid / GridShort trade (`reports/edgetest_raw_trades_{u}.csv`) is sized by its
coin's forecast at the latest forecast time ≤ arming:
- z = forecast / (trailing-720h mean cross-sectional sd of forecasts);
- Grid: clip(1 + 0.5z, 0, 2);
- GridShort: clip(1 − 0.5z, 0, 2);
- book = Σ size × return × 5% per trade, by exit day.

**2. NEUTRAL.** Hourly portfolio:
- weights ∝ the cross-sectional z of the forecast, clipped ±3, demeaned (dollar-neutral), gross 1;
- a coin trades only when its target moves > 0.25% of capital;
- costs:
  - taker: 0.055% per side + half-spread by trailing liquidity quartile (4.2 / 2.9 / 1.8 / 0.6 bp);
  - maker: 0.02%, fills assumed, reported only;
- funding on positions;
- reported at 0h and at a conservative 1h execution delay.

**3. OVERLAY.** As use 1, but z = the market forecast (cross-sectional mean of all coins' forecasts)
divided by its trailing-720h sd.

## Nulls

- **Uses 1 and 3:** z shuffled across trades (200), and z taken 1–30 days away on the same coin (20).
- **Use 2:** forecasts shuffled across coins within each hour (20), and time-shifted per coin (10).

## PASS (BOTH universes)

**Uses 1 and 3** (sized book against the flat book):
1. ΔSharpe > 0;
2. ΔSharpe > 0 in both halves;
3. beats ≥ 95% of the shuffles;
4. beats ≥ 19 of the 20 time-shifts;
5. maxDD no worse than 1.1 × the flat book's.

**Use 2:**
1. taker, 0h delay: Sharpe with weekly t ≥ 3;
2. last 12 months > 0;
3. beats all 30 nulls;
4. the 1h-delay run is reported. If it is ≤ 0, the result is labelled latency-fragile.

**If a sizing use passes:** shadow sizing only, alongside the frozen Grid forward test. Grid itself is
not changed before the 2027 checkpoints.

## Spot (deferred, written down now)

Bybit non-VIP spot fees are 0.1% maker and taker, against 0.02% / 0.055% on perps. Grid's trades re-fee'd
to spot lose about 0.16% per round trip: OOS +0.169 → +0.009%/trade, BT +0.196 → +0.036. **A spot grid
is dead.** Spot only fits a slow, long-only tilt (weekly rebalance, against an equal-weight spot basket,
no funding). That needs a multi-day forecast (`forecast_engine.py --hours 168`) and its own
pre-registration.

## Trials

**3** (uses 1, 2, 3).

## RESULT (run once, 2026-10-06, pre-registration 1e0b069): ALL THREE FAIL

Log: `reports/forecast_run.txt`. Forecasts: `data/forecast/{oos,backtest}_h4.npz`, covering 63–66% of
hour × coin cells (after the 12-month warm-up and coin listings).

**1. Allocator (coin forecast sizes Grid / GridShort).** Book = daily P&L, Grid + GridShort.

| | flat Sharpe / maxDD | sized Sharpe / maxDD | ΔSharpe (halves) | vs shuffles / shifts |
|---|---|---|---|---|
| OOS | 2.38 / −4.1% | 2.19 / −3.7% | **−0.185** (−0.11 / −0.25) | 76% / 19/20 |
| BT | 3.13 / −3.9% | 2.56 / −4.9% | **−0.574** (−0.11 / −0.83) | 9% / 2/20 |

**3. Overlay (market forecast).**

| | sized Sharpe / maxDD | ΔSharpe (halves) | vs shuffles / shifts |
|---|---|---|---|
| OOS | 2.33 / −3.4% | −0.048 (−0.03 / −0.07) | 53% / 16/20 |
| BT | 3.09 / −4.0% | −0.039 (+0.02 / −0.07) | 42% / 13/20 |

**2. Neutral hourly portfolio** (turnover ≈ 7,000x gross per year):

| | taker 0h | taker 1h | maker 0h | maker 1h | vs 30 nulls |
|---|---|---|---|---|---|
| OOS | −421%/yr | −530 | −1 | −110 | 30/30 |
| BT | −486%/yr | −531 | −73 | −118 | 30/30 |

**Reading.**
- **Uses 1 and 3:** sizing Grid by the forecast lowers Grid's Sharpe in both universes.
  - On OOS the coin forecast carries a little information (it beats 19/20 time-shifts). But unequal
    bets on trades of equal quality cost more than that information adds.
  - On BT the coin forecast mis-sizes Grid: worse than 91% of random sizings.
  - The market overlay is indistinguishable from chance.
- **Use 2:** the forecast's cross-sectional ranking is real. It beats all 30 null portfolios.
  - **Gross before costs:** about +115–140%/yr at 0h delay (OOS), but only about +6–33%/yr at a 1h
    delay.
  - **Why:** most of the ranking power is last-print microstructure, which cannot be traded at the price
    that produced it.
  - At ~7,000x turnover, costs are 140–540%/yr.

**Verdict.** The price-feature forecast is real information that cannot be monetized at retail costs:
- not as standalone trades (earlier trials);
- not as Grid sizing;
- not as a market overlay;
- not as a neutral portfolio.

A slower variant (a smoothed forecast, longer horizon, wider bands) would be a new trial. Per the 1h-delay
numbers, what survives is ≤ 30%/yr gross before any turnover. The price-only feature space at 1–12h
is closed.
