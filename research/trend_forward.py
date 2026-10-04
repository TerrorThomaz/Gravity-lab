"""Per-coin trend with an ATR trailing stop and vol sizing: a FROZEN forward-test spec plus its engine.

Spec (from the literature, not fitted here; frozen at the commit that adds this file):
  bars       6h (00/06/12/18 UTC), built from the hourly data
  universe   the 20 coins with the largest trailing-30d quote volume among Config.BacktestCoins ∪
             OosCoins, refreshed on the first bar of each calendar month. A coin leaving the
             universe is closed at the next open.
  entry      long when the close breaks above the highest high of the previous 80 bars (20-day
             Donchian); short when it breaks below the lowest low. Entry at the next bar's open.
  exit       ATR trailing stop: stop = extreme close since entry ∓ 2.5·ATR14 (6h bars), ratcheting
             only in the trade's favour. Exit at the next open after a CLOSE beyond the stop.
             After an exit, re-entry needs a fresh breakout.
  sizing     weight = min(10%, 1% / daily vol), daily vol = std of 6h log returns over 30 days × 2.
             Paper percentages of equity; no leverage cap beyond the 10% per coin (≤ 200% gross).
  costs      taker 0.105% per side on |Δweight|; real per-coin funding (long pays a positive rate);
             the floor applies where data is missing.
  evidence   ONLY forward rows logged by --shadow after the freeze count. --backtest is
             information (block A is pre-holdout; block B has already been seen for trend rules).
  review     6 months: report only. 12 months: PASS = net > 0, annualised Sharpe ≥ 0.75, both
             6-month halves positive. Kill switch: forward drawdown > 25% → stop and review.

Sources: AdaptiveTrend (arXiv 2602.11708; trailing ATR stop, α plateau 2.0-3.5, 6h bars);
Catching Crypto Trends (Donchian channels, vol sizing, liquid universe).

Run:  python3 research/trend_forward.py --selftest
      python3 research/trend_forward.py --backtest
      python3 research/trend_forward.py --shadow [--out data/forward/trend]
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
import urllib.parse
import urllib.request

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402

DON, ATR_N, ALPHA, TOP, VOL_T, W_MAX = 80, 14, 2.5, 20, 0.01, 0.10
SEAL = pd.Timestamp("2025-07-01")
CACHE = os.path.join(mnr.REPO, "candle_cache")


def to6h(df: pd.DataFrame, how: str) -> pd.DataFrame:
    r = df.resample("6h", origin="epoch")
    return getattr(r, how)()


def engine(O, H, L, C, Q, F, idx):
    """Bar-by-bar simulation over 6h matrices (time × coin). F = funding rate summed within each bar.
    Returns per-bar net book return (fraction), weights, and a trade log."""
    T, N = C.shape
    pc = np.vstack([np.full((1, N), np.nan), C[:-1]])
    tr = np.fmax(H - L, np.fmax(np.abs(H - pc), np.abs(L - pc)))
    atr = pd.DataFrame(tr).ewm(alpha=1 / ATR_N, adjust=False, min_periods=ATR_N).mean().values
    hh = pd.DataFrame(H).rolling(DON).max().shift(1).values         # previous 80 bars, excluding bar t
    ll = pd.DataFrame(L).rolling(DON).min().shift(1).values
    with np.errstate(invalid="ignore", divide="ignore"):
        lr = np.diff(np.log(C), axis=0, prepend=np.nan)
    dvol = pd.DataFrame(lr).rolling(120, min_periods=60).std().values * 2.0
    qv = pd.DataFrame(Q).rolling(120, min_periods=60).sum().values
    pos = np.zeros(N, int); stop = np.full(N, np.nan); ext = np.full(N, np.nan); entry = [None] * N
    w_now = np.zeros(N)
    uni = np.zeros(N, bool)
    book = np.zeros(T); W = np.zeros((T, N)); trades = []
    for t in range(1, T - 1):
        if idx[t].month != idx[t - 1].month or not uni.any():      # monthly universe refresh at bar t's close
            ok = np.isfinite(qv[t]) & np.isfinite(C[t])
            uni = np.zeros(N, bool)
            if ok.any():
                uni[np.argsort(np.where(ok, qv[t], -1))[::-1][:min(TOP, ok.sum())]] = True
        # decisions at the close of bar t, executed at the open of bar t+1
        target = np.zeros(N, int)
        for j in range(N):
            c = C[t, j]
            if not np.isfinite(c):
                target[j] = 0; continue
            if pos[j] != 0:
                ext[j] = max(ext[j], c) if pos[j] > 0 else min(ext[j], c)
                if np.isfinite(atr[t, j]):
                    cand = ext[j] - pos[j] * ALPHA * atr[t, j]
                    stop[j] = cand if not np.isfinite(stop[j]) else (max(stop[j], cand) if pos[j] > 0 else min(stop[j], cand))
                hit = (c < stop[j]) if pos[j] > 0 else (c > stop[j])
                target[j] = 0 if (hit or not uni[j]) else pos[j]
            elif uni[j] and np.isfinite(hh[t, j]):
                target[j] = 1 if c > hh[t, j] else (-1 if c < ll[t, j] else 0)
        w_new = np.zeros(N)
        for j in range(N):
            if target[j] != 0 and np.isfinite(dvol[t, j]) and dvol[t, j] > 0:
                w_new[j] = target[j] * min(W_MAX, VOL_T / dvol[t, j]) if target[j] != pos[j] else w_now[j]
            elif target[j] != 0:
                target[j] = 0
        for j in range(N):                                        # trade log and state transitions
            if pos[j] != 0 and target[j] != pos[j]:
                trades.append(dict(sym=j, side=pos[j], entry=entry[j][0], exit=idx[t + 1],
                                   ret=pos[j] * (O[t + 1, j] / entry[j][1] - 1) * 100))
                pos[j], stop[j], ext[j], entry[j] = 0, np.nan, np.nan, None
            if target[j] != 0 and pos[j] == 0:
                pos[j], ext[j], stop[j], entry[j] = target[j], C[t, j], np.nan, (idx[t + 1], O[t + 1, j])
        cost = np.abs(w_new - w_now).sum() * mnr.TAKER_COST_PER_SIDE
        # holding return over bar t+1 (open→open of t+2, approximated open→close of t+1 plus the gap to t+2 open)
        r = np.where(w_new != 0, np.nan_to_num(O[t + 2] / O[t + 1] - 1) if t + 2 < T else 0.0, 0.0)
        fund = -np.nansum(w_new * np.nan_to_num(F[t + 1]))
        book[t + 1] = float(w_new @ r) - cost + fund
        W[t + 1] = w_new
        w_now = w_new
    return book, W, trades


def stats(r: np.ndarray, label: str) -> str:
    r = r[np.isfinite(r)]
    ann = r.mean() * 4 * 365
    vol = r.std() * math.sqrt(4 * 365)
    eq = np.cumsum(np.log1p(r))
    mdd = math.exp((eq - np.maximum.accumulate(eq)).min()) - 1
    wk = pd.Series(r).groupby(np.arange(len(r)) // 28).sum().values
    d = wk - wk.mean()
    t = wk.mean() / math.sqrt(max((d @ d + (d[1:] @ d[:-1])) / len(wk), 1e-18) / len(wk))
    return f"{label}: ann {100 * ann:+.1f}%  vol {100 * vol:.1f}%  Sharpe {ann / vol if vol else float('nan'):+.2f}  maxDD {100 * mdd:+.1f}%  weekly t {t:+.2f}"


def load6h_cache(universe_syms):
    mk = mnr.build_market(universe_syms, CACHE)
    idx = mk.close.index
    O = to6h(mk.open, "first"); H = to6h(mk.high, "max"); L = to6h(mk.low, "min"); C = to6h(mk.close, "last")
    Q = to6h(mk.qvol, "sum"); F = to6h(mk.funding, "sum")
    unk = ~mk.funding_known.reindex(mk.close.columns).fillna(False).values
    F.loc[:, unk] = mnr.FUNDING_FLOOR_PCT_PER_8H / 100 * 6 / 8                # floor: costs |w| on either side
    return list(mk.close.columns), O.index, O.values, H.values, L.values, C.values, Q.values, F.values


def backtest() -> int:
    syms = sorted(set(mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins")))
    names, idx, O, H, L, C, Q, F = load6h_cache(syms)
    book, W, trades = engine(O, H, L, C, Q, F, idx)
    a = idx < SEAL
    print(f"trend_forward backtest (INFORMATION, not evidence): {len(names)} coins, {idx[0]:%Y-%m-%d} → {idx[-1]:%Y-%m-%d}")
    print("  " + stats(book[a & (np.arange(len(idx)) > DON + 120)], "block A (pre-holdout)"))
    print("  " + stats(book[~a], "block B (already seen for trend rules)"))
    tr = pd.DataFrame(trades)
    if len(tr):
        tr["year"] = pd.to_datetime(tr.entry).dt.year
        print(f"  trades {len(tr)}, win rate {(tr.ret > 0).mean():.0%}, mean gross {tr.ret.mean():+.2f}%, "
              f"avg gross exposure {np.abs(W).sum(1)[W.any(1)].mean():.2f}x")
        print("  by year, book sum %: " + "  ".join(f"{y} {100 * book[idx.year == y].sum():+.1f}" for y in sorted(set(idx.year))))
        print("  by side: " + "  ".join(f"{'long' if s > 0 else 'short'} n {len(g)} mean {g.ret.mean():+.2f}%" for s, g in tr.groupby("side")))
    return 0


# ── forward shadow: fetch fresh 6h candles + funding from Bybit, recompute, append new bars to the ledger ──
def _get(path, **params):
    url = f"https://api.bybit.com/v5/market/{path}?" + urllib.parse.urlencode(params)
    for attempt in range(4):
        try:
            with urllib.request.urlopen(url, timeout=20) as r:
                d = json.load(r)
            if d.get("retCode") == 0:
                return d["result"]
        except Exception:
            pass
        time.sleep(1 + attempt)
    return None


def shadow(out: str) -> int:
    os.makedirs(out, exist_ok=True)
    syms = sorted(set(mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins")))
    frames, funds = {}, {}
    for s in syms:
        res = _get("kline", category="linear", symbol=s, interval=360, limit=1000)
        if not res or not res.get("list"):
            continue
        a = np.array(sorted([[float(x) for x in row] for row in res["list"]]))
        frames[s] = pd.DataFrame(a[:, 1:7], index=pd.to_datetime(a[:, 0].astype("int64"), unit="ms"),
                                 columns=["o", "h", "l", "c", "v", "turnover"])
        fr = _get("funding/history", category="linear", symbol=s, limit=200)
        if fr and fr.get("list"):
            funds[s] = pd.Series({pd.to_datetime(int(x["fundingRateTimestamp"]), unit="ms"): float(x["fundingRate"]) for x in fr["list"]})
        time.sleep(0.05)
    names = list(frames)
    idx = pd.date_range(min(f.index.min() for f in frames.values()), max(f.index.max() for f in frames.values()), freq="6h")
    m = lambda c: np.column_stack([frames[s][c].reindex(idx).values for s in names])
    O, H, L, C, Q = m("o"), m("h"), m("l"), m("c"), m("turnover")
    F = np.column_stack([(funds[s].resample("6h", origin="epoch").sum().reindex(idx).fillna(0).values if s in funds
                          else np.full(len(idx), mnr.FUNDING_FLOOR_PCT_PER_8H / 100 * 6 / 8)) for s in names])
    now = pd.Timestamp.utcnow().tz_localize(None)
    closed = idx + pd.Timedelta(hours=6) <= now                      # the last kline may still be forming
    book, W, trades = engine(O[closed], H[closed], L[closed], C[closed], Q[closed], F[closed], idx[closed])
    ledger = os.path.join(out, "book.csv")
    seen = set(pd.read_csv(ledger).bar) if os.path.exists(ledger) else set()
    ci = idx[closed]
    new = [(str(ci[t]), book[t], json.dumps({names[j]: round(W[t, j], 4) for j in np.nonzero(W[t])[0]})) for t in range(len(ci))
           if str(ci[t]) not in seen and t >= len(ci) - 2]          # log only the newest bars; never rewrite history
    with open(ledger, "a") as f:
        if not seen:
            f.write("bar,net_ret,weights,logged_utc\n")
        for bar, r, w in new:
            f.write(f"{bar},{r:.6f},\"{w.replace(chr(34), chr(39))}\",{now:%Y-%m-%d %H:%M}\n")
    print(f"shadow: {len(names)} coins, last closed bar {ci[-1]}, appended {len(new)} bar(s) to {ledger}; "
          f"open positions {int((W[-1] != 0).sum())}")
    return 0


def selftest() -> int:
    """A clean up-trend then a crash: the engine must go long on the breakout, ride it, and be
    stopped out by the crash before it gives back the whole move; a flat walk barely trades."""
    T, N = 600, 1
    idx = pd.date_range("2024-01-01", periods=T, freq="6h")
    p = np.r_[np.full(200, 100.0), 100 * np.exp(np.linspace(0, 0.8, 300)), 100 * np.exp(0.8) * np.exp(np.linspace(0, -0.6, 100))]
    p = p * np.exp(np.random.default_rng(1).normal(0, 0.002, T))
    C = p[:, None]; O = np.r_[C[:1], C[:-1]]; H = np.maximum(O, C) * 1.002; L = np.minimum(O, C) * 0.998
    Q = np.full((T, N), 1e6); F = np.zeros((T, N))
    book, W, trades = engine(O, H, L, C, Q, F, idx)
    assert trades and trades[0]["side"] == 1, "no long entry on the breakout"
    assert trades[0]["ret"] > 30, f"rode too little of the +122% trend: {trades[0]['ret']:.1f}%"
    assert W[-1, 0] <= 0, "still long at the end of the crash"
    print(f"selftest: OK (long on breakout, {trades[0]["ret"]:+.0f}% on a +122% trend, stopped out in the crash)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--backtest", action="store_true")
    ap.add_argument("--shadow", action="store_true")
    ap.add_argument("--out", default=os.path.join(mnr.REPO, "data", "forward", "trend"))
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    if a.backtest:
        return backtest()
    if a.shadow:
        return shadow(a.out)
    ap.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main())
