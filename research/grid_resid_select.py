"""Covariance residual as a FREE selector inside Grid: do Grid sessions armed on coins whose
idiosyncratic move is most negative earn more?

Pre-registration: docs/GRID_RESID_SELECT_2026-10.md (frozen at the commit that adds this file).

Standalone residual-reversion books can't pay their costs (resid book retired; j4big hedge legs eat
the timing). Grid trades anyway, so if the residual carries reversion, selecting Grid's coins by it
costs nothing extra. Residual = hourly return minus its projection on $ + top-3 PCs of the trailing
30d covariance, refit daily on data before the day (anomaly_fade_bounce.residuals). Signal = the
4h cumulative residual z (zscore24, n=4), read at the last hourly bar that CLOSED by arming time
(entry_time − 1h, conservative under either timestamp convention).

Run:  python3 research/grid_resid_select.py --universe oos --trades reports/edgetest_raw_trades_oos.csv
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from anomaly_fade_bounce import mnr, residuals, zscore24  # noqa: E402
from grid_paths import day_t  # noqa: E402

RANDOM, SHIFTS, SEED = 2000, 20, 20261006


def signal(mk, trades: pd.DataFrame, z: np.ndarray, shift_days: np.ndarray | None = None) -> np.ndarray:
    idx = mk.close.index
    col = {s: i for i, s in enumerate(mk.close.columns)}
    at = pd.to_datetime(trades.entry_time).dt.floor("h") - pd.Timedelta(hours=1)
    if shift_days is not None:
        at = at + pd.to_timedelta(shift_days, unit="D")
    r = idx.get_indexer(at)
    c = trades.symbol.map(col).fillna(-1).astype(int).values
    ok = (r >= 0) & (c >= 0)
    out = np.full(len(trades), np.nan)
    out[ok] = z[r[ok], c[ok]]
    return out


def lift(ret: np.ndarray, sig: np.ndarray, blk: np.ndarray, prefer_low: bool):
    """Mean of the preferred tercile minus the mean of all trades with a signal, week-clustered t."""
    ok = np.isfinite(sig)
    lo, hi = np.nanquantile(sig[ok], [1 / 3, 2 / 3])
    sel = ok & ((sig <= lo) if prefer_low else (sig >= hi))
    base = ret[ok].mean()
    m, t, _ = day_t(ret[sel] - base, blk[sel])
    return m, t, sel, ok, (lo, hi)


def run(mk, tr: pd.DataFrame, strategy: str, zname: str, z: np.ndarray, prefer_low: bool, rng, gate: bool):
    g = tr[tr.strategy == strategy].reset_index(drop=True)
    ret = g.return_pct.values
    et = pd.to_datetime(g.entry_time)
    blk = ((et - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values
    sig = signal(mk, g, z)
    m, t, sel, ok, cuts = lift(ret, sig, blk, prefer_low)
    mid = et[ok].min() + (et[ok].max() - et[ok].min()) / 2
    halves = []
    for part in (et < mid, et >= mid):
        mh, th, *_ = lift(ret[part.values], sig[part.values], blk[part.values], prefer_low)
        halves.append(f"{mh:+.3f} [{th:+.1f}]")
    n = sel.sum()
    pool = np.flatnonzero(ok)
    rnd = np.array([ret[rng.choice(pool, n, replace=False)].mean() for _ in range(RANDOM)]) - ret[ok].mean()
    shifts = []
    for _ in range(SHIFTS):
        sd = rng.integers(1, 31, len(g)) * rng.choice([-1, 1], len(g))
        s2 = signal(mk, g, z, sd)
        shifts.append(lift(ret, s2, blk, prefer_low)[0])
    terc = [ret[ok & (sig <= cuts[0])].mean(), ret[ok & (sig > cuts[0]) & (sig < cuts[1])].mean(), ret[ok & (sig >= cuts[1])].mean()]
    tag = "  [PRIMARY]" if gate else ""
    print(f"   {strategy:<9} {zname:<4} prefer {'low ' if prefer_low else 'high'}  n {ok.sum()}/{len(g)}  "
          f"terciles low/mid/high {terc[0]:+.3f} / {terc[1]:+.3f} / {terc[2]:+.3f}  (all {ret[ok].mean():+.3f})")
    print(f"      lift {m:+.3f}%/trade [t {t:+.2f}]  halves {halves[0]} / {halves[1]}  "
          f"beats random {np.mean(m > rnd):.1%}  beats time-shifts {sum(m > s for s in shifts)}/{SHIFTS}{tag}")
    return dict(m=m, t=t, halves=halves, rnd=np.mean(m > rnd), shifts=sum(m > s for s in shifts))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["backtest", "oos"], required=True)
    ap.add_argument("--trades", required=True)
    a = ap.parse_args()
    rng = np.random.default_rng(SEED)
    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, os.path.join(mnr.REPO, "candle_cache"))
    tr = pd.read_csv(a.trades)
    print(f"{a.universe}: {mk.close.shape[1]} coins; trades {tr.strategy.value_counts().to_dict()}")
    res = residuals(mk)
    _, z4 = zscore24(res, n=4)
    _, z24 = zscore24(res, n=24)
    run(mk, tr, "Grid", "z4", z4, True, rng, gate=True)
    run(mk, tr, "Grid", "z24", z24, True, rng, gate=False)
    if (tr.strategy == "GridShort").any():
        run(mk, tr, "GridShort", "z4", z4, False, rng, gate=False)
        run(mk, tr, "GridShort", "z24", z24, False, rng, gate=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
