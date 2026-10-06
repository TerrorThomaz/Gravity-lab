# PRE-REGISTRATION: multi-length indicators in the open search (2026-10-06)

Written before any run on market data. Frozen at the commit that adds this file together with
`research/indicators15.py`. Its causality selftest passed before the freeze.

## Why

discover15's 29 features saw:
- RSI at **one** length (14 bars = 3.5h);
- EMA distance at 5h / 12.5h / 50h;
- **no** MACD, **no** stochastic;
- no oscillator looking back 7 days.

The user's question: the rules may be right while the indicators are used inefficiently. Most
indicators re-weight past returns that the return windows already span. But bounded transforms and
cross-length combinations ("short RSI low inside a 7-day uptrend") cost a depth-3 tree most of its
splits to construct.

## Block (19 features on 15m bars, per coin, all causal)

- **RSI** at 6, 56, 224 and 672 bars (14 is already in the base).
- **EMA distance / ATR** at 96, 384 and 672 bars (1d, 4d, 7d).
- **MACD (12, 26, 9)**, line and histogram / ATR, on 15m, 1h and 6h time scales.
- **Stochastic %K** at 14, 56 and 224 bars.
- **Cross-length:**
  - RSI14 − RSI224;
  - (RSI14 − 50) × sign of the 7-day EMA's 24h slope.
- **VWAP:** distance to the 24h VWAP / ATR.

## Harness (structure15's, unchanged)

- Identical candidates (discover15's seed 20261009) and the same model spec.
- Quarterly walk-forward on BacktestCoins. OosCoins are scored, never trained on.
- **Engine:** LightGBM with gbm_np's spec, now the default, run in `~/Gravity-lab/.venv-research`. Its
  selftest gives planted IC +0.115 against +0.113 for gbm_np, nothing without the planted column, and
  nothing on the label null.
- **Arms:**
  - A = base 29;
  - B = base + block;
  - P1–P5 = B with the block's rows permuted jointly (seeds 8000–8004).
- **Metric:** out-of-sample rank IC, paired by quarter × side.

## PASS (H = 16 = 4h, BOTH universes)

1. Δ(B − A) > 0 with t ≥ 2;
2. positive in both halves;
3. beats all 5 placebos.

**If FAIL:** report MDE80. If MDE80 ≤ 0.01, the result is "absent at literature size".

## Trials

1 primary + 2 secondary horizons = **3**.

## RESULT (run once, 2026-10-06, pre-registration 1c288c5): FAIL by the OOS t only

Log: `reports/indicators15_run.txt`. Engine: LightGBM. Base IC 0.042–0.056, similar to gbm_np's.

| H | univ | Δ(B − A) [t] | halves | placebos beaten | MDE80 |
|---|---|---|---|---|---|
| **16 (primary)** | BT | **+0.0038 [+2.08]** | +0.0043 / +0.0034 | **5/5** | 0.0052 |
| **16 (primary)** | OOS | **+0.0039 [+1.87]** | +0.0064 / +0.0014 | **5/5** | 0.0059 |
| 4 | BT / OOS | +0.0011 [+0.7] / +0.0020 [+1.3] | mixed / both + | — | 0.004 |
| 48 | BT / OOS | +0.0063 [+1.95] / +0.0048 [+1.40] | +0.012 / +0.001 and +0.010 / −0.001 | — | 0.009 / 0.010 |

- **Importance:** the only block feature in the top 10 is `rsi672` (the 7-day RSI, 10th). The model
  still leans on lbbw, k, disp16, mvol, mz96 and hour.
- **Selection (arm B, H = 16):** −0.024 / −0.086 BT, −0.100 / +0.008 OOS per trade. Hedged is negative
  everywhere. Not tradeable.

**Verdict: FAIL.** The OOS t of 1.87 misses the pre-registered 2. Within that, this is the most
consistent *information* result of the project:
- **About +0.004 IC** (+9% relative), the same size in both universes;
- positive in all four universe-halves at 4h;
- beats every placebo in both universes.

**Indicators at multiple lengths, especially the 7-day oscillator, carry a small real increment that a
one-length feature set missed. The user's hunch is right in direction.**

It is far too small to move selected trades across the cost line. At 12h it lives in the first half
only (the decay pattern again).

**Kept as a feature-set improvement** for any future search: the block is cheap and causal. It is not
a trading result.
