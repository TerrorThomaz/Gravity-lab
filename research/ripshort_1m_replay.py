"""Replay RipShort's STOP exits on 1m bars — is the 15m verdict right when the move is faster?

A 15m high/low is exactly the max/min of its 1m bars, so "was the stop / target touched" cannot
change on 1m. Two things can:
  1. ORDER: the 15m simulator books a STOP whenever stop and target sit in the same exit bar (the
     pessimistic rule). On 1m the target may have come first — then the trade actually won.
  2. GAP: a stop is booked AT the stop price plus a modelled gap charge (StopGapPct). On 1m the
     minute that crossed it may have OPENED beyond the stop — the real fill is that open.
Rules (fixed before the run): replay the exit 15m bar minute by minute; target-only minute first →
target exit (maker-free non-stop cost); stop minute first, or a minute touching BOTH → stop, filled
at max(stop, that minute's open) with the non-stop cost (the measured gap replaces the modelled
one). Target/trail/time exits are unchanged (target exits had no stop in their bar; trail/time are
decided on 15m closes by design). Only trades whose exit bar lies inside the 1m data are replayed.

Writes <out> with ret_15m and ret_1m per trade (1m-window trades only), then:
    python3 research/ripshort_regime_gate.py --trades <out> --retcol ret_1m ...
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import anomaly_fade_bounce as afb  # noqa: E402

mnr = afb.mnr
BAR = pd.Timedelta(minutes=15)


def load_1m(sym: str, cache: str):
    df = mnr._read_ms_csv(os.path.join(cache, f"{sym}_1m.csv"), 6)
    if df is None or df.empty:
        return None
    df = df[~df.index.duplicated(keep="last")].sort_index()
    return df.index.values, df[1].values, df[2].values, df[3].values


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", required=True)
    ap.add_argument("--trace", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()

    tr = pd.read_csv(a.trades, parse_dates=["entry_time", "exit_time"])
    tr = tr[tr.strategy == "RipShort"]
    tc = pd.read_csv(a.trace, parse_dates=["entry_time", "exit_time"])
    df = tr.merge(tc, on=["symbol", "entry_time", "exit_time"], how="inner")
    assert len(df) <= min(len(tr), len(tc)), "join produced duplicates"
    print(f"{len(tr)} RipShort trades, {len(tc)} traced, {len(df)} joined")

    rows, stats = [], dict(stop=0, order_flip=0, gap=0, no_touch=0, outside=0)
    for sym, g in df.groupby("symbol"):
        m = load_1m(sym, a.cache)
        if m is None:
            stats["outside"] += len(g); continue
        t1, o1, h1, l1 = m
        for r in g.itertuples(index=False):
            lo = np.datetime64(r.exit_time)
            if lo < t1[0] or lo + np.timedelta64(15, "m") > t1[-1]:
                stats["outside"] += 1; continue
            ret1 = r.return_pct
            if r.reason == "stop":
                stats["stop"] += 1
                i, j = np.searchsorted(t1, lo), np.searchsorted(t1, lo + np.timedelta64(15, "m"))
                new_exit, new_cost = None, r.cost_other
                for k in range(i, j):
                    hit_s, hit_t = h1[k] >= r.stop_px, l1[k] <= r.target
                    if hit_t and not hit_s:
                        new_exit = r.target; stats["order_flip"] += 1; break
                    if hit_s:
                        new_exit = max(r.stop_px, o1[k])
                        if o1[k] > r.stop_px: stats["gap"] += 1
                        break
                if new_exit is None:                     # 1m never touched the stop: data mismatch, keep 15m
                    stats["no_touch"] += 1
                else:
                    d_price = (r.exit_px - new_exit) / r.entry * 100.0      # short: lower exit = better
                    ret1 = r.return_pct + r.size_mult * (d_price + (r.cost_stop - new_cost))
            rows.append(dict(entry_time=r.entry_time, exit_time=r.exit_time, symbol=sym, strategy="RipShort",
                             reason=r.reason, ret_15m=r.return_pct, ret_1m=ret1))
    out = pd.DataFrame(rows)
    out.to_csv(a.out, index=False)
    print(f"1m-window trades: {len(out)} ({out.entry_time.min():%Y-%m-%d} → {out.entry_time.max():%Y-%m-%d}); "
          f"outside 1m data: {stats['outside']}")
    print(f"stop exits replayed: {stats['stop']} · target came first (15m booked a loss, 1m says win): {stats['order_flip']} · "
          f"gapped through the stop: {stats['gap']} · 1m never touched the stop: {stats['no_touch']}")
    d = out.ret_1m - out.ret_15m
    print(f"mean per trade: 15m {out.ret_15m.mean():+.3f}%  →  1m {out.ret_1m.mean():+.3f}%   (Δ {d.mean():+.4f}%, "
          f"changed trades {int((d.abs() > 1e-9).sum())}, Δ on changed {d[d.abs() > 1e-9].mean():+.3f}%)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
