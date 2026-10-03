"""Forward recorder for the data candles cannot give: order-book depth/imbalance and liquidations
(plus OI/funding from tickers at 1-minute resolution). Bybit linear perps, public endpoints only.

Why forward: Bybit serves no history for order-book depth or liquidations, so the only honest dataset
is one recorded from now on. Every row carries OUR receive time (recv_ms) next to the exchange's
own timestamp — research must use recv_ms (what we actually knew, and when), never exch_ms alone.

Every 60 s (aligned to the minute):
  tickers.csv  one REST call for all linear tickers → recv_ms, symbol, mark, index, last, bid1, ask1,
               open_interest, oi_value, funding_rate, next_funding_ms, turnover24h
  book.csv     one REST order-book snapshot (200 levels) per symbol → recv_ms, exch_ms, symbol, mid,
               spread_bps, bid/ask USD depth within 0.5% / 1% / 2% of mid, imbalance per band
Continuously:
  liquidations.csv  WebSocket allLiquidation.{symbol} → recv_ms, exch_ms, symbol, side, size, price
Files: data/recorder/YYYY-MM-DD/<name>.csv (UTC date of receive). Append-only; restart-safe.

Run:  python3 bot/market_recorder.py            (Ctrl+C to stop)
      python3 bot/market_recorder.py --once     (one cycle, then exit — smoke test)
      python3 bot/market_recorder.py --selftest (book-feature arithmetic on a synthetic book)
"""

from __future__ import annotations

import argparse
import asyncio
import csv
import json
import os
import sys
import time

import aiohttp

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, os.path.join(REPO, "scripts"))
import market_neutral_research as mnr  # noqa: E402  (only for the Config.cs symbol lists)

REST = "https://api.bybit.com/v5/market"
WS = "wss://stream.bybit.com/v5/public/linear"
BANDS = (0.005, 0.01, 0.02)
OUT = os.path.join(REPO, "data", "recorder")

TICKER_COLS = ["recv_ms", "symbol", "mark", "index", "last", "bid1", "ask1", "open_interest", "oi_value",
               "funding_rate", "next_funding_ms", "turnover24h"]
BOOK_COLS = ["recv_ms", "exch_ms", "symbol", "mid", "spread_bps"] + \
            [f"{s}_{int(b * 1000)}bp" for b in BANDS for s in ("bid_usd", "ask_usd", "imb")]
LIQ_COLS = ["recv_ms", "exch_ms", "symbol", "side", "size", "price"]


def now_ms() -> int:
    return int(time.time() * 1000)


def book_features(bids: list[tuple[float, float]], asks: list[tuple[float, float]]) -> dict | None:
    """USD depth within each band of mid, and imbalance (bid−ask)/(bid+ask). None if the book is empty."""
    if not bids or not asks:
        return None
    best_bid, best_ask = bids[0][0], asks[0][0]
    mid = (best_bid + best_ask) / 2
    if mid <= 0 or best_ask < best_bid:
        return None
    out = {"mid": mid, "spread_bps": (best_ask - best_bid) / mid * 1e4}
    for b in BANDS:
        bu = sum(p * q for p, q in bids if p >= mid * (1 - b))
        au = sum(p * q for p, q in asks if p <= mid * (1 + b))
        out[f"bid_usd_{int(b * 1000)}bp"], out[f"ask_usd_{int(b * 1000)}bp"] = bu, au
        out[f"imb_{int(b * 1000)}bp"] = (bu - au) / (bu + au) if bu + au > 0 else 0.0
    return out


class Sink:
    """Append rows to data/recorder/<UTC date>/<name>.csv, writing the header once per file."""
    def __init__(self, name: str, cols: list[str]):
        self.name, self.cols = name, cols

    def write(self, rows: list[dict]) -> None:
        if not rows:
            return
        day = time.strftime("%Y-%m-%d", time.gmtime(rows[0]["recv_ms"] / 1000))
        d = os.path.join(OUT, day); os.makedirs(d, exist_ok=True)
        path = os.path.join(d, f"{self.name}.csv")
        new = not os.path.exists(path)
        with open(path, "a", newline="") as f:
            w = csv.DictWriter(f, fieldnames=self.cols, extrasaction="ignore")
            if new:
                w.writeheader()
            w.writerows(rows)


class Recorder:
    def __init__(self, symbols: list[str]):
        self.symbols, self.uni = symbols, set(symbols)
        self.tickers, self.book, self.liq = Sink("tickers", TICKER_COLS), Sink("book", BOOK_COLS), Sink("liquidations", LIQ_COLS)
        self.live = list(symbols)          # refreshed from tickers each cycle
        self.stats = {"cycles": 0, "books": 0, "book_err": 0, "liqs": 0, "ws_reconnects": 0}

    async def get(self, session: aiohttp.ClientSession, path: str, **params) -> dict | None:
        for attempt in range(3):
            try:
                async with session.get(f"{REST}/{path}", params=params, timeout=aiohttp.ClientTimeout(total=15)) as r:
                    d = await r.json()
                if d.get("retCode") == 0:
                    return d["result"]
                await asyncio.sleep(1 + attempt)
            except Exception:
                await asyncio.sleep(1 + attempt)
        return None

    async def snapshot_cycle(self, session: aiohttp.ClientSession) -> None:
        res = await self.get(session, "tickers", category="linear")
        recv = now_ms()
        if res:
            live = {t.get("symbol") for t in res.get("list", [])}
            self.live = [x for x in self.symbols if x in live]     # delisted config symbols drop out
            rows = []
            for t in res.get("list", []):
                if t.get("symbol") not in self.uni:
                    continue
                rows.append({"recv_ms": recv, "symbol": t["symbol"], "mark": t.get("markPrice"), "index": t.get("indexPrice"),
                             "last": t.get("lastPrice"), "bid1": t.get("bid1Price"), "ask1": t.get("ask1Price"),
                             "open_interest": t.get("openInterest"), "oi_value": t.get("openInterestValue"),
                             "funding_rate": t.get("fundingRate"), "next_funding_ms": t.get("nextFundingTime"),
                             "turnover24h": t.get("turnover24h")})
            self.tickers.write(rows)

        sem = asyncio.Semaphore(4)                       # ~4 in flight: a few requests/second overall
        rows: list[dict] = []

        async def one(sym: str):
            async with sem:
                r = await self.get(session, "orderbook", category="linear", symbol=sym, limit=200)
                rv = now_ms()
                if not r:
                    self.stats["book_err"] += 1; return
                f = book_features([(float(p), float(q)) for p, q in r.get("b", [])],
                                  [(float(p), float(q)) for p, q in r.get("a", [])])
                if f is None:
                    self.stats["book_err"] += 1; return
                rows.append({"recv_ms": rv, "exch_ms": r.get("ts"), "symbol": sym, **f})
                self.stats["books"] += 1

        await asyncio.gather(*(one(s) for s in self.live))
        self.book.write(sorted(rows, key=lambda x: x["recv_ms"]))
        self.stats["cycles"] += 1

    async def snapshots(self, session: aiohttp.ClientSession, once: bool) -> None:
        while True:
            await asyncio.sleep(60 - (time.time() % 60))  # align to the minute
            t0 = time.time()
            await self.snapshot_cycle(session)
            if once:
                return
            if self.stats["cycles"] % 10 == 0:
                print(f"{time.strftime('%Y-%m-%d %H:%M:%S', time.gmtime())} UTC  cycle {self.stats['cycles']} "
                      f"({time.time() - t0:.0f}s)  books {self.stats['books']}  book errors {self.stats['book_err']}  "
                      f"liquidations {self.stats['liqs']}  ws reconnects {self.stats['ws_reconnects']}", flush=True)

    async def liquidations(self, session: aiohttp.ClientSession) -> None:
        while True:
            # Only LIVE symbols: an invalid topic can fail its whole subscribe batch.
            topics = [f"allLiquidation.{s}" for s in self.live]
            try:
                async with session.ws_connect(WS, heartbeat=None, timeout=aiohttp.ClientWSTimeout(ws_close=10)) as ws:
                    for i in range(0, len(topics), 10):      # Bybit: ≤10 args per subscribe message
                        await ws.send_str(json.dumps({"op": "subscribe", "args": topics[i:i + 10]}))
                    last_ping = time.time()
                    while True:
                        if time.time() - last_ping > 20:
                            await ws.send_str(json.dumps({"op": "ping"})); last_ping = time.time()
                        try:
                            msg = await ws.receive(timeout=25)
                        except asyncio.TimeoutError:
                            continue
                        if msg.type != aiohttp.WSMsgType.TEXT:
                            raise ConnectionError(f"ws {msg.type}")
                        d = json.loads(msg.data)
                        if not str(d.get("topic", "")).startswith("allLiquidation."):
                            continue
                        rv = now_ms()
                        rows = [{"recv_ms": rv, "exch_ms": x.get("T"), "symbol": x.get("s"), "side": x.get("S"),
                                 "size": x.get("v"), "price": x.get("p")} for x in d.get("data", [])]
                        self.liq.write(rows); self.stats["liqs"] += len(rows)
            except Exception as e:
                self.stats["ws_reconnects"] += 1
                print(f"  liquidation stream dropped ({e!r}); reconnecting in 5 s", flush=True)
                await asyncio.sleep(5)


def selftest() -> int:
    bids = [(99.9, 10), (99.5, 10), (99.0, 10), (97.0, 10)]
    asks = [(100.1, 5), (100.6, 5), (101.5, 5), (104.0, 5)]
    f = book_features(bids, asks)
    mid = 100.0
    assert abs(f["mid"] - mid) < 1e-9 and abs(f["spread_bps"] - 20.0) < 1e-9
    assert abs(f["bid_usd_5bp"] - (99.9 * 10 + 99.5 * 10)) < 1e-9          # within 0.5%: ≥ 99.5
    assert abs(f["ask_usd_5bp"] - (100.1 * 5)) < 1e-9                      # within 0.5%: ≤ 100.5
    assert abs(f["bid_usd_20bp"] - (99.9 * 10 + 99.5 * 10 + 99.0 * 10)) < 1e-9
    assert f["imb_10bp"] > 0 and book_features([], asks) is None
    print("selftest: OK")
    return 0


async def run(once: bool) -> int:
    syms = sorted(set(mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins") + ["BTCUSDT", "ETHUSDT"]))
    rec = Recorder(syms)
    print(f"recorder: {len(syms)} symbols → {OUT}  (receive-time stamped; Ctrl+C to stop)", flush=True)
    async with aiohttp.ClientSession() as session:
        await rec.snapshot_cycle(session)             # first cycle also discovers the live universe
        print(f"live on Bybit: {len(rec.live)} of {len(syms)} configured symbols", flush=True)
        if once:
            print(f"one cycle: {rec.stats}")
            return 0
        await asyncio.gather(rec.snapshots(session, once=False), rec.liquidations(session))
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    try:
        return asyncio.run(run(a.once))
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())
