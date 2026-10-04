import sys; sys.path.insert(0, "research")
import numpy as np, rulepath as rp
from grid_paths import SEAL, atr, fills, mnr, sigma
mk = mnr.build_market(mnr.config_symbols("BacktestCoins"), rp.CACHE)
keep = mk.close.index < SEAL; idx = mk.close.index[keep]
O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
rate = mk.funding.values[keep]; fcum = np.cumsum(rate, axis=0)
known = mk.funding_known.reindex(mk.close.columns).fillna(False).values
S, A = sigma(C), atr(H, L, C); fl = fills(O, H, L, C, A, S)
d_end = int(np.searchsorted(idx, rp.D_END))
for side in (1, -1):
    m = (fl["k"] == 3) & (fl["side"] == side) & (fl["f"] + 30 < d_end)          # DISCOVERY only
    f, j, px = fl["f"][m], fl["j"][m], fl["px"][m]; s = fl["side"][m]; a = A[fl["t"][m], j]
    row = []
    for h in (4, 8, 12, 24):
        g, xb, mkr = rp.exit_paths("time", h, None, None, s, f, j, px, a, O, H, L, C)
        fund = np.where(known[j], s * (fcum[xb, j] - fcum[f, j]) * 100, mnr.FUNDING_FLOOR_PCT_PER_8H * (xb - f) / 8)
        net = g - rp.MAKER - rp.TAKER - fund
        row.append((h, np.nanmean(net), len(net)))
    print(("long " if side > 0 else "short") + "  DISCOVERY k3 time exits: " + "  ".join(f"{h}h {v:+.3f}% (n {n})" for h, v, n in row))
