#!/usr/bin/env python3
"""
Edge check on the trade CSVs the C# backtests write to reports/ (StrategyEvaluation.Report).

Prints per-strategy and per-split stats with a DAY-CLUSTERED t-stat: trades opened on the
same day across many coins are one correlated bet, not N independent ones, so the naive
per-trade t overstates significance (typically ~1.6-2x here).

Splits follow DataSplit (src/core/TrainingData.cs: train 80% / val 10% / test 10%) applied
to the BTC h1 history span. The OOS logs start later than BTC (OOS coins list later), so
pass --btc-start with the first BTC bar the backtest used (default 2020-10-28, the first
entry in the fulltest OOS book); the end defaults to the log's last entry. Only the "test"
rows are free of every selection step (GA, router, HMM, guard).

NOT EVIDENCE OF AN EDGE. These CSVs are per-trade returns from in-sample-gated books; see
docs/RIGOR_REWORK_2026-09.md — `edgetest` is the measurement of record. Use this only to spot
inconsistencies between reports (e.g. a strategy flipping sign between two of them) or a CSV
that is stale against the committed genotypes.

  python3 scripts/trade_log_edge.py reports/oos_trades.csv [more.csv ...] [--btc-start YYYY-MM-DD]
  python3 scripts/trade_log_edge.py reports/edgetest_raw_trades.csv --hedge
  python3 scripts/trade_log_edge.py reports/edgetest_raw_trades.csv --grid-signals

--hedge adds a "·hedged" row per strategy: each trade held against the same covariance
anchors the carry book uses (the dollar direction plus the top 3 eigenvectors of the trailing
30d covariance of the coins in the file, taken as of the entry day), with the hedge legs
charged maker cost on entry and exit. What survives is the trade's return net of the market
moves it happened to ride.

--grid-signals asks whether any other strategy's activity says something about the grid. For
each non-grid strategy, activity = its open-trade count at the end of each hour, ranked
against its own trailing 30 days (point-in-time). Grid sessions are split into terciles of
that rank at ARMING (the moment a gate would decide). Reported: mean/PF per tercile, and
high-minus-low with a day-clustered z, overall and per half. The expected sign is written
down in advance (a long signal should help Grid and hurt GridShort; a short signal the
reverse). With ~10 tests, only |z| > 2.8 (Bonferroni at 5%) counts.
"""
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import market_neutral_research as mnr  # noqa: E402

LONG = {"Grid", "grid", "DipLong", "diplong", "SwingLong", "swing_long", "FadeLong", "fadelong",
        "AccumGrid", "accumgrid"}


def hedged_returns(d: pd.DataFrame, factors: int = 3) -> np.ndarray:
    """Trade return (%) plus the P&L of the legs that make it factor-neutral at entry."""
    mk = mnr.build_market(sorted(d.symbol.unique()), os.path.join(mnr.REPO, "candle_cache"))
    close, idx = mk.close.values, mk.close.index
    col = {c: i for i, c in enumerate(mk.close.columns)}
    ffill = pd.DataFrame(close).ffill().values
    low, high = mk.low.values, mk.high.values
    has_px = "entry_price" in d.columns
    anchors: dict[pd.Timestamp, tuple] = {}
    out = np.full(len(d), np.nan)
    for n, (et, xt, sym, ret, strat, epx) in enumerate(zip(
            d.entry_time, d.exit_time, d.symbol, d.return_pct, d.strategy,
            d.entry_price if has_px else np.zeros(len(d)))):
        i = col.get(sym)
        t, x = idx.searchsorted(et.floor("1h")), idx.searchsorted(xt.floor("1h"))
        if i is None or t < 24 * 30 or x >= len(idx):
            continue
        day = et.floor("1D")
        if day not in anchors:                  # one covariance per entry day, strictly before it
            t_d = idx.searchsorted(day) - 1
            uni = mnr.liquid_universe(mk, t_d - 24 * 30, t_d + 1, 40)
            anchors[day] = (uni, mnr.log_returns(mk, t_d - 24 * 30, t_d + 1, uni)) if len(uni) > factors + 2 else None
        if anchors[day] is None:
            continue
        uni, lr = anchors[day]
        if i not in uni:                        # the traded coin always belongs to its own hedge
            uni = uni + [i]
            lr = mnr.log_returns(mk, idx.searchsorted(day) - 1 - 24 * 30, idx.searchsorted(day), uni)
        dirn = 1.0 if strat in LONG else -1.0
        # A grid session's entry_time is when it was ARMED; its rungs fill later, when price
        # comes to them. Hedging from arming would short the very dip the grid then buys and book
        # that drop as hedge profit. Start the hedge at the first bar that reaches the session's
        # mean fill price instead.
        if "grid" in strat.lower() and epx > 0:
            seg = low[t:x + 1, i] <= epx if dirn > 0 else high[t:x + 1, i] >= epx
            hit = np.flatnonzero(seg)
            t = t + int(hit[0]) if len(hit) else t
        w = np.zeros(len(uni)); w[uni.index(i)] = dirn
        hedge = mnr.factor_neutral(w, lr, factors) - w          # legs added on top of the trade
        with np.errstate(invalid="ignore", divide="ignore"):
            move = np.nan_to_num(ffill[x, uni] / ffill[t, uni] - 1.0)
        cost = 2 * np.abs(hedge).sum() * mnr.MAKER_COST_PER_SIDE
        out[n] = ret + 100.0 * (hedge @ move - cost)
    return out

TRAIN, VAL = 0.80, 0.10


def pf(x):
    neg = -x[x < 0].sum()
    return x[x > 0].sum() / neg if neg > 0 else float("nan")


def t_day(d):
    daily = d.groupby(d.entry_time.dt.floor("D")).return_pct.sum()
    return daily.mean() / daily.std() * np.sqrt(len(daily)) if len(daily) > 2 else float("nan")


def _day_mean_se(x: pd.DataFrame) -> tuple[float, float, int]:
    daily = x.groupby(x.entry_time.dt.floor("D")).return_pct.mean()
    return daily.mean(), daily.std(ddof=1) / np.sqrt(len(daily)) if len(daily) > 1 else np.nan, len(daily)


def grid_signals(d: pd.DataFrame) -> None:
    grids = [g for g in ("Grid", "GridShort") if g in set(d.strategy)]
    others = [s for s in sorted(set(d.strategy)) if s not in grids]
    hours = pd.date_range(d.entry_time.min().floor("h"), d.exit_time.max().ceil("h"), freq="h")
    end = (hours + pd.Timedelta(hours=1)).values
    print("\n== other strategies' activity at grid ARMING (terciles of rank vs own trailing 30d)")
    print("   high-minus-low: mean %/trade difference, day-clustered z; Bonferroni bar |z| > 2.8")
    for sig in others:
        x = d[d.strategy == sig]
        ent, ext = np.sort(x.entry_time.values), np.sort(x.exit_time.values)
        cnt = pd.Series(np.searchsorted(ent, end, "right") - np.searchsorted(ext, end, "right"), index=hours)
        pct = cnt.rolling(24 * 30, min_periods=24 * 7).rank(pct=True)
        for g in grids:
            gg = d[d.strategy == g].copy()
            gg["pct"] = pct.reindex(gg.entry_time.dt.floor("h")).values
            gg = gg.dropna(subset=["pct"])
            lo, mid, hi = gg[gg.pct <= 1 / 3], gg[(gg.pct > 1 / 3) & (gg.pct <= 2 / 3)], gg[gg.pct > 2 / 3]
            if min(len(lo), len(hi)) < 30:
                continue
            (mh, sh, _), (ml, sl, _) = _day_mean_se(hi), _day_mean_se(lo)
            z = (mh - ml) / np.sqrt(sh ** 2 + sl ** 2)
            expect = (1 if sig in LONG else -1) * (1 if g in LONG else -1)
            half = gg.entry_time.quantile(0.5)
            hz = []
            for part in (gg[gg.entry_time < half], gg[gg.entry_time >= half]):
                a, b = part[part.pct > 2 / 3], part[part.pct <= 1 / 3]
                hz.append(a.return_pct.mean() - b.return_pct.mean() if len(a) and len(b) else np.nan)
            print(f"  {sig:10s} -> {g:9s}  low/mid/high mean {lo.return_pct.mean():+.3f}/{mid.return_pct.mean():+.3f}/"
                  f"{hi.return_pct.mean():+.3f}%  PF {pf(lo.return_pct):.2f}/{pf(mid.return_pct):.2f}/{pf(hi.return_pct):.2f}"
                  f"  | hi-lo {mh - ml:+.3f}% z {z:+.2f} (expected {'+' if expect > 0 else '-'})"
                  f"  halves {hz[0]:+.3f}/{hz[1]:+.3f}")


def report(path, btc_start, hedge=False, signals=False):
    d = pd.read_csv(path, parse_dates=["entry_time", "exit_time"])
    if signals:
        grid_signals(d)
        return
    if hedge:
        h = d.assign(return_pct=hedged_returns(d), strategy=d.strategy + " ·hedged").dropna(subset=["return_pct"])
        print(f"  hedged {len(h)}/{len(d)} trades (rest lack 30d of history or price data)")
        d = pd.concat([d, h], ignore_index=True)
    t0, t1 = min(pd.Timestamp(btc_start), d.entry_time.min()), d.entry_time.max()
    cut1, cut2 = t0 + (t1 - t0) * TRAIN, t0 + (t1 - t0) * (TRAIN + VAL)
    d["split"] = np.where(d.entry_time < cut1, "train-time",
                          np.where(d.entry_time < cut2, "val", "test"))
    r = d.return_pct
    print(f"\n== {path}  n={len(d)}  {t0.date()} .. {t1.date()}  (val from {cut1.date()}, test from {cut2.date()})")
    print(f"  all: mean {r.mean():+.3f}%  PF {pf(r):.2f}  WR {(r > 0).mean():.0%}  "
          f"t(naive) {r.mean() / r.std() * np.sqrt(len(r)):+.1f}  t(day) {t_day(d):+.1f}")
    rows = []
    for (s, sp), g in d.groupby(["strategy", "split"]):
        rows.append({"strategy": s, "split": sp, "n": len(g), "mean%": g.return_pct.mean(),
                     "PF": pf(g.return_pct), "t_day": t_day(g)})
    for sp, g in d.groupby("split"):
        rows.append({"strategy": "ALL", "split": sp, "n": len(g), "mean%": g.return_pct.mean(),
                     "PF": pf(g.return_pct), "t_day": t_day(g)})
    t = pd.DataFrame(rows).pivot(index="strategy", columns="split")
    order = ["train-time", "val", "test"]
    t = t.reindex(columns=pd.MultiIndex.from_product([["n", "mean%", "PF", "t_day"], order]))
    with pd.option_context("display.width", 200, "display.float_format", lambda v: f"{v:,.2f}"):
        print(t.to_string())


if __name__ == "__main__":
    args, start = sys.argv[1:], "2020-10-28"
    hedge, signals = "--hedge" in args, "--grid-signals" in args
    args = [a for a in args if a not in ("--hedge", "--grid-signals")]
    if "--btc-start" in args:
        i = args.index("--btc-start")
        start = args[i + 1]
        del args[i:i + 2]
    for p in args or ["reports/oos_trades.csv"]:
        report(p, start, hedge, signals)
