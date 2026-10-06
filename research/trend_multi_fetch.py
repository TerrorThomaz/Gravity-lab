"""Fetch Binance spot daily klines + USDT-M funding for the multi-coin trend universe (fixed in the
pre-registration: the 10 coins below, all prominent Binance USDT pairs by mid-2018 — chosen by 2018
prominence, not 2026 survival; EOS, TRX, XLM, ETC are 2018 large caps that lagged since).

Run:  python3 research/trend_multi_fetch.py
"""
import os, sys
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import pandas as pd
import binance_trend as bt

COINS = ["BTC", "ETH", "BNB", "XRP", "ADA", "LTC", "EOS", "XLM", "TRX", "ETC"]

def main():
    os.makedirs(bt.DATA, exist_ok=True)
    for c in COINS:
        bn = f"{c}USDT"
        rows, t = [], 1500000000000
        while True:
            d = bt._get(f"https://api.binance.com/api/v3/klines?symbol={bn}&interval=1d&startTime={t}&limit=1000")
            if not d:
                break
            rows += d
            t = d[-1][0] + 86400000
            if len(d) < 1000:
                break
        k = pd.DataFrame([r[:6] for r in rows], columns=["t", "open", "high", "low", "close", "vol"])
        k["day"] = pd.to_datetime(k.t, unit="ms")
        k[["day", "open", "high", "low", "close"]].to_csv(os.path.join(bt.DATA, f"{bn}_1d.csv"), index=False)
        f, t = [], 1500000000000
        while True:
            d = bt._get(f"https://fapi.binance.com/fapi/v1/fundingRate?symbol={bn}&startTime={t}&limit=1000")
            if not d:
                break
            f += [(x["fundingTime"], float(x["fundingRate"])) for x in d]
            t = d[-1]["fundingTime"] + 1
            if len(d) < 1000:
                break
        pd.DataFrame(f, columns=["ms", "rate"]).to_csv(os.path.join(bt.DATA, f"{bn}_funding_binance.csv"), index=False)
        print(f"{c}: {len(k)} days {k.day.min():%Y-%m-%d} → {k.day.max():%Y-%m-%d}; funding {len(f)} "
              f"({pd.to_datetime(f[0][0], unit='ms'):%Y-%m-%d} →)" if f else f"{c}: {len(k)} days, no funding", flush=True)

if __name__ == "__main__":
    main()
