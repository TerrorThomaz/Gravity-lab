"""Fetch the data the candle cache cannot give: implied volatility and spot/perp basis history.

  deribit_dvol_{BTC,ETH}_1h.csv    Deribit DVOL (30d implied vol index, %), hourly OHLC, from 2021-03
  {SYM}_premium1h.csv              Bybit linear premium index (perp vs index, the funding input), hourly
  {SYM}_index1h.csv                Bybit index price (multi-venue spot composite), hourly
  {SYM}_spot1h.csv                 Bybit SPOT candles (o,h,l,c,volume,turnover), where a spot pair exists

Files go to <repo>/data/external/ (next to candle_cache, untracked). Incremental: an existing file is
extended from its last timestamp. Symbols: Config.cs BacktestCoins + OosCoins.

Run:  python3 research/fetch_external.py [--only deribit|bybit] [--symbols BTCUSDT,ETHUSDT]
"""

from __future__ import annotations

import argparse
import concurrent.futures as cf
import json
import os
import sys
import time
import urllib.parse
import urllib.request

import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402

OUT = os.path.join(os.path.dirname(os.path.realpath(os.path.join(mnr.REPO, "candle_cache"))), "data", "external")
START_MS = 1_577_836_800_000                    # 2020-01-01
HOUR = 3_600_000


def get(url: str, params: dict, tries: int = 5) -> dict:
    q = url + "?" + urllib.parse.urlencode(params)
    for k in range(tries):
        try:
            with urllib.request.urlopen(urllib.request.Request(q, headers={"User-Agent": "gravity-research"}), timeout=20) as r:
                return json.load(r)
        except Exception as e:                  # noqa: BLE001  network: retry with backoff
            if k == tries - 1:
                raise RuntimeError(f"{q}: {e}") from e
            time.sleep(1.5 * (k + 1))


def save(path: str, rows: list[list], cols: list[str]) -> int:
    new = pd.DataFrame(rows, columns=cols).astype(float)
    new["ts"] = new["ts"].astype("int64")
    if os.path.exists(path):
        new = pd.concat([pd.read_csv(path), new])
    new = new.drop_duplicates("ts", keep="last").sort_values("ts")
    new.to_csv(path, index=False)
    return len(new)


def last_ts(path: str) -> int:
    return int(pd.read_csv(path, usecols=["ts"]).ts.max()) if os.path.exists(path) else 0


def deribit_dvol(cur: str) -> str:
    path = os.path.join(OUT, f"deribit_dvol_{cur}_1h.csv")
    since = max(last_ts(path), START_MS)
    end, rows = int(time.time() * 1000), []
    while True:                                   # the API returns the newest ≤1000 rows, then a continuation
        r = get("https://www.deribit.com/api/v2/public/get_volatility_index_data",
                dict(currency=cur, start_timestamp=since, end_timestamp=end, resolution=3600))["result"]
        rows += r["data"]
        if not r.get("continuation") or not r["data"]:
            break
        end = r["continuation"]
        time.sleep(0.1)
    n = save(path, rows, ["ts", "open", "high", "low", "close"])
    return f"deribit DVOL {cur}: {n} hourly rows -> {path}"


def bybit_series(sym: str, kind: str) -> str:
    """kind: premium | index | spot. Pages backwards from now in 1000-bar windows."""
    ep = {"premium": ("premium-index-price-kline", "linear"), "index": ("index-price-kline", "linear"),
          "spot": ("kline", "spot")}[kind]
    path = os.path.join(OUT, f"{sym}_{kind}1h.csv")
    since = max(last_ts(path) + HOUR, START_MS)
    end, rows = int(time.time() * 1000) // HOUR * HOUR - HOUR, []   # last CLOSED hour
    while end >= since:
        for k in range(4):                        # "internal error" is often transient: retry before
            r = get(f"https://api.bybit.com/v5/market/{ep[0]}",   # reading it as the start of history
                    dict(category=ep[1], symbol=sym, interval=60, end=end, limit=1000))   # a start before listing errors
            if r.get("retCode") == 0:
                break
            time.sleep(2 * (k + 1))
        if r.get("retCode") != 0:
            if rows:                              # spot answers "internal error" past the pair's listing
                break
            return f"{sym} {kind}: {r.get('retMsg')} (no {'spot pair' if kind == 'spot' else 'data'})"
        lst = r["result"]["list"]
        if not lst:
            break
        rows += [x for x in lst if int(x[0]) >= since]
        if int(lst[-1][0]) <= since:
            break
        end = int(lst[-1][0]) - HOUR
        time.sleep(0.05)
    if not rows:
        return f"{sym} {kind}: nothing new"
    cols = ["ts", "open", "high", "low", "close"] + (["volume", "turnover"] if kind == "spot" else [])
    n = save(path, rows, cols)
    return f"{sym} {kind}: {n} rows"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only", choices=["deribit", "bybit"])
    ap.add_argument("--symbols")
    ap.add_argument("--threads", type=int, default=4)
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)
    if a.only != "bybit":
        for cur in ("BTC", "ETH"):
            print(deribit_dvol(cur), flush=True)
    if a.only != "deribit":
        syms = a.symbols.split(",") if a.symbols else sorted(set(mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins")))
        jobs = [(s, k) for s in syms for k in ("premium", "index", "spot")]
        with cf.ThreadPoolExecutor(a.threads) as ex:
            for i, msg in enumerate(ex.map(lambda sk: bybit_series(*sk), jobs), 1):
                if "rows" not in msg or i % 30 == 0:
                    print(f"[{i}/{len(jobs)}] {msg}", flush=True)
    print(f"done -> {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
