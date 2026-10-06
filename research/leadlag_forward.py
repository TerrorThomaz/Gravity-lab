"""FORWARD test: trade followers after a BTC minute move, on the recorder's synchronised bid/ask.

Pre-registration: docs/FORWARD_LEADS_2026-10.md (frozen at the commit that adds this file).

Event   BTC mid (bid1+ask1)/2 moves ≥ 0.25% between consecutive ticker snapshots (one REST call for all
        symbols per minute, so every follower's quote is from the same instant — no stale last-trades).
Trade   each of the 20 FOLLOWERS in BTC's direction: enter at the NEXT snapshot (≈ 1 minute latency) at
        the ask (long) / bid (short), exit 10 snapshots later at the bid / ask; fee 0.055%/side (Bybit
        taker). Events within 10 minutes of the previous one are skipped. P&L per event = mean over the
        followers with quotes.
Report  also: 0-latency entry (same snapshot as the BTC move; optimistic) and Hyperliquid fees (0.045%).
Decide  (rule in the doc) at the evaluation date only; earlier runs are report-only.

Run:  python3 research/leadlag_forward.py [--count]
"""
import argparse, glob, math, os, sys
import numpy as np, pandas as pd

REC = os.path.expanduser("~/Gravity-lab/data/recorder")
FWD_START = pd.Timestamp("2026-10-08 00:00")
EVAL_AT, MIN_EVENTS = pd.Timestamp("2027-01-07"), 300
FOLLOWERS = ['ETHUSDT', 'SOLUSDT', 'ZECUSDT', 'XRPUSDT', 'NEARUSDT', 'SANDUSDT', 'HYPEUSDT', 'QNTUSDT', 'SUIUSDT', 'ADAUSDT',
             'WLDUSDT', 'DOGEUSDT', 'ENAUSDT', 'STRKUSDT', 'AAVEUSDT', 'ONDOUSDT', '1000PEPEUSDT', 'UNIUSDT', 'BNBUSDT', 'TAOUSDT']
THR, HOLD = 0.0025, 10
FEES = {"Bybit taker": 0.055, "Hyperliquid taker": 0.045}


def load():
    t = pd.concat([pd.read_csv(f, usecols=["recv_ms", "symbol", "bid1", "ask1"]) for f in sorted(glob.glob(os.path.join(REC, "*", "tickers.csv")))],
                  ignore_index=True)
    t["m"] = pd.to_datetime(t.recv_ms // 60000 * 60000, unit="ms")
    t = t[(t.m >= FWD_START) & t.symbol.isin(FOLLOWERS + ["BTCUSDT"])]
    bid = t.pivot_table(index="m", columns="symbol", values="bid1", aggfunc="last")
    ask = t.pivot_table(index="m", columns="symbol", values="ask1", aggfunc="last")
    return bid, ask


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--count", action="store_true"); a = ap.parse_args()
    bid, ask = load()
    if bid.empty:
        print("no forward snapshots yet"); return 0
    mid = (bid + ask) / 2
    idx = mid.index
    consec = np.r_[False, (np.diff(idx.values) == np.timedelta64(60, "s"))]
    rb = np.log(mid["BTCUSDT"]).diff().values
    ev, last = [], -10**9
    for i in np.flatnonzero(consec & (np.abs(np.nan_to_num(rb)) >= THR)):
        if i - last > HOLD and i + 1 + HOLD < len(idx):
            ev.append(i); last = i
    print(f"forward window {idx[0]} → {idx[-1]}: {len(idx)} snapshots, {len(ev)} BTC events (|Δmid| ≥ {THR:.2%})")
    if a.count or not ev:
        return 0
    for lat, lname in ((1, "1-snapshot latency [PRIMARY]"), (0, "0 latency (optimistic)")):
        for fname, fee in FEES.items():
            rows = []
            for i in ev:
                s = np.sign(rb[i])
                e, x = i + lat, i + lat + HOLD
                if x >= len(idx) or (idx[x] - idx[e]) > pd.Timedelta(minutes=HOLD + 2):
                    continue                                                     # recorder gap inside the hold
                ein = (ask if s > 0 else bid).iloc[e][FOLLOWERS]
                eout = (bid if s > 0 else ask).iloc[x][FOLLOWERS]
                pnl = (s * np.log(eout / ein) * 100 - 2 * fee).dropna()
                if len(pnl):
                    rows.append((idx[i], pnl.mean()))
            r = pd.Series(dict(rows))
            if len(r) < 2:
                continue
            h = len(r) // 2
            t = r.mean() / r.std(ddof=1) * math.sqrt(len(r))
            print(f"   {lname:<30} {fname:<18} n {len(r):>4}  mean net {r.mean():+.3f}%/event [t {t:+.2f}]  "
                  f"halves {r.iloc[:h].mean():+.3f} / {r.iloc[h:].mean():+.3f}")
    print(f"   decision only at {EVAL_AT:%Y-%m-%d} with ≥ {MIN_EVENTS} events; earlier output is report-only")
    return 0


if __name__ == "__main__":
    sys.exit(main())
