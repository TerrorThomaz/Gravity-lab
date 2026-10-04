"""Is the fill-bar gain real? A 1m check of grid-rung fills in block A.

Every one-bar rung fill in grid_paths.py closes its fill hour 0.2-0.6% in its favour (m(1)), and
shuffled bars show the same: it comes from bar shape. A limit at the level catches the bottom of a
wick. The hourly model counts a fill whenever the bar's low trades 5bp through the level. A wick
that pierces for one minute on thin volume may never fill a real resting order (queue position),
and the fills that DO get filled are the ones where price stays through: adverse selection.

1m candles exist locally only from 2025-08-31, which is sealed block B. So this samples block-A
fills and fetches the 1m candles of each fill hour from Bybit (one request per fill). It measures
fill mechanics inside the fill hour only; no post-fill drift is read.

Per sampled fill, with level L (long; shorts mirrored):
  touch fill        first minute with low ≤ L·(1−5bp)    (the hourly model, reproduced at 1m)
  through fill      first minute that CLOSES at or beyond L — price stayed through the level for
                    a full minute, so a resting order at L was certainly reached
  r1                side · ln(hour close / fill px), px = min(open of the fill minute, L)
Reported: the share of touch fills that are also through fills; mean r1 for touch fills,
through fills and wick-only fills (touch, but not through); minutes beyond L; USD turnover in those
minutes. If the wick-only fills carry the gain, m(1) is an artefact of the fill model.

Run:  python3 research/wick_1m.py --universe oos|backtest [--per-cell 300]
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
from context_gate import gates  # noqa: E402
from grid_paths import KS, SEAL, THROUGH, atr, mnr, run_engine  # noqa: E402

URL = "https://api.bybit.com/v5/market/kline"


def fetch_1m(sym: str, start: pd.Timestamp) -> list[list[float]] | None:
    """The 60 one-minute candles of the hour starting at `start`: [ms, o, h, l, c, vol, turnover]."""
    s = int(start.value // 1_000_000)
    q = urllib.parse.urlencode(dict(category="linear", symbol=sym, interval=1, start=s, end=s + 59 * 60_000, limit=60))
    for attempt in range(5):
        try:
            with urllib.request.urlopen(f"{URL}?{q}", timeout=20) as r:
                d = json.load(r)
            if d.get("retCode") == 0:
                rows = sorted([[float(x) for x in row] for row in d["result"]["list"]])
                return rows if len(rows) >= 55 else None
            time.sleep(1 + attempt)
        except Exception:
            time.sleep(1 + attempt)
    return None


def measure(rows, side: int, L: float) -> dict | None:
    a = np.array(rows)
    o, h, l, c, turn = a[:, 1], a[:, 2], a[:, 3], a[:, 4], a[:, 6]
    if side == 1:
        touch, through, beyond = l <= L * (1 - THROUGH), c <= L, l <= L
    else:
        touch, through, beyond = h >= L * (1 + THROUGH), c >= L, h >= L
    if not touch.any():
        return {"touch": False}
    i = int(np.argmax(touch))
    px = min(o[i], L) if side == 1 else max(o[i], L)
    r1 = 100 * side * math.log(c[-1] / px)
    thr = through[i:].any()                              # stayed through for a full minute, at or after the touch
    return {"touch": True, "through": bool(thr), "r1": r1, "min_beyond": int(beyond.sum()),
            "usd_beyond": float(turn[beyond].sum()), "minute": i}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    ap.add_argument("--per-cell", type=int, default=300)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()

    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, a.cache)
    keep = mk.close.index < SEAL
    times, syms = mk.close.index[keep], list(mk.close.columns)
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    fl, _ = run_engine(O, H, L, C)
    A = atr(H, L, C)
    t, j, side, k = fl["t"], fl["j"], fl["side"], fl["k"]
    level = C[t, j] - side * k * A[t, j]
    G = gates(H, L, C)
    rng = np.random.default_rng(20261007)

    cells = {}
    for s in (1, -1):
        for kk in KS:
            cells[f"{'long' if s > 0 else 'short'} k{kk}"] = np.where((side == s) & (k == kk))[0]
    for name in ("fade_s", "swing", "fade_l"):
        s, M = G[name]
        cells[f"gate {name}"] = np.where((side == s) & M[t, j])[0]
    picks = {c: rng.choice(ix, min(a.per_cell, len(ix)), replace=False) for c, ix in cells.items()}

    jobs = sorted({int(i) for ix in picks.values() for i in ix})
    print(f"{a.universe}: fetching 1m candles for {len(jobs)} block-A fill hours …", flush=True)

    def one(i):
        rows = fetch_1m(syms[j[i]], times[t[i] + 1])
        time.sleep(0.1)
        return i, (None if rows is None else measure(rows, int(side[i]), float(level[i])))

    with ThreadPoolExecutor(max_workers=8) as ex:
        res = dict(ex.map(one, jobs))

    print(f"\n   {'cell':<14}{'n':>5}{'1m touch':>9}{'through':>9}   {'r1 all':>8}{'through':>9}{'wick-only':>10}   "
          f"{'min beyond (med)':>17}{'USD beyond (med)':>18}")
    for c, ix in picks.items():
        m = [res[int(i)] for i in ix if res.get(int(i)) is not None]
        tm = [x for x in m if x["touch"]]
        if not tm:
            continue
        thr = [x for x in tm if x["through"]]
        wo = [x for x in tm if not x["through"]]
        f = lambda xs: np.mean([x["r1"] for x in xs]) if xs else float("nan")
        print(f"   {c:<14}{len(m):>5}{len(tm) / len(m):>9.0%}{len(thr) / len(tm):>9.0%}   {f(tm):+8.3f}{f(thr):+9.3f}{f(wo):+10.3f}   "
              f"{np.median([x['min_beyond'] for x in tm]):>17.0f}{np.median([x['usd_beyond'] for x in tm]):>18,.0f}")
    print("\n   1m touch = the hourly fill reproduces on 1m candles; through = a minute CLOSED at or beyond the level")
    print("   r1 in % (fill-hour close vs fill price). If 'through' r1 ≤ 0 and the gain sits in 'wick-only', the")
    print("   hourly m(1) is fill-model optimism: a resting order is least likely to be reached exactly there.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
