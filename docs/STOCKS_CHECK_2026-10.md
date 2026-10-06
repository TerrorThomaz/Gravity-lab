# PRE-REGISTRATION: do the crypto mechanisms hold in US stocks? (2026-10-07)

Written before any stock data is read. Frozen at the commit that adds this file together with
`research/stocks_check.py`. The rules are copied from the crypto book, not fitted.

## A. Trend: the trend hybrid's rule on index ETFs

- **Signal:** sign of the equal-weight SPY + QQQ 21-trading-day log return.
- **Position:** long or short, 5 overlapping cohorts held 5 days each.
- **Costs:** 1 bp per side on exposure changes. Borrow and cash yield are ignored.
- **Data:** daily adjusted closes from Yahoo.
- **PASS:** alpha against buy-and-hold with t ≥ 2, AND Sharpe > 0 in both halves.
- **Report only:** each of 8 ETFs on its own (SPY, QQQ, IWM, DIA, EFA, EEM, TLT, GLD), and Sharpe by
  decade.

## B. Dip rebound: Grid's mechanism, hourly, last 730 days

- **Event:** a regular-session hourly bar with return ≤ −2 sd of the trailing 70 bars.
- **Trade:** buy at the next bar's open, hold 1 / 2 / 4 / 6 bars.
- **Costs:** 1 bp per side for ETFs, 2 bp per side for stocks.
- **Benchmark:** the excess return over all bars at the same time of day.
- **Groups:**
  - index ETFs (market-wide dips);
  - 30 of today's large caps, split by whether SPY dipped too (z ≤ −1).
- **Statistics:** day-clustered t.
- **PASS:** index-ETF excess at 2h or 4h > 0 with t ≥ 2, positive in both halves.

## Limits

- **Low power for B.** 730 days hold about 5,000 hourly bars per symbol.
- **Survivorship.** The large caps are today's, which biases longs upward.
- **No carry analogue** is tested.

**Trials:** 2.
