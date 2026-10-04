"""Rule-path v2: authority costs + risk-aware decisions, then export the overlay's decisions.

The v1 overlay failed for two reasons nothing in the pipeline could see (post-mortem, 2026-10-04):
  1. RISK was never in the objective. Deep rungs fill in flushes, so they are one correlated bet.
  2. COST MISMATCH. The labels used flat costs and real funding, while the authority charges
     ATR-scaled slippage, a stop gap and the funding floor. The EV was optimistic for high-ATR deep
     rungs.

Fixed before the first run (2026-10-04):
  labels     k=3 one-bar rungs only (the overlay's rung), each exit simulated EXACTLY like
             StructGridSimulator.GetOverlayReturns: intrabar hard stop first (the GA genotype's
             HardStopAtrMult, long 3.0 / short 2.958, at level-or-open, taker + gap), close rules →
             next open, TP maker, max-hold close, simple returns. Costs = research/tradecosts.py
             (the C# parity-tested mirror), funding = the floor (as edgetest charges the grid family).
  features   the 24 rulepath features + mz1, mz4 (market move over the last 1h / 4h, z) + prevk3
             (same-side k=3 fills during the arming bar across the universe: correlated exposure,
             known at its close).
  model      per side × exit (10): an L2 logistic regression (as rulepath), trained on BacktestCoins
             fills whose label ended before 2024-01-01.
  decision   EV(x) = P·W̄ − (1−P)·L̄. Arm the exit with the highest EV among those with
             EV ≥ κ·σ_type, κ = 0.1 (σ_type = that type's discovery std of net). The overlay must
             predict a per-trade Sharpe of at least ~0.1, about the GA grid's own 0.13, so it cannot
             dilute the book. SHORTS: never armed when prevk3 ≥ 3. That is the one cluster rule with
             the same sign in discovery and 2024+, both universes (shorting into a cascade loses).
  exposure   overlay caps 4 per side in edgetest (were 12).
  window     decisions only for arming bars ≥ 2024-01-01 (no discovery leakage via market features).
  out        reports/rulepath2_models.json, reports/overlay_decisions_v2_<universe>.csv

Run:  python3 research/rulepath2.py --selftest
      python3 research/rulepath2.py
"""

from __future__ import annotations

import json
import os
import sys
import time

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rulepath as rp  # noqa: E402
import tradecosts as tc  # noqa: E402
from grid_paths import THROUGH, atr, fills, mnr, sigma  # noqa: E402

HARD = {1: 3.0, -1: 2.958215799163196}            # GA genotypes' HardStopAtrMult (grid_best / grid_short)
KAPPA, CASCADE = 0.1, 3
START = pd.Timestamp("2024-01-01")
EXTRA = ["mz1", "mz4", "prevk3"]
NAMES = rp.FEATURES + EXTRA


def exit_overlay(kind, hmax, tp, sla, side, f, j, px, a, hard_mult, O, H, L, C):
    """Vectorised mirror of StructGridSimulator.Run's exit loop. Returns gross % (simple), exit bar, intrabar-stop, maker."""
    T, n = C.shape[0], len(f)
    s = side.astype(float)
    done = np.zeros(n, bool); xpx = np.full(n, np.nan); xb = np.full(n, -1, np.int64)
    stopf = np.zeros(n, bool); mk = np.zeros(n, bool)
    hard = px - s * hard_mult * a
    tgt = px + s * tp * a
    ext = C[np.minimum(f, T - 1), j]
    stop = ext - s * sla * a if kind == "trail" else px - s * sla * a
    for h in range(1, hmax + 1):
        b = f + h
        live = ~done & (b < T)
        if not live.any():
            break
        bi = np.where(live, b, 0)
        o, hi, lo, c = O[bi, j], H[bi, j], L[bi, j], C[bi, j]
        hit = live & np.where(s > 0, lo <= hard, hi >= hard)
        xpx[hit] = np.where(s > 0, np.minimum(o, hard), np.maximum(o, hard))[hit]; xb[hit] = bi[hit]; stopf[hit] = True
        done |= hit; live &= ~hit
        if kind != "time":
            cl = live & np.where(s > 0, c <= stop, c >= stop)
            nx = cl & (bi + 1 < T)
            xpx[nx] = O[np.minimum(bi + 1, T - 1), j][nx]; xb[nx] = bi[nx] + 1
            done |= cl; live &= ~cl                       # no next bar → unlabelled (xb stays −1)
        if kind == "tpsl":
            tph = live & np.where(s > 0, hi >= tgt * (1 + THROUGH), lo <= tgt * (1 - THROUGH))
            xpx[tph] = tgt[tph]; xb[tph] = bi[tph]; mk[tph] = True
            done |= tph; live &= ~tph
        if kind == "trail":
            ext = np.where(live, np.where(s > 0, np.fmax(ext, c), np.fmin(ext, c)), ext)
            stop = np.where(live, np.where(s > 0, np.fmax(stop, ext - sla * a), np.fmin(stop, ext + sla * a)), stop)
        if h == hmax:
            last = live
            xpx[last] = c[last]; xb[last] = bi[last]; done |= last
    gross = s * (xpx / px - 1.0) * 100.0
    return gross, xb, stopf, mk


def build(universe: str) -> dict:
    t0 = time.time()
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins"), rp.CACHE)
    idx, names = mk.close.index, list(mk.close.columns)          # full history (decisions run to the data end)
    O, H, L, C, V = mk.open.values, mk.high.values, mk.low.values, mk.close.values, mk.qvol.values
    S, A = sigma(C), atr(H, L, C)
    fl = fills(O, H, L, C, A, S)
    k3 = fl["k"] == 3
    fl = {k: v[k3] for k, v in fl.items()}
    t, j, f, side, px = fl["t"], fl["j"], fl["f"], fl["side"], fl["px"]
    a = A[t, j]
    ns = idx.values.astype("datetime64[ns]").astype(np.int64)
    lab = {}
    for e, (kind, hmax, tp, sla) in rp.EXITS.items():
        g = np.full(len(t), np.nan); xb_all = np.full(len(t), -1, np.int64)
        for sd in (1, -1):
            w = side == sd
            gg, xb, st, mkr = exit_overlay(kind, hmax, tp or 0.0, sla or 0.0, side[w], f[w], j[w], px[w], a[w], HARD[sd], O, H, L, C)
            ok = xb >= 0
            xbc = np.where(ok, xb, 0)
            cost = tc.round_trip_pct(tc.atr_pct(a[w], px[w]), st, entry_maker=True, exit_maker=mkr)
            fund = tc.funding_floor_pct(ns[f[w]], ns[xbc])
            g[w] = np.where(ok, gg - cost + fund, np.nan); xb_all[w] = xb
        lab[e] = (g, xb_all)
    X = rp.feature_matrix(idx, O, H, L, C, V, mk.funding_mask.values, mk.funding.values, t, j, S, A)
    with np.errstate(invalid="ignore", divide="ignore"):
        mlr = np.nanmean(np.diff(np.log(C), axis=0, prepend=np.nan), axis=1)
    mcum = np.nancumsum(np.nan_to_num(mlr))
    msig = pd.Series(mlr).rolling(168, min_periods=84).std().values
    lag = lambda x, n: np.concatenate([np.full(n, np.nan), x[:-n]])
    mz1 = (mcum - lag(mcum, 1)) / msig
    mz4 = (mcum - lag(mcum, 4)) / (msig * 2.0)
    prev = {sd: np.bincount(f[side == sd], minlength=len(idx)) for sd in (1, -1)}   # same-side k3 fills per bar
    prevk3 = np.where(side > 0, prev[1][t], prev[-1][t])                          # fills DURING the arming bar t
    X = np.hstack([X, np.column_stack([mz1[t], mz4[t], prevk3]).astype(np.float32)])
    print(f"   {universe}: {len(names)} coins, {len(t):,} k3 fills (full history) labelled under authority costs  [{time.time() - t0:.0f}s]", flush=True)
    return dict(idx=idx, names=names, t=t, j=j, f=f, side=side, lab=lab, X=X, T=len(idx))


def fit(D, rng):
    d_end = int(np.searchsorted(D["idx"], rp.D_END))
    models = {}
    for sd in (1, -1):
        for e in rp.EXITS:
            net, xb = D["lab"][e]
            ii = np.where((D["side"] == sd) & np.isfinite(net) & (xb >= 0) & (xb < d_end))[0]
            Xi = D["X"][ii].astype(float)
            lo, hi = np.nanquantile(Xi, 0.01, axis=0), np.nanquantile(Xi, 0.99, axis=0)
            Z = np.clip(Xi, lo, hi); mu, sdv = np.nanmean(Z, 0), np.nanstd(Z, 0) + 1e-9
            Z = np.nan_to_num((Z - mu) / sdv)
            y = (net[ii] > 0).astype(float)
            beta = rp.logit_fit(np.column_stack([np.ones(len(Z)), Z]), y, l2=0.001 * len(Z))
            r = net[ii]
            models[(sd, e)] = dict(beta=beta, lo=lo, hi=hi, mu=mu, sd=sdv, W=float(r[r > 0].mean()), L=float(-r[r <= 0].mean()),
                                   sigma=float(r.std()), mean=float(r.mean()), n=len(ii), win=float(y.mean()))
    return models


def decisions_fast(D, models):
    out = []
    sel = D["idx"][D["t"]] >= START
    exits = list(rp.EXITS)
    for sd in (1, -1):
        w = np.where(sel & (D["side"] == sd))[0]
        if not len(w):
            continue
        X = D["X"][w]
        ev = np.column_stack([rp.ev(models[(sd, e)], X) for e in exits])
        thr = np.array([KAPPA * models[(sd, e)]["sigma"] for e in exits])
        ok = ev >= thr[None, :]
        best = np.where(ok, ev, -np.inf).argmax(1)
        arm = ok.any(1)
        if sd < 0:
            arm &= X[:, NAMES.index("prevk3")] < CASCADE
        df = pd.DataFrame({"symbol": [D["names"][x] for x in D["j"][w][arm]], "arm_time": D["idx"][D["t"][w][arm]],
                           "side": sd, "exit": [exits[b] for b in best[arm]], "ev": np.round(ev[arm, best[arm]], 4)})
        out.append((df, len(w)))
    return out


def selftest() -> int:
    """Mirror check against StructGridSimulator's hand-built cases (StructGridTests): TP and hard stop."""
    T = 80
    O = np.full((T, 1), 100.0); H = O + 0.5; L = O - 0.5; C = O.copy()
    L[40, 0] = 96.9                                                  # fill at 97 (a = 1)
    O[42, 0], H[42, 0], L[42, 0], C[42, 0] = 97.5, 98.2, 97.4, 97.8  # TP 98 touched (TPSL1)
    O[41, 0], H[41, 0], L[41, 0], C[41, 0] = 97.2, 97.6, 97.0, 97.3
    g, xb, st, mk = exit_overlay("tpsl", 24, 1.0, 1.0, np.array([1]), np.array([40]), np.array([0]), np.array([97.0]),
                                 np.array([1.0]), 3.0, O, H, L, C)
    assert xb[0] == 42 and mk[0] and not st[0] and abs(g[0] - (98 / 97 - 1) * 100) < 1e-9, (g, xb, mk)
    O[41, 0], H[41, 0], L[41, 0], C[41, 0] = 96.5, 96.8, 93.5, 96.0  # wick through the hard stop 94
    g, xb, st, mk = exit_overlay("time", 24, 0.0, 0.0, np.array([1]), np.array([40]), np.array([0]), np.array([97.0]),
                                 np.array([1.0]), 3.0, O, H, L, C)
    assert xb[0] == 41 and st[0] and abs(g[0] - (94 / 97 - 1) * 100) < 1e-9, (g, xb, st)
    print("rulepath2 selftest: OK (TP maker exit and intrabar hard stop mirror StructGridSimulator)")
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    rng = np.random.default_rng(20261014)
    print("rule-path v2 (authority costs, hard stop, risk-aware decisions) …", flush=True)
    D, E = build("backtest"), build("oos")
    models = fit(D, rng)
    print("\n── DISCOVERY (BacktestCoins < 2024), k3 rungs under AUTHORITY costs + GA hard stop ──")
    for (sd, e), m in models.items():
        b = m["beta"][1:]; top = np.argsort(-np.abs(b))[:4]
        print(f"   {'L' if sd > 0 else 'S'} {e:<8} n {m['n']:>6}  win {m['win']:.0%}  mean {m['mean']:+.3f}%  σ {m['sigma']:.2f}  "
              f"per-trade SR {m['mean'] / m['sigma']:+.3f}   " + "  ".join(f"{'+' if b[i] > 0 else '−'}{NAMES[i]}({b[i]:+.2f})" for i in top))
    json.dump({f"{sd}|3|{e}": {"features": NAMES, **{k: (v.tolist() if isinstance(v, np.ndarray) else v) for k, v in m.items()}}
               for (sd, e), m in models.items()}, open(os.path.join(mnr.REPO, "reports", "rulepath2_models.json"), "w"))
    for un, U in (("backtest", D), ("oos", E)):
        parts = decisions_fast(U, models)
        df = pd.concat([p for p, _ in parts]).sort_values(["symbol", "arm_time"])
        total = sum(n for _, n in parts)
        out = os.path.join(mnr.REPO, "reports", f"overlay_decisions_v2_{un}.csv")
        df.to_csv(out, index=False, date_format="%Y-%m-%d %H:%M:%S")
        print(f"\n   {un}: {total:,} k3 fills from {START:%Y-%m-%d}; armed {len(df):,} ({len(df) / max(total, 1):.0%}) — "
              f"long {int((df.side > 0).sum())}, short {int((df.side < 0).sum())}; exits {df.exit.value_counts().to_dict()} → {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
