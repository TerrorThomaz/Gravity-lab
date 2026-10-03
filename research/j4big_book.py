"""j4big as a BOOK: short a coin's fresh idiosyncratic push, hold 4h — trade rarely, not slowly.

anomaly_fade_4h.py measured the signal (4h forward residual −0.20% BacktestCoins / −0.45% OOS, t −9/−10)
and listed what a book still needed: slot caps, funding, a time-shift control. This adds exactly that.

Fixed before the first run (2 trials: hedged, unhedged):
- Event  : j4big = 4h residual z >= 3 AND 24h residual z >= 3 (residual off $ + top-3 PCs of the
           trailing 30d 15m covariance, refit daily on data before the day). One per coin per 4h.
- Entry  : SHORT at the open two bars after the signal bar (+15m — a spike print cannot fake it).
- Exit   : close of the 16th bar (4h hold). Time exits cannot be guaranteed as maker: TAKER every side.
- Hedged : weights w = −(e_i − q qᵀ e_i), the same projection the residual uses, so P&L ≈ −residual;
           every leg pays costs (gross |w|₁, in and out). Unhedged: the coin alone.
- Funding: real per-symbol settlements inside the hold on the coin leg (short receives positive
           funding); hedge legs' funding ignored (small, mixed sign).
- Book   : 10% of capital per event (coin-leg notional), <= 10 live, arrival order, P&L at exit day.
- Control: the SAME events shifted 1–30 days (random sign) on the same coin, 20 replicates.
Survivorship: 37 delisted OosCoins are missing — biases a short AGAINST itself.

Run:  python3 research/j4big_book.py [--universe oos|backtest]
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from anomaly_fade_4h import BPH, N4, N24, WIN, load_15m  # noqa: E402
from anomaly_fade_bounce import K, events, mnr, zscore24  # noqa: E402

HOLD, DELAY, SLOT, MAX_LIVE, SHIFTS = 4 * BPH, 2, 0.10, 10, 20


def residuals_q(mk) -> tuple[np.ndarray, dict[int, tuple[np.ndarray, np.ndarray]]]:
    """anomaly_fade_bounce.residuals, also returning each day's (columns, q) so the hedge can be
    built from the very projection that defines the residual."""
    C = mk.close.values
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = np.full_like(r, np.nan)
    qs: dict[int, tuple[np.ndarray, np.ndarray]] = {}
    days = np.flatnonzero(mk.close.index == mk.close.index.normalize())
    for d0, d1 in zip(days, np.r_[days[1:], len(C)]):
        if d0 < WIN + 1:
            continue
        cols = np.array(mnr.liquid_universe(mk, d0 - WIN, d0, top=10_000))
        if len(cols) < 10:
            continue
        lr = mnr.log_returns(mk, d0 - WIN, d0, list(cols))
        _, vecs = np.linalg.eigh(np.cov(lr.T))
        q, _ = np.linalg.qr(np.column_stack([np.ones(len(cols)), vecs[:, ::-1][:, :K]]))
        R = r[d0:d1][:, cols]
        miss = np.isnan(R)
        R0 = np.where(miss, 0.0, R)
        E = R0 - (R0 @ q) @ q.T
        E[miss] = np.nan
        res[d0:d1, cols] = E
        qs[d0] = (cols, q)
    return res, qs


def funding_15m(mk, cache: str) -> np.ndarray:
    """Funding rate at its settlement 15m bar, 0 elsewhere (fraction per settlement)."""
    F = np.zeros(mk.close.shape)
    for j, s in enumerate(mk.close.columns):
        f = mnr.load_funding(s, cache)
        if f is None:
            continue
        f = f.copy(); f.index = f.index.floor("15min")
        f = f[~f.index.duplicated(keep="last")].reindex(mk.close.index).fillna(0.0)
        F[:, j] = f.values
    return F


def event_pnl(mk, res, qs, day_of, F, j: int, e: int, hedged: bool) -> float | None:
    """Net P&L (fraction of the coin-leg notional) of a 4h short entered at bar e's open."""
    C, O = mk.close.values, mk.open
    x = e + HOLD - 1
    if x >= len(C) or np.isnan(O[e, j]) or np.isnan(C[x, j]):
        return None
    cost = mnr.TAKER_COST_PER_SIDE
    fund = F[e:x + 1, j].sum()                                  # short receives positive funding
    if not hedged:
        return -math.log(C[x, j] / O[e, j]) + fund - 2 * cost
    d0 = day_of[e]
    if d0 not in qs:
        return None
    cols, q = qs[d0]
    k = np.flatnonzero(cols == j)
    if len(k) == 0:
        return None
    seg = res[e:x + 1, j]                                       # residual from bar e's open-ish
    if np.isnan(seg).mean() > 0.2:
        return None
    ei = np.zeros(len(cols)); ei[k[0]] = 1.0
    gross = np.abs(ei - q @ (q.T @ ei)).sum()                   # coin leg (≈1) + hedge legs
    return -np.nansum(seg) + fund - 2 * cost * gross


def book(rows: list[tuple[pd.Timestamp, pd.Timestamp, float]], lo, hi) -> dict:
    live, daily, n = [], {}, 0
    for t_in, t_out, pnl in sorted(rows, key=lambda z: z[0]):
        live = [x for x in live if x > t_in]
        if len(live) >= MAX_LIVE:
            continue
        live.append(t_out); n += 1
        d = t_out.normalize(); daily[d] = daily.get(d, 0.0) + SLOT * pnl
    s = pd.Series(daily).reindex(pd.date_range(lo, hi, freq="D"), fill_value=0.0)
    ann, vol = s.mean() * 365, s.std() * math.sqrt(365)
    wk = s.resample("7D").sum()
    eq = s.cumsum()
    return dict(n=n, ann=100 * ann, sharpe=ann / vol if vol > 0 else 0.0,
                t_wk=wk.mean() / wk.std() * math.sqrt(len(wk)) if wk.std() > 0 else 0.0,
                mdd=100 * (eq - eq.cummax()).min(), by_year=(s.groupby(s.index.year).sum() * 100).round(1).to_dict())


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--universe", choices=["backtest", "oos"], default="oos")
    a = ap.parse_args()
    mk = load_15m(mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins"), a.cache)
    print(f"{a.universe}: {mk.close.shape[1]} coins, {mk.close.index[0]} .. {mk.close.index[-1]}")
    res, qs = residuals_q(mk)
    _, z4 = zscore24(res, N4, WIN)
    _, z24 = zscore24(res, N24, WIN)
    nz = lambda x: np.nan_to_num(x, nan=-np.inf)
    trig = (nz(z4) >= 3) & (nz(z24) >= 3)
    Lres = np.nancumsum(np.nan_to_num(res), axis=0)
    F = funding_15m(mk, a.cache)
    days = np.flatnonzero(mk.close.index == mk.close.index.normalize())
    day_of = np.zeros(len(mk.close), dtype=int)
    for d0, d1 in zip(days, np.r_[days[1:], len(mk.close)]):
        day_of[d0:d1] = d0
    idx = mk.close.index
    ev = [(j, s + DELAY) for j in range(mk.close.shape[1])
          for _, s in events(Lres[:, j], trig[:, j], False, look=N4)]
    print(f"j4big events: {len(ev)} on {mk.close.shape[1]} coins")
    lo, hi = idx[0].normalize(), idx[-1].normalize()

    rng = np.random.default_rng(20261009)
    for hedged in (True, False):
        rows, pnls = [], []
        for j, e in ev:
            p = event_pnl(mk, res, qs, day_of, F, j, e, hedged)
            if p is None:
                continue
            rows.append((idx[e], idx[e + HOLD - 1], p)); pnls.append((idx[e], p))
        lo_ev = min(r[0] for r in rows).normalize()               # earliest event, not the first coin's
        real = book(rows, lo_ev, hi)
        pv = np.array([p for _, p in pnls]); tv = pd.Series(pv, index=[t for t, _ in pnls])
        # t of the EVENT mean, standard error clustered by day (events on one day share the market)
        g = tv.groupby(tv.index.normalize())
        S, nd = g.sum(), g.count()
        mu = S.sum() / nd.sum()
        t_day = mu / (math.sqrt(((S - nd * mu) ** 2).sum()) / nd.sum())
        mid = tv.index.sort_values()[len(tv) // 2]
        ctrl = []
        for _ in range(SHIFTS):
            rr = []
            for j, e in ev:
                k = int(rng.integers(96, 96 * 30)) * (1 if rng.random() < 0.5 else -1)
                e2 = e + k
                if e2 < WIN + 1 or e2 + HOLD >= len(idx):
                    continue
                p = event_pnl(mk, res, qs, day_of, F, j, e2, hedged)
                if p is not None:
                    rr.append((idx[e2], idx[e2 + HOLD - 1], p))
            ctrl.append(book(rr, lo_ev, hi))
        cs = np.array([c["sharpe"] for c in ctrl]); ca = np.array([c["ann"] for c in ctrl])
        name = "HEDGED (residual)" if hedged else "UNHEDGED (coin only)"
        print(f"\n── {name}: {len(pv)} events · net/event {100 * pv.mean():+.3f}% (t_day {t_day:.2f}, "
              f"halves {100 * tv[tv.index < mid].mean():+.3f} / {100 * tv[tv.index >= mid].mean():+.3f}) · win {100 * (pv > 0).mean():.0f}%")
        print(f"   book (10%/event, ≤{MAX_LIVE} live): {real['n']} taken · {real['ann']:+.1f}%/yr · Sharpe {real['sharpe']:.2f} · "
              f"t_wk {real['t_wk']:.2f} · maxDD {real['mdd']:.1f}%")
        print(f"   time-shift control ({SHIFTS}×): ann {np.median(ca):+.1f}%/yr (max {ca.max():+.1f}) · "
              f"Sharpe {np.median(cs):.2f} · real beats {(cs < real['sharpe']).mean():.0%}")
        print(f"   by year: {real['by_year']}")
    print("\nTrials: 2 (hedged, unhedged), all constants fixed before the run. Costs: taker every leg/side "
          f"({100 * mnr.TAKER_COST_PER_SIDE:.3f}%/side).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
