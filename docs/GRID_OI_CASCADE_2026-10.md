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

## RESULT (run once, 2026-10-07, pre-registration 423e02d): PRIMARY FAIL; the cascade effect exists, outside Grid, on liquid coins

Log: `reports/grid_oi_cascade_run.txt`.

**Primary, Grid selected by the 4h OI change:**
- OOS: lift −0.011 [t −0.28], 34% vs random, 4/20 shifts.
- BT: +0.011 [t +0.26], 70%, 12/20.
- **FAIL.** Grid earns the same whether or not OI is falling at arming. **Grid's edge is not the
  liquidation cascade.** Grid arms in calm, compressed markets (low ADX, tight bands); cascades happen
  in fast ones.

**GridShort (secondary):** OOS +0.094 [t 1.38], 98% vs random, 20/20 shifts. BT +0.022 [t 0.32]. Not
consistent across universes.

**Event study (secondary): dip + OI drop vs dip alone, excess over the coin's mean:**

| | 2h | 4h | 8h |
|---|---|---|---|
| BT, dip + OI drop (n 4,037) | +0.194 [1.5] | **+0.365 [2.2]**, halves +0.27 / +0.46 | **+0.685 [2.5]**, halves +0.52 / +0.86 |
| BT, dip, OI not falling (n 40,376) | +0.060 | +0.094 | +0.125 |
| OOS, dip + OI drop (n 2,453) | +0.046 | +0.127 [0.8] | +0.076 [0.3] |
| OOS, dip, OI not falling | +0.067 | +0.085 | +0.088 |

**Robustness, report only (looked at after the result):**
- **BT, net of a 0.21% taker round trip, 8h:** +0.44% per event; median +0.26%; 53% winners. It is
  +0.49% without the 5 most extreme days.
- **BT by year:** 2021 +1.39, 2022 **−1.08** (the bear year, FTX), 2023 +0.67, 2024 +1.16, 2025 +0.66,
  2026 +0.32.
- **OOS:** −0.19% net, and −0.20% without the extreme days.

**Reading.**
- **Liquidation cascades in LIQUID perps rebound,** about 0.4% per event net of taker costs over 8h.
  Small coins' OI drops do not rebound; there they look like capitulation.
- **The bear year loses.** A falling market keeps cascading.
- **This is a new lead, not a pass.** It was found in a secondary, over the full history (block B
  included), and its robustness was examined after the result.
- **Next:** pre-register a liquid-coin cascade-rebound rule, with a bear-regime question (does the
  trend hybrid's signal gate it?) fixed in advance, as a **forward** test. The recorder's liquidation
  stream measures cascades directly from now on.
