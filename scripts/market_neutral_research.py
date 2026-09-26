#!/usr/bin/env python3
"""
Market-neutral research harness: pairs stat-arb and hedged funding carry.

Standalone on purpose. It shares no code path with the directional strategies, the GA,
the router or the guard, so a result here cannot be flattered by any of them. It shares
only the COST CONSTANTS (below, copied from src/core) and the SYMBOL LISTS (parsed from
src/core/Config.cs at runtime, so there is one authority).

Three books, all walk-forward and all with parameters fixed in advance (no search here):

  pairs    Engle-Granger cointegration pairs. Pairs are selected on a FORMATION window,
           then traded on the NEXT window with the formation hedge ratio frozen. Every
           window is out-of-sample for its own selection. A RANDOM-PAIRS control runs the
           exact same trading rules on randomly drawn pairs from the same universe: if
           cointegration selection does not beat random pairs, the selection is noise.

  carry    Cross-sectional funding carry, beta-neutral. Short the highest-funding perps,
           long the lowest, legs scaled so the book has zero trailing BTC beta. Control:
           the same book ranked on SHUFFLED funding (keeps turnover and hedging, removes
           the signal).

  xcarry   Correlated-pair carry ("carry over correlated movement"). Among pairs whose
           trailing return correlation is high, take the ones with the widest funding
           spread: short the high-funding leg, long the low-funding leg, hedge ratio from
           trailing returns, so the correlation cancels most of the price move and the
           funding differential is what remains. Same shuffled-funding control.

Accounting (identical for all three):
  * signals use data up to bar t close; positions change at bar t+DELAY close (default 1)
  * cost per side = fee 0.055% + slippage 0.05% on |traded notional|   (Simulator.cs
    FeeRoundTripPct = 0.11 and Config.SlippageBps = 10 round trip, split per side)
  * funding: real per-symbol rates at their actual settlement timestamps; long pays a
    positive rate. A symbol with no funding file is charged the repo's floor
    (FundingRateSession.FallbackIntervalPct = 0.01% per 8h tick) in BOTH directions —
    never booked as income.
  * P&L is additive on a unit of capital (no compounding), so returns are directly
    comparable across periods. Positions drift with price between rebalances.

Data:
  candle_cache/{SYM}_15m.csv (the C# cache, resampled to 1h) or candle_cache/{SYM}_60m.csv
  candle_cache/{SYM}_funding.csv   (ms,rate — the C# funding cache format)
  --fetch fills missing 60m/funding files from Bybit's public REST API (no keys).

Usage:
  python3 scripts/market_neutral_research.py all                    # BacktestCoins
  python3 scripts/market_neutral_research.py pairs --universe oos   # never-trained coins
  python3 scripts/market_neutral_research.py all --fetch            # download first
  python3 scripts/market_neutral_research.py selftest               # synthetic sanity checks
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass, field

import numpy as np
import pandas as pd

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# ── Cost constants (mirrors of the C# authorities; keep in sync) ──────────────────────
FEE_ROUND_TRIP_PCT = 0.11            # src/core/Simulator.cs  TradeCosts.FeeRoundTripPct
SLIPPAGE_BPS_ROUND_TRIP = 10.0       # src/core/Config.cs     Config.SlippageBps
FUNDING_FLOOR_PCT_PER_8H = 0.01      # src/core/FundingRateSession.cs FallbackIntervalPct
COST_PER_SIDE = (FEE_ROUND_TRIP_PCT + SLIPPAGE_BPS_ROUND_TRIP / 100.0) / 2.0 / 100.0  # fraction

HOURS_PER_YEAR = 24 * 365


# ═══════════════════════════════════════════════════════════════════════════════════
# Data
# ═══════════════════════════════════════════════════════════════════════════════════

def config_symbols(name: str) -> list[str]:
    """Parse a string[] from src/core/Config.cs so the symbol lists have one authority."""
    src = open(os.path.join(REPO, "src", "core", "Config.cs"), encoding="utf-8").read()
    m = re.search(rf"string\[\]\s+{name}\s*=\s*\[(.*?)\];", src, re.S)
    if not m:
        raise SystemExit(f"Config.{name} not found in Config.cs")
    body = re.sub(r"//[^\n]*", "", m.group(1))
    return re.findall(r'"([A-Z0-9]+)"', body)


def _read_ms_csv(path: str, ncols: int) -> pd.DataFrame | None:
    if not os.path.exists(path):
        return None
    df = pd.read_csv(path, header=None, usecols=range(ncols), on_bad_lines="skip")
    df = df[pd.to_numeric(df[0], errors="coerce").notna()].astype(float)
    if df.empty:
        return None
    df.index = pd.to_datetime(df[0].astype("int64"), unit="ms")
    return df.drop(columns=[0]).sort_index()


def load_h1(sym: str, cache: str) -> pd.DataFrame | None:
    """Hourly close + quote volume. Prefers the C# 15m cache, falls back to a 60m file."""
    for fname, freq in ((f"{sym}_15m.csv", "15min"), (f"{sym}_60m.csv", "60min")):
        df = _read_ms_csv(os.path.join(cache, fname), 6)
        if df is None:
            continue
        df.columns = ["o", "h", "l", "c", "v"]
        df = df[~df.index.duplicated(keep="last")]
        qv = df["c"] * df["v"]
        if freq == "15min":
            # Hour bar is labelled by its START (Bybit convention); require all 4 quarters.
            g = df["c"].resample("1h")
            out = pd.DataFrame({"close": g.last(), "qvol": qv.resample("1h").sum(),
                                "n": g.count()})
            out = out[out["n"] == 4].drop(columns="n")
        else:
            out = pd.DataFrame({"close": df["c"], "qvol": qv})
        return out
    return None


def load_funding(sym: str, cache: str) -> pd.Series | None:
    df = _read_ms_csv(os.path.join(cache, f"{sym}_funding.csv"), 2)
    if df is None:
        return None
    s = df[1]
    return s[~s.index.duplicated(keep="last")]


def _get_json(url: str, retries: int = 4) -> dict:
    for i in range(retries):
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                return json.loads(r.read())
        except Exception as e:  # network errors: backoff and retry
            if i == retries - 1:
                raise
            time.sleep(2 ** (i + 1))
    raise RuntimeError("unreachable")


def fetch_bybit(sym: str, cache: str, years: float = 4.0) -> None:
    """Fill candle_cache/{sym}_60m.csv and {sym}_funding.csv from Bybit public REST."""
    os.makedirs(cache, exist_ok=True)
    base = "https://api.bybit.com/v5/market"
    start_ms = int((time.time() - years * 365 * 86400) * 1000)

    kpath = os.path.join(cache, f"{sym}_60m.csv")
    if not os.path.exists(kpath) and not os.path.exists(os.path.join(cache, f"{sym}_15m.csv")):
        rows, end = {}, None
        while True:
            q = {"category": "linear", "symbol": sym, "interval": "60", "limit": 1000}
            if end:
                q["end"] = end
            lst = _get_json(f"{base}/kline?{urllib.parse.urlencode(q)}").get("result", {}).get("list") or []
            if not lst:
                break
            for k in lst:
                rows[int(k[0])] = k[:6]
            oldest = min(int(k[0]) for k in lst)
            if oldest <= start_ms or len(lst) < 1000:
                break
            end = oldest - 1
            time.sleep(0.15)
        if rows:
            with open(kpath, "w") as f:
                for ms in sorted(rows):
                    f.write(",".join(str(x) for x in rows[ms]) + "\n")

    fpath = os.path.join(cache, f"{sym}_funding.csv")
    if not os.path.exists(fpath):
        rows, end = {}, None
        while True:
            q = {"category": "linear", "symbol": sym, "limit": 200}
            if end:
                q["endTime"] = end
            lst = _get_json(f"{base}/funding/history?{urllib.parse.urlencode(q)}").get("result", {}).get("list") or []
            if not lst:
                break
            for x in lst:
                rows[int(x["fundingRateTimestamp"])] = x["fundingRate"]
            oldest = min(int(x["fundingRateTimestamp"]) for x in lst)
            if oldest <= start_ms or len(lst) < 200:
                break
            end = oldest - 1
            time.sleep(0.15)
        if rows:
            with open(fpath, "w") as f:
                for ms in sorted(rows):
                    f.write(f"{ms},{rows[ms]}\n")


@dataclass
class Market:
    close: pd.DataFrame            # hour x sym, NaN where no bar
    qvol: pd.DataFrame             # hour x sym, quote volume
    funding: pd.DataFrame          # hour x sym, rate at settlement hours else 0
    funding_known: pd.Series       # sym -> has real funding data
    funding_mask: pd.DataFrame     # hour x sym, True at a real settlement hour
    missing: list[str] = field(default_factory=list)


def build_market(symbols: list[str], cache: str) -> Market:
    closes, vols, funds, missing = {}, {}, {}, []
    for s in symbols:
        h = load_h1(s, cache)
        if h is None or len(h) < 24 * 60:
            missing.append(s)
            continue
        closes[s], vols[s] = h["close"], h["qvol"]
        f = load_funding(s, cache)
        if f is not None:
            funds[s] = f
    if not closes:
        raise SystemExit(f"No candle data found in {cache}/ — run with --fetch, or run the C# "
                         f"backtests once to populate the cache.")
    close = pd.DataFrame(closes).sort_index()
    idx = pd.date_range(close.index.min(), close.index.max(), freq="1h")
    close = close.reindex(idx)
    qvol = pd.DataFrame(vols).reindex(idx)
    funding = pd.DataFrame(0.0, index=idx, columns=close.columns)
    mask = pd.DataFrame(False, index=idx, columns=close.columns)
    for s, f in funds.items():
        f = f.copy()
        f.index = f.index.floor("1h")
        f = f[~f.index.duplicated(keep="last")]
        f = f[(f.index >= idx[0]) & (f.index <= idx[-1])]
        funding.loc[f.index, s] = f.values
        mask.loc[f.index, s] = True
    known = pd.Series({s: s in funds for s in close.columns})
    return Market(close, qvol, funding, known, mask, missing)


# ═══════════════════════════════════════════════════════════════════════════════════
# Engine — one accounting path for every book
# ═══════════════════════════════════════════════════════════════════════════════════

@dataclass
class BookResult:
    price: np.ndarray      # per-hour P&L components, fraction of book capital
    funding: np.ndarray
    cost: np.ndarray
    turnover: float        # total |traded notional| / capital
    index: pd.DatetimeIndex

    @property
    def net(self) -> np.ndarray:
        return self.price + self.funding + self.cost


def run_book(close: np.ndarray, funding: np.ndarray, fmask: np.ndarray, fknown: np.ndarray,
             targets: dict[int, np.ndarray], index: pd.DatetimeIndex,
             cost_per_side: float = COST_PER_SIDE) -> BookResult:
    """
    Simulate signed notional holdings (fraction of capital) against hourly closes.

    targets[t] = desired holdings decided at bar t; it is EXECUTED at bar t (caller applies
    the signal delay when building targets). Execution happens at the bar-t close, after
    that bar's return and funding accrue to the previous holdings.
    """
    n, m = close.shape
    ret = np.zeros_like(close)
    with np.errstate(invalid="ignore", divide="ignore"):
        ret[1:] = close[1:] / close[:-1] - 1.0
    ret = np.nan_to_num(ret, nan=0.0, posinf=0.0, neginf=0.0)
    hour = index.hour.values
    floor_tick = (hour % 8 == 0)
    floor = FUNDING_FLOOR_PCT_PER_8H / 100.0

    h = np.zeros(m)
    price = np.zeros(n); fund = np.zeros(n); cost = np.zeros(n)
    turnover = 0.0
    for t in range(n):
        if t > 0 and h.any():
            price[t] = h @ ret[t]
            # real funding: long pays +rate; unknown symbol: floor cost both directions
            fund[t] = -(h[fknown] @ funding[t, fknown])
            if floor_tick[t]:
                fund[t] -= np.abs(h[~fknown]).sum() * floor
            h = h * (1.0 + ret[t])
        tgt = targets.get(t)
        if tgt is not None:
            # a symbol whose price is missing at t cannot be traded: keep what we have
            tradable = ~np.isnan(close[t])
            new = np.where(tradable, tgt, h)
            traded = np.abs(new - h).sum()
            cost[t] = -traded * cost_per_side
            turnover += traded
            h = new
    return BookResult(price, fund, cost, turnover, index)


# ═══════════════════════════════════════════════════════════════════════════════════
# Statistics
# ═══════════════════════════════════════════════════════════════════════════════════

def adf_t(y: np.ndarray, lags: int = 1) -> tuple[float, float]:
    """ADF regression Δy = a + g·y[-1] + Σ d·Δy[-k]. Returns (t-stat of g, half-life hours)."""
    dy = np.diff(y)
    ylag = y[lags:-1]
    cols = [np.ones(len(ylag)), ylag]
    for k in range(1, lags + 1):
        cols.append(dy[lags - k:-k])
    X = np.column_stack(cols)
    Y = dy[lags:]
    beta, *_ = np.linalg.lstsq(X, Y, rcond=None)
    resid = Y - X @ beta
    dof = max(1, len(Y) - X.shape[1])
    s2 = resid @ resid / dof
    try:
        cov = s2 * np.linalg.inv(X.T @ X)
    except np.linalg.LinAlgError:
        return 0.0, math.inf
    g = beta[1]
    t = g / math.sqrt(cov[1, 1]) if cov[1, 1] > 0 else 0.0
    phi = 1.0 + g
    hl = -math.log(2) / math.log(phi) if 0 < phi < 1 else math.inf
    return t, hl


# Engle-Granger 2-variable critical values (MacKinnon 2010, constant, large n)
EG_CRIT_5PCT = -3.34


def summarize(name: str, r: BookResult, trades: list | None = None) -> dict:
    net = pd.Series(r.net, index=r.index)
    daily = net.resample("1D").sum()
    daily = daily[daily.index >= net[net != 0].index.min()] if (net != 0).any() else daily
    n_days = max(1, len(daily))
    years = n_days / 365.0
    ann = daily.sum() / years if years > 0 else 0.0
    vol = daily.std() * math.sqrt(365) if len(daily) > 1 else 0.0
    sharpe = ann / vol if vol > 0 else 0.0
    # weekly-block t-stat: daily P&L is autocorrelated through overlapping holds
    wk = net.resample("7D").sum()
    t_wk = wk.mean() / wk.std() * math.sqrt(len(wk)) if len(wk) > 2 and wk.std() > 0 else 0.0
    eq = net.cumsum()
    mdd = (eq - eq.cummax()).min()
    out = {
        "book": name,
        "ann_net_%": 100 * ann,
        "ann_price_%": 100 * r.price.sum() / years,
        "ann_funding_%": 100 * r.funding.sum() / years,
        "ann_cost_%": 100 * r.cost.sum() / years,
        "vol_%": 100 * vol,
        "sharpe": sharpe,
        "t_weekly": t_wk,
        "maxDD_%": 100 * mdd,
        "turnover_x_yr": r.turnover / years,
        "days": n_days,
    }
    if trades is not None:
        tr = np.array([x["net"] for x in trades]) if trades else np.array([])
        out["trades"] = len(tr)
        out["trade_mean_%"] = 100 * tr.mean() if len(tr) else float("nan")
        out["win_rate"] = (tr > 0).mean() if len(tr) else float("nan")
        pos, neg = tr[tr > 0].sum(), -tr[tr < 0].sum()
        out["PF"] = pos / neg if neg > 0 else float("nan")
    return out


def by_year(r: BookResult) -> pd.Series:
    return (pd.Series(r.net, index=r.index).groupby(r.index.year).sum() * 100).round(2)


def print_table(rows: list[dict], title: str) -> None:
    print(f"\n{'═' * 4} {title} {'═' * max(4, 86 - len(title))}")
    df = pd.DataFrame(rows).set_index("book")
    with pd.option_context("display.width", 200, "display.max_columns", 30,
                           "display.float_format", lambda v: f"{v:,.2f}"):
        print(df.to_string())


# ═══════════════════════════════════════════════════════════════════════════════════
# Helpers shared by the books
# ═══════════════════════════════════════════════════════════════════════════════════

def liquid_universe(mk: Market, t0: int, t1: int, top: int, min_cov: float = 0.95) -> list[int]:
    """Columns with >= min_cov bar coverage in [t0,t1) and a price at t1-1, ranked by median
    quote volume over the window. Point-in-time: uses nothing after t1-1."""
    c = mk.close.values[t0:t1]
    cov = (~np.isnan(c)).mean(axis=0)
    alive = ~np.isnan(mk.close.values[t1 - 1])
    ok = np.where((cov >= min_cov) & alive)[0]
    med = np.nanmedian(mk.qvol.values[t0:t1, ok], axis=0)
    order = np.argsort(-np.nan_to_num(med, nan=-1))
    return list(ok[order[:top]])


def log_returns(mk: Market, t0: int, t1: int, cols: list[int]) -> np.ndarray:
    p = pd.DataFrame(mk.close.values[t0:t1, cols]).ffill().values
    with np.errstate(invalid="ignore", divide="ignore"):
        lr = np.diff(np.log(p), axis=0)
    return np.nan_to_num(lr, nan=0.0)


# ═══════════════════════════════════════════════════════════════════════════════════
# Book 1 — pairs stat-arb
# ═══════════════════════════════════════════════════════════════════════════════════

@dataclass
class PairsParams:
    formation_h: int = 24 * 90
    trading_h: int = 24 * 30
    universe_top: int = 40
    min_corr: float = 0.60
    max_pairs: int = 10
    max_per_coin: int = 2
    hl_min: float = 6.0
    hl_max: float = 24 * 10.0
    entry_z: float = 2.0
    exit_z: float = 0.0
    stop_z: float = 4.0
    delay: int = 1


def select_pairs(mk: Market, t0: int, t1: int, p: PairsParams, rng: np.random.Generator | None):
    """Return (pairs, n_tested). Each pair = (colA, colB, beta, halflife).
    rng != None → RANDOM control: draw pairs uniformly from the same universe, same count."""
    uni = liquid_universe(mk, t0, t1, p.universe_top)
    if len(uni) < 4:
        return [], 0
    logp = np.log(pd.DataFrame(mk.close.values[t0:t1, uni]).ffill().bfill().values)
    lr = np.diff(logp, axis=0)
    corr = np.corrcoef(lr.T)
    cands, tested = [], 0
    for i in range(len(uni)):
        for j in range(i + 1, len(uni)):
            if rng is None and corr[i, j] < p.min_corr:
                continue
            y, x = logp[:, i], logp[:, j]
            beta = np.polyfit(x, y, 1)[0]
            if not (0.2 < beta < 5.0):
                continue
            tested += 1
            t, hl = adf_t(y - beta * x)
            cands.append((t, uni[i], uni[j], beta, hl))
    if rng is not None:
        rng.shuffle(cands)
        pool = [(t, a, b, be, (hl if math.isfinite(hl) else 72.0)) for t, a, b, be, hl in cands]
    else:
        pool = sorted([c for c in cands if c[0] < EG_CRIT_5PCT and p.hl_min <= c[4] <= p.hl_max])
    chosen, used = [], {}
    for t, a, b, beta, hl in pool:
        if used.get(a, 0) >= p.max_per_coin or used.get(b, 0) >= p.max_per_coin:
            continue
        chosen.append((a, b, beta, min(max(hl, p.hl_min), p.hl_max)))
        used[a] = used.get(a, 0) + 1
        used[b] = used.get(b, 0) + 1
        if len(chosen) >= p.max_pairs:
            break
    return chosen, tested


def trade_pair(mk: Market, a: int, b: int, beta: float, hl: float, t0: int, t1: int, t2: int,
               p: PairsParams):
    """Trade one pair over [t1, t2) using formation [t0, t1) for the frozen hedge ratio.
    Returns (BookResult over [t1,t2), trades)."""
    lookback = int(np.clip(4 * hl, 24, p.formation_h))
    max_hold = int(max(24, 3 * hl))
    cols = [a, b]
    lo = max(0, t1 - lookback)
    px = pd.DataFrame(mk.close.values[lo:t2, cols]).ffill()
    spread = np.log(px[0]) - beta * np.log(px[1])
    mu = spread.rolling(lookback, min_periods=lookback // 2).mean()
    sd = spread.rolling(lookback, min_periods=lookback // 2).std()
    z = ((spread - mu) / sd).values[t1 - lo:]
    alive_a = ~np.isnan(mk.close.values[t1:t2, a])
    alive_b = ~np.isnan(mk.close.values[t1:t2, b])

    n = t2 - t1
    wa, wb = 1.0 / (1.0 + beta), beta / (1.0 + beta)
    targets: dict[int, np.ndarray] = {}
    trades, pos, entry_bar, cooldown = [], 0, -1, False
    decisions = []  # (decided_at, new_pos)
    for k in range(n):
        zk = z[k]
        last = k >= n - 1 - p.delay
        dead = not (alive_a[k] and alive_b[k])
        if pos == 0:
            if cooldown and (np.isnan(zk) or abs(zk) < p.entry_z):
                cooldown = False
            if not last and not dead and not cooldown and not np.isnan(zk):
                if zk > p.entry_z:
                    pos, entry_bar = -1, k
                    decisions.append((k, -1))
                elif zk < -p.entry_z:
                    pos, entry_bar = 1, k
                    decisions.append((k, 1))
        else:
            exit_now = last or dead or np.isnan(zk) or (k - entry_bar) >= max_hold
            if not exit_now:
                if (pos == -1 and zk <= p.exit_z) or (pos == 1 and zk >= -p.exit_z):
                    exit_now = True
                elif abs(zk) >= p.stop_z:
                    exit_now, cooldown = True, True
            if exit_now:
                pos = 0
                decisions.append((k, 0))
    exec_pairs = []
    for k, newpos in decisions:
        e = min(n - 1, k + p.delay)
        targets[e] = np.array([newpos * wa, -newpos * wb])
        exec_pairs.append((e, newpos))
    r = run_book(mk.close.values[t1:t2, cols], mk.funding.values[t1:t2, cols],
                 mk.funding_mask.values[t1:t2, cols], mk.funding_known.values[cols],
                 targets, mk.close.index[t1:t2])
    net = r.net
    for (e_in, pin), (e_out, pout) in zip(exec_pairs[0::2], exec_pairs[1::2]):
        trades.append({"entry": mk.close.index[t1 + e_in], "exit": mk.close.index[t1 + e_out],
                       "a": mk.close.columns[a], "b": mk.close.columns[b], "dir": pin,
                       "net": net[e_in:e_out + 1].sum()})
    return r, trades


def run_pairs(mk: Market, p: PairsParams, rng: np.random.Generator | None = None,
              quiet: bool = False):
    n = len(mk.close)
    price = np.zeros(n); fund = np.zeros(n); cost = np.zeros(n)
    turnover, trades, windows = 0.0, [], []
    t1 = p.formation_h
    while t1 + 24 * 7 <= n:
        t0, t2 = t1 - p.formation_h, min(n, t1 + p.trading_h)
        pairs, tested = select_pairs(mk, t0, t1, p, rng)
        slot = 1.0 / p.max_pairs            # capital per slot; empty slots sit in cash
        for a, b, beta, hl in pairs:
            r, tr = trade_pair(mk, a, b, beta, hl, t0, t1, t2, p)
            price[t1:t2] += slot * r.price
            fund[t1:t2] += slot * r.funding
            cost[t1:t2] += slot * r.cost
            turnover += slot * r.turnover
            trades += tr                    # per-trade stats stay on the pair's own capital
        windows.append((mk.close.index[t1].date(), len(pairs), tested))
        t1 = t2
    start = p.formation_h
    res = BookResult(price[start:], fund[start:], cost[start:], turnover, mk.close.index[start:])
    if not quiet and rng is None:
        sel = [w[1] for w in windows]
        tested = [w[2] for w in windows]
        print(f"  windows={len(windows)}  pairs/window: mean {np.mean(sel):.1f}  "
              f"(tested per window: mean {np.mean(tested):.0f}; at 5% the expected false "
              f"positives alone are ~{0.05 * np.mean(tested):.1f})")
    return res, trades


# ═══════════════════════════════════════════════════════════════════════════════════
# Book 2 — cross-sectional funding carry, beta-neutral
# ═══════════════════════════════════════════════════════════════════════════════════

@dataclass
class CarryParams:
    warmup_h: int = 24 * 30
    rebalance_h: int = 24
    signal_h: int = 24 * 7          # funding averaged over this trailing window
    beta_h: int = 24 * 30
    universe_top: int = 40
    quantile: float = 0.2
    delay: int = 1
    btc: str = "BTCUSDT"


def trailing_funding(mk: Market, t: int, h: int, cols: list[int]) -> np.ndarray:
    """Mean real funding per settlement over (t-h, t]; NaN when no real settlements."""
    f = mk.funding.values[t - h + 1:t + 1, cols]
    msk = mk.funding_mask.values[t - h + 1:t + 1, cols]
    cnt = msk.sum(axis=0)
    with np.errstate(invalid="ignore", divide="ignore"):
        return np.where(cnt > 0, (f * msk).sum(axis=0) / cnt, np.nan)


def signal_source(mk: Market, shuffle_rng: np.random.Generator | None) -> np.ndarray:
    """Column whose funding history is used as each column's SIGNAL. Identity for the real
    book. For the control, one fixed permutation among funded symbols for the whole run:
    the signal keeps its real persistence (so turnover and hedging are comparable) but is
    no longer attached to the funding the position actually receives."""
    src = np.arange(mk.close.shape[1])
    if shuffle_rng is not None:
        funded = np.where(mk.funding_known.values)[0]
        src[funded] = shuffle_rng.permutation(funded)
    return src


def run_carry(mk: Market, p: CarryParams, shuffle_rng: np.random.Generator | None = None):
    n, m = mk.close.shape
    if p.btc not in mk.close.columns:
        raise SystemExit(f"{p.btc} missing — carry needs it for beta hedging")
    btc = list(mk.close.columns).index(p.btc)
    targets: dict[int, np.ndarray] = {}
    start = max(p.warmup_h, p.beta_h, p.signal_h)
    legs = []
    src = signal_source(mk, shuffle_rng)
    for t in range(start, n - p.delay, p.rebalance_h):
        uni = [c for c in liquid_universe(mk, t - p.beta_h, t + 1, p.universe_top)
               if mk.funding_known.values[c] and c != btc]
        if len(uni) < 10:
            continue
        sig = trailing_funding(mk, t, p.signal_h, [src[c] for c in uni])
        ok = ~np.isnan(sig)
        uni = [u for u, o in zip(uni, ok) if o]
        sig = sig[ok]
        if len(uni) < 10:
            continue
        lr = log_returns(mk, t - p.beta_h, t + 1, uni + [btc])
        x = lr[:, -1]
        betas = np.array([np.cov(lr[:, i], x)[0, 1] / x.var() for i in range(len(uni))])
        betas = np.clip(betas, 0.2, 3.0)
        k = max(2, int(len(uni) * p.quantile))
        order = np.argsort(sig)
        longs, shorts = order[:k], order[-k:]
        bl, bs = betas[longs].mean(), betas[shorts].mean()
        L = bs / (bl + bs)               # L·bl = S·bs, L+S = 1 → zero trailing BTC beta
        S = 1.0 - L
        w = np.zeros(m)
        for i in longs:
            w[uni[i]] += L / k
        for i in shorts:
            w[uni[i]] -= S / k
        targets[t + p.delay] = w
        legs.append((sig[shorts].mean() - sig[longs].mean()) * 100)
    r = run_book(mk.close.values, mk.funding.values, mk.funding_mask.values,
                 mk.funding_known.values, targets, mk.close.index)
    return _trim(r, start), legs


def _trim(r: BookResult, start: int) -> BookResult:
    return BookResult(r.price[start:], r.funding[start:], r.cost[start:], r.turnover, r.index[start:])


# ═══════════════════════════════════════════════════════════════════════════════════
# Book 3 — correlated-pair carry
# ═══════════════════════════════════════════════════════════════════════════════════

@dataclass
class XCarryParams:
    warmup_h: int = 24 * 30
    rebalance_h: int = 24
    signal_h: int = 24 * 7
    corr_h: int = 24 * 30
    universe_top: int = 40
    min_corr: float = 0.80
    min_spread_pct: float = 0.01    # funding differential per settlement, % (≈11%/yr at 8h)
    keep_spread_pct: float = 0.005  # hysteresis: an open pair survives down to this
    max_pairs: int = 10
    max_per_coin: int = 2
    delay: int = 1


def run_xcarry(mk: Market, p: XCarryParams, shuffle_rng: np.random.Generator | None = None):
    n, m = mk.close.shape
    targets: dict[int, np.ndarray] = {}
    start = max(p.warmup_h, p.corr_h, p.signal_h)
    open_pairs: dict[tuple[int, int], float] = {}   # (short_col, long_col) -> hedge ratio
    spreads = []
    src = signal_source(mk, shuffle_rng)
    for t in range(start, n - p.delay, p.rebalance_h):
        uni = [c for c in liquid_universe(mk, t - p.corr_h, t + 1, p.universe_top)
               if mk.funding_known.values[c]]
        if len(uni) < 4:
            continue
        sig = trailing_funding(mk, t, p.signal_h, [src[c] for c in uni]) * 100
        pos = {c: i for i, c in enumerate(uni)}
        lr = log_returns(mk, t - p.corr_h, t + 1, uni)
        corr = np.corrcoef(lr.T)
        var = lr.var(axis=0)

        def score(i: int, j: int) -> float:  # funding spread short i / long j
            return sig[i] - sig[j] if not (np.isnan(sig[i]) or np.isnan(sig[j])) else -np.inf

        kept: dict[tuple[int, int], float] = {}
        for (cs, cl), hr in open_pairs.items():
            if cs in pos and cl in pos:
                i, j = pos[cs], pos[cl]
                if corr[i, j] >= p.min_corr - 0.1 and score(i, j) >= p.keep_spread_pct:
                    kept[(cs, cl)] = hr
        cands = []
        for i in range(len(uni)):
            for j in range(len(uni)):
                if i == j or corr[i, j] < p.min_corr:
                    continue
                s = score(i, j)
                if s >= p.min_spread_pct:
                    cands.append((s, uni[i], uni[j], i, j))
        cands.sort(reverse=True)
        used: dict[int, int] = {}
        for cs, cl in kept:
            used[cs] = used.get(cs, 0) + 1
            used[cl] = used.get(cl, 0) + 1
        for s, cs, cl, i, j in cands:
            if len(kept) >= p.max_pairs:
                break
            if (cs, cl) in kept or used.get(cs, 0) >= p.max_per_coin or used.get(cl, 0) >= p.max_per_coin:
                continue
            # hedge: units of long leg per unit short leg that minimise variance
            hr = float(np.clip(np.cov(lr[:, i], lr[:, j])[0, 1] / var[j], 0.3, 3.0))
            kept[(cs, cl)] = hr
            used[cs] = used.get(cs, 0) + 1
            used[cl] = used.get(cl, 0) + 1
        open_pairs = kept
        w = np.zeros(m)
        slot = 1.0 / p.max_pairs
        for (cs, cl), hr in open_pairs.items():
            w[cs] -= slot / (1 + hr)
            w[cl] += slot * hr / (1 + hr)
            spreads.append(score(pos[cs], pos[cl]))
        targets[t + p.delay] = w
    r = run_book(mk.close.values, mk.funding.values, mk.funding_mask.values,
                 mk.funding_known.values, targets, mk.close.index)
    return _trim(r, start), spreads


# ═══════════════════════════════════════════════════════════════════════════════════
# Reports
# ═══════════════════════════════════════════════════════════════════════════════════

def report_pairs(mk: Market, n_controls: int, seed: int) -> list[dict]:
    p = PairsParams()
    print("\n── PAIRS (cointegration, walk-forward: select on 90d, trade next 30d) ──")
    rows = []
    r, trades = run_pairs(mk, p)
    rows.append(summarize("pairs: cointegrated", r, trades))
    ctrl = []
    for s in range(n_controls):
        rc, tc = run_pairs(mk, p, rng=np.random.default_rng(seed + s), quiet=True)
        ctrl.append(summarize(f"random#{s}", rc, tc))
    if ctrl:
        c = pd.DataFrame(ctrl)
        row = {"book": f"pairs: RANDOM control (median of {n_controls})"}
        row.update(c.drop(columns="book").median().to_dict())
        rows.append(row)
        pct = (c["sharpe"] < rows[0]["sharpe"]).mean()
        print(f"  selected-pairs Sharpe beats {pct:.0%} of random-pair controls "
              f"(random Sharpe p10/p50/p90 = {c.sharpe.quantile(.1):.2f}/{c.sharpe.median():.2f}/"
              f"{c.sharpe.quantile(.9):.2f})")
    # gross (zero-cost) view: is there anything before costs?
    g = run_book_gross_view(r)
    rows.append(summarize("pairs: cointegrated, GROSS of cost", g, None))
    # small fixed sensitivity grid, printed in full so no cell can be cherry-picked
    for ez in (1.5, 2.5):
        for fm in (24 * 60, 24 * 180):
            pp = PairsParams(entry_z=ez, formation_h=fm)
            rr, tt = run_pairs(mk, pp, quiet=True)
            rows.append(summarize(f"  grid entry_z={ez} form={fm // 24}d", rr, tt))
    print_table(rows, "PAIRS")
    print("  net P&L by year (% of capital):", by_year(r).to_dict())
    if trades:
        td = pd.DataFrame(trades)
        top = td.assign(pair=td.a + "/" + td.b).groupby("pair").net.agg(["size", "sum"])
        print("  most-traded pairs:", top.sort_values("size", ascending=False).head(8)
              .assign(sum=lambda d: (100 * d["sum"]).round(2)).to_dict("index"))
    return rows


def run_book_gross_view(r: BookResult) -> BookResult:
    return BookResult(r.price, r.funding, np.zeros_like(r.cost), r.turnover, r.index)


def report_carry(mk: Market, n_controls: int, seed: int) -> list[dict]:
    print("\n── CARRY (cross-sectional funding, beta-neutral, daily rebalance) ──")
    known = int(mk.funding_known.sum())
    print(f"  symbols with real funding data: {known}/{len(mk.funding_known)}")
    rows = []
    r, legs = run_carry(mk, CarryParams())
    if legs:
        print(f"  mean funding spread short-leg minus long-leg: {np.mean(legs):.4f}%/settlement")
    rows.append(summarize("carry: beta-neutral", r))
    ctrl = [summarize(f"shuffle#{s}", run_carry(mk, CarryParams(),
                      shuffle_rng=np.random.default_rng(seed + s))[0]) for s in range(n_controls)]
    if ctrl:
        c = pd.DataFrame(ctrl)
        row = {"book": f"carry: SHUFFLED-funding control (median of {n_controls})"}
        row.update(c.drop(columns="book").median().to_dict())
        rows.append(row)
    for rb in (8, 72):
        for q in (0.1, 0.3):
            rr, _ = run_carry(mk, CarryParams(rebalance_h=rb, quantile=q))
            rows.append(summarize(f"  grid rebalance={rb}h q={q}", rr))
    print_table(rows, "CARRY")
    print("  net P&L by year (% of capital):", by_year(r).to_dict())
    return rows


def report_xcarry(mk: Market, n_controls: int, seed: int) -> list[dict]:
    print("\n── XCARRY (carry the funding spread across highly correlated pairs) ──")
    rows = []
    r, spreads = run_xcarry(mk, XCarryParams())
    if spreads:
        print(f"  mean entry funding spread held: {np.mean(spreads):.4f}%/settlement "
              f"(pair-days held: {len(spreads)})")
    rows.append(summarize("xcarry: correlated pairs", r))
    ctrl = [summarize(f"shuffle#{s}", run_xcarry(mk, XCarryParams(),
                      shuffle_rng=np.random.default_rng(seed + s))[0]) for s in range(n_controls)]
    if ctrl:
        c = pd.DataFrame(ctrl)
        row = {"book": f"xcarry: SHUFFLED-funding control (median of {n_controls})"}
        row.update(c.drop(columns="book").median().to_dict())
        rows.append(row)
    for mc in (0.7, 0.9):
        for ms in (0.005, 0.02):
            rr, _ = run_xcarry(mk, XCarryParams(min_corr=mc, min_spread_pct=ms,
                                                keep_spread_pct=ms / 2))
            rows.append(summarize(f"  grid corr>={mc} spread>={ms}%", rr))
    print_table(rows, "XCARRY")
    print("  net P&L by year (% of capital):", by_year(r).to_dict())
    return rows


# ═══════════════════════════════════════════════════════════════════════════════════
# Synthetic self-test — proves the machinery can find an edge that exists and does not
# invent one that doesn't. Run this before trusting a real-data result.
# ═══════════════════════════════════════════════════════════════════════════════════

def synthetic_market(seed: int, n_coins: int = 30, days: int = 540, coint_pairs: int = 5,
                     funding_edge: bool = True) -> Market:
    rng = np.random.default_rng(seed)
    n = days * 24
    idx = pd.date_range("2022-01-01", periods=n, freq="1h")
    mkt = np.cumsum(rng.normal(0, 0.006, n))
    cols, logp = [], []
    cols.append("BTCUSDT"); logp.append(mkt + np.cumsum(rng.normal(0, 0.002, n)))
    for i in range(1, n_coins):
        beta = rng.uniform(0.8, 1.6)
        logp.append(beta * mkt + np.cumsum(rng.normal(0, 0.008, n)) + rng.uniform(-2, 2))
        cols.append(f"C{i:02d}USDT")
    # cointegrated partners: B = A·β + OU(half-life ~24h)
    for k in range(coint_pairs):
        a = 1 + k
        ou = np.zeros(n)
        phi = 0.5 ** (1 / 24)
        for t in range(1, n):
            ou[t] = phi * ou[t - 1] + rng.normal(0, 0.006)
        logp.append(logp[a] * 1.0 + ou + 0.3)
        cols.append(f"P{k:02d}USDT")
    close = pd.DataFrame(np.exp(np.array(logp).T), index=idx, columns=cols)
    qvol = pd.DataFrame(1e6, index=idx, columns=cols)
    funding = pd.DataFrame(0.0, index=idx, columns=cols)
    mask = pd.DataFrame(False, index=idx, columns=cols)
    settle = idx.hour % 8 == 0
    for c in cols:
        # persistent per-coin funding level (AR(1) on settlements) — pure carry, independent
        # of price, so a hedged carry book should capture it and a shuffled one should not
        # without the edge: same level for every coin plus i.i.d. noise — nothing persistent
        # for a trailing average to rank on
        ns = settle.sum()
        if funding_edge:
            lvl = rng.normal(0.0001, 0.0002)
            f = np.zeros(ns)
            for t in range(1, ns):
                f[t] = 0.97 * f[t - 1] + 0.03 * lvl + rng.normal(0, 0.00003)
        else:
            f = 0.0001 + rng.normal(0, 0.0001, ns)
        funding.loc[settle, c] = f
        mask.loc[settle, c] = True
    return Market(close, qvol, funding, pd.Series(True, index=cols), mask)


def selftest() -> int:
    fails = 0

    def check(cond: bool, msg: str):
        nonlocal fails
        print(f"  [{'PASS' if cond else 'FAIL'}] {msg}")
        fails += 0 if cond else 1

    print("selftest: ADF on white noise vs random walk")
    rng = np.random.default_rng(1)
    t_noise, hl_noise = adf_t(rng.normal(size=2000))
    t_rw, _ = adf_t(np.cumsum(rng.normal(size=2000)))
    check(t_noise < -10, f"stationary series rejects unit root (t={t_noise:.1f})")
    check(t_rw > EG_CRIT_5PCT, f"random walk does not (t={t_rw:.2f})")

    print("selftest: engine accounting")
    idx = pd.date_range("2024-01-01", periods=5, freq="1h")
    close = np.array([[100.0], [110.0], [110.0], [121.0], [121.0]])
    fund = np.zeros((5, 1)); fund[2, 0] = 0.001
    r = run_book(close, fund, fund != 0, np.array([True]), {0: np.array([1.0]), 3: np.array([0.0])},
                 idx, cost_per_side=0.001)
    check(abs(r.price.sum() - (0.10 + 0.11)) < 1e-12, f"price P&L drifts with holdings ({r.price.sum():.4f})")
    check(abs(r.funding.sum() + 0.0011) < 1e-12, f"long pays positive funding on drifted notional ({r.funding.sum():.5f})")
    check(abs(r.cost.sum() + 0.001 * (1 + 1.21)) < 1e-12, f"cost on traded notional ({r.cost.sum():.5f})")

    print("selftest: pairs finds planted cointegration, random control does not")
    mk = synthetic_market(7, funding_edge=False)
    r, tr = run_pairs(mk, PairsParams(), quiet=True)
    s = summarize("x", r, tr)
    rc, tc = run_pairs(mk, PairsParams(), rng=np.random.default_rng(3), quiet=True)
    sc = summarize("x", rc, tc)
    check(s["sharpe"] > 1.0, f"planted pairs Sharpe {s['sharpe']:.2f} > 1 (trades {s['trades']})")
    check(sc["sharpe"] < s["sharpe"] - 0.5, f"random control Sharpe {sc['sharpe']:.2f} well below")
    mk0 = synthetic_market(7, coint_pairs=0, funding_edge=False)
    r0, t0 = run_pairs(mk0, PairsParams(), quiet=True)
    s0 = summarize("x", r0, t0)
    check(s0["ann_net_%"] <= 0.5, f"no planted pairs → no net edge (ann {s0['ann_net_%']:.2f}%)")

    print("selftest: carry captures planted funding dispersion, shuffled control does not")
    mk = synthetic_market(11, coint_pairs=0, funding_edge=True)
    rc, _ = run_carry(mk, CarryParams())
    sc = summarize("x", rc)
    rs, _ = run_carry(mk, CarryParams(), shuffle_rng=np.random.default_rng(5))
    ss = summarize("x", rs)
    check(sc["ann_funding_%"] > 5, f"carry funding income {sc['ann_funding_%']:.1f}%/yr")
    check(ss["ann_funding_%"] < sc["ann_funding_%"] / 3,
          f"shuffled funding income {ss['ann_funding_%']:.1f}%/yr")
    mk0 = synthetic_market(11, coint_pairs=0, funding_edge=False)
    r0, _ = run_carry(mk0, CarryParams())
    s0 = summarize("x", r0)
    check(s0["ann_net_%"] < 1.0, f"no persistent funding dispersion → no net carry edge (ann {s0['ann_net_%']:.2f}%)")

    print("selftest: xcarry earns the spread on correlated pairs")
    mk = synthetic_market(13, coint_pairs=5, funding_edge=True)
    rx, _ = run_xcarry(mk, XCarryParams())
    sx = summarize("x", rx)
    check(sx["ann_funding_%"] > 0, f"xcarry funding income {sx['ann_funding_%']:.2f}%/yr")
    print(f"\nselftest: {'OK' if fails == 0 else f'{fails} FAILED'}")
    return 1 if fails else 0


# ═══════════════════════════════════════════════════════════════════════════════════

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("book", choices=["pairs", "carry", "xcarry", "all", "selftest"])
    ap.add_argument("--universe", choices=["backtest", "oos", "both"], default="backtest",
                    help="Config.BacktestCoins, Config.OosCoins, or both")
    ap.add_argument("--cache", default=os.path.join(REPO, "candle_cache"))
    ap.add_argument("--fetch", action="store_true", help="download missing data from Bybit")
    ap.add_argument("--controls", type=int, default=20, help="random/shuffled control runs")
    ap.add_argument("--seed", type=int, default=12345)
    ap.add_argument("--since", help="drop data before this date (YYYY-MM-DD)")
    a = ap.parse_args()

    if a.book == "selftest":
        return selftest()

    syms = {"backtest": config_symbols("BacktestCoins"), "oos": config_symbols("OosCoins"),
            "both": config_symbols("BacktestCoins") + config_symbols("OosCoins")}[a.universe]
    if "BTCUSDT" not in syms:
        syms = ["BTCUSDT"] + syms
    if a.fetch:
        for i, s in enumerate(syms):
            try:
                fetch_bybit(s, a.cache)
                print(f"\r  fetched {i + 1}/{len(syms)} {s:<16}", end="", flush=True)
            except Exception as e:
                print(f"\n  fetch {s} failed: {e}")
        print()
    mk = build_market(syms, a.cache)
    if a.since:
        keep = mk.close.index >= pd.Timestamp(a.since)
        mk = Market(mk.close[keep], mk.qvol[keep], mk.funding[keep], mk.funding_known,
                    mk.funding_mask[keep], mk.missing)
    print(f"Universe {a.universe}: {mk.close.shape[1]} symbols loaded, {len(mk.missing)} missing; "
          f"{mk.close.index[0]:%Y-%m-%d} → {mk.close.index[-1]:%Y-%m-%d}; "
          f"cost/side {COST_PER_SIDE * 100:.3f}%")
    if a.book in ("pairs", "all"):
        report_pairs(mk, a.controls, a.seed)
    if a.book in ("carry", "all"):
        report_carry(mk, a.controls, a.seed)
    if a.book in ("xcarry", "all"):
        report_xcarry(mk, a.controls, a.seed)
    print("\nRead: an edge needs (1) net > 0 with t_weekly ≳ 2, (2) a clear gap over its "
          "control row, and (3) survival on --universe oos. Grid rows are sensitivity, not "
          "candidates — do not pick the best one.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
