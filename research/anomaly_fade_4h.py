"""The 4-hour version of anomaly_fade_bounce.py, on 15m bars.

The user's refinement (2026-10-01): see the large movement, but trade the fading sentiment over
roughly 4 hours.

Fixed before the first run (4 trials, ga_trials.json: anomaly_fade_4h):
- 15m bars from 2021-01-01. Residual = 15m return minus its projection on the dollar direction and
  the top-3 PCs of the trailing 30d (2880-bar) covariance, refit daily on data before the day.
- Definitions (16 bars = 4h; one event per coin until it resolves):
  j4     : 4h residual z >= 3. Measured from the next open, so a NEGATIVE forward residual is a
           short's profit.
  j4big  : j4, and the 24h residual z >= 3 as well (the jump is part of a large move).
  j4fade : j4, then half of the 4h residual gain is given back within 8h. Measured from the next
           open; POSITIVE = bounce.
  raw4   : 4h RAW return z >= 3 (no covariance), measured like j4.
- Forward windows 1/2/4/8h, residual and raw (raw minus the all-bar mean for that window).
- Entry at the next 15m open, and again one bar later ("+15m"). The trigger bar's close can be a
  spike print, so a reversal that exists only at the next open is microstructure, not sentiment.
- Bar to clear: |4h forward residual| above the round-trip cost (0.21% taker, 0.04% maker), the
  same sign in both halves, holding at +15m, and then on OosCoins.

Run:  python3 research/anomaly_fade_4h.py [--universe oos]
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from types import SimpleNamespace

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from anomaly_fade_bounce import events, mnr, residuals, t_day, zscore24  # noqa: E402

BPH, START = 4, "2021-01-01"
WIN, N4, N24, FADE = 30 * 24 * BPH, 4 * BPH, 24 * BPH, 8 * BPH
FWD_H = (1, 2, 4, 8)


def load_15m(syms: list[str], cache: str) -> SimpleNamespace:
    o, c, v = {}, {}, {}
    for s in syms:
        df = mnr._read_ms_csv(os.path.join(cache, f"{s}_15m.csv"), 6)
        if df is None:
            continue
        df = df[~df.index.duplicated(keep="last")]
        df = df[df.index >= START]
        if len(df) < WIN * 2:
            continue
        o[s], c[s], v[s] = df[1].astype("float32"), df[4].astype("float32"), (df[4] * df[5]).astype("float32")
    close = pd.DataFrame(c).sort_index()
    idx = pd.date_range(close.index.min(), close.index.max(), freq="15min")
    return SimpleNamespace(close=close.reindex(idx).astype("float64"),
                           open=pd.DataFrame(o).reindex(idx)[close.columns].values.astype("float64"),
                           qvol=pd.DataFrame(v).reindex(idx)[close.columns])


def study(mk, label: str) -> pd.DataFrame:
    C, O = mk.close.values, mk.open
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = residuals(mk, WIN)
    _, z4 = zscore24(res, N4, WIN)
    _, z24 = zscore24(res, N24, WIN)
    _, zr4 = zscore24(r, N4, WIN)
    Lres = np.nancumsum(np.nan_to_num(res), axis=0)
    Lraw = np.nancumsum(np.nan_to_num(r), axis=0)
    del r
    n = len(C)
    base = {}
    with np.errstate(invalid="ignore", divide="ignore"):
        for h in FWD_H:
            b = h * BPH
            base[h] = np.nanmean(np.log(C[b:] / O[1:n - b + 1]))
    nz = lambda x: np.nan_to_num(x, nan=-np.inf)
    defs = {
        "j4":     (nz(z4) >= 3, Lres, False),
        "j4big":  ((nz(z4) >= 3) & (nz(z24) >= 3), Lres, False),
        "j4fade": (nz(z4) >= 3, Lres, True),
        "raw4":   (nz(zr4) >= 3, Lraw, False),
    }
    del z4, z24, zr4
    rows = []
    for name, (trig, lev, fade) in defs.items():
        for j in range(C.shape[1]):
            for t, s in events(lev[:, j], trig[:, j], fade, look=N4, fade_h=FADE):
                for delay in (1, 2):                       # next open, and 15 minutes later
                    e = s + delay
                    if e + max(FWD_H) * BPH > n or np.isnan(O[e, j]):
                        continue
                    row = dict(defn=name, delay=delay, sym=mk.close.columns[j],
                               time=mk.close.index[e])
                    for h in FWD_H:
                        b = h * BPH
                        row[f"raw{h}"] = np.log(C[e + b - 1, j] / O[e, j]) - base[h]
                        row[f"res{h}"] = Lres[e + b - 1, j] - Lres[e - 1, j]
                    rows.append(row)
    ev = pd.DataFrame(rows).dropna()
    report(ev, label)
    return ev


def report(ev: pd.DataFrame, label: str) -> None:
    print(f"\n=== {label}: forward returns in % (raw drift-adjusted). j4/j4big/raw4: negative = "
          f"short profit. j4fade: positive = bounce")
    print(f"{'defn':>7s} {'entry':>6s} {'n':>6s} {'days':>5s} "
          + " ".join(f"{'res' + str(h) + 'h':>7s}" for h in FWD_H)
          + " " + " ".join(f"{'raw' + str(h) + 'h':>7s}" for h in FWD_H)
          + f" {'t res4':>7s} {'t raw4':>7s} | {'H1 res4':>7s} {'H2 res4':>7s}")
    for (name, delay), g in ev.groupby(["defn", "delay"], sort=False):
        mid = g.time.quantile(0.5)
        print(f"{name:>7s} {'next' if delay == 1 else '+15m':>6s} {len(g):6d} "
              f"{g.time.dt.floor('D').nunique():5d} "
              + " ".join(f"{100 * g[f'res{h}'].mean():7.3f}" for h in FWD_H) + " "
              + " ".join(f"{100 * g[f'raw{h}'].mean():7.3f}" for h in FWD_H)
              + f" {t_day(g.res4, g.time):7.2f} {t_day(g.raw4, g.time):7.2f} | "
              f"{100 * g[g.time < mid].res4.mean():7.3f} {100 * g[g.time >= mid].res4.mean():7.3f}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--universe", choices=["backtest", "oos"], default="backtest")
    a = ap.parse_args()
    mk = load_15m(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"), a.cache)
    print(f"{a.universe}: {mk.close.shape[1]} coins, {mk.close.index[0]} .. {mk.close.index[-1]}")
    ev = study(mk, a.universe)
    out = os.path.join(mnr.REPO, "reports", f"anomaly_fade_4h_events_{a.universe}.csv")
    ev.to_csv(out, index=False)
    print(f"events -> {out}")
    if a.universe == "backtest":
        path = os.path.join(mnr.REPO, "genotypes", "ga_trials.json")
        d = json.load(open(path))
        d["anomaly_fade_4h"] = d.get("anomaly_fade_4h", 0) + 4
        json.dump(d, open(path, "w"), indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
