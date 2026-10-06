"""Cash-and-carry basis book: long spot + short perp (equal notional), entered when funding is high.

Pre-registration: docs/BASIS_CARRY_2026-10.md (frozen at the commit that adds this file).

Run:  python3 research/basis_carry.py --universe oos|backtest
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
from grid_paths import mnr  # noqa: E402

EXT = os.path.join(os.path.dirname(os.path.realpath(os.path.join(mnr.REPO, "candle_cache"))), "data", "external")
ENTER, EXIT = 15.0, 5.0          # %/yr, trailing 7d mean funding
SLOT, MAX_POS, SHIFTS = 0.05, 20, 20
FEES_RT = 0.2 + 0.11             # spot 0.1%/side + perp taker 0.055%/side, % round trip
HALF_SPREAD_BP = (4.2, 2.9, 1.8, 0.6)


def load(u: str):
    syms = mnr.config_symbols("OosCoins" if u == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, os.path.join(mnr.REPO, "candle_cache"))
    spot = {}
    for s in mk.close.columns:
        p = os.path.join(EXT, f"{s}_spot1h.csv")
        if os.path.exists(p) and mk.funding_known.get(s, False):
            d = pd.read_csv(p)
            spot[s] = pd.Series(d.close.values, index=pd.to_datetime(d.ts, unit="ms"))
    cols = list(spot)
    idx = mk.close.index
    S = pd.DataFrame(spot).reindex(index=idx, columns=cols)
    P = mk.close[cols]
    F = mk.funding[cols].where(mk.funding_mask[cols], 0.0)
    Q = mk.qvol[cols]
    # daily grid at 00:00 labels: decisions use data through the 00:00 settlement; trade at that bar's close (01:00)
    days = idx[idx.hour == 0]
    f7 = F.rolling(24 * 7).sum() / 7 * 365 * 100                              # %/yr, trailing 7 days incl. 00:00
    liq = Q.rolling(720, min_periods=240).median()
    return days, cols, S, P, F, f7, liq


def simulate(days, cols, S, P, F, f7, liq, sig=None):
    """Daily loop. sig: optional f7 replacement (time-shift null)."""
    sig = f7 if sig is None else sig
    held: dict[str, float] = {}                                                # sym -> entry cost already charged
    pnl = pd.Series(0.0, index=days)
    nlog = []
    cf = F.cumsum()
    for d0, d1 in zip(days[:-1], days[1:]):
        s0, s1 = S.loc[d0], S.loc[d1]
        p0, p1 = P.loc[d0], P.loc[d1]
        day = 0.0
        for s in list(held):                                                   # carry existing positions d0 → d1
            if not (np.isfinite(s0[s]) and np.isfinite(s1[s]) and np.isfinite(p0[s]) and np.isfinite(p1[s])):
                continue
            basis = (s1[s] / s0[s] - 1) - (p1[s] / p0[s] - 1)
            fund = cf[s].loc[d1] - cf[s].loc[d0]                               # settlements after d0's close .. d1
            day += (basis + fund) * 100
        f = sig.loc[d0]
        lq = liq.loc[d0]
        q = np.searchsorted(np.nanquantile(lq.dropna(), [0.25, 0.5, 0.75]), lq.fillna(0), side="right") if lq.notna().sum() >= 8 else np.zeros(len(cols), int)
        hs = dict(zip(cols, np.array(HALF_SPREAD_BP)[q] / 100))
        cost = 0.0
        for s in list(held):                                                   # exits
            if not np.isfinite(f[s]) or f[s] < EXIT:
                cost += FEES_RT / 2 + 2 * hs[s] * 1.0
                del held[s]
        room = MAX_POS - len(held)
        cand = f[(f > ENTER) & np.isfinite(s0) & np.isfinite(p0)].drop(list(held), errors="ignore").sort_values(ascending=False)
        for s in cand.index[:max(room, 0)]:                                     # entries, highest funding first
            cost += FEES_RT / 2 + 2 * hs[s]
            held[s] = 1.0
        pnl[d1] = (day - cost) * SLOT
        nlog.append(len(held))
    return pnl, np.mean(nlog)


def stats(pnl: pd.Series, label: str):
    wk = pnl.resample("W").sum()
    ann = pnl.mean() * 365
    sr = pnl.mean() / pnl.std() * math.sqrt(365) if pnl.std() > 0 else np.nan
    t = wk.mean() / wk.std() * math.sqrt(len(wk)) if wk.std() > 0 else np.nan
    cum = pnl.cumsum()
    dd = (cum - cum.cummax()).min()
    h = len(pnl) // 2
    last = pnl[pnl.index >= pnl.index[-1] - pd.Timedelta(days=365)].sum()
    yr = pnl.groupby(pnl.index.year).sum()
    print(f"   {label:<34} {ann:+6.2f}%/yr  Sharpe {sr:+.2f}  t_wk {t:+.2f}  maxDD {dd:+.2f}%  "
          f"halves {pnl.iloc[:h].mean() * 365:+.2f} / {pnl.iloc[h:].mean() * 365:+.2f}  last 12m {last:+.2f}%  "
          + " ".join(f"{y}:{v:+.1f}" for y, v in yr.items()))
    return dict(ann=ann, t=t, dd=dd, h1=pnl.iloc[:h].mean(), h2=pnl.iloc[h:].mean(), last=last)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    a = ap.parse_args()
    rng = np.random.default_rng(20261006)
    days, cols, S, P, F, f7, liq = load(a.universe)
    first = S.apply(lambda c: c.first_valid_index()).min()
    days = days[days >= first + pd.Timedelta(days=8)]
    print(f"{a.universe}: {len(cols)} coins with spot + perp + real funding; {days[0]:%Y-%m-%d} → {days[-1]:%Y-%m-%d}")
    pnl, avg = simulate(days, cols, S, P, F, f7, liq)
    res = stats(pnl, f"PRIMARY timed carry (avg {avg:.1f} pos)")
    base_cols = [c for c in ("BTCUSDT", "ETHUSDT") if c in cols]
    if base_cols:
        always = pd.DataFrame(np.inf, index=f7.index, columns=cols)
        always[[c for c in cols if c not in base_cols]] = -np.inf
        b, _ = simulate(days, cols, S, P, F, always, liq)
        stats(b / (SLOT * len(base_cols)), f"baseline: always-on {'+'.join(c[:3] for c in base_cols)} (50/50)")
    nulls = []
    for _ in range(SHIFTS):
        sh = f7.copy()
        for c in cols:
            sh[c] = f7[c].shift(int(rng.integers(30, 181)) * 24 * int(rng.choice([-1, 1])))
        nulls.append(simulate(days, cols, S, P, F, sh, liq)[0].mean() * 365)
    print(f"   time-shifted signal: real {res['ann']:+.2f}%/yr beats {sum(res['ann'] > n for n in nulls)}/{SHIFTS} "
          f"(null median {np.median(nulls):+.2f})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
