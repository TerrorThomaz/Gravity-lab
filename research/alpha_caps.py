"""Per-strategy MAX exposure allocated by alpha, replacing the fixed grid exposure cap (30%).

Each strategy gets a budget b_i = w_i x capital, Σ w_i = 1, and may never have more than b_i deployed,
so the book can never lever. A grid session is b_i / 12 (the 12-open cap), so P&L is linear in the
budget and the sleeve series is edgetest's UNCAPPED grid sleeve (12 x 5% = 60% max exposure; the
cap-60 dump binds nothing) divided by 0.6, i.e. per unit of max exposure. Trend: |exposure| ≤ 1 already.

Weights, monthly from days strictly before the month:
  alpha  w ∝ Σ⁻¹μ, clipped at 0 (SleeveSizer.Method.Alpha): each sleeve's alpha against the others over
         its residual variance; Sharpe shrunk toward the sleeves' common Sharpe (prior 365 days, all
         past days), σ and Σ from the trailing 90 days, correlation-shrunk 0.3.
  erc    the same budgets split by ERC (correlation shrink): separates "alpha" from "no fixed cap".
Baseline: the current rule (ERC, scaled to trailing peak gross 1, w ≤ 1, grid cap 30%).
Hybrid rows keep the cap-30 crowding limit (≤ 6 sessions) inside each alpha / ERC budget.

Input: reports/sleeves_daily_cap{30,60}_{oos,backtest}.csv (edgetest GRAVITY_EDGE_SLEEVES dump).
Run:   python3 research/alpha_caps.py
"""
from __future__ import annotations

import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402

NAMES = ["grid", "gridshort", "trend"]
MAX_EXP = {"grid": 0.6, "gridshort": 0.6, "trend": 1.0}     # per unit of sleeve capital in the cap-60 dump


def cs(c, lam=0.3):
    o = c * (1 - lam); np.fill_diagonal(o, np.diag(c)); return o


def weights(method, P, days, st, n0=365.0):
    past = (days < st) & (days >= st - pd.Timedelta(days=90))
    k = P.shape[1]
    if past.sum() < 60:
        return np.ones(k) / k, past
    c = np.cov(P[past].T)
    if method == "alpha":
        x = P[days < st]
        sr = x.mean(0) / x.std(0, ddof=1); n = len(x)
        mu = (n * sr + n0 * sr.mean()) / (n + n0) * np.sqrt(np.diag(c))
        w = np.clip(np.linalg.solve(cs(c), mu), 0, None)
        if w.sum() <= 0:
            w = 1 / np.sqrt(np.diag(c))
    else:
        w = mnr.erc_weights(cs(c))
    return w / w.sum(), past


def run(P, G, days, method, scale=False, maxw=np.inf):
    out, gr, W = np.zeros(len(days)), np.zeros(len(days)), {}
    for m in pd.period_range(days[0], days[-1], freq="M"):
        st = m.start_time
        w, past = weights(method, P, days, st)
        if scale and past.sum() >= 60:
            pk = (G[past] @ w).max(); w = w / pk if pk > 0 else w
        w = np.minimum(w, maxw); W[str(m)] = w
        sel = (days >= st) & (days <= m.end_time)
        r, g = P[sel] @ w, G[sel] @ w
        out[sel], gr[sel] = np.where(g > 1, r / g, r), np.minimum(g, 1)
    return out, gr, W


def stats(name, x, g, W):
    eq = np.cumprod(1 + x); yrs = len(x) / 365
    sh = lambda v: v.mean() / v.std() * np.sqrt(365)
    h = len(x) // 2
    aw = np.mean(list(W.values()), 0)
    print(f"  {name:44s} CAGR {(eq[-1] ** (1 / yrs) - 1) * 100:5.1f}%  vol {x.std() * np.sqrt(365) * 100:4.1f}%  "
          f"Sharpe {sh(x):4.2f} (halves {sh(x[:h]):.2f}/{sh(x[h:]):.2f})  maxDD {((eq / np.maximum.accumulate(eq)) - 1).min() * 100:5.1f}%  "
          f"gross μ {g.mean():.2f}  avg w {np.round(aw, 2)}  Oct-26 w {np.round(W.get('2026-10', aw), 2)}")


def main() -> int:
    for u in ("oos", "backtest"):
        print(f"\n=== {u} (no carry; seen data — information) ===")
        d30 = pd.read_csv(f"reports/sleeves_daily_cap30_{u}.csv", parse_dates=["date"]).set_index("date")
        d60 = pd.read_csv(f"reports/sleeves_daily_cap60_{u}.csv", parse_dates=["date"]).set_index("date")
        days = d60.index
        P30 = d30[[n + "_pnl" for n in NAMES]].values; G30 = d30[[n + "_gross" for n in NAMES]].values
        P = np.column_stack([d60[n + "_pnl"].values / MAX_EXP[n] for n in NAMES])
        G = np.column_stack([d60[n + "_gross"].values / MAX_EXP[n] for n in NAMES])
        stats("current: ERC, scaled to gross, w≤1, cap 30%", *run(P30, G30, days, "erc", scale=True, maxw=1.0))
        stats("max exposure split by ERC (no fixed cap)", *run(P, G, days, "erc"))
        stats("max exposure split by ALPHA (no fixed cap)", *run(P, G, days, "alpha"))
        # Hybrid: alpha sets each budget, the 6-session crowding limit stays INSIDE it (cap-30 sleeve =
        # 5% sessions, ≤ 6 open, max exposure 0.3 → per unit of max exposure / 0.3; trend unchanged).
        mx = {"grid": 0.3, "gridshort": 0.3, "trend": 1.0}
        Ph = np.column_stack([d30[n + "_pnl"].values / mx[n] for n in NAMES])
        Gh = np.column_stack([d30[n + "_gross"].values / mx[n] for n in NAMES])
        stats("ALPHA budgets, 6-session crowding limit kept", *run(Ph, Gh, days, "alpha"))
        stats("ERC budgets, 6-session crowding limit kept", *run(Ph, Gh, days, "erc"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
