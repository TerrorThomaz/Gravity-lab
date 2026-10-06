"""FORWARD test: buy liquid-coin liquidation cascades (dip + open-interest drop), hold 8h.

Pre-registration: docs/FORWARD_LEADS_2026-10.md (frozen at the commit that adds this file). The rule is
grid_oi_cascade's event study, frozen: BacktestCoins; hourly price z ≤ −2 AND hourly OI change z ≤ −2
(each vs the trailing 720h, strictly before); long at the next hour's open, exit at the close 8h later;
taker 0.21% round trip. Gated variant: only when the frozen trend hybrid's logged signal for the last
closed day is +1 (data/forward/trend_hybrid/decisions.csv). Only events at/after FWD_START count.

OI after the 2026-10-03 backfill is re-fetched from Bybit's public history at evaluation time into
data/external/oi/ (not the shared candle_cache), so no daily job is needed.

Run:  python3 research/cascade_forward.py [--fetch]
"""
import argparse, json, math, os, sys, time, urllib.request
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import numpy as np, pandas as pd
from anomaly_fade_bounce import mnr
import grid_oi_cascade as g

FWD_START = pd.Timestamp("2026-10-08 00:00")
REPORT_AT, DECIDE_AT = pd.Timestamp("2027-04-08"), pd.Timestamp("2027-10-08")
OIX = os.path.expanduser("~/Gravity-lab/data/external/oi")
DEC = os.path.expanduser("~/Gravity-lab/data/forward/trend_hybrid/decisions.csv")
COST = 0.21


def fetch_oi(sym: str):
    path = os.path.join(OIX, f"{sym}_oi1h.csv")
    have = pd.read_csv(path) if os.path.exists(path) else pd.DataFrame(columns=["ms", "oi"])
    start = int(have.ms.max()) + 1 if len(have) else int(pd.Timestamp("2026-09-01").value // 10**6)
    rows, cursor = [], ""
    while True:
        u = (f"https://api.bybit.com/v5/market/open-interest?category=linear&symbol={sym}&intervalTime=1h"
             f"&startTime={start}&endTime={int(time.time() * 1000)}&limit=200" + (f"&cursor={cursor}" if cursor else ""))
        r = json.load(urllib.request.urlopen(urllib.request.Request(u, headers={"User-Agent": "gravity-research"}), timeout=20))
        if r.get("retCode") != 0:
            break
        lst = r["result"]["list"]
        rows += [(int(x["timestamp"]), float(x["openInterest"])) for x in lst]
        cursor = r["result"].get("nextPageCursor") or ""
        if not cursor or not lst:
            break
        time.sleep(0.05)
    if rows:
        os.makedirs(OIX, exist_ok=True)
        pd.concat([have, pd.DataFrame(rows, columns=["ms", "oi"])]).drop_duplicates("ms").sort_values("ms").to_csv(path, index=False)


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--fetch", action="store_true"); a = ap.parse_args()
    mk = mnr.build_market(mnr.config_symbols("BacktestCoins"), os.path.join(mnr.REPO, "candle_cache"))
    if a.fetch:
        for s in mk.close.columns:
            try:
                fetch_oi(s)
            except Exception as e:                                               # noqa: BLE001
                print(f"   {s}: OI fetch failed ({e})")
    OI = g.oi_matrix(mk)
    for s in OI.columns:                                                         # forward OI on top of the backfill
        p = os.path.join(OIX, f"{s}_oi1h.csv")
        if os.path.exists(p):
            d = pd.read_csv(p)
            x = pd.Series(d.oi.values, index=pd.to_datetime(d.ms, unit="ms")).reindex(OI.index)
            OI[s] = OI[s].fillna(x)
    C, O = mk.close, mk.open
    pz, oz = g.zchange(C, 1), g.zchange(OI, 1)
    lc, lo = np.log(C.values), np.log(O.values)
    T, k = len(C), 8
    fwd = np.full(C.shape, np.nan); fwd[: T - k] = (lc[k:T] - lo[1:T - k + 1]) * 100 - COST
    m = (pz <= -2) & (oz <= -2) & np.isfinite(fwd) & (C.index >= FWD_START)[:, None]
    ti, ji = np.nonzero(m)
    ev = pd.DataFrame({"t": C.index[ti], "sym": C.columns[ji], "net": fwd[ti, ji]})
    print(f"forward cascades since {FWD_START:%Y-%m-%d}: {len(ev)} events on {ev.t.dt.floor('D').nunique() if len(ev) else 0} days "
          f"(data to {C.index[-1]})")
    if len(ev):
        dec = pd.read_csv(DEC, parse_dates=["day"]).set_index("day").signal
        sig = ev.t.dt.floor("D").map(lambda d: dec[dec.index < d].iloc[-1] if (dec.index < d).any() else np.nan)
        for name, x in (("ungated [PRIMARY]", ev), ("gated: trend hybrid signal +1", ev[sig.values == 1])):
            if len(x) < 2:
                continue
            day = x.t.dt.floor("D")
            gsum = (x.net - x.net.mean()).groupby(day.values).sum()
            t = x.net.mean() / (math.sqrt((gsum ** 2).sum()) / len(x))
            h = len(x) // 2
            print(f"   {name:<32} n {len(x):>4}  mean net {x.net.mean():+.3f}% [day-clustered t {t:+.2f}]  "
                  f"halves {x.net.iloc[:h].mean():+.3f} / {x.net.iloc[h:].mean():+.3f}")
    print(f"   report-only at {REPORT_AT:%Y-%m-%d}; decision at {DECIDE_AT:%Y-%m-%d}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
