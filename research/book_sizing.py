"""The full book under simple sizings (INFORMATION, seen data): what it makes and what it risks.

Sleeves (daily P&L as a fraction of the sleeve's own capital):
  carry      factor-neutral funding carry, OosCoins (band 0.5%)
  grid       Grid, edgetest trade log, 5%/session, cap 12 (booked at exit)
  gridshort  GridShort, same
  trend      BTC+ETH trend hybrid (spot long, perp short)
Sizings, rebalanced monthly from the trailing 90 days strictly before the month, weights sum to 1
(a capital split, never leverage):
  equal capital   1/N each
  inverse vol     w ∝ 1/σ (equal risk per sleeve; with N sleeves this is NOT ERC unless uncorrelated)
  ERC             equal risk contribution with the shrunk covariance (the C# StrategyAllocator rule)

Run:  python3 research/book_sizing.py [--trades reports/live_book_trades_2026-10-04.csv]
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
from grid_paths import mnr  # noqa: E402
from power_check import CACHE, alpha_t, book_daily, sharpe  # noqa: E402


def split(S: pd.DataFrame, how: str, lookback_d: int = 90, min_d: int = 60) -> tuple[pd.Series, pd.DataFrame]:
    months = S.index.to_period("M")
    w = pd.DataFrame(index=S.index, columns=S.columns, dtype=float)
    for mth in months.unique():
        st = mth.start_time
        past = S[(S.index < st) & (S.index >= st - pd.Timedelta(days=lookback_d))]
        if how == "equal capital" or len(past) < min_d:
            wt = np.full(S.shape[1], 1.0 / S.shape[1])
        elif how == "inverse vol":
            wt = 1.0 / past.std().values
        else:
            wt = mnr.erc_weights(mnr.shrink(past.cov().values))
        w.loc[months == mth] = wt / wt.sum()
    return (w * S).sum(axis=1), w


def row(name: str, x: pd.Series, bte: pd.Series) -> dict:
    e = (1 + x).cumprod()
    dd = e / e.cummax() - 1
    yrs = len(x) / 365
    cg = e.iloc[-1] ** (1 / yrs) - 1
    beta = float(np.polyfit(bte.values, x.values, 1)[0])
    return {"book": name, "CAGR %": 100 * cg, "vol %": 100 * x.std() * math.sqrt(365), "Sharpe": sharpe(x),
            "maxDD %": 100 * dd.min(), "Calmar": cg / -dd.min() if dd.min() < 0 else np.nan,
            "worst day %": 100 * x.min(), "longest DD days": int((dd < 0).astype(int).groupby((dd == 0).cumsum()).sum().max()),
            "beta BTC/ETH": beta, "alpha t": alpha_t(x.values, bte.values)}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", default=os.path.join(mnr.REPO, "reports", "live_book_trades_2026-10-04.csv"))
    a = ap.parse_args()
    import binance_trend as bt
    from bh_check import benchmarks
    from carry_hedge import book_loop, carry_targets, vol_scale
    _, S = book_daily(a.trades)
    S = S.iloc[:-1]
    d, O, C, F, K = bt.load()
    S["trend"] = pd.Series(bt.sleeve(O, C, F, K, hybrid=True)[0], index=d).reindex(S.index).fillna(0.0)
    mk = mnr.build_market(mnr.config_symbols("OosCoins"), CACHE)
    tg, band, _ = carry_targets(mk)
    plain = book_loop(mk.close.values, mk.funding.values, mk.funding_known.values, tg, band, index=mk.close.index).resample("1D").sum()
    sc = vol_scale(plain).reindex(mk.close.index.floor("D")).values
    carry_vm = book_loop(mk.close.values, mk.funding.values, mk.funding_known.values, tg, band, scale_day=sc,
                         index=mk.close.index).resample("1D").sum().reindex(S.index).fillna(0.0)
    bte = benchmarks(S.index)["BTC/ETH"]
    print(f"FULL BOOK, {S.index[0]:%Y-%m-%d} → {S.index[-1]:%Y-%m-%d} ({len(S) / 365:.1f}y), INFORMATION (seen data)\n")
    print("sleeves alone:")
    rows = [row(c, S[c], bte) for c in S.columns] + [row("carry vol-managed", carry_vm, bte),
                                                     row("buy & hold BTC/ETH", bte, bte)]
    fmt = {"display.width": 240, "display.float_format": lambda v: f"{v:,.2f}"}
    with pd.option_context(*sum(fmt.items(), ())):
        print(pd.DataFrame(rows).set_index("book").to_string())
        print(f"\ndaily correlation:\n{S.corr().round(2).to_string()}\n")
        rows, weights = [], {}
        for sleeves, tag in ((S, ""), (S.assign(carry=carry_vm), " [carry vol-managed]"),
                             (S.drop(columns="trend"), " [live: no trend]")):
            for how in ("equal capital", "inverse vol", "ERC"):
                x, w = split(sleeves, how)
                rows.append(row(how + tag, x, bte))
                weights[how + tag] = w.mean()
        print("BOOK under each sizing:")
        print(pd.DataFrame(rows).set_index("book").to_string())
        print("\naverage capital weights:")
        print(pd.DataFrame(weights).T.fillna(0).map(lambda v: f"{100 * v:.0f}%").to_string())
        x, _ = split(S, "inverse vol")
        print("\ninverse-vol book (4 sleeves), by year %: "
              + "  ".join(f"{y} {100 * ((1 + g).prod() - 1):+.1f}" for y, g in x.groupby(x.index.year)))
        e = (1 + x).cumprod()
        print(f"  €10,000 at {S.index[0]:%Y-%m-%d} → €{10000 * e.iloc[-1]:,.0f} at {S.index[-1]:%Y-%m-%d}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
