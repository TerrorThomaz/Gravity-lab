#!/usr/bin/env python3
"""
Does the BTC regime (legacy classifier or HMM state) say when a book makes money?

Inputs: reports/btc_regime_series.csv (`dotnet run -- hmmdump`) and, per book, either a trade
log (entry_time, exit_time, strategy, return_pct — e.g. reports/edgetest_raw_trades.csv) or
the carry book's daily P&L from market_neutral_research.py.

Per book it prints P&L by regime / HMM state (argmax), with each half of the window shown
separately so a state that only worked once is visible, and then ONE walk-forward gate fixed
in advance: trade only while the current state's trailing-180d P&L, from observations that
had already CLOSED, is positive (fewer than 30 observations -> stay on). A random gate
dropping the same fraction is the control.

Timing: a regime bar is labelled by its OPEN and computed from its close, so it is usable
from time + 1h. The HMM itself (emissions, transitions) was fitted by Baum-Welch on the whole
history, so the states are not fully out-of-sample even though the filter is causal.

  python3 scripts/regime_edge.py grid  [trades.csv]   # Grid sessions at ARMING
  python3 scripts/regime_edge.py carry [--universe oos|backtest]
  ... [--regimes reports/regime_pc1_py.csv] [--since YYYY-MM-DD]   # another regime series,
                                                                   # and a common start date
"""
from __future__ import annotations

import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import market_neutral_research as mnr  # noqa: E402

REPO = mnr.REPO
LOOKBACK = pd.Timedelta(days=180)
MIN_OBS = 30
REGIMES = os.path.join(REPO, "reports", "btc_regime_series.csv")
SINCE: pd.Timestamp | None = None


def load_regimes() -> pd.DataFrame:
    r = pd.read_csv(REGIMES, parse_dates=["time"])
    if "legacy" not in r.columns:           # an HMM-only series (scripts/factor_hmm.py)
        r["legacy"] = "n/a"
    p = [c for c in r.columns if c.startswith("p") and c[1:].isdigit()]
    r["state"] = "s" + r[p].values.argmax(axis=1).astype(str) + ":" + r.hmm_label
    r["known"] = r.time + pd.Timedelta(hours=1)
    return r.sort_values("known")


def tag(times: pd.Series, reg: pd.DataFrame) -> pd.DataFrame:
    """Regime known at each time: the last bar whose close is at or before it."""
    t = pd.DataFrame({"t": pd.to_datetime(times.values).astype("datetime64[ns]"),
                      "i": np.arange(len(times))}).sort_values("t")
    r = reg[["known", "legacy", "state"]].assign(known=reg.known.astype("datetime64[ns]"))
    m = pd.merge_asof(t, r, left_on="t", right_on="known")
    return m.sort_values("i")[["legacy", "state"]].reset_index(drop=True)


def t_day(ret: pd.Series, day: pd.Series) -> float:
    d = ret.groupby(day.values).sum()
    return d.mean() / d.std() * np.sqrt(len(d)) if len(d) > 2 and d.std() > 0 else np.nan


def by_group(obs: pd.DataFrame, col: str) -> None:
    half = obs.t.quantile(0.5)
    rows = []
    for g, x in obs.groupby(col):
        a, b = x[x.t < half], x[x.t >= half]
        rows.append({col: g, "n": len(x), "share": len(x) / len(obs), "mean%": x.ret.mean(),
                     "t_day": t_day(x.ret, x.day), "1st half mean%": a.ret.mean(),
                     "2nd half mean%": b.ret.mean()})
    with pd.option_context("display.width", 200, "display.float_format", lambda v: f"{v:,.3f}"):
        print(pd.DataFrame(rows).set_index(col).to_string())


def walk_forward_gate(obs: pd.DataFrame, col: str) -> np.ndarray:
    """Admit an observation iff its state's P&L over the trailing 180d, counting only
    observations CLOSED before it was decided, is positive. Too little history -> admit."""
    closed = obs.sort_values("closed")
    admit = np.ones(len(obs), dtype=bool)
    for k, (t, s) in enumerate(zip(obs.t, obs[col])):
        past = closed[(closed.closed <= t) & (closed.closed > t - LOOKBACK) & (closed[col] == s)]
        if len(past) >= MIN_OBS:
            admit[k] = past.ret.sum() > 0
    return admit


def gate_report(obs: pd.DataFrame, unit: str, seed: int = 7) -> None:
    def line(name, x):
        pf = x.ret[x.ret > 0].sum() / -x.ret[x.ret < 0].sum() if (x.ret < 0).any() else np.nan
        print(f"  {name:34s} n={len(x):6d}  sum {x.ret.sum():9.1f}  mean {x.ret.mean():+.4f}  "
              f"PF {pf:.2f}  t_day {t_day(x.ret, x.day):+.2f}")
    print(f"\n  walk-forward gate (trailing 180d, closed {unit} only, min {MIN_OBS}):")
    line("ALWAYS ON", obs)
    rng = np.random.default_rng(seed)
    for col in [c for c in ("legacy", "state") if obs[c].nunique() > 1]:
        a = walk_forward_gate(obs, col)
        line(f"gated on {col} ({a.mean():.0%} admitted)", obs[a])
        ctrl = [obs[rng.random(len(obs)) < a.mean()] for _ in range(50)]
        sums = np.array([c.ret.sum() for c in ctrl])
        print(f"  {'  random gate, same fraction':34s} sum p10/p50/p90 {np.quantile(sums, .1):.1f}/"
              f"{np.median(sums):.1f}/{np.quantile(sums, .9):.1f}   (gated beats {np.mean(sums < obs[a].ret.sum()):.0%})")


def grid(path: str) -> None:
    reg = load_regimes()
    d = pd.read_csv(path, parse_dates=["entry_time", "exit_time"])
    for strat in [s for s in ("Grid", "GridShort") if s in set(d.strategy)]:
        g = d[d.strategy == strat].reset_index(drop=True)
        obs = pd.concat([pd.DataFrame({"t": g.entry_time, "closed": g.exit_time, "ret": g.return_pct,
                                       "day": g.entry_time.dt.floor("D")}),
                         tag(g.entry_time, reg)], axis=1).dropna()
        obs = obs[obs.t >= SINCE] if SINCE is not None else obs
        print(f"\n══ {strat} (n={len(obs)}), regime at ARMING — {os.path.basename(REGIMES)}")
        if obs.legacy.nunique() > 1:
            by_group(obs, "legacy")
        by_group(obs, "state")
        gate_report(obs, "trades")


def carry(universe: str) -> None:
    reg = load_regimes()
    mnr.COST_PER_SIDE = mnr.MAKER_COST_PER_SIDE
    key = {"oos": "OosCoins", "backtest": "BacktestCoins"}[universe]
    syms = mnr.config_symbols(key)
    syms = syms if "BTCUSDT" in syms else ["BTCUSDT"] + syms
    mk = mnr.build_market(syms, os.path.join(REPO, "candle_cache"))
    r, _ = mnr.run_carry(mk, mnr.CarryParams(band=0.005))
    daily = pd.Series(r.net, index=r.index).resample("1D").sum() * 100
    daily = daily[daily.index >= daily[daily != 0].index.min()]
    # the day's P&L is gated by the regime known at the START of that day
    obs = pd.concat([pd.DataFrame({"t": daily.index, "closed": daily.index + pd.Timedelta(days=1),
                                   "ret": daily.values, "day": daily.index}),
                     tag(pd.Series(daily.index), reg)], axis=1).dropna()
    obs = obs[obs.t >= SINCE] if SINCE is not None else obs
    print(f"\n══ CARRY ({universe}, maker, band 0.5%), daily P&L % by regime known at day start — "
          f"{os.path.basename(REGIMES)}")
    if obs.legacy.nunique() > 1:
        by_group(obs, "legacy")
    by_group(obs, "state")
    gate_report(obs, "days")
    print("  (gating a carry book costs ~2 x gross x maker fee per switch; not charged above)")


if __name__ == "__main__":
    a = sys.argv[1:]
    for flag in ("--regimes", "--since"):
        if flag in a:
            i = a.index(flag)
            if flag == "--regimes":
                REGIMES = a[i + 1]
            else:
                SINCE = pd.Timestamp(a[i + 1])
            del a[i:i + 2]
    if not a or a[0] not in ("grid", "carry"):
        sys.exit(__doc__)
    if a[0] == "grid":
        grid(a[1] if len(a) > 1 else os.path.join(REPO, "reports", "edgetest_raw_trades.csv"))
    else:
        carry(a[a.index("--universe") + 1] if "--universe" in a else "oos")
