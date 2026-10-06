"""Three pre-registered uses of the forecast engine's output (docs/FORECAST_USES_2026-10.md).

  1 ALLOCATOR  size each Grid / GridShort trade by the coin's forecast at arming:
               Grid clip(1 + 0.5·z, 0, 2), GridShort clip(1 − 0.5·z, 0, 2); z = forecast / trailing 30d sd.
  2 NEUTRAL    hourly dollar-neutral portfolio, weights ∝ cross-sectional z of the forecast (clip ±3),
               gross 1, no-trade band 0.25% per coin; costs fee + half-spread by liquidity quartile.
  3 OVERLAY    size every Grid / GridShort trade by the MARKET forecast (cross-sectional mean of the
               coins' forecasts), same clip rule as 1.

Nulls: 1 and 3 — forecasts shuffled across trades (200) and taken 1–30 days away on the same coin (20);
2 — forecasts shuffled across coins within each hour (20) and time-shifted per coin (10).

Run:  ~/Gravity-lab/.venv-research/bin/python research/forecast_uses.py --universe oos|backtest
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
from forecast_engine import OUT  # noqa: E402
from grid_paths import mnr  # noqa: E402

K, SLOT, BAND = 0.5, 0.05, 0.0025
FEE_T, FEE_M = 0.055, 0.02                        # % per side, perps non-VIP
HALF_SPREAD_BP = (4.2, 2.9, 1.8, 0.6)             # recorder, by liquidity quartile least → most
SHUF, SHIFT, SHUF2, SHIFT2 = 200, 20, 20, 10


def load_forecast(u: str, H: int = 4):
    z = np.load(os.path.join(OUT, f"{u}_h{H}.npz"), allow_pickle=False)
    return pd.DatetimeIndex(z["times"]), list(z["symbols"]), z["F"].astype(np.float64)


def trailing_scale(F: np.ndarray, hours: int = 720) -> np.ndarray:
    """Per hour: the mean cross-sectional sd of forecasts over the trailing `hours`, strictly before."""
    xs = np.nanstd(F, axis=1)
    return pd.Series(xs).rolling(hours, min_periods=hours // 2).mean().shift(1).values


def book(ret: np.ndarray, size: np.ndarray, exit_day: np.ndarray) -> tuple[float, float, float]:
    """Daily book from trades: (Sharpe, maxDD %, total %)."""
    pnl = pd.Series(ret * size * SLOT).groupby(exit_day).sum()
    pnl = pnl.reindex(pd.date_range(pnl.index.min(), pnl.index.max()), fill_value=0.0)
    cum = pnl.cumsum()
    return pnl.mean() / pnl.std() * math.sqrt(365), float((cum - cum.cummax()).min()), float(cum.iloc[-1])


def sizes(z: np.ndarray, side: np.ndarray) -> np.ndarray:
    return np.clip(1 + K * side * np.nan_to_num(z), 0, 2)


def trade_z(tr: pd.DataFrame, times, syms, Z: np.ndarray, shift_days=None) -> np.ndarray:
    """z at the latest forecast time ≤ arming time, for each trade's coin. Z: time × coin (or time × 1)."""
    at = pd.to_datetime(tr.entry_time)
    if shift_days is not None:
        at = at + pd.to_timedelta(shift_days, unit="D")
    i = times.searchsorted(at, side="right") - 1
    if Z.shape[1] == 1:
        j = np.zeros(len(tr), int)
    else:
        col = {s: k for k, s in enumerate(syms)}
        j = tr.symbol.map(col).fillna(-1).astype(int).values
    ok = (i >= 0) & (j >= 0)
    out = np.full(len(tr), np.nan)
    out[ok] = Z[i[ok], j[ok]]
    return out


def sizing_use(name, tr, times, syms, Z, rng):
    side = np.where(tr.strategy == "Grid", 1, -1)
    ret = tr.return_pct.values
    day = pd.to_datetime(tr.exit_time).dt.floor("D").values
    z = trade_z(tr, times, syms, Z)
    flat = book(ret, np.ones(len(tr)), day)
    sz = sizes(z, side)
    real = book(ret, sz, day)
    d = real[0] - flat[0]
    et = pd.to_datetime(tr.entry_time)
    mid = et.min() + (et.max() - et.min()) / 2
    halves = []
    for part in (et < mid, et >= mid):
        p = part.values
        halves.append(book(ret[p], sz[p], day[p])[0] - book(ret[p], np.ones(p.sum()), day[p])[0])
    shuf = [book(ret, sizes(rng.permutation(z), side), day)[0] - flat[0] for _ in range(SHUF)]
    shifts = [book(ret, sizes(trade_z(tr, times, syms, Z, rng.integers(1, 31, len(tr)) * rng.choice([-1, 1], len(tr))), side),
                   day)[0] - flat[0] for _ in range(SHIFT)]
    print(f"   {name}: coverage {np.isfinite(z).mean():.0%}, mean size {sz.mean():.2f}")
    print(f"      flat   Sharpe {flat[0]:+.2f}  maxDD {flat[1]:+.1f}%  total {flat[2]:+.0f}%")
    print(f"      sized  Sharpe {real[0]:+.2f}  maxDD {real[1]:+.1f}%  total {real[2]:+.0f}%   ΔSharpe {d:+.3f}  "
          f"halves {halves[0]:+.3f} / {halves[1]:+.3f}")
    print(f"      nulls: beats {np.mean(d > np.array(shuf)):.1%} of {SHUF} shuffles (p95 {np.quantile(shuf, 0.95):+.3f}), "
          f"{sum(d > s for s in shifts)}/{SHIFT} time-shifts")
    return dict(d=d, halves=halves, shuf=np.mean(d > np.array(shuf)), shifts=sum(d > s for s in shifts),
                dd_ok=real[1] >= flat[1] * 1.1)


def neutral_use(u, times, syms, F, rng):
    mk = mnr.build_market(mnr.config_symbols("OosCoins" if u == "oos" else "BacktestCoins"),
                          os.path.join(mnr.REPO, "candle_cache"))
    # market rows are hour OPEN labels; the forecast at an hour's close t belongs to the bar labelled t − 1h
    lab = times - pd.Timedelta(hours=1)
    C = mk.close.reindex(index=lab, columns=syms).values
    fund = mk.funding.reindex(index=lab, columns=syms).fillna(0).values            # rate (fraction) at settlements
    qv = mk.qvol.reindex(index=lab, columns=syms).values
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.vstack([np.diff(np.log(C), axis=0), np.full((1, C.shape[1]), np.nan)]) * 100   # r[t] = t → t+1, %
    liq = pd.DataFrame(qv).rolling(720, min_periods=240).median().shift(1).values
    q = np.full(liq.shape, 0)
    for t in range(0, len(liq), 24):                                    # quartile by trailing liquidity, daily
        row = liq[t]
        ok = np.isfinite(row)
        if ok.sum() >= 8:
            q[t:t + 24, ok] = np.searchsorted(np.nanquantile(row[ok], [0.25, 0.5, 0.75]), row[ok], side="right")
    half = np.array(HALF_SPREAD_BP)[q] / 100

    def run(Fz, fee, delay):
        T, N = Fz.shape
        w = np.zeros(N)
        pnl, traded = np.zeros(T), np.zeros(T)
        for t in range(T - 1 - delay):
            f = Fz[t]
            ok = np.isfinite(f) & np.isfinite(r[t + delay])
            tgt = np.zeros(N)
            if ok.sum() >= 10:
                zx = np.clip((f[ok] - f[ok].mean()) / (f[ok].std() + 1e-12), -3, 3)
                zx -= zx.mean()
                tgt[ok] = zx / np.abs(zx).sum()
            move = np.abs(tgt - w) > BAND
            dw = np.where(move, tgt - w, 0.0)
            traded[t] = np.abs(dw).sum()
            cost = (np.abs(dw) * (fee + (half[t] if fee == FEE_T else 0))).sum()
            w = w + dw
            pnl[t + delay] += np.nansum(w * np.nan_to_num(r[t + delay])) - np.nansum(w * fund[t + delay]) * 100 - cost
        return pnl, traded

    def stats(pnl, label):
        s = pd.Series(pnl, index=times)
        d = s.resample("D").sum()
        wk = s.resample("W").sum()
        sr = d.mean() / d.std() * math.sqrt(365) if d.std() > 0 else np.nan
        tw = wk.mean() / wk.std() * math.sqrt(len(wk)) if wk.std() > 0 else np.nan
        last = d[d.index >= d.index[-1] - pd.Timedelta(days=365)].sum()
        yr = d.groupby(d.index.year).sum()
        print(f"      {label:<26} {d.sum() / (len(d) / 365):+6.1f}%/yr  Sharpe {sr:+.2f}  t_wk {tw:+.2f}  last 12m {last:+.1f}%  "
              + " ".join(f"{y}:{v:+.0f}" for y, v in yr.items()))
        return sr, tw, last

    print(f"   2 NEUTRAL hourly portfolio (dollar-neutral, gross 1, band {BAND:.2%}/coin)")
    out = {}
    for fee, nm in ((FEE_T, "taker + spread"), (FEE_M, "maker (fills assumed)")):
        for delay in (0, 1):
            pnl, traded = run(F, fee, delay)
            lbl = f"{nm}, delay {delay}h"
            out[(nm, delay)] = stats(pnl, lbl)
            if fee == FEE_T and delay == 0:
                print(f"         turnover {traded.mean() * 24 * 365:.0f}x gross per year")
    nulls = []
    for _ in range(SHUF2):
        Fs = F.copy()
        for t in range(len(Fs)):
            ok = np.isfinite(Fs[t])
            Fs[t, ok] = rng.permutation(Fs[t, ok])
        nulls.append(stats(run(Fs, FEE_T, 0)[0], "null: shuffled across coins")[0])
    for _ in range(SHIFT2):
        Fs = np.full_like(F, np.nan)
        for j in range(F.shape[1]):
            k = int(rng.integers(1, 31)) * 24 * int(rng.choice([-1, 1]))
            Fs[:, j] = np.roll(F[:, j], k)
        nulls.append(stats(run(Fs, FEE_T, 0)[0], "null: time-shifted")[0])
    real = out[("taker + spread", 0)][0]
    print(f"      real taker Sharpe {real:+.2f} beats {sum(real > n for n in nulls)}/{len(nulls)} nulls")
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    a = ap.parse_args()
    rng = np.random.default_rng(20261006)
    times, syms, F = load_forecast(a.universe)
    scale = trailing_scale(F)
    Z = F / scale[:, None]
    m = np.nanmean(F, axis=1)
    mscale = pd.Series(m).rolling(720, min_periods=360).std().shift(1).values
    Zm = (m / mscale)[:, None]
    tr = pd.read_csv(os.path.join(mnr.REPO, "reports", f"edgetest_raw_trades_{a.universe}.csv"))
    tr = tr[tr.strategy.isin(["Grid", "GridShort"])].reset_index(drop=True)
    tr = tr[pd.to_datetime(tr.entry_time) >= times[0] + pd.DateOffset(months=1)].reset_index(drop=True)
    print(f"{a.universe}: forecasts {times[0]:%Y-%m-%d} → {times[-1]:%Y-%m-%d}, {len(syms)} coins; "
          f"Grid/GridShort trades {len(tr)}")
    sizing_use("1 ALLOCATOR (coin forecast)  [PRIMARY of use 1]", tr, times, syms, Z, rng)
    sizing_use("3 OVERLAY (market forecast)", tr, times, syms, Zm, rng)
    neutral_use(a.universe, times, syms, F, rng)
    return 0


if __name__ == "__main__":
    sys.exit(main())
