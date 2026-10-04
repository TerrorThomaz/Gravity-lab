"""Equal capital per sleeve, inverse-volatility bet sizing inside the grid sleeves (INFORMATION, seen data).

Book: 25% of capital each to carry, Grid, GridShort and the trend hybrid, rebalanced monthly.
Carry and trend keep their own construction (each is already a whole book at ≤ 1x gross of its capital).

Grid / GridShort bets (sessions from the edgetest trade log), in entry order, fixed sleeve capital:
  flat        5% per bet (edgetest's MaxPositionPct), ≤ 12 open per strategy (PortfolioReplay.DefaultCaps)
  inverse vol bet = 5% × ATR%_ref / ATR%_coin, capped at 10%; ATR%_coin = Wilder ATR14 / close of the
              coin's last closed hourly bar before entry, ATR%_ref = the cross-sectional median over
              live coins at that bar (nothing fitted: a calm coin gets more, a wild one less, and a
              median coin exactly the flat 5%)
  exposure cap the open notional may not exceed the cap (30% / 60% / 100% of sleeve capital); a bet
              that does not fit is cut to the headroom (Simulator.SimulatePortfolioExposureCapped's rule)
P&L is booked at exit, as research/power_check.book_daily books the grid sleeves.

Run:  python3 research/bet_sizing.py [--trades reports/live_book_trades_2026-10-04.csv]
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
from power_check import CACHE, book_daily  # noqa: E402
from book_sizing import row, split  # noqa: E402

BASE, BET_CAP, MAX_OPEN = 0.05, 0.10, 12


def atr_pct(mk) -> pd.DataFrame:
    H, L, C = mk.high, mk.low, mk.close
    pc = C.shift(1)
    tr = pd.concat([H - L, (H - pc).abs(), (L - pc).abs()]).groupby(level=0).max()
    return tr.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean() / C


def sleeve(trades: pd.DataFrame, strategy: str, atr: pd.DataFrame, mode: str, exp_cap: float) -> pd.Series:
    g = trades[trades.strategy == strategy].sort_values("entry_time")
    ref = atr.median(axis=1)
    hours = (g.entry_time.dt.floor("h") - pd.Timedelta(hours=1))
    open_, pnl = [], []
    for (et, xt, sym, r), hr in zip(g[["entry_time", "exit_time", "symbol", "return_pct"]].itertuples(index=False), hours):
        open_ = [(x, s) for x, s in open_ if x > et]
        if len(open_) >= MAX_OPEN:
            continue
        size = BASE
        if mode == "inverse vol":
            a = atr.at[hr, sym] if (hr in atr.index and sym in atr.columns) else np.nan
            size = min(BET_CAP, BASE * ref.get(hr, np.nan) / a) if np.isfinite(a) and a > 0 else BASE
        size = min(size, max(0.0, exp_cap - sum(s for _, s in open_)))
        if size <= 0:
            continue
        open_.append((xt, size))
        pnl.append((xt.floor("D"), r / 100 * size))
    s = pd.DataFrame(pnl, columns=["d", "p"]).groupby("d").p.sum()
    return s


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", default=os.path.join(mnr.REPO, "reports", "live_book_trades_2026-10-04.csv"))
    a = ap.parse_args()
    import binance_trend as bt
    from bh_check import benchmarks
    _, S0 = book_daily(a.trades)
    S0 = S0.iloc[:-1]
    idx = S0.index
    d, O, C, F, K = bt.load()
    trend = pd.Series(bt.sleeve(O, C, F, K, hybrid=True)[0], index=d).reindex(idx).fillna(0.0)
    tr = pd.read_csv(a.trades, parse_dates=["entry_time", "exit_time"])
    mk = mnr.build_market(sorted(set(tr.symbol)), CACHE)
    atr = atr_pct(mk)
    bte = benchmarks(idx)["BTC/ETH"]
    rows, gl = [], []
    for mode in ("flat", "inverse vol"):
        for cap in (0.30, 0.60, 1.00):
            g = sleeve(tr, "Grid", atr, mode, cap).reindex(idx).fillna(0.0)
            gs = sleeve(tr, "GridShort", atr, mode, cap).reindex(idx).fillna(0.0)
            tag = f"{mode}, exposure cap {cap:.0%}"
            gl += [row(f"Grid  [{tag}]", g, bte), row(f"GridShort [{tag}]", gs, bte)]
            S = pd.DataFrame({"carry": S0.carry, "grid": g, "gridshort": gs, "trend": trend})
            x, _ = split(S, "equal capital")
            rows.append(row(f"BOOK 25% each [{tag}]", x, bte))
            if mode == "inverse vol" and cap == 0.60:
                best = x
    print(f"EQUAL CAPITAL BOOK (carry, Grid, GridShort, trend at 25% each), {idx[0]:%Y-%m-%d} → {idx[-1]:%Y-%m-%d}, "
          f"INFORMATION (seen data)\n")
    with pd.option_context("display.width", 240, "display.float_format", lambda v: f"{v:,.2f}"):
        print("grid sleeves alone (fraction of the sleeve's capital):")
        print(pd.DataFrame(gl).set_index("book")[["CAGR %", "vol %", "Sharpe", "maxDD %", "worst day %"]].to_string())
        print("\nthe book:")
        print(pd.DataFrame(rows).set_index("book").to_string())
    print("\nbook [inverse vol, cap 60%], by year %: "
          + "  ".join(f"{y} {100 * ((1 + g).prod() - 1):+.1f}" for y, g in best.groupby(best.index.year)))
    print(f"  €10,000 → €{10000 * (1 + best).prod():,.0f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
