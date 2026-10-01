"""Market-backed overcorrection dip (the user's hypothesis, 2026-10-01).

"The coin's family moves +5% bull and the coin +7%: 5 of the 7 is backed by the market. The market
corrects, possibly overcorrects, so 7 becomes 3 or 2. There is something left to capture by buying
that dip."

Covariance split, per 15m bar: the coin's return = its MARKET-BACKED part (its projection on the
dollar direction + top-3 PCs of the trailing 30d covariance, i.e. its loading times the family move)
+ its RESIDUAL. Refit daily on data before the day (anomaly_fade_bounce.residuals).

Fixed before the first run (4 trials, ga_trials.json: anomaly_backed_dip). Window 24h; z = move over
the window / (trailing 30d std * sqrt(window)):
  backed    : market-backed 24h z >= 2 (the rally still holds), the residual rose >= +2 z at some
              point in the window (the overshoot), and the residual is now <= -1.5 z (overcorrected
              below fair value).
  nobacking : the same overshoot and overcorrection, with |market-backed z| < 0.5.
  noexcess  : backing and overcorrection, but the residual never reached +2 z (a laggard, no overshoot).
  dip       : residual 24h z <= -1.5, nothing else (baseline).
--loose (added after the first run found 43/28 events, too rare to measure; chosen for sample size,
not outcome, and 4 more trials): backing >= 1, overshoot >= 1.5, overcorrection <= -1.
One event per coin per 24h. Entry 15 minutes after the next open (anomaly_fade_4h's spike guard).
Forward 1/2/4/8/24h. Residual: POSITIVE = recovers toward fair = a hedged long's profit. Raw (drift-
adjusted): an unhedged long's profit.
Bar to clear: residual 4h-24h above 0.21% (taker round trip), the same sign in both halves, backed
beating nobacking and noexcess, then OosCoins.

Run:  python3 research/anomaly_backed_dip.py [--universe oos]
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
from anomaly_fade_4h import BPH, N24, WIN, load_15m  # noqa: E402
from anomaly_fade_bounce import events, mnr, residuals, t_day  # noqa: E402

FWD_H = (1, 2, 4, 8, 24)
DELAY = 2                                   # next open + 15 minutes


def zwin(level: np.ndarray, step: np.ndarray, n: int):
    """Window move of a cumulative series, the trailing std of its steps (strictly before the
    window), and the max excursion within the window, all in z units."""
    sd = pd.DataFrame(step).rolling(WIN, min_periods=WIN // 2).std().shift(n).values * math.sqrt(n)
    L = pd.DataFrame(level)
    move = (L - L.shift(n)).values / sd
    peak = (L.rolling(n + 1).max() - L.shift(n)).values / sd
    return move, peak


def study(mk, label: str, zb: float = 2.0, zp: float = 2.0, zo: float = -1.5) -> pd.DataFrame:
    C, O = mk.close.values, mk.open
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = residuals(mk, WIN)
    fit = r - res                                       # market-backed part; NaN where no fit
    Lres = np.nancumsum(np.nan_to_num(res), axis=0)
    Lfit = np.nancumsum(np.nan_to_num(fit), axis=0)
    zr, pr = zwin(Lres, res, N24)
    zf, _ = zwin(Lfit, fit, N24)
    del fit, r
    n = len(C)
    base = {}
    with np.errstate(invalid="ignore", divide="ignore"):
        for h in FWD_H:
            b = h * BPH
            base[h] = np.nanmean(np.log(C[b:] / O[1:n - b + 1]))
    nz = lambda x, v: np.nan_to_num(x, nan=v)
    over = nz(zr, 0) <= zo
    defs = {
        "backed":    (nz(zf, 0) >= zb) & (nz(pr, 0) >= zp) & over,
        "nobacking": (np.abs(nz(zf, 9)) < 0.5) & (nz(pr, 0) >= zp) & over,
        "noexcess":  (nz(zf, 0) >= zb) & (nz(pr, 9) < zp) & over,
        "dip":       over,
    }
    rows = []
    for name, trig in defs.items():
        for j in range(C.shape[1]):
            for t, s in events(Lres[:, j], trig[:, j], False, look=N24):
                e = s + DELAY
                if e + max(FWD_H) * BPH > n or np.isnan(O[e, j]):
                    continue
                row = dict(defn=name, sym=mk.close.columns[j], time=mk.close.index[e],
                           zfit=zf[t, j], zres=zr[t, j], peak=pr[t, j])
                for h in FWD_H:
                    b = h * BPH
                    row[f"raw{h}"] = np.log(C[e + b - 1, j] / O[e, j]) - base[h]
                    row[f"res{h}"] = Lres[e + b - 1, j] - Lres[e - 1, j]
                rows.append(row)
    ev = pd.DataFrame(rows).dropna()
    print(f"\n=== {label}: forward % from next open + 15m. POSITIVE = recovers (long profit). "
          f"res = hedged, raw = unhedged (drift-adjusted)")
    print(f"{'defn':>9s} {'n':>6s} {'days':>5s} " + " ".join(f"{'res' + str(h):>6s}" for h in FWD_H)
          + " " + " ".join(f"{'raw' + str(h):>6s}" for h in FWD_H)
          + f" {'t res4':>6s} {'t res24':>7s} {'t raw24':>7s} | {'H1 r24':>6s} {'H2 r24':>6s} "
          f"{'H1 w24':>6s} {'H2 w24':>6s}")
    for name, g in ev.groupby("defn", sort=False):
        mid = g.time.quantile(0.5)
        a, b = g[g.time < mid], g[g.time >= mid]
        print(f"{name:>9s} {len(g):6d} {g.time.dt.floor('D').nunique():5d} "
              + " ".join(f"{100 * g[f'res{h}'].mean():6.2f}" for h in FWD_H) + " "
              + " ".join(f"{100 * g[f'raw{h}'].mean():6.2f}" for h in FWD_H)
              + f" {t_day(g.res4, g.time):6.2f} {t_day(g.res24, g.time):7.2f} "
              f"{t_day(g.raw24, g.time):7.2f} | {100 * a.res24.mean():6.2f} {100 * b.res24.mean():6.2f} "
              f"{100 * a.raw24.mean():6.2f} {100 * b.raw24.mean():6.2f}")
    return ev


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--universe", choices=["backtest", "oos"], default="backtest")
    ap.add_argument("--loose", action="store_true",
                    help="second trial set, chosen for sample size only: backing 1, overshoot 1.5, over -1")
    a = ap.parse_args()
    mk = load_15m(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"), a.cache)
    print(f"{a.universe}: {mk.close.shape[1]} coins, {mk.close.index[0]} .. {mk.close.index[-1]}")
    th = (1.0, 1.5, -1.0) if a.loose else (2.0, 2.0, -1.5)
    ev = study(mk, f"{a.universe} thresholds {th}", *th)
    out = os.path.join(mnr.REPO, "reports", f"anomaly_backed_dip_events_{a.universe}{'_loose' if a.loose else ''}.csv")
    ev.to_csv(out, index=False)
    print(f"events -> {out}")
    if a.universe == "backtest":
        path = os.path.join(mnr.REPO, "genotypes", "ga_trials.json")
        d = json.load(open(path))
        d["anomaly_backed_dip"] = d.get("anomaly_backed_dip", 0) + 4
        json.dump(d, open(path, "w"), indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
