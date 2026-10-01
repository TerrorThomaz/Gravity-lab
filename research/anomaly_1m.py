"""The anomaly studies on 1m bars (the last 13 months, the only 1m history in candle_cache/).

Design, fixed before the first run (5 trials, ga_trials.json: anomaly_1m):
- Factor basis: the dollar direction + top-3 PCs of the trailing 30d of 15m returns, built from the
  same 1m closes and refit daily on data before the day. It is applied to every 1m return. 1m
  covariance is not used: asynchronous prints shrink 1m correlations toward 0 (the Epps effect) and
  bid-ask noise dominates 1m variance.
- z scale: a window move divided by the trailing 30d std of 15m residual steps * sqrt(window / 15m).
  That is the same scale as the 15m studies, so the thresholds mean the same thing. Scaling by 1m
  steps would read bid-ask bounce as volatility.
- Definitions (one event per coin per window):
  j4big      : 4h residual z >= 3 and 24h residual z >= 3. A SHORT: negative = profit
               (anomaly_fade_4h).
  backed24   : 24h market-backed z >= 1, residual peak >= 1.5, residual now <= -1. A LONG:
               positive = profit (the anomaly_backed_dip --loose rule).
  backed4    : the same over a 4h window.
  nobacking4 : |4h backed z| < 0.5, peak >= 1.5, now <= -1.
  dip4       : 4h residual z <= -1.
- Entry at the open 2 bars after the signal bar (1 minute after the next open). Forward windows are
  5m/15m/1h/4h/24h, residual and raw (raw minus the all-bar mean).
- Comparison: the 15m event files restricted to the same months, for j4big and backed24.

Run:  python3 research/anomaly_1m.py [--universe oos]
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from anomaly_fade_bounce import events, mnr, t_day  # noqa: E402

M15, DAY = 15, 1440
WIN15 = 30 * 24 * 4                         # 30d of 15m steps
H4, H24 = 240, 1440
FWD = (5, 15, 60, 240, 1440)
DELAY = 2


def load_1m(syms: list[str], cache: str):
    o, c = {}, {}
    for s in syms:
        df = mnr._read_ms_csv(os.path.join(cache, f"{s}_1m.csv"), 5)
        if df is None:
            continue
        df = df[~df.index.duplicated(keep="last")]
        o[s], c[s] = df[1].astype("float32"), df[4].astype("float32")
    close = pd.DataFrame(c)
    idx = pd.date_range(close.index.min().normalize(), close.index.max(), freq="1min")
    C = close.reindex(idx).values.astype("float32")
    O = pd.DataFrame(o).reindex(idx)[close.columns].values.astype("float32")
    return idx, list(close.columns), C, O


def residuals_1m(idx, C: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan).astype("float32")
    res = np.full_like(r, np.nan)
    C15 = C[::M15]                                      # idx starts at midnight: rows on :00/:15/...
    for d0 in range(0, len(C), DAY):
        q0 = d0 // M15
        if q0 < WIN15 + 1:
            continue
        w = C15[q0 - WIN15:q0]
        cols = np.flatnonzero(((~np.isnan(w)).mean(0) >= 0.95) & ~np.isnan(C[d0 - 1]))
        if len(cols) < 10:
            continue
        p = pd.DataFrame(w[:, cols]).ffill().values.astype("float64")
        with np.errstate(invalid="ignore", divide="ignore"):
            lr = np.nan_to_num(np.diff(np.log(p), axis=0))
        _, vecs = np.linalg.eigh(np.cov(lr.T))
        q, _ = np.linalg.qr(np.column_stack([np.ones(len(cols)), vecs[:, ::-1][:, :3]]))
        R = r[d0:d0 + DAY][:, cols].astype("float64")
        miss = np.isnan(R)
        R0 = np.where(miss, 0.0, R)
        E = R0 - (R0 @ q) @ q.T
        E[miss] = np.nan
        res[d0:d0 + DAY, cols] = E
    return r, res


def zwin(L: np.ndarray, n: int):
    """Window move and in-window peak of cumulative series L (1m), in z units of 15m steps."""
    s15 = pd.Series(L[::M15])
    sd15 = s15.diff().rolling(WIN15, min_periods=WIN15 // 2).std().shift(n // M15)
    sd = np.repeat(sd15.values, M15)[:len(L)] * math.sqrt(n / M15)
    Ls = pd.Series(L)
    move = (L - Ls.shift(n).values) / sd
    peak = (Ls.rolling(n + 1).max().values - Ls.shift(n).values) / sd
    return move, peak


def study(idx, names, C, O, label: str) -> pd.DataFrame:
    r, res = residuals_1m(idx, C)
    n = len(C)
    valid0 = np.flatnonzero(~np.isnan(res).all(1))[0]     # first fitted bar (after the 30d warmup)
    sums, cnts = {h: 0.0 for h in FWD}, {h: 0 for h in FWD}
    rows = []
    for j, sym in enumerate(names):
        c, o = C[:, j].astype("float64"), O[:, j].astype("float64")
        with np.errstate(invalid="ignore", divide="ignore"):
            for h in FWD:
                x = np.log(c[valid0 + h:] / o[valid0 + 1:n - h + 1])
                sums[h] += np.nansum(x)
                cnts[h] += np.isfinite(x).sum()
        e_ = res[:, j].astype("float64")
        f_ = r[:, j].astype("float64") - e_
        Lres, Lfit = np.nancumsum(np.nan_to_num(e_)), np.nancumsum(np.nan_to_num(f_))
        zr4, pr4 = zwin(Lres, H4)
        zr24, pr24 = zwin(Lres, H24)
        zf4, _ = zwin(Lfit, H4)
        zf24, _ = zwin(Lfit, H24)
        nz = lambda x, v=0.0: np.nan_to_num(x, nan=v)
        defs = {
            "j4big":      ((nz(zr4) >= 3) & (nz(zr24) >= 3), H4),
            "backed24":   ((nz(zf24) >= 1) & (nz(pr24) >= 1.5) & (nz(zr24) <= -1), H24),
            "backed4":    ((nz(zf4) >= 1) & (nz(pr4) >= 1.5) & (nz(zr4) <= -1), H4),
            "nobacking4": ((np.abs(nz(zf4, 9)) < 0.5) & (nz(pr4) >= 1.5) & (nz(zr4) <= -1), H4),
            "dip4":       (nz(zr4) <= -1, H4),
        }
        for name, (trig, look) in defs.items():
            trig[:valid0 + H24] = False
            for t, s in events(Lres, trig, False, look=look):
                e = s + DELAY
                if e + max(FWD) > n or np.isnan(o[e]):
                    continue
                row = dict(defn=name, sym=sym, time=idx[e])
                for h in FWD:
                    row[f"raw{h}"] = np.log(c[e + h - 1] / o[e])
                    row[f"res{h}"] = Lres[e + h - 1] - Lres[e - 1]
                rows.append(row)
    ev = pd.DataFrame(rows).dropna()
    for h in FWD:
        ev[f"raw{h}"] -= sums[h] / cnts[h]
    report(ev, label, idx[valid0 + H24])
    return ev


def report(ev: pd.DataFrame, label: str, start) -> None:
    hs = {5: "5m", 15: "15m", 60: "1h", 240: "4h", 1440: "24h"}
    print(f"\n=== {label} 1m, events from {start.date()}: forward %. j4big: negative = short profit; "
          f"others: positive = long profit")
    print(f"{'defn':>10s} {'n':>6s} {'days':>4s} " + " ".join(f"{'res' + hs[h]:>7s}" for h in FWD)
          + " " + " ".join(f"{'raw' + hs[h]:>7s}" for h in FWD)
          + f" {'t res1h':>7s} {'t res4h':>7s} {'t raw4h':>7s} | {'H1 r4h':>6s} {'H2 r4h':>6s}")
    for name, g in ev.groupby("defn", sort=False):
        mid = g.time.quantile(0.5)
        print(f"{name:>10s} {len(g):6d} {g.time.dt.floor('D').nunique():4d} "
              + " ".join(f"{100 * g[f'res{h}'].mean():7.3f}" for h in FWD) + " "
              + " ".join(f"{100 * g[f'raw{h}'].mean():7.3f}" for h in FWD)
              + f" {t_day(g.res60, g.time):7.2f} {t_day(g.res240, g.time):7.2f} "
              f"{t_day(g.raw240, g.time):7.2f} | {100 * g[g.time < mid].res240.mean():6.3f} "
              f"{100 * g[g.time >= mid].res240.mean():6.3f}")


def compare_15m(universe: str, start) -> None:
    """The 15m results for the same months, so resolution is not confused with period."""
    print(f"\n--- the 15m studies, same period (events from {start.date()})")
    for f, defn, extra in ((f"anomaly_fade_4h_events_{universe}.csv", "j4big", "delay == 2"),
                           (f"anomaly_backed_dip_events_{universe}_loose.csv", "backed", None)):
        p = os.path.join(mnr.REPO, "reports", f)
        if not os.path.exists(p):
            print(f"  {f}: missing, run the 15m study first")
            continue
        g = pd.read_csv(p, parse_dates=["time"])
        g = g[(g.defn == defn) & (g.time >= start)]
        if extra:
            g = g.query(extra)
        print(f"  15m {defn:>7s}: n {len(g):5d}  res1h {100 * g.res1.mean():7.3f}  res4h "
              f"{100 * g.res4.mean():7.3f}  res24h {100 * g.res24.mean() if 'res24' in g else float('nan'):7.3f}  "
              f"raw4h {100 * g.raw4.mean():7.3f}  t res4h {t_day(g.res4, g.time):6.2f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--universe", choices=["backtest", "oos"], default="backtest")
    a = ap.parse_args()
    idx, names, C, O = load_1m(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"),
                               a.cache)
    print(f"{a.universe}: {len(names)} coins, {idx[0]} .. {idx[-1]} ({len(idx):,} minutes)")
    ev = study(idx, names, C, O, a.universe)
    out = os.path.join(mnr.REPO, "reports", f"anomaly_1m_events_{a.universe}.csv")
    ev.to_csv(out, index=False)
    compare_15m(a.universe, ev.time.min().normalize())
    print(f"events -> {out}")
    if a.universe == "backtest":
        path = os.path.join(mnr.REPO, "genotypes", "ga_trials.json")
        d = json.load(open(path))
        d["anomaly_1m"] = d.get("anomaly_1m", 0) + 5
        json.dump(d, open(path, "w"), indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
