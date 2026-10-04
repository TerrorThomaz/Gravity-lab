"""Export the dynamic-grid overlay's decisions for the C# overlay (edgetest GRAVITY_GRID_OVERLAY).

Option 1 of the dynamic grid: the GA Grid/GridShort stay unchanged, and an overlay adds a deep
(k=3) rung when the classifier says it pays. For every k=3 one-bar rung that fills, the rulepath
logistic models (reports/rulepath_models.json, trained on BacktestCoins discovery < 2024) score
the 5 exits for that side. The best exit is armed only if its EV > 0.

Decisions are emitted only for arming bars from 2024-01-01: the models' market-wide features are
shared across coins, so scoring any coin inside 2020-2023 would leak the discovery period. The
features use bars ≤ t only (rulepath.feature_matrix), so a decision is known at the arming bar's
close.

Run:  python3 research/overlay_decisions.py --universe oos|backtest
Out:  reports/overlay_decisions_<universe>.csv  (symbol, arm_time, side, exit, ev)
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import rulepath as rp  # noqa: E402
from grid_paths import atr, fills, mnr, sigma  # noqa: E402

START = pd.Timestamp("2024-01-01")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    a = ap.parse_args()
    models = {}
    for key, m in json.load(open(os.path.join(mnr.REPO, "reports", "rulepath_models.json"))).items():
        s, k, e = key.split("|")
        models[(int(s), int(k), e)] = {kk: (np.array(v) if isinstance(v, list) else v) for kk, v in m.items()}
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"), rp.CACHE)
    idx, names = mk.close.index, list(mk.close.columns)                 # FULL history: decisions run to the data end
    O, H, L, C, V = mk.open.values, mk.high.values, mk.low.values, mk.close.values, mk.qvol.values
    S, A = sigma(C), atr(H, L, C)
    fl = fills(O, H, L, C, A, S)
    m = (fl["k"] == 3) & (idx[fl["t"]] >= START)
    t, j, side = fl["t"][m], fl["j"][m], fl["side"][m]
    X = rp.feature_matrix(idx, O, H, L, C, V, mk.funding_mask.values, mk.funding.values, t, j, S, A)
    rows = []
    for sd in (1, -1):
        w = side == sd
        evs = np.column_stack([rp.ev(models[(sd, 3, e)], X[w]) for e in rp.EXITS])
        best = evs.argmax(1); bv = evs.max(1)
        names_e = list(rp.EXITS)
        for tt, jj, b, v in zip(t[w], j[w], best, bv):
            if v > 0:
                rows.append((names[jj], idx[tt], sd, names_e[b], round(float(v), 4)))
    out = os.path.join(mnr.REPO, "reports", f"overlay_decisions_{a.universe}.csv")
    df = pd.DataFrame(rows, columns=["symbol", "arm_time", "side", "exit", "ev"]).sort_values(["symbol", "arm_time"])
    df.to_csv(out, index=False, date_format="%Y-%m-%d %H:%M:%S")
    print(f"{a.universe}: {m.sum():,} k3 fills from {START:%Y-%m-%d}; armed {len(df):,} ({len(df) / max(m.sum(), 1):.0%}); "
          f"exits {df.exit.value_counts().to_dict()} → {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
