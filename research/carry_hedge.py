"""Two pre-registered carry trials (docs/EDGE_DISCOVERY_SCOPE_2026-10.md). Run once.

The live book's worst days come from carry (−1.95%/day on the book's worst 5% days). Carry's P&L by
leg (OosCoins, %/yr): long price −8.3, long funding +17.8; short price +9.0, short funding +6.6;
costs −12.1. The long leg EARNS funding (its coins mostly have negative rates), so moving longs to
spot wholesale would cost ~18%/yr. Hence:

TRIAL A — volatility-managed carry (the tail control; Moreira & Muir 2017, fixed windows, no fit).
  scale_d = min(1, σ_ref / σ_7d), where σ_7d is the std of plain carry's daily net over the 7 days
  before day d, and σ_ref is the median of σ_7d over the 180 days before d. The day's carry targets
  are multiplied by scale_d (never above 1: no leverage). Placebo: the same scale series permuted in
  30-day blocks (100 draws): same average de-risking, no timing.

TRIAL B — venue switch for paying longs. At each rebalance, a long position whose coin has a Bybit
  USDT spot market (today's listing, applied to the past) AND trailing-7d mean funding > 0 is held on
  spot until the next rebalance (no funding). Every perp↔spot move of a held long pays 2 taker sides.

Accounting: the run_book loop of market_neutral_research (costs, band 0.5%, real funding), with the
two changes above. Book = ERC(carry variant, Grid, GridShort), as research/power_check.book_daily.

Run:  python3 research/carry_hedge.py --selftest
      python3 research/carry_hedge.py --run
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import urllib.request

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402
from power_check import CACHE, alpha_t, sharpe  # noqa: E402

TRADES = os.path.join(mnr.REPO, "reports", "live_book_trades_2026-10-04.csv")


def carry_targets(mk):
    """The plain carry book's targets (decided by run_carry), captured without changing it."""
    cap = {}
    orig = mnr.run_book

    def spy(close, funding, fmask, fknown, targets, index, cost_per_side=None, band=0.0):
        cap.update(targets=targets, band=band)
        return orig(close, funding, fmask, fknown, targets, index, cost_per_side, band)
    mnr.run_book = spy
    try:
        r, _ = mnr.run_carry(mk, mnr.CarryParams(band=0.005))
    finally:
        mnr.run_book = orig
    return cap["targets"], cap["band"], r


def book_loop(close, funding, fknown, targets, band, scale_day=None, spot_long=None, cost=mnr.COST_PER_SIDE,
              index=None):
    """run_book's loop. scale_day: per-hour multiplier of the targets (constant within a day).
    spot_long: {target hour: bool[m]} — longs exempt from funding until the next rebalance; a held
    long that changes venue pays 2·|h|·cost."""
    n, m = close.shape
    ret = np.zeros_like(close)
    with np.errstate(invalid="ignore", divide="ignore"):
        ret[1:] = close[1:] / close[:-1] - 1.0
    ret = np.nan_to_num(ret, nan=0.0, posinf=0.0, neginf=0.0)
    floor_tick = index.hour.values % 8 == 0
    floor = mnr.FUNDING_FLOOR_PCT_PER_8H / 100.0
    h = np.zeros(m)
    spot = np.zeros(m, bool)
    net = np.zeros(n)
    for t in range(n):
        if t > 0 and h.any():
            perp = ~(spot & (h > 0))
            f = -(h[fknown & perp] @ funding[t, fknown & perp])
            if floor_tick[t]:
                f -= np.abs(h[~fknown & perp]).sum() * floor
            net[t] += h @ ret[t] + f
            h = h * (1.0 + ret[t])
        tgt = targets.get(t)
        if tgt is not None:
            if scale_day is not None:
                tgt = tgt * scale_day[t]
            tradable = ~np.isnan(close[t])
            new = np.where(tradable & (np.abs(tgt - h) >= band), tgt, h)
            net[t] -= np.abs(new - h).sum() * cost
            if spot_long is not None:
                nsp = spot_long.get(t, spot)
                moved = (nsp != spot) & (h > 0) & (new > 0)
                net[t] -= 2 * np.minimum(h, new)[moved].sum() * cost
                spot = nsp
            h = new
    return pd.Series(net, index=index)


def vol_scale(daily: pd.Series) -> pd.Series:
    s7 = daily.rolling(7).std().shift(1)                       # the 7 days BEFORE day d
    ref = s7.rolling(180, min_periods=60).median()
    return (ref / s7).clip(upper=1.0).fillna(1.0)


def spot_listed() -> set[str]:
    with urllib.request.urlopen("https://api.bybit.com/v5/market/instruments-info?category=spot&limit=1000", timeout=30) as r:
        d = json.load(r)["result"]["list"]
    return {x["symbol"] for x in d if x["status"] == "Trading" and x["quoteCoin"] == "USDT"}


def stats(x: pd.Series) -> dict:
    x = x[x.ne(0).cumsum() > 0]
    e = (1 + x).cumprod()
    h = len(x) // 2
    return {"Sharpe": sharpe(x), "CAGR %": 100 * (e.iloc[-1] ** (365 / len(x)) - 1),
            "vol %": 100 * x.std() * math.sqrt(365), "maxDD %": 100 * (e / e.cummax() - 1).min(),
            "worst 1% day %": 100 * x.quantile(0.01), "half1": sharpe(x.iloc[:h]), "half2": sharpe(x.iloc[h:])}


def run() -> int:
    from bh_check import benchmarks
    listed = spot_listed()
    out = {}
    for u in ("oos", "backtest"):
        mk = mnr.build_market(mnr.config_symbols("OosCoins" if u == "oos" else "BacktestCoins"), CACHE)
        targets, band, r = carry_targets(mk)
        C, Fd, K, idx = mk.close.values, mk.funding.values, mk.funding_known.values, mk.close.index
        plain_h = book_loop(C, Fd, K, targets, band, index=idx)
        ref = pd.Series(r.net, index=idx)
        assert abs(plain_h.sum() - ref.sum()) < 1e-9, "loop is not run_book"
        plain = plain_h.resample("1D").sum()
        sc = vol_scale(plain)
        sc_h = sc.reindex(idx.floor("D")).values
        A = book_loop(C, Fd, K, targets, band, scale_day=sc_h, index=idx).resample("1D").sum()
        # placebo: scale permuted in 30-day blocks
        rng = np.random.default_rng(5)
        nb = math.ceil(len(sc) / 30)
        plac = []
        for _ in range(100):
            perm = np.concatenate([sc.values[i * 30:(i + 1) * 30] for i in rng.permutation(nb)])[: len(sc)]
            ps = pd.Series(perm, index=sc.index).reindex(idx.floor("D")).values
            plac.append(stats(book_loop(C, Fd, K, targets, band, scale_day=ps, index=idx).resample("1D").sum()))
        cols = list(mk.close.columns)
        has_spot = np.array([c in listed for c in cols])
        sl = {}
        for tk in sorted(targets):
            f = mnr.trailing_funding(mk, tk - 1, 24 * 7, list(range(len(cols))))
            sl[tk] = has_spot & (np.nan_to_num(f, nan=-1.0) > 0)
        B = book_loop(C, Fd, K, targets, band, spot_long=sl, index=idx).resample("1D").sum()
        out[u] = dict(plain=plain, A=A, B=B, scale=sc, plac=plac, spot_share=has_spot.mean())

    print("CARRY TRIALS (pre-registered), run once\n")
    for u, o in out.items():
        rows = {"plain carry": stats(o["plain"]), "A vol-managed": stats(o["A"]), "B venue switch": stats(o["B"])}
        pa = pd.DataFrame(o["plac"])
        rows["A placebo median"] = pa.median().to_dict()
        print(f"[{u}] spot-listed share of universe {o['spot_share']:.0%}; vol scale mean {o['scale'].mean():.2f}, "
              f"min {o['scale'].min():.2f}, days < 1: {(o['scale'] < 1).mean():.0%}")
        with pd.option_context("display.width", 220, "display.float_format", lambda v: f"{v:,.2f}"):
            print(pd.DataFrame(rows).T.to_string())
        print(f"  A beats placebo on Sharpe {np.mean(pa.Sharpe < rows['A vol-managed']['Sharpe']):.0%}, "
              f"on maxDD {np.mean(pa['maxDD %'] < rows['A vol-managed']['maxDD %']):.0%}, "
              f"on worst 1% day {np.mean(pa['worst 1% day %'] < rows['A vol-managed']['worst 1% day %']):.0%}\n")

    # book level (OosCoins: the live book's universe)
    from power_check import book_daily
    _, S = book_daily(TRADES)
    S = S.iloc[:-1]
    o = out["oos"]
    bms = benchmarks(S.index)
    books = {}
    for name, c in (("plain", o["plain"]), ("A", o["A"]), ("B", o["B"])):
        SS = S.copy()
        SS["carry"] = c.reindex(SS.index).fillna(0.0)
        books[name], _ = mnr.erc_combine(SS)
    print("BOOK = ERC(carry variant, Grid, GridShort), OosCoins")
    worst = books["plain"] <= books["plain"].quantile(0.05)
    bte = bms["BTC/ETH"]
    for name, b in books.items():
        beta = float(np.polyfit(bte.values, b.values, 1)[0])
        st = stats(b)
        print(f"  {name:6s} Sharpe {st['Sharpe']:.2f}  CAGR {st['CAGR %']:.1f}%  maxDD {st['maxDD %']:.1f}%  "
              f"worst-1% day {st['worst 1% day %']:+.2f}%  on plain book's worst 5% days {100 * b[worst].mean():+.3f}%/day  "
              f"| vs BTC/ETH B&H (Sharpe {sharpe(bte):.2f}): beta {beta:.2f}, alpha t {alpha_t(b.values, bte.values):+.2f}")

    # verdicts (pre-registered)
    def nobleed(x):
        s = stats(x); return s["Sharpe"] > 0 and s["half1"] > 0 and s["half2"] > 0
    pb, ba, bb = (stats(books[k]) for k in ("plain", "A", "B"))
    pa = pd.DataFrame(out["oos"]["plac"])
    a_ok = (all(nobleed(out[u]["A"]) for u in out)
            and all(stats(out[u]["A"])["worst 1% day %"] > stats(out[u]["plain"])["worst 1% day %"] for u in out)
            and ba["Sharpe"] > pb["Sharpe"] and ba["maxDD %"] >= pb["maxDD %"]
            and np.mean(pa["worst 1% day %"] < stats(out["oos"]["A"])["worst 1% day %"]) >= 0.9)
    b_ok = (all(nobleed(out[u]["B"]) for u in out)
            and all(stats(out[u]["B"])["Sharpe"] > stats(out[u]["plain"])["Sharpe"] for u in out)
            and all(stats(out[u]["B"])[k] > stats(out[u]["plain"])[k] for u in out for k in ("half1", "half2")))
    print(f"\nVERDICT  Trial A vol-managed carry: {'PASS' if a_ok else 'FAIL'}   Trial B venue switch: {'PASS' if b_ok else 'FAIL'}")
    return 0


def selftest() -> int:
    """The loop reproduces run_book; scale 0 means flat; spot longs pay no funding; vol_scale is causal."""
    rng = np.random.default_rng(1)
    n, m = 24 * 60, 4
    idx = pd.date_range("2024-01-01", periods=n, freq="1h")
    C = 100 * np.exp(np.cumsum(rng.normal(0, 0.01, (n, m)), 0))
    Fd = np.zeros((n, m)); Fd[idx.hour % 8 == 0] = 0.001
    K = np.array([True, True, True, False])
    tg = {t: np.array([0.4, -0.3, 0.2, -0.1]) for t in range(30, n, 24)}
    ref = mnr.run_book(C, Fd, np.ones_like(Fd, bool), K, tg, idx, band=0.005)
    mine = book_loop(C, Fd, K, tg, 0.005, index=idx)
    assert abs(ref.net.sum() - mine.sum()) < 1e-12, "loop differs from run_book"
    zero = book_loop(C, Fd, K, tg, 0.0, scale_day=np.zeros(n), index=idx)
    assert abs(zero.sum()) < 1e-15
    sl = {t: np.array([True, False, True, False]) for t in tg}
    sp = book_loop(C, Fd, K, tg, 0.005, spot_long=sl, index=idx)
    assert sp.sum() > mine.sum(), "spot longs should save the positive funding the perp longs paid"
    d = pd.Series(rng.normal(0, 0.01, 400), index=pd.date_range("2024-01-01", periods=400))
    d2 = d.copy(); d2.iloc[300] = 0.5
    assert vol_scale(d).iloc[:301].equals(vol_scale(d2).iloc[:301]), "scale on day d uses day d"
    print("selftest: OK (loop == run_book, scale 0 flat, spot longs skip funding, vol scale causal)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--run", action="store_true")
    a = ap.parse_args()
    return selftest() if a.selftest else run() if a.run else (ap.print_help() or 1)


if __name__ == "__main__":
    sys.exit(main())
