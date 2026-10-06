"""Process-parallel quarterly walk-forward for the discover-family searches (discover15, discover1h,
structure15).

The serial version fitted one numpy GBM at a time on one core: about 220 fits for structure15, ~3h.
Each (arm, quarter, side, null) fit is independent, so they go to a fork pool. Children read the
candidate matrices through fork copy-on-write: nothing large is pickled, only the predictions come
back.

Semantics are the serial walk-forward's: train on candidates whose label ended before the quarter
(purge), cap at 400k rows, winsorise the target at 0.5/99.5%, refit quarterly from 12 months in,
score BacktestCoins (D) and OosCoins (E) for that quarter. Randomness is seeded per task from
(seed, quarter, side, null), so results are deterministic for any worker count, but NOT bit-identical
to runs made before 2026-10-06, which drew everything from one shared sequential rng.

Workers: WF_WORKERS (default: cores − 1, at most 7, because the 7 GB box holds ~1 GB of candidates
plus ~150 MB per fit).

Engine (WF_ENGINE): "lgbm" (default when lightgbm is importable — run under ~/Gravity-lab/.venv-research)
or "np" (gbm_np). LightGBM gets the same model spec as gbm_np (depth 3, 150 rounds, lr 0.05, min leaf
1000, L2 10, row 0.5 / column 0.7 subsampling, 32 bins) and runs its fits one at a time in this process
on all cores (no fork: OpenMP and fork do not mix). ~20× faster than the numpy engine.
"""

from __future__ import annotations

import multiprocessing as mp
import os

import numpy as np
import pandas as pd

from gbm_np import Gbm, apply_bins, bin_edges

try:
    import lightgbm as _lgb
except ImportError:
    _lgb = None
ENGINE = os.environ.get("WF_ENGINE") or ("lgbm" if _lgb is not None else "np")

_S: dict = {}                                           # set in the parent, inherited by forked children


class _Lgbm:
    """gbm_np's spec in LightGBM. Raw floats in; LightGBM bins (32) and routes NaN itself."""
    def __init__(self, seed: int):
        self.p = dict(objective="regression", max_depth=3, num_leaves=8, learning_rate=0.05, min_data_in_leaf=1000,
                      lambda_l2=10.0, bagging_fraction=0.5, bagging_freq=1, feature_fraction=0.7, max_bin=32,
                      seed=seed, deterministic=True, force_row_wise=True, verbose=-1,
                      num_threads=int(os.environ.get("LGBM_THREADS", os.cpu_count() or 4)))

    def fit(self, X, y):
        self.m = _lgb.train(self.p, _lgb.Dataset(X, y, free_raw_data=True), num_boost_round=150)
        return self

    def predict(self, X):
        return self.m.predict(X, num_threads=self.p["num_threads"])


def _fit_model(X, y, seed):
    """(edges, model): edges is None for LightGBM, which bins internally."""
    if ENGINE == "lgbm":
        return None, _Lgbm(seed).fit(X, y)
    edges = bin_edges(X)
    return edges, Gbm(seed=seed).fit(apply_bins(X, edges), y)


def _predict(edges, m, X):
    return m.predict(X) if edges is None else m.predict(apply_bins(X, edges))


def workers() -> int:
    return int(os.environ.get("WF_WORKERS", max(1, min(7, (os.cpu_count() or 2) - 1))))


def _rng(*key) -> np.random.Generator:
    """Deterministic per-task stream; keys are small ints ≥ −1 (side −1, null −1 = real fit)."""
    return np.random.default_rng([int(k) + 10 for k in key])


def _matrix(X_, rows, cols, perm):
    """X_[rows][:, cols], with the `perm` placebo applied: columns in perm[1] come from rows perm[0][rows]."""
    X = X_["X"][rows] if cols is None else X_["X"][rows][:, cols]
    if perm is not None:
        p, pcols = perm
        src = X_["X"][p[rows]]
        X[:, pcols] = src[:, cols][:, pcols] if cols is not None else src[:, pcols]
    return X


def _fit_task(task):
    name, qi, side, z = task
    D, E, h, bar_min, seed = _S["D"], _S["E"], _S["h"], _S["bar_min"], _S["seed"]
    cols, permD, permE = _S["specs"][name]
    q0, q1 = _S["qs"][qi], _S["qs"][qi + 1]
    rng = _rng(seed, qi, side, z)
    sm = (lambda X_: np.ones(len(X_["side"]), bool)) if side == 0 else (lambda X_: X_["side"] == side)
    tr = np.where(sm(D) & (_S["ftD"] + pd.Timedelta(minutes=bar_min * h) < q0) & np.isfinite(D["y"][h]))[0]
    if len(tr) > 400_000:
        tr = np.sort(rng.choice(tr, 400_000, replace=False))
    if len(tr) == 0:
        return task, None, {}
    yt = D["y"][h][tr].copy()
    lo, hi = np.quantile(yt, [0.005, 0.995])
    yt = np.clip(yt, lo, hi)
    if z >= 0:                                           # label null: permute the target within k
        kk = D["k"][tr]
        for kv in np.unique(kk):
            w = np.where(kk == kv)[0]
            yt[w] = yt[rng.permutation(w)]
    Xtr = _matrix(D, tr, cols, permD)
    edges, m = _fit_model(Xtr, yt, int(q0.value % 1e6) if z < 0 else z + 1000)
    del Xtr
    out = {}
    for u, X_, ft, perm in (("bt", D, _S["ftD"], permD), ("oos", E, _S["ftE"], permE)):
        te = np.where(sm(X_) & (ft >= q0) & (ft < q1))[0]
        if len(te):
            out[u] = (te, _predict(edges, m, _matrix(X_, te, cols, perm)).astype(np.float32))
    return task, ((q0, side, edges, m) if z < 0 else None), out


def run(D, E, h, specs: dict, nulls: int = 0, bar_min: int = 15, seed: int = 20261009) -> dict:
    """specs: {name: (cols | None, permD | None, permE | None)}; a perm is (row_permutation, cols_to_permute
    as positions within `cols`). Returns {name: dict(pred, npred, models)} like discover15.walk_forward."""
    ftD, ftE = pd.to_datetime(D["fill_time"]), pd.to_datetime(E["fill_time"])
    start = ftD.min() + pd.DateOffset(months=12)
    from grid_paths import SEAL
    qs = pd.date_range(start.to_period("Q").start_time, SEAL, freq="QS")
    sides = (0,) if D.get("pooled") else (1, -1)
    _S.update(D=D, E=E, h=h, bar_min=bar_min, seed=seed, specs=specs, qs=qs, ftD=ftD, ftE=ftE)
    tasks = [(nm, qi, s, z) for nm in specs for qi in range(len(qs) - 1) for s in sides for z in range(-1, nulls)]
    res = {nm: dict(pred={"bt": np.full(len(D["X"]), np.nan), "oos": np.full(len(E["X"]), np.nan)},
                    npred={u: np.full((nulls, len(X_["X"])), np.nan) for u, X_ in (("bt", D), ("oos", E))},
                    models=[]) for nm in specs}
    nw = workers()
    print(f"      h{h}: {len(tasks)} fits, engine {ENGINE}" + (f" on {nw} workers" if ENGINE == "np" else ""), flush=True)
    done = 0
    pool = mp.get_context("fork").Pool(nw) if ENGINE == "np" else None
    with (pool or _NoPool()) as pl:
        for (nm, qi, s, z), model, out in (pl.imap_unordered(_fit_task, tasks, chunksize=1) if pool else map(_fit_task, tasks)):
            for u, (te, p) in out.items():
                if z < 0:
                    res[nm]["pred"][u][te] = p
                else:
                    res[nm]["npred"][u][z, te] = p
            if model is not None:
                res[nm]["models"].append(model)
            done += 1
            if done % max(1, len(tasks) // 10) == 0:
                print(f"      h{h}: {done}/{len(tasks)} fits done", flush=True)
    for nm in specs:
        res[nm]["models"].sort(key=lambda m: (m[0], -m[1]))
    _S.clear()
    return res


class _NoPool:
    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _imp_task(i):
    D, h, names, cols, seed = _S["D"], _S["h"], _S["names"], _S["cols"], _S["seed"]
    q0, side, edges, m = _S["models"][i]
    rng = _rng(seed, i, 7)
    ft = _S["ft"]
    q1 = q0 + pd.DateOffset(months=3)
    te = np.where(((D["side"] == side) | (side == 0)) & (ft >= q0) & (ft < q1) & np.isfinite(D["y"][h]))[0]
    if len(te) < 5000:
        return None
    te = np.sort(rng.choice(te, min(len(te), 60_000), replace=False))
    X, y = _matrix(D, te, cols, None), D["y"][h][te]
    yr = pd.Series(y).rank()
    ic = lambda P: np.corrcoef(pd.Series(P).rank(), yr)[0, 1]
    b = ic(_predict(edges, m, X))
    drops = []
    for fi in range(len(names)):
        Xp = X.copy(); Xp[:, fi] = Xp[rng.permutation(len(Xp)), fi]
        drops.append(b - ic(_predict(edges, m, Xp)))
    return b, drops


def importance(D, h, R, names, cols=None, seed: int = 20261009) -> list[tuple[str, float]]:
    """discover15.importance, one model per worker. Returns features by mean IC drop, and prints the top 10."""
    _S.update(D=D, h=h, names=names, cols=cols, seed=seed, models=R["models"], ft=pd.to_datetime(D["fill_time"]))
    if ENGINE == "np":
        with mp.get_context("fork").Pool(workers()) as pool:
            out = [r for r in pool.map(_imp_task, range(len(R["models"]))) if r is not None]
    else:
        out = [r for r in map(_imp_task, range(len(R["models"]))) if r is not None]
    _S.clear()
    if not out:
        print("\n   importance: no quarter-side with ≥ 5000 test rows")
        return []
    base = [b for b, _ in out]
    mean = np.mean([d for _, d in out], axis=0)
    top = sorted(zip(names, mean), key=lambda kv: -kv[1])
    print(f"\n   out-of-sample rank IC (H={h}, BacktestCoins): mean {np.mean(base):+.4f} over {len(base)} quarter-sides; "
          f"positive in {np.mean(np.array(base) > 0):.0%}")
    print("   features the model leans on (IC drop when permuted):  " + "  ".join(f"{n} {v:+.4f}" for n, v in top[:10]))
    return top


def selftest() -> int:
    """Deterministic across worker counts, and recovers a planted signal out of sample (OosCoins-style
    universe never trained on), while a pure-noise target yields ~0 IC."""
    from grid_paths import SEAL
    rng = np.random.default_rng(3)

    def uni(n, planted):
        ft = SEAL - pd.to_timedelta(rng.integers(0, 3 * 365 * 24 * 4, n) * 15, unit="min")
        X = rng.normal(size=(n, 6)).astype(np.float32)
        y = (0.3 * (X[:, 0] > 0.5) * planted + rng.normal(0, 1, n)).astype(np.float32)
        return dict(X=X, y={16: y}, side=rng.choice([1, -1], n).astype(np.int8), k=rng.integers(0, 4, n),
                    fill_time=ft.values)
    D, E = uni(120_000, 1), uni(60_000, 1)
    spec = {"real": (None, None, None), "sub": ([1, 2, 3], None, None)}
    os.environ["WF_WORKERS"] = "1"
    r1 = run(D, E, 16, spec, nulls=1)
    os.environ["WF_WORKERS"] = "4"
    r4 = run(D, E, 16, spec, nulls=1)
    for nm in spec:
        for u in ("bt", "oos"):
            assert np.array_equal(r1[nm]["pred"][u], r4[nm]["pred"][u], equal_nan=True), f"{nm}/{u} depends on workers"
            assert np.array_equal(r1[nm]["npred"][u], r4[nm]["npred"][u], equal_nan=True)
    ok = np.isfinite(r4["real"]["pred"]["oos"])
    ic = lambda p: pd.Series(p[ok]).rank().corr(pd.Series(E["y"][16][ok]).rank())
    real, sub, null = ic(r4["real"]["pred"]["oos"]), ic(r4["sub"]["pred"]["oos"]), ic(r4["real"]["npred"]["oos"][0])
    assert real > 0.05 and abs(sub) < 0.02 and abs(null) < 0.02, (real, sub, null)
    print(f"wf_parallel selftest OK: identical at 1 and 4 workers; planted IC {real:+.3f}, "
          f"without the planted column {sub:+.3f}, label null {null:+.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(selftest())
