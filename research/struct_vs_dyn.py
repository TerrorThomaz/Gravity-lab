import sys; sys.path.insert(0, "research")
import numpy as np, rulepath as rp
rng = np.random.default_rng(20261012)
D, E = rp.build("backtest"), rp.build("oos")
models = rp.fit_models(D, rng)
types = rp.rung_types()
for side in (1, -1):
    st = [t for t in types if t[0] == side]
    best = max(st, key=lambda t: models[t]["mean"])
    print(f"STRUCT {'long' if side > 0 else 'short'}: {best}  discovery mean {models[best]['mean']:+.3f}%  win {models[best]['base']:.0%}")
for un, U in (("bt", D), ("oos", E)):
    for mode in ("STRUCT", "ALWAYS", "STATIC"):
        d, n = rp.policy_book(U, models, mode, np.random.default_rng(7))
        sr, t, dsr, ann = rp.sharpe_stats(d)
        h = len(d) // 2
        s1 = rp.sharpe_stats(d.iloc[:h])[0]; s2 = rp.sharpe_stats(d.iloc[h:])[0]
        print(f"  {un:<4}{mode:<7} trades {n:>6}  ann {ann:+6.1f}%  Sharpe {sr:+5.2f} [t {t:+.2f}]  halves {s1:+.2f}/{s2:+.2f}")
    dS, _ = rp.policy_book(U, models, "STRUCT"); dA, _ = rp.policy_book(U, models, "ALWAYS")
    diff = (dA - dS).fillna(0)
    print(f"  {un}: ALWAYS − STRUCT daily P&L: Sharpe of the difference {rp.sharpe_stats(diff)[0]:+.2f} [t {rp.sharpe_stats(diff)[1]:+.2f}]  (the indicator rules' contribution)")
