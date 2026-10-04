import sys; sys.path.insert(0, "research")
import numpy as np, pandas as pd, rulepath as rp
for u in ("backtest", "oos"):
    U = rp.build(u); fl, idx = U["fl"], U["idx"]
    d_end = int(np.searchsorted(idx, rp.D_END))
    for side, ex in ((1, "TIME24"), (-1, "TRAIL30")):
        net, xb = U["lab"][ex]
        m = (fl["k"] == 3) & (fl["side"] == side) & np.isfinite(net) & (xb >= 0) & (xb < d_end)     # DISCOVERY only
        df = pd.DataFrame({"f": fl["f"][m], "net": net[m]})
        df["cluster"] = df.f.map(df.groupby("f").size())
        # causal proxy: k3 fills (same side) in the PREVIOUS bar — known at the arming close
        prev = df.groupby("f").size()
        df["prev"] = (df.f - 1).map(prev).fillna(0)
        out = []
        for lo, hi in ((1, 1), (2, 4), (5, 9), (10, 10**6)):
            g = df[(df.cluster >= lo) & (df.cluster <= hi)]
            out.append(f"{lo}-{hi if hi < 10**6 else '+'}: n {len(g)} mean {g.net.mean():+.3f}")
        p0, p1 = df[df.prev == 0].net, df[df.prev >= 3].net
        print(f"{u:<8} {'long ' if side > 0 else 'short'} DISCOVERY same-bar cluster → " + " | ".join(out)
              + f"   || causal: prev-bar fills=0 mean {p0.mean():+.3f} (n {len(p0)}), ≥3 mean {p1.mean():+.3f} (n {len(p1)})")
