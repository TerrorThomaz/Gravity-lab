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

  python3 scripts/trade_log_edge.py reports/oos_trades.csv [more.csv ...] [--btc-start YYYY-MM-DD]
"""
import sys

import numpy as np
import pandas as pd

TRAIN, VAL = 0.80, 0.10


def pf(x):
    neg = -x[x < 0].sum()
    return x[x > 0].sum() / neg if neg > 0 else float("nan")


def t_day(d):
    daily = d.groupby(d.entry_time.dt.floor("D")).return_pct.sum()
    return daily.mean() / daily.std() * np.sqrt(len(daily)) if len(daily) > 2 else float("nan")


def report(path, btc_start):
    d = pd.read_csv(path, parse_dates=["entry_time"])
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
    if "--btc-start" in args:
        i = args.index("--btc-start")
        start = args[i + 1]
        del args[i:i + 2]
    for p in args or ["reports/oos_trades.csv"]:
        report(p, start)
