# Forward test: BTC+ETH market trend, HYBRID (frozen 2026-10-04, window starts 2026-10-05)

**Why.** The Binance one-shot (`docs/EDGE_DISCOVERY_SCOPE_2026-10.md`, 76de1b8) passed standalone at
t 2.02 and missed the book-addition bar (alpha t 1.58). Its never-seen pre-2020 stretch is flat. The
hybrid version was chosen AFTER seeing that data, so its backtest is information. Only forward data
can be evidence (tier T3).

## Spec (frozen at the commit that adds this file)

`research/binance_trend.py`, `sleeve(..., hybrid=True)`:
- **Signal:** the sign of the equal-weight (BTC, ETH) 30d log return at each daily close (00:00 UTC).
- **Exposure:** 7 overlapping daily cohorts held 7 days, half BTC half ETH, |gross| ≤ 1.
- **Longs on SPOT** (no funding). **Shorts on the Binance USDT-M perp**: a short receives a positive
  funding rate.
- **Costs:** taker 0.105% per side on the net daily change.

## Backtest information (2017-09 → 2026-10; seen, chosen after the one-shot)

| | Sharpe | CAGR | vol | maxDD | beta to B&H | alpha vs B&H |
|---|---|---|---|---|---|---|
| hybrid | 0.84 | 39.1% | 63% | −75% | 0.08 | +48%/yr (t 2.0) |
| buy & hold 50/50 BTC+ETH | 0.84 | 40.1% | 72% | −88% | 1 | 0 |
| B&H scaled to the hybrid's vol | 0.84 | 38.5% | 63% | −84% | | |

- **Overall:** the hybrid matches buy-and-hold's return at less risk, with almost no market beta.
- **By half:** hybrid 0.95 vs B&H 1.15 in the first half, 0.72 vs 0.41 in the second.
- **Against the live book:** ρ −0.02, alpha t 1.74, +0.40%/day on the book's worst 5% days. Adding it
  at a 20% risk budget lifts book Sharpe from 1.63 to 1.82.

## Logging

- **Daily:** `python3 research/binance_trend.py --shadow`, run by `gravity-trendshadow.timer` at
  00:20 UTC.
- **Ledger:** `~/Gravity-lab/data/forward/trend_hybrid/decisions.csv`, append-only. Each row holds
  the signal, the next day's exposure, the closes, the day's funding and the logging time. A row
  logged more than a day late is flagged `late=1`.
- **Evaluation:** `python3 research/binance_trend.py --fetch && python3 research/binance_trend.py --evaluate`.
  It recomputes the sleeve from freshly fetched data, reports how many logged decisions differ
  (must be 0), and compares the result with buy-and-hold and the live book.

## Decision rule (written before the window opens)

| when | rule | outcome |
|---|---|---|
| 3 / 6 / 9 months | report only | no decision |
| any time | sleeve drawdown worse than −75% (the backtest's) | **KILL-SWITCH**: stop and review |
| 2027-10-05 (12 mo) | net Sharpe ≤ 0 (bleeds) OR alpha vs B&H ≤ 0 (its return is market exposure) | **RETIRE** |
| 2027-10-05 | net Sharpe > 0 AND mean on the live book's worst 5% days > 0 | **HEDGE CANDIDATE**: continue |
| 2027-10-05 | additionally Sharpe ≥ B&H Sharpe (at equal risk it returns at least as much) | standalone credit |

**Honest limits.**
- Twelve months is a screen, not proof. At Sharpe 0.8, t ≈ 0.8.
- The buy-and-hold comparison over one year is dominated by that year's market: the dry run over
  Jul–Oct 2026 showed B&H Sharpe 4.2 against the hybrid's 0.9.
- Sizing into the live book needs this screen passed AND the user's decision. Retrain-free by
  construction: the spec has no fitted parameters.
