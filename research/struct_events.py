"""Structure as the ENTRY TRIGGER: do BoS / sweep / Donchian-break events pay, and in what context?

Pre-registration: docs/STRUCT_EVENTS_2026-10.md (frozen at the commit that adds this file).

structure15 let structure RANK grid rungs and random market entries, and it added nothing. But a
break of structure is traded AT the break, and almost none of those moments were candidates. Here
every candidate is a structure event on 15m bars (block A), entered at the next open (taker), on BOTH
sides (continuation and reversal), and a GBM searches all 57 features (discover15's 29 + structure15's
28) plus the event type for the context in which they pay. Random entries matched per coin × side
(grid_paths.random_entries) are the raw baseline.

Run:  python3 research/struct_events.py --count     (event counts only, no outcomes: sets KEEP)
      python3 research/struct_events.py             (the trial)
"""

from __future__ import annotations

import argparse
import gc
import os
import sys
import time

import numpy as np
import pandas as pd

import discover15 as d15
import structure15 as st
import wf_parallel as wfp
from grid_paths import SEAL, atr, day_t, label, mnr, random_entries, sigma

EVENTS = {1: "bos4", 2: "bos16", 3: "sweep4", 4: "don96"}
KEEP = {1: 0.3, 2: 1.0, 3: 0.3, 4: 1.0}          # subsample per event type, set from --count (memory) before freezing
NULLS = 10
FEATURES = st.BASE + st.STRUCT + ["ev", "evdir"]


def events(H, L, C, A):
    """{type: (mask T×N, direction T×N)}, all known at the close of bar t."""
    out = {}
    for code, w in ((1, 4), (2, 16)):
        s = st.swings(H, L, C, A, w)
        out[code] = (s["bos_age"] == 0, s["bos_dir"])
        if w == 4:
            out[3] = (s["sweep"] != 0, s["sweep"])
        del s
        gc.collect()
    Hd, Ld = pd.DataFrame(H), pd.DataFrame(L)
    hi, lo = Hd.rolling(96).max().shift(1).values, Ld.rolling(96).min().shift(1).values
    with np.errstate(invalid="ignore"):
        up = (C > hi) & ~(np.vstack([np.full((1, C.shape[1]), np.nan), C[:-1]]) > np.vstack([np.full((1, C.shape[1]), np.nan), hi[:-1]]))
        dn = (C < lo) & ~(np.vstack([np.full((1, C.shape[1]), np.nan), C[:-1]]) < np.vstack([np.full((1, C.shape[1]), np.nan), lo[:-1]]))
    out[4] = (up | dn, np.where(up, 1.0, np.where(dn, -1.0, 0.0)))
    return out


def build(universe: str, rng, count_only: bool = False):
    if count_only:
        return _build(universe, rng, True)
    import fcache
    return fcache.cached("sev", (universe, tuple(FEATURES), tuple(sorted(KEEP.items()))), rng, lambda: _build(universe, rng))


def _build(universe: str, rng, count_only: bool = False):
    t0 = time.time()
    syms = mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins")
    if os.environ.get("D15_MAXCOINS"):
        syms = syms[: int(os.environ["D15_MAXCOINS"])]
    idx, names, M, fund = d15.load15(syms)
    O, H, L, C = M["o"], M["h"], M["l"], M["c"]
    S, A = sigma(C), atr(H, L, C)
    mlr, mcum = d15.market_path(C)
    valid = np.zeros_like(C, bool)
    valid[:-1] = np.isfinite(C[:-1]) & np.isfinite(O[1:]) & np.isfinite(S[:-1]) & np.isfinite(A[:-1])
    parts, counts = [], {}
    for code, (mask, dirn) in events(H, L, C, A).items():
        t, j = np.nonzero(mask & valid)
        counts[EVENTS[code]] = len(t)
        keep = rng.random(len(t)) < KEEP[code]
        t, j = t[keep], j[keep]
        for side in (1, -1):
            parts.append(dict(t=t, j=j, f=t + 1, side=np.full(len(t), side, np.int8), k=np.zeros(len(t), np.int8),
                              px=O[t + 1, j], sig=S[t, j], ev=np.full(len(t), code, np.int8),
                              evdir=dirn[t, j].astype(np.int8)))
    if count_only:
        T, N = C.shape
        print(f"   {universe}: {len(names)} coins, {T:,} bars; events " +
              "  ".join(f"{k} {v:,} ({v / (T * N) * 96:.2f}/coin-day)" for k, v in counts.items()), flush=True)
        return None
    fl = {k: np.concatenate([p[k] for p in parts]) for k in parts[0]}
    del parts
    r0 = random_entries(dict(j=fl["j"][fl["side"] == 1], side=np.ones((fl["side"] == 1).sum(), np.int8)), C, O, S, rng)
    r0 = {k: np.concatenate([v, v if k != "side" else -v]) for k, v in r0.items()}   # same draws, both sides
    lab, lab0 = label(fl, H, L, C, hs=d15.HS15), label(r0, H, L, C, hs=d15.HS15)
    t, j, f, side = fl["t"], fl["j"], fl["f"], fl["side"].astype(float)
    X = np.full((len(t), len(FEATURES)), np.nan, dtype=np.float32)
    col = {n: i for i, n in enumerate(FEATURES)}
    for name, arr in st.full_iter(idx, M, fund, S, A, mlr, mcum):
        X[:, col[name]] = arr[t, j] if arr.ndim == 2 else arr[t]
        del arr
    X[:, col["k"]] = 0
    X[:, col["ev"]], X[:, col["evdir"]] = fl["ev"], fl["evdir"]
    T = len(idx)
    cost = d15.COST[0]
    y, hedged = {}, {}
    for h in d15.HS15:
        y[h] = (lab[f"r{h}"] - cost).astype(np.float32)
        e = np.minimum(f + h - 1, T - 1)
        mk = np.where(f + h - 1 < T, mcum[e] - mcum[f], np.nan)
        hedged[h] = (lab[f"r{h}"] - side * 100 * mk - cost).astype(np.float32)
    ft = idx[f]
    blk = lambda tt: ((tt - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values
    out = dict(X=X, y=y, hedged=hedged, side=fl["side"], k=np.zeros(len(t), np.int8), ev=fl["ev"], evdir=fl["evdir"],
               fill_time=ft.values, blk=blk(ft), in_gate=np.zeros(len(t), bool),
               r0=dict(side=r0["side"], y={h: lab0[f"r{h}"] - cost for h in d15.HS15}, blk=blk(idx[r0["f"]]),
                       fill_time=idx[r0["f"]].values))
    print(f"   {universe}: {len(names)} coins, {len(t):,} event candidates, {len(r0['f']):,} random entries  "
          f"[{time.time() - t0:.0f}s]", flush=True)
    del M, O, H, L, C, S, A, lab, lab0
    gc.collect()
    return out


def raw_map(D, E, h):
    """Descriptive, decides nothing: net per event × direction relative to the trade, vs random entries."""
    print(f"\n── RAW MAP, H = {h}: mean net % [week-clustered t]; 'with' = trade in the event's direction ──")
    print(f"   {'event':<8}{'univ':<5}{'with: long':>22}{'with: short':>22}{'against: long':>22}{'against: short':>22}")
    for code, name in EVENTS.items():
        for u, X_ in (("bt", D), ("oos", E)):
            cells = []
            for rel in (1, -1):
                for side in (1, -1):
                    m = (X_["ev"] == code) & (X_["side"] == side) & (X_["evdir"] * X_["side"] == rel)
                    mm, t, _ = day_t(X_["y"][h][m], X_["blk"][m])
                    cells.append(f"{mm:+.3f} [{t:+4.1f}] n{m.sum() // 1000}k")
            print(f"   {name:<8}{u:<5}" + "".join(f"{c:>22}" for c in cells))
    for u, X_ in (("bt", D), ("oos", E)):
        r = X_["r0"]
        s = "  ".join(f"{'long' if sd > 0 else 'short'} {day_t(r['y'][h][r['side'] == sd], r['blk'][r['side'] == sd])[0]:+.3f}"
                      for sd in (1, -1))
        print(f"   random entries {u}: {s}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", action="store_true")
    a = ap.parse_args()
    rng = np.random.default_rng(20261006)
    if a.count:
        build("backtest", rng, True)
        build("oos", rng, True)
        return 0
    print("building event candidates (block A, 15m, next-open taker entries) …", flush=True)
    D = build("backtest", rng)
    E = build("oos", rng)
    assert pd.to_datetime(D["fill_time"]).max() < SEAL and pd.to_datetime(E["fill_time"]).max() < SEAL
    for h in d15.HS15:
        raw_map(D, E, h)
        nn = NULLS if h == d15.PRIMARY else 0
        R = wfp.run(D, E, h, {"m": (None, None, None)}, nulls=nn)["m"]
        d15.report(D, E, h, R, nn)
        if h == d15.PRIMARY:
            for u, X_ in (("bt", D), ("oos", E)):
                p = R["pred"][u]
                print(f"   selected by event ({u}, H={h}): " + "  ".join(
                    f"{EVENTS[c]} {'L' if sd > 0 else 'S'} {np.nanmean(np.where((X_['ev'] == c) & (X_['side'] == sd) & (p > 0), X_['y'][h], np.nan)):+.3f}"
                    f" ({((X_['ev'] == c) & (X_['side'] == sd) & (p > 0)).sum()})"
                    for c in EVENTS for sd in (1, -1)))
            wfp.importance(D, h, R, FEATURES)
        del R
        gc.collect()
    print("\nTrials: 1 primary (model-selected events, H=16) + 2 secondary horizons. Block B untouched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
