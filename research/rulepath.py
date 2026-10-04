"""Rule-path edge detector, with a logistic regression that drives an ALWAYS-ON dynamic grid.

Earlier searches scored indicator sets with a FIXED exit. A strategy is gate × exit rule × position
management, and a path-dependent exit can harvest serial structure that a fixed horizon dilutes.
This scores every grid rung under a family of path exits, derives rules (logistic coefficients) on
a discovery period, and validates them as a managed BOOK.

Fixed before the first run (2026-10-04):
  candidates   1h bars, one-bar grid rungs at close_t ∓ k·ATR14 (k = 1, 2, 3), one row per rung,
               touch fill (the user's call), maker entry 0.02%.
  exits        (monitoring starts the bar after the fill; stops trigger on CLOSES and exit at the
                next open, taker; TPs are resting limits filled when price trades 5bp through, maker;
                if a bar both closes beyond the SL and touches the TP, the SL is assumed):
                 TIME24   exit at the close 24h after the fill bar
                 TPSL1    TP +1·ATR, SL −1·ATR, max 24h
                 TPSL2    TP +2·ATR, SL −1·ATR, max 72h
                 TRAIL15  trailing stop 1.5·ATR from the extreme close, max 168h
                 TRAIL30  trailing stop 3.0·ATR from the extreme close, max 168h
               Net = side·return − costs − real funding (floor where missing). ATR = the arming bar's.
  rung types   2 sides × 3 k × 5 exits = 30.
  periods      DISCOVERY: fills before 2024-01-01 (label ended before then). VALIDATION: 2024-01-01 →
               2025-06-30, minus the longest exit. Block B is not touched.
  model        per rung type: an L2 logistic regression (IRLS, l2 = 0.001·n) on standardised,
               winsorised (1/99%) features → P(net > 0). EV = P·W̄ − (1−P)·L̄, with W̄ / L̄ the mean
               win / loss of that type in discovery. Trained on BacktestCoins discovery only.
  policies     at each bar close, per coin and side, the rung type with the highest EV is armed
               (one per side):
                 ALWAYS   always armed (the user's design: confidence moves the rung, never removes it)
                 GATED    armed only if that EV > 0
               Position management: at most one open position per coin and side; nothing new is armed
               on a coin/side while it is open; ≤ 12 open per side (arrival order); 5% of capital per
               position; P&L booked at exit (realised; a stated simplification).
  baselines    STATIC grid (always k=1 with TPSL1, both sides); RANDOM rung type each bar; and
               5 refits with permuted labels (NULLS).
  significance the book's DAILY P&L: annualised Sharpe, t, deflated Sharpe at N = 32 trials.
  pass         ALWAYS on validation, BOTH universes: Sharpe > 0, > STATIC and > RANDOM, beats all
               5 nulls, daily t ≥ 3 (the searched-result bar).

Run:  python3 research/rulepath.py --selftest
      python3 research/rulepath.py
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time

import numpy as np
import pandas as pd
from scipy.stats import norm

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from context_gate import adx, ema, rsi  # noqa: E402
from grid_paths import KS, SEAL, THROUGH, atr, fills, mnr, sigma  # noqa: E402
from positioning_test import logit_fit  # noqa: E402

D_END = pd.Timestamp("2024-01-01")
EXITS = {"TIME24": ("time", 24, None, None), "TPSL1": ("tpsl", 24, 1.0, 1.0), "TPSL2": ("tpsl", 72, 2.0, 1.0),
         "TRAIL15": ("trail", 168, None, 1.5), "TRAIL30": ("trail", 168, None, 3.0)}
HMAX = 168
MAKER, TAKER = mnr.MAKER_COST_PER_SIDE * 100, mnr.TAKER_COST_PER_SIDE * 100
SLOT, CAP, NULLS, N_TRIALS = 0.05, 12, 5, 32
FEATURES = ["z1", "z4", "z24", "z168", "z720", "res24", "res168", "d_ema50", "d_ema200", "rsi14", "adx14",
            "lbbw", "latr", "hi720", "lo720", "vz", "fund", "mz24", "mz168", "mz720", "mdd2160", "mvol",
            "breadth", "disp24"]
CACHE = os.path.join(mnr.REPO, "candle_cache")


# ── exits on the real path ───────────────────────────────────────────────────────────────────────
def exit_paths(kind, hmax, tp, sla, side, f, j, px, a, O, H, L, C):
    T = C.shape[0]
    n = len(f)
    s = side.astype(float)
    done = np.zeros(n, bool); xpx = np.full(n, np.nan); xbar = np.full(n, -1, dtype=np.int64); mk = np.zeros(n, bool)
    ext = np.where(f < T, C[np.minimum(f, T - 1), j], np.nan)           # extreme close, starting at the fill bar's close
    stop = ext - s * sla * a if kind == "trail" else px - s * (sla if kind == "tpsl" else 0) * a
    tgt = px + s * (tp or 0) * a
    for h in range(1, hmax + 1):
        b = f + h
        live = ~done & (b < T)
        if not live.any():
            break
        bi = np.where(live, b, 0)
        c, hi, lo = C[bi, j], H[bi, j], L[bi, j]
        nxt_ok = bi + 1 < T
        o_next = np.where(nxt_ok, O[np.minimum(bi + 1, T - 1), j], np.nan)
        if kind in ("tpsl", "trail"):
            hit_sl = live & np.where(s > 0, c <= stop, c >= stop) & np.isfinite(c)
            xpx[hit_sl] = o_next[hit_sl]; xbar[hit_sl] = bi[hit_sl] + 1; done |= hit_sl
            live &= ~hit_sl
        if kind == "tpsl":
            hit_tp = live & np.where(s > 0, hi >= tgt * (1 + THROUGH), lo <= tgt * (1 - THROUGH))
            xpx[hit_tp] = tgt[hit_tp]; xbar[hit_tp] = bi[hit_tp]; mk[hit_tp] = True; done |= hit_tp
            live &= ~hit_tp
        if kind == "trail":
            ext = np.where(live & np.isfinite(c), np.where(s > 0, np.fmax(ext, c), np.fmin(ext, c)), ext)
            stop = np.where(live, np.where(s > 0, np.fmax(stop, ext - sla * a), np.fmin(stop, ext + sla * a)), stop)
        if h == hmax:
            last = live & np.isfinite(c)
            xpx[last] = c[last]; xbar[last] = bi[last]; done |= last
    gross = 100 * s * np.log(xpx / px)
    return gross, xbar, mk


def label_all(fl, A, O, H, L, C, fcum, known):
    """Net % for every candidate under every exit; also the exit bar (for position management)."""
    f, j, side, px = fl["f"], fl["j"], fl["side"], fl["px"]
    a = A[fl["t"], j]
    out = {}
    for name, (kind, hmax, tp, sla) in EXITS.items():
        g, xb, mk = exit_paths(kind, hmax, tp, sla, side, f, j, px, a, O, H, L, C)
        xbc = np.where(xb >= 0, xb, 0)
        fund = np.where(known[j], side * (fcum[xbc, j] - fcum[f, j]) * 100, mnr.FUNDING_FLOOR_PCT_PER_8H * (xbc - f) / 8)
        net = g - MAKER - np.where(mk, MAKER, TAKER) - fund
        out[name] = (np.where(xb >= 0, net, np.nan), xb)
    return out


# ── features at the arming bar ───────────────────────────────────────────────────────────────────
def feature_matrix(idx, O, H, L, C, V, rate_mask, rate, t, j, S, A):
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
        lr = np.diff(lc, axis=0, prepend=np.nan)
    mlr = np.nanmean(lr, axis=1)
    mcum = np.nancumsum(np.nan_to_num(mlr))
    msig = pd.Series(mlr).rolling(168, min_periods=84).std().values
    lag = lambda X, n: np.concatenate([np.full((n,) + X.shape[1:], np.nan), X[:-n]])
    X = np.full((len(t), len(FEATURES)), np.nan, dtype=np.float32)
    col = {n: i for i, n in enumerate(FEATURES)}
    put = lambda name, arr: X.__setitem__((slice(None), col[name]), arr[t, j] if arr.ndim == 2 else arr[t])
    for n in (1, 4, 24, 168, 720):
        put(f"z{n}", (lc - lag(lc, n)) / (S * np.sqrt(n)))
    for n in (24, 168):
        put(f"res{n}", ((lc - lag(lc, n)) - (mcum - lag(mcum, n))[:, None]) / (S * np.sqrt(n)))
    put("d_ema50", (C - ema(C, 50)) / A); put("d_ema200", (C - ema(C, 200)) / A)
    put("rsi14", rsi(C)); put("adx14", adx(H, L, C))
    Cd = pd.DataFrame(C)
    with np.errstate(invalid="ignore", divide="ignore"):
        put("lbbw", np.log(4 * Cd.rolling(20).std().values / Cd.rolling(20).mean().values))
        put("latr", np.log(A / atr(H, L, C, 100)))
        put("hi720", (lc - np.log(Cd.rolling(720, min_periods=360).max().values)) / S)
        put("lo720", (lc - np.log(Cd.rolling(720, min_periods=360).min().values)) / S)
        lq = np.log1p(V)
    put("vz", lq - pd.DataFrame(lq).rolling(168, min_periods=84).mean().values)
    put("fund", pd.DataFrame(np.where(rate_mask, rate, np.nan)).ffill().values * 1e4)
    for n in (24, 168, 720):
        put(f"mz{n}", (mcum - lag(mcum, n)) / (msig * np.sqrt(n)))
    put("mdd2160", mcum - pd.Series(mcum).rolling(2160, min_periods=720).max().values)
    mv = pd.Series(mlr)
    put("mvol", np.log(mv.rolling(168).std() / mv.rolling(2160).std()).values)
    e720 = ema(C, 720)
    put("breadth", np.nanmean(np.where(np.isfinite(C) & np.isfinite(e720), C > e720, np.nan), axis=1))
    put("disp24", np.nanstd((lc - lag(lc, 24)) / (S * np.sqrt(24)), axis=1))
    return X


def build(universe: str) -> dict:
    t0 = time.time()
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins"), CACHE)
    keep = mk.close.index < SEAL
    idx = mk.close.index[keep]
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    V = mk.qvol.values[keep]
    rate, rmask = mk.funding.values[keep], mk.funding_mask.values[keep]
    known = mk.funding_known.reindex(mk.close.columns).fillna(False).values
    S, A = sigma(C), atr(H, L, C)
    fl = fills(O, H, L, C, A, S)
    lab = label_all(fl, A, O, H, L, C, np.cumsum(rate, axis=0), known)
    X = feature_matrix(idx, O, H, L, C, V, rmask, rate, fl["t"], fl["j"], S, A)
    print(f"   {universe}: {len(mk.close.columns)} coins, {len(fl['t']):,} rung fills × {len(EXITS)} exits labelled  [{time.time() - t0:.0f}s]", flush=True)
    return dict(idx=idx, fl=fl, lab=lab, X=X, T=len(idx))


# ── models ───────────────────────────────────────────────────────────────────────────────────────
def rung_types():
    return [(s, k, e) for s in (1, -1) for k in KS for e in EXITS]


def fit_models(D, rng, permute=False):
    fl, X, idx = D["fl"], D["X"], D["idx"]
    ft = idx[np.minimum(fl["f"], D["T"] - 1)]
    models = {}
    for s, k, e in rung_types():
        net, xb = D["lab"][e]
        ok = (fl["side"] == s) & (fl["k"] == k) & np.isfinite(net) & (xb >= 0)
        ok &= idx[np.minimum(np.maximum(xb, 0), D["T"] - 1)] < D_END            # label ended inside discovery
        ii = np.where(ok)[0]
        if len(ii) > 200_000:
            ii = rng.choice(ii, 200_000, replace=False)
        Xi = X[ii].astype(float)
        lo, hi = np.nanquantile(Xi, 0.01, axis=0), np.nanquantile(Xi, 0.99, axis=0)
        Xi = np.clip(Xi, lo, hi)
        mu, sd = np.nanmean(Xi, 0), np.nanstd(Xi, 0) + 1e-9
        Z = np.nan_to_num((Xi - mu) / sd)
        y = (net[ii] > 0).astype(float)
        if permute:
            y = rng.permutation(y)
        beta = logit_fit(np.column_stack([np.ones(len(Z)), Z]), y, l2=0.001 * len(Z))
        w, l = net[ii][net[ii] > 0].mean(), -net[ii][net[ii] <= 0].mean()
        models[(s, k, e)] = dict(beta=beta, lo=lo, hi=hi, mu=mu, sd=sd, W=w, L=l, n=len(ii),
                                 base=float((net[ii] > 0).mean()), mean=float(net[ii].mean()))
    return models


def ev(m, X):
    Z = np.nan_to_num((np.clip(X.astype(float), m["lo"], m["hi"]) - m["mu"]) / m["sd"])
    p = 1 / (1 + np.exp(-np.clip(m["beta"][0] + Z @ m["beta"][1:], -30, 30)))
    return p * m["W"] - (1 - p) * m["L"]


# ── the dynamic-grid book on the validation period ───────────────────────────────────────────────
def policy_book(D, models, mode, rng=None):
    """mode: ALWAYS | GATED | STATIC | RANDOM. Returns the daily P&L series (fraction of capital)."""
    fl, X, idx, T = D["fl"], D["X"], D["idx"], D["T"]
    v0 = int(np.searchsorted(idx, D_END))
    v1 = int(np.searchsorted(idx, SEAL)) - HMAX - 2
    types = rung_types()
    # EV of every rung type at every candidate's ARMING bar: the decision is per (t, j, side), and a
    # candidate exists only where that rung actually filled. Choose per (t, j, side), then check the fill.
    sel = (fl["t"] >= v0) & (fl["t"] < v1)
    ci = np.where(sel)[0]
    t, j, s, k = fl["t"][ci], fl["j"][ci], fl["side"][ci], fl["k"][ci]
    # the features at (t, j) are identical for every k/side; compute EVs once per candidate row
    evs = np.full((len(ci), len(types)), -np.inf)
    if mode in ("ALWAYS", "GATED"):
        for ti, (ss, kk, e) in enumerate(types):
            m = (s == ss)
            evs[m, ti] = ev(models[(ss, kk, e)], X[ci[m]])
    # choice per (t, j, side): among this side's 15 types; the chosen k must be the rung that filled
    key = (t.astype(np.int64) * 1000 + j) * 2 + (s > 0)
    df = pd.DataFrame({"key": key, "row": np.arange(len(ci)), "t": t, "j": j, "s": s, "k": k})
    trades = []
    for kk_key, g in df.groupby("key", sort=False):
        side = int(g.s.iloc[0])
        side_types = [ti for ti, (ss, kk, e) in enumerate(types) if ss == side]
        if mode == "STATIC":
            choice = types.index((side, 1, "TPSL1")); best = 0.0
        elif mode == "RANDOM":
            choice = side_types[rng.integers(len(side_types))]; best = 0.0
        else:
            r0 = int(g.row.iloc[0])
            vals = evs[r0, side_types]
            choice = side_types[int(np.argmax(vals))]; best = float(np.max(vals))
            if mode == "GATED" and best <= 0:
                continue
        _, kk, e = types[choice]
        hit = g[g.k == kk]
        if not len(hit):
            continue                                   # the chosen rung was not reached: no fill
        r = int(hit.row.iloc[0])
        net, xb = D["lab"][e]
        c = ci[r]
        if not np.isfinite(net[c]) or xb[c] < 0:
            continue
        trades.append((int(fl["f"][c]), int(xb[c]), int(fl["j"][c]), side, float(net[c])))
    # position management: one open per coin/side, ≤ CAP per side, arrival order
    trades.sort()
    open_until = {}
    open_side = {1: [], -1: []}
    pnl = np.zeros(T)
    taken = 0
    for f, xbar, jj, side, net in trades:
        if open_until.get((jj, side), -1) >= f:
            continue
        open_side[side] = [x for x in open_side[side] if x >= f]
        if len(open_side[side]) >= CAP:
            continue
        open_until[(jj, side)] = xbar
        open_side[side].append(xbar)
        pnl[min(xbar, T - 1)] += SLOT * net / 100
        taken += 1
    daily = pd.Series(pnl, index=idx).resample("1D").sum()
    daily = daily[(daily.index >= D_END) & (daily.index < idx[v1] + pd.Timedelta(days=8))]
    return daily, taken


def sharpe_stats(d: pd.Series):
    x = d.values
    sr_d = x.mean() / x.std() if x.std() > 0 else 0.0
    n = len(x)
    t = sr_d * math.sqrt(n)
    sk = float(pd.Series(x).skew()); ku = float(pd.Series(x).kurt()) + 3
    g = 0.5772156649
    sr0 = math.sqrt(1 / n) * ((1 - g) * norm.ppf(1 - 1 / N_TRIALS) + g * norm.ppf(1 - 1 / (N_TRIALS * math.e)))
    dsr = norm.cdf((sr_d - sr0) * math.sqrt(n - 1) / math.sqrt(max(1e-12, 1 - sk * sr_d + (ku - 1) / 4 * sr_d ** 2)))
    return sr_d * math.sqrt(365), t, dsr, 100 * x.sum() / (n / 365)


def selftest() -> int:
    """Deterministic paths: a long TP hit, a long SL hit (close-trigger, next-open fill), a trail ratchet."""
    T = 10
    O = np.full((T, 3), 100.0); H = O.copy(); L = O.copy(); C = O.copy()
    H[2, 0] = 101.1                                                 # coin 0: bar 2 trades through TP 101
    C[2, 1], L[2, 1], O[3, 1] = 98.8, 98.8, 98.5                    # coin 1: closes below SL 99 at bar 2 → exit 98.5 at bar 3's open
    C[1:6, 2] = [101, 103, 106, 104.8, 103]; H[1:6, 2] = C[1:6, 2]; L[1:6, 2] = C[1:6, 2]; O[6, 2] = 102.5   # coin 2: trail 1.5·a(=1) from 106 → stop 104.5 → close 103 < stop at bar 5 → exit 102.5
    f = np.array([1, 1, 1]); j = np.array([0, 1, 2]); side = np.array([1, 1, 1]); px = np.array([100.0, 100.0, 100.0]); a = np.ones(3)
    g, xb, mk = exit_paths("tpsl", 24, 1.0, 1.0, side[:2], f[:2], j[:2], px[:2], a[:2], O, H, L, C)
    assert abs(g[0] - 100 * math.log(1.01)) < 1e-9 and mk[0] and xb[0] == 2, (g, xb, mk)
    assert abs(g[1] - 100 * math.log(0.985)) < 1e-9 and not mk[1] and xb[1] == 3, (g, xb, mk)
    g, xb, mk = exit_paths("trail", 168, None, 1.5, side[2:], f[2:], j[2:], px[2:], a[2:], O, H, L, C)
    assert xb[0] == 6 and abs(g[0] - 100 * math.log(1.025)) < 1e-9, (g, xb)
    print("selftest: OK (TP maker fill, SL close-trigger next-open fill, trailing stop ratchets and exits)")
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    rng = np.random.default_rng(20261012)
    print("rule-path detector — building rung fills and path-exit labels (block A) …", flush=True)
    D, E = build("backtest"), build("oos")
    models = fit_models(D, rng)

    print("\n── DISCOVERY (BacktestCoins, before 2024): rung types, base win rate, mean net, top derived rules ──")
    for (s, k, e), m in models.items():
        b = m["beta"][1:]
        top = np.argsort(-np.abs(b))[:4]
        rules = "  ".join(f"{'+' if b[i] > 0 else '−'}{FEATURES[i]}({b[i]:+.2f})" for i in top)
        print(f"   {'L' if s > 0 else 'S'} k{k} {e:<8} n {m['n']:>7}  win {m['base']:.0%}  mean {m['mean']:+.3f}%   {rules}")

    print("\n── VALIDATION (2024-01 → 2025-06): the dynamic grid as a managed book ──")
    verdict = []
    for uname, U in (("bt", D), ("oos", E)):
        res = {}
        for mode in ("ALWAYS", "GATED", "STATIC", "RANDOM"):
            d, n = policy_book(U, models, mode, np.random.default_rng(7))
            res[mode] = (d, n)
            sr, t, dsr, ann = sharpe_stats(d)
            print(f"   {uname:<4}{mode:<7} trades {n:>6}  ann {ann:+6.1f}%  Sharpe {sr:+5.2f}  t {t:+5.2f}  DSR(N={N_TRIALS}) {dsr:.3f}")
        nulls = []
        for z in range(NULLS):
            mz = fit_models(D, np.random.default_rng(100 + z), permute=True)
            nulls.append(sharpe_stats(policy_book(U, mz, "ALWAYS")[0])[0])
        sr_a, t_a, _, _ = sharpe_stats(res["ALWAYS"][0])
        beat = sum(sr_a > x for x in nulls)
        print(f"        permuted-label nulls (ALWAYS): Sharpe {np.round(nulls, 2).tolist()} → real beats {beat}/{NULLS}")
        ok = (sr_a > 0 and sr_a > sharpe_stats(res["STATIC"][0])[0] and sr_a > sharpe_stats(res["RANDOM"][0])[0]
              and beat == NULLS and t_a >= 3)
        verdict.append(ok)
        print(f"        → {uname} {'PASS' if ok else 'FAIL'}")
    out = os.path.join(mnr.REPO, "reports", "rulepath_models.json")
    json.dump({f"{s}|{k}|{e}": {"features": FEATURES, "beta": m["beta"].tolist(), "mu": m["mu"].tolist(), "sd": m["sd"].tolist(),
                                "lo": m["lo"].tolist(), "hi": m["hi"].tolist(), "W": m["W"], "L": m["L"]}
               for (s, k, e), m in models.items()}, open(out, "w"))
    print(f"\nVERDICT (ALWAYS, both universes): {'PASS' if all(verdict) else 'FAIL'}.  Models → {out}")
    print(f"Trials: {N_TRIALS} (30 rung-type models + 2 policies), fixed before the run. Block B untouched.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
