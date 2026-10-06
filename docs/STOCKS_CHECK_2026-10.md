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

## RESULT (2026-10-07, pre-registration 795aa4c + a pre-result data fix): BOTH FAIL

**Data fix.** Yahoo's `range=max` silently returns MONTHLY bars for `interval=1d`. The first run's
trend numbers were therefore monthly returns annualised as daily: "SPY +216%/yr, Sharpe 3.1". They
were read only as an impossibility and voided. The script now requests explicit epochs and asserts
that the bars are daily. The hourly data was correct, so B is unchanged. Log:
`reports/stocks_check_run.txt`. Drawdowns are cumulative log-%.

**A. Trend rule (21-day sign, long/short):**

| | trend %/yr / Sharpe | buy & hold %/yr / Sharpe | beta | alpha t | halves Sharpe |
|---|---|---|---|---|---|
| **SPY + QQQ (primary)** | +2.0 / 0.10 | +9.3 / 0.42 | −0.26 | **+1.22** | +0.01 / +0.22 |
| SPY | +0.1 / 0.01 | +10.3 / 0.55 | −0.23 | +0.91 | +0.02 / −0.01 |
| QQQ | +4.9 / 0.20 | +10.4 / 0.39 | −0.25 | +1.67 | |
| other 6 ETFs | Sharpe −0.03 to +0.13 | | | +0.4 to +1.1 (GLD −0.5) | |

Fails (alpha t 1.22 < 2). Note the **negative beta (−0.2 to −0.3) on every equity ETF**: as in crypto,
the rule mostly acts as a crash hedge. But equities' long-run drift (+9%/yr) makes buy-and-hold the
better book.

**B. Hourly dip rebound** (excess over the same time of day, day-clustered t):

| group | 1h | 2h | 4h | 6h |
|---|---|---|---|---|
| **index ETFs, market-wide dip (primary)** | −0.007 [−0.2] | −0.010 [−0.2] | −0.036 [−0.6] | −0.037 [−0.5] |
| stocks, market dipped too | −0.006 | −0.001 | −0.007 | +0.004 |
| stocks, only the stock dipped | +0.026 [+1.4] | +0.011 | +0.029 [+0.8] | +0.019 |

Fails. Grid's mechanism, the hours-scale rebound after a market-wide flush, **does not exist in US
stocks**. That fits the equity literature on intraday momentum. Grid's edge is crypto-specific: likely
liquidation cascades in leveraged perps, which overshoot and then refill.
