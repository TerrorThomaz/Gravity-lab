"""ONE-SHOT block-B tests, pre-registered in docs/EDGE_DISCOVERY_SCOPE_2026-10.md (2026-10-04).

Trial 1: TREND sleeve. Trial 2: Grid k=2 dips gated by trend. Specs and pass rules live in the
doc's PRE-REGISTRATION section, and this code was committed with it before any block-B read.
Run once.

Run:  python3 research/blockB_test.py
"""

from __future__ import annotations

import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import atr, day_t, fills, mnr, sigma  # noqa: E402

B0 = pd.Timestamp("2025-07-01")
TAKER2 = 2 * mnr.TAKER_COST_PER_SIDE * 100
RUNG = (mnr.MAKER_COST_PER_SIDE + mnr.TAKER_COST_PER_SIDE) * 100
CACHE = os.path.join(mnr.REPO, "candle_cache")


def funding_pct(mk, t, e, j, side):
    """Real settlements in (t, e] per symbol. The floor applies (either side) where data is missing,
    including hours after the symbol's last cached settlement."""
    rate = mk.funding.values
    fcum = np.cumsum(rate, axis=0)
    mask = mk.funding_mask.values
    last = np.array([np.where(mask[:, c])[0].max() if mask[:, c].any() else -1 for c in range(rate.shape[1])])
    known = mk.funding_known.reindex(mk.close.columns).fillna(False).values
    cov = last[j]
    e_c, t_c = np.minimum(e, cov), np.minimum(t, cov)
    real = np.where(known[j] & (cov >= 0), side * (fcum[np.maximum(e_c, 0), j] - fcum[np.maximum(t_c, 0), j]) * 100, 0.0)
    uncovered = np.where(known[j] & (cov >= 0), np.maximum(0, e - np.maximum(t, cov)), e - t)
    return real + mnr.FUNDING_FLOOR_PCT_PER_8H * uncovered / 8


def run(universe: str) -> dict:
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins"), CACHE)
    idx = mk.close.index
    O, H, L, C = mk.open.values, mk.high.values, mk.low.values, mk.close.values
    T, N = C.shape
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
        lr = np.diff(lc, axis=0, prepend=np.nan)
    mcum = np.nancumsum(np.nan_to_num(np.nanmean(lr, axis=1)))
    sig = np.sign(mcum - np.concatenate([np.full(720, np.nan), mcum[:-720]]))
    b0 = int(np.searchsorted(idx, B0))
    blk = lambda tt: ((idx[tt] - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values
    res = {"universe": universe, "end": idx[-1]}

    # ── Trial 1 ──
    days = np.where((idx.hour == 0) & np.isfinite(sig) & (np.arange(T) >= b0))[0]
    tt, jj = (a.ravel() for a in np.meshgrid(days, np.arange(N), indexing="ij"))
    e = tt + 168                                                   # close of the bar 168h after entry bar tt+1
    ok = (e < T) & (tt + 1 < T)
    tt, jj, e = tt[ok], jj[ok], e[ok]
    ok = np.isfinite(C[e, jj]) & np.isfinite(O[tt + 1, jj])
    tt, jj, e = tt[ok], jj[ok], e[ok]
    raw = 100 * (lc[e, jj] - np.log(O[tt + 1, jj]))
    s = sig[tt]
    net = {nm: sd * raw - TAKER2 - funding_pct(mk, tt + 1, e, jj, sd)
           for nm, sd in (("trend", s), ("long", np.ones_like(s)), ("short", -np.ones_like(s)))}
    res["t1"] = {nm: day_t(v, blk(tt))[:2] for nm, v in net.items()}
    res["t1_n"], res["t1_long_share"] = len(tt), float((s > 0).mean())
    res["t1_month"] = pd.Series(net["trend"]).groupby(idx[tt].to_period("M")).mean()

    # ── Trial 2 ──
    fl = fills(O, H, L, C, atr(H, L, C), sigma(C))
    m = (fl["k"] == 2) & (fl["t"] >= b0) & np.isfinite(sig[fl["t"]])
    ft, fj, fs, fpx = fl["t"][m], fl["j"][m], fl["side"][m].astype(float), fl["px"][m]
    agree = sig[ft] == fs
    res["t2"] = {}
    for h in (12, 24, 72):
        e = ft + 1 + h - 1                                         # close h bars after the fill bar starts
        ok = e < T
        ee = np.where(ok, e, 0)
        v = np.where(ok, fs * 100 * (lc[ee, fj] - np.log(fpx)) - RUNG - funding_pct(mk, ft + 1, ee, fj, fs), np.nan)
        res["t2"][h] = {"with": day_t(v[agree], blk(ft[agree]))[:2], "against": day_t(v[~agree], blk(ft[~agree]))[:2],
                        "n": (int((agree & ok).sum()), int((~agree & ok).sum()))}
    return res


def main() -> int:
    out = [run("backtest"), run("oos")]
    print(f"BLOCK B one-shot ({B0:%Y-%m-%d} → data end {out[0]['end']:%Y-%m-%d %H:%M}), pre-registered 2026-10-04\n")
    p1 = p2 = True
    for r in out:
        t1 = r["t1"]
        ok1 = t1["trend"][0] > 0 and t1["trend"][0] > max(t1["long"][0], t1["short"][0])
        p1 &= ok1
        print(f"[{r['universe']}] TRIAL 1 TREND, 7d hold: n {r['t1_n']} entries ({r['t1_long_share']:.0%} long)")
        print(f"   trend {t1['trend'][0]:+.3f}% [t {t1['trend'][1]:+.1f}]   always-long {t1['long'][0]:+.3f}   "
              f"always-short {t1['short'][0]:+.3f}   → {'PASS' if ok1 else 'FAIL'}")
        print("   by entry month: " + "  ".join(f"{k} {v:+.2f}" for k, v in r["t1_month"].items()))
        w, a = r["t2"][24]["with"], r["t2"][24]["against"]
        ok2 = w[0] > 0 and w[0] - a[0] > 0
        p2 &= ok2
        print(f"[{r['universe']}] TRIAL 2 k2 dips with trend, 24h (primary): with {w[0]:+.3f}% [t {w[1]:+.1f}]  "
              f"against {a[0]:+.3f}% [t {a[1]:+.1f}]  diff {w[0] - a[0]:+.3f}  (n {r['t2'][24]['n']}) → {'PASS' if ok2 else 'FAIL'}")
        for h in (12, 72):
            w, a = r["t2"][h]["with"], r["t2"][h]["against"]
            print(f"      {h}h (reported): with {w[0]:+.3f} [t {w[1]:+.1f}]  against {a[0]:+.3f} [t {a[1]:+.1f}]")
        print()
    print(f"VERDICT  Trial 1 TREND: {'PASS' if p1 else 'FAIL'}   Trial 2 trend-gated dips: {'PASS' if p2 else 'FAIL'}   (both universes required)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
