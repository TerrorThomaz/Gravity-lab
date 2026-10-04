"""Positive control: can the pipeline see market trend-following at all, and at which horizon?

The open 15m search (discover15.py) found nothing, and its longest horizon was 12h. The user's point:
in a large bull market buying dips should pay, and shorting should pay in bears. If the effect is
real but lives at multi-day horizons, a ≤12h label cannot see it (a +150%/yr trend is ~0.2% per 12h,
about the cost of one trade). This measures the textbook rule at 12h through 14d with the same costs.

Rules (fixed in advance; time-series momentum on the equal-weight market, Moskowitz-Ooi-Pedersen
style, crypto-length lookback):
  signal       sign of the equal-weight market's trailing 30-day log return, at the 00:00 UTC close
  TREND        every coin, every day: side = signal, entry at the next open (taker), hold H
  DIP-IN-TREND a 2-ATR one-bar rung (grid_paths engine, per rung, maker entry) taken only when the
               rung's side agrees with the signal; hold H from the fill
  baselines    the same entries always long and always short (each coin's drift), and the dip rung
               taken regardless of signal
Costs: taker in/out 0.21%; rung maker in, taker out 0.125%. Funding is not charged (a known omission:
a longs-in-bull book pays positive funding; it is reported as an upper bound).
Horizons: 12h, 1d, 3d, 7d, 14d. Statistics: mean net % per trade, week-clustered t (lag-1 NW), and
halves. Block A only.

Run:  python3 research/trend_control.py --universe oos|backtest
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import SEAL, atr, day_t, fills, mnr, sigma  # noqa: E402

HOURS = (12, 24, 72, 168, 336)
TAKER2 = 2 * mnr.TAKER_COST_PER_SIDE * 100
RUNG = (mnr.MAKER_COST_PER_SIDE + mnr.TAKER_COST_PER_SIDE) * 100


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"), a.cache)
    keep = mk.close.index < SEAL
    idx = mk.close.index[keep]
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    T, N = C.shape
    with np.errstate(invalid="ignore", divide="ignore"):
        lr = np.diff(np.log(C), axis=0, prepend=np.nan)
        lc = np.log(C)
    mcum = np.nancumsum(np.nan_to_num(np.nanmean(lr, axis=1)))
    sig30 = np.sign(mcum - np.concatenate([np.full(720, np.nan), mcum[:-720]]))    # known at the close of bar t
    blk = lambda tt: ((idx[tt] - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values
    mid = idx[len(idx) // 2]
    print(f"{a.universe}: {N} coins, block A {idx[0]:%Y-%m-%d} → {idx[-1]:%Y-%m-%d}; market 30d signal long "
          f"{np.nanmean(sig30 > 0):.0%} of hours")

    def fwd(tt, jj, px, h):
        e = tt + 1 + h - 1                                       # entry bar tt+1; close of bar tt+h
        ok = e < T
        out = np.full(len(tt), np.nan)
        out[ok] = 100 * (lc[e[ok], jj[ok]] - np.log(px[ok]))
        return out

    def line(name, x, tt, cost):
        cells = []
        for h, r in x.items():
            v = r - cost
            m, t, _ = day_t(v, blk(tt))
            first = tt < np.searchsorted(idx, mid)
            m1, _, _ = day_t(v[first], blk(tt[first]))
            m2, _, _ = day_t(v[~first], blk(tt[~first]))
            cells.append(f"{m:+7.3f}[{t:+5.1f}] {m1:+6.2f}/{m2:+6.2f}")
        print(f"   {name:<22}" + "  ".join(cells))

    print("   net % per trade [week t]  1st/2nd half, by hold: " + "   ".join(f"{h}h" for h in HOURS))
    # TREND: daily entries at 00:00 closes
    days = np.where((idx.hour == 0) & np.isfinite(sig30))[0]
    days = days[days < T - 1]
    tt, jj = np.meshgrid(days, np.arange(N), indexing="ij")
    tt, jj = tt.ravel(), jj.ravel()
    ok = np.isfinite(C[tt, jj]) & np.isfinite(O[tt + 1, jj])
    tt, jj = tt[ok], jj[ok]
    px = O[tt + 1, jj]
    s = sig30[tt]
    raw = {h: fwd(tt, jj, px, h) for h in HOURS}
    line("TREND (side = signal)", {h: s * r for h, r in raw.items()}, tt, TAKER2)
    line("  always long", raw, tt, TAKER2)
    line("  always short", {h: -r for h, r in raw.items()}, tt, TAKER2)
    line("  long, bull days only", {h: r[s > 0] for h, r in raw.items()}, tt[s > 0], TAKER2)
    line("  short, bear days only", {h: -r[s < 0] for h, r in raw.items()}, tt[s < 0], TAKER2)

    # DIP-IN-TREND: per-rung k=2 fills (grid_paths engine), side must agree with the signal
    fl = fills(O, H, L, C, atr(H, L, C), sigma(C))
    k2 = fl["k"] == 2
    ft, fj, fs, fpx = fl["t"][k2], fl["j"][k2], fl["side"][k2].astype(float), fl["px"][k2]
    agree = sig30[ft] == fs
    rr = {h: fs * fwd(ft, fj, fpx, h) for h in HOURS}                 # from the fill price, entry bar = ft+1
    line("DIP/RIP k2, with trend", {h: r[agree] for h, r in rr.items()}, ft[agree], RUNG)
    line("  k2 against trend", {h: r[~agree] for h, r in rr.items()}, ft[~agree], RUNG)
    line("  k2 all", rr, ft, RUNG)
    for side, nm in ((1, "long dips in bull"), (-1, "short rips in bear")):
        m = agree & (fs == side)
        line(f"  {nm}", {h: r[m] for h, r in rr.items()}, ft[m], RUNG)
    print("   Trials: TREND + DIP-IN-TREND, fixed lookback 30d; funding not charged (upper bound for longs in bull).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
