"""A small histogram gradient-boosting regressor in numpy (squared loss), for research only.

Why not sklearn: it isn't installed, and this is ~80 lines. Trees grow level-wise to `depth`, using
one bincount per feature per level for all nodes at once, so a 300k × 35 fit takes seconds.
Regularisation: min_leaf rows, L2 on leaf values, row and feature subsampling, shrinkage.
"""

from __future__ import annotations

import numpy as np

BINS = 32


def bin_edges(X: np.ndarray, bins: int = BINS) -> list[np.ndarray]:
    qs = np.linspace(0, 1, bins + 1)[1:-1]
    return [np.unique(np.nanquantile(X[:, f], qs)) for f in range(X.shape[1])]


def apply_bins(X: np.ndarray, edges: list[np.ndarray]) -> np.ndarray:
    """uint8 codes; NaN gets its own code (BINS) so trees can route missing values."""
    out = np.empty(X.shape, dtype=np.uint8)
    for f, e in enumerate(edges):
        col = X[:, f]
        b = np.searchsorted(e, col, side="right").astype(np.uint8)
        b[np.isnan(col)] = BINS
        out[:, f] = b
    return out


class Gbm:
    def __init__(self, depth=3, rounds=150, lr=0.05, min_leaf=1000, l2=10.0, row_sub=0.5, col_sub=0.7, seed=0):
        self.depth, self.rounds, self.lr, self.min_leaf, self.l2 = depth, rounds, lr, min_leaf, l2
        self.row_sub, self.col_sub, self.rng = row_sub, col_sub, np.random.default_rng(seed)
        self.trees: list[tuple[np.ndarray, np.ndarray, np.ndarray]] = []   # (feat, thresh) per level-node, leaf values
        self.base = 0.0

    def _grow(self, Xb, r):
        n, p = Xb.shape
        B = BINS + 1
        node = np.zeros(n, dtype=np.int64)
        feats, ths = [], []
        cols = self.rng.choice(p, max(1, int(self.col_sub * p)), replace=False)
        for lvl in range(self.depth):
            nn = 2 ** lvl
            best_gain = np.zeros(nn); best_f = np.full(nn, -1); best_t = np.zeros(nn, dtype=np.int64)
            G = np.bincount(node, weights=r, minlength=nn)
            N = np.bincount(node, minlength=nn).astype(float)
            parent = G ** 2 / (N + self.l2)
            for f in cols:
                idx = node * B + Xb[:, f]
                g = np.bincount(idx, weights=r, minlength=nn * B).reshape(nn, B)
                c = np.bincount(idx, minlength=nn * B).reshape(nn, B).astype(float)
                gl, cl = np.cumsum(g, 1)[:, :-1], np.cumsum(c, 1)[:, :-1]      # left = bins ≤ t (NaN code stays right)
                gr, cr = G[:, None] - gl, N[:, None] - cl
                gain = gl ** 2 / (cl + self.l2) + gr ** 2 / (cr + self.l2) - parent[:, None]
                gain[(cl < self.min_leaf) | (cr < self.min_leaf)] = 0
                t = gain.argmax(1); gmax = gain[np.arange(nn), t]
                upd = gmax > best_gain
                best_gain[upd], best_f[upd], best_t[upd] = gmax[upd], f, t[upd]
            feats.append(best_f.copy()); ths.append(best_t.copy())
            go_right = np.where(best_f[node] >= 0, Xb[np.arange(n), np.maximum(best_f[node], 0)] > best_t[node], False)
            node = node * 2 + go_right
        nl = 2 ** self.depth
        G = np.bincount(node, weights=r, minlength=nl); N = np.bincount(node, minlength=nl)
        return np.array(feats, dtype=object), np.array(ths, dtype=object), G / (N + self.l2)

    @staticmethod
    def _route(Xb, feats, ths):
        node = np.zeros(len(Xb), dtype=np.int64)
        for f, t in zip(feats, ths):
            fn, tn = f[node], t[node]
            node = node * 2 + np.where(fn >= 0, Xb[np.arange(len(Xb)), np.maximum(fn, 0)] > tn, False)
        return node

    def fit(self, Xb: np.ndarray, y: np.ndarray) -> "Gbm":
        self.base = float(y.mean())
        pred = np.full(len(y), self.base)
        for _ in range(self.rounds):
            rows = self.rng.random(len(y)) < self.row_sub
            feats, ths, leaf = self._grow(Xb[rows], (y - pred)[rows])
            self.trees.append((feats, ths, leaf))
            pred += self.lr * leaf[self._route(Xb, feats, ths)]
        return self

    def predict(self, Xb: np.ndarray) -> np.ndarray:
        out = np.full(len(Xb), self.base)
        for feats, ths, leaf in self.trees:
            out += self.lr * leaf[self._route(Xb, feats, ths)]
        return out


def selftest() -> int:
    rng = np.random.default_rng(1)
    n = 200_000
    X = rng.normal(size=(n, 6))
    X[rng.random(n) < 0.05, 2] = np.nan
    y = 0.5 * ((X[:, 0] > 0.5) & (X[:, 1] < 0)) + rng.normal(0, 1, n)       # planted interaction, buried in noise
    e = bin_edges(X[: n // 2]); Xb = apply_bins(X, e)
    m = Gbm(rounds=100, min_leaf=500).fit(Xb[: n // 2], y[: n // 2])
    p = m.predict(Xb[n // 2:]); yt = y[n // 2:]
    top = yt[p > np.quantile(p, 0.8)].mean() - yt.mean()
    assert top > 0.2, f"planted interaction not recovered out of sample: top-quintile lift {top:.3f}"
    yn = rng.normal(0, 1, n)                                              # pure noise: no lift out of sample
    mn = Gbm(rounds=100, min_leaf=500).fit(Xb[: n // 2], yn[: n // 2])
    pn = mn.predict(Xb[n // 2:])
    lift = yn[n // 2:][pn > np.quantile(pn, 0.8)].mean() - yn[n // 2:].mean()
    assert abs(lift) < 0.03, f"noise produced out-of-sample lift {lift:.3f}"
    print(f"gbm selftest: OK (planted lift {top:+.3f} recovered OOS; noise lift {lift:+.3f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(selftest())
