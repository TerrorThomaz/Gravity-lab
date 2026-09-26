#!/usr/bin/env python3
"""
Regime HMM on BTC vs on the universe's own first principal component: same code, same
features, only the anchor differs. Writes reports/regime_{btc,pc1}_py.csv in the column
layout scripts/regime_edge.py reads (time = bar open; usable from time + 1h).

PC1 index: each day, the top eigenvector of the trailing 30d hourly-return covariance of the
top-40 liquid coins (point-in-time, like the carry hedge), sign-fixed and scaled to sum 1; the
next day's hourly index return is those weights on the coins' returns.

Features mirror HmmAnnotator.BuildFeature (EMA 20/50/200 distances, EMA50 slope, 20-bar
momentum, EMA crosses), with the two high/low features replaced by close-only equivalents so
both anchors are treated identically: 14/100-bar return-volatility ratio for ATR14/ATR100, and
the 14-bar efficiency ratio for ADX. Diagonal Gaussian HMM, 6 states, Baum-Welch with 3
restarts, then the causal forward filter with the same smoothing as ForwardFilterSmoothed.
Like the C# HMM, it is FITTED on the whole history; only the filter is causal.

  python3 scripts/factor_hmm.py        # then: python3 scripts/regime_edge.py ... --regimes <csv>
"""
from __future__ import annotations

import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import market_neutral_research as mnr  # noqa: E402

K, RESTARTS, ITERS, ALPHA, DWELL = 6, 3, 60, 0.75, 0.02


def pc1_index(mk: mnr.Market) -> pd.Series:
    n = len(mk.close)
    with np.errstate(invalid="ignore", divide="ignore"):
        rets = np.nan_to_num(mk.close.values[1:] / mk.close.values[:-1] - 1.0)
    rets = np.vstack([np.zeros(rets.shape[1]), rets])
    idx_ret = np.zeros(n)
    for t in range(24 * 30, n, 24):
        uni = mnr.liquid_universe(mk, t - 24 * 30, t, 40)
        if len(uni) < 5:
            continue
        _, vecs = np.linalg.eigh(np.cov(mnr.log_returns(mk, t - 24 * 30, t, uni).T))
        w = vecs[:, -1]
        w = w * np.sign(w.sum())
        w = w / w.sum()                      # a unit-exposure portfolio of the co-movement
        idx_ret[t:t + 24] = rets[t:t + 24, uni] @ w
    start = 24 * 30
    return pd.Series(np.exp(np.cumsum(np.log1p(idx_ret[start:]))), index=mk.close.index[start:])


def ema(x: pd.Series, n: int) -> pd.Series:
    return x.ewm(span=n, adjust=False).mean()


def features(c: pd.Series) -> pd.DataFrame:
    e20, e50, e200 = ema(c, 20), ema(c, 50), ema(c, 200)
    r = np.log(c).diff()
    vol_ratio = (r.rolling(14).std() / r.rolling(100).std()).clip(0, 5) / 5
    er = (c.diff(14).abs() / c.diff().abs().rolling(14).sum()).clip(0, 1)
    f = pd.DataFrame({
        "d20": ((c - e20) / e20).clip(-.5, .5), "d50": ((c - e50) / e50).clip(-.5, .5),
        "d200": ((c - e200) / e200).clip(-.5, .5), "slope50": (e50 / e50.shift(20) - 1).clip(-.1, .1),
        "volratio": vol_ratio, "er": er, "mom20": (c / c.shift(20) - 1).clip(-.5, .5),
        "x2050": ((e20 - e50) / e50).clip(-.3, .3), "x50200": ((e50 - e200) / e200).clip(-.5, .5),
    })
    return f.iloc[200:].dropna()


def emissions(X, mu, var):
    lp = -0.5 * (np.log(2 * np.pi * var)[None] + (X[:, None, :] - mu[None]) ** 2 / var[None]).sum(axis=2)
    m = lp.max(axis=1, keepdims=True)
    return np.exp(lp - m), m[:, 0]


def forward(B, A, pi):
    T = len(B)
    a = np.zeros((T, K)); c = np.zeros(T)
    a[0] = pi * B[0]; c[0] = a[0].sum(); a[0] /= c[0]
    for t in range(1, T):
        a[t] = (a[t - 1] @ A) * B[t]
        c[t] = a[t].sum(); a[t] /= c[t]
    return a, c


def fit(X: np.ndarray, seed: int):
    rng = np.random.default_rng(seed)
    T, d = X.shape
    mu = X[rng.choice(T, K, replace=False)]
    var = np.tile(X.var(axis=0), (K, 1))
    A = np.full((K, K), 0.02 / (K - 1)); np.fill_diagonal(A, 0.98)
    pi = np.full(K, 1 / K)
    ll_prev = -np.inf
    for _ in range(ITERS):
        B, m = emissions(X, mu, var)
        a, c = forward(B, A, pi)
        b = np.ones((T, K))
        for t in range(T - 2, -1, -1):
            b[t] = (A @ (B[t + 1] * b[t + 1])) / c[t + 1]
        g = a * b
        g /= g.sum(axis=1, keepdims=True)
        xi = np.zeros((K, K))
        for t in range(T - 1):
            x = a[t][:, None] * A * (B[t + 1] * b[t + 1])[None] / c[t + 1]
            xi += x
        pi = g[0]
        A = xi / xi.sum(axis=1, keepdims=True)
        w = g.sum(axis=0)
        mu = (g.T @ X) / w[:, None]
        var = np.maximum((g.T @ X ** 2) / w[:, None] - mu ** 2, 1e-6)
        ll = np.log(c).sum() + m.sum()
        if ll - ll_prev < 1e-4 * abs(ll):
            break
        ll_prev = ll
    return ll, mu, var, A, pi


def filtered(X, mu, var, A, pi) -> np.ndarray:
    B, _ = emissions(X, mu, var)
    raw, _ = forward(B, A, pi)
    out = np.zeros_like(raw); out[0] = raw[0]
    prev, dwell = raw[0].argmax(), 1
    for t in range(1, len(raw)):
        cur = raw[t].argmax()
        dwell = dwell + 1 if cur == prev else 1
        prev = cur
        s = ALPHA * raw[t] + (1 - ALPHA) * out[t - 1]
        s[cur] += DWELL * dwell / (dwell + 10.0)
        out[t] = s / s.sum()
    return out


def run(name: str, close: pd.Series) -> None:
    f = features(close)
    X = ((f - f.mean()) / f.std()).values
    best = max((fit(X, s) for s in range(RESTARTS)), key=lambda r: r[0])
    p = filtered(X, *best[1:])
    out = pd.DataFrame(p, index=f.index, columns=[f"p{k}" for k in range(K)])
    out.insert(0, "hmm_label", name)
    out.index.name = "time"
    path = os.path.join(mnr.REPO, "reports", f"regime_{name}_py.csv")
    out.to_csv(path, float_format="%.5f")
    occ = pd.Series(p.argmax(axis=1)).value_counts(normalize=True).sort_index().round(3).to_dict()
    print(f"  {name}: {len(out)} bars, LL {best[0]:.0f}, state occupancy {occ} -> {path}")


if __name__ == "__main__":
    syms = list(dict.fromkeys(["BTCUSDT"] + mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins")))
    mk = mnr.build_market(syms, os.path.join(mnr.REPO, "candle_cache"))
    print(f"universe: {mk.close.shape[1]} coins, {mk.close.index[0]:%Y-%m-%d} -> {mk.close.index[-1]:%Y-%m-%d}")
    run("btc", mk.close["BTCUSDT"].dropna())
    run("pc1", pc1_index(mk))
