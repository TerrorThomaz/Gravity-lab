"""Export the Python sleeves for edgetest's GRAVITY_EDGE_SLEEVES mode: date,pnl,gross per day.

  carry  plain factor-neutral carry on OosCoins (CarryParams(band=0.005)); gross 1 on days it holds positions
  trend  BTC+ETH trend hybrid (spot long, perp short); gross = |exposure|

Run:  python3 research/export_sleeves.py [--out reports/sleeves]
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
from power_check import CACHE  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(mnr.REPO, "reports", "sleeves"))
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    import binance_trend as bt
    from carry_hedge import book_loop, carry_targets
    mk = mnr.build_market(mnr.config_symbols("OosCoins"), CACHE)
    tg, band, _ = carry_targets(mk)
    c = book_loop(mk.close.values, mk.funding.values, mk.funding_known.values, tg, band, index=mk.close.index).resample("1D").sum()
    c = c[c.ne(0).cumsum() > 0].iloc[:-1]
    pd.DataFrame({"date": c.index.strftime("%Y-%m-%d"), "pnl": c.values, "gross": 1.0}).to_csv(os.path.join(a.out, "carry.csv"), index=False)
    d, O, C, F, K = bt.load()
    net, pos, _ = bt.sleeve(O, C, F, K, hybrid=True)
    t = pd.DataFrame({"date": d.strftime("%Y-%m-%d"), "pnl": net, "gross": np.abs(pos)})
    t = t[(d >= bt.START) & (d < d[-1])]
    t.to_csv(os.path.join(a.out, "trend.csv"), index=False)
    print(f"carry {c.index[0]:%Y-%m-%d} → {c.index[-1]:%Y-%m-%d}, trend {t.date.iloc[0]} → {t.date.iloc[-1]} → {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
