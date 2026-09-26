#!/usr/bin/env python3
"""
Dip-bounce with DCA: buy a capitulation, average down up to three times, exit on the bounce or
at break-even once averaged in. A behavioural reversal bet (overreaction / short-term reversal,
documented on equities) tested on the crypto universe with every rule fixed in advance.

Rules (DipParams defaults):
  trigger   close <= (1 - 15%) x highest close of the trailing 7 days, coin in top-40 liquid
  entry     limit buy at the trigger close, resting 24h from the next bar (maker)
  DCA       up to 3 adds at P0 x (1 - 5% x k), k = 1..3; four equal tranches of the slot
  exit      1 tranche: take profit at avg +5% (the bounce). 2+ tranches: all out at avg +0.5%
            (break-even after costs). Both resting limits (maker).
  stop      one DCA step below the last add (P0 x 0.80), market (taker); gaps fill at the open
  time      30 days, market (taker). 24h cooldown per coin after an exit.
  book      at most 10 positions open, 10% of capital per slot, arrival order.

Fill discipline: decisions at bar close, orders rest from the next bar. An order placed in a
bar never fills in that same bar. A bar that fills a DCA add or hits the stop does not also
take profit. Limit fills are at the order price, never better.

Controls: RANDOM entries (same mechanics, the same number of entries per coin at random
eligible bars; does the 15% trigger add anything?) and SHUFFLED bars (the real rules on each
coin's bars permuted in time; does price actually bounce after drops here?). DCA-to-break-even
makes a high win rate on its own; only the gap over these controls is evidence.

  python3 scripts/dip_dca_research.py run --universe oos --exec maker
  python3 scripts/dip_dca_research.py selftest
"""
from __future__ import annotations

import argparse
import math
import os
import sys
from dataclasses import dataclass, replace

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import market_neutral_research as mnr  # noqa: E402


@dataclass(frozen=True)
class DipParams:
    drop: float = 0.15
    lookback_h: int = 24 * 7
    adds: int = 3
    step: float = 0.05
    tp: float = 0.05
    be: float = 0.005
    stop: bool = True
    max_hold_h: int = 24 * 30
    order_ttl_h: int = 24
    cooldown_h: int = 24
    slots: int = 10
    universe_top: int = 40


def dip_coin(o, h, l, c, fund, fmask, known: bool, hours, trig, p: DipParams, maker: float, taker: float):
    """One coin. Returns trades as dicts with per-bar P&L slices in units of the SLOT's capital."""
    n = len(c)
    floor = mnr.FUNDING_FLOOR_PCT_PER_8H / 100.0
    tranche = 1.0 / (p.adds + 1)
    trades = []
    state, cool_until, t_order, entry_px = "flat", 0, -1, 0.0
    for t in range(n):
        ct = c[t]
        if ct != ct:
            continue
        if state == "pending":
            if l[t] <= entry_px:
                state, t0, P0 = "long", t, entry_px
                q, cash, fills = tranche / P0, -tranche, 1
                levels = [P0 * (1 - p.step * k) for k in range(1, p.adds + 1)]
                stop_px = P0 * (1 - p.step * (p.adds + 1)) if p.stop else -1.0
                pr, fd, cs = [], [], [-tranche * maker]
                prev_eq, last_c = 0.0, P0
                if p.stop and l[t] <= stop_px:           # crashed through the stop in the fill bar
                    cash += q * stop_px
                    cs[-1] -= q * stop_px * taker
                    pr.append(cash)
                    trades.append(dict(t0=t0, t1=t, price=np.array(pr), fund=np.zeros(1), cost=np.array(cs),
                                       fills=1, reason="stop"))
                    state, cool_until = "flat", t + p.cooldown_h
                    continue
                eq = cash + q * ct
                pr.append(eq - prev_eq); fd.append(0.0); prev_eq, last_c = eq, ct
                continue
            if t > t_order + p.order_ttl_h:
                state = "flat"
        elif state == "long":
            f = 0.0
            if known:
                if fmask[t]:
                    f = -q * last_c * fund[t]
            elif hours[t] % 8 == 0:
                f = -q * last_c * floor
            cost = 0.0
            avg = (-cash) / q
            target = avg * (1 + (p.tp if fills == 1 else p.be))
            dca_or_stop = False
            while fills <= p.adds and l[t] <= levels[fills - 1]:
                px = levels[fills - 1]
                q += tranche / px; cash -= tranche; cost -= tranche * maker
                fills += 1; dca_or_stop = True
            reason = None
            if p.stop and l[t] <= stop_px:
                px = min(stop_px, o[t]) if o[t] == o[t] else stop_px
                reason, dca_or_stop = "stop", True
                cash += q * px; cost -= q * px * taker
            elif not dca_or_stop and h[t] >= target:
                reason = "tp" if fills == 1 else "be"
                cash += q * target; cost -= q * target * maker
            elif t - t0 >= p.max_hold_h:
                reason = "time"
                cash += q * ct; cost -= q * ct * taker
            eq = cash + (0.0 if reason else q * ct)
            pr.append(eq - prev_eq); fd.append(f); cs.append(cost); prev_eq, last_c = eq, ct
            if reason:
                trades.append(dict(t0=t0, t1=t, price=np.array(pr), fund=np.array(fd), cost=np.array(cs),
                                   fills=fills, reason=reason))
                state, cool_until = "flat", t + p.cooldown_h
            continue
        if state == "flat" and t >= cool_until and trig[t]:
            state, t_order, entry_px = "pending", t, ct
    return trades


def triggers(mk: mnr.Market, C: np.ndarray, p: DipParams) -> np.ndarray:
    n, m = C.shape
    elig = np.zeros((n, m), dtype=bool)
    for t in range(24 * 30, n, 24):
        elig[t:t + 24, mnr.liquid_universe(mk, t - 24 * 30, t + 1, p.universe_top)] = True
    hi = pd.DataFrame(C).rolling(p.lookback_h, min_periods=p.lookback_h // 2).max().values
    with np.errstate(invalid="ignore"):
        return elig & (C <= (1 - p.drop) * hi)


def run(mk: mnr.Market, p: DipParams, shuffle_rng=None, random_entries_like: dict | None = None,
        rng=None):
    """Returns (BookResult, trades, entries-per-coin)."""
    n, m = mk.close.shape
    O, H, L, C = mk.open.values, mk.high.values, mk.low.values, mk.close.values
    if shuffle_rng is not None:
        O, H, L, C = mnr.shuffled_bars(O, H, L, C, shuffle_rng)
    trig = triggers(mk, C, p)
    if random_entries_like is not None:
        # same number of entries per coin, at random eligible bars: the trigger's information removed
        elig = triggers(mk, C, replace(p, drop=-1.0))
        rnd = np.zeros_like(trig)
        for j in range(m):
            k = random_entries_like.get(j, 0)
            pool = np.flatnonzero(elig[:, j])
            if k and len(pool):
                rnd[rng.choice(pool, size=min(k, len(pool)), replace=False), j] = True
        trig = rnd
    hours = mk.close.index.hour.values
    all_tr, per_coin = [], {}
    for j in range(m):
        if not trig[:, j].any():
            continue
        tr = dip_coin(O[:, j], H[:, j], L[:, j], C[:, j], mk.funding.values[:, j],
                      mk.funding_mask.values[:, j], bool(mk.funding_known.values[j]), hours,
                      trig[:, j], p, mnr.COST_PER_SIDE, mnr.TAKER_COST_PER_SIDE)
        per_coin[j] = len(tr)
        for x in tr:
            x["coin"] = j
        all_tr += tr
    # book: at most `slots` open, arrival order, each slot 1/slots of capital
    price = np.zeros(n); fund = np.zeros(n); cost = np.zeros(n)
    open_until, kept = [], []
    for x in sorted(all_tr, key=lambda x: x["t0"]):
        open_until = [e for e in open_until if e >= x["t0"]]
        if len(open_until) >= p.slots:
            continue
        open_until.append(x["t1"])
        kept.append(x)
        s = x["t0"]
        k = len(x["price"])
        price[s:s + k] += x["price"] / p.slots
        fund[s:s + k] += x["fund"][:k] / p.slots if len(x["fund"]) >= k else np.pad(x["fund"], (0, k - len(x["fund"])))[:k] / p.slots
        cost[s:s + k] += x["cost"][:k] / p.slots
    start = 24 * 30
    return mnr._trim(mnr.BookResult(price, fund, cost, 0.0, mk.close.index), start), kept, per_coin


def trade_stats(tr: list) -> dict:
    if not tr:
        return {}
    net = np.array([x["price"].sum() + x["fund"].sum() + x["cost"].sum() for x in tr]) * 100
    reasons = pd.Series([x["reason"] for x in tr]).value_counts(normalize=True)
    fills = np.array([x["fills"] for x in tr])
    return {"trades": len(tr), "win%": 100 * (net > 0).mean(), "mean%slot": net.mean(),
            "worst%slot": net.min(), "stop%": 100 * reasons.get("stop", 0.0),
            "avg_fills": fills.mean(), "PnL_from_stops%": 100 * net[[x["reason"] == "stop" for x in tr]].sum() / abs(net.sum()) if net.sum() else float("nan")}


def report(mk: mnr.Market, n_controls: int, seed: int) -> None:
    p = DipParams()
    print(f"\n── DIP-DCA (drop {p.drop:.0%} from {p.lookback_h // 24}d high, {p.adds} adds every {p.step:.0%}, "
          f"TP +{p.tp:.0%} / BE +{p.be:.1%}, stop P0x{1 - p.step * (p.adds + 1):.2f}, {p.slots} slots) ──")
    rows = []

    def add(name, res, tr):
        row = mnr.summarize(name, res)
        row.update(trade_stats(tr))
        rows.append(row)
        return row

    r, tr, per_coin = run(mk, p)
    real = add("dip-dca", r, tr)
    rng = np.random.default_rng(seed)
    rc = [add(f"random#{k}", *run(mk, p, random_entries_like=per_coin, rng=rng)[:2]) for k in range(n_controls)]
    sc = [add(f"shuffle#{k}", *run(mk, p, shuffle_rng=np.random.default_rng(seed + 100 + k))[:2]) for k in range(n_controls)]
    rows = [real]
    for name, ctrl in (("RANDOM-entry control", rc), ("SHUFFLED-bars control", sc)):
        c = pd.DataFrame(ctrl)
        row = {"book": f"dip-dca: {name} (median of {n_controls})"}
        row.update(c.drop(columns="book").median(numeric_only=True).to_dict())
        rows.append(row)
        print(f"  real out-earns {(c['ann_net_%'] < real['ann_net_%']).mean():.0%} of the {name}s (net %/yr)")
    for name, pp in (("drop 10%", replace(p, drop=0.10)), ("drop 20%", replace(p, drop=0.20)),
                     ("no DCA (1 tranche, stop -5%)", replace(p, adds=0)),
                     ("no stop (pure DCA-to-BE)", replace(p, stop=False))):
        add(f"  sensitivity: {name}", *run(mk, pp)[:2])
    cols = ["ann_net_%", "ann_price_%", "ann_funding_%", "ann_cost_%", "vol_%", "sharpe", "t_weekly", "maxDD_%",
            "trades", "win%", "mean%slot", "worst%slot", "stop%", "avg_fills", "PnL_from_stops%"]
    df = pd.DataFrame(rows).set_index("book")[cols]
    with pd.option_context("display.width", 250, "display.max_columns", 30, "display.float_format", lambda v: f"{v:,.2f}"):
        print(df.to_string())
    print("  net P&L by year (% of capital):", mnr.by_year(r).to_dict())
    reasons = pd.Series([x["reason"] for x in tr]).value_counts().to_dict()
    print(f"  exits: {reasons}")


def selftest() -> int:
    fails = 0

    def check(ok, msg):
        nonlocal fails
        print(f"  [{'PASS' if ok else 'FAIL'}] {msg}")
        fails += 0 if ok else 1

    print("selftest: dip-dca on synthetic paths (true sub-hour path behind every hourly bar)")
    hrs = 24 * 365 * 2
    p = DipParams(drop=0.08)            # synthetic vol is lower than alts'; scale the trigger to it
    hours = np.arange(hrs) % 24

    def run_one(kind, seed, maker, taker):
        o, h, l, c = mnr.synthetic_ohlc(seed, hrs, kind, vol_h=0.006)
        hi = pd.Series(c).rolling(p.lookback_h, min_periods=p.lookback_h // 2).max().values
        trig = c <= (1 - p.drop) * hi
        tr = dip_coin(o, h, l, c, np.zeros(hrs), np.zeros(hrs, dtype=bool), True, hours, trig, p, maker, taker)
        gross = sum(x["price"].sum() for x in tr) * 100
        net = gross + sum(x["cost"].sum() for x in tr) * 100
        return gross, net, len(tr)

    # The fill rules (order of DCA / stop / TP inside a bar) are an assumption about the path.
    # Measure them: the same random walks as hourly OHLC and at the true sub-hour resolution,
    # where fill order is exact. A rule that manufactures profit shows as a positive paired gap.
    sub = 12
    pf = replace(p, lookback_h=p.lookback_h * sub, max_hold_h=p.max_hold_h * sub,
                 order_ttl_h=p.order_ttl_h * sub, cooldown_h=p.cooldown_h * sub)
    diffs, nets = [], []
    for k in range(16):
        o, h, l, c, fine = mnr.synthetic_ohlc(200 + k, hrs, "rw", sub=sub, vol_h=0.006, with_path=True)
        hi = pd.Series(c).rolling(p.lookback_h, min_periods=p.lookback_h // 2).max().values
        tr = dip_coin(o, h, l, c, np.zeros(hrs), np.zeros(hrs, dtype=bool), True, hours, c <= (1 - p.drop) * hi,
                      p, mnr.MAKER_COST_PER_SIDE, mnr.TAKER_COST_PER_SIDE)
        hf = pd.Series(fine).rolling(pf.lookback_h, min_periods=pf.lookback_h // 2).max().values
        nf = len(fine)
        tf = dip_coin(fine, fine, fine, fine, np.zeros(nf), np.zeros(nf, dtype=bool), True, np.arange(nf) % 24,
                      fine <= (1 - p.drop) * hf, pf, 0.0, 0.0)
        g_h = sum(x["price"].sum() for x in tr) * 100
        diffs.append(g_h - sum(x["price"].sum() for x in tf) * 100)
        nets.append(sum(x["cost"].sum() for x in tr))
    d = np.array(diffs)
    se = d.std(ddof=1) / math.sqrt(len(d))
    check(d.mean() < 2.5 * se + 2.0,
          f"hourly OHLC vs exact fill order: {d.mean():+.1f}%-of-slot per run (±{se:.1f}) — fill rules add no edge")
    check(max(nets) < 0, "costs always charged")
    g, n_, k = run_one("ou", 7, mnr.MAKER_COST_PER_SIDE, mnr.TAKER_COST_PER_SIDE)
    check(n_ > 0 and k > 0, f"mean-reverting series: net {n_:+.1f}%-of-slot over {k} trades")
    print(f"\nselftest: {'OK' if fails == 0 else f'{fails} FAILED'}")
    return 1 if fails else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["run", "selftest"])
    ap.add_argument("--universe", choices=["backtest", "oos"], default="oos")
    ap.add_argument("--exec", dest="execution", choices=["taker", "maker"], default="maker")
    ap.add_argument("--controls", type=int, default=10)
    ap.add_argument("--seed", type=int, default=12345)
    a = ap.parse_args()
    mnr.COST_PER_SIDE = mnr.MAKER_COST_PER_SIDE if a.execution == "maker" else mnr.TAKER_COST_PER_SIDE
    if a.cmd == "selftest":
        return selftest()
    key = {"oos": "OosCoins", "backtest": "BacktestCoins"}[a.universe]
    syms = mnr.config_symbols(key)
    syms = syms if "BTCUSDT" in syms else ["BTCUSDT"] + syms
    mk = mnr.build_market(syms, os.path.join(mnr.REPO, "candle_cache"))
    print(f"Universe {a.universe}: {mk.close.shape[1]} coins, {mk.close.index[0]:%Y-%m-%d} → "
          f"{mk.close.index[-1]:%Y-%m-%d}; exec {a.execution}")
    report(mk, a.controls, a.seed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
