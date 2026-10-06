"""Do the crypto book's mechanisms hold in US stocks? Two fixed rules, written before any run
(docs/STOCKS_CHECK_2026-10.md).

A  TREND (the trend hybrid's rule): sign of the equal-weight SPY+QQQ 21-trading-day log return at
   each close → long or short the pair, 5 overlapping daily cohorts held 5 days, |gross| ≤ 1,
   1 bp per side on exposure changes. Against buy-and-hold. Also each index ETF on its own (report).
B  DIP REBOUND (Grid's mechanism): an hourly regular-session bar whose return is ≤ −2 sd of the
   trailing 70 hourly returns; buy at the next bar's open, hold 1/2/4/6 bars. Against all bars at the
   same time of day. Index ETFs (market-wide dips) and 30 large caps (split: the market dipped too, or
   only the stock). Costs 1 bp/side ETFs, 2 bp/side stocks. Day-clustered t.

Data: Yahoo chart API (daily max history; hourly last 730 days) → data/external/stocks/.
Survivorship: the 30 large caps are TODAY's large caps, which biases any long test upward.

Run:  python3 research/stocks_check.py
"""

from __future__ import annotations

import json
import math
import os
import time
import urllib.request

import numpy as np
import pandas as pd

OUT = os.path.expanduser("~/Gravity-lab/data/external/stocks")
ETFS = ["SPY", "QQQ", "IWM", "DIA", "EFA", "EEM", "TLT", "GLD"]
STOCKS = ["AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "TSLA", "JPM", "XOM", "UNH", "JNJ", "V", "PG", "HD", "MA",
          "AVGO", "COST", "LLY", "MRK", "PEP", "KO", "BAC", "WMT", "DIS", "NFLX", "AMD", "INTC", "CRM", "ORCL", "CSCO"]


def yahoo(sym: str, interval: str, rng: str) -> pd.DataFrame:
    path = os.path.join(OUT, f"{sym}_{interval}.csv")
    if os.path.exists(path) and time.time() - os.path.getmtime(path) < 86400:
        return pd.read_csv(path, index_col=0, parse_dates=True)
    url = f"https://query1.finance.yahoo.com/v8/finance/chart/{sym}?interval={interval}&range={rng}&includeAdjustedClose=true"
    d = json.load(urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"}), timeout=30))["chart"]["result"][0]
    q = d["indicators"]["quote"][0]
    df = pd.DataFrame({k: q[k] for k in ("open", "high", "low", "close", "volume")},
                      index=pd.to_datetime(d["timestamp"], unit="s", utc=True).tz_convert("America/New_York").tz_localize(None))
    if interval == "1d" and "adjclose" in d["indicators"]:
        df["close"] = d["indicators"]["adjclose"][0]["adjclose"]                # dividends + splits
    df = df.dropna(subset=["close"])
    os.makedirs(OUT, exist_ok=True)
    df.to_csv(path)
    time.sleep(0.3)
    return df


def trend() -> None:
    px = pd.DataFrame({s: yahoo(s, "1d", "max").close for s in ETFS}).sort_index()
    r = np.log(px).diff()

    def book(cols, label):
        rr = r[cols].dropna().mean(axis=1)
        sig = np.sign(rr.rolling(21).sum())
        expo = sum(sig.shift(1 + k) for k in range(5)) / 5                     # 5 overlapping cohorts, decided at close
        net = (expo * rr - expo.diff().abs() * 0.0001).dropna()
        bh = rr.reindex(net.index)
        a = pd.DataFrame({"s": net, "b": bh}).dropna()
        ann = lambda x: x.mean() * 252
        sr = lambda x: x.mean() / x.std() * math.sqrt(252)
        dd = lambda x: (x.cumsum() - x.cumsum().cummax()).min() * 100
        beta = np.cov(a.s, a.b)[0, 1] / a.b.var()
        res = a.s - beta * a.b
        t_alpha = res.mean() / res.std() * math.sqrt(len(res))
        h = len(a) // 2
        print(f"   {label:<12} {a.index[0]:%Y}→{a.index[-1]:%Y}  trend: {ann(a.s) * 100:+5.1f}%/yr Sharpe {sr(a.s):+.2f} maxDD {dd(a.s):+.0f}%   "
              f"B&H: {ann(a.b) * 100:+5.1f}%/yr Sharpe {sr(a.b):+.2f} maxDD {dd(a.b):+.0f}%   beta {beta:+.2f}  alpha t {t_alpha:+.2f}  "
              f"halves Sharpe {sr(a.s.iloc[:h]):+.2f}/{sr(a.s.iloc[h:]):+.2f}  "
              + " ".join(f"{y}s:{sr(g):+.1f}" for y, g in a.s.groupby((a.index.year // 10) * 10)))
    print("\nA  TREND (21d sign, 5×5d cohorts, long/short, 1 bp)")
    book(["SPY", "QQQ"], "SPY+QQQ [P]")
    for s in ETFS:
        book([s], s)


def dips() -> None:
    print("\nB  DIP REBOUND (hourly, z ≤ −2 vs trailing 70 bars; buy next open; net of costs; vs same time-of-day baseline)")
    H = {s: yahoo(s, "60m", "730d") for s in ["SPY", "QQQ", "IWM"] + STOCKS}
    spy_z = None
    rows = []
    for s, df in H.items():
        df = df[(df.index.time >= pd.Timestamp("09:30").time()) & (df.index.time < pd.Timestamp("16:00").time())]
        lr = np.log(df.close).diff()
        z = lr / lr.rolling(70, min_periods=50).std().shift(1)
        if s == "SPY":
            spy_z = z
        cost = (1 if s in ("SPY", "QQQ", "IWM") else 2) * 2 / 100           # % round trip
        o = df.open.values
        c = df.close.values
        for k in (1, 2, 4, 6):
            ent = np.r_[o[1:], np.nan]                                           # next bar's open
            ex = np.r_[c[k:], [np.nan] * k]                                      # close k bars after the event bar
            fwd = np.log(ex / ent) * 100 - cost
            d = pd.DataFrame({"fwd": fwd, "z": z.values, "tod": df.index.time, "day": df.index.date,
                              "spyz": spy_z.reindex(df.index).values}, index=df.index)
            rows.append(d.assign(sym=s, k=k))
    X = pd.concat(rows)
    def tday(x, day):
        g = pd.Series(x.values - x.mean()).groupby(day.values).sum()
        return x.mean() / (math.sqrt((g ** 2).sum()) / len(x)) if len(x) > 30 else np.nan
    for name, m in (("index ETFs (market-wide dip)", X.sym.isin(["SPY", "QQQ", "IWM"])),
                    ("stocks, market dipped too (SPY z ≤ −1)", ~X.sym.isin(["SPY", "QQQ", "IWM"]) & (X.spyz <= -1)),
                    ("stocks, only the stock dipped (SPY z > −1)", ~X.sym.isin(["SPY", "QQQ", "IWM"]) & (X.spyz > -1))):
        out = []
        for k in (1, 2, 4, 6):
            sub = X[m & (X.k == k)].dropna(subset=["fwd", "z"])
            ev = sub[sub.z <= -2]
            base = sub.groupby("tod").fwd.mean()
            ex = ev.fwd - ev.tod.map(base).values                               # excess over same time-of-day
            h = len(ev) // 2
            out.append(f"{k}h: net {ev.fwd.mean():+.3f}% excess {ex.mean():+.3f} [t {tday(ex, ev.day):+.1f}] "
                       f"halves {ex.iloc[:h].mean():+.3f}/{ex.iloc[h:].mean():+.3f} (n {len(ev)})")
        print(f"   {name}\n      " + "\n      ".join(out))


if __name__ == "__main__":
    trend()
    dips()
