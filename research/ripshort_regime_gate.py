"""Does RipShort pay when the coin's downtrend is its OWN, not its market's?

Post-hoc observation (2026-10-03, OOS edgetest of the clean 09-23 RipShort retrain): PF 0.96 in BTC
Bear (its home, 12,594 trades) but 1.19 in BTC Ranging (1,238). Hypothesis: RipShort shorts a coin
in its own downtrend; that pays when the weakness is idiosyncratic and gets squeezed when the whole
market is falling and relief rallies are market-wide. The OOS result GENERATED the hypothesis, so
the TEST is BacktestCoins: RipShort's GA trained only inside BTC-bear windows, so its non-bear trades
there are as untrained as OOS ones, and the hypothesis never saw them. (Its BTC-bear trades on
BacktestCoins overlap training — that inflates the DROPPED group, making the test harder to pass.)

Fixed before the first run — 2 trials:
  A  BTC gate    : keep a trade only if BTC's legacy regime at the last closed hour is not Bear.
  B  family gate : keep a trade only if the coin's SYSTEMATIC path is not in a downtrend. Systematic
                   path = cumulative (hourly return − residual), residual off $ + top-3 PCs of the
                   trailing 30d covariance refit daily (anomaly_fade_bounce.residuals) — BTC dominates
                   PC1 but every co-movement family enters. "Downtrend" = RipShort's own regime rule
                   on that path with the genotype's numbers: below EMA(138) and EMA(138) lower than
                   34 bars earlier.
Read: kept trades must be profitable (mean > 0, PF > 1, day-clustered t ≳ 2) on BacktestCoins, beat
the dropped ones, hold in both halves, and beat 1,000 random gates keeping the same fraction.

Run:  python3 research/ripshort_regime_gate.py --trades <csv> --universe backtest|oos
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

EMA_N, SLOPE_N, DRAWS = 138, 34, 1000


def family_bear(mk) -> pd.DataFrame:
    """hour x sym: True where the coin's systematic (factor-explained) path is in a downtrend."""
    C = mk.close.values
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    res = residuals(mk)
    fac = np.where(np.isnan(res) | np.isnan(r), np.nan, r - res)
    path = pd.DataFrame(np.nancumsum(np.nan_to_num(fac), axis=0), index=mk.close.index, columns=mk.close.columns)
    known = pd.DataFrame(~np.isnan(fac), index=path.index, columns=path.columns).cumsum() > EMA_N
    ema = path.ewm(span=EMA_N, adjust=False).mean()
    bear = (path < ema) & (ema < ema.shift(SLOPE_N))
    return bear.where(known)                                   # NaN until the path has history


def stats(df: pd.DataFrame) -> dict:
    r = df.ret.values
    if len(r) == 0:
        return dict(n=0, mean=np.nan, pf=np.nan, win=np.nan, t=np.nan)
    g = df.groupby(df.entry.dt.floor("D")).ret
    S, n = g.sum(), g.count()
    mu = S.sum() / n.sum()
    se = math.sqrt(((S - n * mu) ** 2).sum()) / n.sum()
    pos, neg = r[r > 0].sum(), -r[r < 0].sum()
    return dict(n=len(r), mean=mu, pf=pos / neg if neg > 0 else np.nan, win=(r > 0).mean() * 100, t=mu / se if se > 0 else np.nan)


def line(name: str, s: dict) -> str:
    return f"  {name:<28} n {s['n']:6d}  mean {s['mean']:+7.3f}%  PF {s['pf']:5.2f}  win {s['win']:5.1f}%  t_day {s['t']:5.2f}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", required=True)
    ap.add_argument("--universe", choices=["backtest", "oos"], required=True)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--regimes", default=os.path.join(mnr.REPO, "reports", "btc_regime_series.csv"))
    ap.add_argument("--retcol", default="return_pct", help="return column (ripshort_1m_replay writes ret_15m / ret_1m)")
    a = ap.parse_args()

    tr = pd.read_csv(a.trades, parse_dates=["entry_time"])
    tr = tr[tr.strategy == "RipShort"].rename(columns={"entry_time": "entry", a.retcol: "ret"})
    tr["bar"] = tr.entry.dt.floor("1h") - pd.Timedelta(hours=1)          # last CLOSED hour bar
    print(f"{a.universe}: {len(tr)} RipShort trades, {tr.entry.min():%Y-%m-%d} → {tr.entry.max():%Y-%m-%d}")

    reg = pd.read_csv(a.regimes, parse_dates=["time"])[["time", "legacy"]].sort_values("time")
    tr = pd.merge_asof(tr.sort_values("bar"), reg, left_on="bar", right_on="time", direction="backward")

    syms = sorted(set(tr.symbol) | {"BTCUSDT"})
    mk = mnr.build_market(syms, a.cache)
    fb = family_bear(mk).stack(future_stack=True).rename("fam_bear").reset_index()
    fb.columns = ["bar", "symbol", "fam_bear"]
    tr = tr.merge(fb, on=["bar", "symbol"], how="left")
    tr = tr.dropna(subset=["legacy", "fam_bear"]).sort_values("entry").reset_index(drop=True)
    tr["fam_bear"] = tr.fam_bear.astype(bool)
    print(f"  {len(tr)} trades with both labels · BTC Bear {100 * (tr.legacy == 'Bear').mean():.0f}% · "
          f"family bear {100 * tr.fam_bear.mean():.0f}% · overlap (both) {100 * ((tr.legacy == 'Bear') & tr.fam_bear).mean():.0f}%")

    mid = tr.entry.quantile(0.5)
    rng = np.random.default_rng(20261013)
    print(line("ALL RipShort", stats(tr)))
    for name, keep in (("A  BTC not Bear", tr.legacy != "Bear"), ("B  family not in downtrend", ~tr.fam_bear)):
        k, d = tr[keep], tr[~keep]
        sk = stats(k)
        print(f"\n── {name}: keeps {100 * keep.mean():.0f}% of trades")
        print(line("   KEPT", sk))
        print(line("   dropped", stats(d)))
        print(line("   kept, 1st half", stats(k[k.entry < mid])))
        print(line("   kept, 2nd half", stats(k[k.entry >= mid])))
        null = np.array([tr.ret.values[rng.random(len(tr)) < keep.mean()].mean() for _ in range(DRAWS)])
        print(f"   vs {DRAWS} random gates of the same fraction: kept mean beats {(null < sk['mean']).mean():.1%}  (p = {(1 + (null >= sk['mean']).sum()) / (1 + DRAWS):.3f})")
        print("   kept mean by BTC regime: " + "  ".join(f"{g} {v.ret.mean():+.3f}% (n {len(v)})" for g, v in k.groupby("legacy")))
    print("\nTrials: 2 (A, B), fixed before the run. Returns are edgetest's net % per trade (this branch's cost model).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
