# Design: Kelly Per-Position Cap + Router Fold Recency Weighting

**Date:** 2026-06-12
**Status:** Approved

## Problem Summary

Two independent issues identified from `fulltest` output:

1. **Kelly tail risk** — the stress test shows 254.9% unconstrained notional demand from concurrent SwingLong positions at peak (2026-01-04). In live trading (`papertrade`), if Kelly sizing is applied per-strategy independently without cross-strategy coordination, a crash at this moment produces a -37.5% portfolio hit (3× ATR expansion scenario). The portfolio simulation's 30% total cap uses a first-come-first-served headroom model, so simultaneous opens from correlated strategies can still produce outsized individual allocations.

2. **Router overfit to past bull runs** — the router costs -3.6pp in val (5%cap) and -2.5pp in the 2025 bull year specifically. The 5-fold router fitness uses equal-weight fold averaging, so the first 3 folds (which cover the dominant 2023–2024 bull run) over-determine the router's learned thresholds. The router learns to pass many bull trades through aggressively and generalizes poorly to the more recent mixed/bear regime.

## Change 1: Kelly Per-Position Cap in SimulatePortfolioExposureCapped

**File:** `Simulator.cs`

**What changes:** Add a `maxPerPositionFrac` parameter (default `0.15`) to `SimulatePortfolioExposureCapped`. The position size computation becomes:

```csharp
double desiredFrac = Math.Min(conf * kellyMultiplier, maxPerPositionFrac);
```

This caps any single position at 15% of portfolio balance regardless of how high the Kelly-derived confidence is. With 8 concurrent SwingLong positions at 15% each, total demand is 120% — still aggressive but bounded vs. the prior 254.9%.

**Why 15%:** SwingLong's per-trade Kelly fraction derives from Sharpe ~98, which yields unrealistically high Kelly values. A 15% per-trade cap limits the worst-case concurrent exposure to a survivable level while preserving confidence-based relative sizing between strategies and individual trades.

**What does NOT change:** The total exposure headroom mechanism (`maxTotalExposurePct`), the `ddScale` drawdown brake, the guard multiplier paths. These remain unchanged.

**Callsites to update:** All callers of `SimulatePortfolioExposureCapped` that currently pass `maxPositionFrac: 1.0` (or omit it, using the default) should be updated to use the new default of `0.15`. The 5%cap path already passes `maxPositionFrac: 0.05` and is unaffected.

**Stress test output:** Update the synthetic worst-case calculation in `FullTest.cs` / stress test section to reflect the capped per-position size rather than raw conf, so it accurately reflects what the portfolio sim actually deploys.

## Change 2: Router Fold Recency Weighting in RegimeRouterGA

**File:** `RegimeRouterGA.cs`

**What changes:** In `Fitness()`, replace the equal-weight average with a recency-weighted average. The weights are fixed constants:

```csharp
private static readonly double[] FoldWeights = [1.0, 1.0, 1.0, 1.5, 2.0];
```

Weighted fitness:

```csharp
double totalW        = FoldWeights.Take(folds).Sum();
double weightedMean  = scores.Select((s, i) => s * FoldWeights[i]).Sum() / totalW;
double weightedVar   = scores.Select((s, i) => FoldWeights[i] * Math.Pow(s - weightedMean, 2)).Sum() / totalW;
double weightedStd   = Math.Sqrt(weightedVar);
return weightedMean - 0.75 * weightedStd;
```

**Why these weights:** The 2× weight on the last fold and 1.5× on the second-to-last doubles the signal from the most recent ~1.2yr of data without completely discarding the earlier folds. The penalty term (`0.75 × std`) is also recency-weighted so the router is penalised more for inconsistency in recent folds. The weights are not evolved — they are a fixed training objective, not a hyperparameter.

**What does NOT change:** The `Fitness()` signature, the `ScorePortfolio` function, the fold count (5), the `FilterActive` logic, or the `BayesianOptimizer` post-GA refinement step. The change is isolated to the aggregation of fold scores.

**Expected effect:** The router's learned `BullMinConf` and `BullMinBars` thresholds should become less aggressive (allow more bull trades through) when the recent regime is mixed or bearish, and more conservative when the recent regime is a strong bull. Val performance in the 2025–2026 window should improve.

## Scope

- No new genes, no new genotype fields, no new training commands.
- No changes to strategy simulators (FadeShort, DipLong, SwingLong, Grid, FadeLong).
- No changes to the coevolution loop structure.
- FadeLong and Exit Modifier are left as-is (0-trade issue is a separate concern, not part of this change).

## Validation

After implementation, run `dotnet run -- fulltest` and verify:
1. Stress test peak exposure is ≤ 120% (8 × 15%) rather than 254.9%.
2. Val Kelly DD remains ≤ 2% (should be lower with the cap).
3. Router val edge at 5%cap improves from -3.6pp toward 0 or positive after `routertrain`.
4. OOS router edge remains strongly positive (the recency weighting should not hurt OOS).
5. 2025 bull year-by-year val return improves from +0.9%.
