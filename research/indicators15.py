"""Multi-length indicator ablation: does seeing RSI / EMA / MACD / stochastic at many lengths (1.5h to
7 days) add out-of-sample information to the discover15 open search?

Pre-registration: docs/INDICATORS_ABLATION_2026-10.md (frozen at the commit that adds this file).

discover15 saw RSI at ONE length (14 bars = 3.5h), EMA distance at 5h / 12.5h / 50h, no MACD, no
stochastic, nothing looking back 7 days through an oscillator. Most indicators are re-weightings of past
returns that the dense return windows (z1..z384) already span, but bounded transforms and cross-length
combinations ("short RSI low inside a 7-day uptrend") cost a depth-3 tree most of its splits to build.
Same harness as structure15: identical candidates (seed), model, walk-forward; arms A (base) vs B
(base + block) vs 5 placebos (block rows permuted jointly).

Run:  ~/Gravity-lab/.venv-research/bin/python research/indicators15.py
"""

from __future__ import annotations

import gc
import sys

import numpy as np
import pandas as pd

import discover15 as d15
import wf_parallel as wfp
from context_gate import ema, rsi
from structure15 import ic_table, paired

BASE = list(d15.FEATURES)
IND = ["rsi6", "rsi56", "rsi224", "rsi672", "d_ema96", "d_ema384", "d_ema672",
       "macd1", "macdh1", "macd4", "macdh4", "macd24", "macdh24",
       "stoch14", "stoch56", "stoch224", "rsi14_m_224", "rsi14_x_slope672", "vwap96"]
PLACEBOS = 5


def ind_iter(idx, M, A):
    H, L, C, V = M["h"], M["l"], M["c"], M["v"]
    for n in (6, 56, 224, 672):
        yield f"rsi{n}", rsi(C, n)
    for n in (96, 384, 672):
        yield f"d_ema{n}", (C - ema(C, n)) / A
    for s in (1, 4, 24):                                   # MACD(12,26,9) on 15m, 1h and 6h time scales
        m = ema(C, 12 * s) - ema(C, 26 * s)
        sig = pd.DataFrame(m).ewm(span=9 * s, adjust=False, min_periods=9 * s).mean().values
        yield f"macd{s}", m / A
        yield f"macdh{s}", (m - sig) / A
    Hd, Ld = pd.DataFrame(H), pd.DataFrame(L)
    for n in (14, 56, 224):
        lo, hi = Ld.rolling(n).min().values, Hd.rolling(n).max().values
        with np.errstate(invalid="ignore", divide="ignore"):
            yield f"stoch{n}", (C - lo) / (hi - lo)
    r14, r224 = rsi(C, 14), rsi(C, 224)
    yield "rsi14_m_224", r14 - r224
    e672 = ema(C, 672)
    slope = (e672 - np.vstack([np.full((96, C.shape[1]), np.nan), e672[:-96]])) / A
    yield "rsi14_x_slope672", (r14 - 50) * np.sign(slope)
    pv = pd.DataFrame(C * V).rolling(96, min_periods=48).sum().values
    vv = pd.DataFrame(V).rolling(96, min_periods=48).sum().values
    with np.errstate(invalid="ignore", divide="ignore"):
        yield "vwap96", (C - pv / vv) / A


_base_iter = d15.feature_iter


def full_iter(idx, M, fund, S, A, mlr, mcum):
    yield from _base_iter(idx, M, fund, S, A, mlr, mcum)
    yield from ind_iter(idx, M, A)


def selftest() -> int:
    """Causality: scrambling every bar after t0 changes no indicator at or before t0."""
    rng = np.random.default_rng(2)
    T, N = 3000, 3
    C = 100 * np.exp(np.cumsum(rng.normal(0, 0.003, (T, N)), 0))
    M = dict(h=C * 1.002, l=C * 0.998, c=C, v=rng.uniform(1, 2, (T, N)))
    A = np.full((T, N), 0.3)
    full = dict(ind_iter(None, M, A))
    t0 = 2000
    M2 = {k: v.copy() for k, v in M.items()}
    for v in M2.values():
        v[t0 + 1:] = v[t0 + 1:][::-1] * 1.05
    cut = dict(ind_iter(None, M2, A))
    for n in IND:
        assert np.allclose(full[n][: t0 + 1], cut[n][: t0 + 1], equal_nan=True), f"look-ahead in {n}"
    print(f"selftest OK: no look-ahead in {len(IND)} indicators")
    return 0


def main() -> int:
    d15.FEATURES = BASE + IND
    d15.feature_iter = full_iter
    ci = {n: i for i, n in enumerate(d15.FEATURES)}
    rng = np.random.default_rng(20261009)
    print(f"building candidate sets (block A, 15m) + {len(IND)} multi-length indicators … engine {wfp.ENGINE}", flush=True)
    D = d15.build("backtest", rng)
    E = d15.build("oos", rng)
    colsA, colsB = [ci[n] for n in BASE], [ci[n] for n in BASE + IND]
    pc = [i for i, c in enumerate(colsB) if c >= len(BASE)]
    for h in d15.HS15:
        specs = {"A": (colsA, None, None), "B": (colsB, None, None)}
        if h == d15.PRIMARY:
            for z in range(PLACEBOS):
                r = lambda n: np.random.default_rng(8000 + z).permutation(n)
                specs[f"P{z + 1}"] = (colsB, (r(len(D["X"])), pc), (r(len(E["X"])), pc))
        res = wfp.run(D, E, h, specs)
        ics = {nm: {u: ic_table(res[nm]["pred"][u], X_, h) for u, X_ in (("bt", D), ("oos", E))} for nm in specs}
        print(f"\n══ H = {h}{'  [PRIMARY]' if h == d15.PRIMARY else ''}: out-of-sample rank IC, paired by quarter × side ══")
        for u in ("bt", "oos"):
            a = ics["A"][u]
            m, t, h1, h2, mde = paired(ics["B"][u] - a)
            print(f"   {u}: arm A IC {a.mean():+.4f} ({len(a)} quarter-sides)   Δ(B − A) {m:+.4f} [t {t:+.2f}]  "
                  f"halves {h1:+.4f} / {h2:+.4f}  MDE80 {mde:.4f}")
            if h == d15.PRIMARY:
                pm = [paired(ics[f"P{z + 1}"][u] - a)[0] for z in range(PLACEBOS)]
                print(f"      placebo Δ: {' '.join(f'{v:+.4f}' for v in pm)}  → real beats {sum(m > v for v in pm)}/{PLACEBOS}")
        if h == d15.PRIMARY:
            print(f"\n   ── arm B selection table (discover15 format, no label nulls) ──")
            d15.report(D, E, h, res["B"], 0)
            wfp.importance(D, h, res["B"], BASE + IND, colsB)
        del res
        gc.collect()
    print("\nTrials: 1 primary (B − A at H=16) + 2 secondary horizons. Block B untouched.")
    return 0


if __name__ == "__main__":
    sys.exit(selftest() if "--selftest" in sys.argv else main())
