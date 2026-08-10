#!/usr/bin/env bash
# Full retrain driver for the 2026-08 fitness/fold rework.
#
# Every genotype in genotypes/ was selected under an objective that has since been
# corrected (non-monotone fold aggregator, invisible slippage, inverted funding sign
# on the long strategies), so all of them need reselecting. See CLAUDE.md
# "GA fitness" and the caution blocks under "Saved genotype files".
#
# Usage:
#   scripts/retrain_all.sh                  # full chain, in dependency order
#   scripts/retrain_all.sh routertrain dynamicguardtrain   # just these, in this order
#   SEED=12345 scripts/retrain_all.sh       # pass --seed to every command
#
# Sequential by design: each GA already saturates all cores via Parallel.ForEach,
# so running two at once only costs cache and makes the logs interleave.
set -u
cd "$(dirname "$0")/.." || exit 1

LOGDIR="${LOGDIR:-logs/retrain-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$LOGDIR"
RESULTS="$LOGDIR/RESULTS.txt"

# Dependency order: strategies -> vol variants -> rotator -> router -> guard -> coevolve.
# Router consumes strategy trade lists; guard consumes the router-gated portfolio;
# coevolve co-adapts router<->guard against frozen strategies.
DEFAULT_CHAIN=(
  train gridtrain gridshorttrain fadelongtrain ripshorttrain
  diplongtrain swinglongtrain accumgridtrain
  lowvoltrain rotatortrain
  routertrain
  dynamicguardtrain
  coevolvetrain
)
CHAIN=("${@:-}")
[ -z "${1:-}" ] && CHAIN=("${DEFAULT_CHAIN[@]}")

# A second concurrent run silently races on the same genotypes/*.json and both
# writers "win" unpredictably. Observed for real: two `train` processes at 347% and
# 490% CPU writing one file. Refuse rather than corrupt.
if pgrep -f 'bin/Release/net10.0/Gravity-gen2 ' >/dev/null 2>&1; then
  echo "REFUSING: a Gravity-gen2 worker is already running:" >&2
  pgrep -af 'bin/Release/net10.0/Gravity-gen2 ' >&2
  exit 1
fi

# Genotypes are tracked, but `git checkout` only recovers what was COMMITTED, and a
# GA run overwrites in place. Snapshot uncommitted state before touching anything.
cp -r genotypes "$LOGDIR/genotypes_before" || exit 1

dotnet build -c Release >"$LOGDIR/build.log" 2>&1 || { echo "BUILD FAILED, see $LOGDIR/build.log" >&2; exit 1; }

echo "retrain chain: ${CHAIN[*]}" | tee "$RESULTS"
echo "log dir: $LOGDIR" | tee -a "$RESULTS"

for cmd in "${CHAIN[@]}"; do
  echo "=== START $cmd $(date -Is) ===" | tee -a "$RESULTS"
  t0=$SECONDS
  dotnet run -c Release -- "$cmd" ${SEED:+--seed "$SEED"} >"$LOGDIR/$cmd.log" 2>&1
  rc=$?
  echo "=== DONE  $cmd rc=$rc $(( (SECONDS-t0)/60 ))min ===" | tee -a "$RESULTS"
  # Seeds are printed unconditionally by GaSearch.AnnounceSeed. Hoist them into the
  # summary: a run whose seed was never recorded cannot be reproduced or rolled back to.
  grep -h '\[seed\]' "$LOGDIR/$cmd.log" 2>/dev/null | sed 's/^ */    /' | tee -a "$RESULTS"
  [ $rc -ne 0 ] && echo "    ^^ FAILED — downstream stages may train against a stale genotype" | tee -a "$RESULTS"
done

echo "ALL DONE $(date -Is)" | tee -a "$RESULTS"
echo | tee -a "$RESULTS"
echo "changed genotypes:" | tee -a "$RESULTS"
for f in genotypes/*.json; do
  cmp -s "$f" "$LOGDIR/genotypes_before/$(basename "$f")" || echo "    $f" | tee -a "$RESULTS"
done
echo | tee -a "$RESULTS"
echo "Next: validate with 'dotnet run -c Release -- combinedbacktest' and 'oosbacktest'." | tee -a "$RESULTS"
echo "Pre-retrain genotypes preserved at $LOGDIR/genotypes_before" | tee -a "$RESULTS"
