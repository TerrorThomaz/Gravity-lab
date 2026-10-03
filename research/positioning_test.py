"""Does POSITIONING (open interest, long/short crowding) at entry separate the strategies' winners from
their losers — the information candles never had?

Fixed before the first run.
Availability (verified 2026-10-03): a value stamped T is the state at T, published ~30 s after T, not
revised. It may be used only from T + 5 min — joined backward-as-of on entry_time − 5 min.
Features per trade (per coin, from candle_cache/{SYM}_oi1h.csv and _lsr1h.csv):
  oi_ch4, oi_ch24, oi_ch72   ln OI(T)/OI(T−h)
  oi_z24                     oi_ch24 / std of oi_ch24 over the trailing 30 d (≤ T)
  lsr                        long/short account ratio (buyRatio) at T
  lsr_ch24                   lsr(T) − lsr(T−24h)
  lsr_z                      (lsr − trailing-30d mean) / trailing-30d std
  s_*                        the five change/z features × trade side (+1 long, −1 short): positive =
                             positioning moving WITH the trade's direction
Tests (trades = edgetest raw dumps, honest stop fills):
  1. per strategy × feature: Spearman IC with ret (t on distinct days), ret by tercile; BH over all tests
  2. one L2 logistic P(win) (features + signed + side + strategy one-hot), walk-forward quarterly on
     trades exited ≥1 d before the quarter; vs each strategy's own historical win rate (Brier skill)
     and vs 20 refits on labels permuted WITHIN strategy (AUC null)
Pass: model AUC beats the permutation null AND Brier skill > 0 vs own history — on both universes.

Run:  python3 research/positioning_test.py --trades <csv> --universe oos|backtest
"""

from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
import pandas as pd
from scipy.stats import spearmanr

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "scripts"))
import market_neutral_research as mnr  # noqa: E402

LAG = pd.Timedelta(minutes=5)
FEATS = ["oi_ch4", "oi_ch24", "oi_ch72", "oi_z24", "lsr", "lsr_ch24", "lsr_z"]
SIGNED = ["oi_ch4", "oi_ch24", "oi_ch72", "oi_z24", "lsr_ch24", "lsr_z"]
L2, NULLS, MIN_TRAIN = 1.0, 20, 2000


def coin_features(sym: str, cache: str) -> pd.DataFrame | None:
    def rd(kind):
        p = os.path.join(cache, f"{sym}_{kind}1h.csv")
        if not os.path.exists(p):
            return None
        s = pd.read_csv(p, header=None, names=["ms", "v"])
        s.index = pd.to_datetime(s.ms, unit="ms"); return s.v[~s.index.duplicated()].sort_index()
    oi, ls = rd("oi"), rd("lsr")
    if oi is None or ls is None or len(oi) < 24 * 40:
        return None
    oi = oi.asfreq("1h"); ls = ls.asfreq("1h")
    lo = np.log(oi.where(oi > 0))
    f = pd.DataFrame(index=oi.index)
    for h in (4, 24, 72):
        f[f"oi_ch{h}"] = lo - lo.shift(h)
    f["oi_z24"] = f.oi_ch24 / f.oi_ch24.rolling(720, min_periods=240).std()
    f["lsr"] = ls
    f["lsr_ch24"] = ls - ls.shift(24)
    f["lsr_z"] = (ls - ls.rolling(720, min_periods=240).mean()) / ls.rolling(720, min_periods=240).std()
    f["avail"] = f.index + LAG                       # when each row may first be used
    return f.dropna(subset=["oi_ch24", "lsr"]).reset_index(drop=True)


def t_day(x: np.ndarray, y: np.ndarray, days: np.ndarray) -> tuple[float, float]:
    ic = spearmanr(x, y).statistic
    nd = len(np.unique(days))
    return ic, ic * math.sqrt(max(1, nd - 2) / max(1e-12, 1 - ic * ic))


def bh(p: np.ndarray) -> np.ndarray:
    n = len(p); o = np.argsort(p); q = np.empty(n); m = 1.0
    for r, i in enumerate(o[::-1]):
        k = n - r; m = min(m, p[i] * n / k); q[i] = m
    return q


def logit_fit(X: np.ndarray, y: np.ndarray, l2: float = L2) -> np.ndarray:
    """L2 logistic by Newton/IRLS on standardised X (intercept unpenalised)."""
    n, p = X.shape; w = np.zeros(p)
    R = np.eye(p) * l2; R[0, 0] = 0
    for _ in range(25):
        z = np.clip(X @ w, -30, 30); mu = 1 / (1 + np.exp(-z))
        g = X.T @ (mu - y) + R @ w
        H = (X * (mu * (1 - mu))[:, None]).T @ X + R
        step = np.linalg.solve(H, g); w -= step
        if np.abs(step).max() < 1e-6:
            break
    return w


def auc(s: np.ndarray, y: np.ndarray) -> float:
    r = pd.Series(s).rank().values; pos = y.sum(); neg = len(y) - pos
    return (r[y == 1].sum() - pos * (pos + 1) / 2) / (pos * neg) if pos and neg else np.nan


def walk_forward(df: pd.DataFrame, X: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    P = np.full(len(df), np.nan); B = np.full(len(df), np.nan)
    q0 = df.entry.min().to_period("Q").start_time
    for q in pd.date_range(q0, df.entry.max(), freq="QS"):
        tr = (df.exit < q - pd.Timedelta(days=1)).values
        te = ((df.entry >= q) & (df.entry < q + pd.offsets.QuarterBegin(1))).values
        if tr.sum() < MIN_TRAIN or not te.any():
            continue
        mu, sd = X[tr].mean(0), X[tr].std(0); sd[sd < 1e-12] = 1
        Z = lambda A: np.column_stack([np.ones(len(A)), (A - mu) / sd])
        w = logit_fit(Z(X[tr]), y[tr])
        P[te] = 1 / (1 + np.exp(-np.clip(Z(X[te]) @ w, -30, 30)))
        rate = pd.Series(y[tr]).groupby(df.strategy.values[tr]).mean()
        B[te] = [rate.get(s, y[tr].mean()) for s in df.strategy.values[te]]
    return P, B


def selftest() -> int:
    rng = np.random.default_rng(1)
    n = 6000
    X = rng.normal(size=(n, 3))
    for planted, lo, hi in ((True, 0.62, 1.0), (False, 0.45, 0.55)):
        z = 1.2 * X[:, 0] if planted else 0 * X[:, 0]
        y = (rng.random(n) < 1 / (1 + np.exp(-z))).astype(float)
        tr, te = slice(0, n // 2), slice(n // 2, n)
        mu, sd = X[tr].mean(0), X[tr].std(0)
        Z = lambda A: np.column_stack([np.ones(len(A)), (A - mu) / sd])
        w = logit_fit(Z(X[tr]), y[tr])
        a = auc(Z(X[te]) @ w, y[te])
        assert lo < a < hi, f"{'planted' if planted else 'noise'} OOS AUC {a:.3f} outside ({lo}, {hi})"
    # as-of availability: a row stamped T may only serve trades entering at or after T + 5 min
    f = pd.DataFrame({"avail": pd.to_datetime(["2024-01-01 10:05", "2024-01-01 11:05"]), "v": [1.0, 2.0]})
    g = pd.DataFrame({"use_at": pd.to_datetime(["2024-01-01 11:04", "2024-01-01 11:05"])})
    m = pd.merge_asof(g, f, left_on="use_at", right_on="avail", direction="backward")
    assert list(m.v) == [1.0, 2.0], m
    print("selftest: OK (planted signal found OOS, noise ≈ 0.5, as-of join never uses a row before it exists)")
    return 0


def main() -> int:
    if "--selftest" in sys.argv:
        return selftest()
    ap = argparse.ArgumentParser()
    ap.add_argument("--trades", required=True)
    ap.add_argument("--universe", choices=["oos", "backtest"], required=True)
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    a = ap.parse_args()

    tr = pd.read_csv(a.trades, parse_dates=["entry_time", "exit_time"]).rename(
        columns={"entry_time": "entry", "exit_time": "exit", "return_pct": "ret"})
    side = {"FadeShort": -1, "GridShort": -1, "RipShort": -1}
    tr["dir"] = tr.strategy.map(lambda s: side.get(s, 1))
    tr["use_at"] = tr.entry - LAG
    parts = []
    for sym, g in tr.groupby("symbol"):
        f = coin_features(sym, a.cache)
        if f is None:
            continue
        m = pd.merge_asof(g.sort_values("use_at"), f.sort_values("avail"), left_on="use_at", right_on="avail",
                          direction="backward", tolerance=pd.Timedelta(hours=3))
        parts.append(m)
    df = pd.concat(parts).dropna(subset=FEATS).sort_values("entry").reset_index(drop=True)
    assert (df.avail <= df.entry - LAG).all(), "lookahead: a feature row became available after entry − 5 min"
    for c in SIGNED:
        df["s_" + c] = df.dir * df[c]
    print(f"{a.universe}: {len(df):,} trades with positioning ({len(tr):,} total), "
          f"{df.entry.min():%Y-%m-%d} → {df.entry.max():%Y-%m-%d} · availability check passed (all rows ≤ entry − 5 min)")

    # ── 1. univariate ──
    days = df.entry.dt.floor("D").values
    rows = []
    cols = FEATS + ["s_" + c for c in SIGNED]
    for s, g in df.groupby("strategy"):
        if len(g) < 500:
            continue
        for c in cols:
            ic, t = t_day(g[c].values, g.ret.values, g.entry.dt.floor("D").values)
            rows.append((s, c, len(g), ic, t))
    u = pd.DataFrame(rows, columns=["strategy", "feature", "n", "ic", "t"])
    u["p"] = 2 * (1 - pd.Series(np.abs(u.t)).apply(lambda z: 0.5 * (1 + math.erf(z / math.sqrt(2)))))
    u["q"] = bh(u.p.values)
    print(f"\n── 1. univariate: {len(u)} tests, {int((u.q < 0.05).sum())} with BH q < 0.05 ──")
    top = u.reindex(u.t.abs().sort_values(ascending=False).index).head(12)
    for r in top.itertuples():
        print(f"   {r.strategy:<10} {r.feature:<11} n {r.n:6d}  IC {r.ic:+.3f}  t {r.t:+5.2f}  q {r.q:.3f}")

    # ── 2. walk-forward P(win) ──
    strats = sorted(df.strategy.unique())
    X = np.column_stack([df[c].values for c in cols] + [df.dir.values] + [(df.strategy == s).values.astype(float) for s in strats])
    y = (df.ret > 0).astype(float).values
    P, B = walk_forward(df, X, y)
    cov = ~np.isnan(P)
    a_real = auc(P[cov], y[cov])
    rng = np.random.default_rng(20261015); nulls = []
    for _ in range(NULLS):
        yp = y.copy()
        for s in strats:
            idx = np.where(df.strategy.values == s)[0]; yp[idx] = rng.permutation(y[idx])
        Pn, _ = walk_forward(df, X, yp)
        nulls.append(auc(Pn[cov], yp[cov]))
    nulls = np.array(nulls)
    print(f"\n── 2. walk-forward P(win): {cov.sum():,} covered trades ──")
    print(f"   pooled AUC {a_real:.3f}  vs label-permuted nulls {nulls.mean():.3f} ± {nulls.std():.3f} (max {nulls.max():.3f}) "
          f"→ p = {(1 + (nulls >= a_real).sum()) / (1 + NULLS):.3f}")
    print("   per strategy: AUC · Brier skill vs own historical win rate · ret% by P tercile (low/mid/high)")
    for s in strats:
        m = cov & (df.strategy.values == s)
        if m.sum() < 300:
            continue
        bs = 1 - np.mean((P[m] - y[m]) ** 2) / np.mean((B[m] - y[m]) ** 2)
        g = df[m].assign(P=P[m]).sort_values("P"); n3 = len(g) // 3
        print(f"   {s:<10} n {m.sum():6d}  AUC {auc(P[m], y[m]):.3f}  Brier skill {bs:+.4f}  "
              f"{g.ret.iloc[:n3].mean():+.3f} / {g.ret.iloc[n3:-n3].mean():+.3f} / {g.ret.iloc[-n3:].mean():+.3f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
