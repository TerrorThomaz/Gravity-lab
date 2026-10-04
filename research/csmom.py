"""Cross-sectional momentum (Liu-Tsyvinski-Wu style), pre-registered in docs/EDGE_DISCOVERY_SCOPE_2026-10.md.

Weekly: rank eligible coins by their past L-week log return, long the top quintile and short the
bottom (dollar-neutral, equal weight), trade at the next open, hold one week. Taker costs on
turnover, real funding. Block A gates block B: B is run once, only after A passes.

Run:  python3 research/csmom.py --block A
      python3 research/csmom.py --block B        (only if A passed)
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

SEAL = pd.Timestamp("2025-07-01")
LOOKBACKS = (3, 1, 2, 4)                # weeks; 3 is PRIMARY
PLACEBOS = 500
CACHE = os.path.join(mnr.REPO, "candle_cache")


def nw_t(x: np.ndarray) -> float:
    x = x[np.isfinite(x)]
    d = x - x.mean()
    v = (d @ d + 2 * 0.5 * (d[1:] @ d[:-1])) / len(x)
    return float(x.mean() / math.sqrt(max(v, 1e-18) / len(x)))


def book(mk, L: int, block: str, rng=None):
    """Weekly net returns (fraction) of the long-short book, plus diagnostics. rng: shuffle ranks (placebo)."""
    idx = mk.close.index
    O, C, Q = mk.open.values, mk.close.values, mk.qvol.values
    rate, known = mk.funding.values, mk.funding_known.reindex(mk.close.columns).fillna(False).values
    fcum = np.cumsum(rate, axis=0)
    T, N = C.shape
    reb = np.where((idx.dayofweek == 0) & (idx.hour == 0))[0]
    reb = reb[(reb >= L * 168) & (reb + 1 < T)]
    in_blk = (idx[reb] < SEAL) if block == "A" else (idx[reb] >= SEAL)
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
    Of = pd.DataFrame(O).ffill().values                       # mark a coin with no price at exit at its last price
    qv30 = pd.DataFrame(Q).rolling(720, min_periods=360).sum().values
    w_prev = np.zeros(N)
    out, mkt, longs, shorts, dates = [], [], [], [], []
    rows = list(zip(reb[:-1], reb[1:]))
    for a, b in rows:
        if not (idx[a] < SEAL if block == "A" else idx[a] >= SEAL):
            continue
        past = lc[a] - lc[a - L * 168]
        ok = np.isfinite(past) & np.isfinite(O[a + 1]) & np.isfinite(qv30[a])
        if ok.sum() < 10:
            continue
        qcut = np.nanquantile(qv30[a][ok], 0.2)
        ok &= qv30[a] >= qcut
        ids = np.where(ok)[0]
        score = past[ids] if rng is None else rng.permutation(past[ids])
        order = ids[np.argsort(score)]
        q = max(1, len(order) // 5)
        w = np.zeros(N)
        w[order[-q:]] = 0.5 / q
        w[order[:q]] = -0.5 / q
        e0, e1 = a + 1, min(b + 1, T - 1)
        r = np.where(w != 0, np.exp(np.log(Of[e1]) - np.log(O[e0])) - 1, 0.0)
        r = np.nan_to_num(r)
        gross = float(w @ r)
        turn = float(np.abs(w - w_prev).sum())
        cost = turn * mnr.TAKER_COST_PER_SIDE
        fund = np.where(known, (fcum[e1] - fcum[e0]), mnr.FUNDING_FLOOR_PCT_PER_8H / 100 * (e1 - e0) / 8 * np.sign(w))
        fpnl = -float(w @ np.nan_to_num(fund))                # long pays a positive rate; the floor costs |w| either side
        out.append(gross - cost + fpnl)
        mr = np.nanmean(np.exp(np.log(Of[e1][ids]) - np.log(O[e0][ids])) - 1)
        dates.append(idx[a]); mkt.append(mr); longs.append(float(np.mean(r[order[-q:]]))); shorts.append(float(np.mean(r[order[:q]])))
        w_prev = w
    return np.array(out), np.array(mkt), np.array(longs), np.array(shorts), dates


def report(universe: str, block: str) -> bool:
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if universe == "oos" else "BacktestCoins"), CACHE)
    print(f"\n[{universe}] block {block}")
    passed = True
    for L in LOOKBACKS:
        r, m, lo, sh, dates = book(mk, L, block)
        t = nw_t(r)
        h = len(r) // 2
        ann, vol = r.mean() * 52, r.std() * math.sqrt(52)
        eq = np.cumsum(np.log1p(r))
        mdd = float((eq - np.maximum.accumulate(eq)).min())
        beta = float(np.cov(r, m)[0, 1] / np.var(m)) if len(r) > 2 else float("nan")
        line = (f"   L={L}w{' [PRIMARY]' if L == 3 else '          '}  weeks {len(r)}  mean {100 * r.mean():+.3f}%/wk [t {t:+.2f}]  "
                f"ann {100 * ann:+.1f}%  vol {100 * vol:.1f}%  Sharpe {ann / vol if vol else float('nan'):+.2f}  maxDD {100 * (math.exp(mdd) - 1):+.1f}%  "
                f"β {beta:+.2f}  halves {100 * r[:h].mean():+.3f} / {100 * r[h:].mean():+.3f}  winners-leg {100 * lo.mean():+.3f} losers-leg {100 * sh.mean():+.3f}")
        print(line)
        if L == 3:
            yrs = pd.Series(r, index=pd.DatetimeIndex(dates)).groupby(lambda d: d.year).agg(lambda x: 100 * x.sum())
            print("      by year (sum %/yr): " + "  ".join(f"{y} {v:+.1f}" for y, v in yrs.items()))
            if block == "A":
                rng = np.random.default_rng(20261011)
                pl = np.array([book(mk, 3, block, rng)[0].mean() for _ in range(PLACEBOS)])
                pct = float((pl < r.mean()).mean())
                print(f"      random-ranking placebos: real beats {pct:.1%} (placebo mean {100 * pl.mean():+.3f}%/wk, 95th pct {100 * np.quantile(pl, 0.95):+.3f})")
                passed = r.mean() > 0 and t >= 2 and r[:h].mean() > 0 and r[h:].mean() > 0 and pct >= 0.95
            else:
                passed = r.mean() > 0
            print(f"      → {'PASS' if passed else 'FAIL'} (pre-registered block-{block} rule)")
    return passed


class _Synth:
    """Minimal stand-in for mnr.Market: hourly prices with a planted (or no) weekly momentum."""
    def __init__(self, mom: float, seed: int, N: int = 40, weeks: int = 160):
        rng = np.random.default_rng(seed)
        T = weeks * 168
        drift = np.zeros((T, N))
        w = rng.normal(0, 0.03, N)                             # per-coin weekly drift, persistent across weeks when mom > 0
        for k in range(weeks):
            w = mom * w + math.sqrt(1 - mom ** 2) * rng.normal(0, 0.03, N)   # AR(1), variance-preserving
            drift[k * 168:(k + 1) * 168] = w / 168
        lp = np.cumsum(drift + rng.normal(0, 0.004, (T, N)), axis=0)
        self.close = pd.DataFrame(np.exp(lp), index=pd.date_range("2021-01-04", periods=T, freq="1h"))
        self.open = self.close.shift(1).bfill()
        self.qvol = pd.DataFrame(1e6, index=self.close.index, columns=self.close.columns)
        self.funding = pd.DataFrame(0.0, index=self.close.index, columns=self.close.columns)
        self.funding_known = pd.Series(True, index=self.close.columns)


def selftest() -> int:
    r0 = np.concatenate([book(_Synth(0.0, s), 3, "A")[0] for s in (1, 2, 3)])
    assert r0.mean() < 0.001 and abs(nw_t(r0)) < 3, f"no-momentum world earned {100 * r0.mean():+.3f}%/wk t {nw_t(r0):+.2f}"
    r1 = book(_Synth(0.9, 4), 3, "A")[0]
    assert r1.mean() > 0 and nw_t(r1) > 3, f"planted momentum not found: {100 * r1.mean():+.3f}%/wk t {nw_t(r1):+.2f}"
    print(f"selftest: OK (no momentum {100 * r0.mean():+.3f}%/wk ≈ −costs; planted {100 * r1.mean():+.3f}%/wk t {nw_t(r1):+.1f})")
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    ap = argparse.ArgumentParser()
    ap.add_argument("--block", choices=["A", "B"], required=True)
    a = ap.parse_args()
    res = [report(u, a.block) for u in ("backtest", "oos")]
    print(f"\nVERDICT block {a.block}: {'PASS' if all(res) else 'FAIL'} (both universes required)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
