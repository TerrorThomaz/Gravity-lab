#!/usr/bin/env bash
# Retrain ONLY the two trainers that actually leaked.
#
# WHY THIS IS TWO GENOTYPES AND NOT ELEVEN
#   Every pre-centralisation trainer cut its window as a PREFIX of the series: 0.75
#   (TrainCommands, BacktestCommands, CoevolveGA) or 0.80 (LongTrainCommands,
#   LowVolTrainCommands, RegimeRouterGA). DataSplit.TrainFraction is now 0.80 — the largest
#   prefix any of them consumed — so validation starts at a bar NONE of them ever trained on.
#   The 0.75 trainers saw 5% less data than they could have. That is a sample-size difference,
#   not a validity one, and it does not justify discarding a selected genotype.
#
#   VolatilityWeightedRotatorGA and DynamicGuardGA had no split at all. They fit on 1.00 of the
#   series, including every bar the backtests report as validation. Those two are leakage by
#   construction and are the only ones that must be thrown away.
#
# ORDER: guard before rotator — the rotator's safety score reads the guard's multiplier.
#
# RECOVERY:
#     git checkout pre-split-retrain -- genotypes/vol_rotator_genotype.json
#     git checkout pre-split-retrain -- genotypes/dynamic_guard_genotype.json
set -uo pipefail
cd "$(dirname "$0")/.."

STAMP=$(date +%Y%m%d_%H%M%S)
mkdir -p logs reports

if pgrep -f 'dotnet exec.*Gravity' >/dev/null 2>&1; then
    echo "REFUSING: a dotnet run is already active. Concurrent GAs overwrite each other's genotypes."
    exit 1
fi

# Build ONCE. obj/ is shared across configurations, so a Debug build during a run is NOT safe
# either — it swaps the binary out from under the live process. Three runs were lost to this.
echo "Building once (no builds after this point)..."
if dotnet build -c Release 2>&1 | grep -qE 'error CS'; then echo "BUILD FAILED"; exit 1; fi

run () {
    local cmd="$1" tag="$2"; shift 2
    local log="logs/leak_${tag}_${STAMP}.log"
    printf '── %-20s → %s\n' "$cmd" "$log"
    if dotnet run -c Release --no-build -- "$cmd" "$@" > "$log" 2>&1; then
        grep -E 'Saved →' "$log" | tail -2
    else
        echo "   FAILED (exit $?) — see $log"
    fi
}

run dynamicguardtrain guard
run rotatortrain      rotator

echo
echo "Validate — every command below now grades its book through StrategyEvaluation:"
echo "  dotnet run -c Release -- oosbacktest    # never-trained coins — the clean arbiter"
echo "  dotnet run -c Release -- fulltest       # val + OOS side by side"
