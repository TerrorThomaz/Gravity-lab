"""Disk cache for candidate builds (features + labels): compute once, reuse across runs and ablations.

Key = the build's name and arguments + the rng state at entry + the source of every module that shapes
the build + SEAL. Any code edit or a different candidate draw is a cache miss, never a stale hit. The
rng state AFTER the build is stored too and restored on a hit, so later draws (the next universe's
subsample) are identical to an uncached run.

Pickle is safe here: the files are written only by this module, into the local data/feature_cache
directory, and never come from anywhere else. Do not point DIR at a shared or downloaded location.

ponytail: whole-build granularity. A new feature block recomputes the whole build (~3 min/universe);
cache per column if feature blocks start to churn.
"""

from __future__ import annotations

import hashlib
import os
import pickle

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = ["discover15.py", "structure15.py", "struct_events.py", "indicators15.py", "grid_paths.py",
       "context_gate.py", os.path.join("..", "scripts", "market_neutral_research.py")]
DIR = os.path.join(os.path.dirname(os.path.realpath(os.path.join(HERE, "..", "candle_cache"))), "data", "feature_cache")


def cached(name: str, key: tuple, rng, fn):
    src = "".join(open(os.path.join(HERE, f)).read() for f in SRC if os.path.exists(os.path.join(HERE, f)))
    h = hashlib.sha256(repr((name, key, rng.bit_generator.state, os.environ.get("D15_MAXCOINS"))).encode()
                       + src.encode()).hexdigest()[:20]
    path = os.path.join(DIR, f"{name}_{h}.pkl")
    if os.path.exists(path):
        with open(path, "rb") as f:
            obj, state = pickle.load(f)
        rng.bit_generator.state = state
        print(f"   [cache] {name}: loaded {os.path.basename(path)}", flush=True)
        return obj
    obj = fn()
    os.makedirs(DIR, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        pickle.dump((obj, rng.bit_generator.state), f, protocol=5)
    os.replace(tmp, path)
    return obj
