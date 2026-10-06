"""Lead-lag measurement (descriptive, no trial): do coins that co-move with BTC LAG it, enough to enter
followers after BTC moves? And how accurate is the forecast engine on BTC itself?

For each follower and horizon k: regress the follower's return over minutes t+1..t+k on BTC's return in
minute t, controlling for BTC's own return over t+1..t+k (its continuation) and the follower's minute-t
return (pooled OLS). The BTC(t) coefficient is the LAGGED response that a fast follower could still
capture. Event view: BTC minute moves |r| ≥ 0.25% → the follower's signed move over the next k minutes,
beyond beta × BTC's own next move, against ~0.10% taker round trip (Hyperliquid).

Run:  python3 research/leadlag.py
"""
import glob, math, os, sys
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
import numpy as np, pandas as pd
from grid_paths import mnr

CACHE = os.path.join(mnr.REPO, "candle_cache")


def load(sfx, syms=None, every=1):
    files = sorted(glob.glob(os.path.join(CACHE, f"*USDT{sfx}.csv")))
    out = {}
    for f in files:
        s = os.path.basename(f).replace(f"{sfx}.csv", "")
        if syms and s not in syms:
            continue
        d = mnr._read_ms_csv(f, 6)
        if d is None or len(d) < 10_000:
            continue
        out[s] = d.iloc[:, 3][~d.index.duplicated(keep="last")]
    idx = out["BTCUSDT"].index
    R = pd.DataFrame({s: np.log(v.reindex(idx)).diff() for s, v in out.items()})
    return R.astype(np.float32)


def response(R, ks, unit):
    b = R["BTCUSDT"].values.astype(float)
    T = len(b)
    cb = np.r_[0, np.nancumsum(np.nan_to_num(b))]
    print(f"\n── lagged response to BTC, {unit} bars, {R.shape[1] - 1} followers, {R.index[0]:%Y-%m-%d} → {R.index[-1]:%Y-%m-%d} ──")
    rows = []
    for k in ks:
        bf = cb[np.minimum(np.arange(T) + 1 + k, T)] - cb[np.minimum(np.arange(T) + 1, T)]   # BTC over t+1..t+k
        XtX, XtY, YtY, n = np.zeros((4, 4)), np.zeros(4), 0.0, 0     # normal equations, accumulated per coin
        for s in R.columns:                                             # (stacking all coins needed ~3 GB: OOM)
            if s == "BTCUSDT":
                continue
            a = R[s].values.astype(float)
            ca = np.r_[0, np.nancumsum(np.nan_to_num(a))]
            af = ca[np.minimum(np.arange(T) + 1 + k, T)] - ca[np.minimum(np.arange(T) + 1, T)]
            ok = np.isfinite(a) & np.isfinite(b) & (np.arange(T) + 1 + k < T)
            X = np.column_stack([np.ones(ok.sum()), b[ok], bf[ok], a[ok]]); Y = af[ok]
            XtX += X.T @ X; XtY += X.T @ Y; YtY += Y @ Y; n += len(Y)
        coef = np.linalg.solve(XtX, XtY)
        sse = YtY - 2 * coef @ XtY + coef @ XtX @ coef
        se = math.sqrt(sse / (n - 4) * np.linalg.inv(XtX)[1, 1])
        rows.append((k, coef[1], coef[1] / se, coef[2], coef[3]))
        print(f"   k={k:>3}: lagged response to BTC(t) {coef[1]:+.4f} [t {coef[1] / se:+.1f}]   (beta to BTC's own next move "
              f"{coef[2]:+.2f}; follower's own lag {coef[3]:+.3f})")
    return b, cb


def events(R, ks, thr):
    b = R["BTCUSDT"].values.astype(float)
    T = len(b)
    cb = np.r_[0, np.nancumsum(np.nan_to_num(b))]
    ev = np.flatnonzero(np.abs(np.nan_to_num(b)) >= thr)
    ev = ev[ev + max(ks) + 1 < T]
    print(f"   BTC minute moves |r| ≥ {thr * 100:.2f}%: {len(ev)} events; follower move in BTC's direction over the next k minutes")
    for k in ks:
        sig, ex = [], []
        bf = cb[ev + 1 + k] - cb[ev + 1]
        for s in R.columns:
            if s == "BTCUSDT":
                continue
            a = R[s].values.astype(float)
            ca = np.r_[0, np.nancumsum(np.nan_to_num(a))]
            af = ca[ev + 1 + k] - ca[ev + 1]
            ok = np.isfinite(a[ev])
            sig.append(np.sign(b[ev][ok]) * af[ok]); ex.append(np.sign(b[ev][ok]) * (af[ok] - bf[ok]))
        sig, ex = np.concatenate(sig) * 100, np.concatenate(ex) * 100
        print(f"      k={k:>2} min: follower {sig.mean():+.3f}%   minus BTC's own next move {ex.mean():+.3f}%   (cost ≈ 0.10% round trip)")


def forecast_btc():
    p = os.path.join(os.path.dirname(os.path.realpath(CACHE)), "data", "forecast", "backtest_h4.npz")
    z = np.load(p, allow_pickle=False)
    t, s, F = pd.DatetimeIndex(z["times"]), list(z["symbols"]), z["F"]
    j = s.index("BTCUSDT")
    R = load("_15m", {"BTCUSDT"})["BTCUSDT"]
    c = R.cumsum()
    fut = (c.shift(-16) - c).reindex(t - pd.Timedelta(minutes=15)).values * 100          # next 4h from the forecast time
    f = F[:, j]
    ok = np.isfinite(f) & np.isfinite(fut)
    ic = pd.Series(f[ok]).rank().corr(pd.Series(fut[ok]).rank())
    hit = np.mean(np.sign(f[ok]) == np.sign(fut[ok]))
    top = f[ok] >= np.quantile(f[ok], 0.9)
    print(f"\n── forecast engine on BTC (4h, walk-forward): rank IC {ic:+.3f}, direction hit rate {hit:.1%} over {ok.sum():,} hours; "
          f"top-decile forecasts: hit {np.mean(fut[ok][top] > 0):.1%}, mean next-4h {fut[ok][top].mean():+.3f}% vs all {fut[ok].mean():+.3f}%")


def main():
    R1 = load("_1m")
    response(R1, (1, 2, 5, 10, 15), "1m")
    events(R1, (1, 2, 5, 10), 0.0025)
    del R1
    R15 = load("_15m")
    response(R15, (1, 2, 4), "15m")
    forecast_btc()


if __name__ == "__main__":
    main()
