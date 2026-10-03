"""One principle across the strategies: FADE systematic overextension, FOLLOW idiosyncratic trends.

Evidence it rests on (2026-10-02/03): Grid's dip-buying pays on market-wide flushes and is ~0 once
hedged to the coin's own move; FadeShort's edge was market timing; liquid coins' 24h residual moves
continue; RipShort separates when the downtrend is the coin's own (p 0.001, crash-dependent).

Fixed before the first run — 4 new trials (RipShort already tested):
  family path   = cumulative hourly return explained by $ + top-3 PCs of the trailing 30d covariance
                  (refit daily, ripshort_regime_gate.family_bear's construction); trend state with
                  RipShort's rule, both ways: DOWN = path < EMA138 and EMA138 below its value 34 bars
                  earlier; UP = path > EMA138 and EMA138 above it.
  FadeShort  (fades a pump)            keep if family UP
  DipLong    (buys a dip)              keep if family DOWN
  SwingLong  (buys a reversal)         keep if family DOWN
  FadeLong   (buys an oversold bounce) keep if family DOWN
  (RipShort  (follows a downtrend)     keep if family NOT DOWN — tested 2026-10-03, reported for reference)
Test set: OosCoins (never trained, and the principle was not derived from these strategies);
BacktestCoins as a second check. Pass = kept book profitable (mean > 0, PF > 1, day-clustered t ≳ 2),
beats the dropped trades and 1,000 random same-fraction gates, holds in both halves, AND survives
removing its best 3 months (the RipShort lesson: crash months must not masquerade as edge).

Run:  python3 research/family_principle.py --trades <edgetest raw csv> --universe oos|backtest
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
from anomaly_fade_bounce import mnr, residuals  # noqa: E402
from ripshort_regime_gate import EMA_N, SLOPE_N, line, stats  # noqa: E402

RULES = {"FadeShort": "up", "DipLong": "down", "SwingLong": "down", "FadeLong": "down"}
DRAWS = 1000


def family_states(mk) -> tuple[pd.DataFrame, pd.DataFrame]:
    C = mk.close.values
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = residuals(mk)
    fac = np.where(np.isnan(res) | np.isnan(r), np.nan, r - res)
    path = pd.DataFrame(np.nancumsum(np.nan_to_num(fac), axis=0), index=mk.close.index, columns=mk.close.columns)
    known = pd.DataFrame(~np.isnan(fac), index=path.index, columns=path.columns).cumsum() > EMA_N
    ema = path.ewm(span=EMA_N, adjust=False).mean()
    down = ((path < ema) & (ema < ema.shift(SLOPE_N))).where(known)
    up = ((path > ema) & (ema > ema.shift(SLOPE_N))).where(known)
    return up, down


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", required=True)
    ap.add_argument("--universe", choices=["backtest", "oos"], required=True)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()

    tr = pd.read_csv(a.trades, parse_dates=["entry_time"]).rename(columns={"entry_time": "entry", "return_pct": "ret"})
    tr = tr[tr.strategy.isin(RULES)]
    tr["bar"] = tr.entry.dt.floor("1h") - pd.Timedelta(hours=1)
    mk = mnr.build_market(sorted(set(tr.symbol) | {"BTCUSDT"}), a.cache)
    up, down = family_states(mk)
    for name, df in (("fam_up", up), ("fam_down", down)):
        st = df.stack(future_stack=True).rename(name).reset_index(); st.columns = ["bar", "symbol", name]
        tr = tr.merge(st, on=["bar", "symbol"], how="left")
    tr = tr.dropna(subset=["fam_up", "fam_down"]).copy()
    tr["fam_up"], tr["fam_down"] = tr.fam_up.astype(bool), tr.fam_down.astype(bool)
    print(f"{a.universe}: {len(tr)} trades with family state, {tr.entry.min():%Y-%m-%d} → {tr.entry.max():%Y-%m-%d}")

    rng = np.random.default_rng(20261014)
    for s, side in RULES.items():
        t = tr[tr.strategy == s].sort_values("entry").reset_index(drop=True)
        keep = t.fam_up if side == "up" else t.fam_down
        k, d = t[keep], t[~keep]
        sk, mid = stats(k), t.entry.quantile(0.5)
        null = np.array([t.ret.values[rng.random(len(t)) < keep.mean()].mean() for _ in range(DRAWS)])
        months = k.groupby(k.entry.dt.to_period("M")).ret.sum().sort_values(ascending=False)
        ex3 = k[~k.entry.dt.to_period("M").isin(months.head(3).index)]
        print(f"\n── {s}: keep if family {side.upper()} — keeps {100 * keep.mean():.0f}%")
        print(line("   ALL", stats(t)))
        print(line("   KEPT", sk))
        print(line("   dropped", stats(d)))
        print(line("   kept, 1st half", stats(k[k.entry < mid])))
        print(line("   kept, 2nd half", stats(k[k.entry >= mid])))
        print(line("   kept, minus best 3 months", stats(ex3)))
        print(f"   vs {DRAWS} random gates: kept mean beats {(null < sk['mean']).mean():.1%} (p = {(1 + (null >= sk['mean']).sum()) / (1 + DRAWS):.3f})"
              f" · best months {months.head(3).round(0).to_dict()}")
        print("   kept by year: " + "  ".join(f"{y} {g.ret.mean():+.3f}% (n {len(g)})" for y, g in k.groupby(k.entry.dt.year)))
    print("\nTrials: 4 (FadeShort, DipLong, SwingLong, FadeLong), fixed before the run; honest stop fills; edgetest net % per trade.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
