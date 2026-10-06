# PRE-REGISTRATION: is Grid's edge forced selling? Open-interest drops at arming (2026-10-07)

Written before any OI statistic is read. Frozen at the commit that adds this file together with
`research/grid_oi_cascade.py`.

## Mechanism

Grid earns on market-wide dips that rebound within hours. That edge does not exist in US stocks (see
`STOCKS_CHECK_2026-10.md`). The candidate cause is **liquidation cascades**: leveraged perp longs are
force-closed at market, the selling overshoots, and price refills. The footprint is **open interest
falling** during the dip. If this is the cause:
- Grid trades armed during OI drops should earn more;
- dips with OI drops should rebound more than dips without.

## Data

- **OI:** hourly open interest (`candle_cache/*_oi1h.csv`, 2021-01 →), read at the last hour
  ≤ arming − 1h.
- **Trades:** Grid / GridShort trades in `reports/edgetest_raw_trades_{oos,backtest}.csv` (genotype =
  the frozen forward one).

## PRIMARY (both universes)

**Signal:** 4h OI change z (log change ÷ trailing 720h sd, strictly before). Prefer the **low** tercile.

**PASS (as `grid_resid_select`):**
1. lift > 0 with week-clustered t ≥ 3;
2. both halves > 0;
3. beats ≥ 95% of 2,000 random same-size selections;
4. beats ≥ 19 of 20 time-shifts.

## SECONDARY (report)

- **GridShort,** same signal.
- **Event study, all coins, hourly:** dip (1h price z ≤ −2) with an OI drop (1h OI z ≤ −2), against a
  dip without (OI z > 0). Forward 2 / 4 / 8h from the next open, as excess over the coin's mean,
  day-clustered.

**If PASS:** OI-gated arming becomes a forward shadow ranking next to the frozen Grid. Grid itself is
not changed before 2027.

**Trials:** 2 (primary + event study).
