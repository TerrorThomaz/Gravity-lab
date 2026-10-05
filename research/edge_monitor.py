"""Bayesian edge monitor for a live sleeve (Grid first): is the edge PROBABLY still there?

Model: a local-level Kalman filter on the sleeve's DAILY P&L (trades cluster in flushes, so the
day, not the trade, is the observation unit):
    μ_t = μ_{t−1} + η,  η ~ N(0, q)        the edge drifts ("the current market state")
    y_t = μ_t + ε,      ε ~ N(0, R)        daily P&L, % of sleeve
- Prior μ_0 ~ N(SHRINK · backtest mean, R / N0): the backtest is selection-biased (GA trials, roster
  chosen on OOS), so its mean is halved, and it is worth N0 DAYS of data, never its trade count.
- q = R · (2 / W)²: steady-state gain ≈ 2/W, i.e. the evidence decays like a W-day window.
- Robust: innovations clipped at ±3√S, so one crash day cannot flip the posterior (heavy tails).
- Output per day: posterior mean, P(μ > 0), ALARM when P(μ > 0) < ALARM_P.

Daily P&L here = Σ return_pct × 5% (Grid's per-session sleeve fraction) on each trade's EXIT day.
ponytail: exit-day realised P&L, no mark-to-market and no 12-slot cap; switch to edgetest's MTM
daily curve if the monitor is ever used for sizing rather than alarms.

Run:  python3 research/edge_monitor.py --selftest
      python3 research/edge_monitor.py                         planted-decay test on Grid (report)
      python3 research/edge_monitor.py --live FORWARD_TRADES.csv --since 2026-10-05
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd

SHRINK, N0, W, ALARM_P, CLIP = 0.5, 60, 180, 0.2, 3.0      # fixed before the first run
SLEEVE_FRAC = 0.05
PRIOR_DAYS = 365                                            # replay: first year plays "the backtest"
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TRADES = os.path.join(REPO, "reports", "edgetest_raw_trades.csv")
_ncdf = np.vectorize(lambda z: 0.5 * math.erfc(-z / math.sqrt(2)))


def daily(trades: pd.DataFrame, strategy: str = "Grid", start=None, end=None) -> pd.Series:
    g = trades[trades.strategy == strategy]
    day = pd.to_datetime(g.exit_time).dt.floor("D").values
    s = (g.return_pct * SLEEVE_FRAC).groupby(day).sum()
    idx = pd.date_range(start or s.index.min(), end or s.index.max(), freq="D")
    return s.reindex(idx, fill_value=0.0)


def robust_var(x: np.ndarray) -> np.ndarray:
    """Variance after winsorising at the 1/99% quantiles, per column."""
    lo, hi = np.quantile(x, [0.01, 0.99], axis=0)
    return np.clip(x, lo, hi).var(axis=0, ddof=1)


def kalman(y: np.ndarray, m0, R, n0=N0, w=W, clip=CLIP):
    """y: T × K (K independent series). Returns posterior mean and sd, T × K, AFTER each day's update."""
    T, K = y.shape
    m, P = np.broadcast_to(m0, K).astype(float).copy(), np.broadcast_to(R / n0, K).astype(float).copy()
    q = R * (2.0 / w) ** 2 if w else np.zeros(K)
    M, Sd = np.empty((T, K)), np.empty((T, K))
    for t in range(T):
        P = P + q
        S = P + R
        e = np.clip(y[t] - m, -clip * np.sqrt(S), clip * np.sqrt(S))
        Kg = P / S
        m, P = m + Kg * e, (1 - Kg) * P
        M[t], Sd[t] = m, np.sqrt(P)
    return M, Sd


def onsets(alarm: np.ndarray) -> np.ndarray:
    """Count of off→on alarm transitions per column."""
    return (alarm[1:] & ~alarm[:-1]).sum(axis=0) + alarm[0]


def block_boot(x: np.ndarray, n: int, reps: int, block: int, rng) -> np.ndarray:
    """Circular block bootstrap, n × reps: keeps the clustering of flush days."""
    T = len(x)
    starts = rng.integers(0, T, (n // block + 1, reps))
    ix = (starts[:, None, :] + np.arange(block)[None, :, None]).reshape(-1, reps)[:n] % T
    return x[ix]


def run_case(Y, decay_at, frac, w, n0, rng_mu):
    """Y: T × K bootstrap series. Removes `frac` of the edge from day decay_at[k] on, keeping the noise.
    Prior and R from the first PRIOR_DAYS (causal); the monitor runs on the rest."""
    T, K = Y.shape
    Yd = Y - frac * rng_mu * (np.arange(T)[:, None] >= decay_at[None, :])
    pri = Yd[:PRIOR_DAYS]
    R = robust_var(pri)
    M, Sd = kalman(Yd[PRIOR_DAYS:], SHRINK * pri.mean(axis=0), R, n0, w)
    p = _ncdf(M / Sd)
    return p, Yd[PRIOR_DAYS:]


def trailing_mean_alarm(Yp: np.ndarray, win: int = 180) -> np.ndarray:
    """The RollingStrategyGate-style baseline: alarm while the trailing-window mean is below 0."""
    c = np.cumsum(np.vstack([np.zeros((1, Yp.shape[1])), Yp]), axis=0)
    t = np.arange(1, len(Yp) + 1)[:, None]
    lo = np.maximum(t - win, 0)
    mean = (c[t[:, 0]] - c[lo[:, 0]]) / np.minimum(t, win)
    return mean < 0


def delay_stats(alarm: np.ndarray, start: np.ndarray, horizon: int = 730):
    """Days from the decay to the first alarm at or after it (np.inf if none within horizon)."""
    d = np.full(alarm.shape[1], np.inf)
    for k in range(alarm.shape[1]):
        hit = np.flatnonzero(alarm[start[k]: start[k] + horizon, k])
        if len(hit):
            d[k] = hit[0]
    return d


def fmt_delay(d: np.ndarray) -> str:
    s = np.sort(d)                                          # undetected paths count as never (∞)
    q = lambda p: (lambda v: f"{v:.0f}d" if np.isfinite(v) else "never")(s[min(int(p * len(s)), len(s) - 1)])
    return f"median {q(0.5):>5}  p80 {q(0.8):>5}  found ≤1y {np.mean(d <= 365):>4.0%}"


def planted_decay(reps: int = 300, seed: int = 20261005) -> int:
    rng = np.random.default_rng(seed)
    y = daily(pd.read_csv(TRADES)).values
    T = len(y)
    mu = y.mean()
    print(f"Grid daily P&L (exit-day, {SLEEVE_FRAC:.0%}/session): {T} days, mean {mu:+.4f}%/day, "
          f"sd {y.std():.4f}, ann. Sharpe {mu / y.std() * math.sqrt(365):.2f}, zero days {np.mean(y == 0):.0%}")
    print(f"spec: prior = {SHRINK} × first-{PRIOR_DAYS}d mean, worth N0 = {N0} days; window W = {W}d; "
          f"ALARM when P(edge > 0) < {ALARM_P}; {reps} block-bootstrap paths (20-day blocks)\n")
    Y = block_boot(y, T, reps, 20, rng)
    late = rng.integers(PRIOR_DAYS + 365, T - 365, reps) - PRIOR_DAYS        # dies a year+ into monitoring
    doa = np.zeros(reps, int)                                                 # dead on arrival
    never = np.full(reps, 10 ** 9)
    yrs = (T - PRIOR_DAYS) / 365
    print(f"{'W':>4} {'N0':>4}  {'false alarms/yr':>15} {'time in alarm':>13}  | {'edge dies later (100%)':<42} | "
          f"{'dies later, half edge':<42} | {'dead on arrival':<42}")
    for w in (90, 180, 365):
        for n0 in (30, 60, 120):
            p0, _ = run_case(Y, never + PRIOR_DAYS, 1.0, w, n0, mu)
            a0 = p0 < ALARM_P
            pl, _ = run_case(Y, late + PRIOR_DAYS, 1.0, w, n0, mu)
            ph, _ = run_case(Y, late + PRIOR_DAYS, 0.5, w, n0, mu)
            pd_, _ = run_case(Y, doa + PRIOR_DAYS, 1.0, w, n0, mu)
            tag = "  ← spec" if (w, n0) == (W, N0) else ""
            print(f"{w:>4} {n0:>4}  {onsets(a0).mean() / yrs:>15.2f} {a0.mean():>13.1%}  | "
                  f"{fmt_delay(delay_stats(pl < ALARM_P, late)):<42} | {fmt_delay(delay_stats(ph < ALARM_P, late)):<42} | "
                  f"{fmt_delay(delay_stats(pd_ < ALARM_P, doa)):<42}{tag}")
    # baseline: trailing-180d mean < 0 (no prior, no Bayes)
    Yp = Y[PRIOR_DAYS:]
    b0 = trailing_mean_alarm(Yp)
    dec = lambda frac, at: Yp - frac * mu * (np.arange(len(Yp))[:, None] >= at[None, :])
    print(f"\nbaseline trailing-180d mean < 0:  false alarms/yr {onsets(b0).mean() / yrs:.2f}, time in alarm {b0.mean():.1%}  | "
          f"dies later {fmt_delay(delay_stats(trailing_mean_alarm(dec(1.0, late)), late))} | "
          f"half {fmt_delay(delay_stats(trailing_mean_alarm(dec(0.5, late)), late))}")
    # sizing, report only: size_t = clip(posterior mean_{t-1} / prior mean, 0, 1)
    print("\nsizing by posterior (report only), spec config, mean over paths:")
    for name, at in (("healthy", never), ("dies later", late)):
        p, Yd = run_case(Y, at + PRIOR_DAYS, 1.0, W, N0, mu)
        M, _ = kalman(Yd, SHRINK * Y[:PRIOR_DAYS].mean(axis=0), robust_var(Y[:PRIOR_DAYS]), N0, W)
        size = np.clip(np.vstack([np.full((1, reps), SHRINK), M[:-1]]) / (SHRINK * Y[:PRIOR_DAYS].mean(axis=0)), 0, 1)
        sr = lambda X: np.mean(X.mean(axis=0) / X.std(axis=0)) * math.sqrt(365)
        print(f"   {name:<11} always-on: total {Yd.sum(axis=0).mean():+7.1f}%  Sharpe {sr(Yd):+.2f}   "
              f"posterior-sized: total {(size * Yd).sum(axis=0).mean():+7.1f}%  Sharpe {sr(size * Yd):+.2f}  "
              f"mean size {size.mean():.2f}")
    return 0


def live(path: str, since: str) -> int:
    tr = pd.read_csv(TRADES)
    bt = daily(tr, end=pd.Timestamp(since) - pd.Timedelta(days=1)).values
    fw = daily(pd.read_csv(path), start=since)
    fw = fw[fw.index < pd.Timestamp.now().floor("D")]                         # only closed days
    m0, R = SHRINK * bt.mean(), robust_var(bt[:, None])
    print(f"prior: backtest mean {bt.mean():+.4f}%/day × {SHRINK} = {float(m0):+.4f}, worth {N0} days; R = {float(R[0]):.4f}")
    if not len(fw):
        print("no closed forward days yet")
        return 0
    M, Sd = kalman(fw.values[:, None], m0, R)
    for d, m, s in zip(fw.index[-10:], M[-10:, 0], Sd[-10:, 0]):
        p = float(_ncdf(m / s))
        print(f"   {d:%Y-%m-%d}  posterior {m:+.4f} ± {s:.4f}  P(edge > 0) {p:.2f}{'  ALARM' if p < ALARM_P else ''}")
    return 0


def selftest() -> int:
    rng = np.random.default_rng(0)
    # 1) with no drift (W = 0) and no clipping, the filter IS the conjugate normal-normal update
    y = rng.normal(0.03, 0.2, (400, 1))
    M, Sd = kalman(y, 0.0, np.array([0.04]), n0=60, w=0, clip=1e9)
    prec = 60 / 0.04 + 400 / 0.04
    assert np.isclose(M[-1, 0], (400 / 0.04) * y.mean() / prec) and np.isclose(Sd[-1, 0], prec ** -0.5)
    # 2) the edge removal keeps the noise: decayed minus original is a constant step
    Y = np.tile(y, (1, 2))
    d = (Y - 0.03 * (np.arange(400)[:, None] >= np.array([100, 400])[None, :]))[:, 0] - y[:, 0]
    assert np.allclose(d[100:], -0.03) and np.allclose(d[:100], 0)
    # 3) bootstrap preserves the marginal distribution (values drawn from the series only)
    B = block_boot(y[:, 0], 400, 5, 20, rng)
    assert np.isin(B, y[:, 0]).all() and B.shape == (400, 5)
    # 4) a series with zero edge must alarm eventually; a strong edge must not (sanity, not calibration)
    p0 = _ncdf(np.divide(*kalman(rng.normal(0.0, 0.2, (2000, 1)), 0.02, np.array([0.04]))))[-1, 0]
    p1 = _ncdf(np.divide(*kalman(rng.normal(0.05, 0.2, (2000, 1)), 0.02, np.array([0.04]))))[-1, 0]
    assert p1 > 0.95 and p0 < p1, (p0, p1)
    print("selftest OK: conjugate update exact at W=0; decay is a pure mean shift; bootstrap resamples; ordering sane")
    return 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--live")
    ap.add_argument("--since", default="2026-10-05")
    ap.add_argument("--reps", type=int, default=300)
    a = ap.parse_args()
    sys.exit(selftest() if a.selftest else live(a.live, a.since) if a.live else planted_decay(a.reps))
