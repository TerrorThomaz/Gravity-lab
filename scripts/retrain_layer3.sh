#!/usr/bin/env bash
# Retrain every strategy with Layer 3 (participation impact) charged.
#
# PRECONDITION — read before running:
#   Every simulator's call sites must actually FEED barNotional to TradeCost. A simulator whose
#   call sites pass the defaults charges no impact, so GRAVITY_IMPACT=1 is inert for it and its
#   "Layer 3 retrain" is really just a plain retrain under a fresh seed. Verify with:
#       grep -c 'EntryBarNotional' src/strategies/*/*Simulator.cs
#   Any file returning 0 is not wired.
#
# RECOVERY — the pre-Layer-3 set is tagged, so a worse successor is one command away:
#       git checkout pre-layer3-genotypes -- genotypes/
#   Per strategy:
#       git checkout pre-layer3-genotypes -- genotypes/<file>.json
#
# Each genotype is ALSO copied aside before its own retrain, because GA runs are non-deterministic
# and a rerun can land in a worse basin (observed: RipShort held-out PF fell to 0.95 after a
# routine retrain). Committed history protects what was committed; these copies protect the rest.
set -uo pipefail
cd "$(dirname "$0")/.."

export GRAVITY_IMPACT=1

STAMP=$(date +%Y%m%d_%H%M%S)
BACKUP="genotypes/_pre_layer3_${STAMP}"
mkdir -p "$BACKUP" logs
cp genotypes/*.json "$BACKUP"/ 2>/dev/null
echo "Backed up current genotypes → $BACKUP"

# Guard against a second instance: two GAs writing the same genotype file interleave and the
# survivor is whichever finished last, which is silent and unreproducible.
if pgrep -f 'dotnet exec.*Gravity' >/dev/null 2>&1; then
    echo "REFUSING: a dotnet run is already active. Concurrent GAs overwrite each other's genotypes."
    exit 1
fi

# Build ONCE, up front. Building while a run is live swaps the binary out from under it — obj/ is
# shared across configurations, so a Debug build is NOT safe either. Two runs were lost to this.
echo "Building (once — no builds after this point)..."
dotnet build -c Release 2>&1 | grep -E 'error CS' && { echo "BUILD FAILED"; exit 1; }

run () {   # run <command> <log-suffix>
    local cmd="$1" tag="$2"
    local log="logs/l3_${tag}_${STAMP}.log"
    echo "── $cmd → $log"
    if dotnet run -c Release --no-build -- "$cmd" > "$log" 2>&1; then
        grep -E 'Saved →|Frozen genotype' "$log" | tail -2
    else
        echo "   FAILED (exit $?) — see $log"
    fi
}

run train           fadeshort
run gridtrain       grid
run gridshorttrain  gridshort
run fadelongtrain   fadelong
run ripshorttrain   ripshort
run diplongtrain    diplong
run swinglongtrain  swinglong
run accumgridtrain  accumgrid

echo
echo "Done. Compare each against the pre-Layer-3 set on OOS before accepting:"
echo "  dotnet run -c Release -- oosbacktest"
echo "  git checkout pre-layer3-genotypes -- genotypes/<file>.json   # to revert one"
