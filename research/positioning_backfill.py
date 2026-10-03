"""Backfill Bybit POSITIONING history: hourly open interest and long/short account ratio.

Both are public REST endpoints with history back to ~2022, so they can be tested against every
trade we already have — unlike order-book depth and liquidations, which only the forward recorder
(bot/market_recorder.py) can capture. Incremental: re-running only fetches what is missing.

Writes candle_cache/{SYM}_oi1h.csv (ms,open_interest) and candle_cache/{SYM}_lsr1h.csv
(ms,buy_ratio). Universe = Config.BacktestCoins + Config.OosCoins (+ BTC, ETH).

Run:  python3 research/positioning_backfill.py [--since 2021-01-01] [--symbols A,B]
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import sys
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "scripts"))
import market_neutral_research as mnr  # noqa: E402

BASE = "https://api.bybit.com/v5/market"
HOUR_MS = 3_600_000
PAUSE = 0.12          # ~8 requests/s — far under Bybit's public limit


def get(path: str, **params) -> dict:
    url = f"{BASE}/{path}?" + urllib.parse.urlencode(params)
    for attempt in range(5):
        try:
            with urllib.request.urlopen(url, timeout=20) as r:
                d = json.load(r)
            if d.get("retCode") == 0:
                return d["result"]
            if d.get("retCode") in (10006, 10018):          # rate limited
                time.sleep(2 + 2 * attempt); continue
            return {"list": []}                              # e.g. symbol not found: nothing to fetch
        except Exception:
            time.sleep(1 + attempt)
    return {"list": []}


def load(path: str) -> dict[int, float]:
    if not os.path.exists(path):
        return {}
    with open(path) as f:
        return {int(r[0]): float(r[1]) for r in csv.reader(f) if r and r[0].isdigit()}


def save(path: str, rows: dict[int, float]) -> None:
    tmp = path + ".tmp"
    with open(tmp, "w", newline="") as f:
        w = csv.writer(f)
        for k in sorted(rows):
            w.writerow([k, rows[k]])
    os.replace(tmp, path)


def backfill(sym: str, kind: str, since_ms: int, cache: str) -> int:
    path = os.path.join(cache, f"{sym}_{kind}1h.csv")
    rows = load(path)
    start = max(since_ms, (max(rows) + HOUR_MS) if rows else since_ms)
    now = int(time.time() * 1000)
    added, page = 0, (200 if kind == "oi" else 500) * HOUR_MS
    t = start
    while t < now:
        end = min(t + page, now)
        if kind == "oi":
            res = get("open-interest", category="linear", symbol=sym, intervalTime="1h", startTime=t, endTime=end, limit=200)
            pts = [(int(x["timestamp"]), float(x["openInterest"])) for x in res.get("list", [])]
        else:
            res = get("account-ratio", category="linear", symbol=sym, period="1h", startTime=t, endTime=end, limit=500)
            pts = [(int(x["timestamp"]), float(x["buyRatio"])) for x in res.get("list", [])]
        for k, v in pts:
            if k not in rows:
                rows[k] = v; added += 1
        t = end + 1
        time.sleep(PAUSE)
    if added:
        save(path, rows)
    return added


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--since", default="2021-01-01")
    ap.add_argument("--symbols", default="")
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()
    since = int(time.mktime(time.strptime(a.since, "%Y-%m-%d"))) * 1000
    syms = a.symbols.split(",") if a.symbols else sorted(set(mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins") + ["BTCUSDT", "ETHUSDT"]))
    t0, done = time.time(), [0]
    from concurrent.futures import ThreadPoolExecutor

    def one(s: str) -> None:
        oi, lsr = backfill(s, "oi", since, a.cache), backfill(s, "lsr", since, a.cache)
        done[0] += 1
        print(f"[{done[0]}/{len(syms)}] {s:<16} +{oi} OI rows, +{lsr} L/S rows  ({time.time() - t0:.0f}s)", flush=True)

    # 8 symbols in parallel at ~8 req/s each ≈ 64 req/s, under Bybit's public 600 req / 5 s per IP.
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        list(ex.map(one, syms))
    return 0


if __name__ == "__main__":
    sys.exit(main())
