"""ONE pre-registered trial: market time-series momentum on BTC + ETH, 2017-09 → 2026-10, with funding.

Spec (frozen with the pre-registration in docs/EDGE_DISCOVERY_SCOPE_2026-10.md; the trial-1 sleeve
from research/power_check.py, Moskowitz-Ooi-Pedersen with a crypto-length lookback):
  data      Binance spot daily klines BTCUSDT, ETHUSDT (from 2017-08-17). A day runs 00:00 → 00:00 UTC.
  signal    s_d = sign of the equal-weight (BTC, ETH) 30-day log return at the close of day d.
  position  7 overlapping daily cohorts, 1/7 of capital each, half BTC half ETH; the cohort opened at
            the open of day d+1 takes side s_d and closes at the open of day d+8. Exposure on day t is
            the net of the open cohorts (|gross| ≤ 1, never levered).
  costs     taker (fee + slippage, research/tradecosts-equivalent 0.105%/side) on the NET daily change
            of each coin's position: an execution nets offsetting cohort orders.
  funding   a long pays a positive rate, per settlement falling in (day t 00:00, day t+1 00:00].
            Source by day: Binance USDT-M funding where it exists (2019-09 →), else BitMEX
            (XBTUSD 2016-05 →, ETHUSD 2018-08 →), else the repo floor 0.01%/8h charged as a cost on
            either side.
  book      the live book: ERC(carry, Grid, GridShort) daily, as research/power_check.book_daily.
            Risk-budget sizing: monthly, from trailing-90d vols strictly before the month,
            w_i ∝ b_i/σ_i summing to 1 (b = 0.8 book / 0.2 trend PRIMARY; 0.1 and 0.3 printed only).

Decision rules: see the pre-registration. Run once.

Run:  python3 research/binance_trend.py --fetch      (data only, no statistics)
      python3 research/binance_trend.py --selftest
      python3 research/binance_trend.py --run --trades <edgetest trade log>
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
import urllib.request

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import mnr  # noqa: E402

DATA = os.path.join(mnr.REPO, "candle_cache", "binance")
ASSETS = {"BTC": ("BTCUSDT", "XBTUSD"), "ETH": ("ETHUSDT", "ETHUSD")}
LOOK, HOLD = 30, 7
COST = mnr.TAKER_COST_PER_SIDE                                  # fraction per side
FLOOR_DAY = 3 * mnr.FUNDING_FLOOR_PCT_PER_8H / 100
START, END = pd.Timestamp("2017-09-16"), pd.Timestamp("2026-10-04")
CLEAN_END = pd.Timestamp("2020-03-25")                          # Bybit cache starts here: before it, never seen
BUDGET = 0.2


def _get(url):
    for k in range(5):
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                return json.load(r)
        except Exception:
            time.sleep(2 + 2 * k)
    raise SystemExit(f"fetch failed: {url}")


def fetch() -> int:
    os.makedirs(DATA, exist_ok=True)
    for a, (bn, bm) in ASSETS.items():
        rows, t = [], 1500000000000
        while True:
            d = _get(f"https://api.binance.com/api/v3/klines?symbol={bn}&interval=1d&startTime={t}&limit=1000")
            if not d:
                break
            rows += d
            t = d[-1][0] + 86400000
            if len(d) < 1000:
                break
        k = pd.DataFrame([r[:5] for r in rows], columns=["t", "open", "high", "low", "close"])
        k["day"] = pd.to_datetime(k.t, unit="ms")
        k[["day", "open", "high", "low", "close"]].to_csv(os.path.join(DATA, f"{bn}_1d.csv"), index=False)
        f, t = [], 1500000000000
        while True:
            d = _get(f"https://fapi.binance.com/fapi/v1/fundingRate?symbol={bn}&startTime={t}&limit=1000")
            if not d:
                break
            f += [(x["fundingTime"], float(x["fundingRate"])) for x in d]
            t = d[-1]["fundingTime"] + 1
            if len(d) < 1000:
                break
        pd.DataFrame(f, columns=["ms", "rate"]).to_csv(os.path.join(DATA, f"{bn}_funding_binance.csv"), index=False)
        g, start = [], 0
        while True:
            d = _get(f"https://www.bitmex.com/api/v1/funding?symbol={bm}&count=500&start={start}&reverse=false")
            if not d:
                break
            g += [(x["timestamp"], float(x["fundingRate"])) for x in d]
            start += len(d)
            if len(d) < 500:
                break
            time.sleep(1.2)                                       # BitMEX public limit
        pd.DataFrame(g, columns=["ts", "rate"]).to_csv(os.path.join(DATA, f"{bn}_funding_bitmex.csv"), index=False)
        print(f"{a}: {len(k)} daily bars {k.day.min():%Y-%m-%d} → {k.day.max():%Y-%m-%d}; Binance funding {len(f)}, "
              f"BitMEX funding {len(g)} ({g[0][0][:10] if g else '—'} →)")
    return 0


def daily_funding(bn: str, days: pd.DatetimeIndex) -> tuple[np.ndarray, np.ndarray]:
    """Per day t: summed rate of settlements in (t, t+1], and whether a real source covered it."""
    b = pd.read_csv(os.path.join(DATA, f"{bn}_funding_binance.csv"))
    m = pd.read_csv(os.path.join(DATA, f"{bn}_funding_bitmex.csv"))
    key = lambda ts: (ts - pd.Timedelta(milliseconds=1)).dt.floor("D")
    bs = b.rate.groupby(key(pd.to_datetime(b.ms, unit="ms"))).sum()
    ms = m.rate.groupby(key(pd.to_datetime(m.ts).dt.tz_localize(None))).sum()
    b0 = bs.index.min()
    rate = np.where(days >= b0, bs.reindex(days).fillna(0.0).values, ms.reindex(days).values)
    known = (days >= b0) | ms.reindex(days).notna().values
    return np.nan_to_num(rate), known


def load():
    px, fr, kn = {}, {}, {}
    for a, (bn, _) in ASSETS.items():
        k = pd.read_csv(os.path.join(DATA, f"{bn}_1d.csv"), parse_dates=["day"]).set_index("day")
        px[a] = k
    days = px["BTC"].index.intersection(px["ETH"].index)
    for a, (bn, _) in ASSETS.items():
        fr[a], kn[a] = daily_funding(bn, days)
    O = np.column_stack([px[a].open.reindex(days).values for a in ASSETS])
    C = np.column_stack([px[a].close.reindex(days).values for a in ASSETS])
    F = np.column_stack([fr[a] for a in ASSETS])
    K = np.column_stack([kn[a] for a in ASSETS])
    return days, O, C, F, K


def sleeve(O, C, F, K, shift: int = 0):
    """Daily net return (open t → open t+1) of the frozen sleeve. shift > 0 rolls the signal
    circularly (placebo: same signal statistics, broken timing)."""
    D = len(C)
    lc = np.log(C)
    s = np.full(D, np.nan)
    s[LOOK:] = np.sign((lc[LOOK:] - lc[:-LOOK]).mean(1))
    if shift:
        v = np.isfinite(s)
        s[v] = np.roll(s[v], shift)
    pos = np.zeros(D)                                             # exposure (per coin: pos/2) during day t
    for k in range(1, HOLD + 1):                                  # cohort from day t−k's close, open on day t
        d = np.arange(D) - k
        pos += np.where(d >= LOOK, s[np.clip(d, 0, D - 1)], 0.0) / HOLD
    P = np.column_stack([pos, pos]) / 2
    ret = np.zeros(D)
    ret[:-1] = (P[:-1] * (O[1:] / O[:-1] - 1)).sum(1)
    trade = np.abs(np.diff(np.vstack([np.zeros((1, 2)), P]), axis=0)).sum(1)
    fund = np.where(K, -P * F, -np.abs(P) * FLOOR_DAY).sum(1)
    return ret - COST * trade + fund, pos, (ret, COST * trade, fund)


def sharpe(x):
    x = np.asarray(x)
    return float(x.mean() / x.std() * math.sqrt(365)) if x.std() > 0 else 0.0


def week_t(x):
    w = x[: len(x) // 7 * 7].reshape(-1, 7).sum(1)
    return float(w.mean() / w.std(ddof=1) * math.sqrt(len(w)))


def budget_combine(S: pd.DataFrame, b: np.ndarray, lookback_d: int = 90, min_d: int = 60) -> pd.Series:
    """Risk-budget split, monthly: w_i ∝ b_i/σ_i (trailing vols strictly before the month), sum 1."""
    months = S.index.to_period("M")
    w = pd.DataFrame(index=S.index, columns=S.columns, dtype=float)
    for mth in months.unique():
        st = mth.start_time
        past = S[(S.index < st) & (S.index >= st - pd.Timedelta(days=lookback_d))]
        wt = b / past.std().values if len(past) >= min_d else b.copy()
        w.loc[months == mth] = wt / wt.sum()
    return (w * S).sum(axis=1)


def row(name, x: pd.Series) -> dict:
    eq = (1 + x).cumprod()
    yrs = len(x) / 365
    return {"": name, "years": yrs, "CAGR %": 100 * (eq.iloc[-1] ** (1 / yrs) - 1), "vol %": 100 * x.std() * math.sqrt(365),
            "Sharpe": sharpe(x), "maxDD %": 100 * (eq / eq.cummax() - 1).min(), "weekly t": week_t(x.values)}


def run(trades: str) -> int:
    from power_check import alpha_t, book_daily
    days, O, C, F, K = load()
    net, pos, (pr, co, fu) = sleeve(O, C, F, K)
    x = pd.Series(net, index=days)
    w = (days >= START) & (days < END)
    xs = x[w]
    mid = xs.index[len(xs) // 2]
    h1, h2 = xs[xs.index < mid], xs[xs.index >= mid]
    clean = xs[xs.index < CLEAN_END]
    rng = np.random.default_rng(11)
    nv = int(np.isfinite(np.r_[np.full(LOOK, np.nan), np.ones(len(days) - LOOK)]).sum())
    plac = [sharpe(pd.Series(sleeve(O, C, F, K, shift=int(k))[0], index=days)[w])
            for k in rng.integers(60, nv - 60, 100)]
    pct = float(np.mean(np.array(plac) < sharpe(xs)))
    yrs = len(clean) / 365
    print(f"BINANCE TREND one-shot: BTC+ETH market TSMOM {LOOK}d/{HOLD}d, {xs.index[0]:%Y-%m-%d} → {xs.index[-1]:%Y-%m-%d}")
    print(f"  funding: real-source share {K[w].mean():.0%} of coin-days; long share {np.mean(pos[w] > 0):.0%} of days")
    print(f"  components, %/yr: price {100 * pr[w].mean() * 365:+.1f}  costs {-100 * co[w].mean() * 365:+.1f}  "
          f"funding {100 * fu[w].mean() * 365:+.1f}")
    tab = [row("full window", xs), row("half 1", h1), row("half 2", h2), row("CLEAN (pre-2020-03, never seen)", clean)]
    tab += [row(f"year {y}", xs[xs.index.year == y]) for y in sorted(set(xs.index.year))]
    with pd.option_context("display.width", 200, "display.float_format", lambda v: f"{v:,.2f}"):
        print(pd.DataFrame(tab).set_index("").to_string())
    print(f"  placebo (100 circular signal shifts): real Sharpe {sharpe(xs):+.2f} beats {pct:.0%}; "
          f"placebo median {np.median(plac):+.2f}, p90 {np.quantile(plac, 0.9):+.2f}")
    print(f"  clean-window power at true Sharpe 1: {1 - 0.5 * math.erfc(-(math.sqrt(yrs) - 2) / math.sqrt(2)):.0%} "
          f"(t ≈ SR·√{yrs:.1f}y)")

    book, _ = book_daily(trades)
    j = book.index.intersection(xs.index)
    bb, xb = book.reindex(j), xs.reindex(j)
    rho = float(np.corrcoef(xb, bb)[0, 1])
    at = alpha_t(xb.values, bb.values)
    worst = bb <= bb.quantile(0.05)
    print(f"\n  vs live book ({j[0]:%Y-%m-%d} → {j[-1]:%Y-%m-%d}, seen data): trend Sharpe {sharpe(xb):+.2f}, ρ {rho:+.2f}, "
          f"alpha t {at:+.2f}; on the book's worst 5% days {100 * xb[worst].mean():+.3f}%/day (book {100 * bb[worst].mean():+.3f})")
    S = pd.DataFrame({"book": bb, "trend": xb})
    tab = [row("book alone", bb)]
    for b in (BUDGET, 0.1, 0.3):
        tab.append(row(f"book + trend, risk budget {b:.0%}" + (" (PRIMARY)" if b == BUDGET else ""),
                       budget_combine(S, np.array([1 - b, b]))))
    with pd.option_context("display.width", 200, "display.float_format", lambda v: f"{v:,.2f}"):
        print(pd.DataFrame(tab).set_index("").to_string())

    sa = sharpe(xs) > 0 and sharpe(h1) > 0 and sharpe(h2) > 0
    standalone = sa and week_t(xs.values) >= 2 and pct >= 0.9
    addition = at >= 2 and sa
    print(f"\nVERDICT  standalone: {'PASS' if standalone else 'FAIL'}   book addition (alpha t ≥ 2, no bleed): "
          f"{'PASS' if addition else 'FAIL'}   no-bleed condition (Sharpe > 0 full + both halves): {'met' if sa else 'NOT met'}")
    return 0


def selftest() -> int:
    """Sleeve accounting on a synthetic path: a steady uptrend → long, earns the drift less costs and
    funding; netting charges no cost while the signal holds; a placebo shift is a permutation."""
    D = 400
    p = 100 * np.exp(np.r_[np.zeros(50), np.linspace(0, 1.0, 350)])
    O = C = np.column_stack([p, p])
    F = np.full((D, 2), 0.0003)
    K = np.ones((D, 2), bool)
    net, pos, (pr, co, fu) = sleeve(O, C, F, K)
    assert np.isclose(pos[200], 1.0) and pos[:LOOK + 1].sum() == 0, "not fully long in the trend / traded before the signal"
    assert co[100:390].sum() == 0, "netting broken: costs while the signal is unchanged"
    assert np.allclose(fu[200], -0.0003), "long should pay the positive rate on its full exposure"
    assert abs(pr[200] - (p[201] / p[200] - 1)) < 1e-12
    F2 = F.copy(); K2 = K.copy(); K2[:, :] = False
    _, _, (_, _, fu2) = sleeve(O, C, F2, K2)
    assert np.allclose(fu2[200], -FLOOR_DAY), "floor must cost either side"
    a, b = sleeve(O, C, F, K, shift=37)[1], pos
    assert abs(np.sort(a[LOOK + HOLD:]).sum() - np.sort(b[LOOK + HOLD:]).sum()) < 60, "shift changed the signal mix"
    print("selftest: OK (long in trend, netting, funding sign + floor, placebo shift)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--fetch", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--run", action="store_true")
    ap.add_argument("--trades", default=os.path.join(mnr.REPO, "reports", "live_book_trades_2026-10-04.csv"))
    a = ap.parse_args()
    if a.fetch:
        return fetch()
    if a.selftest:
        return selftest()
    if a.run:
        return run(a.trades)
    ap.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main())
