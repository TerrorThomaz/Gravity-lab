"""Queue-aware rung fills on 1m candles + a walk-forward search over (rung depth k, exit time).

The 1m wick check (wick_1m.py) bracketed fill realism between two models. "Touch" (the hourly model)
is optimistic: wick-only fills carry +0.8-2.0% each. "Through" (a minute closes beyond the level) is
conservative. This model sits between them, as an exchange queue does: a resting limit at L fills
only once the volume traded BEYOND L since it was placed exceeds the queue ahead of it plus its own
size, Q (USD).

The volume beyond L in one minute is estimated as turnover × the fraction of that minute's range
lying beyond L (uniform-price assumption). Fill price is L; the fill minute is the first at which
cumulative beyond-volume ≥ Q. The order lives for the fill hour only, as in grid_paths.

Candidates (block A, sampled):
  k=1,2,3  the grid_paths one-bar rungs at close_t ∓ k·ATR14_t, maker entry
  k=0      a market entry at the open of a random hour on the same coins: the "every entry" baseline,
           taker entry
Exit (fixed in advance): the close of minute fill+m, m ∈ {15, 30, 60, 120, 240, 480, 900}, taker.
Costs: rungs maker in + taker out = 0.125%; k=0 taker in + taker out = 0.21%.

Search, declared before the run: per side, pick the (k ∈ {1,2,3}, m) with the best mean net on all
EARLIER years of block A (expanding, at least 2 years), and trade it in the next year. That is 21
cells per side, all counted as trials. Reported: the chained out-of-year result, against the
average cell that year (random selection) and against k=0.

Run:  python3 research/queue_fill.py --universe oos|backtest [--per-cell 3000]
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import KS, SEAL, THROUGH, atr, day_t, mnr, run_engine  # noqa: E402

URL = "https://api.bybit.com/v5/market/kline"
MS = (15, 30, 60, 120, 240, 480, 900)
QS = {"touch": 0.0, "5k": 5e3, "25k": 25e3, "100k": 1e5, "through": None}
PRIMARY = "25k"
COST_RUNG = (mnr.MAKER_COST_PER_SIDE + mnr.TAKER_COST_PER_SIDE) * 100
COST_MKT = 2 * mnr.TAKER_COST_PER_SIDE * 100
BARS = 1000                                   # one request: 16.7h of 1m, enough for fill minute ≤ 59 + 900
CACHE = os.path.join(mnr.REPO, "candle_cache", "m1_windows")


def fetch(sym: str, start: pd.Timestamp) -> np.ndarray | None:
    """1000 one-minute candles from `start`: columns ms, o, h, l, c, vol, turnover. Disk-cached."""
    s = int(start.value // 1_000_000)
    path = os.path.join(CACHE, f"{sym}_{s}.npy")
    if os.path.exists(path):
        a = np.load(path)
        return a if len(a) else None
    q = urllib.parse.urlencode(dict(category="linear", symbol=sym, interval=1, start=s, end=s + (BARS - 1) * 60_000, limit=BARS))
    a = np.empty((0, 7))
    for attempt in range(5):
        try:
            with urllib.request.urlopen(f"{URL}?{q}", timeout=20) as r:
                d = json.load(r)
            if d.get("retCode") == 0:
                rows = sorted([[float(x) for x in row] for row in d["result"]["list"]])
                a = np.array(rows) if len(rows) >= BARS - 10 else np.empty((0, 7))
                break
            time.sleep(1 + attempt)
        except Exception:
            time.sleep(1 + attempt)
    else:
        return None                                          # network failure: do not cache
    np.save(path, a)
    return a if len(a) else None


def queue_fill(a: np.ndarray, side: int, L: float, q: float | None) -> int | None:
    """Fill minute (0..59) of a resting limit at L under queue q (USD); None if unfilled in the hour.
    q=None is the 'through' model: the first minute that closes at or beyond L."""
    o, h, l, c, turn = a[:60, 1], a[:60, 2], a[:60, 3], a[:60, 4], a[:60, 6]
    if q is None:
        hit = (c <= L) if side == 1 else (c >= L)
        return int(np.argmax(hit)) if hit.any() else None
    rng = np.maximum(h - l, 1e-12)
    frac = np.clip((L - l) / rng, 0, 1) if side == 1 else np.clip((h - L) / rng, 0, 1)
    pierced = (l <= L * (1 - THROUGH)) if side == 1 else (h >= L * (1 + THROUGH))
    if not pierced.any():
        return None
    first = int(np.argmax(pierced))
    if q == 0:
        return first
    cum = np.cumsum(np.where(np.arange(60) >= first, turn * frac, 0.0))
    ok = cum >= q
    return int(np.argmax(ok)) if ok.any() else None


def exits(a: np.ndarray, side: int, i: int, px: float) -> dict:
    c = a[:, 4]
    return {m: (100 * side * math.log(c[i + m] / px) if i + m < len(c) else np.nan) for m in MS}


def selftest() -> int:
    a = np.zeros((BARS, 7))
    a[:, 1:5] = 101.0; a[:, 6] = 1e4                         # flat at 101, out of reach of L = 100
    a[10, 1:5] = [100.4, 100.5, 99.9, 100.2]                 # wick: pierces by 10bp, range 0.6 → 1/6 of $10k beyond
    a[11, 1:5] = [100.0, 100.0, 99.4, 99.5]; a[11, 6] = 6e4  # fully beyond: all $60k counts, closes through
    a[11 + 15, 4] = 100.5                                    # exit price 15 minutes after a minute-11 fill
    assert queue_fill(a, 1, 100.0, 0.0) == 10                # touch: the wick minute
    assert queue_fill(a, 1, 100.0, 5e3) == 11                # $1.67k beyond in the wick < $5k → next minute
    assert queue_fill(a, 1, 100.0, None) == 11               # through: first close ≤ L
    assert queue_fill(a, 1, 100.0, 1e6) is None              # queue never cleared inside the hour
    assert queue_fill(a, -1, 102.0, 0.0) is None             # short level never reached
    assert abs(exits(a, 1, 11, 100.0)[15] - 100 * math.log(1.005)) < 1e-9
    print("selftest: OK (touch / queue / through ordering, unfilled queue, exit arithmetic)")
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    ap.add_argument("--per-cell", type=int, default=3000)
    ap.add_argument("--workers", type=int, default=16)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()
    os.makedirs(CACHE, exist_ok=True)

    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, a.cache)
    keep = mk.close.index < SEAL - pd.Timedelta(days=1)      # windows run 16.7h past the fill: stay out of B
    times, syms = mk.close.index[keep], list(mk.close.columns)
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    fl, _ = run_engine(O, H, L, C)
    A = atr(H, L, C)
    t, j, side, k = fl["t"], fl["j"], fl["side"], fl["k"]
    level = C[t, j] - side * k * A[t, j]
    rng = np.random.default_rng(20261008)

    jobs = []                                                # (t, j, side, k, level)
    for s in (1, -1):
        for kk in KS:
            ix = np.where((side == s) & (k == kk))[0]
            for i in rng.choice(ix, min(a.per_cell, len(ix)), replace=False):
                jobs.append((int(t[i]), int(j[i]), s, kk, float(level[i])))
    ok_bars = np.argwhere(np.isfinite(C[:-1]) & np.isfinite(O[1:]))
    for s in (1, -1):                                        # k=0: market entry at a random hour
        for tt, jj in ok_bars[rng.choice(len(ok_bars), a.per_cell, replace=False)]:
            jobs.append((int(tt), int(jj), s, 0, float("nan")))
    print(f"{a.universe}: {len(jobs)} candidates (block A to {times[-1]:%Y-%m-%d}); fetching 1m windows "
          f"(cached in {os.path.relpath(CACHE, mnr.REPO)}) …", flush=True)

    def one(job):
        tt, jj, s, kk, lv = job
        w = fetch(syms[jj], times[tt + 1])
        if w is None:
            return None
        base = {"t": tt, "time": times[tt + 1], "side": s, "k": kk}
        if kk == 0:
            return [{**base, "q": qn, "fill": True, **{f"g{m}": v for m, v in exits(w, s, 0, w[0, 1]).items()}} for qn in QS]
        out = []
        for qn, qv in QS.items():
            i = queue_fill(w, s, lv, qv)
            row = {**base, "q": qn, "fill": i is not None}
            if i is not None:
                row.update({f"g{m}": v for m, v in exits(w, s, i, lv).items()})
            out.append(row)
        return out

    t0 = time.time()
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        res = [r for r in ex.map(one, jobs) if r]
    df = pd.DataFrame([row for r in res for row in r])
    print(f"   {len(res)} windows in {time.time() - t0:.0f}s")
    df["blk"] = (df.time - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)
    df["year"] = df.time.dt.year
    for m in MS:
        df[f"n{m}"] = df[f"g{m}"] - np.where(df.k == 0, COST_MKT, COST_RUNG)

    print(f"\n── 1. FILL RATE by queue model (share of hourly-model fills that fill) ──")
    for qn in QS:
        r = df[(df.q == qn) & (df.k > 0)].groupby(["side", "k"]).fill.mean()
        print(f"   {qn:<8}" + "  ".join(f"{'L' if s > 0 else 'S'}k{kk} {v:4.0%}" for (s, kk), v in r.items()))

    print(f"\n── 2. NET % per trade by exit minute (rungs: −{COST_RUNG:.3f}%, k=0 market: −{COST_MKT:.3f}%) [week t] ──")
    for qn in QS:
        print(f"   queue {qn}:")
        for (s, kk), g in df[(df.q == qn) & df.fill].groupby(["side", "k"]):
            if kk == 0 and qn != PRIMARY:
                continue
            cells = [day_t(g[f"n{m}"].values, g.blk.values)[:2] for m in MS]
            print(f"     {'long ' if s > 0 else 'short'} k{kk} n{len(g):>5}  " +
                  "  ".join(f"+{m}m {mm:+.3f}[{tt:+4.1f}]" for m, (mm, tt) in zip(MS, cells)))

    print(f"\n── 3. WALK-FORWARD SEARCH (queue {PRIMARY}): best (k, m) on all earlier years, traded next year ──")
    P = df[(df.q == PRIMARY) & df.fill]
    years = sorted(P.year.unique())
    for s in (1, -1):
        Ps = P[(P.side == s) & (P.k > 0)]
        chain, rand, mkt = [], [], []
        for y in years[2:]:
            tr = Ps[Ps.year < y]
            score = {(kk, m): tr[tr.k == kk][f"n{m}"].mean() for kk in KS for m in MS}
            kk, m = max(score, key=score.get)
            te = Ps[Ps.year == y]
            pick = te[te.k == kk]
            chain.append(pick.assign(ret=pick[f"n{m}"]))
            avg = np.nanmean([te[te.k == k2][f"n{m2}"].mean() for k2 in KS for m2 in MS])
            z = P[(P.side == s) & (P.k == 0) & (P.year == y)]
            mkt_best = z[f"n{m}"].mean()
            rand.append(avg); mkt.append(mkt_best)
            print(f"   {'long ' if s > 0 else 'short'} {y}: chose k{kk} +{m}m (train {score[(kk, m)]:+.3f})  "
                  f"test {pick[f'n{m}'].mean():+.3f} (n {len(pick)})   avg cell {avg:+.3f}   k=0 same exit {mkt_best:+.3f}")
        ch = pd.concat(chain)
        mm, tt, _ = day_t(ch.ret.values, ch.blk.values)
        print(f"   {'long ' if s > 0 else 'short'} CHAINED out-of-year: {mm:+.3f}%/trade [t {tt:+.1f}], n {len(ch)}"
              f"   vs avg cell {np.nanmean(rand):+.3f}, k=0 {np.nanmean(mkt):+.3f}")
    print("\n   Trials: 21 cells per side (k × exit), 5 queue models reported (25k primary), fixed before the run.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
