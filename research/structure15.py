"""Does market STRUCTURE add out-of-sample information to the discover15 open search? (ablation)

Pre-registration: docs/STRUCTURE_ABLATION_2026-10.md (frozen at the commit that adds this file).

Why: discover15's 29 features are one-bar snapshots (window returns, EMA distances, RSI, ...).
A break of structure is STATEFUL — a confirmed swing level that persists until broken, however long
ago it formed — so a depth-3 tree over fixed windows cannot rebuild it. The search never asked.
This adds a structure block, per coin AND on the equal-weight market index, and measures the change
in out-of-sample rank IC with everything else frozen (same candidates, same seed, same model, same
walk-forward).

Arms (H = 16 bars = 4h PRIMARY; H = 4 / 48 secondary):
  A       base 29 features (reproduces discover15)
  B       base + full structure block           ← primary comparison B − A
  Bc, Bm  base + coin-only / market-only block  (secondary, report only)
  P1..P5  base + structure block with its rows permuted jointly (placebo: same columns, no link)

Run:  python3 research/structure15.py              (full run, block A only)
      python3 research/structure15.py --selftest   (causality + known-path checks, no market data)
"""

from __future__ import annotations

import argparse
import gc
import sys

import numpy as np
import pandas as pd

import discover15 as d15
import wf_parallel as wfp
from grid_paths import SEAL, day_t

W_SCALES = (4, 16)                    # fractal half-width in 15m bars: swing confirmed W bars later
TOUCH_ATR = 0.25                      # a "test" = high within 0.25 ATR of the level, close not beyond
BASE = list(d15.FEATURES)
COIN = [f"{n}{w}" for w in W_SCALES for n in ("s_hi", "s_lo", "bos_dir", "bos_age", "hhhl")] + \
       ["sweep4", "tests_hi4", "tests_lo4", "pdh", "pdl", "rnd", "dath", "don_brk"]
MKT = [f"m_{n}{w}" for w in W_SCALES for n in ("s_hi", "s_lo", "bos_dir", "bos_age", "hhhl")]
STRUCT = COIN + MKT
PLACEBOS = 5


def swings(H, L, C, A, w):
    """Causal swing state per column, recorded at the CLOSE of each bar. H/L/C/A are T × N.

    Pivot high at p: H[p] > max(H[p-w:p]) and H[p] >= max(H[p+1:p+w+1]); it is only KNOWN at p+w,
    so it becomes the standing level from bar p+w on. BoS = first close beyond the standing level
    (fires once per level). Sweep = wick beyond an unbroken level, close back inside.
    """
    T, N = C.shape
    Hd, Ld = pd.DataFrame(H), pd.DataFrame(L)
    with np.errstate(invalid="ignore"):
        ph = (H > Hd.rolling(w).max().shift(1).values) & (H >= Hd[::-1].rolling(w).max()[::-1].shift(-1).values)
        pl = (L < Ld.rolling(w).min().shift(1).values) & (L <= Ld[::-1].rolling(w).min()[::-1].shift(-1).values)
    out = {n: np.full((T, N), np.nan, np.float32) for n in ("s_hi", "s_lo", "bos_dir", "bos_age", "hhhl",
                                                             "sweep", "tests_hi", "tests_lo")}
    sh, sh0, sl, sl0 = (np.full(N, np.nan) for _ in range(4))
    hb, lb = np.zeros(N, bool), np.zeros(N, bool)            # level already broken
    bdir, bt = np.zeros(N), np.full(N, -1)
    th, tl = np.zeros(N), np.zeros(N)
    for t in range(T):
        if t >= w:                                           # confirmations known at this close
            c = ph[t - w]
            sh0[c], sh[c], hb[c], th[c] = sh[c], H[t - w, c], False, 0
            c = pl[t - w]
            sl0[c], sl[c], lb[c], tl[c] = sl[c], L[t - w, c], False, 0
        h, l, cl, a = H[t], L[t], C[t], A[t]
        with np.errstate(invalid="ignore"):
            up, dn = (cl > sh) & ~hb, (cl < sl) & ~lb
            sweep = np.where((l < sl) & (cl > sl) & ~lb, 1.0, 0.0) - np.where((h > sh) & (cl < sh) & ~hb, 1.0, 0.0)
            th += (h >= sh - TOUCH_ATR * a) & ~up & ~hb
            tl += (l <= sl + TOUCH_ATR * a) & ~dn & ~lb
        bdir[up], bt[up] = 1, t
        hb |= up
        bdir[dn], bt[dn] = -1, t                             # a bar breaking both ways counts as down
        lb |= dn
        out["s_hi"][t], out["s_lo"][t] = (cl - sh) / a, (cl - sl) / a
        out["bos_dir"][t] = bdir
        out["bos_age"][t] = np.where(bt >= 0, np.log1p(t - bt), np.nan)
        out["hhhl"][t] = np.sign(sh - sh0) + np.sign(sl - sl0)
        out["sweep"][t], out["tests_hi"][t], out["tests_lo"][t] = sweep, th, tl
    return out


def levels(idx, H, L, C, A):
    """Anchoring levels at each close: prior UTC day's high/low, nearest round number, all-time high,
    and a fresh 24h (96-bar) Donchian break."""
    day = lambda X, f: pd.DataFrame(X, index=idx).resample("D").agg(f).shift(1).reindex(idx, method="ffill").values
    with np.errstate(invalid="ignore", divide="ignore"):
        step = 10.0 ** (np.floor(np.log10(C)) - 1)           # two significant digits: 84,900 → 1,000s
        Hd, Ld = pd.DataFrame(H), pd.DataFrame(L)
        hi96, lo96 = Hd.rolling(96).max().shift(1).values, Ld.rolling(96).min().shift(1).values
        yield "pdh", (C - day(H, "max")) / A
        yield "pdl", (C - day(L, "min")) / A
        yield "rnd", (C - np.round(C / step) * step) / A
        yield "dath", np.log(C / np.fmax.accumulate(H, axis=0))
        yield "don_brk", np.where(C > hi96, 1.0, np.where(C < lo96, -1.0, 0.0))


def structure_iter(idx, M, A, mcum):
    H, L, C = M["h"], M["l"], M["c"]
    for w in W_SCALES:
        s = swings(H, L, C, A, w)
        for n in ("s_hi", "s_lo", "bos_dir", "bos_age", "hhhl"):
            yield f"{n}{w}", s[n]
        if w == 4:
            yield "sweep4", s["sweep"]
            yield "tests_hi4", s["tests_hi"]
            yield "tests_lo4", s["tests_lo"]
        del s
        gc.collect()
    yield from levels(idx, H, L, C, A)
    # market index: close-only (no wicks), so no sweep/tests; "ATR" = mean |15m move| over 14 bars
    m = mcum[:, None]
    ma = pd.Series(np.abs(np.diff(mcum, prepend=np.nan))).rolling(14).mean().values[:, None]
    for w in W_SCALES:
        s = swings(m, m, m, ma, w)
        for n in ("s_hi", "s_lo", "bos_dir", "bos_age", "hhhl"):
            yield f"m_{n}{w}", s[n][:, 0]


_base_iter = d15.feature_iter


def full_iter(idx, M, fund, S, A, mlr, mcum):
    yield from _base_iter(idx, M, fund, S, A, mlr, mcum)
    yield from structure_iter(idx, M, A, mcum)


def ic_table(R, X_, h, mask_rows=None):
    """Out-of-sample rank IC per (quarter, side) from the walk-forward predictions."""
    ft = pd.to_datetime(X_["fill_time"])
    q = ft.to_period("Q")
    out = {}
    p, y = R, X_["y"][h]
    ok = np.isfinite(p) & np.isfinite(y)
    for side in (1, -1):
        for qq in pd.unique(q[ok & (X_["side"] == side)]):
            m = ok & (X_["side"] == side) & (q == qq)
            if m.sum() >= 5000:
                out[(qq, side)] = pd.Series(p[m]).rank().corr(pd.Series(y[m]).rank())
    return pd.Series(out)


def paired(d: pd.Series) -> tuple[float, float, float, float, float]:
    """mean, t across quarter-sides, first-half mean, second-half mean, MDE at 80% power (2.8·SE)."""
    d = d.dropna().sort_index()
    se = d.std(ddof=1) / np.sqrt(len(d))
    half = len(d) // 2
    return d.mean(), d.mean() / se, d.iloc[:half].mean(), d.iloc[half:].mean(), 2.8 * se


def main_run() -> int:
    d15.FEATURES = BASE + STRUCT              # ponytail: monkeypatch keeps discover15's build() untouched
    d15.feature_iter = full_iter
    ALL = d15.FEATURES
    ci = {n: i for i, n in enumerate(ALL)}
    sets = {"A": BASE, "B": BASE + STRUCT, "Bc": BASE + COIN, "Bm": BASE + MKT}
    rng = np.random.default_rng(20261009)     # discover15's seed: identical candidate subsample
    print("building candidate sets (block A, 15m, touch fills) + structure block …", flush=True)
    D = d15.build("backtest", rng)
    E = d15.build("oos", rng)
    assert pd.to_datetime(D["fill_time"]).max() < SEAL and pd.to_datetime(E["fill_time"]).max() < SEAL
    for h in d15.HS15:
        names = ["A", "B"] + (["Bc", "Bm"] if h == d15.PRIMARY else [])
        cols = {nm: [ci[n] for n in sets[nm]] for nm in names}
        specs = {nm: (cols[nm], None, None) for nm in names}
        if h == d15.PRIMARY:                  # placebo: structure rows permuted jointly, per universe
            pc = [i for i, c in enumerate(cols["B"]) if c >= len(BASE)]
            for z in range(PLACEBOS):
                r = lambda n: np.random.default_rng(7000 + z).permutation(n)
                specs[f"P{z + 1}"] = (cols["B"], (r(len(D["X"])), pc), (r(len(E["X"])), pc))
        res = wfp.run(D, E, h, specs)
        ics = {nm: {u: ic_table(res[nm]["pred"][u], X_, h) for u, X_ in (("bt", D), ("oos", E))} for nm in specs}
        for nm in names:
            print(f"\n   ── arm {nm}, H = {h} — selection table (discover15 format, no label nulls) ──")
            d15.report(D, E, h, res[nm], 0)
        plc = [ics[f"P{z + 1}"] for z in range(PLACEBOS)] if h == d15.PRIMARY else []
        print(f"\n══ H = {h}{'  [PRIMARY]' if h == d15.PRIMARY else ''}: out-of-sample rank IC, paired by quarter × side ══")
        for u in ("bt", "oos"):
            a = ics["A"][u]
            print(f"   {u}: arm A IC {a.mean():+.4f} ({len(a)} quarter-sides)")
            for nm in names[1:]:
                m, t, h1, h2, mde = paired(ics[nm][u] - a)
                print(f"      Δ({nm} − A) {m:+.4f} [t {t:+.2f}]  halves {h1:+.4f} / {h2:+.4f}  MDE80 {mde:.4f}")
            if plc:
                pm = [paired(p[u] - a)[0] for p in plc]
                real = paired(ics["B"][u] - a)[0]
                print(f"      placebo Δ: {' '.join(f'{v:+.4f}' for v in pm)}  → real beats {sum(real > v for v in pm)}/{len(pm)}")
        if h == d15.PRIMARY:
            wfp.importance(D, h, res["B"], sets["B"], cols["B"])
        del res
        gc.collect()
    print("\nTrials: 1 primary (B − A at H=16) + 4 secondary (Bc, Bm, H=4, H=48). Block B untouched.")
    return 0


def selftest() -> int:
    rng = np.random.default_rng(1)
    T, N = 3000, 3
    C = 100 * np.exp(np.cumsum(rng.normal(0, 0.003, (T, N)), axis=0))
    H, L = C * (1 + np.abs(rng.normal(0, 0.002, (T, N)))), C * (1 - np.abs(rng.normal(0, 0.002, (T, N))))
    A = np.full((T, N), 0.3)
    idx = pd.date_range("2024-01-01", periods=T, freq="15min")
    full = {n: v for w in W_SCALES for n, v in swings(H, L, C, A, w).items()} | dict(levels(idx, H, L, C, A))
    # 1) causality: scrambling every bar after t0 must not change any feature at or before t0
    t0 = 2000
    H2, L2, C2 = H.copy(), L.copy(), C.copy()
    for X in (H2, L2, C2):
        X[t0 + 1:] = X[t0 + 1:][::-1] * 1.07
    cut = {n: v for w in W_SCALES for n, v in swings(H2, L2, C2, A, w).items()} | dict(levels(idx, H2, L2, C2, A))
    for n in full:
        assert np.allclose(full[n][: t0 + 1], cut[n][: t0 + 1], equal_nan=True), f"look-ahead in {n}"
    # 2) known path (w=2): pivot high 10 at bar 5, confirmed at bar 7; first close above 10 at bar 12
    c = np.array([5, 6, 7, 8, 9, 10, 8, 7, 6, 7, 8, 9, 11, 12, 13], float)[:, None]
    s = swings(c, c, c, np.ones_like(c), 2)
    assert np.isnan(s["s_hi"][6, 0]) and s["s_hi"][7, 0] == 7 - 10, "level must appear only at confirmation"
    assert s["bos_dir"][11, 0] == 0 and s["bos_dir"][12, 0] == 1 and s["bos_age"][12, 0] == 0, "BoS at bar 12"
    assert np.isclose(s["bos_age"][14, 0], np.log1p(2)), "BoS fires once per level"
    # 3) sweep: wick above an unbroken high, close back below
    h, l, cc = c.copy(), c.copy(), c.copy()
    h[9], cc[9] = 10.5, 7
    s = swings(h, l, cc, np.ones_like(c), 2)
    assert s["sweep"][9, 0] == -1 and s["bos_dir"][9, 0] == 0, "sweep is not a break"
    print("selftest OK: no look-ahead in", len(full), "features; pivot/BoS/sweep timing exact")
    return 0


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    sys.exit(selftest() if ap.parse_args().selftest else main_run())
