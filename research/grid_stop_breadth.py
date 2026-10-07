"""Gap A — Grid stop-out breadth as a pause (A1) or a fast basket short (A2).
Pre-registered in docs/PREREG_GAP_A_STOP_BREADTH_2026-10.md (committed before this ran).

Run:  python3 research/grid_stop_breadth.py --universe oos|backtest
"""
from __future__ import annotations

import argparse
import os

import numpy as np
import pandas as pd

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
H1 = pd.Timedelta(hours=1)
COST = 0.21          # % round trip, taker: fee 0.11 + 2 x 5 bp slippage
W, HOLD = pd.Timedelta(hours=6), pd.Timedelta(hours=24)
SHIFTS = [s * sign for s in range(7, 71, 7) for sign in (1, -1)]   # 20 shifts, days
RNG = np.random.default_rng(7)


def episodes(stops: pd.DataFrame, k: int) -> pd.DatetimeIndex:
    """Trigger times: >= k distinct coins with a known stop in the trailing W; episodes >= 24h apart."""
    kn, sy = stops.known.values, stops.symbol.values
    out, last = [], None
    for i in range(len(kn)):
        if last is not None and kn[i] < last + HOLD.to_timedelta64():
            continue
        win = sy[(kn > kn[i] - W.to_timedelta64()) & (kn <= kn[i])]
        if len(set(win)) >= k:
            out.append(kn[i]); last = kn[i]
    return pd.DatetimeIndex(out)


def paused_mask(dec: np.ndarray, ev: pd.DatetimeIndex, hold=HOLD) -> np.ndarray:
    m = np.zeros(len(dec), bool)
    for e in ev.values:
        m |= (dec >= e) & (dec < e + hold.to_timedelta64())
    return m


def load_basket(symbols) -> pd.DataFrame:
    """15m OPEN prices, one column per coin (the entry/exit price for a decision known at bar start)."""
    cols = {}
    for s in symbols:
        p = os.path.join(REPO, "candle_cache", f"{s}_15m.csv")
        if not os.path.exists(p):
            continue
        d = pd.read_csv(p, header=None, usecols=[0, 1], names=["t", "o"])
        cols[s] = pd.Series(d.o.values, index=pd.to_datetime(d.t, unit="ms"))
    return pd.DataFrame(cols).sort_index()


def short_net(px: pd.DataFrame, ev: pd.DatetimeIndex, hold=HOLD) -> np.ndarray:
    """Net % of an equal-weight basket short opened at the first 15m open >= e, closed 24h later."""
    idx = px.index
    i0 = idx.searchsorted(ev.values); i1 = idx.searchsorted((ev + hold).values)
    ok = i1 < len(idx)
    a, b = px.values[i0[ok]], px.values[i1[ok]]
    r = np.nanmean(np.where((a > 0) & (b > 0), b / a - 1, np.nan), axis=1) * 100
    out = np.full(len(ev), np.nan); out[ok] = -r - COST
    return out


def tstat(x):
    x = x[~np.isnan(x)]
    return x.mean() / x.std(ddof=1) * np.sqrt(len(x)) if len(x) > 2 else np.nan


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", default="oos")
    a = ap.parse_args()
    t = pd.read_csv(os.path.join(REPO, "reports", f"edgetest_raw_trades_{a.universe}.csv"), parse_dates=["entry_time", "exit_time"])
    g = t[t.strategy == "Grid"].copy()
    g["dec"] = g.entry_time + H1
    stops = g[g.stop == 1].assign(known=lambda x: x.exit_time + H1).sort_values("known")
    lo, hi = g.dec.min(), g.dec.max()
    mid = lo + (hi - lo) / 2
    dec, ret = g.dec.values, g.return_pct.values
    px = load_basket(sorted(t.symbol.unique()))
    print(f"=== Gap A, {a.universe}: {len(g)} Grid sessions, {len(stops)} stops, basket {px.shape[1]} coins, {lo:%Y-%m-%d} → {hi:%Y-%m-%d}")

    def rand_eps(n):
        span = (hi - lo).total_seconds()
        return pd.DatetimeIndex(sorted(lo + pd.to_timedelta(RNG.uniform(0, span, n), unit="s"))).floor("15min")

    for k in (2, 3):
        ev = episodes(stops, k)
        tag = "PRIMARY" if k == 2 else "secondary (reported, not judged)"
        print(f"\n── K = {k}  [{tag}]  episodes {len(ev)}")
        # A1 pause
        m = paused_mask(dec, ev)
        pm = ret[m].mean()
        rnd = np.array([ret[paused_mask(dec, rand_eps(len(ev)))].mean() for _ in range(200)])
        sh = np.array([ret[paused_mask(dec, ev + pd.Timedelta(days=s))].mean() for s in SHIFTS])
        halves = [ret[m & (dec < np.datetime64(mid))].sum(), ret[m & (dec >= np.datetime64(mid))].sum()]
        p_r, n_s = (rnd <= pm).mean(), int((sh > pm).sum())
        ok1 = pm < 0 and p_r <= 0.05 and n_s >= 19 and all(h < 0 for h in halves)
        print(f"  A1 pause : paused {m.sum()} sessions ({m.mean():.1%}), mean {pm:+.3f}% vs kept {ret[~m].mean():+.3f}% | "
              f"random below {1 - p_r:.0%} | shifts above {n_s}/20 | paused sum by half {halves[0]:+.1f}/{halves[1]:+.1f}  → {'PASS' if ok1 else 'FAIL'}")
        # A2 short
        r = short_net(px, ev); mu, tt = np.nanmean(r), tstat(r)
        rr = np.array([np.nanmean(short_net(px, rand_eps(len(ev)))) for _ in range(200)])
        ss = np.array([np.nanmean(short_net(px, ev + pd.Timedelta(days=s))) for s in SHIFTS])
        evm = ev < mid
        h2 = [np.nanmean(r[evm]), np.nanmean(r[~evm])]
        ok2 = mu > 0 and tt >= 2 and (rr < mu).mean() >= 0.95 and int((ss < mu).sum()) >= 19 and all(h > 0 for h in h2)
        gross = np.nanmean(r + COST)
        print(f"  A2 short : mean net {mu:+.3f}% (gross {gross:+.3f}%), t {tt:.2f}, n {np.sum(~np.isnan(r))} | random below {(rr < mu).mean():.0%} "
              f"(placebo mean {rr.mean():+.3f}) | shifts below {int((ss < mu).sum())}/20 | halves {h2[0]:+.3f}/{h2[1]:+.3f}  → {'PASS' if ok2 else 'FAIL'}")
        # Reported: does it pay when Grid loses? Grid P&L exiting inside each window vs the short.
        gx = g.exit_time.values
        gw = np.array([ret[(gx >= e) & (gx < e + HOLD.to_timedelta64())].sum() for e in ev.values])
        okr = ~np.isnan(r)
        if okr.sum() > 3:
            print(f"  reported : Grid sum exiting in window mean {gw.mean():+.2f}%; corr(short, Grid-in-window) {np.corrcoef(r[okr], gw[okr])[0, 1]:+.2f}")
        for hh in (12, 48):
            hd = pd.Timedelta(hours=hh)
            mm = paused_mask(dec, ev, hd)
            print(f"  sensitivity H={hh}h: paused mean {ret[mm].mean():+.3f}% (n {mm.sum()}), short net {np.nanmean(short_net(px, ev, hd)):+.3f}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
