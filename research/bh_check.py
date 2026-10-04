"""S8 buy-and-hold check for the live book and each of its sleeves (information: seen data).

Rule (docs/RESEARCH_REVIEW_2026-10.md, S8): a sleeve's return must not be market exposure (alpha vs
buy-and-hold > 0), and on the standalone route its Sharpe must be ≥ buy-and-hold's (at equal risk it
returns at least as much).

Benchmarks, daily, spot, no funding, no costs:
  EW-OOS   equal weight over the live OosCoins, rebalanced daily (the coins the book trades). Survivors
           only (37 delisted coins missing), which flatters this benchmark: a harsh comparison.
  BTC/ETH  50/50, rebalanced daily.
Measured: Sharpe, CAGR, vol, maxDD, beta and alpha (weekly OLS, alpha t), the benchmark scaled to the
sleeve's vol, and both halves.

Run:  python3 research/bh_check.py [--trades reports/live_book_trades_2026-10-04.csv]
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


def benchmarks(idx: pd.DatetimeIndex) -> dict[str, pd.Series]:
    mk = mnr.build_market(mnr.config_symbols("OosCoins"), CACHE)
    c = mk.close.resample("1D").last()
    with np.errstate(invalid="ignore"):
        ew = (c / c.shift(1) - 1).mean(axis=1)
    import binance_trend as bt
    days, O, C, _, _ = bt.load()
    bte = pd.Series(0.5 * (C[1:] / C[:-1] - 1).sum(1), index=days[1:])     # close→close, same calendar day
    return {"EW-OOS": ew.reindex(idx).fillna(0.0), "BTC/ETH": bte.reindex(idx).fillna(0.0)}


def cagr(x):
    return 100 * ((1 + x).prod() ** (365 / len(x)) - 1)


def mdd(x):
    e = (1 + x).cumprod()
    return 100 * (e / e.cummax() - 1).min()


def report(name: str, x: pd.Series, bms: dict[str, pd.Series]) -> list[dict]:
    rows = []
    for bn, b in bms.items():
        beta = float(np.polyfit(b.values, x.values, 1)[0])
        bm = b * (x.std() / b.std())
        h = len(x) // 2
        halves = [(sharpe(x.iloc[s]), sharpe(b.iloc[s])) for s in (slice(0, h), slice(h, None))]
        ok = (x - beta * b).mean() > 0 and sharpe(x) >= sharpe(b)
        rows.append({"sleeve": name, "vs": bn, "Sharpe": sharpe(x), "B&H Sharpe": sharpe(b),
                     "CAGR %": cagr(x), "vol %": 100 * x.std() * math.sqrt(365), "maxDD %": mdd(x),
                     "B&H@vol CAGR %": cagr(bm), "B&H@vol maxDD %": mdd(bm), "beta": beta,
                     "alpha %/yr": 100 * (x - beta * b).mean() * 365, "alpha t": alpha_t(x.values, b.values),
                     "halves (x/B&H)": f"{halves[0][0]:+.2f}/{halves[0][1]:+.2f}  {halves[1][0]:+.2f}/{halves[1][1]:+.2f}",
                     "S8 standalone": "PASS" if ok else "FAIL"})
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", default=os.path.join(mnr.REPO, "reports", "live_book_trades_2026-10-04.csv"))
    a = ap.parse_args()
    book, S = book_daily(a.trades)
    book, S = book.iloc[:-1], S.iloc[:-1]                                 # the last day is incomplete
    bms = benchmarks(book.index)
    rows = []
    for name, x in [("BOOK (ERC carry+Grid+GridShort)", book), ("Grid", S.grid), ("GridShort", S.gridshort),
                    ("carry", S.carry), ("Grid+GridShort (sum)", S.grid + S.gridshort)]:
        rows += report(name, x, bms)
    print(f"S8 buy-and-hold check, {book.index[0]:%Y-%m-%d} → {book.index[-1]:%Y-%m-%d} ({len(book) / 365:.1f}y), "
          f"INFORMATION (seen data)\n")
    with pd.option_context("display.width", 260, "display.max_columns", 30, "display.float_format", lambda v: f"{v:,.2f}"):
        print(pd.DataFrame(rows).to_string(index=False))
    # the book's worst market days: does it hold up when buy-and-hold crashes?
    for bn, b in bms.items():
        w = b <= b.quantile(0.05)
        print(f"\n  on the 5% worst {bn} days (B&H {100 * b[w].mean():+.2f}%/day): book {100 * book[w].mean():+.3f}, "
              + ", ".join(f"{c} {100 * S[c][w].mean():+.3f}" for c in S.columns) + " %/day")
    return 0


if __name__ == "__main__":
    sys.exit(main())
