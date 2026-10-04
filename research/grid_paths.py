"""Phase 0 of docs/EDGE_DISCOVERY_SCOPE_2026-10.md: every grid fill, no exit, plus the thesis check.

Thesis (the user's): every strategy is a GATED GRID. It enters where a grid rung could have rested;
what separates it from Grid is setup detection (when the rung is out) and closing (when to leave).
So the unit of study is the price path AFTER a generic grid fill, not a trade with an exit.

Fill engine. At the close of every 1h bar t, for every coin, rest limits at close_t ∓ k·ATR14_t
(k = 1, 2, 3; long below, short above). Each order lives one bar.
  - It fills in bar t+1 only if price trades THROUGH by 5bp (never in bar t: the same-bar fix).
  - Fill price is min(open, level) for a long, max(open, level) for a short.
  - One row per rung reached (a 3-ATR bar fills k=1, 2 and 3): never label by the deepest rung,
    which would condition on the bar's full extreme.

Label. The signed log path r(h) = side·ln(C[fill+h−1] / px) at h hours (h=1 is the fill bar's
close). It is reported in % and as z = r / (σ·√h), where σ is the trailing 168h std of 1h log
returns at t. Under a random walk z has mean 0 at every h. The edge question is the DRIFT
m(h) − m(1): the move after the fill. The level m(1) mostly reflects the fill model, which is
deliberately pessimistic (THROUGH).

Nulls:
  - R0: market entries at random bars of the same coin, the same count;
  - shuffled: the same engine on bars permuted in time (no serial structure).

Block A only (fills before 2025-07-01). Block B is sealed and never read here.

Run:  python3 research/grid_paths.py --selftest
      python3 research/grid_paths.py --universe oos|backtest --trades <all_trades csv>
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "scripts"))
import market_neutral_research as mnr  # noqa: E402

KS = (1, 2, 3)
HS = (1, 2, 4, 8, 12, 24, 48, 72)
THROUGH = 0.0005                    # same as dip_dca_research: a limit fills only when traded through
SIG_N, ATR_N = 168, 14
SEAL = pd.Timestamp("2025-07-01")   # block B starts here: never read in Phase 0
COST_MT = (mnr.MAKER_COST_PER_SIDE + mnr.TAKER_COST_PER_SIDE) * 100   # maker in, taker out, % round trip
SIDE = {"FadeShort": -1, "GridShort": -1, "RipShort": -1,
        "Grid": 1, "DipLong": 1, "SwingLong": 1, "FadeLong": 1}


# ── indicators (matrices: time × coin; value at t uses bars ≤ t) ─────────────────────────
def atr(H, L, C, n=ATR_N):
    pc = np.vstack([np.full((1, C.shape[1]), np.nan), C[:-1]])
    tr = np.fmax(H - L, np.fmax(np.abs(H - pc), np.abs(L - pc)))
    return pd.DataFrame(tr).ewm(alpha=1 / n, adjust=False, min_periods=n).mean().values


def sigma(C, n=SIG_N):
    with np.errstate(invalid="ignore", divide="ignore"):
        r = np.diff(np.log(C), axis=0, prepend=np.nan)
    return pd.DataFrame(r).rolling(n, min_periods=n // 2).std().values


# ── fill engine + path labeller ──────────────────────────────────────────────────────────
def fills(O, H, L, C, A, S):
    """All one-bar grid-rung fills, ONE ROW PER RUNG reached: a bar that trades through 3 ATR fills
    the k=1, 2 and 3 rungs. It used to keep only the deepest rung per bar, so a "k=2 fill" meant
    "reached 2 ATR but NOT 3 ATR this hour", which conditions on the bar's full extreme. On real
    bars that selects hours that reversed, and it showed up as a fake +0.3-0.6% at k=2 under every
    fill model."""
    out = []
    for side in (1, -1):
        for k in KS:
            lvl = C[:-1] - side * k * A[:-1]
            if side == 1:
                hit = L[1:] <= lvl * (1 - THROUGH)
            else:
                hit = H[1:] >= lvl * (1 + THROUGH)
            t, j = np.nonzero(hit & (lvl > 0) & np.isfinite(S[:-1]))
            lv = lvl[t, j]
            o = O[t + 1, j]
            px = np.minimum(o, lv) if side == 1 else np.maximum(o, lv)
            out.append(dict(t=t, j=j, f=t + 1, side=np.full(len(t), side, np.int8), k=np.full(len(t), k, np.int8),
                            px=px, sig=S[t, j]))
    return {key: np.concatenate([d[key] for d in out]) for key in out[0]}


def label(fl, H, L, C, hs=HS):
    """r(h) in %, z(h), and MFE/MAE in % at each h, for every fill (NaN past the data's end)."""
    T = C.shape[0]
    f, j, side, px = fl["f"], fl["j"], fl["side"].astype(float), fl["px"]
    res = {}
    run_hi, run_lo = C[f, j].copy(), C[f, j].copy()             # fill bar: only its close is post-fill for sure
    for h in range(1, max(hs) + 1):
        idx = f + h - 1
        ok = idx < T
        ii = np.where(ok, idx, 0)
        c = np.where(ok, C[ii, j], np.nan)
        if h > 1:
            run_hi = np.fmax(run_hi, np.where(ok, H[ii, j], np.nan))
            run_lo = np.fmin(run_lo, np.where(ok, L[ii, j], np.nan))
        if h in hs:
            r = side * np.log(c / px)
            mfe = np.where(side > 0, np.log(run_hi / px), -np.log(run_lo / px))
            mae = np.where(side > 0, np.log(run_lo / px), -np.log(run_hi / px))
            res[f"r{h}"] = 100 * r
            res[f"z{h}"] = r / (fl["sig"] * math.sqrt(h))
            res[f"mfe{h}"], res[f"mae{h}"] = 100 * mfe, 100 * mae
    return res


def random_entries(fl, C, O, S, rng):
    """R0: same count per coin × side, market entry at the open of a random bar with data."""
    T, N = C.shape
    t_all, j_all, side_all = [], [], []
    for side in (1, -1):
        for j in range(N):
            n = int(((fl["j"] == j) & (fl["side"] == side)).sum())
            v = np.where(np.isfinite(C[:-1, j]) & np.isfinite(S[:-1, j]) & np.isfinite(O[1:, j]))[0]
            if n == 0 or len(v) == 0:
                continue
            t_all.append(rng.choice(v, n)); j_all.append(np.full(n, j)); side_all.append(np.full(n, side, np.int8))
    t = np.concatenate(t_all); j = np.concatenate(j_all)
    return dict(t=t, j=j, f=t + 1, side=np.concatenate(side_all), k=np.zeros(len(t), np.int8),
                px=O[t + 1, j], sig=S[t, j])


# ── statistics ────────────────────────────────────────────────────────────────────────────
def day_t(x, blocks):
    """Fill-weighted mean with a cluster-robust t over time blocks (weekly: longer than any path).
    NOT "average per day, then t across days": that weights each day by 1/fills-that-day, and the
    fill count depends on later prices that same day — look-ahead in the weights. The selftest
    caught it as t ≈ +2 on both sides of a pure random walk."""
    ok = np.isfinite(x)
    if ok.sum() < 30:
        return np.nan, np.nan, 0
    x, b = x[ok], blocks[ok]
    m = x.mean()
    s = pd.Series(x - m).groupby(b).sum()
    if len(s) < 10:
        return float(m), np.nan, len(s)
    s = s.reindex(range(int(s.index.min()), int(s.index.max()) + 1), fill_value=0.0).values
    # lag-1 Newey-West across adjacent weeks: a 72h path started late in one week lives in the next
    v = (s ** 2).sum() + 2 * 0.5 * (s[1:] * s[:-1]).sum()
    se = math.sqrt(max(v, (s ** 2).sum() * 0.25) * len(s) / (len(s) - 1)) / len(x)
    return float(m), float(m / se), len(s)


def frame(fl, lab, times, syms):
    df = pd.DataFrame({"sym": np.asarray(syms)[fl["j"]], "side": fl["side"], "k": fl["k"],
                       "fill_time": times[fl["f"]], "px": fl["px"], **lab})
    df["day"] = df.fill_time.dt.floor("D")
    df["blk"] = (df.fill_time - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7)   # weekly clusters
    return df


def detrend(df, C, times, syms):
    """e{h} = Δ(h) minus the coin's own average Δ(h) over every bar of the same calendar month.
    A measurement control, not a tradable signal (the month's drift is not known in advance): it
    asks whether fills carry information BEYOND the coin's trend, which the shuffled bars and R0
    both show is large (OOS alts drift down ~0.5% per 72h)."""
    month = pd.Series(times).dt.to_period("M").values
    col = {x: i for i, x in enumerate(syms)}
    mi = pd.Index(pd.unique(month))
    fm = mi.get_indexer(df.fill_time.dt.to_period("M").values)
    fj = df.sym.map(col).values
    with np.errstate(invalid="ignore", divide="ignore"):
        lc = np.log(C)
    for h in HS[1:]:
        fr = np.full_like(lc, np.nan)
        fr[: len(lc) - (h - 1)] = lc[h - 1:] - lc[: len(lc) - (h - 1)]
        B = pd.DataFrame(fr).groupby(month).mean().reindex(mi).values * 100
        df[f"e{h}"] = (df[f"r{h}"] - df["r1"]) - df.side.values * B[fm, fj]
    return df


def drift_table(df, label_):
    rows = []
    for (side, k), g in df.groupby(["side", "k"]):
        days = g.blk.values
        row = {"book": label_, "side": "long" if side > 0 else "short", "k": int(k), "n": len(g), "days": g.day.nunique()}
        for h in HS:
            m, _, _ = day_t(g[f"r{h}"].values, days)
            row[f"m{h}"] = m
        for h in HS[1:]:
            m, t, _ = day_t((g[f"r{h}"] - g["r1"]).values, days)
            row[f"d{h}"], row[f"t{h}"] = m, t
        for h in HS[1:]:
            row[f"x{h}"], row[f"tx{h}"] = day_t(g[f"e{h}"].values, days)[:2]
        mz, tz, _ = day_t(g["z24"].values, days)
        row["z24"], row["tz24"] = mz, tz
        rows.append(row)
    return pd.DataFrame(rows)


def print_drift(tabs):
    print(f"\n   drift after the fill, Δ(h) = m(h) − m(1) in %, week-clustered t in [ ];  cost maker-in/taker-out {COST_MT:.3f}%")
    print("   " + f"{'book':<9}{'side':<6}{'k':>2}{'n':>9}  {'m(1)':>7}" + "".join(f"{'Δ' + str(h):>16}" for h in HS[1:]))
    for t in tabs:
        for _, r in t.iterrows():
            print("   " + f"{r.book:<9}{r.side:<6}{r.k:>2}{r.n:>9}  {r.m1:+7.3f}"
                  + "".join(f"{r[f'd{h}']:+8.3f} [{r[f't{h}']:+5.1f}]" for h in HS[1:]))
    print("\n   EXCESS over the coin's same-month drift, e(h) in % [week-clustered t] — the number that matters")
    print("   " + f"{'book':<9}{'side':<6}{'k':>2}{'n':>9}  {'':>7}" + "".join(f"{'e' + str(h):>16}" for h in HS[1:]))
    for t in tabs:
        for _, r in t.iterrows():
            print("   " + f"{r.book:<9}{r.side:<6}{r.k:>2}{r.n:>9}  {'':>7}"
                  + "".join(f"{r[f'x{h}']:+8.3f} [{r[f'tx{h}']:+5.1f}]" for h in HS[1:]))


# ── selftest: the instrument, before any result ──────────────────────────────────────────
def synth(n_bars, n_coins, ou, seed, sub=60, vol=0.01):
    """Per-coin log price = random walk + OU deviation (hourly half-life 4h) sampled `sub` times
    per bar, so OHLC come from a near-continuous path. ou=0 is a pure random walk."""
    rng = np.random.default_rng(seed)
    n = n_bars * sub
    w = np.cumsum(rng.normal(0, vol / math.sqrt(sub), (n, n_coins)), axis=0)
    x = np.zeros((n, n_coins))
    if ou:
        phi = 0.5 ** (1 / (4 * sub))
        e = rng.normal(0, ou * vol / math.sqrt(sub), (n, n_coins))
        for i in range(1, n):
            x[i] = phi * x[i - 1] + e[i]
    p = np.exp(np.log(100) + w + x).reshape(n_bars, sub, n_coins)
    return p[:, 0], p.max(axis=1), p.min(axis=1), p[:, -1]


def run_engine(O, H, L, C):
    A, S = atr(H, L, C), sigma(C)
    fl = fills(O, H, L, C, A, S)
    return fl, label(fl, H, L, C)


def selftest() -> int:
    times = lambda n: pd.date_range("2022-01-01", periods=n, freq="1h")
    syms = [f"S{i}" for i in range(6)]

    def drift(O, H, L, C, side, k, h=12):
        fl, lab = run_engine(O, H, L, C)
        df = frame(fl, lab, times(C.shape[0]), syms)
        g = df[(df.side == side) & (df.k == k)]
        m, t, _ = day_t((g[f"r{h}"] - g["r1"]).values, g.blk.values)
        return m, t, g.r1.mean(), len(g)

    # 1. random walk: no drift after any fill, and the fill model is never optimistic
    O, H, L, C = synth(12000, 6, ou=0, seed=1)
    for side in (1, -1):
        for k in (1, 2):                                         # Gaussian bars almost never move 3 ATR in an hour
            m, t, r1, n = drift(O, H, L, C, side, k)
            assert n > 100 and abs(t) < 3.5, f"null drift side {side} k {k}: {m:+.4f}% t {t:+.2f} (n {n})"
            assert r1 <= 0.01, f"fill model optimistic on a random walk: side {side} k {k} m(1) {r1:+.4f}%"
    # 2. planted mean reversion: drift after a fill must be positive (both sides), and vanish on shuffled bars
    O, H, L, C = synth(12000, 6, ou=2.0, seed=2)
    for side in (1, -1):
        m, t, _, n = drift(O, H, L, C, side, 1)
        assert t > 5 and m > 0, f"planted reversion not found: side {side} {m:+.4f}% t {t:+.2f}"
    Os, Hs, Ls, Cs = mnr.shuffled_bars(O, H, L, C, np.random.default_rng(3))
    m, t, _, _ = drift(Os, Hs, Ls, Cs, 1, 1)
    assert abs(t) < 3.5, f"shuffled bars kept the planted drift: t {t:+.2f}"
    # 3. causality: truncating after bar T changes no fill (or its label inputs) whose arming bar < T−1
    fl_full, _ = run_engine(O, H, L, C)
    T = 7000
    fl_cut, _ = run_engine(O[:T], H[:T], L[:T], C[:T])
    key = lambda fl, m: set(zip(fl["t"][m], fl["j"][m], fl["side"][m], fl["k"][m], np.round(fl["px"][m], 10)))
    assert key(fl_full, fl_full["t"] < T - 1) == key(fl_cut, fl_cut["t"] < T - 1), "a fill depends on bars after it"
    # 4. per-rung fills nest: a bar that fills rung k also fills rung k−1 (never label by deepest rung)
    fl, _ = run_engine(*synth(6000, 6, ou=0, seed=4))
    have = set(zip(fl["t"], fl["j"], fl["side"], fl["k"]))
    assert all((t_, j_, s_, k_ - 1) in have for t_, j_, s_, k_ in have if k_ > 1), "rung fills do not nest"
    print("selftest: OK (random walk: no drift, fills never optimistic; planted reversion found, gone when shuffled; causal)")
    return 0


# ── thesis check ─────────────────────────────────────────────────────────────────────────
def thesis(df_fills, trades, C, times, syms, mode):
    """Match every strategy trade to a grid fill (same coin, same side) and decompose it.
    mode "approx": the nearest fill within ±6h of the entry, earlier ones included. That is the
                   user's framing: a grid only approximates an entry, and a rung that fills a little
                   early is the same trade. It is valid for coverage, entry price and exit attribution.
    mode "causal": the first fill in the 24h AFTER the entry. This is the only valid form for SETUP
                   VALUE: the strategy's trigger (e.g. a bullish BoS) is computed from the bounce after
                   an earlier fill, so gating earlier fills by it selects dips already known to bounce."""
    col = {s: i for i, s in enumerate(syms)}
    tix = pd.Series(np.arange(len(times)), index=times)
    F = df_fills.set_index(["sym", "side", "fill_time"]).sort_index()
    rows = []
    for tr in trades.itertuples():
        if tr.symbol not in col:
            continue
        side = SIDE[tr.strategy]
        # A rung armed at the close of hour t fills in bar t+1, whose start is `fill_time`. The setup is
        # known at the strategy's entry E, so only rungs with fill_time ≥ E may be gated by it. Matching
        # earlier fills selects dips that the strategy's later bounce confirmed: look-ahead.
        h0 = tr.entry_time.ceil("1h")
        lo, hi = (h0, h0 + pd.Timedelta(hours=24)) if mode == "causal" else \
                 (tr.entry_time - pd.Timedelta(hours=6), tr.entry_time + pd.Timedelta(hours=6))
        try:
            cand = F.loc[(tr.symbol, side, slice(lo, hi)), :]
        except KeyError:
            cand = None
        rec = {"strategy": tr.strategy, "ret": tr.return_pct, "matched": False}
        if cand is not None and len(cand):
            ft = cand.index.get_level_values("fill_time")
            pick = 0 if mode == "causal" else int(np.argmin(np.abs((ft - tr.entry_time).total_seconds())))
            c = cand.iloc[pick]
            fill_t = ft[pick]
            j = col[tr.symbol]
            # ponytail: exit marked at the close of the 1h bar containing it (strategies exit on 15m); noise, not bias
            xi = tix.get(tr.exit_time.floor("1h"))
            path_exit = 100 * side * math.log(C[xi, j] / c.px) if xi is not None and np.isfinite(C[xi, j]) else np.nan
            rec.update(matched=True, k=int(c.k), lag_h=(fill_t - tr.entry_time).total_seconds() / 3600,
                       entry_edge=100 * side * math.log(tr.entry_price / c.px) if tr.entry_price > 0 else np.nan,
                       path_exit=path_exit, day=fill_t.floor("D"), blk=(fill_t - pd.Timestamp("2020-01-06")) // pd.Timedelta(days=7), side=side, fill_time=fill_t,
                       hold_h=(tr.exit_time - fill_t).total_seconds() / 3600,
                       **{f"r{h}": c[f"r{h}"] for h in HS})
        rows.append(rec)
    return pd.DataFrame(rows)


def setup_value(m, df_fills):
    """r(h) of strategy-matched fills minus the mean r(h) of ALL fills with the same side, k and day."""
    base = df_fills.groupby(["side", "k", "day"])[[f"r{h}" for h in HS]].mean()
    mm = m.join(base, on=["side", "k", "day"], rsuffix="_all")
    return mm


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--universe", choices=["oos", "backtest"])
    ap.add_argument("--trades")
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--out", default=os.path.join(mnr.REPO, "reports"))
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    assert a.universe and a.trades, "--universe and --trades are required"

    syms = mnr.config_symbols("OosCoins" if a.universe == "oos" else "BacktestCoins")
    mk = mnr.build_market(syms, a.cache)
    keep = mk.close.index < SEAL                                   # block A only: B stays sealed
    times = mk.close.index[keep]
    syms = list(mk.close.columns)
    O, H, L, C = (d.values[keep] for d in (mk.open, mk.high, mk.low, mk.close))
    print(f"{a.universe}: {len(syms)} coins, block A {times[0]:%Y-%m-%d} → {times[-1]:%Y-%m-%d} (sealed from {SEAL:%Y-%m-%d})")

    fl, lab = run_engine(O, H, L, C)
    df = detrend(frame(fl, lab, times, syms), C, times, syms)
    S = sigma(C)
    r0 = random_entries(fl, C, O, S, np.random.default_rng(20261003))
    d0 = detrend(frame(r0, label(r0, H, L, C), times, syms), C, times, syms)
    Os, Hs_, Ls, Cs = mnr.shuffled_bars(O, H, L, C, np.random.default_rng(20261004))
    fs, ls = run_engine(Os, Hs_, Ls, Cs)
    dsh = detrend(frame(fs, ls, times, syms), Cs, times, syms)
    print(f"   fills: {len(df):,} real · {len(dsh):,} on shuffled bars · {len(d0):,} random entries (R0)")

    print("\n── 1. DRIFT MAP: what price does after a generic grid fill ──")
    tabs = [drift_table(df, "real"), drift_table(dsh, "shuffled"), drift_table(d0, "R0")]
    print_drift(tabs)
    print("\n   level m(h) in % (incl. the pessimistic fill), real fills:")
    for _, r in tabs[0].iterrows():
        print(f"   {r.side:<6}k{r.k}  " + "  ".join(f"h{h} {r[f'm{h}']:+.3f}" for h in HS) + f"   z24 {r.z24:+.3f} [t {r.tz24:+.1f}]")
    print("   MFE / MAE at 24h, real vs shuffled (median %):")
    for (side, k), g in df.groupby(["side", "k"]):
        gs = dsh[(dsh.side == side) & (dsh.k == k)]
        print(f"   {'long' if side > 0 else 'short':<6}k{k}  MFE {g.mfe24.median():5.2f} vs {gs.mfe24.median():5.2f}   "
              f"MAE {g.mae24.median():+6.2f} vs {gs.mae24.median():+6.2f}")

    print("\n── 2. THESIS CHECK: is each strategy a gated grid? ──")
    tr = pd.read_csv(a.trades, parse_dates=["entry_time", "exit_time"])
    tr = tr[(tr.entry_time < SEAL) & tr.strategy.isin(SIDE)]
    # Time-shift null: the same trades moved ±7-60 days on the same coin. "First fill after a random
    # time" is not an average fill (the first rung of a falling cascade still has the cascade ahead),
    # so every setup number is read against this, never against 0.
    rng = np.random.default_rng(20261005)
    sh = tr.copy()
    off = pd.to_timedelta(rng.choice([-1, 1], len(sh)) * rng.integers(7, 61, len(sh)), unit="D")
    sh["entry_time"], sh["exit_time"] = sh.entry_time + off, sh.exit_time + off
    sh = sh[(sh.entry_time > times[0] + pd.Timedelta(days=30)) & (sh.exit_time < times[-1])]

    m = thesis(df, tr, C, times, syms, "approx")
    mn = thesis(df, sh, C, times, syms, "approx")
    print("   (a) ENTRY APPROXIMATION: nearest rung within ±6h of the entry, earlier fills allowed")
    print(f"   {'strategy':<10}{'trades':>8}{'covered':>9}{'(null)':>8}  k1/k2/k3        lag_h  entry_edge  strat_net  path@exit  exit_added")
    for s_, g in m.groupby("strategy"):
        mt = g[g.matched]
        cn = mn[mn.strategy == s_].matched.mean()
        ks = "/".join(f"{(mt.k == k).mean():.0%}" for k in KS) if len(mt) else "—"
        ea = mt.ret - (mt.path_exit - mt.entry_edge - COST_MT)     # path re-based to the strategy's own entry
        print(f"   {s_:<10}{len(g):>8}{g.matched.mean():>8.0%}{cn:>8.0%}  {ks:<14}{mt.lag_h.median():>6.1f}  {mt.entry_edge.mean():>+9.3f}  "
              f"{mt.ret.mean():>+9.3f}  {mt.path_exit.mean():>+9.3f}  {ea.mean():>+9.3f}")
    print("   covered = a 1-3 ATR rung on the same coin/side within ±6h (null = same trades time-shifted ±7-60d)")
    print("   entry_edge = how much better the rung's price was than the strategy's entry (%); path@exit = the")
    print(f"   rung's gross path at the strategy's exit time; exit_added = strategy net − (path@exit − entry_edge − {COST_MT:.3f}%),")
    print("   i.e. what the strategy's exit did versus just holding to the same time from its own entry price")

    print("\n   (b) SETUP VALUE, causal: first rung fill in the 24h AFTER the entry, minus all fills of the same")
    print("   side, k and day (%, week-clustered t). REAL vs the time-shifted NULL, and their difference.")
    print("   " + f"{'strategy':<10}{'n':>7}  " + "".join(f"{'h' + str(h) + ' real / null / diff [t]':>34}" for h in (4, 12, 24)))
    sv = setup_value(thesis(df, tr, C, times, syms, "causal").query("matched").copy(), df)
    svn = setup_value(thesis(df, sh, C, times, syms, "causal").query("matched").copy(), df)
    for s_, g in sv.groupby("strategy"):
        gn = svn[svn.strategy == s_]
        cells = []
        for h in (4, 12, 24):
            m1, t1, _ = day_t((g[f"r{h}"] - g[f"r{h}_all"]).values.astype(float), g.blk.values)
            m0, t0, _ = day_t((gn[f"r{h}"] - gn[f"r{h}_all"]).values.astype(float), gn.blk.values)
            td = (m1 - m0) / math.sqrt((m1 / t1) ** 2 + (m0 / t0) ** 2)
            cells.append(f"{m1:+7.3f} /{m0:+7.3f} /{m1 - m0:+7.3f} [{td:+5.1f}]")
        print("   " + f"{s_:<10}{len(g):>7}  " + "".join(f"{c:>34}" for c in cells))

    os.makedirs(a.out, exist_ok=True)
    path = os.path.join(a.out, f"grid_paths_A_{a.universe}.csv.gz")
    df.drop(columns=["day", "blk"]).to_csv(path, index=False, float_format="%.5g")
    print(f"\n   wrote {path} (block A fills only)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
