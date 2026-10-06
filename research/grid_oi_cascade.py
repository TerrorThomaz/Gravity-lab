"""Is Grid's edge forced selling? Open-interest drops as the liquidation-cascade signature.

Pre-registration: docs/GRID_OI_CASCADE_2026-10.md (frozen at the commit that adds this file).

PRIMARY  Grid's real trades (edgetest logs), split into terciles by the 4h open-interest change at
         arming (z vs the trailing 720h, strictly before; read at the last hour ≤ arming − 1h):
         prefer the LOW tercile (OI fell most = positions force-closed). Same lift / controls as
         grid_resid_select (2,000 random same-size selections, 20 time-shifts, halves).
SECONDARY event study on all coins, hourly: a price dip (1h return z ≤ −2) WITH an OI drop (1h OI
         change z ≤ −2) vs a dip WITHOUT (OI z > 0): forward return from the next hour's open over
         2 / 4 / 8 h, excess over the coin's all-hours mean, day-clustered t.

Run:  python3 research/grid_oi_cascade.py --universe oos|backtest --trades reports/edgetest_raw_trades_<u>.csv
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from anomaly_fade_bounce import mnr  # noqa: E402
import grid_resid_select as grs  # noqa: E402


def oi_matrix(mk) -> pd.DataFrame:
    cols = {}
    for s in mk.close.columns:
        p = os.path.join(mnr.REPO, "candle_cache", f"{s}_oi1h.csv")
        if os.path.exists(p):
            d = pd.read_csv(p, header=None, names=["ms", "oi"])
            cols[s] = pd.Series(d.oi.values, index=pd.to_datetime(d.ms, unit="ms")).groupby(level=0).last()
    return pd.DataFrame(cols).reindex(index=mk.close.index, columns=mk.close.columns)


def zchange(X: pd.DataFrame, n: int) -> np.ndarray:
    with np.errstate(invalid="ignore", divide="ignore"):
        d = np.log(X).diff(n)
    sd = d.rolling(720, min_periods=360).std().shift(n)
    return (d / sd).values


def tday(x: np.ndarray, day: np.ndarray) -> float:
    g = pd.Series(x - x.mean()).groupby(day).sum()
    return x.mean() / (math.sqrt((g ** 2).sum()) / len(x)) if len(x) > 30 else np.nan


def events(mk, OI) -> None:
    C = mk.close
    O = mk.open
    pz = zchange(C, 1)
    oz = zchange(OI, 1)
    lc = np.log(C.values)
    lo = np.log(O.values)
    T = len(C)
    print("   SECONDARY event study (all coins, hourly): dip = price z ≤ −2; forward from next open; excess over the coin's mean")
    for k in (2, 4, 8):
        fwd = np.full(C.shape, np.nan)
        fwd[: T - k] = (lc[k: T] - lo[1: T - k + 1]) * 100                    # open t+1 → close t+k
        base = np.nanmean(fwd, axis=0)
        ex = fwd - base
        day = np.repeat(C.index.floor("D").values[:, None], C.shape[1], 1)
        out = []
        for name, m in (("dip + OI drop", (pz <= -2) & (oz <= -2)), ("dip, OI not falling", (pz <= -2) & (oz > 0))):
            m &= np.isfinite(ex)
            x, d = ex[m], day[m]
            et = C.index[np.nonzero(m)[0]]
            h = len(x) // 2
            order = np.argsort(et)
            xs = x[order]
            out.append(f"{name}: {x.mean():+.3f}% [t {tday(x, d.astype('datetime64[D]')):+.1f}] halves {xs[:h].mean():+.3f}/{xs[h:].mean():+.3f} (n {m.sum()})")
        print(f"      {k}h  " + "   |   ".join(out))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["backtest", "oos"], required=True)
    ap.add_argument("--trades", required=True)
    a = ap.parse_args()
    rng = np.random.default_rng(20261007)
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"),
                          os.path.join(mnr.REPO, "candle_cache"))
    OI = oi_matrix(mk)
    tr = pd.read_csv(a.trades)
    print(f"{a.universe}: {mk.close.shape[1]} coins, OI coverage {np.isfinite(OI.values).mean():.0%}; trades {tr.strategy.value_counts().to_dict()}")
    z4 = zchange(OI, 4)
    grs.run(mk, tr, "Grid", "oi4", z4, True, rng, gate=True)
    grs.run(mk, tr, "GridShort", "oi4", z4, True, rng, gate=False)
    events(mk, OI)
    return 0


if __name__ == "__main__":
    sys.exit(main())
