"""Residual reversion with a SELF-CALIBRATING λ: signal horizon and holding period re-estimated every
week on a trailing window, pooled by (liquidity bucket × covariance family) — never per coin.

Pre-registration: docs/RESID_LAMBDA_2026-10.md (frozen at the commit that adds this file).

Why: residual reversion is real but small and DECAYING (4h autocorrelation −0.051 → −0.013 across
block A), and per-coin reversion speed does not persist (half→half rank corr −0.13, sign agreement 38%).
So λ must come from a trailing window (tracks the decay) and from POOLED cells (coin identity is noise).

Every parameter that is estimated is estimated on data strictly before it is used. There is no global
fit, so the whole history is walk-forward by construction. The design choices below are fixed in advance.

Run:  python3 research/resid_lambda.py --selftest
      python3 research/resid_lambda.py --universe oos|backtest
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

HS = (2, 4, 8, 24)              # signal horizons, hours
HOLDS = (2, 4, 8, 24)           # holding periods, hours
ZMIN = 2.0                      # |residual z| to count as an event
TRAIL_D = 90                    # trailing window for λ, days
MIN_EV = 100                    # events a cell needs in the window; else fall back bucket → global
T_MIN = 2.5                     # trailing t a (h, H) needs before it is traded: best-of-16 on noise
                                # otherwise clears the cost line routinely (selftest, winner's curse)
N_LIQ, N_FAM = 4, 4             # liquidity quartiles × covariance families
FAM_D = 90                      # trailing window for families and liquidity, refit monthly
FEE_RT = 0.11                   # % round trip, Bybit non-VIP taker both sides
HALF_SPREAD_BP = (4.2, 2.9, 1.8, 0.6)   # per side by liquidity quartile (least → most), recorder 2026-10
OLD_RT = 0.21                   # the repo's flat taker model, reported alongside
SLOT, MAX_LIVE, SHIFTS = 0.05, 20, 20


def zsig(res: np.ndarray, h: int, win: int = 720) -> np.ndarray:
    """h-hour cumulative residual, z-scored by the trailing `win`-hour residual sd (strictly before)."""
    df = pd.DataFrame(res)
    s = df.rolling(h, min_periods=h).sum()
    sd = df.rolling(win, min_periods=win // 2).std().shift(h)
    return (s / (sd * math.sqrt(h))).values


def fwd(res: np.ndarray, H: int) -> np.ndarray:
    """Residual summed over the next H bars (t+1 .. t+H): what an entry at t+1's open earns, hedged."""
    c = np.vstack([np.zeros((1, res.shape[1])), np.nancumsum(np.nan_to_num(res), axis=0)])
    T = len(res)
    out = np.full(res.shape, np.nan)
    out[: T - H] = c[1 + H: T + 1] - c[1: T - H + 1]
    return out


def cells_monthly(close: pd.DataFrame, qvol: pd.DataFrame, rng) -> np.ndarray:
    """Cell id (liq quartile × family) per hour × coin, refit on the first hour of each month from the
    trailing FAM_D days only. Families = k-means on the coins' loadings on the top-3 covariance PCs."""
    T, N = close.shape
    cell = np.full((T, N), -1, np.int16)
    lr = np.log(close).diff().values
    months = np.flatnonzero(close.index.is_month_start & (close.index.hour == 0))
    for m0, m1 in zip(months, np.r_[months[1:], T]):
        a = m0 - FAM_D * 24
        if a < 0:
            continue
        R = lr[a:m0]
        ok = np.isfinite(R).mean(0) > 0.9
        if ok.sum() < N_LIQ * N_FAM:
            continue
        Rz = np.nan_to_num(R[:, ok] - np.nanmean(R[:, ok], 0))
        _, vec = np.linalg.eigh(np.cov(Rz.T))
        L = vec[:, ::-1][:, :3] * np.sqrt(np.maximum(np.linalg.eigvalsh(np.cov(Rz.T))[::-1][:3], 0))
        L = L / (np.linalg.norm(L, axis=1, keepdims=True) + 1e-12)          # direction of co-movement
        fam = kmeans(L, N_FAM, rng)
        liq = np.nanmedian(qvol.values[a:m0][:, ok], 0)
        q = np.searchsorted(np.quantile(liq, [0.25, 0.5, 0.75]), liq, side="right")
        cid = np.full(N, -1)
        cid[ok] = q * N_FAM + fam
        cell[m0:m1] = cid
    return cell


def kmeans(X: np.ndarray, k: int, rng, iters: int = 50) -> np.ndarray:
    c = X[rng.choice(len(X), k, replace=False)]
    for _ in range(iters):
        lab = ((X[:, None, :] - c[None]) ** 2).sum(-1).argmin(1)
        c = np.array([X[lab == j].mean(0) if (lab == j).any() else c[j] for j in range(k)])
    return lab


def tgate(mean, n, sd) -> float:
    """The trailing mean if it is significant (t ≥ T_MIN), else −∞ (cell stands aside)."""
    return mean if n > 1 and sd > 0 and mean / (sd / math.sqrt(n)) >= T_MIN else -np.inf


def plan(Z: dict, F: dict, cell: np.ndarray, times: pd.DatetimeIndex, cost_rt: np.ndarray, mode: str):
    """Weekly decisions: for each week and each cell, the (h, H) with the best trailing expected net
    reversion per event (−sign(z)·fwd residual − cost), traded next week only if it is > 0.
    mode: 'cell' (liq × family, fallback liq → global), 'liq', 'global', 'fixed' (h=4, H=4, always on).
    Returns {week_start_index: {cell_id: (h, H)}}."""
    weeks = np.flatnonzero((times.dayofweek == 0) & (times.hour == 0))
    ev = {}
    for h in HS:
        t, j = np.nonzero(np.abs(Z[h]) >= ZMIN)
        ev[h] = (t, j, np.sign(Z[h][t, j]))
    out = {}
    for w in weeks:
        if mode == "fixed":
            out[w] = {"*": (4, 4)}
            continue
        lo = w - TRAIL_D * 24
        if lo < 0:
            continue
        best = {}
        for h in HS:
            t, j, s = ev[h]
            for H in HOLDS:
                m = (t >= lo) & (t < w - H) & (t % max(h, H) == 0)          # purge; non-overlapping events
                # (overlapping hourly events share most of their signal and label: an event-level t over
                #  them is several times too large — selftest)
                tt, jj, ss = t[m], j[m], s[m]
                g = -ss * F[H][tt, jj] * 100 - cost_rt[cell[w, jj].clip(0) // N_FAM]
                c = cell[w, jj]
                keys = {"cell": c, "liq": c // N_FAM, "global": np.zeros_like(c)}[mode]
                ok = np.isfinite(g) & (c >= 0)
                df = pd.DataFrame({"k": keys[ok], "g": g[ok]}).groupby("k").g.agg(["mean", "size", "std"])
                if mode == "cell":                                            # fallbacks for thin cells
                    lq = pd.DataFrame({"k": (c // N_FAM)[ok], "g": g[ok]}).groupby("k").g.agg(["mean", "size", "std"])
                    gl = (g[ok].mean(), ok.sum(), g[ok].std())
                    for cid in range(N_LIQ * N_FAM):
                        if cid in df.index and df.loc[cid, "size"] >= MIN_EV:
                            v = tgate(*df.loc[cid, ["mean", "size", "std"]])
                        elif cid // N_FAM in lq.index and lq.loc[cid // N_FAM, "size"] >= MIN_EV:
                            v = tgate(*lq.loc[cid // N_FAM, ["mean", "size", "std"]])
                        else:
                            v = tgate(*gl) if gl[1] >= MIN_EV else -np.inf
                        if v > best.get(cid, (-np.inf,))[0]:
                            best[cid] = (v, h, H)
                else:
                    for k_, row in df.iterrows():
                        v = tgate(row["mean"], row["size"], row["std"])
                        if row["size"] >= MIN_EV and v > best.get(k_, (-np.inf,))[0]:
                            best[k_] = (v, h, H)
        out[w] = {k_: (h, H) for k_, (v, h, H) in best.items() if v > 0}
    return out, weeks


def select(decisions, weeks, Z, cell, T, mode) -> pd.DataFrame:
    """Trades: each |z_h| ≥ ZMIN event whose cell's decision that week uses this h, entered at t+1 open
    against the residual's sign, held H bars. One position per coin, ≤ MAX_LIVE live, arrival order."""
    cand = []
    wk_of = lambda t: np.searchsorted(weeks, t, side="right") - 1
    for h in HS:
        t, j = np.nonzero(np.abs(Z[h]) >= ZMIN)
        keep = (t < T - max(HOLDS) - 2)
        t, j = t[keep], j[keep]
        wi = wk_of(t)
        ok = wi >= 0
        t, j, wi = t[ok], j[ok], wi[ok]
        c = cell[t, j]
        for k in range(len(t)):
            if c[k] < 0:
                continue
            dec = decisions.get(weeks[wi[k]])
            if not dec:
                continue
            key = "*" if mode == "fixed" else (c[k] if mode == "cell" else c[k] // N_FAM if mode == "liq" else 0)
            d = dec.get(key)
            if d is None or d[0] != h:
                continue
            cand.append((t[k], j[k], -np.sign(Z[h][t[k], j[k]]), d[1], c[k] // N_FAM))
    if not cand:
        return pd.DataFrame(columns=["t", "j", "side", "H", "liq"])
    cand.sort()
    busy = {}
    live: list[int] = []
    out = []
    for t, j, side, H, lq in cand:
        if busy.get(j, -1) > t:
            continue
        live = [u for u in live if u > t]
        if len(live) >= MAX_LIVE:
            continue
        busy[j] = t + H + 1
        live.append(t + H)
        out.append((t, j, side, H, lq))
    return pd.DataFrame(out, columns=["t", "j", "side", "H", "liq"])


def pnl(tr: pd.DataFrame, O, C, fund, cost_rt, rng=None) -> pd.DataFrame:
    """Net % per trade (unhedged coin, funding on the coin leg). rng: time-shift null — the same trades
    taken 1–30 days earlier or later on the same coin."""
    if tr.empty:
        return tr
    T, N = C.shape
    tr = tr.copy()
    if rng is not None:
        tr["t"] = (tr.t + rng.integers(1, 31, len(tr)) * 24 * rng.choice([-1, 1], len(tr))).clip(0, T - max(HOLDS) - 3)
    cf = np.vstack([np.zeros((1, N)), np.nancumsum(np.nan_to_num(fund), axis=0)])
    e, x, j = tr.t.values + 1, tr.t.values + tr.H.values, tr.j.values
    with np.errstate(invalid="ignore", divide="ignore"):
        gross = tr.side.values * np.log(C[x, j] / O[e, j]) * 100
    tr["gross"] = gross - tr.side.values * (cf[x + 1, j] - cf[e, j]) * 100
    tr["net"] = tr.gross - cost_rt[tr.liq.astype(int).clip(0)]
    tr["net_old"] = tr.gross - OLD_RT
    tr["exit_t"] = x
    return tr.dropna(subset=["net"])


def summarize(tr: pd.DataFrame, times, label: str, col: str = "net") -> dict:
    if tr.empty:
        print(f"   {label:<34} no trades")
        return dict(m=np.nan, t=np.nan)
    wk = ((times[tr.t.values] - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)).values
    x = tr[col].values
    s = pd.Series(x - x.mean()).groupby(wk).sum()
    t = x.mean() * len(x) / np.sqrt((s ** 2).sum()) if (s ** 2).sum() > 0 else np.nan
    day = times[tr.exit_t.values].floor("D")
    pnl = (tr[col] * SLOT).groupby(day).sum()
    pnl = pnl.reindex(pd.date_range(day.min(), day.max()), fill_value=0)
    sr = pnl.mean() / pnl.std() * math.sqrt(365) if pnl.std() > 0 else np.nan
    yr = tr.groupby(times[tr.t.values].year)[col].mean()
    last = tr[times[tr.t.values] >= times[-1] - pd.Timedelta(days=365)][col]
    mid = len(tr) // 2
    print(f"   {label:<34} n {len(x):>6}  mean {x.mean():+.3f}% [t {t:+.2f}]  halves {x[:mid].mean():+.3f} / {x[mid:].mean():+.3f}  "
          f"last 12m {last.mean():+.3f} (n {len(last)})  book {pnl.sum():+.0f}% Sharpe {sr:+.2f}  "
          f"by year " + " ".join(f"{y}:{v:+.2f}" for y, v in yr.items()))
    return dict(m=x.mean(), t=t, h1=x[:mid].mean(), h2=x[mid:].mean(), last=last.mean())


def run(times, res, O, C, fund, qvol_df, close_df, rng, label):
    cell = cells_monthly(close_df, qvol_df, rng)
    Z = {h: zsig(res, h) for h in HS}
    F = {H: fwd(res, H) for H in HOLDS}
    cost = FEE_RT + 2 * np.array(HALF_SPREAD_BP) / 100
    print(f"\n{label}: {C.shape[1]} coins, {len(times)} hours; cost by liquidity quartile {np.round(cost, 3)} % round trip")
    out = {}
    for mode in ("cell", "liq", "global", "fixed"):
        dec, weeks = plan(Z, F, cell, times, cost, mode)
        sel = select(dec, weeks, Z, cell, len(times), mode)
        tr = pnl(sel, O, C, fund, cost)
        tag = {"cell": "PRIMARY liq×family trailing λ", "liq": "liquidity-only trailing λ",
               "global": "global trailing λ", "fixed": "fixed h=4 H=4 (textbook)"}[mode]
        out[mode] = summarize(tr, times, tag)
        if mode == "cell":
            summarize(tr, times, "   same trades at the old flat 0.21%", "net_old")
            if len(tr):
                print("      chosen (h, H) share: " + ", ".join(f"{k} {v:.0%}" for k, v in
                      (tr.groupby(["H"]).size() / len(tr)).items()) + "  (by hold H)")
            sh = []
            for _ in range(SHIFTS):
                trs = pnl(sel, O, C, fund, cost, rng=rng)
                sh.append(trs.net.mean() if len(trs) else np.nan)
            print(f"      time-shift null: real {out['cell']['m']:+.3f} beats {sum(out['cell']['m'] > s for s in sh)}/{SHIFTS} "
                  f"(null median {np.nanmedian(sh):+.3f})")
            out["shifts"] = sum(out["cell"]["m"] > s for s in sh)
    return out


def selftest() -> int:
    """Synthetic: residuals with planted AR(1) reversion of strength −0.3 at 4h in ONE liquidity
    quartile and none elsewhere. The planner must pick that quartile and trade nothing else; on
    pure noise it must trade (almost) nothing and lose about its costs."""
    rng = np.random.default_rng(5)
    T, N = 24 * 600, 32
    times = pd.date_range("2022-01-03", periods=T, freq="h")
    qv = pd.DataFrame(np.repeat(np.linspace(1, 100, N)[None], T, 0), index=times)        # fixed liquidity order
    for planted in (True, False):
        res = rng.normal(0, 0.01, (T, N))
        if planted:
            top = slice(0, N // 4)                                                       # least-liquid quartile
            b4 = np.add.reduceat(res[:, top], np.arange(0, T, 4), axis=0)              # 4h blocks
            for k in range(1, len(b4)):
                res[k * 4, top] += -0.3 * b4[k - 1]                                      # next block's first hour reverts
        C = 100 * np.exp(np.cumsum(res, 0))
        cl = pd.DataFrame(C, index=times)
        cell = cells_monthly(cl, qv, rng)
        Z = {h: zsig(res, h) for h in HS}
        F = {H: fwd(res, H) for H in HOLDS}
        cost = np.full(4, 0.05)
        dec, weeks = plan(Z, F, cell, times, cost, "liq")
        tr = pnl(select(dec, weeks, Z, cell, T, "liq"), C, C, np.zeros_like(C), cost)
        if planted:
            assert len(tr) and (tr.liq == 0).mean() > 0.9 and tr.net.mean() > 0, (len(tr), tr.liq.value_counts().to_dict() if len(tr) else None)
            print(f"   planted: {len(tr)} trades, {(tr.liq == 0).mean():.0%} in the planted quartile, mean net {tr.net.mean():+.3f}%")
        else:
            n = len(tr)
            m = tr.net.mean() if n else 0.0
            assert n < 500, (n, m)                     # stands aside; the mean of a few trades is noise
            print(f"   noise:   {n} trades, mean net {m:+.3f}% (planner mostly stands aside)")
    print("selftest OK")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--universe", choices=["oos", "backtest"])
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    from anomaly_fade_bounce import mnr, residuals
    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, os.path.join(mnr.REPO, "candle_cache"))
    res = residuals(mk)
    rng = np.random.default_rng(20261006)
    run(mk.close.index, res, mk.open.values, mk.close.values, mk.funding.values, mk.qvol, mk.close, rng, a.universe)
    return 0


if __name__ == "__main__":
    sys.exit(main())
