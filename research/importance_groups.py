"""Full and GROUPED permutation importance for the indicators15 arm-B model (H = 16, BacktestCoins OOS
quarters). Report only, no trial: it explains a finished model.

Single-feature permutation splits credit between correlated features: permute lbbw and latr still
carries the volatility, so both look small. Grouped importance permutes each family jointly (one row
permutation for all its columns), which measures what the family as a whole contributes.

Run:  ~/Gravity-lab/.venv-research/bin/python research/importance_groups.py
"""

from __future__ import annotations

import sys

import numpy as np
import pandas as pd

import discover15 as d15
import indicators15 as ind
import wf_parallel as wfp

GROUPS = {
    "volatility (coin)": ["lbbw", "latr"],
    "volatility (market)": ["mvol"],
    "dispersion (cross-section)": ["disp16"],
    "coin returns / momentum": ["z1", "z4", "z16", "z96", "z384"],
    "coin residual (own move)": ["res4", "res16", "res96"],
    "market returns + breadth": ["mz4", "mz16", "mz96", "mz384", "breadth"],
    "EMA distance / trend": ["d_ema20", "d_ema50", "d_ema200", "slope50", "d_ema96", "d_ema384", "d_ema672"],
    "RSI / stochastic (oscillators)": ["rsi14", "rsi6", "rsi56", "rsi224", "rsi672", "stoch14", "stoch56",
                                       "stoch224", "rsi14_m_224", "rsi14_x_slope672"],
    "MACD": ["macd1", "macdh1", "macd4", "macdh4", "macd24", "macdh24"],
    "range position / VWAP": ["rpos96", "vwap96"],
    "trend strength (ADX)": ["adx14"],
    "volume": ["vz"],
    "funding": ["fund"],
    "NOT INDICATORS: rung depth": ["k"],
    "NOT INDICATORS: time of day / week": ["hour", "dow"],
}
NON_IND = {"k", "hour", "dow"}


def main() -> int:
    d15.FEATURES = ind.BASE + ind.IND
    d15.feature_iter = ind.full_iter
    names = d15.FEATURES
    assert sorted(sum(GROUPS.values(), [])) == sorted(names), set(names) ^ set(sum(GROUPS.values(), []))
    rng = np.random.default_rng(20261009)
    D = d15.build("backtest", rng)
    E = d15.build("oos", rng)
    h = d15.PRIMARY
    R = wfp.run(D, E, h, {"B": (None, None, None)})["B"]
    ft = pd.to_datetime(D["fill_time"])
    col = {n: i for i, n in enumerate(names)}
    single = {n: [] for n in names}
    grouped = {g: [] for g in GROUPS}
    prng = np.random.default_rng(7)
    for q0, side, edges, m in R["models"]:
        te = np.where((D["side"] == side) & (ft >= q0) & (ft < q0 + pd.DateOffset(months=3)) & np.isfinite(D["y"][h]))[0]
        if len(te) < 5000:
            continue
        te = np.sort(prng.choice(te, min(len(te), 60_000), replace=False))
        X, y = D["X"][te], pd.Series(D["y"][h][te]).rank()
        ic = lambda P: np.corrcoef(pd.Series(P).rank(), y)[0, 1]
        b = ic(wfp._predict(edges, m, X))
        perm = prng.permutation(len(X))
        for n in names:
            Xp = X.copy(); Xp[:, col[n]] = X[perm, col[n]]
            single[n].append(b - ic(wfp._predict(edges, m, Xp)))
        for g, cols in GROUPS.items():
            Xp = X.copy(); c = [col[n] for n in cols]; Xp[:, c] = X[perm][:, c]
            grouped[g].append(b - ic(wfp._predict(edges, m, Xp)))
    s = pd.Series({n: np.mean(v) for n, v in single.items()}).sort_values(ascending=False)
    gs = pd.Series({g: np.mean(v) for g, v in grouped.items()}).sort_values(ascending=False)
    gse = pd.Series({g: np.std(v, ddof=1) / np.sqrt(len(v)) for g, v in grouped.items()})
    print(f"\nH={h}, {len(grouped['volume'])} quarter-sides. IC drop when permuted (higher = the model relies on it more)\n")
    print("TOP INDICATORS (single feature; rung depth and calendar excluded):")
    for i, (n, v) in enumerate(s[[n for n in s.index if n not in NON_IND]].head(15).items(), 1):
        g = next(k for k, cols in GROUPS.items() if n in cols)
        print(f"   {i:>2}. {n:<18} {v:+.4f}   [{g}]")
    print("\nFAMILIES (permuted jointly — credit no longer split between correlated members):")
    for g, v in gs.items():
        print(f"   {g:<38} {v:+.4f} ± {gse[g]:.4f}   ({len(GROUPS[g])} features)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
