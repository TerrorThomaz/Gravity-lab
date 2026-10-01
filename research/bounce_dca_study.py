"""Bounce-long with a 3-rung DCA sidecar and a rescue grid, and no stop loss.

Research prototype, not a live strategy. It answers one question before any C# is written:
does "never close at a loss; average down 3x; grid the bag back to break-even" have an edge
of its own, or does it reshape the payoff of whatever edge the entry signal already has?

Costs follow the repo's model: TradeCosts.FeeRoundTripPct = 0.11 plus Config.SlippageBps = 10,
so 0.21% per round trip per unit, charged per leg. Funding uses FundingRateSession's interest-rate
floor, 0.01% per 8h on open long notional. Real bull-market funding is higher, so holding a bag
costs more than this charges.

Execution is honest by construction, matching the 2026-09-24 fix in GridSimulator. Every decision
uses bar i's close, and every resting order can fill at the earliest on bar i+1.

Accounting: P&L is reported as a percentage of the capital the position must RESERVE
(base + every DCA + every rescue rung), since that money is spoken for from entry. A position
still open when the data ends is marked to the last close and counted. Dropping it is the
survivorship bias that makes "never closes at a loss" look like "never loses".

Run:  python3 research/bounce_dca_study.py
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np

FEE_LEG = (0.11 + 0.10) / 2 / 100     # fraction per leg, fee + slippage
FUND_PER_BAR = 0.0001 / 8             # 0.01%/8h floor on long notional, per h1 bar


@dataclass
class Params:
    drop_atr: float = 4.0        # entry: close fell >= drop_atr * ATR over `lookback` bars
    lookback: int = 24
    rsi_max: float = 30.0        # entry: RSI(14) below this on the signal bar
    dca_step_atr: float = 2.0    # rung k sits k*dca_step_atr*ATR_entry below the entry
    dca_sizes: tuple = (1.0, 1.5, 2.0)  # unit sizes of the 3 DCA rungs (base = 1.0)
    tp_pct: float = 1.0          # close everything at avg_cost * (1 + tp_pct%) net
    rescue_rungs: int = 4        # grid rungs below the last DCA, used only after all 3 filled
    rescue_step_atr: float = 1.5
    rescue_size: float = 1.0
    max_hold: int | None = None  # None = never close at a loss (the proposal)
    stop_pct: float | None = None  # catastrophic stop on avg cost, None = off
    dca: bool = True
    rescue: bool = True


@dataclass
class Trade:
    entry_bar: int
    exit_bar: int
    pnl: float                   # quote currency, per 1.0 base unit of notional = 1.0
    reserved: float              # max capital the plan can commit, same units
    peak_used: float             # capital actually committed at the worst moment
    mae: float                   # worst mark-to-market P&L as a fraction of reserved
    dcas_filled: int
    rescue_used: int
    open_at_end: bool
    kind: str = ""

    @property
    def ret_on_reserved(self) -> float:
        return self.pnl / self.reserved


def atr(h, l, c, n=14):
    tr = np.maximum(h[1:] - l[1:], np.maximum(abs(h[1:] - c[:-1]), abs(l[1:] - c[:-1])))
    tr = np.concatenate([[h[0] - l[0]], tr])
    out = np.empty_like(tr)
    out[0] = tr[0]
    a = 1.0 / n
    for i in range(1, len(tr)):
        out[i] = out[i - 1] + a * (tr[i] - out[i - 1])
    return out


def rsi(c, n=14):
    d = np.diff(c, prepend=c[0])
    up, dn = np.maximum(d, 0), np.maximum(-d, 0)
    au, ad = np.empty_like(c), np.empty_like(c)
    au[0], ad[0] = up[0], dn[0]
    a = 1.0 / n
    for i in range(1, len(c)):
        au[i] = au[i - 1] + a * (up[i] - au[i - 1])
        ad[i] = ad[i - 1] + a * (dn[i] - ad[i - 1])
    rs = au / np.maximum(ad, 1e-12)
    return 100 - 100 / (1 + rs)


def simulate(o, h, l, c, p: Params) -> list[Trade]:
    """One position at a time. Intrabar ordering is resolved PESSIMISTICALLY: on a bar where a
    buy order fills, no sell order (TP or rescue sell) is allowed to fill. OHLC cannot say
    whether the low or the high came first, and assuming "down, then back up" inside one bar is
    exactly the same-bar optimism that inflated Grid's Sharpe from 0.67 to 4.15."""
    n = len(c)
    A, R = atr(h, l, c), rsi(c)
    warm = max(p.lookback, 30) + 1
    trades: list[Trade] = []

    i = warm
    while i < n - 1:
        # signal on CLOSED bar i: big drop, oversold, and a bounce (close above the prior high)
        if not (c[i - p.lookback] - c[i] >= p.drop_atr * A[i]
                and R[i] < p.rsi_max and c[i] > h[i - 1]):
            i += 1
            continue

        e = i + 1                        # enter at the next bar's open
        a0 = A[i]
        qty, cost = 1.0 / o[e], 1.0      # base leg = 1.0 unit of quote notional
        fees, funding, banked = FEE_LEG, 0.0, 0.0

        dca_px = [o[e] - (k + 1) * p.dca_step_atr * a0 for k in range(3)] if p.dca else []
        dca_px = [x for x in dca_px if x > 0]
        n_dca = 0
        # rescue rungs: dicts with buy, sell, qty, armed_from
        rescue: list[dict] = []
        rescue_live_from = None
        rescue_used = 0

        reserved = 1.0 + sum(p.dca_sizes[:len(dca_px)]) \
            + (p.rescue_rungs * p.rescue_size if (p.rescue and p.dca) else 0.0)
        peak_used, mae = 1.0, 0.0

        def held_qty():
            return qty + sum(r["qty"] for r in rescue)

        def basis():
            return cost + sum(p.rescue_size for r in rescue if r["qty"] > 0)

        def tp_price():
            need = basis() * (1 + p.tp_pct / 100) + fees + funding - banked
            return need / (held_qty() * (1 - FEE_LEG))

        tp_open = tp_price()
        exit_px, j = None, e
        while j < n:
            lo, hi, op = l[j], h[j], o[j]
            funding += FUND_PER_BAR * held_qty() * op
            bought = j == e                 # the base leg filled at this bar's open

            # 1) DCA rungs, resting since entry
            while n_dca < len(dca_px) and lo <= dca_px[n_dca]:
                fpx = min(dca_px[n_dca], op)          # a gap down fills at the open
                sz = p.dca_sizes[n_dca]
                qty += sz / fpx
                cost += sz
                fees += FEE_LEG * sz
                n_dca += 1
                bought = True
                if n_dca == len(dca_px) and p.rescue and p.dca:
                    rescue = [{"buy": fpx - (k + 1) * p.rescue_step_atr * a0,
                               "sell": 0.0, "qty": 0.0, "sell_from": 0}
                              for k in range(p.rescue_rungs)]
                    rescue = [r for r in rescue if r["buy"] > 0]
                    rescue_live_from = j + 1          # placed after this fill, live next bar

            # 2) rescue grid buys
            if rescue_live_from is not None and j >= rescue_live_from:
                for r in rescue:
                    if r["qty"] == 0.0 and lo <= r["buy"]:
                        fpx = min(r["buy"], op)
                        r["qty"] = p.rescue_size / fpx
                        r["sell"] = r["buy"] + p.rescue_step_atr * a0
                        r["sell_from"] = j + 1
                        fees += FEE_LEG * p.rescue_size
                        rescue_used += 1
                        bought = True

            peak_used = max(peak_used, basis())
            mtm = held_qty() * lo - basis() - fees - funding + banked
            mae = min(mae, mtm / reserved)

            if not bought:
                # 3) rescue sells: profit is banked against the basis, the rung re-arms
                for r in rescue:
                    if r["qty"] > 0.0 and j >= r["sell_from"] and hi >= r["sell"]:
                        proceeds = r["qty"] * max(r["sell"], op)
                        banked += proceeds - p.rescue_size
                        fees += FEE_LEG * proceeds
                        r["qty"] = 0.0
                # 4) take profit at the level that stood at this bar's open
                if hi >= tp_open:
                    exit_px = max(tp_open, op)

            if exit_px is None and p.max_hold is not None and j - e >= p.max_hold:
                exit_px = c[j]
            if exit_px is None and p.stop_pct is not None \
                    and c[j] <= basis() / held_qty() * (1 - p.stop_pct / 100):
                exit_px = c[j]
            if exit_px is not None:
                break
            tp_open = tp_price()
            j += 1

        open_at_end = exit_px is None
        if open_at_end:
            j, exit_px = n - 1, c[n - 1]
        gross = held_qty() * exit_px
        fees += FEE_LEG * gross
        pnl = gross - basis() - fees - funding + banked
        trades.append(Trade(e, j, pnl, reserved, peak_used, mae, n_dca, rescue_used, open_at_end))
        i = j + 1
    return trades


# ----------------------------------------------------------------------------------------------
# Synthetic worlds. Each one has a KNOWN answer, which is the point: real data would tell us how
# the strategy did, synthetic data tells us why.
# ----------------------------------------------------------------------------------------------

BARS_PER_YEAR = 24 * 365
PATHS = 60


def make_path(rng, n, world: str):
    """h1 OHLC. Student-t(4) steps at ~1%/h, roughly an altcoin's hourly vol."""
    vol = 0.01
    z = rng.standard_t(4, n) / math.sqrt(2.0)            # unit variance
    drift = np.zeros(n)
    if world == "bleed":                                   # -60%/yr: the typical alt in a bear
        drift[:] = math.log(0.4) / BARS_PER_YEAR
    r = vol * z + drift - 0.5 * vol * vol
    if world == "bounce_edge":
        # a REAL bounce effect: after a 24h drop of >8%, the next 24h recover 30% of it.
        # This is the hypothesis the previous session believed it found.
        lp = np.cumsum(r)
        k = 0
        while k < n - 48:
            if k >= 24 and lp[k] - lp[k - 24] < -0.08:
                give = -0.3 * (lp[k] - lp[k - 24]) / 24
                r[k + 1:k + 25] += give
                lp = np.cumsum(r)
                k += 24
            k += 1
    if world == "crash":                                   # driftless, plus delist-style jumps
        jumps = rng.random(n) < 1.0 / (BARS_PER_YEAR * 1.5)  # ~1 per 1.5 years
        r[jumps] += math.log(0.35)                         # -65% and no mean reversion after
    c = 100.0 * np.exp(np.cumsum(r))
    o = np.concatenate([[100.0], c[:-1]])
    wig = np.abs(rng.normal(0, 0.004, n)) * c
    h = np.maximum(o, c) + wig
    l = np.maximum(np.minimum(o, c) - np.abs(rng.normal(0, 0.004, n)) * c, 1e-9)
    return o, h, l, c


VARIANTS = {
    # Same entry and the same +2% net target everywhere. Only the loss handling differs.
    "A stop 8%, no DCA":            Params(dca=False, rescue=False, stop_pct=8.0, tp_pct=2.0),
    "B no stop, no DCA":            Params(dca=False, rescue=False, tp_pct=2.0),
    "C 3xDCA+grid, NO stop (you)":  Params(tp_pct=2.0),
    "D 3xDCA+grid, 30d max hold":   Params(tp_pct=2.0, max_hold=24 * 30),
    "E 3xDCA, no grid, NO stop":    Params(tp_pct=2.0, rescue=False),
}


def summarize(trades: list[Trade], years: float):
    rr = np.array([t.ret_on_reserved for t in trades]) * 100
    hold = np.array([t.exit_bar - t.entry_bar for t in trades]) / 24
    cap_days = sum(t.reserved * (t.exit_bar - t.entry_bar + 1) / 24 for t in trades)
    pnl = sum(t.pnl for t in trades)
    return {
        "n": len(rr),
        "win%": 100 * np.mean(rr > 0),
        "mean%": rr.mean(),
        "median%": np.median(rr),
        "p1%": np.percentile(rr, 1),
        "worst%": rr.min(),
        "open@end": sum(t.open_at_end for t in trades),
        "worstMAE%": 100 * min(t.mae for t in trades),
        "hold_d_p50": np.median(hold),
        "hold_d_max": hold.max(),
        # P&L per calendar year on the capital the plan must RESERVE for one slot. C reserves
        # 9.5 base units (1 + 1 + 1.5 + 2 + 4 rescue) to make the same +2% on what fills.
        "yr%/slot": 100 * pnl / (trades[0].reserved * years * PATHS),
    }


def t_stat(x):
    x = np.asarray(x)
    return x.mean() / (x.std(ddof=1) / math.sqrt(len(x))) if len(x) > 2 else 0.0


def run(world: str, paths: int = PATHS, years: float = 3.0, seed: int = 7):
    n = int(BARS_PER_YEAR * years)
    rng = np.random.default_rng(seed)
    data = [make_path(rng, n, world) for _ in range(paths)]
    print(f"\n=== world: {world}   ({paths} paths x {years:.0f}y of h1)")
    hdr = (f"{'variant':30s} {'n':>5s} {'win%':>6s} {'mean%':>7s} {'t':>6s} {'median%':>8s} "
           f"{'p1%':>7s} {'worst%':>7s} {'open':>5s} {'MAE%':>7s} {'hold50d':>7s} "
           f"{'holdMax':>7s} {'yr%/slot':>8s}")
    print(hdr)
    for name, p in VARIANTS.items():
        tr = [t for d in data for t in simulate(*d, p)]
        s = summarize(tr, years)
        tt = t_stat([t.ret_on_reserved for t in tr])
        print(f"{name:30s} {s['n']:5d} {s['win%']:6.1f} {s['mean%']:7.2f} {tt:6.2f} "
              f"{s['median%']:8.2f} {s['p1%']:7.1f} {s['worst%']:7.1f} {s['open@end']:5d} "
              f"{s['worstMAE%']:7.1f} {s['hold_d_p50']:7.1f} {s['hold_d_max']:7.0f} "
              f"{s['yr%/slot']:8.2f}")


if __name__ == "__main__":
    for w in ("random_walk", "bleed", "crash", "bounce_edge"):
        run(w)
