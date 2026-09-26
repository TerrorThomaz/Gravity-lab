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
    # After the LAST add, wait for structure instead of the fixed stop: exit on a close below the
    # most recent confirmed swing low that formed after that add (a fractal, pivot_k BARS each
    # side, usable pivot_k bars later). Until one exists, a catastrophic stop one ladder step
    # below the old stop. Otherwise hold for the break-even limit.
    bos_after_max: bool = False
    pivot_k: int = 2


# High-frequency variant, fixed before its first run (2026-09-26): a 3% dip from the 24h high,
# adds every 1%, +1% bounce / +0.2% break-even (maker round trip is 0.04%), stop one step below
# the last add, 24h max hold. Run on 15m bars: at 1% steps an hourly bar often spans an add and
# the target, and the conservative same-bar rule would then decide the result, not the market.
HF = DipParams(drop=0.03, lookback_h=24, adds=3, step=0.01, tp=0.01, be=0.002,
               max_hold_h=24, order_ttl_h=1, cooldown_h=1)


# A limit fills only when price trades THROUGH it by this much, not when it merely touches.
# A touch-fill buys every time the bar opens at the order price, including the times price then
# runs away: on the exact sub-bar path those orders never fill (adverse selection), and the
# selftest measured the touch rule as optimistic at +0.023%-of-slot per trade, ~7 s.e.
THROUGH = 0.0005


def in_bars(p: DipParams, bph: int) -> DipParams:
    """The params' hour counts expressed in bars of 60/bph minutes."""
    return replace(p, lookback_h=p.lookback_h * bph, max_hold_h=p.max_hold_h * bph,
                   order_ttl_h=p.order_ttl_h * bph, cooldown_h=p.cooldown_h * bph)


def build_market_15m(syms: list[str], cache: str) -> mnr.Market:
    """Same shape as mnr.build_market, on the C# 15m cache directly (float32 to fit memory)."""
    frames = {}
    for sym in syms:
        df = mnr._read_ms_csv(os.path.join(cache, f"{sym}_15m.csv"), 6)
        if df is None or len(df) < 96 * 60:
            continue
        df.columns = ["o", "h", "l", "c", "v"]
        frames[sym] = df[~df.index.duplicated(keep="last")]
    idx = pd.date_range(min(f.index.min() for f in frames.values()),
                        max(f.index.max() for f in frames.values()), freq="15min")

    def mat(col):
        return pd.DataFrame({k: f[col] for k, f in frames.items()}).reindex(idx).astype("float32")
    close = mat("c")
    funding = pd.DataFrame(np.zeros(close.shape, dtype="float32"), index=idx, columns=close.columns)
    mask = pd.DataFrame(False, index=idx, columns=close.columns)
    known = {}
    for sym in close.columns:
        f = mnr.load_funding(sym, cache)
        known[sym] = f is not None
        if f is None:
            continue
        f.index = f.index.floor("15min")
        f = f[~f.index.duplicated(keep="last")]
        f = f[(f.index >= idx[0]) & (f.index <= idx[-1])]
        funding.loc[f.index, sym] = f.values.astype("float32")
        mask.loc[f.index, sym] = True
    return mnr.Market(close, close * mat("v"), funding, pd.Series(known), mask, [],
                      mat("o"), mat("h"), mat("l"))


def dip_coin(o, h, l, c, fund, fmask, known: bool, floor_tick, trig, p: DipParams, maker: float, taker: float):
    """One coin. `p` in BARS (see in_bars). floor_tick marks the 8h settlements where a symbol
    without funding data pays the floor. Returns trades with per-bar P&L slices in units of the
    SLOT's capital."""
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
            if l[t] <= entry_px * (1 - THROUGH):
                state, t0, P0 = "long", t, entry_px
                q, cash, fills = tranche / P0, -tranche, 1
                levels = [P0 * (1 - p.step * k) for k in range(1, p.adds + 1)]
                stop_px = P0 * (1 - p.step * (p.adds + 1)) if p.stop else -1.0
                pr, fd, cs = [], [], [-tranche * maker]
                prev_eq, last_c = 0.0, P0
                # Price reached the entry from above, so any add level this bar's low went through
                # was reached AFTER the entry: those adds certainly filled. Skipping them would let
                # the trade keep its single-tranche +TP target on a path that had already averaged
                # down (optimistic), and would stop out a crash holding one tranche instead of all.
                while fills <= p.adds and l[t] <= levels[fills - 1] * (1 - THROUGH):
                    px = levels[fills - 1]
                    q += tranche / px; cash -= tranche; cs[-1] -= tranche * maker
                    fills += 1
                t_max = t if fills == p.adds + 1 else None
                struct_low = None
                if p.bos_after_max and t_max is not None:
                    stop_px = P0 * (1 - p.step * (p.adds + 2))
                if p.stop and l[t] <= stop_px:           # crashed through the stop in the fill bar
                    cash += q * stop_px
                    cs[-1] -= q * stop_px * taker
                    pr.append(cash)
                    trades.append(dict(t0=t0, t1=t, price=np.array(pr), fund=np.zeros(1), cost=np.array(cs),
                                       fills=fills, reason="stop"))
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
            elif floor_tick[t]:
                f = -q * last_c * floor
            cost = 0.0
            avg = (-cash) / q
            target = avg * (1 + (p.tp if fills == 1 else p.be))
            dca_or_stop = False
            while fills <= p.adds and l[t] <= levels[fills - 1] * (1 - THROUGH):
                px = levels[fills - 1]
                q += tranche / px; cash -= tranche; cost -= tranche * maker
                fills += 1; dca_or_stop = True
                if fills == p.adds + 1:
                    t_max = t
                    if p.bos_after_max:     # the fixed stop gives way to structure + catastrophe stop
                        stop_px = P0 * (1 - p.step * (p.adds + 2))
            reason = None
            if p.bos_after_max and struct_low is not None and ct < struct_low:
                reason, dca_or_stop = "bos", True          # structure broke down: the crash case
                cash += q * ct; cost -= q * ct * taker
            elif p.stop and l[t] <= stop_px:
                px = min(stop_px, o[t]) if o[t] == o[t] else stop_px
                reason, dca_or_stop = "stop", True
                cash += q * px; cost -= q * px * taker
            elif not dca_or_stop and h[t] >= target * (1 + THROUGH):
                reason = "tp" if fills == 1 else "be"
                cash += q * target; cost -= q * target * maker
            elif t - t0 >= p.max_hold_h:
                reason = "time"
                cash += q * ct; cost -= q * ct * taker
            eq = cash + (0.0 if reason else q * ct)
            pr.append(eq - prev_eq); fd.append(f); cs.append(cost); prev_eq, last_c = eq, ct
            if not reason and p.bos_after_max and t_max is not None:
                i, k = t - p.pivot_k, p.pivot_k       # confirmed at this close; formed after the last add
                if i - k >= t_max and l[i] <= np.min(l[i - k:t + 1]):
                    struct_low = l[i]
            if reason:
                trades.append(dict(t0=t0, t1=t, price=np.array(pr), fund=np.array(fd), cost=np.array(cs),
                                   fills=fills, reason=reason))
                state, cool_until = "flat", t + p.cooldown_h
            continue
        if state == "flat" and t >= cool_until and trig[t]:
            state, t_order, entry_px = "pending", t, ct
    return trades


def triggers(mk: mnr.Market, C: np.ndarray, p: DipParams, bph: int = 1) -> np.ndarray:
    """p in bars; bph = bars per hour (for the daily liquidity refresh and its 30d window)."""
    n, m = C.shape
    elig = np.zeros((n, m), dtype=bool)
    day, month = 24 * bph, 24 * 30 * bph
    for t in range(month, n, day):
        elig[t:t + day, mnr.liquid_universe(mk, t - month, t + 1, p.universe_top)] = True
    hi = pd.DataFrame(C).rolling(p.lookback_h, min_periods=p.lookback_h // 2).max().values
    with np.errstate(invalid="ignore"):
        return elig & (C <= (1 - p.drop) * hi)


def run(mk: mnr.Market, p: DipParams, shuffle_rng=None, random_entries_like: dict | None = None,
        rng=None, bph: int = 1):
    """p in HOURS. Returns (BookResult, trades, entries-per-coin)."""
    n, m = mk.close.shape
    pb = in_bars(p, bph)
    O, H, L, C = mk.open.values, mk.high.values, mk.low.values, mk.close.values
    if shuffle_rng is not None:
        O, H, L, C = mnr.shuffled_bars(O, H, L, C, shuffle_rng)
    trig = triggers(mk, C, pb, bph)
    if random_entries_like is not None:
        # same number of entries per coin, at random eligible bars: the trigger's information removed
        elig = triggers(mk, C, replace(pb, drop=-1.0), bph)
        rnd = np.zeros_like(trig)
        for j in range(m):
            k = random_entries_like.get(j, 0)
            pool = np.flatnonzero(elig[:, j])
            if k and len(pool):
                rnd[rng.choice(pool, size=min(k, len(pool)), replace=False), j] = True
        trig = rnd
    ix = mk.close.index
    floor_tick = np.asarray((ix.hour % 8 == 0) & (ix.minute == 0))
    all_tr, per_coin = [], {}
    for j in range(m):
        if not trig[:, j].any():
            continue
        tr = dip_coin(O[:, j], H[:, j], L[:, j], C[:, j], mk.funding.values[:, j],
                      mk.funding_mask.values[:, j], bool(mk.funding_known.values[j]), floor_tick,
                      trig[:, j], pb, mnr.COST_PER_SIDE, mnr.TAKER_COST_PER_SIDE)
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
    start = 24 * 30 * bph
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


def report(mk: mnr.Market, n_controls: int, seed: int, p: DipParams, bph: int) -> None:
    print(f"\n── DIP-DCA (drop {p.drop:.1%} from {p.lookback_h}h high, {p.adds} adds every {p.step:.1%}, "
          f"TP +{p.tp:.1%} / BE +{p.be:.1%}, stop P0x{1 - p.step * (p.adds + 1):.2f}, max hold {p.max_hold_h}h, "
          f"{p.slots} slots, {60 // bph}m bars) ──")
    rows = []

    def add(name, res, tr):
        row = mnr.summarize(name, res)
        row.update(trade_stats(tr))
        rows.append(row)
        return row

    r, tr, per_coin = run(mk, p, bph=bph)
    real = add("dip-dca", r, tr)
    rng = np.random.default_rng(seed)
    rc = [add(f"random#{k}", *run(mk, p, random_entries_like=per_coin, rng=rng, bph=bph)[:2]) for k in range(n_controls)]
    sc = [add(f"shuffle#{k}", *run(mk, p, shuffle_rng=np.random.default_rng(seed + 100 + k), bph=bph)[:2])
          for k in range(n_controls)]
    rows = [real]
    for name, ctrl in (("RANDOM-entry control", rc), ("SHUFFLED-bars control", sc)):
        c = pd.DataFrame(ctrl)
        row = {"book": f"dip-dca: {name} (median of {n_controls})"}
        row.update(c.drop(columns="book").median(numeric_only=True).to_dict())
        rows.append(row)
        print(f"  real out-earns {(c['ann_net_%'] < real['ann_net_%']).mean():.0%} of the {name}s (net %/yr)")
    scale = lambda f: replace(p, drop=p.drop * f, step=p.step * f, tp=p.tp * f)
    for name, pp in (("all moves x0.5", scale(0.5)), ("all moves x2", scale(2.0)),
                     (f"no DCA (1 tranche, stop -{p.step:.1%})", replace(p, adds=0)),
                     ("no stop (pure DCA-to-BE)", replace(p, stop=False))):
        add(f"  sensitivity: {name}", *run(mk, pp, bph=bph)[:2])
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

    for label, p0, bph, vol, bars in (("std", DipParams(drop=0.08), 1, 0.006, 24 * 365 * 2),
                                      ("hf", HF, 4, 0.003, 24 * 4 * 365)):
        # synthetic vol sits below alts', so the std trigger is scaled to it; hf uses its real params
        p = in_bars(p0, bph)
        print(f"selftest [{label}]: dip-dca on synthetic paths (true sub-bar path behind every bar)")
        tick = np.arange(bars) % (8 * bph) == 0
        sub = 12
        pf = in_bars(p, sub)
        diffs, nets = [], []
        for k in range(24):
            o, h, l, c, fine = mnr.synthetic_ohlc(200 + k, bars, "rw", sub=sub, vol_h=vol, with_path=True)
            hi = pd.Series(c).rolling(p.lookback_h, min_periods=p.lookback_h // 2).max().values
            # Bar side pays only the modelled SLIPPAGE on its market exits (no fees on either side):
            # the exact path stops out at the first price past the stop, an overshoot that live is
            # slippage, and the bar model fills at the stop price and charges that slippage as cost.
            slip = mnr.SLIPPAGE_BPS_ROUND_TRIP / 2 / 10_000
            tr = dip_coin(o, h, l, c, np.zeros(bars), np.zeros(bars, dtype=bool), True, tick,
                          c <= (1 - p.drop) * hi, p, 0.0, slip)
            nf = len(fine)
            hf_ = pd.Series(fine).rolling(pf.lookback_h, min_periods=pf.lookback_h // 2).max().values
            tf = dip_coin(fine, fine, fine, fine, np.zeros(nf), np.zeros(nf, dtype=bool), True,
                          np.arange(nf) % (8 * bph * sub) == 0, fine <= (1 - p.drop) * hf_, pf, 0.0, 0.0)
            diffs.append((sum(x["price"].sum() + x["cost"].sum() for x in tr)
                          - sum(x["price"].sum() for x in tf)) * 100 / max(1, len(tr)))
            nets.append(sum(x["cost"].sum() for x in
                            dip_coin(o, h, l, c, np.zeros(bars), np.zeros(bars, dtype=bool), True, tick,
                                     c <= (1 - p.drop) * hi, p, mnr.MAKER_COST_PER_SIDE, mnr.TAKER_COST_PER_SIDE)))
        d = np.array(diffs)
        se = d.std(ddof=1) / math.sqrt(len(d))
        # The fill rules (order of DCA / stop / TP inside a bar) are an assumption about the path.
        # Measured here against the true sub-bar path, per trade: optimism shows as a positive gap.
        check(d.mean() < 2.5 * se,
              f"bar OHLC vs exact fill order: {d.mean():+.3f}%-of-slot per trade (±{se:.3f}) — fill rules add no edge")
        check(max(nets) < 0, "costs always charged")
        o, h, l, c = mnr.synthetic_ohlc(7, bars, "ou", vol_h=vol)
        hi = pd.Series(c).rolling(p.lookback_h, min_periods=p.lookback_h // 2).max().values
        tr = dip_coin(o, h, l, c, np.zeros(bars), np.zeros(bars, dtype=bool), True, tick,
                      c <= (1 - p.drop) * hi, p, mnr.MAKER_COST_PER_SIDE, mnr.TAKER_COST_PER_SIDE)
        net = sum(x["price"].sum() + x["cost"].sum() for x in tr) * 100
        check(len(tr) > 0 and net > 0, f"mean-reverting series: net {net:+.1f}%-of-slot over {len(tr)} trades")
    print("selftest [bos]: after the last add, structure decides, not the old fixed stop")
    pb = replace(HF, bos_after_max=True, cooldown_h=0, max_hold_h=1000, order_ttl_h=5)
    # entry 100 (trigger bar), adds fill at 99/98/97; old fixed stop 96, catastrophe stop 95;
    # a swing low at 95.6 forms after the last add (below the OLD stop), then a close at 95.5
    px = [100, 99.9, 98.9, 97.9, 96.9, 96.3, 95.6, 96.2, 96.4, 96.0, 95.5]
    a = np.array(px, dtype=float)
    trig = np.zeros(len(a), dtype=bool); trig[0] = True
    z = np.zeros(len(a))
    tr = dip_coin(a, a, a, a, z, z.astype(bool), True, z.astype(bool), trig, pb, 0.0, 0.0)
    check(len(tr) == 1 and tr[0]["reason"] == "bos" and tr[0]["t1"] == len(a) - 1 and tr[0]["fills"] == 4,
          f"holds through the old -4% stop, exits on the close below the confirmed swing low "
          f"({tr[0]['reason'] if tr else 'no trade'} at bar {tr[0]['t1'] if tr else '-'})")
    a2 = np.array(px[:9] + [97.5, 98.2, 98.9], dtype=float)
    trig2 = np.zeros(len(a2), dtype=bool); trig2[0] = True
    z2 = np.zeros(len(a2))
    tr2 = dip_coin(a2, a2, a2, a2, z2, z2.astype(bool), True, z2.astype(bool), trig2, pb, 0.0, 0.0)
    check(len(tr2) == 1 and tr2[0]["reason"] == "be", f"recovery without a breakdown exits at break-even "
          f"({tr2[0]['reason'] if tr2 else 'no trade'})")

    print(f"\nselftest: {'OK' if fails == 0 else f'{fails} FAILED'}")
    return 1 if fails else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["run", "selftest"])
    ap.add_argument("--universe", choices=["backtest", "oos"], default="oos")
    ap.add_argument("--exec", dest="execution", choices=["taker", "maker"], default="maker")
    ap.add_argument("--controls", type=int, default=10)
    ap.add_argument("--variant", choices=["std", "hf", "hf-bos"], default="std",
                    help="std: 15%% dip / 5%% steps on 1h bars; hf: 3%% dip / 1%% steps on 15m bars")
    ap.add_argument("--seed", type=int, default=12345)
    a = ap.parse_args()
    mnr.COST_PER_SIDE = mnr.MAKER_COST_PER_SIDE if a.execution == "maker" else mnr.TAKER_COST_PER_SIDE
    if a.cmd == "selftest":
        return selftest()
    key = {"oos": "OosCoins", "backtest": "BacktestCoins"}[a.universe]
    syms = mnr.config_symbols(key)
    syms = syms if "BTCUSDT" in syms else ["BTCUSDT"] + syms
    cache = os.path.join(mnr.REPO, "candle_cache")
    p, bph = {"std": (DipParams(), 1), "hf": (HF, 4), "hf-bos": (replace(HF, bos_after_max=True), 4)}[a.variant]
    mk = build_market_15m(syms, cache) if bph == 4 else mnr.build_market(syms, cache)
    print(f"Universe {a.universe}: {mk.close.shape[1]} coins, {mk.close.index[0]:%Y-%m-%d} → "
          f"{mk.close.index[-1]:%Y-%m-%d}; exec {a.execution}")
    report(mk, a.controls, a.seed, p, bph)
    return 0


if __name__ == "__main__":
    sys.exit(main())
