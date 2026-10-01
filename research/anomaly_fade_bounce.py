"""Event study: an idiosyncratic jump up that fades back down. Does it bounce?

The user's hypothesis (2026-10-01): a coin that jumps up more than its co-movement explains, then
fades, bounces back. Covariance defines "more than its co-movement explains". The jump is measured
on the RESIDUAL return: what is left after projecting each hour's cross-section off the dollar
direction and the top-3 PCs of the trailing 30d covariance (mnr.factor_neutral, carry's anchor).

Fixed before the first run (4 trials, ga_trials.json: anomaly_fade_bounce):
- Residual: refit daily on the 720h strictly before the day, over coins with >=95% coverage.
  sigma = the coin's trailing 720h residual std.
- Definitions (one event per coin until its fade resolves; the fade must come within 72h):
  resid  : 24h residual z >= 3, then the residual gives back half of its gain from 24h before the
           trigger to the running peak.
  raw    : the same, on raw returns and raw vol (no covariance).
  market : raw z >= 3 but residual z < 1 (a market-explained jump), with the raw fade rule.
  resid0 : the resid anomaly, entered at the trigger without waiting for the fade.
- Entry at the next bar's open. Forward windows 6/24/72h, both raw (log, minus the all-bar raw mean
  for the same window, i.e. drift-adjusted) and residual.
- Stats: day-clustered t, first/second half. The bar is a 24h forward mean above the cost of the
  trade (0.21% taker, 0.04% maker) in both halves, with the residual definition beating raw and
  market. Then OosCoins, once.

Run:  python3 research/anomaly_fade_bounce.py [--universe oos]
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "scripts"))
import market_neutral_research as mnr  # noqa: E402

WIN, K, Z_UP, Z_MKT, GIVE, FADE_H = 720, 3, 3.0, 1.0, 0.5, 72
FWD = (6, 24, 72)


def residuals(mk: mnr.Market) -> np.ndarray:
    """Hourly residual log-returns, NaN where the coin had no fit that day or no bar."""
    C = mk.close.values
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = np.full_like(r, np.nan)
    days = np.flatnonzero(mk.close.index.hour == 0)
    for d0, d1 in zip(days, np.r_[days[1:], len(C)]):
        if d0 < WIN + 1:
            continue
        cols = mnr.liquid_universe(mk, d0 - WIN, d0, top=10_000)
        if len(cols) < 10:
            continue
        lr = mnr.log_returns(mk, d0 - WIN, d0, cols)
        _, vecs = np.linalg.eigh(np.cov(lr.T))
        q, _ = np.linalg.qr(np.column_stack([np.ones(len(cols)), vecs[:, ::-1][:, :K]]))
        R = r[d0:d1][:, cols]
        miss = np.isnan(R)
        R0 = np.where(miss, 0.0, R)
        E = R0 - (R0 @ q) @ q.T
        E[miss] = np.nan
        res[d0:d1, cols] = E
    return res


def zscore24(x: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """24h cumulative sum and its z against the trailing 720h hourly std (strictly before)."""
    df = pd.DataFrame(x)
    s24 = df.rolling(24, min_periods=20).sum()
    sd = df.rolling(WIN, min_periods=WIN // 2).std().shift(24)
    return s24.values, (s24 / (sd * math.sqrt(24))).values


def events(level: np.ndarray, trig: np.ndarray, fade: bool) -> list[tuple[int, int]]:
    """(trigger bar, signal bar) per event for one coin. level = cumulative log series."""
    out, t, n = [], 24, len(level)
    idx = np.flatnonzero(trig)
    k = 0
    while k < len(idx):
        t = idx[k]
        if not fade:
            out.append((t, t))
            nxt = t + 24
        else:
            base, peak, sig = level[t - 24], level[t], None
            for s in range(t + 1, min(n, t + FADE_H + 1)):
                if np.isnan(level[s]):
                    break
                peak = max(peak, level[s])
                if level[s] <= peak - GIVE * (peak - base):
                    sig = s
                    break
            if sig is not None:
                out.append((t, sig))
            nxt = (sig or t + FADE_H) + 1
        while k < len(idx) and idx[k] < nxt:
            k += 1
    return out


def study(mk: mnr.Market, label: str) -> pd.DataFrame:
    O, C = mk.open.values, mk.close.values
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = residuals(mk)
    _, z_raw = zscore24(r)
    _, z_res = zscore24(res)
    L_raw = np.nancumsum(np.nan_to_num(r), axis=0)
    L_res = np.nancumsum(np.nan_to_num(res), axis=0)
    n = len(C)
    # drift baseline: mean raw forward log return over all bars of all coins, per window
    with np.errstate(invalid="ignore", divide="ignore"):
        base = {h: np.nanmean(np.log(C[h:] / O[1:n - h + 1])) for h in FWD}
    defs = {
        "resid":  (z_res >= Z_UP, L_res, True),
        "raw":    (z_raw >= Z_UP, L_raw, True),
        "market": ((z_raw >= Z_UP) & (z_res < Z_MKT), L_raw, True),
        "resid0": (z_res >= Z_UP, L_res, False),
    }
    rows = []
    for name, (trig, lev, fade) in defs.items():
        trig = np.nan_to_num(trig, nan=0).astype(bool)
        for j in range(C.shape[1]):
            for t, s in events(lev[:, j], trig[:, j], fade):
                e = s + 1
                if e + max(FWD) > n or np.isnan(O[e, j]):
                    continue
                row = dict(defn=name, sym=mk.close.columns[j], time=mk.close.index[e],
                           res_jump=L_res[t, j] - L_res[t - 24, j], hrs_to_sig=s - t)
                for h in FWD:
                    row[f"raw{h}"] = np.log(C[e + h - 1, j] / O[e, j]) - base[h]
                    row[f"res{h}"] = L_res[e + h - 1, j] - L_res[e - 1, j]
                rows.append(row)
    ev = pd.DataFrame(rows).dropna()
    report(ev, label)
    return ev


def t_day(x: pd.Series, times: pd.Series) -> float:
    d = x.groupby(times.dt.floor("D")).mean()
    return d.mean() / (d.std(ddof=1) / math.sqrt(len(d))) if len(d) > 2 else float("nan")


def report(ev: pd.DataFrame, label: str) -> None:
    print(f"\n=== {label}: forward returns in % from the next open (raw is drift-adjusted)")
    print(f"{'defn':>7s} {'n':>6s} {'days':>5s} " + " ".join(f"{'raw' + str(h):>7s}" for h in FWD)
          + " " + " ".join(f"{'res' + str(h):>7s}" for h in FWD)
          + f" {'t raw24':>8s} {'t res24':>8s} | {'H1 raw24':>8s} {'H2 raw24':>8s} {'H1 res24':>8s} {'H2 res24':>8s}")
    for name, g in ev.groupby("defn", sort=False):
        mid = g.time.quantile(0.5)
        h1, h2 = g[g.time < mid], g[g.time >= mid]
        print(f"{name:>7s} {len(g):6d} {g.time.dt.floor('D').nunique():5d} "
              + " ".join(f"{100 * g[f'raw{h}'].mean():7.3f}" for h in FWD) + " "
              + " ".join(f"{100 * g[f'res{h}'].mean():7.3f}" for h in FWD)
              + f" {t_day(g.raw24, g.time):8.2f} {t_day(g.res24, g.time):8.2f} | "
              f"{100 * h1.raw24.mean():8.3f} {100 * h2.raw24.mean():8.3f} "
              f"{100 * h1.res24.mean():8.3f} {100 * h2.res24.mean():8.3f}")


def selftest() -> int:
    """Planted world: a factor drives every coin; one coin gets an idiosyncratic +jump. The
    residual must see the jump at full size and the market move at ~zero."""
    rng = np.random.default_rng(1)
    hrs, m = 24 * 60, 30
    f = rng.normal(0, 0.01, hrs)
    beta = rng.uniform(0.5, 1.5, m)
    r = f[:, None] * beta + rng.normal(0, 0.003, (hrs, m))
    r[-10, 0] += 0.10                                   # idiosyncratic +10% on coin 0
    r[-5, :] += 0.05 * beta                             # market-wide +5% (explained by co-movement)
    C = 100 * np.exp(np.cumsum(r, axis=0))
    idx = pd.date_range("2024-01-01", periods=hrs, freq="1h")
    cl = pd.DataFrame(C, index=idx, columns=[f"C{i}" for i in range(m)])
    mk = mnr.Market(cl, cl * 0 + 1e6, cl * 0, pd.Series(False, index=cl.columns), cl * 0 > 1,
                    [], cl, cl, cl)
    res = residuals(mk)
    assert res[-10, 0] > 0.08, res[-10, 0]
    assert np.nanmax(np.abs(res[-5])) < 0.02, np.nanmax(np.abs(res[-5]))
    lev = np.array([0, 0, 0, 1, 3, 4, 3, 2.4, 2.5, 3.0])  # trigger at 4, peak 4, half back at 2.0
    assert events(np.r_[np.zeros(24), lev], np.r_[np.zeros(24 + 4), 1, np.zeros(5)].astype(bool),
                  True) == []                           # 2.4 never reaches half (2.0): no event
    lev2 = np.r_[np.zeros(24), [1, 3, 4, 2.0, 2.5]]
    assert events(lev2, np.r_[np.zeros(26), 1, 0, 0, 0][:len(lev2)].astype(bool), True) == [(26, 27)]
    print("selftest ok")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--universe", choices=["backtest", "oos"], default="backtest")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, a.cache)
    print(f"{a.universe}: {mk.close.shape[1]} coins, {mk.close.index[0]} .. {mk.close.index[-1]}")
    ev = study(mk, a.universe)
    out = os.path.join(mnr.REPO, "reports", f"anomaly_fade_events_{a.universe}.csv")
    ev.to_csv(out, index=False)
    print(f"events -> {out}")
    if a.universe == "backtest":
        path = os.path.join(mnr.REPO, "genotypes", "ga_trials.json")
        d = json.load(open(path))
        d["anomaly_fade_bounce"] = d.get("anomaly_fade_bounce", 0) + 4
        json.dump(d, open(path, "w"), indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
