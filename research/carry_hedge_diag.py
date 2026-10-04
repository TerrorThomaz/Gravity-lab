"""What hedges carry? Diagnosis on seen data (INFORMATION, no pass rule).

1. Carry's worst days by component: long/short leg price, long/short leg funding, costs.
2. Candidate hedges built with carry's own machinery (daily rebalance, liquid top 40, quintiles, 0.5%
   band, taker costs, real funding), each judged on: correlation with carry, mean on carry's worst 5%
   days, standalone Sharpe and halves.
   XSMOM-n   long the 7d winners, short the 7d losers, projected off the same anchors as carry
             (dollar + top-3 PCs). Rationale: high funding = crowded longs = recent winners, so carry
             is structurally SHORT momentum; its losses should be momentum's gains.
   XSMOM-raw the same, dollar-neutral only.
   ALTSEASON equal-weight alts long, BTC short (carry's short leg is speculative alts).
   TREND     the frozen BTC+ETH trend hybrid.

Run:  python3 research/carry_hedge_diag.py [--universe oos|backtest]
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402
from power_check import CACHE, sharpe  # noqa: E402
from carry_hedge import carry_targets  # noqa: E402


def legs(mk, targets, band):
    """run_book's loop, split into components per hour."""
    C, Fd, K = mk.close.values, mk.funding.values, mk.funding_known.values
    n, m = C.shape
    ret = np.zeros_like(C)
    with np.errstate(invalid="ignore", divide="ignore"):
        ret[1:] = C[1:] / C[:-1] - 1.0
    ret = np.nan_to_num(ret, nan=0.0, posinf=0.0, neginf=0.0)
    out = {k: np.zeros(n) for k in ("long price", "short price", "long funding", "short funding", "costs")}
    h = np.zeros(m)
    for t in range(n):
        if t > 0 and h.any():
            L, S = h > 0, h < 0
            fr = np.where(K, Fd[t], 0.0)
            out["long price"][t], out["short price"][t] = h[L] @ ret[t, L], h[S] @ ret[t, S]
            out["long funding"][t], out["short funding"][t] = -(h[L] @ fr[L]), -(h[S] @ fr[S])
            h = h * (1.0 + ret[t])
        tg = targets.get(t)
        if tg is not None:
            new = np.where(~np.isnan(C[t]) & (np.abs(tg - h) >= band), tg, h)
            out["costs"][t] = -np.abs(new - h).sum() * mnr.COST_PER_SIDE
            h = new
    return pd.DataFrame(out, index=mk.close.index).resample("1D").sum()


def xsmom(mk, neutral: bool, look_h: int = 24 * 7):
    p = mnr.CarryParams(band=0.005)
    n, m = mk.close.shape
    targets = {}
    for t in range(max(p.warmup_h, p.cov_h), n - p.delay, p.rebalance_h):
        uni = mnr.liquid_universe(mk, t - p.cov_h, t + 1, p.universe_top)
        if len(uni) < 10:
            continue
        lr = mnr.log_returns(mk, t - p.cov_h, t + 1, uni)
        sig = lr[-look_h:].sum(axis=0)
        k = max(2, int(len(uni) * p.quantile))
        o = np.argsort(sig)
        wu = np.zeros(len(uni))
        wu[o[-k:]], wu[o[:k]] = 1.0 / k, -1.0 / k
        if neutral:
            wu = mnr.factor_neutral(wu, lr, p.factors)
        g = np.abs(wu).sum()
        if g <= 1e-12:
            continue
        w = np.zeros(m)
        w[uni] = wu / g
        targets[t + p.delay] = w
    r = mnr.run_book(mk.close.values, mk.funding.values, mk.funding_mask.values, mk.funding_known.values,
                     targets, mk.close.index, band=p.band)
    return pd.Series(r.net, index=mk.close.index).resample("1D").sum()


def altseason(mk):
    cols = list(mk.close.columns)
    n, m = mk.close.shape
    targets = {}
    if "BTCUSDT" not in cols:
        return None
    b = cols.index("BTCUSDT")
    for t in range(24 * 30, n - 1, 24):
        live = [j for j in mnr.liquid_universe(mk, t - 24 * 30, t + 1, 40) if j != b]
        if len(live) < 10:
            continue
        w = np.zeros(m)
        w[live] = 0.5 / len(live)
        w[b] = -0.5
        targets[t + 1] = w
    r = mnr.run_book(mk.close.values, mk.funding.values, mk.funding_mask.values, mk.funding_known.values,
                     targets, mk.close.index, band=0.005)
    return pd.Series(r.net, index=mk.close.index).resample("1D").sum()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", default="oos", choices=["oos", "backtest"])
    a = ap.parse_args()
    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, CACHE)
    mk_alt = mk if "BTCUSDT" in syms else mnr.build_market(syms + ["BTCUSDT"], CACHE)   # BTC only for ALTSEASON
    targets, band, _ = carry_targets(mk)
    comp = legs(mk, targets, band)
    carry = comp.sum(axis=1)
    carry = carry[carry.ne(0).cumsum() > 0].iloc[:-1]
    comp = comp.reindex(carry.index)
    worst = carry <= carry.quantile(0.05)
    print(f"[{a.universe}] carry {carry.index[0]:%Y-%m-%d} → {carry.index[-1]:%Y-%m-%d}, Sharpe {sharpe(carry):.2f}")
    print("  carry's worst 5% days, mean %/day by component: "
          + ", ".join(f"{c} {100 * comp[c][worst].mean():+.3f}" for c in comp.columns)
          + f"  (total {100 * carry[worst].mean():+.3f})")
    print("  all days, mean %/day: " + ", ".join(f"{c} {100 * comp[c].mean():+.3f}" for c in comp.columns))

    import binance_trend as bt
    d, O, C, F, K = bt.load()
    cands = {"XSMOM-n (7d, neutral)": xsmom(mk, True), "XSMOM-raw (7d)": xsmom(mk, False),
             "ALTSEASON (alts − BTC)": altseason(mk_alt),
             "TREND hybrid": pd.Series(bt.sleeve(O, C, F, K, hybrid=True)[0], index=d)}
    rows = []
    for name, x in cands.items():
        if x is None:
            continue
        x = x.reindex(carry.index).fillna(0.0)
        h = len(x) // 2
        rows.append({"candidate": name, "Sharpe": sharpe(x), "half1": sharpe(x.iloc[:h]), "half2": sharpe(x.iloc[h:]),
                     "ρ with carry": float(np.corrcoef(x, carry)[0, 1]),
                     "ρ on carry's worst 10% days": float(np.corrcoef(x[carry <= carry.quantile(0.1)],
                                                                      carry[carry <= carry.quantile(0.1)])[0, 1]),
                     "on carry's worst 5% days %/d": 100 * x[worst].mean(),
                     "carry + x (inv-vol 50/50 risk) Sharpe": sharpe(carry / carry.std() + x / x.std())})
    with pd.option_context("display.width", 220, "display.float_format", lambda v: f"{v:+,.3f}"):
        print(pd.DataFrame(rows).to_string(index=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
