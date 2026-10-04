"""Open edge search, regime scale: 1h grid entries, holds of 1-7 days, real funding, and a positive control.

discover15.py found nothing, but it was blind to the one effect a positive control shows is real
(trend_control.py: a 30-day market trend rule earns +1.1-1.4%/trade at 7 days). Its holds stopped at
12h, and its longest feature looked back 4 days. This is the same search with:
  - regime-scale features (coin and market 7d/30d/90d trend, distance to 30d high/low, market
    drawdown from its 90d high, breadth over the 30d EMA, 7d funding);
  - multi-day holds, with real funding charged;
  - the TREND RULE run on the same candidates as a built-in positive control. The search has to
    recover at least that much, otherwise the machinery is still blind.

Fixed before the first run (2026-10-04):
  candidates  1h bars, block A (before 2025-07-01):
              - rungs at close_t ∓ k·ATR14 (k = 1, 2, 3), one row per rung, touch fills, maker in;
              - k=0 market entries at the next open, taker in.
              Kept: k1 50%, k2/k3 100%, k0 5% of bars.
  target      net % at H = 24 / 72 / 168 h; 72 is PRIMARY. Net = side·ln(C[exit]/px) − cost − funding.
              Costs: rungs 0.125%, k0 0.21%. Funding: real per-symbol settlements over the hold
              (a long pays a positive rate); symbols without data pay the floor (0.01%/8h) either way.
  features    FEATURES below, all known at the close of the arming bar.
  model       discover15's: numpy GBM per side, trained on BacktestCoins, quarterly walk-forward with
              a purge = H. OosCoins are scored, never trained on.
  rule        trade if predicted net > 0.
  nulls       10 refits with the target permuted within k (primary H only).
  control     TREND = candidates whose side matches the sign of the market's 30d return.
  pass        primary: selected net > 0 with week t ≥ 3 in BOTH universes, beating all 10 nulls,
              positive in both halves, AND at least the TREND control's net (otherwise the model adds
              nothing to a one-line rule).

Run:  python3 research/discover1h.py [--nulls 10]
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
from discover15 import importance, report, walk_forward  # noqa: E402
from grid_paths import SEAL, atr, day_t, fills, label, mnr, sigma  # noqa: E402

HS = (24, 72, 168)
PRIMARY = 72
COST = {0: 2 * mnr.TAKER_COST_PER_SIDE * 100, 1: (mnr.MAKER_COST_PER_SIDE + mnr.TAKER_COST_PER_SIDE) * 100}
KEEP = {0: 0.05, 1: 0.50, 2: 1.0, 3: 1.0}
FEATURES = ["z1", "z4", "z24", "z168", "z720", "z2160", "res24", "res168", "res720",
            "d_ema50", "d_ema200", "rsi14", "adx14", "lbbw", "latr", "hi720", "lo720", "vz",
            "fund", "fund168",
            "mz24", "mz168", "mz720", "mz2160", "mdd2160", "mvol", "breadth", "disp24", "hour", "dow", "k"]
CACHE = os.path.join(mnr.REPO, "candle_cache")
# --pooled: one model for both sides, with every directional feature also given SIGNED by the trade's side
# ("the market's 30d trend in my direction"). Added after the per-side run failed its positive control:
# per-side models split "follow the trend" into two halves with few regimes each, confounded with drift.
DIRECTIONAL = ["z1", "z4", "z24", "z168", "z720", "z2160", "res24", "res168", "res720", "d_ema50", "d_ema200",
               "rsi14", "hi720", "lo720", "fund", "fund168", "mz24", "mz168", "mz720", "mz2160", "mdd2160", "breadth"]


def build(universe: str, rng) -> dict:
    t0 = time.time()
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins"), CACHE)
    keep = mk.close.index < SEAL
    idx, names = mk.close.index[keep], list(mk.close.columns)
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    V = mk.qvol.values[keep]
    T, N = C.shape
    S, A = sigma(C), atr(H, L, C)
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
        lr = np.diff(lc, axis=0, prepend=np.nan)
    mlr = np.nanmean(lr, axis=1)
    mcum = np.nancumsum(np.nan_to_num(mlr))
    # funding: cumulative real rate per symbol (fraction); unknown symbols charged the floor regardless of side
    rate = mk.funding.values[keep]
    known = mk.funding_known.reindex(names).fillna(False).values
    fcum = np.cumsum(rate, axis=0)

    fl = fills(O, H, L, C, A, S)
    sel = rng.random(len(fl["t"])) < np.vectorize(KEEP.get)(fl["k"])
    fl = {k: v[sel] for k, v in fl.items()}
    ok = np.argwhere(np.isfinite(C[:-1]) & np.isfinite(O[1:]) & np.isfinite(S[:-1]))
    pick = ok[rng.random(len(ok)) < KEEP[0]]
    parts = [fl]
    for side in (1, -1):
        tt, jj = pick[:, 0], pick[:, 1]
        parts.append(dict(t=tt, j=jj, f=tt + 1, side=np.full(len(tt), side, np.int8), k=np.zeros(len(tt), np.int8),
                          px=O[tt + 1, jj], sig=S[tt, jj]))
    fl = {k: np.concatenate([p[k] for p in parts]) for k in parts[0]}
    lab = label(fl, H, L, C, hs=HS)
    t, j, f, side, k = fl["t"], fl["j"], fl["f"], fl["side"].astype(float), fl["k"]

    lag = lambda X, n: np.concatenate([np.full((n,) + X.shape[1:], np.nan), X[:-n]])
    msig = pd.Series(mlr).rolling(168, min_periods=84).std().values
    X = np.full((len(t), len(FEATURES)), np.nan, dtype=np.float32)
    col = {n: i for i, n in enumerate(FEATURES)}

    def put(name, arr):
        X[:, col[name]] = arr[t, j] if arr.ndim == 2 else arr[t]

    for n in (1, 4, 24, 168, 720, 2160):
        put(f"z{n}", (lc - lag(lc, n)) / (S * np.sqrt(n)))
    for n in (24, 168, 720):
        put(f"res{n}", ((lc - lag(lc, n)) - (mcum - lag(mcum, n))[:, None]) / (S * np.sqrt(n)))
    e50 = ema(C, 50)
    put("d_ema50", (C - e50) / A); put("d_ema200", (C - ema(C, 200)) / A)
    put("rsi14", rsi(C)); put("adx14", adx(H, L, C))
    Cd = pd.DataFrame(C)
    with np.errstate(invalid="ignore", divide="ignore"):
        put("lbbw", np.log(4 * Cd.rolling(20).std().values / Cd.rolling(20).mean().values))
        put("latr", np.log(A / atr(H, L, C, 100)))
        put("hi720", (lc - np.log(Cd.rolling(720, min_periods=360).max().values)) / S)
        put("lo720", (lc - np.log(Cd.rolling(720, min_periods=360).min().values)) / S)
        lq = np.log1p(V)
    put("vz", lq - pd.DataFrame(lq).rolling(168, min_periods=84).mean().values)
    last = pd.DataFrame(np.where(mk.funding_mask.values[keep], rate, np.nan)).ffill().values * 1e4
    put("fund", last)
    put("fund168", (fcum - lag(fcum, 168)) * 1e4)
    for n in (24, 168, 720, 2160):
        put(f"mz{n}", (mcum - lag(mcum, n)) / (msig * np.sqrt(n)))
    put("mdd2160", mcum - pd.Series(mcum).rolling(2160, min_periods=720).max().values)
    mv = pd.Series(mlr)
    put("mvol", np.log(mv.rolling(168).std() / mv.rolling(2160).std()).values)
    e720 = ema(C, 720)
    put("breadth", np.nanmean(np.where(np.isfinite(C) & np.isfinite(e720), C > e720, np.nan), axis=1))
    put("disp24", np.nanstd((lc - lag(lc, 24)) / (S * np.sqrt(24)), axis=1))
    put("hour", idx.hour.values.astype(float)); put("dow", idx.dayofweek.values.astype(float))
    X[:, col["k"]] = k

    y, hedged = {}, {}
    cost = np.where(k == 0, COST[0], COST[1])
    for h in HS:
        e = f + h - 1
        okh = e < T
        ee = np.where(okh, e, 0)
        fund_pct = np.where(known[j], side * (fcum[ee, j] - fcum[t, j]) * 100,
                            mnr.FUNDING_FLOOR_PCT_PER_8H * h / 8)
        y[h] = np.where(okh, lab[f"r{h}"] - cost - fund_pct, np.nan).astype(np.float32)
        mkh = np.where(okh, mcum[ee] - mcum[f], np.nan)              # hedge from the fill bar's close
        hedged[h] = (y[h] - side * 100 * mkh).astype(np.float32)
    sig30 = np.sign(mcum - lag(mcum, 720))
    trend = sig30[t] == side

    G = gates(H, L, C)
    in_gate = np.zeros(len(t), bool)
    for gs, Gm in G.values():
        in_gate |= Gm[t, j] & (side == gs)
    ft = idx[f]
    out = dict(X=X, y=y, hedged=hedged, side=fl["side"], k=k, fill_time=ft.values, trend=trend,
               blk=((ft - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values, in_gate=in_gate)
    print(f"   {universe}: {N} coins, {T:,} 1h bars, {len(t):,} candidates ({np.bincount(k, minlength=4)} by k), "
          f"{in_gate.mean():.0%} inside a strategy gate, {trend.mean():.0%} with the 30d trend  [{time.time() - t0:.0f}s]", flush=True)
    gc.collect()
    return out


def control(D, E, h):
    """The positive control on the same candidates: TREND = side matches the market's 30d sign."""
    print(f"   positive control, H={h}h  (TREND net [t] · against-trend net · all · by year for TREND)")
    for u, X_ in (("bt", D), ("oos", E)):
        for side in (1, -1):
            y, b = X_["y"][h], X_["blk"]
            ok = (X_["side"] == side) & np.isfinite(y)
            tr = ok & X_["trend"]
            m, t, _ = day_t(y[tr], b[tr])
            yrs = pd.to_datetime(X_["fill_time"]).year
            by = "  ".join(f"{yy} {y[tr & (yrs == yy)].mean():+.2f}" for yy in sorted(np.unique(yrs[tr])))
            print(f"     {u:<4}{'long' if side > 0 else 'short':<6} TREND {m:+.3f} [{t:+4.1f}] (n {tr.sum():>7})  against "
                  f"{y[ok & ~X_['trend']].mean():+.3f}  all {y[ok].mean():+.3f}   {by}")


def by_year(D, E, h, R):
    print(f"   model-selected net by year, H={h}h:")
    for u, X_ in (("bt", D), ("oos", E)):
        p, y = R["pred"][u], X_["y"][h]
        yrs = pd.to_datetime(X_["fill_time"]).year
        for side in (1, -1):
            s_ = (X_["side"] == side) & np.isfinite(p) & np.isfinite(y) & (p > 0)
            print(f"     {u:<4}{'long' if side > 0 else 'short':<6}" + "  ".join(
                f"{yy} {y[s_ & (yrs == yy)].mean():+.2f} (n {(s_ & (yrs == yy)).sum()})" for yy in sorted(np.unique(yrs[s_]))))


def pool(X_: dict) -> list[str]:
    """Append side and side-signed directional features; RSI and breadth are centred first."""
    col = {n: i for i, n in enumerate(FEATURES)}
    s = X_["side"].astype(np.float32)[:, None]
    centre = {"rsi14": 50.0, "breadth": 0.5}
    extra = np.column_stack([s[:, 0]] + [s[:, 0] * (X_["X"][:, col[n]] - centre.get(n, 0.0)) for n in DIRECTIONAL])
    X_["X"] = np.hstack([X_["X"], extra.astype(np.float32)])
    X_["pooled"] = True
    return FEATURES + ["side"] + [f"s_{n}" for n in DIRECTIONAL]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--nulls", type=int, default=10)
    ap.add_argument("--pooled", action="store_true")
    a = ap.parse_args()
    rng = np.random.default_rng(20261010)
    print(f"building candidate sets (block A, 1h, touch fills, real funding){' — POOLED, side-signed' if a.pooled else ''} …", flush=True)
    D, E = build("backtest", rng), build("oos", rng)
    names = FEATURES
    if a.pooled:
        names = pool(D); pool(E)
    for h in HS:
        nn = a.nulls if h == PRIMARY else 0
        R = walk_forward(D, E, h, nn, rng, bar_min=60)
        report(D, E, h, R, nn, bar_min=60, primary=PRIMARY)
        control(D, E, h)
        by_year(D, E, h, R)
        if h == PRIMARY:
            importance(D, h, R, rng, names=names)
    print("\nTrials: 3 horizons (72h primary), 1 model spec, 1 rule, 1 control — fixed before the run. Block B untouched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
