# Variant Router + Configurable Fitness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-strategy variant training (ATR-regime scoped, configurable fitness weights), a per-coin ATR-based variant selector, JSON output logging for the frontend, and a FastAPI server to trigger training from the web dashboard.

**Architecture:** `FitnessConfig` record carries all GA fitness weights + ATR range; each GA reads it from `fitness_config.json` at startup. `VariantRouter` computes per-bar ATR ratio from pre-computed ATR arrays and selects the correct genotype. All train commands gain `--variant <id>` to save variant-specific genotype files. Four JSON files (`backtest_results`, `training_progress`, `fulltest_results`, `live_journal`) feed the frontend. FastAPI serves static files and streams training stdout via SSE.

**Tech Stack:** C# .NET 10, xunit 2.x, Python 3 / FastAPI / uvicorn, React 18 + Babel standalone (frontend)

## Global Constraints

- Working directory for all `dotnet` commands: `/home/thomas/Gravity-gen2`
- Build: `dotnet build -c Release` — must pass after every task
- Tests: `dotnet test Gravity-gen2.Tests/` — must stay green after every task
- Namespace: `TradingGA` throughout — do not change
- Default variant id: `"default"` — existing genotype file paths in `Config.cs` unchanged
- New variant files: `genotypes/{strategy}_{variant}_genotype.json` (e.g. `genotypes/fade_short_hival_genotype.json`)
- `FitnessConfig` new-weight defaults are **0.0** for SharpeW/CalmarW/PfW/SortinoW — preserves existing training behaviour for default variant
- `fitness_config.json` lives at repo root; gitignored; overwritten each run
- `VariantRouter.VolCoverage` takes pre-computed `double[] atr`, not raw candles — avoids redundant computation
- ATR baseline window: 100 bars; ATR period: 14 (matches existing GA ATR usage)

---

## File Map

| Action | File | What changes |
|---|---|---|
| Create | `src/core/FitnessConfig.cs` | New record + `Load()` |
| Modify | `src/core/GenotypeDto.cs` | Add `AtrLow`/`AtrHigh` to all 5 DTOs |
| Create | `src/core/VariantRouter.cs` | `VolCoverage` + `Select<T>` |
| Modify | `src/strategies/fade_short/FadeShortGA.cs` | Add `_cfg` field, extend `FoldScore` |
| Modify | `src/strategies/grid/GridGA.cs` | Add `_cfg` field, extend `FoldScore` |
| Modify | `src/strategies/swing_long/SwingLongGA.cs` | Add `_cfg` field, extend `FoldScore` |
| Modify | `src/strategies/dip_long/DipLongGA.cs` | Add `_cfg` field, extend `FoldScore` |
| Modify | `src/strategies/fade_long/FadeLongGA.cs` | Add `_cfg` field, extend `FoldScore` |
| Modify | `commands/TrainCommands.cs` | Parse `--variant`, load `FitnessConfig`, variant save path |
| Modify | `commands/LongTrainCommands.cs` | Same for all long train commands |
| Modify | `commands/Program.cs` | Pass `args` to train commands |
| Modify | `commands/CombinedBacktest.cs` | Load all variant genotypes, write `backtest_results.json` |
| Modify | `commands/OosBacktest.cs` | Same |
| Modify | `commands/FullTest.cs` | Write `fulltest_results.json` |
| Modify | `commands/PapertradeCommands.cs` | VariantRouter, append `live_journal.json` |
| Create | `bot/api_server.py` | FastAPI: static files + `/api/train` SSE + `/api/stop` + `/api/status` |
| Create | `frontend/` (all files) | Copy prototype files from zip |
| Modify | `frontend/store.js` | Add `atrRange`, training API calls |
| Modify | `.gitignore` | Add runtime output JSON files |
| Create | `Gravity-gen2.Tests/core/FitnessConfigTests.cs` | Unit tests |
| Create | `Gravity-gen2.Tests/core/VariantRouterTests.cs` | Unit tests |

---

## Task 1: FitnessConfig record

**Files:**
- Create: `src/core/FitnessConfig.cs`
- Create: `Gravity-gen2.Tests/core/FitnessConfigTests.cs`

**Interfaces:**
- Produces: `FitnessConfig` record with `Load(path)` static method; used by every GA in later tasks

- [ ] **Step 1: Write the failing tests**

```csharp
// Gravity-gen2.Tests/core/FitnessConfigTests.cs
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class FitnessConfigTests
{
    [Fact]
    public void Defaults_AllWeightsCorrect()
    {
        var cfg = new FitnessConfig();
        Assert.Equal("default", cfg.VariantId);
        Assert.Equal(1.0,   cfg.GainW);
        Assert.Equal(0.9,   cfg.WrW);
        Assert.Equal(0.8,   cfg.QualityW);
        Assert.Equal(0.6,   cfg.FreqW);
        Assert.Equal(1.2,   cfg.DdPenalty);
        Assert.Equal(1.0,   cfg.RetentionW);
        Assert.Equal(0.0,   cfg.SharpeW);
        Assert.Equal(0.0,   cfg.CalmarW);
        Assert.Equal(0.0,   cfg.PfW);
        Assert.Equal(0.0,   cfg.SortinoW);
        Assert.Equal(0.0,   cfg.AtrLow);
        Assert.Equal(9999.0,cfg.AtrHigh);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = FitnessConfig.Load("nonexistent_file_xyz.json");
        Assert.Equal(new FitnessConfig(), cfg);
    }

    [Fact]
    public void Load_ValidJson_OverridesFields()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{"SharpeW":0.7,"CalmarW":0.9,"AtrLow":1.5,"AtrHigh":9999}""");
        var cfg = FitnessConfig.Load(path);
        File.Delete(path);
        Assert.Equal(0.7,  cfg.SharpeW);
        Assert.Equal(0.9,  cfg.CalmarW);
        Assert.Equal(1.5,  cfg.AtrLow);
        Assert.Equal(1.0,  cfg.GainW);   // unchanged default
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
dotnet test Gravity-gen2.Tests/ -v 2>&1 | grep -E "FAIL|PASS|Error|FitnessConfig"
```
Expected: compile error — `FitnessConfig` not found.

- [ ] **Step 3: Create FitnessConfig.cs**

```csharp
// src/core/FitnessConfig.cs
using System.Text.Json;

namespace TradingGA;

public record FitnessConfig(
    string VariantId  = "default",
    double GainW      = 1.0,
    double WrW        = 0.9,
    double QualityW   = 0.8,
    double FreqW      = 0.6,
    double DdPenalty  = 1.2,
    double RetentionW = 1.0,
    double SharpeW    = 0.0,   // 0 = inactive; frontend suggests 0.7 for new variants
    double CalmarW    = 0.0,
    double PfW        = 0.0,
    double SortinoW   = 0.0,
    double AtrLow     = 0.0,
    double AtrHigh    = 9999.0
)
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    public static FitnessConfig Load(string path = "fitness_config.json")
    {
        if (!File.Exists(path)) return new FitnessConfig();
        return JsonSerializer.Deserialize<FitnessConfig>(File.ReadAllText(path), _opts)
               ?? new FitnessConfig();
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test Gravity-gen2.Tests/ -v 2>&1 | grep -E "FAIL|PASS|FitnessConfig"
```
Expected: 3 tests PASS.

- [ ] **Step 5: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
git add src/core/FitnessConfig.cs Gravity-gen2.Tests/core/FitnessConfigTests.cs
git commit -m "feat: FitnessConfig record with Load() for variant fitness weights"
```

---

## Task 2: VariantRouter + GenotypeDto AtrLow/AtrHigh

**Files:**
- Create: `src/core/VariantRouter.cs`
- Modify: `src/core/GenotypeDto.cs` (add `AtrLow`/`AtrHigh` to all 5 DTOs)
- Create: `Gravity-gen2.Tests/core/VariantRouterTests.cs`

**Interfaces:**
- Consumes: `FitnessConfig` (AtrLow, AtrHigh fields); `Indicators.Atr()` (internal, same assembly)
- Produces:
  - `VariantRouter.VolCoverage(double[] atr, int start, int end, double atrLow, double atrHigh) -> double`
  - `VariantRouter.Select<T>(double[] atr, int barIndex, VariantSpec<T>[] variants) -> T?`
  - `VariantSpec<T>` record

- [ ] **Step 1: Write the failing tests**

```csharp
// Gravity-gen2.Tests/core/VariantRouterTests.cs
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class VariantRouterTests
{
    // Flat ATR array: all ratios = 1.0, so [0, 9999] covers 100% of bars
    private static double[] FlatAtr(int n, double val = 1.0)
        => Enumerable.Repeat(val, n).ToArray();

    [Fact]
    public void VolCoverage_DefaultRange_ReturnsOne()
    {
        var atr = FlatAtr(200);
        double cov = VariantRouter.VolCoverage(atr, 100, 200, 0.0, 9999.0);
        Assert.Equal(1.0, cov, precision: 6);
    }

    [Fact]
    public void VolCoverage_NoBarInRange_ReturnsZero()
    {
        // All ATR values equal baseline → ratio = 1.0; range is [2.0, 9999]
        var atr = FlatAtr(200);
        double cov = VariantRouter.VolCoverage(atr, 100, 200, 2.0, 9999.0);
        Assert.Equal(0.0, cov, precision: 6);
    }

    [Fact]
    public void Select_NoVariantsWithGenotype_ReturnsNull()
    {
        var variants = new[]
        {
            new VariantSpec<string>("hival", 1.5, 9999.0, null)
        };
        // flat ATR → ratio 1.0 → below 1.5 → no match
        var atr = FlatAtr(200);
        string? result = VariantRouter.Select(atr, 150, variants);
        Assert.Null(result);
    }

    [Fact]
    public void Select_DefaultVariant_MatchesWhenNothingElseDoes()
    {
        var variants = new[]
        {
            new VariantSpec<string>("hival",   1.5, 9999.0, null),
            new VariantSpec<string>("default", 0.0, 9999.0, "myGenotype"),
        };
        var atr = FlatAtr(200);
        string? result = VariantRouter.Select(atr, 150, variants);
        Assert.Equal("myGenotype", result);
    }

    [Fact]
    public void Select_TightestRangeWins()
    {
        var variants = new[]
        {
            new VariantSpec<string>("default", 0.0, 9999.0, "broad"),
            new VariantSpec<string>("hival",   0.9, 1.1,    "tight"),  // ratio=1.0 is inside
        };
        var atr = FlatAtr(200);
        string? result = VariantRouter.Select(atr, 150, variants);
        Assert.Equal("tight", result);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
dotnet test Gravity-gen2.Tests/ -v 2>&1 | grep -E "FAIL|PASS|Error|VariantRouter"
```
Expected: compile error — `VariantRouter` not found.

- [ ] **Step 3: Create VariantRouter.cs**

```csharp
// src/core/VariantRouter.cs
namespace TradingGA;

public record VariantSpec<T>(string Id, double AtrLow, double AtrHigh, T? Genotype)
    where T : class;

public static class VariantRouter
{
    private const int AtrPeriod   = 14;
    private const int AtrBaseline = 100;

    // Fraction of bars in [start, end) where ATR_ratio falls in [atrLow, atrHigh).
    // ATR_ratio[i] = atr[i] / mean(atr[i-AtrBaseline .. i-1]).
    // Bars with insufficient baseline history are excluded from the count.
    public static double VolCoverage(double[] atr, int start, int end, double atrLow, double atrHigh)
    {
        int total = 0, inside = 0;
        for (int i = Math.Max(start, AtrBaseline); i < end; i++)
        {
            double baseline = 0;
            for (int j = i - AtrBaseline; j < i; j++) baseline += atr[j];
            baseline /= AtrBaseline;
            if (baseline < 1e-10) continue;
            double ratio = atr[i] / baseline;
            total++;
            if (ratio >= atrLow && ratio < atrHigh) inside++;
        }
        return total == 0 ? 1.0 : (double)inside / total;
    }

    // Returns the genotype of the best matching variant, or null to abstain.
    // "Best" = tightest AtrHigh - AtrLow among variants whose range contains
    // the ATR ratio at barIndex and whose Genotype is non-null.
    // Requires barIndex >= AtrBaseline.
    public static T? Select<T>(double[] atr, int barIndex, VariantSpec<T>[] variants)
        where T : class
    {
        if (barIndex < AtrBaseline || atr.Length <= barIndex) return null;

        double baseline = 0;
        for (int j = barIndex - AtrBaseline; j < barIndex; j++) baseline += atr[j];
        baseline /= AtrBaseline;
        if (baseline < 1e-10) return null;
        double ratio = atr[barIndex] / baseline;

        VariantSpec<T>? best = null;
        double bestWidth = double.MaxValue;
        foreach (var v in variants)
        {
            if (v.Genotype == null) continue;
            if (ratio < v.AtrLow || ratio >= v.AtrHigh) continue;
            double width = v.AtrHigh - v.AtrLow;
            if (width < bestWidth) { bestWidth = width; best = v; }
        }
        return best?.Genotype;
    }
}
```

- [ ] **Step 4: Add `AtrLow`/`AtrHigh` to all 5 GenotypeDto classes in `src/core/GenotypeDto.cs`**

For each of `FadeShortGenotypeDto`, `FadeLongGenotypeDto`, `DipLongGenotypeDto`, `SwingLongGenotypeDto`, `GridGenotypeDto`, add two properties after `Fitness`:

```csharp
public double AtrLow  { get; init; } = 0.0;
public double AtrHigh { get; init; } = 9999.0;
```

Also update each DTO's `From()` factory to copy the fields. Since the current genotypes don't have these fields, `From()` should set them from the `FitnessConfig` passed at save time. Simplest: add optional parameters to `From()`:

```csharp
// Example for FadeShortGenotypeDto.From():
public static FadeShortGenotypeDto From(FadeShortGenotype g, FitnessConfig? cfg = null) => new()
{
    // ... existing fields ...
    Fitness  = g.Fitness,
    AtrLow   = cfg?.AtrLow  ?? 0.0,
    AtrHigh  = cfg?.AtrHigh ?? 9999.0,
};
```

Apply the same pattern to all five DTO `From()` methods.

- [ ] **Step 5: Run tests — verify they pass**

```bash
dotnet test Gravity-gen2.Tests/ -v 2>&1 | grep -E "FAIL|PASS|VariantRouter"
```
Expected: 5 tests PASS.

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/core/VariantRouter.cs src/core/GenotypeDto.cs Gravity-gen2.Tests/core/VariantRouterTests.cs
git commit -m "feat: VariantRouter with VolCoverage/Select + AtrLow/AtrHigh in GenotypeDto"
```

---

## Task 3: FoldScore extension — FadeShortGA + GridGA

**Files:**
- Modify: `src/strategies/fade_short/FadeShortGA.cs`
- Modify: `src/strategies/grid/GridGA.cs`

**Interfaces:**
- Consumes: `FitnessConfig` (Task 1), `VariantRouter.VolCoverage` (Task 2), `Simulator.SharpeRatio`, `Simulator.CalmarRatio`, `Simulator.SortinoRatio`, `Simulator.ProfitFactor` (all already exist)
- Produces: modified `FoldScore` signatures — see below

**Key facts:**
- `Simulator.SharpeRatio(returns, candleCount)` uses `candleCount / 288.0` for annualisation (15m bars per day)
- `Simulator.CalmarRatio(returns)` — no `candleCount` param; uses 252/count internally
- `Simulator.SortinoRatio(returns, candleCount)` — same annualisation as Sharpe
- `Simulator.ProfitFactor(returns)` — returns ratio, capped at 99.99
- FadeShortGA stores per-fold candle range `[fStart, fEnd)` and pre-computed `cache.Atr` → compute `volWeight` with `VariantRouter.VolCoverage(cache.Atr, fStart, fEnd, cfg.AtrLow, cfg.AtrHigh)`, averaged across all caches in that fold
- GridGA `FoldScore` takes only `List<double> returns` — add `FitnessConfig cfg` and `double volWeight = 1.0` parameters

- [ ] **Step 1: Add `_cfg` field and update constructor in FadeShortGA**

In `FadeShortGA.cs`, find the class-level fields (near the top, where `_populationSize`, `_generations`, etc. are declared). Add:

```csharp
private readonly FitnessConfig _cfg;
```

Find the constructor (it accepts `populationSize`, `generations`, `eliteCount`, etc.) and add:

```csharp
FitnessConfig? cfg = null
```

as the last optional parameter. In the constructor body add:

```csharp
_cfg = cfg ?? new FitnessConfig();
```

- [ ] **Step 2: Update FadeShortGA.FoldScore signature**

Change:
```csharp
private static double FoldScore(List<double> returns, double posFrac)
```
to:
```csharp
private static double FoldScore(List<double> returns, double posFrac, FitnessConfig cfg, double volWeight = 1.0)
```

- [ ] **Step 3: Update FadeShortGA.FoldScore body — replace the final return statement**

The current final return is:
```csharp
return gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
```

Replace with:
```csharp
double baseScore = gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
int    n         = returns.Count;
double sharpe    = Simulator.SharpeRatio(returns, n);
double calmar    = Simulator.CalmarRatio(returns);
double pf        = Simulator.ProfitFactor(returns);
double sortino   = Simulator.SortinoRatio(returns, n);
return baseScore
    * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  3.0)))
    * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  3.0)))
    * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pf - 1.0,       3.0)))
    * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  3.0)))
    * volWeight;
```

- [ ] **Step 4: Update all callers of FoldScore in FadeShortGA**

In `FitnessFromCache`, there are 3 call sites that call `FoldScore(all, posFrac)` or `FoldScore(foldReturns, posFrac)`. Update each:

**Non-fold paths** (2 call sites, no fold index):
```csharp
// Compute average volWeight across all caches (full span)
double volWeight = AverageVolCoverage(caches, 0, caches.Min(c => c.Candles.Length), _cfg);
return FoldScore(all, posFrac, _cfg, volWeight);
```

**Per-fold path** (1 call site, inside the `for (int f = 0; f < k; f++)` loop):
```csharp
double volWeight = AverageVolCoverage(caches, fStart, fEnd, _cfg);
scores[f] = FoldScore(foldReturns, posFrac, _cfg, volWeight);
```

Add a private helper to FadeShortGA to avoid repeating the average-across-caches logic:
```csharp
private static double AverageVolCoverage(IReadOnlyList<CoinCache> caches, int start, int end, FitnessConfig cfg)
{
    if (cfg.AtrLow <= 0.0 && cfg.AtrHigh >= 9999.0) return 1.0;
    double sum = 0; int count = 0;
    foreach (var c in caches)
    {
        if (c.Atr.Length < end) continue;
        sum += VariantRouter.VolCoverage(c.Atr, start, end, cfg.AtrLow, cfg.AtrHigh);
        count++;
    }
    return count == 0 ? 1.0 : sum / count;
}
```

Note: `FitnessFromCache` is a private static method — change it to instance method (remove `static`) so it can access `_cfg`, or pass `_cfg` as a parameter. Simplest: pass `_cfg` to `FitnessFromCache` as an additional parameter.

Find all calls to `FitnessFromCache(...)` in the class and add `_cfg` as the last argument. Update the method signature:
```csharp
private static double FitnessFromCache(
    FadeShortGenotype ind, IReadOnlyList<CoinCache> caches, bool useFolds, FitnessConfig cfg, int folds = 5)
```

- [ ] **Step 5: Apply same pattern to GridGA**

GridGA's `FoldScore` signature:
```csharp
private static double FoldScore(List<double> returns)
```
Change to:
```csharp
private static double FoldScore(List<double> returns, FitnessConfig cfg, double volWeight = 1.0)
```

GridGA's current final return:
```csharp
return gain * 100.0 * wrMult / ddDiv;
```
Replace with:
```csharp
double baseScore = gain * 100.0 * wrMult / ddDiv;
int    n         = returns.Count;
double sharpe    = Simulator.SharpeRatio(returns, n);
double calmar    = Simulator.CalmarRatio(returns);
double pf        = Simulator.ProfitFactor(returns);
double sortino   = Simulator.SortinoRatio(returns, n);
return baseScore
    * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  3.0)))
    * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  3.0)))
    * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pf - 1.0,       3.0)))
    * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  3.0)))
    * volWeight;
```

Add `private readonly FitnessConfig _cfg;` field and constructor parameter `FitnessConfig? cfg = null` → `_cfg = cfg ?? new FitnessConfig();` to GridGA.

In GridGA's `Fitness()` method, update each `FoldScore(...)` call to pass `_cfg` and `volWeight = 1.0` (GridGA uses `List<double>` returns only, no candle slices available at FoldScore call site — volWeight is left as 1.0 here; for variant-specific vol filtering, the VariantRouter in the backtest layer handles selection before trades reach GridGA at all).

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Smoke test — default config produces same-sign fitness**

```bash
dotnet run -- train 2>&1 | head -20
```
Expected: training starts, prints fold scores with same magnitude range as before (~1.0–3.0 range). The default config has all new weights = 0.0 so the formula is identical.

- [ ] **Step 8: Commit**

```bash
git add src/strategies/fade_short/FadeShortGA.cs src/strategies/grid/GridGA.cs
git commit -m "feat: extend FoldScore in FadeShortGA + GridGA with FitnessConfig weights + volWeight"
```

---

## Task 4: FoldScore extension — SwingLongGA, DipLongGA, FadeLongGA

**Files:**
- Modify: `src/strategies/swing_long/SwingLongGA.cs`
- Modify: `src/strategies/dip_long/DipLongGA.cs`
- Modify: `src/strategies/fade_long/FadeLongGA.cs`

**Interfaces:**
- Consumes: `FitnessConfig` (Task 1), `VariantRouter.VolCoverage` (Task 2)
- Key difference from Task 3: these GAs call `FoldScore` with full `h1`/`m15` candle spans per coin. `Indicators.Atr()` is accessible (internal, same assembly). Compute volWeight inside `FoldScore` from m15.

- [ ] **Step 1: Add `_cfg` field to SwingLongGA**

Add field and constructor parameter (same pattern as Task 3):
```csharp
private readonly FitnessConfig _cfg;
// In constructor, last optional param:
FitnessConfig? cfg = null
// Body:
_cfg = cfg ?? new FitnessConfig();
```

- [ ] **Step 2: Update SwingLongGA.FoldScore signature**

Change:
```csharp
private double FoldScore(SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
```
to:
```csharp
private double FoldScore(SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FitnessConfig cfg)
```

- [ ] **Step 3: Compute volWeight inside SwingLongGA.FoldScore and apply extended formula**

At the start of the method, after the `trades.Count < 3` guard, add the volWeight computation and ATR arrays:

```csharp
// Compute vol coverage from m15 candles
double volWeight = 1.0;
if (cfg.AtrLow > 0.0 || cfg.AtrHigh < 9999.0)
{
    var highs  = m15.ToArray().Select(c => c.High).ToArray();
    var lows   = m15.ToArray().Select(c => c.Low).ToArray();
    var closes = m15.ToArray().Select(c => c.Close).ToArray();
    var atr    = Indicators.Atr(highs, lows, closes, 14);
    volWeight  = VariantRouter.VolCoverage(atr, 0, atr.Length, cfg.AtrLow, cfg.AtrHigh);
}
```

Replace the final return:
```csharp
// current:
return gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
```
with:
```csharp
var   retList = trades.Select(t => t.Return).ToList();
int   n       = retList.Count;
double sharpe  = Simulator.SharpeRatio(retList, n);
double calmar  = Simulator.CalmarRatio(retList);
double pf      = Simulator.ProfitFactor(retList);
double sortino = Simulator.SortinoRatio(retList, n);
double base_   = gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
return base_
    * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  3.0)))
    * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  3.0)))
    * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pf - 1.0,       3.0)))
    * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  3.0)))
    * volWeight;
```

Update the call sites in `Fitness()`:
```csharp
// Old:
double s = FoldScore(g, h1, m15);
// New:
double s = FoldScore(g, h1, m15, _cfg);
```

- [ ] **Step 4: Apply same pattern to DipLongGA**

DipLongGA's `FoldScore` takes `List<(double Return, int RegimeBars)>` and doesn't have m15 access directly. The m15 candles are in `CoinData.TrainM15`. Pass m15 through from `Fitness()`:

Change DipLongGA.FoldScore signature:
```csharp
private static double FoldScore(
    List<(double Return, int RegimeBars)> returns,
    double posFrac,
    int    sustainedBars)
```
to:
```csharp
private static double FoldScore(
    List<(double Return, int RegimeBars)> returns,
    double posFrac,
    int    sustainedBars,
    FitnessConfig cfg,
    double volWeight = 1.0)
```

Replace the final return in DipLongGA.FoldScore (same Sharpe/Calmar/PF/Sortino pattern as Step 3).

Add `_cfg` field + constructor param to DipLongGA (same as SwingLong).

In DipLongGA's `Fitness()`, compute volWeight from m15 candles before calling FoldScore:
```csharp
// After getting h1/m15 from CoinData:
var m15Arr = (useValidation ? coin.ValM15 : coin.TrainM15).ToArray();
double volWeight = 1.0;
if (_cfg.AtrLow > 0.0 || _cfg.AtrHigh < 9999.0)
{
    var atr = Indicators.Atr(
        m15Arr.Select(c => c.High).ToArray(),
        m15Arr.Select(c => c.Low).ToArray(),
        m15Arr.Select(c => c.Close).ToArray(), 14);
    volWeight = VariantRouter.VolCoverage(atr, 0, atr.Length, _cfg.AtrLow, _cfg.AtrHigh);
}
```

Pass `_cfg` and `volWeight` to each FoldScore call in DipLongGA.

- [ ] **Step 5: Apply same pattern to FadeLongGA**

FadeLongGA.FoldScore has the identical signature as DipLongGA. Apply exactly the same changes as Step 4, substituting `FadeLongGA` for `DipLongGA` throughout.

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/strategies/swing_long/SwingLongGA.cs src/strategies/dip_long/DipLongGA.cs src/strategies/fade_long/FadeLongGA.cs
git commit -m "feat: extend FoldScore in SwingLong/DipLong/FadeLong GAs with FitnessConfig + volWeight"
```

---

## Task 5: CLI `--variant` arg + `fitness_config.json` in train commands

**Files:**
- Modify: `commands/TrainCommands.cs`
- Modify: `commands/LongTrainCommands.cs`
- Modify: `commands/Program.cs`

**Interfaces:**
- Consumes: `FitnessConfig.Load()` (Task 1), all GA constructors now accept `FitnessConfig?` (Tasks 3–4)
- Produces: `--variant <id>` flag for all train commands; variant-specific save paths; `fitness_config.json` consumed at startup

**Variant resolution logic** (same in every command):
```csharp
static string ResolveVariant(string[] args)
{
    int idx = Array.IndexOf(args, "--variant");
    if (idx >= 0 && idx + 1 < args.Length) return args[idx + 1];
    return Environment.GetEnvironmentVariable("GRAVITY_VARIANT") ?? "default";
}

static string VariantGenoPath(string strategyKey, string variant, string defaultPath)
    => variant == "default" ? defaultPath
       : $"genotypes/{strategyKey}_{variant}_genotype.json";
```

- [ ] **Step 1: Add `args` parameter to train command signatures**

`Program.cs` currently passes no `args` to train commands. Update each train case to pass the full `args` array:

```csharp
// In Program.cs, change e.g.:
case "train": await TrainCommands.RunFadeShortTrain(client); break;
// to:
case "train": await TrainCommands.RunFadeShortTrain(client, args); break;
```

Apply to: `train`, `retrain`, `gridtrain`, `fadelongtrain`, `diplongtrain`, `swinglongtrain`, `routertrain`, `coevolvetrain`, `dynamicguardtrain`.

- [ ] **Step 2: Update RunFadeShortTrain in TrainCommands.cs**

Change the method signature:
```csharp
public static async Task RunFadeShortTrain(BybitRestClient client, bool invertScreen = false)
```
to:
```csharp
public static async Task RunFadeShortTrain(BybitRestClient client, string[] args = [], bool invertScreen = false)
```

At the top of the method body add:
```csharp
string variant = ResolveVariant(args);
var    cfg     = FitnessConfig.Load();
Console.WriteLine($"Training FadeShort / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
string genoPath = VariantGenoPath("fade_short", variant, Config.FadeShortGenoFile);
```

Pass `cfg` when constructing `FadeShortGA`:
```csharp
// Find where FadeShortGA is instantiated (new FadeShortGA(...)) and add cfg:
var ga = new FadeShortGA(..., cfg: cfg);
```

When saving the genotype JSON, use `genoPath` instead of `Config.FadeShortGenoFile`, and pass `cfg` to `From()`:
```csharp
File.WriteAllText(genoPath, JsonSerializer.Serialize(FadeShortGenotypeDto.From(best, cfg)));
```

Add the two helper statics at the bottom of `TrainCommands`:
```csharp
internal static string ResolveVariant(string[] args)
{
    int idx = Array.IndexOf(args, "--variant");
    if (idx >= 0 && idx + 1 < args.Length) return args[idx + 1];
    return Environment.GetEnvironmentVariable("GRAVITY_VARIANT") ?? "default";
}

internal static string VariantGenoPath(string key, string variant, string defaultPath)
    => variant == "default" ? defaultPath : $"genotypes/{key}_{variant}_genotype.json";
```

- [ ] **Step 3: Update RunGridTrain in GridCommands.cs**

Apply the same pattern: add `string[] args = []`, call `ResolveVariant`, load `FitnessConfig`, build variant path for `grid`, pass `cfg` to `GridGA` constructor, save to variant path. Use `TrainCommands.ResolveVariant` and `TrainCommands.VariantGenoPath` (they're `internal static`).

- [ ] **Step 4: Update all 5 long train methods in LongTrainCommands.cs**

For each of `RunFadeLongTrain`, `RunDipLongTrain`, `RunSwingLongTrain`, `RunRegimeRouterTrain`, `RunCoevolve`:
- Add `string[] args = []` parameter
- Call `TrainCommands.ResolveVariant(args)` to get variant
- Load `FitnessConfig.Load()` → `cfg`
- Print startup line
- Build variant geno path using strategy key (`fade_long`, `dip_long`, `swing_long`, `regime_router`)
- Pass `cfg` to the GA constructor
- Save genotype to variant path

- [ ] **Step 5: Add `fitness_config.json` to `.gitignore`**

Open `.gitignore` and add:
```
fitness_config.json
backtest_results.json
training_progress.json
fulltest_results.json
```
(Add `live_journal.json` only if not already present.)

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Smoke test variant flag**

```bash
echo '{"SharpeW":0.5,"AtrLow":0.0,"AtrHigh":9999}' > fitness_config.json
dotnet run -- train --variant smoketest 2>&1 | head -5
```
Expected: first line contains `variant=smoketest | SharpeW=0.5`.

Then verify the output file was created:
```bash
ls genotypes/fade_short_smoketest_genotype.json
```
Expected: file exists. Clean up:
```bash
rm -f fitness_config.json genotypes/fade_short_smoketest_genotype.json
```

- [ ] **Step 8: Commit**

```bash
git add commands/Program.cs commands/TrainCommands.cs commands/LongTrainCommands.cs commands/GridCommands.cs .gitignore
git commit -m "feat: --variant flag + fitness_config.json in all train commands"
```

---

## Task 6: JSON output writers

**Files:**
- Modify: `commands/CombinedBacktest.cs` — write `backtest_results.json`
- Modify: `commands/OosBacktest.cs` — write `backtest_results.json` (OOS section)
- Modify: `commands/FullTest.cs` — write `fulltest_results.json`
- Modify: `commands/PapertradeCommands.cs` — append `live_journal.json`
- Each GA's `Run()` loop — write `training_progress.json` every 10 gens

**Note on training_progress.json:** Each GA writes this file independently during its own `Run()` loop. It is overwritten on each write (not appended).

- [ ] **Step 1: Add `training_progress.json` writes to FadeShortGA.Run()**

In `FadeShortGA.Run()`, find the per-generation loop. After sorting population and computing `topFitness`, every 10 generations write:

```csharp
if (gen % 10 == 0)
{
    var progress = new
    {
        strategy   = "FadeShort",
        variant    = _cfg.VariantId,      // set by CLI --variant in Task 5
        generation = gen,
        bestFitness = eliteIsland.First().Fitness,
        meanFitness = Math.Round(population.Average(g => g.Fitness), 4),
        population  = population.Take(20).Select(g => Math.Round(g.Fitness, 3)).ToArray(),
    };
    File.WriteAllText("training_progress.json",
        System.Text.Json.JsonSerializer.Serialize(progress,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
}
```

Apply the same pattern to `SwingLongGA.Run()`, `DipLongGA.Run()`, `FadeLongGA.Run()`, `GridGA.Run()` — substituting the correct strategy name string.

- [ ] **Step 2: Write `backtest_results.json` from CombinedBacktest**

In `CombinedBacktest.RunCombinedBacktest()`, after all strategy stats are computed and printed, add at the end:

```csharp
// Write structured JSON for frontend
var results = new
{
    timestamp = DateTime.UtcNow.ToString("O"),
    val = new Dictionary<string, object>
    {
        ["FadeShort"] = new Dictionary<string, object>
        {
            ["default"] = new { sharpe = swStats.Sharpe, trades = swStats.N, winRate = swStats.WinRate * 100,
                                pf = swStats.ProfitFactor, maxDD = swStats.MaxDD, ret = swStats.NetReturn }
        },
        ["Grid"] = new Dictionary<string, object>
        {
            ["default"] = new { sharpe = grStats.Sharpe, trades = grStats.N, winRate = grStats.WinRate * 100,
                                pf = grStats.ProfitFactor, maxDD = grStats.MaxDD, ret = grStats.NetReturn }
        },
        // Add SwingLong, DipLong, FadeLong using the same pattern with their respective stats variables
    }
};
File.WriteAllText("backtest_results.json",
    System.Text.Json.JsonSerializer.Serialize(results,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("backtest_results.json written.");
```

You will need to identify the exact variable names for each strategy's stats in `CombinedBacktest.cs` (look for the StrategyStats or summary variables computed before the final print block). Map each to the same JSON shape shown above.

- [ ] **Step 3: Write OOS section into `backtest_results.json` from OosBacktest**

In `OosBacktest.RunOosBacktest()`, after OOS stats are computed, read existing `backtest_results.json` (if present), merge the `oos` section into it, and write back:

```csharp
var oosSection = new Dictionary<string, object>
{
    ["FadeShort"] = new Dictionary<string, object>
    {
        ["default"] = new { sharpe = oosSwStats.Sharpe, trades = oosSwStats.N,
                            winRate = oosSwStats.WinRate * 100, ret = oosSwStats.NetReturn }
    },
    // same for other strategies
};

// Merge into existing file or create new
string path = "backtest_results.json";
var existing = File.Exists(path)
    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path))
      ?? new Dictionary<string, object>()
    : new Dictionary<string, object>();
existing["oos"] = oosSection;
existing["timestamp"] = DateTime.UtcNow.ToString("O");
File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(existing,
    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("backtest_results.json updated with OOS results.");
```

- [ ] **Step 4: Write `fulltest_results.json` from FullTest**

In `FullTest.RunFullTest()`, after computing all 7 sections, write a structured JSON. The shape must match `data.js` mock shapes exactly. Create an anonymous object that includes all the fields the frontend reads:

```csharp
var output = new
{
    timestamp   = DateTime.UtcNow.ToString("O"),
    backtest    = new { sharpe = portSharpe, trades = allTrades.Count, winRate = portWr * 100,
                        maxDD = portMaxDD, cagr = portCagr },
    oos         = new { sharpe = oosSharpe, trades = oosTrades.Count, winRate = oosWr * 100,
                        degradation = (oosSharpe - portSharpe) / Math.Abs(portSharpe) * 100 },
    stats       = new { tTest = new { t = tStat, p = pVal }, dsr = dsr, vc = new { verdict = vcVerdict } },
    wfvFolds    = wfvFoldsDict,         // Dictionary<string, double[]> — per-strategy fold scores
    routerImpact= new { valEdge = valEdgePp, oosEdge = oosEdgePp },
};
File.WriteAllText("fulltest_results.json",
    System.Text.Json.JsonSerializer.Serialize(output,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("fulltest_results.json written.");
```

Map the local variable names in `FullTest.cs` to the fields above — look at the `Console.WriteLine` calls in the 7-section print block to identify the right variable names.

- [ ] **Step 5: Append to `live_journal.json` from PapertradeCommands**

In `PapertradeCommands.RunPaperTrade()`, find where positions close (look for the state machine transition from open to closed, or where `closeTime` is determined). After a close event, append a record:

```csharp
var entry = new
{
    symbol    = sym,
    strategy  = strategyName,
    variant   = "default",
    openTime  = openTime.ToString("O"),
    closeTime = DateTime.UtcNow.ToString("O"),
    entryPrice= entryPrice,
    exitPrice = exitPrice,
    returnPct = Math.Round(returnPct, 4),
    halfKelly = Math.Round(halfKelly, 4),
};
string journalPath = "live_journal.json";
var journal = File.Exists(journalPath)
    ? System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(
          File.ReadAllText(journalPath)) ?? new()
    : new List<System.Text.Json.JsonElement>();
// Append as raw JsonElement to preserve existing entries
var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(entry));
journal.Add(doc.RootElement.Clone());
File.WriteAllText(journalPath, System.Text.Json.JsonSerializer.Serialize(journal,
    new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
```

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/strategies/fade_short/FadeShortGA.cs src/strategies/swing_long/SwingLongGA.cs \
        src/strategies/dip_long/DipLongGA.cs src/strategies/fade_long/FadeLongGA.cs \
        src/strategies/grid/GridGA.cs \
        commands/CombinedBacktest.cs commands/OosBacktest.cs commands/FullTest.cs \
        commands/PapertradeCommands.cs
git commit -m "feat: JSON output writers — training_progress, backtest_results, fulltest_results, live_journal"
```

---

## Task 7: VariantRouter wired into backtest + papertrade

**Files:**
- Modify: `commands/CombinedBacktest.cs`
- Modify: `commands/OosBacktest.cs`
- Modify: `commands/PapertradeCommands.cs`

**Pattern:** At startup, for each strategy, scan `genotypes/` for all files matching `{strategy}_*_genotype.json`. Deserialise each found file, read `AtrLow`/`AtrHigh` from the DTO, build a `VariantSpec<TGenotype>[]`. Pass the array to the simulator.

Since the simulators currently take a single genotype, the variant-aware path adds a thin loop: for each bar, call `VariantRouter.Select(atrArray, barIndex, variants)` and use the returned genotype for that bar's evaluation. This means the per-bar evaluation loop in each simulator needs a `variants[]` overload — or we handle the switching in the backtest layer by running each variant separately and merging trades.

**Simpler approach (no simulator changes):** Run simulation once per variant, filtering returned trades to only those bars where that variant was active (ATR ratio in range). Merge all trade lists. This avoids touching each simulator.

```csharp
// Build VariantSpec for FadeShort
var fsVariants = LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
    "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

// Run each variant's simulation, filter to active bars, merge
var fsTrades = new List<Trade>();
foreach (var v in fsVariants)
{
    var raw = FadeShortSimulator.GetFadeShortReturns(v.Genotype, h1, m15);
    // Filter: keep only trades where ATR ratio at trade open bar is in v's range
    var atr = Indicators.Atr(..., 14);
    fsTrades.AddRange(raw.Where(t => {
        int bar = FindBarIndex(m15, t.Time);
        return VariantRouter.Select(atr, bar, fsVariants) == v.Genotype;
    }));
}
```

A helper to load all variants for a strategy:
```csharp
static VariantSpec<TG>[] LoadVariants<TDto, TG>(
    string strategyKey,
    Func<TDto, TG> toGenotype,
    Func<TDto, (double Low, double High)> getRange)
    where TG : class
{
    var pattern = $"genotypes/{strategyKey}_*_genotype.json";
    var files   = Directory.GetFiles("genotypes", $"{strategyKey}_*_genotype.json");
    // Always include the default file
    if (File.Exists($"genotypes/{strategyKey}_genotype.json"))
        files = files.Append($"genotypes/{strategyKey}_genotype.json").Distinct().ToArray();
    return files.Select(f => {
        var dto = JsonSerializer.Deserialize<TDto>(File.ReadAllText(f))!;
        var (lo, hi) = getRange(dto);
        var variantId = Path.GetFileNameWithoutExtension(f)
                            .Replace($"{strategyKey}_", "").Replace("_genotype", "");
        return new VariantSpec<TG>(variantId, lo, hi, toGenotype(dto));
    }).ToArray();
}
```

- [ ] **Step 1: Add `LoadVariants` helper to CombinedBacktest**

Add the generic `LoadVariants` helper as a private static method in `CombinedBacktest`. Use the exact generic signature above.

- [ ] **Step 2: Replace single-genotype loading with variant loading for FadeShort in CombinedBacktest**

Find the existing `FadeShortGenotype? swG = ...` loading block. Replace with:
```csharp
var fsVariants = LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
    "fade_short",
    dto => dto.ToGenotype(),
    dto => (dto.AtrLow, dto.AtrHigh));
```

Find the simulation call for FadeShort and replace with the variant-merging pattern described above.

- [ ] **Step 3: Apply same pattern to Grid, SwingLong, DipLong, FadeLong in CombinedBacktest**

Each strategy's single-genotype load + simulation becomes a `LoadVariants` call + variant-merging loop.

- [ ] **Step 4: Apply same changes to OosBacktest**

`OosBacktest.RunOosBacktest` follows the same structure as CombinedBacktest. Add the `LoadVariants` helper there too and apply the variant-merging pattern for each of the 5 strategies.

- [ ] **Step 5: Update PapertradeCommands to use VariantRouter per coin**

In `PapertradeCommands.RunPaperTrade()`, when displaying trade state for each strategy/coin:
- Load all variant genotypes for that strategy at startup (same `LoadVariants` pattern)
- For each coin, compute current ATR from the m15 candle slice
- Call `VariantRouter.Select(atr, lastBarIndex, variants)` to get the active genotype
- If null → skip coin for that strategy this cycle
- Print variant id alongside coin: `SOLUSDT | SwingLong [liquid] ATR_ratio=0.81`

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Verify combinedbacktest still runs**

```bash
dotnet run -- combinedbacktest 2>&1 | tail -20
```
Expected: completes, prints portfolio stats, writes `backtest_results.json`.

- [ ] **Step 8: Commit**

```bash
git add commands/CombinedBacktest.cs commands/OosBacktest.cs commands/PapertradeCommands.cs
git commit -m "feat: VariantRouter wired into backtest + papertrade — per-coin ATR-based variant selection"
```

---

## Task 8: FastAPI server

**Files:**
- Create: `bot/api_server.py`
- Modify: `CLAUDE.md` (update serve command)

**Dependencies:** `pip install fastapi uvicorn` (add to `bot/requirements-discord.txt`)

- [ ] **Step 1: Add dependencies to requirements**

Open `bot/requirements-discord.txt` and add:
```
fastapi>=0.111
uvicorn[standard]>=0.29
```

Install:
```bash
pip install fastapi uvicorn 2>&1 | tail -3
```

- [ ] **Step 2: Create `bot/api_server.py`**

```python
# bot/api_server.py
import asyncio, json, os, subprocess, sys
from datetime import datetime
from pathlib import Path
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

ROOT = Path(__file__).parent.parent   # repo root
app  = FastAPI()

# ── Training subprocess state ─────────────────────────────────────────────────
_proc:      subprocess.Popen | None = None
_proc_info: dict = {}

STRATEGY_COMMANDS = {
    "FadeShort":  "train",
    "Grid":       "gridtrain",
    "SwingLong":  "swinglongtrain",
    "DipLong":    "diplongtrain",
    "FadeLong":   "fadelongtrain",
}

class TrainRequest(BaseModel):
    strategy:      str
    variant:       str = "default"
    fitnessConfig: dict = {}

# ── API endpoints ─────────────────────────────────────────────────────────────
@app.post("/api/train")
async def start_training(req: TrainRequest):
    global _proc, _proc_info
    if _proc and _proc.poll() is None:
        raise HTTPException(409, "Training already in progress")
    if req.strategy not in STRATEGY_COMMANDS:
        raise HTTPException(400, f"Unknown strategy: {req.strategy}")

    # Write fitness_config.json
    cfg_path = ROOT / "fitness_config.json"
    cfg_path.write_text(json.dumps(req.fitnessConfig))

    cmd = ["dotnet", "run", "--", STRATEGY_COMMANDS[req.strategy], "--variant", req.variant]
    env = {**os.environ, "GRAVITY_VARIANT": req.variant}
    _proc = subprocess.Popen(
        cmd, cwd=str(ROOT), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, bufsize=1, env=env
    )
    _proc_info = {
        "strategy":  req.strategy,
        "variant":   req.variant,
        "startedAt": datetime.utcnow().isoformat(),
        "lastLine":  "",
    }
    return {"status": "started", "pid": _proc.pid}

@app.get("/api/train/stream")
async def stream_training():
    async def generate():
        if _proc is None:
            yield "data: No training process running\n\n"
            return
        loop = asyncio.get_event_loop()
        while True:
            line = await loop.run_in_executor(None, _proc.stdout.readline)
            if not line:
                yield "data: [DONE]\n\n"
                break
            _proc_info["lastLine"] = line.rstrip()
            yield f"data: {line.rstrip()}\n\n"
    return StreamingResponse(generate(), media_type="text/event-stream")

@app.post("/api/stop")
async def stop_training():
    global _proc
    if _proc is None or _proc.poll() is not None:
        return {"status": "not running"}
    _proc.terminate()
    try:
        _proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        _proc.kill()
    _proc = None
    return {"status": "stopped"}

@app.get("/api/status")
async def get_status():
    running = _proc is not None and _proc.poll() is None
    return {"running": running, **_proc_info}

# ── Static files (frontend) — mount last so API routes take priority ──────────
app.mount("/", StaticFiles(directory=str(ROOT / "frontend"), html=True), name="frontend")
```

- [ ] **Step 3: Test the server starts**

```bash
cd /home/thomas/Gravity-gen2 && uvicorn bot.api_server:app --port 8080 &
sleep 2
curl -s http://localhost:8080/api/status | python3 -m json.tool
kill %1
```
Expected: `{"running": false}` (plus empty proc_info fields).

- [ ] **Step 4: Update CLAUDE.md serve command**

In `CLAUDE.md`, under `Commands`, replace:
```
python3 -m http.server 8080
# Then open: http://localhost:8080/frontend/Gravity%20Terminal.html
```
with:
```
uvicorn bot.api_server:app --port 8080
# Then open: http://localhost:8080/Gravity%20Terminal.html
```

- [ ] **Step 5: Commit**

```bash
git add bot/api_server.py bot/requirements-discord.txt CLAUDE.md
git commit -m "feat: FastAPI server — static frontend + /api/train SSE + /api/stop + /api/status"
```

---

## Task 9: Frontend installation + store.js wiring

**Files:**
- Create: `frontend/` (all files from prototype zip)
- Modify: `frontend/store.js` — add `atrRange`, training API calls, `backtest_results.json` fetch
- Modify: `frontend/Gravity Training.html` — wire SSE log stream + poll `training_progress.json`

- [ ] **Step 1: Copy prototype files into repo**

```bash
cp -r /tmp/gravity-prototype/frontend/* /home/thomas/Gravity-gen2/frontend/
ls /home/thomas/Gravity-gen2/frontend/
```
Expected: 15 files including `store.js`, `nav.jsx`, `charts.jsx`, `data.js`, `terminal-app.jsx`, `design-canvas.jsx`, `directionCockpit.jsx`, `directionTearsheet.jsx`, `directionTerminal.jsx`, and the 4 HTML pages.

- [ ] **Step 2: Add `atrRange` to variant definitions in `frontend/store.js`**

In `store.js`, add `atrRange` to each variant object. Open `frontend/store.js` and update the `VARIANTS` constant:

```js
const VARIANTS = {
  FadeShort: [
    { id:'default', label:'Default',  file:'../genotypes/fade_short_genotype.json',
      active:true,  tags:['always-on'], atrRange:[0, 9999],
      notes:'Primary trained genotype — 93 coins, 5-fold WFV.' },
    { id:'hival',   label:'High Vol', file:null, active:false,
      tags:['high-vol','draft'], atrRange:[1.5, 9999],
      notes:'For ATR expansion regimes. Tighter stop, faster RSI. Not yet trained.' },
    { id:'loval',   label:'Low Vol',  file:null, active:false,
      tags:['low-vol','draft'],  atrRange:[0, 0.8],
      notes:'Ranging / compressed markets. Wider parameters, lower size.' },
  ],
  Grid: [
    { id:'default', label:'Default',    file:'../genotypes/grid_best_genotype.json',
      active:true,  tags:['ranging'], atrRange:[0, 9999], notes:'ADX-gated ranging genotype.' },
    { id:'tight',   label:'Tight Grid', file:null, active:false,
      tags:['low-atr','draft'], atrRange:[0, 0.8], notes:'Smaller step size for low-ATR compression periods.' },
  ],
  SwingLong: [
    { id:'default', label:'Default', file:'../genotypes/swing_long_genotype.json',
      active:true,  tags:['bull'], atrRange:[0, 9999], notes:'Bull-regime RSI divergence + BoS. 93 coins.' },
    { id:'liquid',  label:'Liquid',  file:'../genotypes/swing_best_genotype_liquid.json',
      active:false, tags:['bull','liquid'], atrRange:[0, 9999], notes:'Large-cap only.' },
    { id:'mid',     label:'Mid Cap', file:'../genotypes/swing_best_genotype_mid.json',
      active:false, tags:['bull','mid-cap'], atrRange:[0, 9999], notes:'Mid-cap universe.' },
    { id:'bull_run',label:'Bull Run',file:null, active:false,
      tags:['bull','draft'], atrRange:[0, 9999], notes:'Aggressive params for confirmed bull breakout.' },
  ],
  DipLong: [
    { id:'default', label:'Default',  file:'../genotypes/dip_long_genotype.json',
      active:true,  tags:['bull'], atrRange:[0, 9999], notes:'RSI dip 40–55 in established uptrend + BoS.' },
    { id:'deep',    label:'Deep Dip', file:null, active:false,
      tags:['bull','draft'], atrRange:[0, 9999], notes:'Deeper RSI retrace (30–45).' },
  ],
  FadeLong: [
    { id:'default', label:'Default',      file:'../genotypes/fade_long_genotype.json',
      active:true,  tags:['bear'], atrRange:[0, 9999], notes:'Bear-regime oversold bounce.' },
    { id:'cascade', label:'Bear Cascade', file:null, active:false,
      tags:['bear','draft'], atrRange:[1.3, 9999], notes:'Tuned for multi-wave bear cascades.' },
  ],
};
```

- [ ] **Step 3: Add `backtest_results.json` fetch to store.js**

In `store.js`'s `load()` function, add `backtest_results.json` to the parallel fetches:

```js
const [baseline, liveState, journal, router, guard, backtestResults] = await Promise.all([
  fetchJSON('../backtest_baseline.json',    G.backtest    || {}),
  fetchJSON('../livetrain_state.json',      G.training    || {}),
  fetchJSON('../live_journal.json',         []),
  fetchJSON('../genotypes/regime_router_genotype.json', null),
  fetchJSON('../genotypes/dynamic_guard_genotype.json', null),
  fetchJSON('../backtest_results.json',     null),
]);
state.backtestResults = backtestResults;
```

Add `backtestResults: null` to the initial `state` object.

- [ ] **Step 4: Add `GravStore.train()` to store.js**

At the bottom of the `GravStore` public API block, add:

```js
async function train(strategy, variant, fitnessConfig) {
  const r = await fetch('/api/train', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ strategy, variant, fitnessConfig }),
  });
  return r.json();
}

async function stopTraining() {
  const r = await fetch('/api/stop', { method: 'POST' });
  return r.json();
}

window.GravStore = { state, load, subscribe, setActive, addVariant, VARIANTS, train, stopTraining };
```

- [ ] **Step 5: Wire SSE log stream in `Gravity Training.html`**

In `Gravity Training.html`, find the "Start Training" button handler and add:

```js
async function startTraining() {
  const strategy      = document.getElementById('strategySelect').value;
  const variant       = document.getElementById('variantSelect').value;
  const fitnessConfig = readSliders(); // reads slider values into an object
  const logEl         = document.getElementById('trainingLog');

  logEl.textContent = '';
  await GravStore.train(strategy, variant, fitnessConfig);

  const es = new EventSource('/api/train/stream');
  es.onmessage = e => {
    if (e.data === '[DONE]') { es.close(); return; }
    logEl.textContent += e.data + '\n';
    logEl.scrollTop = logEl.scrollHeight;
  };
  es.onerror = () => es.close();
}
```

Add a `readSliders()` helper. Inspect `Gravity Training.html` for the exact slider `id` attributes (look for `input[type=range]` elements near the fitness weight labels). Map each to the `FitnessConfig` JSON key names (`SharpeW`, `CalmarW`, `PfW`, `SortinoW`, `AtrLow`, `AtrHigh`, `VariantId`):

```js
function readSliders() {
  const v = id => parseFloat(document.getElementById(id)?.value ?? 0);
  return {
    VariantId:  document.getElementById('variantSelect')?.value ?? 'default',
    SharpeW:    v('sharpW'),
    CalmarW:    v('calmarW'),
    PfW:        v('pfW'),
    SortinoW:   v('sortinoW'),
    AtrLow:     v('atrLow'),
    AtrHigh:    v('atrHigh') || 9999,
  };
}
```
If the HTML ids differ, update the `id` strings to match what's in the file.

- [ ] **Step 6: Start server and verify terminal page loads**

```bash
uvicorn bot.api_server:app --port 8080 &
sleep 2
curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/
kill %1
```
Expected: `200`.

- [ ] **Step 7: Commit**

```bash
git add frontend/ CLAUDE.md
git commit -m "feat: frontend dashboard — 4 pages, store.js wiring, training SSE, atrRange in variant registry"
```

---

## Validation Checklist

After all tasks complete, verify:

- [ ] `dotnet build -c Release` passes
- [ ] `dotnet test Gravity-gen2.Tests/` — all tests green (including 3 FitnessConfig + 5 VariantRouter)
- [ ] `echo '{"SharpeW":0.9,"CalmarW":1.2,"AtrLow":1.5}' > fitness_config.json && dotnet run -- train --variant hival 2>&1 | head -3` — prints `variant=hival | SharpeW=0.9 CalmarW=1.2 AtrRange=[1.5,9999]`
- [ ] `ls genotypes/fade_short_hival_genotype.json` — file exists after training
- [ ] `uvicorn bot.api_server:app --port 8080` starts without error
- [ ] `curl http://localhost:8080/api/status` returns `{"running":false,...}`
- [ ] Browser opens `http://localhost:8080/` — Terminal page renders without JS errors
- [ ] `dotnet run -- combinedbacktest 2>&1 | tail -5` — completes and writes `backtest_results.json`
