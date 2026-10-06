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
