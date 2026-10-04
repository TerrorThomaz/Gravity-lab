import sys; sys.path.insert(0, "research")
import numpy as np, pandas as pd, rulepath as rp
cs = pd.read_csv("reports/edgetest_raw_trades.csv", parse_dates=["entry_time", "exit_time"])
U = rp.build("oos"); fl, idx = U["fl"], U["idx"]
w0, w1 = pd.Timestamp("2024-01-01"), pd.Timestamp("2025-06-01")
for strat, side, exit_ in (("Grid", 1, "TIME24"), ("GridShort", -1, "TRAIL30")):
    c = cs[(cs.strategy == strat) & (cs.entry_time >= w0) & (cs.entry_time < w1)]
    net, xb = U["lab"][exit_]
    ft = idx[np.minimum(fl["f"], len(idx) - 1)]
    m = (fl["k"] == 3) & (fl["side"] == side) & (ft >= w0) & (ft < w1) & np.isfinite(net)
    py = net[m]
    print(f"{strat:<10} C# n {len(c):>5} mean {c.return_pct.mean():+.3f}% median {c.return_pct.median():+.3f}%  | "
          f"Python (all fills, unmanaged) n {m.sum():>5} mean {py.mean():+.3f}% median {np.median(py):+.3f}%")
    # match C# trades to Python fills by (symbol, fill time)
    sym = np.array([U["names"][j] for j in fl["j"][m]]) if "names" in U else None

print("-- Python with C#'s management (per coin, next order only after the exit bar) --")
for strat, side, exit_ in (("Grid", 1, "TIME24"), ("GridShort", -1, "TRAIL30")):
    net, xb = U["lab"][exit_]
    m = np.where((fl["k"] == 3) & (fl["side"] == side) & np.isfinite(net) & (xb >= 0))[0]
    df = pd.DataFrame({"j": fl["j"][m], "t": fl["t"][m], "f": fl["f"][m], "xb": xb[m], "net": net[m]}).sort_values(["j", "t"])
    keep = []
    for j, g in df.groupby("j"):
        nxt = -1
        for r in g.itertuples():
            if r.t >= nxt:                       # C#: next arming bar i = previous exit bar
                keep.append(r.Index); nxt = r.xb
    k = df.loc[keep]
    ft = idx[np.minimum(k.f.values, len(idx) - 1)]
    sel = (ft >= w0) & (ft < w1)
    print(f"{strat:<10} Python managed n {sel.sum():>5} mean {k.net.values[sel].mean():+.3f}% median {np.median(k.net.values[sel]):+.3f}%")
