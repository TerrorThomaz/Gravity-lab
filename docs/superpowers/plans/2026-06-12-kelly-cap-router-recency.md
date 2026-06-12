# Kelly Per-Position Cap + Router Fold Recency Weighting — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cap individual Kelly positions at 15% of portfolio balance and weight later training folds more heavily in the router GA so it generalises to recent regimes rather than overfitting past bull runs.

**Architecture:** Two independent changes in two files. `Simulator.cs` gets a tighter default per-position fraction cap (1.0 → 0.15) to bound single-position concentration within the existing 30% total-exposure system. `RegimeRouterGA.cs` replaces equal-weight fold averaging with recency-weighted averaging (later folds count more) in the `Fitness()` method. The router then needs one retrain cycle. No new genes, no schema changes, no new commands.

**Tech Stack:** C# .NET 8, existing GA framework, `dotnet run` CLI.

---

## Files

| Action | File | What changes |
|--------|------|-------------|
| Modify | `Simulator.cs:99` | `maxPositionFrac` default `1.0` → `0.15` |
| Modify | `Simulator.cs:183` | same for `SimulateWithDrawdownGuard` |
| Modify | `test.cs:474–495` | cap `HalfKelly` at `MaxKellyPerPositionFrac` in sweep-line loop |
| Modify | `RegimeRouterGA.cs:207–219` | replace equal-weight mean/std with recency-weighted version |

---

## Task 1: Kelly per-position cap in Simulator.cs

**Files:**
- Modify: `Simulator.cs:99` and `Simulator.cs:183`

### Context

`SimulatePortfolioExposureCapped` sizes each position as:
```csharp
double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
```
`maxPositionFrac` defaults to `1.0` (no cap), allowing a single SwingLong position with high Kelly conf to consume a large share of the 30% total budget before other positions can enter. Changing the default to `0.15` means a single position can claim at most 15% of portfolio balance, leaving headroom for concurrent positions from other strategies or coins.

All callers that already pass `maxPositionFrac: 0.05` (the 5%cap path) are **unaffected** — they pin their own cap explicitly. Only the Kelly callers that omit `maxPositionFrac` will change behaviour.

- [ ] **Step 1: Change `maxPositionFrac` default in the main overload**

In `Simulator.cs`, change line 99 from:
```csharp
        double maxPositionFrac     = 1.0)   // hard cap per position (e.g. 0.05 = 5% max each)
```
to:
```csharp
        double maxPositionFrac     = 0.15)  // hard cap per position (e.g. 0.05 = 5% max each)
```

- [ ] **Step 2: Change `maxPositionFrac` default in `SimulateWithDrawdownGuard`**

In `Simulator.cs`, change line 183 from:
```csharp
        double maxPositionFrac     = 1.0)
```
to:
```csharp
        double maxPositionFrac     = 0.15)
```

- [ ] **Step 3: Build to verify no compilation errors**

```bash
dotnet build -c Release
```
Expected: build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Simulator.cs
git commit -m "fix: cap Kelly per-position at 15% in SimulatePortfolioExposureCapped"
```

---

## Task 2: Update stress test synthetic worst-case display

**Files:**
- Modify: `test.cs:465–505`

### Context

`SyntheticWorstCase` in `test.cs` computes peak concurrent exposure by summing each trade's `HalfKelly` fraction in a sweep-line loop. This currently shows the uncapped raw demand (254.9%), which does not match what the portfolio simulation actually deploys after the Task 1 cap. The fix is to cap each trade's contribution at `0.15` before adding it to the sweep-line total, making the stress test display consistent with the actual simulation.

- [ ] **Step 5: Add cap constant and apply it in the sweep-line**

In `test.cs`, find the `SyntheticWorstCase` method (starts around line 466). The events list is built as:
```csharp
foreach (var t in trades)
{
    bool grid = t.Strategy == "grid";
    events.Add((t.Open,  +t.HalfKelly, grid));
    events.Add((t.Close, -t.HalfKelly, grid));
}
```

Change it to:
```csharp
const double MaxKellyPerPosition = 0.15;
foreach (var t in trades)
{
    bool   grid    = t.Strategy == "grid";
    double capped  = Math.Min(t.HalfKelly, MaxKellyPerPosition);
    events.Add((t.Open,  +capped, grid));
    events.Add((t.Close, -capped, grid));
}
```

- [ ] **Step 6: Build to verify no compilation errors**

```bash
dotnet build -c Release
```
Expected: build succeeds with 0 errors.

- [ ] **Step 7: Run fulltest and verify peak exposure is now ≤ 120%**

```bash
dotnet run -- fulltest 2>&1 | grep -A6 "Synthetic worst case"
```
Expected output: `Total exposure:` shows ≤ 120% (8 × 15%). Previously 254.9%.

- [ ] **Step 8: Verify Kelly DD did not worsen**

In the same fulltest output, check:
```
PORTFOLIO SIMULATION (€100 start · 30% total cap)
```
Expected: Val Kelly DD remains ≤ 2%, OOS Kelly DD remains ≤ 5%.

- [ ] **Step 9: Commit**

```bash
git add test.cs
git commit -m "fix: cap synthetic worst-case exposure at 15% per position to match sim"
```

---

## Task 3: Router fold recency weighting in RegimeRouterGA.cs

**Files:**
- Modify: `RegimeRouterGA.cs:207–219`

### Context

The current fitness aggregation in `Fitness()` is:
```csharp
double mean = scores.Average();
double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
return mean - 0.75 * std;
```

This weights all 5 time folds equally. The first 3 folds cover the dominant 2023–2024 bull run, so the router learns aggressive bull-pass thresholds that hurt performance in the more recent mixed/bear regime. Replacing the average with a recency-weighted average (folds 4–5 count more) pushes the learned thresholds to generalise toward the latest regime.

The weights `[1.0, 1.0, 1.0, 1.5, 2.0]` are fixed training-objective constants, not evolved genes. The variance term is also recency-weighted so inconsistency in recent folds is penalised more heavily.

- [ ] **Step 10: Add the `FoldWeights` constant**

At the top of the `RegimeRouterGA` class body (near the `MinTrades` constant, around line 31), add:

```csharp
    // Recency-weighted fold scoring: later folds (more recent market regime) count more.
    // Weights are applied in time order (index 0 = oldest fold, last = most recent).
    private static readonly double[] FoldWeights = [1.0, 1.0, 1.0, 1.5, 2.0];
```

- [ ] **Step 11: Replace equal-weight aggregation with weighted aggregation**

In `Fitness()`, replace lines 217–219:
```csharp
        double mean = scores.Average();
        double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
        return mean - 0.75 * std;
```

with:
```csharp
        var    weights      = FoldWeights.Take(folds).ToArray();
        double totalW       = weights.Sum();
        double weightedMean = scores.Select((s, i) => s * weights[i]).Sum() / totalW;
        double weightedVar  = scores.Select((s, i) => weights[i] * Math.Pow(s - weightedMean, 2)).Sum() / totalW;
        double weightedStd  = Math.Sqrt(weightedVar);
        return weightedMean - 0.75 * weightedStd;
```

- [ ] **Step 12: Build to verify no compilation errors**

```bash
dotnet build -c Release
```
Expected: build succeeds with 0 errors.

- [ ] **Step 13: Commit**

```bash
git add RegimeRouterGA.cs
git commit -m "feat: router fold recency weighting — later folds weighted 1.5× / 2.0×"
```

---

## Task 4: Retrain router and validate

### Context

The fitness function change affects what `routertrain` optimises for but does not change the saved `regime_router_genotype.json`. The current genotype was trained with equal-weight folds. It must be retrained to produce a router that actually uses the recency-weighted objective. After retraining, run `fulltest` to confirm the router val edge improves.

- [ ] **Step 14: Retrain the router**

```bash
dotnet run -- routertrain
```
Expected: completes and overwrites `regime_router_genotype.json`. The printed `F=` fitness value will differ from the previous value. Note it.

- [ ] **Step 15: Run fulltest and verify router val edge**

```bash
dotnet run -- fulltest 2>&1 | grep -A8 "ROUTER IMPACT"
```
Expected: `Val router edge: 5%cap` should be closer to 0pp or positive (was -3.6pp). OOS edge should remain strongly positive (was +268pp).

- [ ] **Step 16: Verify year-by-year 2025 bull improvement**

```bash
dotnet run -- fulltest 2>&1 | grep -A6 "YEAR-BY-YEAR"
```
Expected: 2025 bull row `5% w/router` should improve from +0.9% (direction matters more than magnitude).

- [ ] **Step 17: Verify peak exposure in stress test is still capped**

```bash
dotnet run -- fulltest 2>&1 | grep -A6 "Synthetic worst case"
```
Expected: `Total exposure:` ≤ 120%.

- [ ] **Step 18: Commit final results**

```bash
git add regime_router_genotype.json
git commit -m "chore: retrain router with recency-weighted fold fitness"
```

---

## Validation Checklist

After Task 4, confirm all of these in the `fulltest` output:

| Metric | Before | Expected after |
|--------|--------|----------------|
| Stress test peak exposure | 254.9% | ≤ 120% |
| Val Kelly DD | 1.7% | ≤ 2.0% (unchanged or lower) |
| OOS Kelly DD | 4.4% | ≤ 6.0% |
| Router val edge 5%cap | -3.6pp | improved (closer to 0 or positive) |
| OOS router edge 5%cap | +268pp | remains strongly positive |
| 2025 bull `5% w/router` | +0.9% | higher |
