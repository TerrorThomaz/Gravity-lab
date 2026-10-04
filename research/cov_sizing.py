"""Covariance sizing, recalculated: risk weights + a vol target, under a NO-LEVERAGE gross limit
(INFORMATION, seen data).

The capital-split sizings (weights sum to 1) leave most of the grid sleeves' capital idle: a grid
sleeve rarely has more than a fraction of its capital in open sessions. Here a sleeve's weight is a
multiplier on its P&L per unit of capital, and the binding constraint is the book's GROSS NOTIONAL:
    gross(d) = Σ_i w_i · g_i(d) ≤ 1          (g_i = sleeve i's open notional per unit capital on day d)
Monthly, from the 90 days strictly before the month:
  risk weights  inverse vol | ERC, shrink toward mean(diag)·I (the C# rule) | ERC, shrink correlations only
  scale k       min(target vol / predicted vol, 1 / max trailing gross)   — the gross cap is causal, so
                the realised peak gross is reported to show whether it held
Sleeves: carry (gross 1 when on), Grid / GridShort (5%/session, ≤ 12 open; gross = open notional),
trend hybrid (gross = |exposure|).

Run:  python3 research/cov_sizing.py [--trades reports/live_book_trades_2026-10-04.csv]
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402
from power_check import book_daily  # noqa: E402
from book_sizing import row  # noqa: E402

LAM = 0.3


def corr_shrink(cov: np.ndarray, lam: float = LAM) -> np.ndarray:
    """Shrink correlations toward 0, keep each sleeve's own variance (faithful when vols differ)."""
    sd = np.sqrt(np.diag(cov))
    c = cov / np.outer(sd, sd)
    c = (1 - lam) * c + lam * np.eye(len(c))
    return c * np.outer(sd, sd)


def grid_gross(trades: pd.DataFrame, strategy: str, idx: pd.DatetimeIndex, cap: float = 0.60) -> pd.Series:
    """Per day: notional of the sessions open at any time that day (5% each, ≤ 12 open; an upper bound)."""
    g = trades[trades.strategy == strategy].sort_values("entry_time")
    open_, keep = [], []
    for et, xt in zip(g.entry_time, g.exit_time):
        open_ = [x for x in open_ if x > et]
        keep.append(len(open_) < 12)
        if keep[-1]:
            open_.append(xt)
    g = g[keep]
    out = pd.Series(0.0, index=idx)
    for et, xt in zip(g.entry_time.dt.floor("D"), g.exit_time.dt.floor("D")):
        out.loc[et:xt] += 0.05
    return out.clip(upper=cap)


def size(S: pd.DataFrame, G: pd.DataFrame, method: str, target: float | None, lb: int = 90, min_d: int = 60):
    months = S.index.to_period("M")
    W = pd.DataFrame(index=S.index, columns=S.columns, dtype=float)
    for mth in months.unique():
        st = mth.start_time
        m = (S.index < st) & (S.index >= st - pd.Timedelta(days=lb))
        past, pg = S[m], G[m]
        if len(past) < min_d:
            w = np.full(S.shape[1], 1.0 / S.shape[1])
        else:
            cov = past.cov().values
            if method == "inverse vol":
                w = 1 / np.sqrt(np.diag(cov))
            elif method == "ERC (C# shrink)":
                w = mnr.erc_weights(mnr.shrink(cov))
            else:
                w = mnr.erc_weights(corr_shrink(cov))
            w = w / w.sum()
            if target is not None:
                pv = float(np.sqrt(w @ corr_shrink(cov) @ w) * np.sqrt(365))
                peak = float((pg.values @ w).max())
                w = w * min(target / pv if pv > 0 else 1.0, 1.0 / peak if peak > 0 else 1.0)
        W.loc[months == mth] = w
    gross = (W * G).sum(axis=1)
    hard = np.minimum(1.0, 1.0 / gross.where(gross > 0, 1.0))   # hard daily cap: shrink to gross 1 on breach days
    return (W * S).sum(axis=1) * hard, gross * hard, W


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", default=os.path.join(mnr.REPO, "reports", "live_book_trades_2026-10-04.csv"))
    ap.add_argument("--grid-cap", type=float, default=0.60)
    a = ap.parse_args()
    import binance_trend as bt
    from bh_check import benchmarks
    _, S = book_daily(a.trades)
    S = S.iloc[:-1]
    idx = S.index
    d, O, C, F, K = bt.load()
    net, pos, _ = bt.sleeve(O, C, F, K, hybrid=True)
    S["trend"] = pd.Series(net, index=d).reindex(idx).fillna(0.0)
    tr = pd.read_csv(a.trades, parse_dates=["entry_time", "exit_time"])
    G = pd.DataFrame({"carry": (S.carry != 0).astype(float), "grid": grid_gross(tr, "Grid", idx),
                      "gridshort": grid_gross(tr, "GridShort", idx),
                      "trend": pd.Series(np.abs(pos), index=d).reindex(idx).fillna(0.0)})
    from bet_sizing import sleeve as bet_sleeve
    if a.grid_cap < 0.6:                                         # grid sleeves with a lower exposure cap
        S["grid"] = bet_sleeve(tr, "Grid", None, "flat", a.grid_cap).reindex(idx).fillna(0.0)
        S["gridshort"] = bet_sleeve(tr, "GridShort", None, "flat", a.grid_cap).reindex(idx).fillna(0.0)
        G["grid"], G["gridshort"] = grid_gross(tr, "Grid", idx, a.grid_cap), grid_gross(tr, "GridShort", idx, a.grid_cap)
    bte = benchmarks(idx)["BTC/ETH"]
    print(f"grid exposure cap {a.grid_cap:.0%} of sleeve capital; a hard daily gross cap of 1 applies to every row")
    print(f"COVARIANCE SIZING under a no-leverage gross limit, {idx[0]:%Y-%m-%d} → {idx[-1]:%Y-%m-%d}, INFORMATION\n")
    print("sleeve gross notional per unit capital: mean / peak  "
          + "  ".join(f"{c} {G[c].mean():.2f}/{G[c].max():.2f}" for c in G.columns) + "\n")
    rows, wts = [], {}
    for method in ("inverse vol", "ERC (C# shrink)", "ERC (corr shrink)"):
        for target in (None, 0.06, 0.10, 0.15, 1.0):
            x, g, W = size(S, G, method, target)
            tag = f"{method}, " + ("capital split (sum 1)" if target is None else
                                   ("max under gross ≤ 1" if target == 1.0 else f"target vol {target:.0%}"))
            r = row(tag, x, bte)
            r.update({"gross mean": g.mean(), "gross peak": g.max(), "days gross>1": int((g > 1.0001).sum())})
            rows.append(r)
            wts[tag] = W.mean()
    with pd.option_context("display.width", 260, "display.float_format", lambda v: f"{v:,.2f}"):
        cols = ["CAGR %", "vol %", "Sharpe", "maxDD %", "Calmar", "worst day %", "gross mean", "gross peak", "days gross>1"]
        print(pd.DataFrame(rows).set_index("book")[cols].to_string())
        print("\naverage sleeve multipliers (fraction of equity per unit of sleeve):")
        print(pd.DataFrame(wts).T.map(lambda v: f"{v:.2f}").to_string())
        bm = bte * (0.10 / (bte.std() * np.sqrt(365)))
        e = (1 + bm).cumprod()
        print(f"\nreference: buy & hold BTC/ETH {100 * ((1 + bte).prod() ** (365 / len(bte)) - 1):.1f}%/yr at "
              f"{100 * bte.std() * np.sqrt(365):.0f}% vol (Sharpe {row('', bte, bte)['Sharpe']:.2f}); scaled to 10% vol: "
              f"{100 * (e.iloc[-1] ** (365 / len(bm)) - 1):.1f}%/yr, maxDD {100 * (e / e.cummax() - 1).min():.1f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
