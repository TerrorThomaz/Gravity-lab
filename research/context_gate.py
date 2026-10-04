"""Context gates over grid fills: the causal form of "every strategy is a gated grid".

Phase 0 (grid_paths.py) showed that the strategies' TRIGGERS (a bullish BoS, say) are the
confirmation: they arrive with the bounce and cost 1-2% of entry price. A strategy's CONTEXT is
known before the dip, and that is what a resting rung can legitimately be gated on. Each gate below
is evaluated at the close of the ARMING bar t; the rung fills in bar t+1 (grid_paths' engine).

Gates, textbook, fixed before the first run (7 trials):

  grid_L / grid_S   both  ADX14 < 20                                   (Grid / GridShort ranging screen)
  dip               long  EMA50 > EMA200 and 40 ≤ RSI14 ≤ 55           (DipLong context, no BoS)
  rip               short EMA50 < EMA200 and 45 ≤ RSI14 ≤ 60           (RipShort context, no BoS)
  fade_s            short bearish divergence: close_t = max close of 24 bars and
                          RSI_t < max RSI over t−24..t−3               (FadeShort context, no BoS)
  swing             long  bullish divergence (mirror) and EMA50 > EMA200   (SwingLong context)
  fade_l            long  bullish divergence and EMA50 < EMA200            (FadeLong context)

Score per gate, at h ∈ {4, 12, 24}, with h = 12 the primary horizon fixed in advance:
  - value = gated fills' r(h) minus the mean r(h) of ALL fills with the same side, k and day,
    with a week-clustered t;
  - the null: the same gate matrix circularly shifted per coin by a random 7-60 days, 20 times
    (same gate fraction and autocorrelation, but no link to the fills);
  - the net level: gated fills' mean r(h) minus the 0.125% maker-in/taker-out cost.

The pass bar (7 trials, plus the ~15% inflation of the cluster t on nulls): value t ≥ 3, beating
all 20 shifts, the same sign in both universes, and a positive net level.

Run:  python3 research/context_gate.py --universe oos|backtest
"""

from __future__ import annotations

import argparse
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import COST_MT, SEAL, day_t, detrend, frame, mnr, run_engine  # noqa: E402

HV = (4, 12, 24)
SHIFTS = 20


def ema(X, n):
    return pd.DataFrame(X).ewm(span=n, adjust=False, min_periods=n).mean().values


def rsi(C, n=14):
    d = pd.DataFrame(C).diff()
    up = d.clip(lower=0).ewm(alpha=1 / n, adjust=False, min_periods=n).mean()
    dn = (-d.clip(upper=0)).ewm(alpha=1 / n, adjust=False, min_periods=n).mean()
    return (100 - 100 / (1 + up / dn)).values


def adx(H, L, C, n=14):
    H, L, C = (pd.DataFrame(x) for x in (H, L, C))
    upm, dnm = H.diff(), -L.diff()
    pdm = upm.where((upm > dnm) & (upm > 0), 0.0)
    ndm = dnm.where((dnm > upm) & (dnm > 0), 0.0)
    tr = pd.concat([H - L, (H - C.shift()).abs(), (L - C.shift()).abs()]).groupby(level=0).max()
    w = lambda x: x.ewm(alpha=1 / n, adjust=False, min_periods=n).mean()
    atr_ = w(tr)
    pdi, ndi = 100 * w(pdm) / atr_, 100 * w(ndm) / atr_
    dx = 100 * (pdi - ndi).abs() / (pdi + ndi)
    return w(dx).values


def gates(H, L, C):
    """Gate matrices (time × coin, bool), each known at the close of bar t. Returns {name: (side, G)}."""
    e50, e200, r, a = ema(C, 50), ema(C, 200), rsi(C), adx(H, L, C)
    Cd, Rd = pd.DataFrame(C), pd.DataFrame(r)
    hi24 = Cd.rolling(24).max().values
    lo24 = Cd.rolling(24).min().values
    rmax = Rd.shift(3).rolling(22).max().values          # RSI over t−24..t−3
    rmin = Rd.shift(3).rolling(22).min().values
    up, dn = e50 > e200, e50 < e200
    bear_div = (C >= hi24) & (r < rmax)
    bull_div = (C <= lo24) & (r > rmin)
    return {
        "grid_L": (1, a < 20), "grid_S": (-1, a < 20),
        "dip": (1, up & (r >= 40) & (r <= 55)),
        "rip": (-1, dn & (r >= 45) & (r <= 60)),
        "fade_s": (-1, bear_div),
        "swing": (1, bull_div & up),
        "fade_l": (1, bull_div & dn),
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()

    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, a.cache)
    keep = mk.close.index < SEAL                            # block A only
    times, syms = mk.close.index[keep], list(mk.close.columns)
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    fl, lab = run_engine(O, H, L, C)
    df = detrend(frame(fl, lab, times, syms), C, times, syms)
    for h in HV:                                            # deviation from the same side/k/day fill mean
        df[f"v{h}"] = df[f"r{h}"] - df.groupby(["side", "k", "day"])[f"r{h}"].transform("mean")
    t_arm, j = fl["t"], fl["j"]
    G = gates(H, L, C)
    rng = np.random.default_rng(20261006)
    print(f"{a.universe}: {len(syms)} coins, block A to {times[-1]:%Y-%m-%d}, {len(df):,} fills")
    print(f"\n   value = gated fills minus all fills of the same side/k/day (%, week-clustered t); "
          f"null = {SHIFTS} per-coin shifts of the gate ±7-60d")
    print(f"   {'gate':<8}{'side':<6}{'gated':>8}{'frac':>6}  " +
          "".join(f"{'h' + str(h) + ' value [t]  null p':>28}" for h in HV) +
          f"   {'net@12':>7}  {'e12 vs drift':>14}")
    T = len(times)
    for name, (side, M) in G.items():
        sel_side = (df.side.values == side)
        g = sel_side & M[t_arm, j]
        sub = df[g]
        cells = []
        for h in HV:
            m, t, _ = day_t(sub[f"v{h}"].values, sub.blk.values)
            nulls = []
            for _ in range(SHIFTS):
                off = rng.integers(7 * 24, 61 * 24) * rng.choice([-1, 1])
                gs = sel_side & np.roll(M, off, axis=0)[t_arm, j]
                nulls.append(np.nanmean(df[f"v{h}"].values[gs]))
            p = (1 + sum(n >= m for n in nulls)) / (1 + SHIFTS)
            cells.append(f"{m:+8.3f} [{t:+5.1f}]  p {p:.2f}")
        net = sub.r12.mean() - COST_MT
        e, te, _ = day_t(sub.e12.values, sub.blk.values)
        print(f"   {name:<8}{'long' if side > 0 else 'short':<6}{g.sum():>8}{g.sum() / sel_side.sum():>6.0%}  "
              + "".join(f"{c:>28}" for c in cells) + f"   {net:+7.3f}  {e:+7.3f} [{te:+4.1f}]")
    print(f"\n   net@12 = gated fills' mean r(12) − {COST_MT:.3f}% (includes the fill-bar wick gain m(1), unverified until the 1m check)")
    print("   Trials: 7 gates, fixed before the run. Pass: t ≥ 3 at h12, beats all shifts, same sign in both universes, net > 0.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
