"""Task 1 of docs/HANDOFF_BOUNCE_DCA_2026-10.md: label every bounce entry on REAL h1 candles.

Variant E mechanics (3x DCA, no rescue grid, no stop). The synthetic study found the rescue grid
never beat E, so C is not carried. The entry and DCA rules are those of research/bounce_dca_study.py:
- entry: drop >= 4*ATR(14) over 24 bars, RSI(14) < 30, close > the prior high; fill at the next open.
- DCA rungs at 2/4/6 * ATR_entry below the fill, sizes 1/1.5/2 (reserve = 5.5 base units).
- TP at +2% net on the average cost. No sell on a bar where a buy filled.
- 0.21%/round trip per unit, charged per leg.
- Funding: real per-symbol rate at settlement hours inside the cache's span, else the 0.01%/8h floor
  (FundingRateSession semantics).

Horizon N (30/90/180 days, or none): if the position has not hit TP N days after entry, it is closed
at that bar's close. That is a "dead bag". With no horizon, a bag still open at the end of the data
is marked to the last close and counted, never dropped.
An entry less than N days before the data ends cannot be labelled, so it counts as CENSORED.

Universe: Config.BacktestCoins only. OosCoins stays untouched for task 2.
Units: P&L in base-order units (base leg = 1.0 of quote).

Run:  python3 research/bounce_dca_label.py [--cache candle_cache] [--csv reports/bounce_dca_labels.csv]
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
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "scripts"))
import market_neutral_research as mnr  # noqa: E402
from bounce_dca_study import atr, rsi  # noqa: E402

LEG = mnr.TAKER_COST_PER_SIDE                     # 0.105% per leg = 0.21% round trip
FLOOR = mnr.FUNDING_FLOOR_PCT_PER_8H / 100.0
DCA_STEP_ATR, DCA_SIZES, TP = 2.0, (1.0, 1.5, 2.0), 0.02
RESERVED = 1.0 + sum(DCA_SIZES)
HORIZONS = (30, 90, 180, None)


def funding_per_bar(idx: pd.DatetimeIndex, f: pd.Series | None) -> np.ndarray:
    """Rate a long pays per unit of notional at each bar's open (positive = cost)."""
    rate = np.where(idx.hour % 8 == 0, FLOOR, 0.0)
    if f is not None and len(f):
        f = f.copy()
        f.index = f.index.floor("1h")
        f = f[~f.index.duplicated(keep="last")]
        inside = (idx >= f.index[0]) & (idx <= f.index[-1])
        rate[inside] = 0.0
        rate[idx.get_indexer(f.index[f.index.isin(idx)])] = f[f.index.isin(idx)].values
    return rate


def signal(h, l, c) -> np.ndarray:
    """Bounce entry on closed bar i: 24-bar drop >= 4*ATR, RSI < 30, close above the prior high."""
    A, R = atr(h, l, c), rsi(c)
    sig = np.zeros(len(c), bool)
    sig[31:] = (c[7:-24] - c[31:] >= 4.0 * A[31:]) & (R[31:] < 30.0) & (c[31:] > h[30:-1])
    return sig


def label_coin(o, h, l, c, fund, horizon_d: int | None, sig: np.ndarray | None = None) -> list[dict]:
    n = len(c)
    A = atr(h, l, c)
    sig = signal(h, l, c) if sig is None else sig
    hz = None if horizon_d is None else horizon_d * 24
    out, i = [], 31
    while i < n - 1:
        if not sig[i]:
            i += 1
            continue
        e, a0 = i + 1, A[i]
        rungs = [x for x in (o[e] - (k + 1) * DCA_STEP_ATR * a0 for k in range(3)) if x > 0]
        qty, cost, fees, fnd, nd, mae = 1.0 / o[e], 1.0, LEG, 0.0, 0, 0.0
        tp = (cost * (1 + TP) + fees) / (qty * (1 - LEG))
        exit_px, kind, j = None, "", e
        while j < n:
            fnd += fund[j] * qty * o[j]
            bought = j == e
            while nd < len(rungs) and l[j] <= rungs[nd]:
                px = min(rungs[nd], o[j])           # a gap down fills at the open
                qty += DCA_SIZES[nd] / px
                cost += DCA_SIZES[nd]
                fees += LEG * DCA_SIZES[nd]
                nd += 1
                bought = True
            mae = min(mae, (qty * l[j] * (1 - LEG) - cost - fees - fnd) / RESERVED)
            if not bought and h[j] >= tp:
                exit_px, kind = max(tp, o[j]), "tp"
            elif hz is not None and j - e >= hz:
                exit_px, kind = c[j], "dead"
            if exit_px is not None:
                break
            tp = (cost * (1 + TP) + fees + fnd) / (qty * (1 - LEG))
            j += 1
        if exit_px is None:                          # still open at the end of the data
            j, exit_px = n - 1, c[n - 1]
            # a horizon that could still have run out is censored; with no horizon it is a dead bag
            kind = "censored" if hz is not None else "open_end"
        pnl = qty * exit_px * (1 - LEG) - cost - fees - fnd
        out.append(dict(entry=e, exit=j, days=(j - e) / 24, kind=kind, dcas=nd, pnl=pnl,
                        mae=mae, funding=fnd))
        i = j + 1
    return out


def book_stats(x: pd.DataFrame, years: float, n_coins: int) -> dict:
    """Net P&L, day-clustered t, and %/yr on the capital one slot per coin must reserve."""
    day = x.groupby(x.time.dt.floor("D")).pnl.sum()
    full = day.reindex(pd.date_range(day.index.min(), day.index.max(), freq="D"), fill_value=0.0)
    return dict(net=x.pnl.sum(), t_day=full.mean() / (full.std(ddof=1) / math.sqrt(len(full))),
                yr_slot=100 * x.pnl.sum() / (RESERVED * n_coins * years))


def run_all(coins: dict, hd, rng=None) -> pd.DataFrame:
    rows = []
    for s, (df, fund) in coins.items():
        o, h, l, c = (df[k].values for k in ("open", "high", "low", "close"))
        sig = None
        if rng is not None:                      # control: same per-coin signal rate, random bars
            real = signal(h, l, c)
            sig = np.zeros(len(c), bool)
            sig[31:] = rng.random(len(c) - 31) < real.mean() * len(c) / (len(c) - 31)
        for t in label_coin(o, h, l, c, fund, hd, sig):
            t.update(sym=s, horizon=hd or 0, time=df.index[t["entry"]])
            rows.append(t)
    return pd.DataFrame(rows)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--csv", default=os.path.join(mnr.REPO, "reports", "bounce_dca_labels.csv"))
    ap.add_argument("--controls", type=int, default=20)
    a = ap.parse_args()

    coins = {}
    for s in mnr.config_symbols("BacktestCoins"):
        df = mnr.load_h1(s, a.cache)
        if df is None or len(df) < 24 * 60:
            continue
        coins[s] = (df, funding_per_bar(df.index, mnr.load_funding(s, a.cache)))
    print(f"BacktestCoins with h1 data: {len(coins)}")

    lab = pd.concat([run_all(coins, hd) for hd in HORIZONS], ignore_index=True)
    os.makedirs(os.path.dirname(a.csv), exist_ok=True)
    lab.to_csv(a.csv, index=False)

    print("\n=== Variant E on real candles, P&L in base-order units (reserve = 5.5 per position)")
    print(f"{'horizon':>8s} {'n':>6s} {'dca%':>6s} {'tp':>6s} {'dead':>5s} {'cens':>5s} "
          f"{'dead%':>6s} {'winP&L':>8s} {'deadP&L':>8s} {'censP&L':>8s} {'net':>8s} "
          f"{'mean%res':>8s} {'t':>6s} {'d2tp_p50':>8s} {'d2tp_p90':>8s} {'worstMAE%':>9s}")
    for hd in HORIZONS:
        x = lab[lab.horizon == (hd or 0)]
        dead = x[x.kind.isin(["dead", "open_end"])]
        tp, cen = x[x.kind == "tp"], x[x.kind == "censored"]
        r = x.pnl / RESERVED * 100
        t = r.mean() / (r.std(ddof=1) / math.sqrt(len(r)))
        print(f"{str(hd or 'none'):>8s} {len(x):6d} {100 * (x.dcas > 0).mean():6.1f} {len(tp):6d} "
              f"{len(dead):5d} {len(cen):5d} {100 * len(dead) / max(1, len(x) - len(cen)):6.2f} "
              f"{tp.pnl.sum():8.1f} {dead.pnl.sum():8.1f} {cen.pnl.sum():8.1f} {x.pnl.sum():8.1f} "
              f"{r.mean():8.3f} {t:6.2f} {tp.days.median():8.1f} {tp.days.quantile(.9):8.1f} "
              f"{100 * x.mae.min():9.1f}")

    # per coin, no horizon (the proposal as stated: never close at a loss)
    x = lab[lab.horizon == 0]
    g = x.groupby("sym")
    pc = pd.DataFrame({
        "n": g.size(), "dca%": 100 * g.dcas.apply(lambda d: (d > 0).mean()),
        "open_end": g.kind.apply(lambda k: (k == "open_end").sum()),
        "winP&L": g.apply(lambda d: d.pnl[d.kind == "tp"].sum(), include_groups=False),
        "bagP&L": g.apply(lambda d: d.pnl[d.kind == "open_end"].sum(), include_groups=False),
        "net": g.pnl.sum(), "worstMAE%": 100 * g.mae.min(),
        "maxDays": g.days.max()}).sort_values("net")
    with pd.option_context("display.max_rows", 200, "display.width", 140,
                           "display.float_format", "{:.2f}".format):
        print("\n=== per coin, no horizon (open bags marked at the last close)")
        print(pc)
    print(f"\ncoins net-negative: {(pc.net < 0).sum()}/{len(pc)}; "
          f"coins with an open bag: {(pc.open_end > 0).sum()}")
    print(f"labels -> {a.csv}")

    # Control: does the bounce signal beat random entries under the same mechanics? On survivor
    # coins with no stop, ANY long eventually recovers; only the gap to this control is the signal's.
    years = np.mean([(df.index[-1] - df.index[0]).days / 365 for df, _ in coins.values()])
    print(f"\n=== real vs random entries (same per-coin rate), {a.controls} seeds; "
          f"%/yr is on 5.5 reserved per coin, avg history {years:.1f}y")
    print(f"{'horizon':>8s} {'real net':>9s} {'t_day':>6s} {'%/yr':>6s} | {'rand net p50':>12s} "
          f"{'p90':>7s} {'%/yr p50':>8s} {'real>rand':>9s}")
    for hd in (180, None):
        r = book_stats(lab[lab.horizon == (hd or 0)], years, len(coins))
        ctl = [book_stats(run_all(coins, hd, np.random.default_rng(k)), years, len(coins))
               for k in range(a.controls)]
        cn = np.array([c["net"] for c in ctl])
        print(f"{str(hd or 'none'):>8s} {r['net']:9.1f} {r['t_day']:6.2f} {r['yr_slot']:6.2f} | "
              f"{np.median(cn):12.1f} {np.percentile(cn, 90):7.1f} "
              f"{np.median([c['yr_slot'] for c in ctl]):8.2f} {100 * (r['net'] > cn).mean():8.0f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
