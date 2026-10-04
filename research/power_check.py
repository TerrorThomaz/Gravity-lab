"""Power check: could our tests have SEEN an edge of realistic size? And what would a hedge add?

Every "no edge" verdict so far is "not significant". That only means "absent" if the test had power.
t ≈ annual Sharpe × √years, so a test on 1–3 years can only see Sharpe ≳ 1.5. Literature edges
(trend, momentum, carry) sit at 0.5–1.0. This measures the actual pass rules against planted edges
of known size, on resampled REAL data (its fat tails, vol clustering and cross-coin correlation).

PART A — planted market trend vs the trend tests' own rules.
  null      stationary block bootstrap of the hourly log-return matrix (rows = hours, all coins
            together, mean block 30d), each block's sign flipped at random: kills drift and any
            momentum longer than a block, keeps tails/clustering/correlation. Both universes use the
            SAME rows, so their correlation is kept too.
  plant     every coin's hourly log return += δ·s_t, s_t = sign of the planted equal-weight market's
            trailing 30d return through bar t−1 (causal, recursive). This is exactly the edge the
            block-B trial-1 TREND rule trades.
  sleeve    each day at 00:00, every coin, side = 30d market sign, hold 168h, taker in/out (0.21%).
            δ is solved for target Sharpes 0…2 on a 30-year planted path. The null keeps real momentum
            INSIDE its 30d blocks, so the baseline Sharpe varies by path (+0.2…+0.6 seen): the true
            Sharpe that counts is "replicate mean SR", the sleeve's OosCoins Sharpe (overlapping
            cohorts, 1/7 capital each) averaged over the very replicates the rules were run on.
  rules     B1   block-B rule: trend mean > 0 and > max(always-long, always-short), both universes
            T1   t ≥ 2 in both universes, and both halves positive in both universes
            T2   t ≥ 3 in both universes. An UPPER bound for any searched result: a search adds
                 multiplicity (DSR) on top, so its power at a given Sharpe is lower still.
            t2   t ≥ 2 in OosCoins alone (reference)
  windows   discovery 2020-11→2024-01 (3.2y), validation 2024-01→2025-07 (1.5y), block A (4.7y),
            block B (1.26y), everything (5.9y).
  omitted   funding (both sides: the planted edge is gross of it; costs are charged).

PART B — what a sleeve adds to the live book, and whether we could detect that.
  book      ERC of the real daily sleeves carry (factor-neutral, band 0.5%) + Grid + GridShort
            (edgetest trade log, 5%/session, cap 12), as `market_neutral_research.py combo` builds it.
  sleeve    X = μ + σ(ρ·z_book + √(1−ρ²)·ε), z_book the real standardised book, ε a sign-flipped
            block bootstrap of the standardised equal-weight market (independent rows). σ = 10%/yr.
  theory    adding X improves the book's Sharpe iff SR_X > ρ·SR_book; the gain is set by the
            residual information ratio IR = (SR_X − ρ·SR_book)/√(1−ρ²), max book Sharpe
            √(SR_book² + IR²). A hedge (ρ < 0) has IR > SR_X: it is easier to see as an addition
            than standalone.
  tests     standalone weekly t ≥ 2, and alpha: intercept t ≥ 2 regressing weekly X on weekly book.

PART C — INFORMATION (seen data, not evidence): the two trend sleeves we have, against the book.
  trend_forward (frozen 6h Donchian + ATR trail, top-20) and the trial-1 market-TSMOM sleeve on
  OosCoins: Sharpe, ρ to the book, IR, alpha t, mean on the book's worst 5% days, Δ book Sharpe.

Run:  python3 research/power_check.py [--reps 200] [--trades reports/edgetest_raw_trades.csv]
      python3 research/power_check.py --selftest
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from grid_paths import day_t, mnr  # noqa: E402

SIG_H, HOLD, BLK = 720, 168, 720
TAKER2 = 2 * mnr.TAKER_COST_PER_SIDE * 100          # % per round trip
CACHE = os.path.join(mnr.REPO, "candle_cache")
TARGETS = (0.0, 0.5, 0.75, 1.0, 1.5, 2.0)
WINDOWS = {"discovery 3.2y": 3.2, "validation 1.5y": 1.5, "block A 4.7y": 4.7, "block B 1.26y": 1.26, "all 5.9y": 5.9}


# ── Part A ────────────────────────────────────────────────────────────────────────────────────────
def boot_rows(T: int, L: int, rng, blk: int = BLK):
    """Stationary bootstrap row indices with one random sign per block."""
    rows, sgn, i = np.empty(L, int), np.empty(L), 0
    while i < L:
        n = min(int(rng.geometric(1 / blk)), L - i)
        rows[i:i + n] = (rng.integers(0, T) + np.arange(n)) % T
        sgn[i:i + n] = rng.choice((-1.0, 1.0))
        i += n
    return rows, sgn


def plant(R: np.ndarray, delta: float):
    """R' = R + δ·s, s_t = sign(trailing SIG_H sum of the planted EW market through t−1)."""
    live = np.isfinite(R)
    with np.errstate(invalid="ignore"):
        m = np.where(live.any(1), np.nanmean(np.where(live, R, np.nan), 1), 0.0)
    m = np.nan_to_num(m)
    L = len(m)
    s, mp, run = np.zeros(L), np.empty(L), 0.0
    for t in range(L):
        if t >= SIG_H:
            s[t] = 1.0 if run > 0 else -1.0
            run -= mp[t - SIG_H]
        mp[t] = m[t] + delta * s[t]
        run += mp[t]
    return np.where(live, R + delta * s[:, None], np.nan), s


def sleeve_daily(Rp: np.ndarray, s: np.ndarray) -> np.ndarray:
    """Daily net book of the trial-1 sleeve: 7 overlapping daily cohorts, 1/7 capital each, equal
    weight over live coins. The decision at the close of bar d·24−1 uses s[d·24] (same information)."""
    L = len(s)
    m = np.nan_to_num(np.nanmean(Rp, 1))
    dec = s[np.arange(0, L, 24)]                                   # side chosen for the cohort opened on day d
    pos = np.zeros(L)
    for k in range(7):
        d = np.arange(L) // 24 - k
        pos += np.where(d >= SIG_H // 24, dec[np.clip(d, 0, len(dec) - 1)], 0.0) / 7
    hourly = pos * (np.exp(m) - 1)
    daily = hourly[: L // 24 * 24].reshape(-1, 24).sum(1)
    daily[SIG_H // 24:] -= TAKER2 / 100 / 7                        # one cohort's round trip per day
    return daily[SIG_H // 24 + 7:]


def sharpe(x) -> float:
    x = np.asarray(x)
    return float(x.mean() / x.std() * math.sqrt(365)) if x.std() > 0 else 0.0


def trial1(lc: np.ndarray, s: np.ndarray):
    """The block-B trial-1 statistics on one universe: trend / long / short (mean, t) and halves."""
    L, N = lc.shape
    days = np.arange(SIG_H, L - HOLD - 1, 24)
    tt, jj = (a.ravel() for a in np.meshgrid(days, np.arange(N), indexing="ij"))
    raw = 100 * (lc[tt + HOLD, jj] - lc[tt, jj])
    ok = np.isfinite(raw)
    tt, jj, raw = tt[ok], jj[ok], raw[ok]
    side = s[tt + 1]                                               # sign known at the close of bar tt
    blk = tt // 168
    out = {nm: day_t(sd * raw - TAKER2, blk)[:2]
           for nm, sd in (("trend", side), ("long", 1.0), ("short", -1.0))}
    h = tt < np.median(tt)
    tr = side * raw - TAKER2
    out["halves"] = (tr[h].mean(), tr[~h].mean())
    return out


def part_a(reps: int, seed: int) -> pd.DataFrame:
    syms_b, syms_o = mnr.config_symbols("BacktestCoins"), mnr.config_symbols("OosCoins")
    mk = mnr.build_market(sorted(set(syms_b + syms_o)), CACHE)
    C = mk.close.values
    with np.errstate(invalid="ignore", divide="ignore"):
        R = np.diff(np.log(C), axis=0)
    cols = list(mk.close.columns)
    jb = np.array([cols.index(c) for c in syms_b if c in cols])
    jo = np.array([cols.index(c) for c in syms_o if c in cols])
    T = len(R)
    rng = np.random.default_rng(seed)
    print(f"PART A: {len(cols)} coins ({len(jb)} BacktestCoins, {len(jo)} OosCoins), {mk.close.index[0]:%Y-%m-%d} → "
          f"{mk.close.index[-1]:%Y-%m-%d}, {reps} replicates per cell")

    # calibrate δ → true sleeve Sharpe on long planted OosCoins paths (linear in δ; checked at the end)
    Lc = 24 * 365 * 30
    rows, sg = boot_rows(T, Lc, rng)
    Rc = R[np.ix_(rows, jo)] * sg[:, None]
    base = sharpe(sleeve_daily(*plant(Rc, 0.0)))
    sr_of = lambda d: sharpe(sleeve_daily(*plant(Rc, d)))
    deltas = {}
    for sr in TARGETS:                         # secant: the plant feeds its own signal, so not linear
        a, fa, b = 0.0, base, 1e-4
        fb = sr_of(b)
        for _ in range(6):
            if abs(fb - sr) < 0.03 or fb == fa:
                break
            a, fa, b = b, fb, max(0.0, b + (sr - fb) * (b - a) / (fb - fa))
            fb = sr_of(b)
        deltas[sr] = b if sr > base else 0.0
    rows, sg = boot_rows(T, Lc, rng)
    Rv = R[np.ix_(rows, jo)] * sg[:, None]
    real = {sr: sharpe(sleeve_daily(*plant(Rv, d))) for sr, d in deltas.items()}
    print(f"  calibration: null sleeve Sharpe {base:+.2f}; δ per hour for target Sharpe: "
          + ", ".join(f"{sr:g}→{100 * d:.4f}% (check {real[sr]:+.2f})" for sr, d in deltas.items()))

    out = []
    for wname, yrs in WINDOWS.items():
        L = int(yrs * 365 * 24) + SIG_H + HOLD + 24
        hits = {sr: {r: 0 for r in ("B1", "T1", "T2", "t2")} for sr in TARGETS}
        srs = {sr: [] for sr in TARGETS}
        for _ in range(reps):
            rows, sg = boot_rows(T, L, rng)
            Rb = R[rows] * sg[:, None]
            for sr in TARGETS:
                Rp, s = plant(Rb, deltas[sr])
                lc = np.where(np.isfinite(Rp), np.cumsum(np.nan_to_num(Rp), 0), np.nan)
                res = [trial1(lc[:, j], s) for j in (jb, jo)]
                srs[sr].append(sharpe(sleeve_daily(Rp[:, jo], s)))
                tr = [r["trend"] for r in res]
                hits[sr]["B1"] += all(r["trend"][0] > 0 and r["trend"][0] > max(r["long"][0], r["short"][0]) for r in res)
                hits[sr]["T1"] += all(r["trend"][1] >= 2 and min(r["halves"]) > 0 for r in res)
                hits[sr]["T2"] += all(t[1] >= 3 for t in tr)
                hits[sr]["t2"] += tr[1][1] >= 2
        for sr in TARGETS:
            out.append({"window": wname, "target": sr, "check path": real[sr], "replicate mean SR": float(np.mean(srs[sr])),
                        **{r: v / reps for r, v in hits[sr].items()}})
        print(f"  {wname} done")
    return pd.DataFrame(out)


# ── Part B / C ────────────────────────────────────────────────────────────────────────────────────
def book_daily(trades: str) -> tuple[pd.Series, pd.DataFrame]:
    mk = mnr.build_market(mnr.config_symbols("OosCoins"), CACHE)
    r, _ = mnr.run_carry(mk, mnr.CarryParams(band=0.005))
    carry = pd.Series(r.net, index=r.index).resample("1D").sum()
    carry = carry[carry.index >= carry[carry != 0].index.min()]
    g, gs = mnr.grid_sleeve(trades, "Grid"), mnr.grid_sleeve(trades, "GridShort")
    idx = pd.date_range(max(carry.index.min(), g.index.min()), min(carry.index.max(), g.index.max()), freq="D")
    S = pd.DataFrame({k: v.reindex(idx, fill_value=0.0) for k, v in (("carry", carry), ("grid", g), ("gridshort", gs))})
    book, _ = mnr.erc_combine(S)
    return book, S


def alpha_t(x: np.ndarray, b: np.ndarray) -> float:
    """Intercept t of weekly x on weekly b (plain OLS on weekly sums; weeks are ~independent here)."""
    n = len(x) // 7 * 7
    X, B = x[:n].reshape(-1, 7).sum(1), b[:n].reshape(-1, 7).sum(1)
    A = np.column_stack([np.ones_like(B), B])
    coef, *_ = np.linalg.lstsq(A, X, rcond=None)
    e = X - A @ coef
    cov = (e @ e) / (len(X) - 2) * np.linalg.inv(A.T @ A)
    return float(coef[0] / math.sqrt(cov[0, 0]))


def week_t(x: np.ndarray) -> float:
    w = x[: len(x) // 7 * 7].reshape(-1, 7).sum(1)
    return float(w.mean() / w.std(ddof=1) * math.sqrt(len(w)))


def invvol_add(book: np.ndarray, x: np.ndarray, lb: int = 90) -> np.ndarray:
    """Two-sleeve ERC (= inverse vol) with trailing-90d vols, strictly before the day."""
    vb = pd.Series(book).rolling(lb, min_periods=60).std().shift(1).values
    vx = pd.Series(x).rolling(lb, min_periods=60).std().shift(1).values
    wb = np.where(np.isfinite(vb) & np.isfinite(vx), (1 / vb) / (1 / vb + 1 / vx), 0.5)
    return wb * book + (1 - wb) * x


def part_b(book: pd.Series, reps: int, seed: int) -> pd.DataFrame:
    mk = mnr.build_market(mnr.config_symbols("OosCoins"), CACHE)
    with np.errstate(invalid="ignore", divide="ignore"):
        m = np.nanmean(np.diff(np.log(mk.close.values), axis=0), 1)
    mkt = pd.Series(np.nan_to_num(m), index=mk.close.index[1:]).resample("1D").sum().values
    eps0 = (mkt - mkt.mean()) / mkt.std()
    b = book.values
    srb, z = sharpe(b), (b - b.mean()) / b.std()
    D = len(b)
    rng = np.random.default_rng(seed)
    sd = 0.10 / math.sqrt(365)
    print(f"\nPART B: book = ERC(carry, Grid, GridShort), {book.index[0]:%Y-%m-%d} → {book.index[-1]:%Y-%m-%d} "
          f"({D / 365:.1f}y), Sharpe {srb:.2f}; {reps} replicates per cell")
    out = []
    for rho in (-0.5, -0.25, 0.0, 0.25, 0.5):
        for sx in (-0.25, 0.0, 0.25, 0.5, 1.0):
            ir = (sx - rho * srb) / math.sqrt(1 - rho ** 2)
            st = al = 0
            gains = []
            for _ in range(reps):
                rows, _ = boot_rows(D, D, rng, blk=30)
                er, es = boot_rows(len(eps0), D, rng, blk=30)
                bb = b[rows]
                x = sx * 0.10 / 365 + sd * (rho * z[rows] + math.sqrt(1 - rho ** 2) * eps0[er] * es)
                st += week_t(x) >= 2
                al += alpha_t(x, bb) >= 2
                gains.append(sharpe(invvol_add(bb, x)) - sharpe(bb))
            out.append({"rho": rho, "SR_x": sx, "IR": ir, "max book SR": math.sqrt(srb ** 2 + max(ir, 0) ** 2),
                        "ΔSR ERC theory": (srb + sx) / math.sqrt(2 + 2 * rho) - srb,
                        "median ΔSR (ERC)": float(np.median(gains)), "P(standalone t≥2)": st / reps,
                        "P(alpha t≥2)": al / reps})
    return pd.DataFrame(out)


def part_c(book: pd.Series) -> pd.DataFrame:
    import trend_forward as tf
    syms = sorted(set(mnr.config_symbols("BacktestCoins") + mnr.config_symbols("OosCoins")))
    names, idx, O, H, L, C, Q, F = tf.load6h_cache(syms)
    bk, _, _ = tf.engine(O, H, L, C, Q, F, idx)
    tfd = pd.Series(bk, index=idx).resample("1D").sum()
    # trial-1 market-TSMOM sleeve on real OosCoins (no bootstrap, no plant)
    mk = mnr.build_market(mnr.config_symbols("OosCoins"), CACHE)
    with np.errstate(invalid="ignore", divide="ignore"):
        R = np.diff(np.log(mk.close.values), axis=0)
    _, s = plant(R, 0.0)
    ts = sleeve_daily(R, s)
    d0 = mk.close.index[1].floor("D") + pd.Timedelta(days=SIG_H // 24 + 7)
    tsd = pd.Series(ts, index=pd.date_range(d0, periods=len(ts), freq="D"))
    rows = []
    worst = book <= book.quantile(0.05)
    for name, x in (("trend_forward (6h Donchian+trail)", tfd), ("market TSMOM 30d/7d (trial-1)", tsd)):
        j = book.index.intersection(x.index)
        xb, bb = x.reindex(j).values, book.reindex(j).values
        rho = float(np.corrcoef(xb, bb)[0, 1])
        srx, srb = sharpe(xb), sharpe(bb)
        rows.append({"sleeve": name, "years": len(j) / 365, "SR_x": srx, "rho": rho,
                     "IR": (srx - rho * srb) / math.sqrt(1 - rho ** 2), "alpha t": alpha_t(xb, bb),
                     "x on book's worst 5% days (%/day)": 100 * float(x.reindex(j)[worst.reindex(j)].mean()),
                     "book SR": srb, "book+x SR (ERC)": sharpe(invvol_add(bb, xb))})
    return pd.DataFrame(rows)


# ── selftest ──────────────────────────────────────────────────────────────────────────────────────
def selftest() -> int:
    """Planting is causal and monotone; the sleeve recovers the planted drift; alpha_t sees a hedge."""
    rng = np.random.default_rng(0)
    R = rng.standard_t(4, (24 * 365 * 6, 20)) * 0.004
    R[:500, :5] = np.nan
    Rp, s = plant(R, 0.0)
    assert np.allclose(np.nan_to_num(Rp), np.nan_to_num(R)) and abs(s).max() == 1
    # causality: perturbing bar t must not change s at or before t
    R2 = R.copy(); R2[5000] += 0.5
    _, s2 = plant(R2, 1e-4); _, s1 = plant(R, 1e-4)
    assert np.array_equal(s1[:5001], s2[:5001]), "s_t uses bar t or later"
    sr = [sharpe(sleeve_daily(*plant(R, d))) for d in (0.0, 1e-4, 2e-4)]
    assert sr[0] < sr[1] < sr[2] and sr[2] > 1.0, f"sleeve does not recover the plant: {sr}"
    # the trial-1 statistic: planted trend → positive t on the trend side
    Rp, s = plant(R, 2e-4)
    lc = np.where(np.isfinite(Rp), np.cumsum(np.nan_to_num(Rp), 0), np.nan)
    r = trial1(lc, s)
    assert r["trend"][1] > 2, r
    # alpha: a zero-Sharpe perfect hedge (ρ = −0.5) of a Sharpe-2 book has positive alpha
    b = rng.normal(2 * 0.1 / 365, 0.1 / math.sqrt(365), 365 * 20)
    x = 0.1 / math.sqrt(365) * (-0.5 * (b - b.mean()) / b.std() + math.sqrt(0.75) * rng.normal(size=len(b)))
    assert alpha_t(x, b) > 2 and abs(week_t(x)) < 2.5, (alpha_t(x, b), week_t(x))
    print(f"selftest: OK (causal plant; sleeve Sharpe {sr[0]:+.2f}/{sr[1]:+.2f}/{sr[2]:+.2f} at δ 0/1e-4/2e-4; "
          f"hedge alpha t {alpha_t(x, b):+.1f} with standalone t {week_t(x):+.1f})")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--reps", type=int, default=200)
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--trades", default=os.path.join(mnr.REPO, "reports", "edgetest_raw_trades.csv"))
    ap.add_argument("--parts", default="abc")
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    fmt = {"display.width": 220, "display.float_format": lambda v: f"{v:,.2f}"}
    if "a" in a.parts:
        res = part_a(a.reps, a.seed)
        with pd.option_context(*sum(fmt.items(), ())):
            print("\nPART A — P(pass) for a planted market trend of the given true Sharpe")
            for w, g in res.groupby("window", sort=False):
                print(f"  [{w}]\n" + g.drop(columns="window").to_string(index=False))
    if "b" in a.parts or "c" in a.parts:
        book, _ = book_daily(a.trades)
        with pd.option_context(*sum(fmt.items(), ())):
            if "b" in a.parts:
                print(part_b(book, a.reps, a.seed).to_string(index=False))
            if "c" in a.parts:
                print("\nPART C — INFORMATION (seen data): existing trend sleeves against the live book")
                print(part_c(book).to_string(index=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
