"""Task 2 of docs/HANDOFF_BOUNCE_DCA_2026-10.md: can the dead bags be predicted at entry?

Everything below was fixed BEFORE the first run (3 trials, recorded in genotypes/ga_trials.json):
- Target: the variant-E position, force-closed at 180 days, ends as "dead" (no TP within 180d).
- Features, all from data at the signal bar's close:
  - drop depth (ATR) and speed (share of the 24-bar drop that happened in the last 6 bars);
  - distance from EMA200 and EMA50 slope over 24 bars;
  - volume z-score (log 24h quote volume vs its trailing 30d);
  - log age in the cache and log median 30d hourly notional;
  - the last settled funding rate;
  - BTC drawdown from its 24h high;
  - legacy BTC regime one-hot (Bull/Bear/HighVol; Ranging is the base) and its confidence.
  The HMM is not used: its emissions were fit on the whole history.
- Model: logistic regression with an L2 penalty lam=1 on standardized features. This is exactly the
  MAP of a Bayesian logistic with an N(0,1) prior per coefficient (the "weakly informative" one).
- Walk-forward: refit at each month start. The model trains on BacktestCoins positions whose OUTCOME
  was known (exit time) at least 1 day before the refit. The 180d label needs up to 180d to resolve,
  so an entry-time cutoff would leak. No model until the training set holds >= 10 dead bags.
- Rule: reject a signal when its predicted risk is above the q-quantile of the training set's own
  predicted risk, for q in {0.95, 0.90, 0.80}, i.e. rejecting about 5/10/20%.
- Measurement: re-simulate with rejected signals masked. A rejected signal frees the coin for a later
  entry. The control is rejecting the same fraction of signals at random (10 seeds).
- Universes: the BacktestCoins walk-forward, then OosCoins scored with the BacktestCoins model of
  that month. OOS labels never enter any fit.
- Gate 2 (OOS): a filtered book beats the unfiltered one AND beats every random-rejection seed, and
  the dead-bag catch rate clears the break-even bar from task 1: catch > FRR * win$/deadloss$.

Run:  python3 research/bounce_dca_filter.py        (--selftest for the model check)
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from bounce_dca_label import RESERVED, atr, funding_per_bar, label_coin, mnr, signal  # noqa: E402

HORIZON = 180
QS = (0.95, 0.90, 0.80)
CONTROLS = 10
FEATS = ["drop_atr", "speed", "dist_ema200", "ema50_slope", "vol_z", "log_age", "log_liq",
         "funding", "btc_dd24", "bull", "bear", "hivol", "reg_conf"]


# ── model ────────────────────────────────────────────────────────────────────────────────────────
def fit_logit(X: np.ndarray, y: np.ndarray, lam: float = 1.0):
    """L2 logistic by Newton steps on standardized X; the intercept is not penalized."""
    mu, sd = X.mean(0), X.std(0) + 1e-12
    Z = np.column_stack([np.ones(len(X)), (X - mu) / sd])
    w, pen = np.zeros(Z.shape[1]), np.r_[0.0, np.full(X.shape[1], lam)]
    for _ in range(50):
        p = 1 / (1 + np.exp(-Z @ w))
        step = np.linalg.solve(Z.T @ (Z * (p * (1 - p))[:, None]) + np.diag(pen + 1e-9),
                               Z.T @ (p - y) + pen * w)
        w -= step
        if np.abs(step).max() < 1e-8:
            break
    return lambda Xn: 1 / (1 + np.exp(-(np.column_stack([np.ones(len(Xn)), (Xn - mu) / sd]) @ w))), w


def auc(score, y) -> float:
    r = pd.Series(score).rank().values
    npos = y.sum()
    return (r[y == 1].sum() - npos * (npos + 1) / 2) / (npos * (len(y) - npos)) if 0 < npos < len(y) else np.nan


# ── data ─────────────────────────────────────────────────────────────────────────────────────────
def load(universe: str, cache: str) -> dict:
    out = {}
    for s in mnr.config_symbols(universe):
        df = mnr.load_h1(s, cache)
        if df is None or len(df) < 24 * 60:
            continue
        f = mnr.load_funding(s, cache)
        out[s] = (df, funding_per_bar(df.index, f), f)
    return out


def features(df: pd.DataFrame, fraw: pd.Series | None, btc: pd.DataFrame, reg: pd.DataFrame,
             sig: np.ndarray) -> pd.DataFrame:
    h, l, c = df["high"].values, df["low"].values, df["close"].values
    i = np.flatnonzero(sig)
    A = atr(h, l, c)
    cs = df["close"]
    e200, e50 = cs.ewm(span=200, adjust=False).mean().values, cs.ewm(span=50, adjust=False).mean().values
    lv = np.log(df["qvol"].rolling(24).sum().clip(lower=1.0))
    vz = ((lv - lv.rolling(720, min_periods=240).mean()) / lv.rolling(720, min_periods=240).std()).values
    liq = np.log(df["qvol"].rolling(720, min_periods=24).median().clip(lower=1.0)).values
    t = df.index[i]
    close_t = t + pd.Timedelta(hours=1)                       # signal bar's close
    if fraw is not None and len(fraw):
        fr = fraw.sort_index()
        fund = fr.reindex(close_t, method="ffill").fillna(0.0001).values
    else:
        fund = np.full(len(i), 0.0001)
    bdd = (btc["close"] / btc["high"].rolling(24).max() - 1).reindex(t).fillna(0.0).values
    rg = reg.reindex(t, method="ffill")                        # regime as of the bar's START
    return pd.DataFrame({
        "bar": i, "time": t,
        "drop_atr": (c[i - 24] - c[i]) / A[i],
        "speed": (c[i - 6] - c[i]) / np.maximum(c[i - 24] - c[i], 1e-12),
        "dist_ema200": c[i] / e200[i] - 1, "ema50_slope": e50[i] / e50[i - 24] - 1,
        "vol_z": np.nan_to_num(vz[i]), "log_age": np.log1p(i / 24), "log_liq": liq[i],
        "funding": fund, "btc_dd24": bdd,
        "bull": (rg["legacy"] == "Bull").values.astype(float),
        "bear": (rg["legacy"] == "Bear").values.astype(float),
        "hivol": (rg["legacy"] == "HighVol").values.astype(float),
        "reg_conf": rg["legacy_conf"].fillna(0.0).values})


def prepare(coins: dict, btc, reg) -> dict:
    """Per coin: OHLC arrays, funding, signal mask, features at every signal bar, unfiltered trades."""
    out = {}
    for s, (df, fund, fraw) in coins.items():
        o, h, l, c = (df[k].values for k in ("open", "high", "low", "close"))
        sig = signal(h, l, c)
        F = features(df, fraw, btc, reg, sig)
        F["sym"] = s
        tr = pd.DataFrame(label_coin(o, h, l, c, fund, HORIZON, sig))
        if len(tr):
            tr["sym"], tr["bar"] = s, tr["entry"] - 1
            tr["time"], tr["resolved"] = df.index[tr["entry"]], df.index[tr["exit"]]
        out[s] = dict(ohlc=(o, h, l, c), fund=fund, sig=sig, F=F, tr=tr)
    return out


# ── walk-forward scoring ─────────────────────────────────────────────────────────────────────────
def walk_forward(train: dict, score_sets: list[dict]):
    """Monthly refits on train-universe outcomes resolved before the refit. Writes risk and the
    per-q cutoffs onto every signal row of every set in score_sets."""
    T = pd.concat([d["tr"].merge(d["F"], on=["sym", "bar"], suffixes=("", "_f"))
                   for d in train.values() if len(d["tr"])], ignore_index=True)
    T = T[T.kind != "censored"]
    T["y"] = (T.kind == "dead").astype(float)
    allF = [d["F"] for ss in score_sets for d in ss.values()]
    t0 = min(f.time.min() for f in allF if len(f))
    t1 = max(f.time.max() for f in allF if len(f))
    for f in allF:
        f["risk"] = np.nan
        for q in QS:
            f[f"cut{q}"] = np.nan
    first, w_last = None, None
    for m in pd.date_range(t0.normalize().replace(day=1), t1, freq="MS"):
        tr = T[T.resolved < m - pd.Timedelta(days=1)]
        if tr.y.sum() < 10:
            continue
        first = first or m
        pred, w_last = fit_logit(tr[FEATS].values, tr.y.values)
        cuts = {q: np.quantile(pred(tr[FEATS].values), q) for q in QS}
        for f in allF:
            sel = (f.time >= m) & (f.time < m + pd.offsets.MonthBegin(1))
            if sel.any():
                f.loc[sel, "risk"] = pred(f.loc[sel, FEATS].values)
                for q in QS:
                    f.loc[sel, f"cut{q}"] = cuts[q]
    return first, w_last


def run_universe(name: str, ds: dict, start, win_dead_ratio: float) -> None:
    def sim(reject) -> pd.DataFrame:
        rows = []
        for s, d in ds.items():
            sig = d["sig"].copy()
            if len(d["F"]):
                sig[d["F"].bar.values[reject(d["F"])]] = False
            tr = pd.DataFrame(label_coin(*d["ohlc"], d["fund"], HORIZON, sig))
            if len(tr):
                tr["sym"] = s
                tr["time"] = d["idx"][tr["entry"].values]
                rows.append(tr)
        x = pd.concat(rows, ignore_index=True)
        return x[x.time >= start]

    def stats(x: pd.DataFrame) -> tuple[float, int, int, float]:
        return x.pnl.sum(), len(x), int((x.kind == "dead").sum()), x[x.kind == "dead"].pnl.sum()

    base = sim(lambda F: np.zeros(len(F), bool))
    bn, bt, bd, bdl = stats(base)
    print(f"\n=== {name}: entries from {start.date()}, horizon {HORIZON}d, P&L in base units "
          f"(reserve {RESERVED})")
    print(f"unfiltered: n {bt}, dead {bd} ({bdl:.1f}), net {bn:.1f}")

    # discrimination on the unfiltered entries
    U = pd.concat([d["tr"].merge(d["F"], on=["sym", "bar"], suffixes=("", "_f"))
                   for d in ds.values() if len(d["tr"])], ignore_index=True)
    U = U[(U.time >= start) & (U.kind != "censored") & U.risk.notna()]
    y = (U.kind == "dead").values.astype(int)
    print(f"walk-forward AUC on unfiltered entries: {auc(U.risk.values, y):.3f}  "
          f"(dead {y.sum()} of {len(y)})")
    print(f"{'reject':>7s} {'rej%':>5s} {'caught':>7s} {'FRR':>6s} {'bar':>6s} {'n':>5s} {'dead':>5s} "
          f"{'deadP&L':>8s} {'net':>7s} {'Δnet':>7s} | {'rand Δ p50':>10s} {'max':>6s} {'beat':>5s}")
    rng = np.random.default_rng(11)
    for q in QS:
        rej_fn = lambda F, q=q: (F.risk > F[f"cut{q}"]).fillna(False).values
        rej = (U.risk > U[f"cut{q}"]).values
        caught = rej[y == 1].mean()
        frr = rej[y == 0].mean()
        bar = frr * win_dead_ratio
        fn, ft, fd, fdl = stats(sim(rej_fn))
        frac = np.mean(np.concatenate([rej_fn(d["F"])[d["F"].time >= start]
                                       for d in ds.values() if len(d["F"])]))
        ctl = []
        for _ in range(CONTROLS):
            seed = rng.integers(1 << 31)
            r2 = np.random.default_rng(seed)
            ctl.append(stats(sim(lambda F: (r2.random(len(F)) < frac) & (F.time >= start).values))[0] - bn)
        ctl = np.array(ctl)
        print(f"{1 - q:7.2f} {100 * frac:5.1f} {100 * caught:6.1f}% {100 * frr:5.1f}% {100 * bar:5.1f}% "
              f"{ft:5d} {fd:5d} {fdl:8.1f} {fn:7.1f} {fn - bn:+7.1f} | {np.median(ctl):+10.1f} "
              f"{ctl.max():+6.1f} {100 * (fn - bn > ctl).mean():4.0f}%")
        print(f"        confusion (unfiltered entries): dead caught {int(rej[y == 1].sum())}/{y.sum()}, "
              f"good rejected {int(rej[y == 0].sum())}/{(y == 0).sum()}")


def record_trials(n: int, key: str = "bounce_dca_filter") -> None:
    """Merge into the C# GaTrialCounter ledger. Trials accumulate, never reset."""
    path = os.path.join(mnr.REPO, "genotypes", "ga_trials.json")
    d = json.load(open(path)) if os.path.exists(path) else {}
    d[key] = d.get(key, 0) + n
    json.dump(d, open(path, "w"), indent=2)


def selftest() -> int:
    rng = np.random.default_rng(0)
    X = rng.normal(size=(4000, 3))
    y = (rng.random(4000) < 1 / (1 + np.exp(-(-3 + 1.5 * X[:, 0])))).astype(float)
    pred, w = fit_logit(X, y, lam=1.0)
    assert w[1] > 1.0 and abs(w[2]) < 0.3 and abs(w[3]) < 0.3, w     # finds the planted feature
    assert auc(pred(X), y.astype(int)) > 0.8
    assert abs(auc(rng.random(4000), y.astype(int)) - 0.5) < 0.06     # noise scores ~0.5
    print("selftest ok", np.round(w, 2))
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default=os.path.join(mnr.REPO, "candle_cache"))
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        return selftest()

    btc = mnr.load_h1("BTCUSDT", a.cache)
    reg = pd.read_csv(os.path.join(mnr.REPO, "reports", "btc_regime_series.csv"),
                      parse_dates=["time"]).set_index("time")[["legacy", "legacy_conf"]]
    reg = reg[~reg.index.duplicated(keep="last")].sort_index()
    bt_raw, oos_raw = load("BacktestCoins", a.cache), load("OosCoins", a.cache)
    bt, oos = prepare(bt_raw, btc, reg), prepare(oos_raw, btc, reg)
    for raw, ds in ((bt_raw, bt), (oos_raw, oos)):
        for s in ds:
            ds[s]["idx"] = raw[s][0].index
    print(f"coins: BacktestCoins {len(bt)}, OosCoins {len(oos)}")

    first, w = walk_forward(bt, [bt, oos])
    record_trials(len(QS))
    print(f"first model month: {first.date()}")
    print("last model's coefficients (standardized):")
    print("  " + "  ".join(f"{k} {v:+.2f}" for k, v in zip(["icpt"] + FEATS, w)))

    # break-even bar from task 1's real numbers on BacktestCoins at 180d: total winner P&L / total
    # dead-bag loss. A filter rejecting FRR of good entries must catch > FRR * ratio of dead bags.
    allt = pd.concat([d["tr"] for d in bt.values() if len(d["tr"])])
    ratio = allt[allt.kind == "tp"].pnl.sum() / -allt[allt.kind == "dead"].pnl.sum()
    print(f"break-even: catch > {ratio:.2f} x FRR")

    run_universe("BacktestCoins (walk-forward)", bt, first, ratio)
    run_universe("OosCoins (never fitted)", oos, first, ratio)
    return 0


if __name__ == "__main__":
    sys.exit(main())
