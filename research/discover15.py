"""Open edge search over every 15m grid entry: find what the strategies did not.

Earlier phases scored the strategies' own setups as gates over grid fills. That only proves the
framework can FIND known setups. This searches a broad, fixed feature set for gates nobody wrote
down, then asks how much of what it finds lies outside every strategy context gate.

Fixed before the first run (2026-10-03):
  candidates  15m bars, block A (before 2025-07-01):
              - rungs at close_t ∓ k·ATR14 (k = 1, 2, 3), one row per rung reached, TOUCH fills
                (the user's call: sizing small enough that queue logic is tested live);
              - k=0 market entries at the next open.
              Subsampled for memory: k1 30%, k2 60%, k3 100%, k0 2% of bars.
  features    29, all known at the close of the arming bar (see FEATURES).
  target      net % = side·ln(C[fill+H−1] / px) − cost. Costs: rungs maker-in/taker-out 0.125%,
              k0 taker both ways 0.21%. Horizons H = 4 / 16 / 48 bars (1h / 4h / 12h);
              H = 16 is PRIMARY. 3 trials.
  model       numpy histogram GBM (gbm_np.py), one per side, depth 3, 150 rounds, lr 0.05,
              min_leaf 1000. Trained on BacktestCoins only, quarterly walk-forward from 12 months
              in, on candidates whose label ended before the quarter (purge). Target winsorised at
              the 0.5/99.5 percentiles of the training set. OosCoins are scored by the same models
              and never trained on.
  rule        trade if predicted net > 0.
  nulls       10 refits per quarter with the target permuted within k (H = 16 only). The
              all-candidate mean is the random-selection baseline.
  pass        H = 16: selected net > 0 with week-clustered t ≥ 3 in BOTH universes, beating all
              10 nulls, and positive in both halves. Market-hedged and outside-strategy-gate
              decompositions are reported.

Run:  python3 research/discover15.py [--nulls 10]
"""

from __future__ import annotations

import argparse
import gc
import os
import sys
import time

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from context_gate import adx, ema, gates, rsi  # noqa: E402
from gbm_np import Gbm, apply_bins, bin_edges  # noqa: E402
from grid_paths import KS, SEAL, atr, day_t, fills, label, mnr, sigma  # noqa: E402

HS15 = (4, 16, 48)
PRIMARY = 16
COST = {0: 2 * mnr.TAKER_COST_PER_SIDE * 100, 1: (mnr.MAKER_COST_PER_SIDE + mnr.TAKER_COST_PER_SIDE) * 100}
KEEP = {0: 0.02, 1: 0.30, 2: 0.60, 3: 1.0}
FEATURES = ["z1", "z4", "z16", "z96", "z384", "res4", "res16", "res96", "d_ema20", "d_ema50", "d_ema200",
            "slope50", "rsi14", "adx14", "lbbw", "latr", "rpos96", "vz", "fund",
            "mz4", "mz16", "mz96", "mz384", "mvol", "breadth", "disp16", "hour", "dow", "k"]
CACHE = os.path.join(mnr.REPO, "candle_cache")


def load15(syms: list[str]):
    frames = {}
    for s in syms:
        d = mnr._read_ms_csv(os.path.join(CACHE, f"{s}_15m.csv"), 6)
        if d is None or len(d) < 96 * 90:
            continue
        d.columns = ["o", "h", "l", "c", "v"]
        frames[s] = d[~d.index.duplicated(keep="last")]
    idx = pd.date_range(min(f.index.min() for f in frames.values()), SEAL - pd.Timedelta(minutes=15), freq="15min")
    names = list(frames)
    M = {c: np.column_stack([frames[s][c].reindex(idx).values for s in names]).astype(np.float64) for c in "ohlcv"}
    fund = np.column_stack([(lambda f: f.reindex(idx + pd.Timedelta(minutes=15), method="ffill").values
                             if f is not None else np.full(len(idx), np.nan))(mnr.load_funding(s, CACHE)) for s in names])
    return idx, names, M, fund * 1e4


def market_path(C):
    with np.errstate(invalid="ignore", divide="ignore"):
        lr = np.diff(np.log(C), axis=0, prepend=np.nan)
    mlr = np.nanmean(lr, axis=1)                                    # equal-weight market, 15m log returns
    return mlr, np.nancumsum(np.nan_to_num(mlr))


def feature_iter(idx, M, fund, S, A, mlr, mcum):
    """Yields (name, array) one feature at a time; 2-D arrays are time × coin, 1-D are market-wide.
    Gathered at the candidates and freed immediately, because 29 full matrices do not fit in memory."""
    H, L, C, V = M["h"], M["l"], M["c"], M["v"]
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
    lag = lambda X, n: np.concatenate([np.full((n,) + X.shape[1:], np.nan), X[:-n]])
    msig = pd.Series(mlr).rolling(168, min_periods=84).std().values
    for n in (1, 4, 16, 96, 384):
        yield f"z{n}", (lc - lag(lc, n)) / (S * np.sqrt(n))
    for n in (4, 16, 96):
        yield f"res{n}", ((lc - lag(lc, n)) - (mcum - lag(mcum, n))[:, None]) / (S * np.sqrt(n))
    e50 = ema(C, 50)
    yield "d_ema20", (C - ema(C, 20)) / A
    yield "d_ema50", (C - e50) / A
    yield "d_ema200", (C - ema(C, 200)) / A
    yield "slope50", (e50 - lag(e50, 20)) / A
    yield "rsi14", rsi(C)
    yield "adx14", adx(H, L, C)
    Cd = pd.DataFrame(C)
    with np.errstate(invalid="ignore", divide="ignore"):
        yield "lbbw", np.log(4 * Cd.rolling(20).std().values / Cd.rolling(20).mean().values)
        yield "latr", np.log(A / atr(H, L, C, 100))
        lo, hi = Cd.rolling(96).min().values, Cd.rolling(96).max().values
        yield "rpos96", (C - lo) / (hi - lo)
        del lo, hi
        lq = np.log1p(V * C)
    yield "vz", lq - pd.DataFrame(lq).rolling(96, min_periods=48).mean().values
    del lq
    yield "fund", fund
    for n in (4, 16, 96, 384):
        yield f"mz{n}", (mcum - lag(mcum, n)) / (msig * np.sqrt(n))
    mv = pd.Series(mlr)
    yield "mvol", np.log(mv.rolling(96).std() / mv.rolling(672).std()).values
    yield "breadth", np.nanmean(np.where(np.isfinite(C), C > e50, np.nan), axis=1)
    z16 = (lc - lag(lc, 16)) / (S * 4.0)
    yield "disp16", np.nanstd(z16, axis=1)
    del z16
    yield "hour", (idx.hour + idx.minute / 60).values.astype(float)
    yield "dow", idx.dayofweek.values.astype(float)


def build(universe: str, rng) -> dict:
    """Cached (fcache): keyed on the feature list, the rng state and the source of the build's modules."""
    import fcache
    return fcache.cached("d15", (universe, tuple(FEATURES), tuple(sorted(KEEP.items())), feature_iter.__module__,
                                 feature_iter.__name__), rng, lambda: _build(universe, rng))


def _build(universe: str, rng) -> dict:
    t0 = time.time()
    syms = mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins")
    if os.environ.get("D15_MAXCOINS"):
        syms = syms[: int(os.environ["D15_MAXCOINS"])]
    idx, names, M, fund = load15(syms)
    O, H, L, C = M["o"], M["h"], M["l"], M["c"]
    S, A = sigma(C), atr(H, L, C)
    mlr, mcum = market_path(C)
    fl = fills(O, H, L, C, A, S)
    keep = rng.random(len(fl["t"])) < np.vectorize(KEEP.get)(fl["k"])
    fl = {k: v[keep] for k, v in fl.items()}
    ok = np.argwhere(np.isfinite(C[:-1]) & np.isfinite(O[1:]) & np.isfinite(S[:-1]))
    pick = ok[rng.random(len(ok)) < KEEP[0]]
    parts = [fl]
    for side in (1, -1):                                             # k=0 market entries, both sides
        tt, jj = pick[:, 0], pick[:, 1]
        parts.append(dict(t=tt, j=jj, f=tt + 1, side=np.full(len(tt), side, np.int8), k=np.zeros(len(tt), np.int8),
                          px=O[tt + 1, jj], sig=S[tt, jj]))
    fl = {k: np.concatenate([p[k] for p in parts]) for k in parts[0]}
    lab = label(fl, H, L, C, hs=HS15)
    t, j, f, side, k = fl["t"], fl["j"], fl["f"], fl["side"].astype(float), fl["k"]
    X = np.full((len(t), len(FEATURES)), np.nan, dtype=np.float32)
    col = {n: i for i, n in enumerate(FEATURES)}
    for name, arr in feature_iter(idx, M, fund, S, A, mlr, mcum):
        X[:, col[name]] = arr[t, j] if arr.ndim == 2 else arr[t]
        del arr
    X[:, col["k"]] = k
    T = len(idx)
    y, hedged = {}, {}
    for h in HS15:
        cost = np.where(k == 0, COST[0], COST[1])
        y[h] = (lab[f"r{h}"] - cost).astype(np.float32)
        e = np.minimum(f + h - 1, T - 1)
        # hedge from the fill bar's close: starting at the arming close would credit the hedge with
        # the very drop that filled the rung (smoke test: hedged t +13 to +20 on BOTH sides)
        mk = np.where(f + h - 1 < T, mcum[e] - mcum[f], np.nan)
        hedged[h] = (lab[f"r{h}"] - side * 100 * mk - cost).astype(np.float32)
    # strategy context gates on 1h bars, looked up at the last hour CLOSED by the arming bar's close
    mk1 = mnr.build_market(names, CACHE)
    keep1 = mk1.close.index < SEAL
    h1 = mk1.close.index[keep1]
    G = gates(*(d.values[keep1] for d in (mk1.high, mk1.low, mk1.close)))
    col1 = {s: i for i, s in enumerate(mk1.close.columns)}
    hour_ix = h1.get_indexer((idx[t] + pd.Timedelta(minutes=15)).floor("1h") - pd.Timedelta(hours=1))
    jj1 = np.array([col1.get(names[x], -1) for x in j])
    in_gate = np.zeros(len(t), bool)
    for name, (gs, Gm) in G.items():
        okk = (hour_ix >= 0) & (jj1 >= 0)
        hit = np.zeros(len(t), bool)
        hit[okk] = Gm[hour_ix[okk], jj1[okk]]
        in_gate |= hit & (side == gs)
    fill_time = idx[f]
    out = dict(X=X, y=y, hedged=hedged, side=fl["side"], k=k, fill_time=fill_time.values,
               blk=((fill_time - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values, in_gate=in_gate)
    print(f"   {universe}: {len(names)} coins, {len(idx):,} 15m bars, {len(t):,} candidates "
          f"({np.bincount(k, minlength=4)} by k), {in_gate.mean():.0%} inside a strategy context gate  [{time.time() - t0:.0f}s]", flush=True)
    del M, O, H, L, C, S, A, lab
    gc.collect()
    return out


def walk_forward(D: dict, E: dict, h: int, nulls: int, rng=None, bar_min: int = 15) -> dict:
    """Per side, quarterly refits on D (BacktestCoins); predictions for D's and E's (OosCoins) quarter.
    Runs in parallel (wf_parallel); `rng` is unused and kept for callers' signatures."""
    import wf_parallel as wfp
    return wfp.run(D, E, h, {"_": (None, None, None)}, nulls, bar_min)["_"]


def report(D, E, h, R, nulls, bar_min: int = 15, primary: int = PRIMARY):
    print(f"\n── H = {h} bars ({h * bar_min} min){'  [PRIMARY]' if h == primary else ''} ──")
    print(f"   {'univ':<5}{'side':<6}{'cands':>9}{'sel%':>6}  {'all-cand net':>13}  {'SELECTED net [t]':>18}  {'nulls ≥':>8}"
          f"  {'1st half':>15}  {'2nd half':>15}  {'hedged [t]':>15}  {'outside gates [t]':>18}  {'inside gates':>13}")
    for u, X_ in (("bt", D), ("oos", E)):
        p = R["pred"][u]
        for side in (1, -1):
            ok = (X_["side"] == side) & np.isfinite(p) & np.isfinite(X_["y"][h])
            sel = ok & (p > 0)
            y, b = X_["y"][h], X_["blk"]
            m, t, _ = day_t(y[sel], b[sel])
            base = y[ok].mean()
            if h == primary and nulls:
                nm = [y[ok & (R["npred"][u][z] > 0)].mean() if (ok & (R["npred"][u][z] > 0)).any() else -np.inf for z in range(nulls)]
                nb = f"{sum(v >= m for v in nm)}/{nulls}"
            else:
                nb = "—"
            fts = pd.to_datetime(X_["fill_time"])
            mid = fts[ok].min() + (fts[ok].max() - fts[ok].min()) / 2
            h1 = day_t(y[sel & (fts < mid)], b[sel & (fts < mid)])
            h2 = day_t(y[sel & (fts >= mid)], b[sel & (fts >= mid)])
            hd = day_t(X_["hedged"][h][sel], b[sel])
            og = sel & ~X_["in_gate"]
            o = day_t(y[og], b[og])
            ig = y[sel & X_["in_gate"]].mean()
            print(f"   {u:<5}{'long' if side > 0 else 'short':<6}{ok.sum():>9}{sel.sum() / max(ok.sum(), 1):>6.0%}  {base:>+13.3f}  "
                  f"{m:>+9.3f} [{t:+5.1f}]  {nb:>8}  {h1[0]:+7.3f} [{h1[1]:+4.1f}]  {h2[0]:+7.3f} [{h2[1]:+4.1f}]  "
                  f"{hd[0]:+7.3f} [{hd[1]:+4.1f}]  {o[0]:+9.3f} [{o[1]:+5.1f}]  {ig:>+13.3f}")
            if True:
                ks = "  ".join(f"k{kv}: {y[sel & (X_['k'] == kv)].mean():+.3f} (n {(sel & (X_['k'] == kv)).sum()})" for kv in range(4))
                print(f"         selected by rung: {ks}")


def importance(D, h, R, rng=None, names=None):
    """Out-of-sample IC drop when one feature is permuted, BacktestCoins, real models (parallel)."""
    import wf_parallel as wfp
    return wfp.importance(D, h, R, names or FEATURES)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--nulls", type=int, default=10)
    a = ap.parse_args()
    rng = np.random.default_rng(20261009)
    print("building candidate sets (block A, 15m, touch fills) …", flush=True)
    D = build("backtest", rng)
    E = build("oos", rng)
    for h in HS15:
        R = walk_forward(D, E, h, (nn := (a.nulls if h == PRIMARY else 0)), rng)
        report(D, E, h, R, nn)
        if h == PRIMARY:
            importance(D, h, R, rng)
    print("\nTrials: 3 horizons (16 primary), 1 model spec, 1 rule — fixed before the run. Block B untouched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
