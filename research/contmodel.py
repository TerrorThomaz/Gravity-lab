"""Continuation model: learned in-trade management for the structural-default grid.

The detector so far decided once, at entry, and the exit was a fixed rule. A continuation model
asks at every checkpoint of an OPEN position whether what is left of the trade is worth holding,
from indicators NOW, how they have CHANGED since entry ("momentum weakening while the rest still
looks fine"), and the trade's own state. It can only help if the post-entry path carries structure;
structural exits turning the grid from −43% to +15% says it does.

Fixed before the first run (2026-10-04):
  positions    the structural default's entries: k=3 one-bar rungs on 1h bars (rulepath engine,
               touch fill, maker entry), both sides. The SAME entries for every exit rule, so the
               comparison isolates the exit.
  baseline     the structural default's fixed exits: long TIME24, short TRAIL30 (rulepath labels).
  CONT exit    checkpoints every 4h after the fill (bars f+4, f+8, …, f+164). At each, EV of the next
               24h = P(next-24h return > 0)·W̄ − (1−P)·L̄. If EV < 0, exit at the next open (taker).
               Otherwise exit at the close of f+168 (taker).
  model        L2 logistic regression per side (IRLS, l2 = 0.001·n) on standardised, winsorised
               features:
                 - the 24 rulepath features at the checkpoint;
                 - deltas since the arming bar: Δz24, Δrsi14, Δbreadth, Δmz24, Δfund, Δadx14;
                 - trade state: unrealised P&L in ATRs, age (h), MFE so far in ATRs, giveback from
                   the MFE in ATRs.
               Label: side · ln(C[c+24] / C[c]) − funding over (c, c+24] > 0.
               Trained on BacktestCoins DISCOVERY fills of every k (k1-k3, 30k per side × 41
               checkpoints, sampled) with c+24 before 2024-01-01.
  validation   2024-01-01 → 2025-06-30, both universes, as a managed book (rulepath management:
               ≤ 1 open per coin/side, ≤ 12 per side, outcome-blind arrival order, 5% per position,
               realised P&L).
  nulls        5 refits with permuted labels; 5 HOLD-SHUFFLED books (CONT's holding times permuted
               across trades of the same side: same hold distribution, no information).
  pass         in BOTH universes: CONT Sharpe > baseline Sharpe, beats all 5 permuted and all 5
               hold-shuffled nulls, and the daily (CONT − baseline) P&L has t ≥ 2. One model, a
               low trial count, so t ≥ 2 on the improvement. Block B untouched.

Run:  python3 research/contmodel.py --selftest
      python3 research/contmodel.py
"""

from __future__ import annotations

import math
import os
import sys
import time

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rulepath as rp  # noqa: E402
from grid_paths import SEAL, atr, fills, mnr, sigma  # noqa: E402
from positioning_test import logit_fit  # noqa: E402

CK = list(range(4, 168, 4))          # checkpoints, hours after the fill bar
NEXT, HMAX = 24, 168
DELTA = ["z24", "rsi14", "breadth", "mz24", "fund", "adx14"]
STATE = ["upnl_atr", "age", "mfe_atr", "giveback_atr"]
NAMES = rp.FEATURES + [f"d_{n}" for n in DELTA] + STATE
NULLS = 5


def build(universe: str) -> dict:
    t0 = time.time()
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins"), rp.CACHE)
    keep = mk.close.index < SEAL
    idx = mk.close.index[keep]
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    V = mk.qvol.values[keep]
    rate, rmask = mk.funding.values[keep], mk.funding_mask.values[keep]
    known = mk.funding_known.reindex(mk.close.columns).fillna(False).values
    S, A = sigma(C), atr(H, L, C)
    fl = fills(O, H, L, C, A, S)
    fcum = np.cumsum(rate, axis=0)
    lab = rp.label_all(fl, A, O, H, L, C, fcum, known)
    U = dict(idx=idx, O=O, H=H, L=L, C=C, V=V, rate=rate, rmask=rmask, known=known, S=S, A=A, fl=fl, lab=lab,
             fcum=fcum, T=len(idx))
    print(f"   {universe}: {len(mk.close.columns)} coins, {len(fl['t']):,} rung fills  [{time.time() - t0:.0f}s]", flush=True)
    return U


def funding_pct(U, a, b, j, side):
    return np.where(U["known"][j], side * (U["fcum"][b, j] - U["fcum"][a, j]) * 100, mnr.FUNDING_FLOOR_PCT_PER_8H * (b - a) / 8)


def checkpoint_features(U, rows):
    """Feature matrix for (fill row × checkpoint). Returns X, the checkpoint bar, and the fill-row index."""
    fl, T = U["fl"], U["T"]
    f = fl["f"][rows]
    cb = (f[:, None] + np.array(CK)[None, :]).ravel()
    r = np.repeat(rows, len(CK))
    ok = cb + NEXT < T
    cb, r = cb[ok], r[ok]
    j, t, side, px = fl["j"][r], fl["t"][r], fl["side"][r].astype(float), fl["px"][r]
    Xc = rp.feature_matrix(U["idx"], U["O"], U["H"], U["L"], U["C"], U["V"], U["rmask"], U["rate"], cb, j, U["S"], U["A"])
    Xe = rp.feature_matrix(U["idx"], U["O"], U["H"], U["L"], U["C"], U["V"], U["rmask"], U["rate"], t, j, U["S"], U["A"])
    col = {n: i for i, n in enumerate(rp.FEATURES)}
    dX = np.column_stack([Xc[:, col[n]] - Xe[:, col[n]] for n in DELTA])
    atrp = U["A"][t, j] / U["C"][t, j]                                 # ATR as a fraction of price, at arming
    C, H, L = U["C"], U["H"], U["L"]
    upnl = side * (C[cb, j] / px - 1) / atrp
    # MFE from the fill bar's close to the checkpoint, on closes (path-honest, no intrabar guess)
    fbar = fl["f"][r]
    mfe = np.zeros(len(cb))
    for h in range(0, max(CK) + 1):
        b = fbar + h
        live = b <= cb
        if not live.any():
            break
        v = side * (C[np.minimum(b, len(C) - 1), j] / px - 1) / atrp
        mfe = np.where(live, np.fmax(mfe, v), mfe)
    st = np.column_stack([upnl, cb - fbar, mfe, mfe - upnl])
    X = np.hstack([Xc, dX, st]).astype(np.float32)
    nxt = side * 100 * np.log(C[cb + NEXT, j] / C[cb, j]) - funding_pct(U, cb, cb + NEXT, j, side)
    return X, cb, r, nxt


def fit(X, y_ret, rng, permute=False):
    lo, hi = np.nanquantile(X, 0.01, axis=0), np.nanquantile(X, 0.99, axis=0)
    Z = np.clip(X.astype(float), lo, hi)
    mu, sd = np.nanmean(Z, 0), np.nanstd(Z, 0) + 1e-9
    Z = np.nan_to_num((Z - mu) / sd)
    y = (y_ret > 0).astype(float)
    if permute:
        y = rng.permutation(y)
    beta = logit_fit(np.column_stack([np.ones(len(Z)), Z]), y, l2=0.001 * len(Z))
    return dict(beta=beta, lo=lo, hi=hi, mu=mu, sd=sd, W=float(y_ret[y_ret > 0].mean()), L=float(-y_ret[y_ret <= 0].mean()))


def ev(m, X):
    Z = np.nan_to_num((np.clip(X.astype(float), m["lo"], m["hi"]) - m["mu"]) / m["sd"])
    p = 1 / (1 + np.exp(-np.clip(m["beta"][0] + Z @ m["beta"][1:], -30, 30)))
    return p * m["W"] - (1 - p) * m["L"]


def decide_exit(n_rows, r, cb, evs):
    """First checkpoint with EV < 0 per fill row → exit bar = that checkpoint + 1 (next open); else −1 (hold to max)."""
    out = np.full(n_rows, -1, dtype=np.int64)
    neg = evs < 0
    if neg.any():
        df = pd.DataFrame({"r": r[neg], "cb": cb[neg]}).groupby("r").cb.min()
        out[df.index.values] = df.values + 1
    return out


def cont_trades(U, rows, exit_open_bar, hold_override=None):
    """(f, xbar, j, side, net) for each fill row under the continuation exit (or explicit hold lengths)."""
    fl, T, O, C = U["fl"], U["T"], U["O"], U["C"]
    f, j, side, px = fl["f"][rows], fl["j"][rows], fl["side"][rows].astype(float), fl["px"][rows]
    if hold_override is not None:
        xb = np.minimum(f + hold_override, T - 1); xpx = C[xb, j]
    else:
        e = exit_open_bar
        early = e >= 0
        xb = np.where(early, e, f + HMAX)
        ok = xb < T
        xb = np.where(ok, xb, T - 1)
        xpx = np.where(early, O[xb, j], C[xb, j])
        xpx = np.where(ok, xpx, np.nan)
    net = side * 100 * np.log(xpx / px) - rp.MAKER - rp.TAKER - funding_pct(U, f, xb, j, side)
    return [(int(a), int(b), int(c), int(d), float(n)) for a, b, c, d, n in zip(f, xb, j, side, net) if np.isfinite(n)], xb - f


def manage(trades, idx, T):
    trades = sorted(trades, key=lambda x: (x[0], x[2], x[3]))          # outcome-blind arrival order
    open_until, open_side, pnl, n = {}, {1: [], -1: []}, np.zeros(T), 0
    for f, xbar, jj, side, net in trades:
        if open_until.get((jj, side), -1) >= f:
            continue
        open_side[side] = [x for x in open_side[side] if x >= f]
        if len(open_side[side]) >= rp.CAP:
            continue
        open_until[(jj, side)] = xbar; open_side[side].append(xbar)
        pnl[min(xbar, T - 1)] += rp.SLOT * net / 100; n += 1
    v1 = int(np.searchsorted(idx, SEAL)) - HMAX - 2
    d = pd.Series(pnl, index=idx).resample("1D").sum()
    return d[(d.index >= rp.D_END) & (d.index < idx[v1] + pd.Timedelta(days=8))], n


def validation_rows(U):
    fl = U["fl"]
    v0 = int(np.searchsorted(U["idx"], rp.D_END)); v1 = int(np.searchsorted(U["idx"], SEAL)) - HMAX - 2
    return np.where((fl["k"] == 3) & (fl["t"] >= v0) & (fl["t"] < v1))[0]


def baseline_trades(U, rows):
    fl = U["fl"]
    out = []
    for r in rows:
        e = "TIME24" if fl["side"][r] > 0 else "TRAIL30"
        net, xb = U["lab"][e]
        if np.isfinite(net[r]) and xb[r] >= 0:
            out.append((int(fl["f"][r]), int(xb[r]), int(fl["j"][r]), int(fl["side"][r]), float(net[r])))
    return out


def selftest() -> int:
    r = np.array([0, 0, 0, 1, 1, 2]); cb = np.array([14, 18, 22, 30, 34, 50]); evs = np.array([+1, -1, -1, +1, +2, +1.0])
    out = decide_exit(3, r, cb, evs)
    assert out.tolist() == [19, -1, -1], out                            # row 0 exits after its first negative checkpoint; others hold
    print("selftest: OK (first negative-EV checkpoint → next-open exit; never-negative rows hold to max)")
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    rng = np.random.default_rng(20261013)
    print("continuation model — building …", flush=True)
    D, E = build("backtest"), build("oos")
    d_end = int(np.searchsorted(D["idx"], rp.D_END))
    models = {}
    for side in (1, -1):
        cand = np.where((D["fl"]["side"] == side) & (D["fl"]["f"] + HMAX + NEXT < d_end))[0]
        rows = rng.choice(cand, min(30_000, len(cand)), replace=False)
        X, cb, r, nxt = checkpoint_features(D, rows)
        ok = np.isfinite(nxt) & (cb + NEXT < d_end)
        models[side] = fit(X[ok], nxt[ok], rng)
        b = models[side]["beta"][1:]
        top = np.argsort(-np.abs(b))[:8]
        print(f"\n   {'LONG' if side > 0 else 'SHORT'} continuation model: {ok.sum():,} checkpoints, next-24h win {np.mean(nxt[ok] > 0):.0%}")
        print("   derived management rules: " + "  ".join(f"{'+' if b[i] > 0 else '−'}{NAMES[i]}({b[i]:+.2f})" for i in top))

    print("\n── VALIDATION (2024-01 → 2025-06): structural-default entries, baseline exits vs the continuation exit ──")
    verdict = []
    for un, U in (("bt", D), ("oos", E)):
        rows = validation_rows(U)
        X, cb, r_ix, _ = checkpoint_features(U, rows)
        side_of = U["fl"]["side"][r_ix]
        evs = np.where(side_of > 0, ev(models[1], X), ev(models[-1], X))
        pos = {v: i for i, v in enumerate(rows)}
        rr = np.array([pos[v] for v in r_ix])
        ex = decide_exit(len(rows), rr, cb, evs)
        tc, holds = cont_trades(U, rows, ex)
        dC, nC = manage(tc, U["idx"], U["T"])
        dB, nB = manage(baseline_trades(U, rows), U["idx"], U["T"])
        sC, tC, _, aC = rp.sharpe_stats(dC); sB, tB, _, aB = rp.sharpe_stats(dB)
        diff = (dC - dB).fillna(0)
        _, tD, _, _ = rp.sharpe_stats(diff)
        print(f"   {un:<4} BASELINE trades {nB:>5}  ann {aB:+6.1f}%  Sharpe {sB:+5.2f}")
        print(f"   {un:<4} CONT     trades {nC:>5}  ann {aC:+6.1f}%  Sharpe {sC:+5.2f}   median hold {np.median(holds):.0f}h "
              f"(exited early {np.mean(ex >= 0):.0%})   CONT − BASELINE daily t {tD:+.2f}")
        pn = []
        for z in range(NULLS):
            mz = {}
            for side in (1, -1):
                cand = np.where((D["fl"]["side"] == side) & (D["fl"]["f"] + HMAX + NEXT < d_end))[0]
                rws = np.random.default_rng(200 + z).choice(cand, min(30_000, len(cand)), replace=False)
                Xd, cbd, _, nx = checkpoint_features(D, rws)
                okd = np.isfinite(nx) & (cbd + NEXT < d_end)
                mz[side] = fit(Xd[okd], nx[okd], np.random.default_rng(300 + z), permute=True)
            evz = np.where(side_of > 0, ev(mz[1], X), ev(mz[-1], X))
            pn.append(rp.sharpe_stats(manage(cont_trades(U, rows, decide_exit(len(rows), rr, cb, evz))[0], U["idx"], U["T"])[0])[0])
        hs = []
        sides = U["fl"]["side"][rows]
        for z in range(NULLS):
            hz = holds.copy()
            g = np.random.default_rng(400 + z)
            for s in (1, -1):
                m = np.where(sides == s)[0]
                hz[m] = hz[g.permutation(m)]
            hs.append(rp.sharpe_stats(manage(cont_trades(U, rows, None, hold_override=hz)[0], U["idx"], U["T"])[0])[0])
        bp, bh = sum(sC > x for x in pn), sum(sC > x for x in hs)
        print(f"        permuted-label nulls Sharpe {np.round(pn, 2).tolist()} → beats {bp}/{NULLS}")
        print(f"        hold-shuffled nulls  Sharpe {np.round(hs, 2).tolist()} → beats {bh}/{NULLS}")
        ok = sC > sB and bp == NULLS and bh == NULLS and tD >= 2
        verdict.append(ok)
        print(f"        → {un} {'PASS' if ok else 'FAIL'}")
    print(f"\nVERDICT: {'PASS' if all(verdict) else 'FAIL'} (both universes required). Block B untouched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
