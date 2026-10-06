"""Multi-coin trend hybrid: each coin on its OWN 30d trend, weighted by INVERSE volatility.

Pre-registration: docs/TREND_MULTI_2026-10.md (frozen at the commit that adds this file).

Same mechanics as the frozen BTC+ETH hybrid (binance_trend.sleeve, hybrid=True): 7 overlapping daily
cohorts held 7 days, |gross| ≤ 1, longs on SPOT (no funding), shorts on the perp (funding; floor
0.01%/8h charged where no real rate), taker 0.105%/side on the net daily change. Differences:
  universe  BTC ETH BNB XRP ADA LTC EOS XLM TRX ETC (2018-prominent Binance USDT pairs; a coin whose
            data ends — EOS, May 2025 — is simply dropped from new cohorts and its weight goes to 0)
  signal    per coin: sign of its own 30d log return at the close
  weights   per cohort: w_i ∝ s_i / σ_i (σ = trailing 60d daily vol, strictly before), Σ|w| = 1

Run:  python3 research/trend_multi.py
"""

from __future__ import annotations

import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import binance_trend as bt  # noqa: E402
from trend_multi_fetch import COINS  # noqa: E402

LOOK, HOLD, VOLW = 30, 7, 60


def load():
    px = {c: pd.read_csv(os.path.join(bt.DATA, f"{c}USDT_1d.csv"), parse_dates=["day"]).set_index("day") for c in COINS}
    days = pd.date_range(min(p.index.min() for p in px.values()), max(p.index.max() for p in px.values()), freq="D")
    O = np.column_stack([px[c].open.reindex(days).values for c in COINS])
    C = np.column_stack([px[c].close.reindex(days).values for c in COINS])
    F, K = np.zeros_like(C), np.zeros(C.shape, bool)
    for i, c in enumerate(COINS):
        b = pd.read_csv(os.path.join(bt.DATA, f"{c}USDT_funding_binance.csv"))
        s = b.rate.groupby((pd.to_datetime(b.ms, unit="ms") - pd.Timedelta(milliseconds=1)).dt.floor("D")).sum()
        F[:, i] = s.reindex(days).fillna(0.0).values
        K[:, i] = days >= s.index.min()
    return days, O, C, F, K


def book(O, C, F, K, mode="own", shift=0):
    """mode: 'own' (each coin its own trend, inverse vol) | 'market' (one equal-weight market sign for all
    coins, inverse vol) | 'bh' (equal-weight long-only basket, spot). Returns daily net returns."""
    D, N = C.shape
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
        r1 = np.diff(lc, axis=0, prepend=np.nan)
    vol = pd.DataFrame(r1).rolling(VOLW, min_periods=VOLW // 2).std().shift(1).values
    sig = np.full((D, N), np.nan)
    sig[LOOK:] = np.sign(lc[LOOK:] - lc[:-LOOK])
    if mode == "market":
        m = np.nanmean(lc[LOOK:] - lc[:-LOOK], axis=1)
        sig[LOOK:] = np.where(np.isfinite(sig[LOOK:]), np.sign(m)[:, None], np.nan)
    if mode == "bh":
        sig = np.where(np.isfinite(C), 1.0, np.nan)
    if shift:
        for j in range(N):
            v = np.isfinite(sig[:, j])
            sig[v, j] = np.roll(sig[v, j], shift)
    live = np.isfinite(C) & np.isfinite(vol) & np.isfinite(np.roll(O, -1, axis=0))
    W = np.where(live & np.isfinite(sig), sig / vol if mode != "bh" else sig, 0.0)
    W = W / np.maximum(np.abs(W).sum(1, keepdims=True), 1e-12)                  # cohort weights decided at day d's close
    P = np.zeros((D, N))
    for k in range(1, HOLD + 1):
        P[k:] += W[:-k] / HOLD
    P = np.where(live, P, 0.0)                                                   # a coin without data carries nothing
    ret = np.zeros(D)
    with np.errstate(invalid="ignore"):
        g = np.nan_to_num(O[1:] / O[:-1] - 1)
    ret[:-1] = (P[:-1] * g).sum(1)
    trade = np.abs(np.diff(np.vstack([np.zeros((1, N)), P]), axis=0)).sum(1)
    fund = np.where(K, -P * F, -np.abs(P) * bt.FLOOR_DAY)
    fund = np.where(P < 0, fund, 0.0).sum(1)                                     # hybrid: only shorts carry funding
    return ret - bt.COST * trade + fund


def main() -> int:
    from power_check import alpha_t, book_daily
    days, O, C, F, K = load()
    start = days[0] + pd.Timedelta(days=LOOK + VOLW)
    w = days >= start
    arms = {"PRIMARY own-trend, inverse-vol (10 coins)": book(O, C, F, K, "own"),
            "market-sign, inverse-vol (10 coins)": book(O, C, F, K, "market"),
            "buy & hold, equal-weight (10 coins)": book(O, C, F, K, "bh")}
    d2, O2, C2, F2, K2 = bt.load()
    hyb = pd.Series(bt.sleeve(O2, C2, F2, K2, hybrid=True)[0], index=d2)
    S = pd.DataFrame({k: pd.Series(v, index=days) for k, v in arms.items()})
    S["BTC+ETH hybrid (frozen forward spec)"] = hyb.reindex(days)
    S = S[w].dropna()
    mid = S.index[len(S) // 2]
    print(f"multi-coin trend, {S.index[0]:%Y-%m-%d} → {S.index[-1]:%Y-%m-%d} ({len(S)} days), coins live per day: "
          f"median {int(np.median(np.isfinite(C[w]).sum(1)))}")
    for k in S:
        x = S[k]
        h1, h2 = x[x.index < mid], x[x.index >= mid]
        eq = (1 + x).cumprod()
        print(f"   {k:<44} CAGR {100 * (eq.iloc[-1] ** (365 / len(x)) - 1):+6.1f}%  Sharpe {bt.sharpe(x):+.2f} "
              f"(halves {bt.sharpe(h1):+.2f} / {bt.sharpe(h2):+.2f})  maxDD {100 * (eq / eq.cummax() - 1).min():+.0f}%  "
              + " ".join(f"{y}:{bt.sharpe(g):+.1f}" for y, g in x.groupby(x.index.year)))
    P = S.iloc[:, 0]
    print(f"   corr(PRIMARY, BTC+ETH hybrid) {P.corr(S.iloc[:, 3]):+.2f};  alpha t of PRIMARY on the hybrid "
          f"{alpha_t(P.values, S.iloc[:, 3].values):+.2f}")
    rng = np.random.default_rng(13)
    plac = [bt.sharpe(pd.Series(book(O, C, F, K, 'own', shift=int(k)), index=days)[S.index]) for k in rng.integers(60, 2000, 100)]
    print(f"   placebo (100 per-coin signal shifts): real {bt.sharpe(P):+.2f} beats {np.mean(np.array(plac) < bt.sharpe(P)):.0%} "
          f"(median {np.median(plac):+.2f}, p90 {np.quantile(plac, 0.9):+.2f})")
    trades = os.path.join(bt.mnr.REPO, "reports", "edgetest_raw_trades_oos.csv")
    b, _ = book_daily(trades)
    j = pd.concat([P.rename("p"), S.iloc[:, 3].rename("h"), b.rename("book")], axis=1).dropna()
    worst = j.book <= j.book.quantile(0.05)
    print(f"   vs the live book ({j.index[0]:%Y-%m} →): alpha t PRIMARY {alpha_t(j.p.values, j.book.values):+.2f}, "
          f"hybrid {alpha_t(j.h.values, j.book.values):+.2f};  on the book's worst 5% days: PRIMARY {100 * j.p[worst].mean():+.2f}%/day, "
          f"hybrid {100 * j.h[worst].mean():+.2f}%/day")
    return 0


if __name__ == "__main__":
    sys.exit(main())
