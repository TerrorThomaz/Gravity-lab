"""Forecast engine: an out-of-sample forecast of every coin's next-H return, every hour, full history.

Pre-registration: docs/FORECAST_USES_2026-10.md (frozen at the commit that adds this file).

One LightGBM model (gbm_np's spec) refit quarterly from 12 months in, trained on BacktestCoins rows
whose label ended before the quarter (purge), sampled every H hours so labels do not overlap; it then
forecasts every hour of that quarter for BOTH universes (OosCoins never trained on). Every forecast is
made by a model that has seen nothing at or after its time — so the whole series, including the
2025-07 → 2026 stretch, is walk-forward. The forecast is the shared INPUT to the pre-registered uses in
forecast_uses.py; it is not itself a trial.

Features: discover15's price/market set + the multi-length indicator block, MINUS the families with ~0
importance (adx14, vz, fund, rpos96, vwap96), MINUS rung depth (k, a candidate property) and calendar
(hour, dow — not a strategy rule, per the user). Target: the coin's next-H log return (raw, %),
winsorised at 0.5/99.5% of the training set.

Output: <repo>/data/forecast/{universe}_h{H}.npz  (times, symbols, F[time × coin], float32, NaN = none)

Run:  ~/Gravity-lab/.venv-research/bin/python research/forecast_engine.py [--hours 4]
"""

from __future__ import annotations

import argparse
import gc
import os
import sys
import time

import numpy as np
import pandas as pd

import discover15 as d15
import indicators15 as ind
import wf_parallel as wfp
from grid_paths import atr, mnr, sigma

DROP = {"adx14", "vz", "fund", "rpos96", "vwap96", "k", "hour", "dow"}
FEATS = [n for n in ind.BASE + ind.IND if n not in DROP]
END = pd.Timestamp("2026-10-06")
OUT = os.path.join(os.path.dirname(os.path.realpath(os.path.join(mnr.REPO, "candle_cache"))), "data", "forecast")
TRAIN_CAP = 1_500_000


def hourly_matrix(universe: str, H: int):
    """Features at every hour (the close of each hour's last 15m bar) for every coin, plus the next-H label."""
    t0 = time.time()
    d15.SEAL = END                                   # ponytail: reuse discover15's loader over the full history
    syms = mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins")
    idx, names, M, fund = d15.load15(syms)
    C = M["c"]
    S, A = sigma(C), atr(M["h"], M["l"], C)
    mlr, mcum = d15.market_path(C)
    rows = np.flatnonzero(idx.minute == 45)          # bar 15:45 closes at 16:00 = the hour's close
    T, N = len(rows), len(names)
    col = {n: i for i, n in enumerate(FEATS)}
    X = np.full((T * N, len(FEATS)), np.nan, np.float32)
    for name, arr in ind.full_iter(idx, M, fund, S, A, mlr, mcum):
        if name in col:
            X[:, col[name]] = (arr[rows] if arr.ndim == 2 else np.repeat(arr[rows][:, None], N, 1)).reshape(-1)
        del arr
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
    k = 4 * H
    y = np.full((T, N), np.nan, np.float32)
    ok = rows + k < len(idx)
    y[ok] = (lc[rows[ok] + k] - lc[rows[ok]]) * 100
    times = idx[rows] + pd.Timedelta(minutes=15)     # forecast time = the hour's close
    print(f"   {universe}: {N} coins × {T:,} hours, {len(FEATS)} features [{time.time() - t0:.0f}s]", flush=True)
    del M, S, A, fund
    gc.collect()
    return times, names, X, y.reshape(-1)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--hours", type=int, default=4)
    a = ap.parse_args()
    H = a.hours
    assert wfp.ENGINE == "lgbm", "run under ~/Gravity-lab/.venv-research (LightGBM)"
    os.makedirs(OUT, exist_ok=True)
    tD, nD, XD, yD = hourly_matrix("backtest", H)
    tE, nE, XE, _ = hourly_matrix("oos", H)
    ND, NE = len(nD), len(nE)
    timeD = np.repeat(tD.values, ND)
    FD = np.full(len(XD), np.nan, np.float32)
    FE = np.full(len(XE), np.nan, np.float32)
    qs = pd.date_range((tD[0] + pd.DateOffset(months=12)).to_period("Q").start_time, END + pd.offsets.QuarterBegin(), freq="QS")
    rng = np.random.default_rng(20261006)
    z1 = FEATS.index("z1")
    hour_i = np.repeat(np.arange(len(tD)), ND)
    for q0, q1 in zip(qs[:-1], qs[1:]):
        tr = np.flatnonzero((timeD + pd.Timedelta(hours=H) <= q0) & (hour_i % H == 0) & np.isfinite(yD))
        if len(tr) > TRAIN_CAP:
            tr = np.sort(rng.choice(tr, TRAIN_CAP, replace=False))
        yt = yD[tr]
        lo, hi = np.quantile(yt, [0.005, 0.995])
        _, m = wfp._fit_model(XD[tr], np.clip(yt, lo, hi), int(q0.value % 1e6))
        for t_, X_, F_, N_ in ((tD, XD, FD, ND), (tE, XE, FE, NE)):
            h = np.flatnonzero((t_ >= q0) & (t_ < q1))
            if len(h):
                r = (h[:, None] * N_ + np.arange(N_)[None]).reshape(-1)
                F_[r] = np.where(np.isfinite(X_[r, z1]), m.predict(X_[r]), np.nan)   # no bar → no forecast
        print(f"      quarter {q0:%Y-%m}: trained on {len(tr):,} rows", flush=True)
    for u, t_, n_, F_ in (("backtest", tD, nD, FD), ("oos", tE, nE, FE)):
        Fm = F_.reshape(len(t_), len(n_))
        path = os.path.join(OUT, f"{u}_h{H}.npz")
        np.savez_compressed(path, times=t_.values.astype("datetime64[ns]"), symbols=np.array(n_), F=Fm)
        print(f"   saved {path}: {np.isfinite(Fm).mean():.0%} of hour × coin cells forecast", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
