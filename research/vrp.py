"""Volatility risk premium, stage 1: DVOL (implied) vs forward realised vol, BTC and ETH.

Pre-registration: docs/VRP_2026-10.md (frozen at the commit that adds this file).

Run:  python3 research/vrp.py
"""

from __future__ import annotations

import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402

EXT = os.path.join(os.path.dirname(os.path.realpath(os.path.join(mnr.REPO, "candle_cache"))), "data", "external")
CACHE = os.path.join(mnr.REPO, "candle_cache")
N, HURDLE, LAGS = 720, 2.5, 30


def nw_t(x: np.ndarray, lags: int = LAGS) -> float:
    m = x.mean()
    e = x - m
    n = len(x)
    s = (e @ e) / n
    for k in range(1, lags + 1):
        s += 2 * (1 - k / (lags + 1)) * (e[k:] @ e[:-k]) / n
    return m / math.sqrt(s / n)


def series(cur: str):
    iv = pd.read_csv(os.path.join(EXT, f"deribit_dvol_{cur}_1h.csv"))
    iv = pd.Series(iv.open.values, index=pd.to_datetime(iv.ts, unit="ms"))       # value AT the hour
    h = mnr.load_h1(f"{cur}USDT", CACHE)
    c = h["c"] if "c" in h else h["close"]
    r = np.log(c).diff()
    r = r[~r.index.duplicated()].asfreq("1h")
    sq = (r ** 2).fillna(0)
    cnt = r.notna().astype(int)
    fwd_ss = sq[::-1].rolling(N, min_periods=N).sum()[::-1].shift(-1)          # next 720 hourly returns
    fwd_n = cnt[::-1].rolling(N, min_periods=N).sum()[::-1].shift(-1)
    rv_fwd = np.sqrt(fwd_ss / fwd_n * 8760) * 100
    trail = np.sqrt(sq.rolling(N).sum() / cnt.rolling(N).sum() * 8760) * 100
    days = iv.index[(iv.index.hour == 0)]
    df = pd.DataFrame({"iv": iv.reindex(days), "rv": rv_fwd.reindex(days), "rv_trail": trail.reindex(days),
                       "px": c.reindex(days)}).dropna(subset=["iv", "rv"])
    df = df[fwd_n.reindex(df.index) >= 0.95 * N]
    return df, c


def grid_blocks(blocks: pd.DatetimeIndex) -> pd.Series:
    tr = pd.read_csv(os.path.join(mnr.REPO, "reports", "edgetest_raw_trades_oos.csv"))
    g = tr[tr.strategy == "Grid"]
    pnl = (g.return_pct * 0.05).groupby(pd.to_datetime(g.exit_time).dt.floor("D").values).sum()
    out = {}
    for b0, b1 in zip(blocks[:-1], blocks[1:]):
        out[b0] = pnl[(pnl.index >= b0) & (pnl.index < b1)].sum()
    return pd.Series(out)


def report(cur: str) -> dict:
    df, c = series(cur)
    v = (df.iv - df.rv).values
    mid = len(df) // 2
    print(f"\n── {cur}: {df.index[0]:%Y-%m-%d} → {df.index[-1]:%Y-%m-%d}, {len(df)} daily samples ──")
    print(f"   mean IV {df.iv.mean():.1f}  mean fwd RV {df.rv.mean():.1f}  →  VRP {v.mean():+.2f} vol pts "
          f"[NW t {nw_t(v):+.2f}]  positive {np.mean(v > 0):.0%} of days  "
          f"halves {v[:mid].mean():+.2f} / {v[mid:].mean():+.2f}  median {np.median(v):+.2f}")
    blk = df.iloc[::30]
    pnl = (blk.iv ** 2 - blk.rv ** 2) / (2 * blk.iv)
    net = pnl - HURDLE
    sr = lambda x: x.mean() / x.std() * math.sqrt(365 / 30)
    print(f"   short var-swap proxy, {len(blk)} non-overlapping 30d blocks (vol pts per block): mean {pnl.mean():+.2f}, "
          f"after {HURDLE} hurdle {net.mean():+.2f};  Sharpe gross {sr(pnl):+.2f}, net {sr(net):+.2f};  skew {pnl.skew():+.2f}; "
          f"positive {np.mean(net > 0):.0%}")
    h = len(net) // 2
    print(f"   net halves: {net.iloc[:h].mean():+.2f} / {net.iloc[h:].mean():+.2f};  by year: "
          + " ".join(f"{y}:{x:+.1f}" for y, x in net.groupby(net.index.year).mean().items()))
    worst = net.nsmallest(5)
    print("   worst blocks: " + "  ".join(f"{d:%Y-%m-%d} {x:+.1f} (IV {blk.iv[d]:.0f} → RV {blk.rv[d]:.0f})" for d, x in worst.items()))
    bret = (blk.px.shift(-1) / blk.px - 1) * 100
    gb = grid_blocks(blk.index)
    j = pd.concat([net.rename("vrp"), bret.rename("btc"), gb.rename("grid")], axis=1).dropna()
    print(f"   correlation of block P&L with {cur} return {j.vrp.corr(j.btc):+.2f}, with Grid (OOS) {j.vrp.corr(j.grid):+.2f};  "
          f"on the 20% worst {cur} blocks: VRP net {j.vrp[j.btc <= j.btc.quantile(0.2)].mean():+.2f} vs all {j.vrp.mean():+.2f}")
    gap = df.iv - df.rv_trail
    hi = df[gap > gap.median()]
    print(f"   timing (report only): VRP when IV − trailing RV above median {(hi.iv - hi.rv).mean():+.2f}, "
          f"below {(df[gap <= gap.median()].iv - df[gap <= gap.median()].rv).mean():+.2f}")
    return dict(vrp=v.mean(), t=nw_t(v), halves=(v[:mid].mean(), v[mid:].mean()), sr_net=sr(net))


def main() -> int:
    res = {cur: report(cur) for cur in ("BTC", "ETH")}
    ok = all(r["vrp"] > HURDLE and r["t"] >= 2 and min(r["halves"]) > 0 and r["sr_net"] > 0.5 for r in res.values())
    print(f"\nSTAGE 1 {'PASS → stage 2 (option quotes)' if ok else 'FAIL'}: " + "; ".join(
        f"{k} VRP {r['vrp']:+.2f} t {r['t']:+.2f} halves {r['halves'][0]:+.2f}/{r['halves'][1]:+.2f} net Sharpe {r['sr_net']:+.2f}"
        for k, r in res.items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
