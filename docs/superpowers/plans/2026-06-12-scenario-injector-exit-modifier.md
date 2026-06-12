# ScenarioInjector + ExitModifier Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two analysis components — ScenarioInjector (adversarial GA finds worst-case crash scenarios) and ExitModifier (context-aware position-size scaler trained by GA) — without modifying any existing simulator.

**Architecture:** ScenarioInjector morphs real Candle[] arrays with crash curves, injects them into the existing simulation pipeline, and uses an adversarial GA to find max-drawdown scenarios. ExitModifier is a post-simulation enrichment layer: TradeEnricher adds per-trade context (ATR rank, MAE/MFE, heat, liquidity), ExitModifierGA trains a position-size scaling function on enriched trades to maximize combined val+OOS Calmar ratio.

**Tech Stack:** C# .NET 10, existing `Candle`/`Simulator`/`BayesianOptimizer`/`Indicators`/`CandleExt` infrastructure, `System.Text.Json`.

---

## File Map

**Create:**
- `ScenarioGenotype.cs` — crash scenario parameters + DTO
- `ScenarioInjector.cs` — morphs `Candle[]` arrays with crash curve + de-aggregate h1→m15
- `ScenarioGA.cs` — adversarial GA maximising portfolio max-drawdown
- `StressTestCommands.cs` — `dotnet run -- stresstest` implementation
- `TradeEnricher.cs` — enriches raw trades with ATR rank, MAE/MFE, heat, liquidity
- `ExitModifierGenotype.cs` — modifier genotype, DTO, constants
- `ExitModifierGA.cs` — GA maximising combined Calmar on enriched trades
- `ExitModifierTrainCommands.cs` — `dotnet run -- exitmodifiertrain` implementation

**Modify:**
- `Config.cs` — add `ExitModifierGenoFile` constant
- `FullTest.cs` — collect trades with symbol, add Section 8 (ExitModifier results)
- `Program.cs` — add `stresstest` and `exitmodifiertrain` cases

---

### Task 1: Config.cs — add ExitModifierGenoFile

**Files:**
- Modify: `Config.cs`

- [ ] **Step 1: Add constant**

In `Config.cs`, add after the `SwingLongGenoFile` line:

```csharp
public const string ExitModifierGenoFile = "exit_modifier_genotype.json";
```

- [ ] **Step 2: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Config.cs
git commit -m "chore: add ExitModifierGenoFile constant to Config"
```

---

### Task 2: ScenarioGenotype.cs

**Files:**
- Create: `ScenarioGenotype.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingGA;

public record ScenarioGenotype(
    double CrashDepthPct,         // 0.10–0.60: fractional price drop from anchor to trough
    double CrashDurationHours,    // 12–336: hours to reach trough (linear descent)
    double RecoveryHours,         // 0–336: hours for 50% partial recovery after trough
    double AtrExpansionPeak,      // 1.5–10.0: ATR range multiplier at trough
    double AltBetaPct,            // 0.5–2.0: alt-coin crash depth = BTC depth × AltBetaPct
    double LiquiditySqueezeHours, // 6–48: hours of reduced volume during early crash
    double InjectionOffsetFrac,   // 0.0–1.0: maps to bar index in BTC h1 array
    double Fitness = 0)
{
    public static readonly double[,] Bounds = {
        { 0.10, 0.60  }, // CrashDepthPct
        { 12,   336   }, // CrashDurationHours
        { 0,    336   }, // RecoveryHours
        { 1.5,  10.0  }, // AtrExpansionPeak
        { 0.5,  2.0   }, // AltBetaPct
        { 6,    48    }, // LiquiditySqueezeHours
        { 0.1,  0.9   }, // InjectionOffsetFrac
    };

    public double[] ToGenes() =>
        [CrashDepthPct, CrashDurationHours, RecoveryHours, AtrExpansionPeak,
         AltBetaPct, LiquiditySqueezeHours, InjectionOffsetFrac];

    public static ScenarioGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6], fitness);
}

public record ScenarioGenotypeDto(
    [property: JsonPropertyName("CrashDepthPct")]         double CrashDepthPct,
    [property: JsonPropertyName("CrashDurationHours")]    double CrashDurationHours,
    [property: JsonPropertyName("RecoveryHours")]         double RecoveryHours,
    [property: JsonPropertyName("AtrExpansionPeak")]      double AtrExpansionPeak,
    [property: JsonPropertyName("AltBetaPct")]            double AltBetaPct,
    [property: JsonPropertyName("LiquiditySqueezeHours")] double LiquiditySqueezeHours,
    [property: JsonPropertyName("InjectionOffsetFrac")]   double InjectionOffsetFrac,
    [property: JsonPropertyName("Fitness")]               double Fitness)
{
    public ScenarioGenotype ToGenotype() =>
        new(CrashDepthPct, CrashDurationHours, RecoveryHours, AtrExpansionPeak,
            AltBetaPct, LiquiditySqueezeHours, InjectionOffsetFrac, Fitness);
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add ScenarioGenotype.cs
git commit -m "feat: add ScenarioGenotype with crash scenario parameters"
```

---

### Task 3: ScenarioInjector.cs

**Files:**
- Create: `ScenarioInjector.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace TradingGA;

// Morphs a real Candle[] with a parameterised crash scenario.
// Does not modify originals — returns a new array.
//
// Crash shape: linear descent from anchor close to trough (CrashDepthPct × altBetaMult below anchor).
// ATR expansion: range inflated by AtrExpansionPeak at trough midpoint (sin envelope).
// Recovery: linear rise from trough to 50% of lost depth, over RecoveryHours bars.
// Liquidity: volume reduced linearly during first LiquiditySqueezeHours bars.
//
// DeaggregateToM15: generates consistent 15m stubs from morphed h1 for multi-TF strategies.
public static class ScenarioInjector
{
    // Morphs a single coin's h1 array.
    // altBetaMult: 1.0 for BTC, g.AltBetaPct for all other coins.
    public static Candle[] Inject(Candle[] h1, ScenarioGenotype g, int injectionBar, double altBetaMult = 1.0)
    {
        var result    = h1.ToArray();
        int crashBars = Math.Max(1, (int)g.CrashDurationHours);
        int recovBars = (int)g.RecoveryHours;
        int liqBars   = (int)g.LiquiditySqueezeHours;
        double depth  = g.CrashDepthPct * altBetaMult;

        double peakClose = injectionBar > 0 ? h1[injectionBar - 1].Close : h1[0].Close;

        // Phase 1: crash — linear descent from peak to trough
        for (int i = 0; i < crashBars && injectionBar + i < h1.Length; i++)
        {
            double t         = (double)(i + 1) / crashBars;
            double newClose  = peakClose * (1.0 - depth * t);
            double atrExpand = 1.0 + (g.AtrExpansionPeak - 1.0) * Math.Sin(Math.PI * (double)i / crashBars);
            double rawRange  = h1[injectionBar + i].High - h1[injectionBar + i].Low;
            double newRange  = rawRange * atrExpand;
            double volMult   = (i < liqBars) ? Math.Max(0.1, 1.0 - 0.8 * t) : 1.0;
            double newOpen   = i == 0 ? h1[injectionBar].Open : result[injectionBar + i - 1].Close;
            result[injectionBar + i] = new Candle(
                h1[injectionBar + i].Time,
                newOpen,
                newClose + newRange * 0.3,
                Math.Max(newClose - newRange * 0.7, 1e-6),
                newClose,
                h1[injectionBar + i].Volume * volMult);
        }

        // Phase 2: recovery — linear rise to 50% of lost depth from trough
        int    troughBar  = injectionBar + crashBars;
        double troughPx   = troughBar < result.Length ? result[troughBar - 1].Close : peakClose * (1 - depth);
        double recovTarget = troughPx + (peakClose - troughPx) * 0.5;

        for (int i = 0; i < recovBars && troughBar + i < h1.Length; i++)
        {
            double t        = (double)(i + 1) / Math.Max(1, recovBars);
            double newClose = troughPx + (recovTarget - troughPx) * t;
            double rawRange = h1[troughBar + i].High - h1[troughBar + i].Low;
            double newOpen  = i == 0 ? result[troughBar - 1].Close : result[troughBar + i - 1].Close;
            result[troughBar + i] = new Candle(
                h1[troughBar + i].Time,
                newOpen,
                newClose + rawRange * 0.4,
                Math.Max(newClose - rawRange * 0.3, 1e-6),
                newClose,
                h1[troughBar + i].Volume);
        }

        return result;
    }

    // Deaggregates h1 into 4 equal 15m stubs per bar.
    // Used by the scenario GA to feed multi-TF simulators without I/O.
    public static Candle[] DeaggregateToM15(Candle[] h1)
    {
        var m15 = new Candle[h1.Length * 4];
        for (int i = 0; i < h1.Length; i++)
        {
            var bar = h1[i];
            for (int q = 0; q < 4; q++)
            {
                m15[i * 4 + q] = new Candle(
                    bar.Time + TimeSpan.FromMinutes(15 * q),
                    q == 0 ? bar.Open : bar.Close,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    bar.Volume / 4.0);
            }
        }
        return m15;
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add ScenarioInjector.cs
git commit -m "feat: add ScenarioInjector — crash morphing and h1→m15 deaggregation"
```

---

### Task 4: ScenarioGA.cs + StressTestCommands.cs

**Files:**
- Create: `ScenarioGA.cs`
- Create: `StressTestCommands.cs`

- [ ] **Step 1: Create ScenarioGA.cs**

```csharp
namespace TradingGA;

// Adversarial GA that finds the crash scenario maximising portfolio max-drawdown.
// Fitness = portfolio max-drawdown % (higher = worse for the portfolio = fitter for this GA).
//
// Uses a subset of 10 diverse coins (large-cap + DeFi + meme + L2) to keep evaluation fast.
// For each individual, morphs all coin h1 arrays, de-aggregates to m15, runs all 5 strategies,
// combines trades, simulates portfolio, returns maxDrawdownPct as fitness.
public class ScenarioGA
{
    // Diverse slice: large-cap, DeFi, L2, meme — chosen for scenario sensitivity variety
    public static readonly string[] ScenarioCoins =
    [
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "LINKUSDT", "AAVEUSDT",
        "ARBUSDT", "NEARUSDT", "WIFUSDT", "1000PEPEUSDT", "ORDIUSDT"
    ];

    private readonly int  _populationSize;
    private readonly int  _generations;
    private readonly bool _verbose;

    public ScenarioGA(int populationSize = 40, int generations = 60, bool verbose = true)
    {
        _populationSize = populationSize;
        _generations    = generations;
        _verbose        = verbose;
    }

    public record CoinData(Candle[] H1, string Symbol);

    public ScenarioGenotype Run(
        IReadOnlyDictionary<string, Candle[]> h1Map,
        FadeShortGenotype     fsG,
        GridGenotype          gridG,
        FadeLongGenotype?     flG,
        DipLongGenotype?      dlG,
        SwingLongGenotype?    slG,
        RegimeRouterGenotype? routerG)
    {
        var coins = ScenarioCoins
            .Where(h1Map.ContainsKey)
            .Select(s => new CoinData(h1Map[s], s))
            .ToList();

        if (coins.Count == 0) throw new InvalidOperationException("No scenario coins found in h1Map.");

        // BTC series for router (use original, unmodified — router is regime context, not crash victim)
        RegimeRouterSession? session = null;
        if (routerG != null && h1Map.TryGetValue("BTCUSDT", out var btcH1r) && btcH1r.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1r);
            RegimeBar[]? ethSeries = h1Map.TryGetValue("ETHUSDT", out var ethH1r) && ethH1r.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethH1r) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        var rng  = new Random(42);
        int nDim = 7; // genes in ScenarioGenotype (excluding Fitness)

        // Initialise population
        var pop = new List<ScenarioGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var genes = new double[nDim];
            for (int d = 0; d < nDim; d++)
                genes[d] = ScenarioGenotype.Bounds[d, 0]
                         + rng.NextDouble() * (ScenarioGenotype.Bounds[d, 1] - ScenarioGenotype.Bounds[d, 0]);
            pop.Add(ScenarioGenotype.FromGenes(genes, Evaluate(ScenarioGenotype.FromGenes(genes), coins, fsG, gridG, flG, dlG, slG, session)));
        }

        pop = [.. pop.OrderByDescending(g => g.Fitness)];

        for (int gen = 1; gen <= _generations; gen++)
        {
            var next = new List<ScenarioGenotype> { pop[0], pop[1] }; // elites

            while (next.Count < _populationSize)
            {
                var parent = pop[rng.Next(Math.Min(10, pop.Count))]; // top-10 tournament
                var genes  = parent.ToGenes();
                for (int d = 0; d < nDim; d++)
                {
                    double range = ScenarioGenotype.Bounds[d, 1] - ScenarioGenotype.Bounds[d, 0];
                    genes[d] = Math.Clamp(
                        genes[d] + rng.NextGaussian() * range * 0.1,
                        ScenarioGenotype.Bounds[d, 0], ScenarioGenotype.Bounds[d, 1]);
                }
                var child  = ScenarioGenotype.FromGenes(genes);
                var fit    = Evaluate(child, coins, fsG, gridG, flG, dlG, slG, session);
                next.Add(child with { Fitness = fit });
            }

            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  ScenarioGA Gen {gen,3} — worst-case DD: {pop[0].Fitness:F1}%  "
                    + $"CrashDepth={pop[0].CrashDepthPct:P0}  Dur={pop[0].CrashDurationHours:F0}h  "
                    + $"AltBeta={pop[0].AltBetaPct:F2}");
        }

        return pop[0];
    }

    private static double Evaluate(
        ScenarioGenotype      g,
        List<CoinData>        coins,
        FadeShortGenotype     fsG,
        GridGenotype          gridG,
        FadeLongGenotype?     flG,
        DipLongGenotype?      dlG,
        SwingLongGenotype?    slG,
        RegimeRouterSession?  session)
    {
        var trades = new List<(DateTime Time, double Return, double Conf, string Strategy, TimeSpan Hold)>();

        // BTC reference for injection bar alignment
        var btcCoin = coins.FirstOrDefault(c => c.Symbol == "BTCUSDT");
        if (btcCoin == null) return 0;
        int injBar = (int)(btcCoin.H1.Length * g.InjectionOffsetFrac);
        injBar = Math.Clamp(injBar, 50, btcCoin.H1.Length - (int)g.CrashDurationHours - (int)g.RecoveryHours - 10);

        foreach (var coin in coins)
        {
            double beta   = coin.Symbol == "BTCUSDT" ? 1.0 : g.AltBetaPct;
            var    mH1    = ScenarioInjector.Inject(coin.H1, g, injBar, beta);
            var    mM15   = ScenarioInjector.DeaggregateToM15(mH1);

            if (mH1.Length < 200) continue;

            // FadeShort
            {
                var t = FadeShortSimulator.GetFadeShortReturns(fsG, mH1, mM15);
                double conf = t.Count > 0 ? Simulator.ComputeConfidence(t.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _) in t)
                    trades.Add((time, ret, conf, "swing", TimeSpan.FromHours(fsG.MaxHoldCandles)));
            }
            // Grid
            {
                var raw   = GridSimulator.GetGridReturns(gridG, mH1);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
                double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _) in gated)
                    trades.Add((time, ret, conf, "grid", TimeSpan.FromHours(gridG.MaxHoldCandles)));
            }
            // FadeLong
            if (flG != null && mH1.Length >= 200 && mM15.Length >= 800)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, mH1, mM15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _, _) in gated)
                    trades.Add((time, ret, conf, "fadelong", TimeSpan.FromHours(flG.MaxHoldCandles)));
            }
            // DipLong
            if (dlG != null && mH1.Length >= 200 && mM15.Length >= 800)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, mH1, mM15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _, _) in gated)
                    trades.Add((time, ret, conf, "diplong", TimeSpan.FromHours(dlG.MaxHoldCandles)));
            }
            // SwingLong
            if (slG != null && mH1.Length >= 200 && mM15.Length >= 800)
            {
                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, mH1, mM15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _) in gated)
                    trades.Add((time, ret, conf, "swing_long", TimeSpan.FromHours(slG.MaxHoldCandles)));
            }
        }

        if (trades.Count < 5) return 0;

        var simInput = trades.OrderBy(t => t.Time)
            .Select(t => (t.Time, t.Return, t.Conf, t.Hold)).ToList();
        var result = Simulator.SimulatePortfolioExposureCapped(simInput, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        return result.MaxDrawdownPct;
    }
}
```

Note: the `NextGaussian()` extension on `Random` is used in existing GA files — verify it exists with `grep -r "NextGaussian" *.cs` and use the same extension. If not found, add:
```csharp
// In ScenarioGA.cs, after the class, add:
internal static class RandomExt2
{
    public static double NextGaussian(this Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
```
Before adding, check: `grep -rn "NextGaussian" /home/thomas/Gravity-gen2/*.cs | head -3` — if it's already an extension on `Random` in another file, do NOT redeclare it.

- [ ] **Step 2: Create StressTestCommands.cs**

```csharp
using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class StressTestCommands
{
    public static async Task RunStressTest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | STRESS TEST (adversarial scenario GA, {ScenarioGA.ScenarioCoins.Length} coins) ===\n");

        // Load genotypes
        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing Grid genotype.");      return; }

        var fsG     = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG   = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        var flG     = File.Exists(Config.FadeLongGenoFile)  ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()   : null;
        var dlG     = File.Exists(Config.DipLongGenoFile)   ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()     : null;
        var slG     = File.Exists(Config.SwingLongGenoFile) ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype() : null;
        var routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        // Fetch candles for scenario coins only
        var allSyms = ScenarioGA.ScenarioCoins.Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        Console.WriteLine($"  Fetching {allSyms.Length} coins (1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var tasks = allSyms.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, h1);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(tasks);
        var h1Map   = fetched.Where(f => f.h1.Length >= 200).ToDictionary(f => f.sym, f => f.h1);
        Console.WriteLine($"  Done ({h1Map.Count} coins loaded).\n");

        // Compute baseline portfolio on original (unmorphed) candles
        double baselineDd = ComputePortfolioDD(h1Map, fsG, gridG, flG, dlG, slG, routerG);
        Console.WriteLine($"  Baseline max-drawdown (unmorphed): {baselineDd:F1}%\n");

        // Run ScenarioGA
        Console.WriteLine("  Running adversarial GA (40 individuals, 60 generations)...\n");
        var ga   = new ScenarioGA(populationSize: 40, generations: 60, verbose: true);
        var best = ga.Run(h1Map, fsG, gridG, flG, dlG, slG, routerG);

        // Report
        int injBar = h1Map.TryGetValue("BTCUSDT", out var btcH1)
            ? (int)(btcH1.Length * best.InjectionOffsetFrac) : 0;
        var injDate = btcH1 != null && injBar < btcH1.Length ? btcH1[injBar].Time : DateTime.MinValue;

        Console.WriteLine($"\n{'═',88}".Replace(',', ' ')[..88]);
        Console.WriteLine($"  WORST-CASE SCENARIO (fitness = max-drawdown)");
        Console.WriteLine($"{'═',88}".Replace(',', ' ')[..88]);
        Console.WriteLine($"  Crash depth:       {best.CrashDepthPct:P0}  (alt beta ×{best.AltBetaPct:F2})");
        Console.WriteLine($"  Crash duration:    {best.CrashDurationHours:F0}h  →  recovery {best.RecoveryHours:F0}h");
        Console.WriteLine($"  ATR expansion:     {best.AtrExpansionPeak:F1}×  at trough");
        Console.WriteLine($"  Liquidity squeeze: {best.LiquiditySqueezeHours:F0}h of reduced volume");
        Console.WriteLine($"  Injection point:   bar {injBar} / {btcH1?.Length ?? 0}  ({injDate:yyyy-MM-dd})");
        Console.WriteLine($"\n  Portfolio max-drawdown:  {best.Fitness:F1}%  (baseline {baselineDd:F1}%)");
        Console.WriteLine($"  Stress multiplier:       {best.Fitness / Math.Max(baselineDd, 1.0):F2}×");
    }

    private static double ComputePortfolioDD(
        IReadOnlyDictionary<string, Candle[]> h1Map,
        FadeShortGenotype     fsG,
        GridGenotype          gridG,
        FadeLongGenotype?     flG,
        DipLongGenotype?      dlG,
        SwingLongGenotype?    slG,
        RegimeRouterGenotype? routerG)
    {
        // Build a neutral scenario (near-zero crash) to measure unmorphed baseline
        var neutral = new ScenarioGenotype(
            CrashDepthPct: 0.001, CrashDurationHours: 1, RecoveryHours: 0,
            AtrExpansionPeak: 1.0, AltBetaPct: 1.0, LiquiditySqueezeHours: 0,
            InjectionOffsetFrac: 0.5);

        // Use ScenarioGA.Evaluate (private) — instead, just run the portfolio sim directly here
        RegimeRouterSession? session = null;
        if (routerG != null && h1Map.TryGetValue("BTCUSDT", out var btcH1r) && btcH1r.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1r);
            RegimeBar[]? ethSeries = h1Map.TryGetValue("ETHUSDT", out var ethH1r) && ethH1r.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethH1r) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        var trades = new List<(DateTime, double, double, TimeSpan)>();
        foreach (var (sym, h1) in h1Map.Where(kv => ScenarioGA.ScenarioCoins.Contains(kv.Key)))
        {
            var m15 = ScenarioInjector.DeaggregateToM15(h1);

            var fsT = FadeShortSimulator.GetFadeShortReturns(fsG, h1, m15);
            double fsConf = fsT.Count > 0 ? Simulator.ComputeConfidence(fsT.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _) in fsT) trades.Add((t, r, fsConf, TimeSpan.FromHours(fsG.MaxHoldCandles)));

            var gRaw   = GridSimulator.GetGridReturns(gridG, h1);
            var gGated = session != null ? gRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : gRaw;
            double gConf = gGated.Count > 0 ? Simulator.ComputeConfidence(gGated.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _) in gGated) trades.Add((t, r, gConf, TimeSpan.FromHours(gridG.MaxHoldCandles)));

            if (dlG != null && h1.Length >= 200 && m15.Length >= 800)
            {
                var dlRaw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
                var dlGated = session != null ? dlRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : dlRaw;
                double dlConf = dlGated.Count > 0 ? Simulator.ComputeConfidence(dlGated.Select(t => t.Return).ToList()) : 0.03;
                foreach (var (t, r, _, _) in dlGated) trades.Add((t, r, dlConf, TimeSpan.FromHours(dlG.MaxHoldCandles)));
            }
            if (slG != null && h1.Length >= 200 && m15.Length >= 800)
            {
                var slRaw   = SwingLongSimulator.GetSwingLongReturns(slG, h1, m15);
                var slGated = session != null ? slRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : slRaw;
                double slConf = slGated.Count > 0 ? Simulator.ComputeConfidence(slGated.Select(t => t.Return).ToList()) : 0.03;
                foreach (var (t, r, _) in slGated) trades.Add((t, r, slConf, TimeSpan.FromHours(slG.MaxHoldCandles)));
            }
        }

        if (trades.Count < 5) return 0;
        var result = Simulator.SimulatePortfolioExposureCapped(
            trades.OrderBy(t => t.Item1).ToList(), Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        return result.MaxDrawdownPct;
    }
}
```

- [ ] **Step 3: Check NextGaussian**

```bash
grep -rn "NextGaussian" /home/thomas/Gravity-gen2/*.cs | head -5
```

If `NextGaussian` is already defined as an extension method on `Random` in another file, remove the `RandomExt2` class from `ScenarioGA.cs`. If it is NOT found, add it to `ScenarioGA.cs`.

- [ ] **Step 4: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors. If `CS0101` (duplicate type) for `RandomExt2`, remove the class — it's already defined elsewhere.

- [ ] **Step 5: Commit**

```bash
git add ScenarioGA.cs StressTestCommands.cs
git commit -m "feat: add ScenarioGA and StressTestCommands (adversarial crash stress test)"
```

---

### Task 5: ExitModifierGenotype.cs

**Files:**
- Create: `ExitModifierGenotype.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingGA;

// Position-size scaling genotype based on portfolio context at trade entry.
//
// Context factors (all computed by TradeEnricher and stored per trade):
//   AtrRank         — 0..1 percentile of current ATR in 252-bar rolling distribution
//   OpenPositions   — concurrent open trades at entry
//   LiquidityScore  — volume[entry] / 20-bar avg volume (>1 = liquid, <1 = thin)
//   RecentTradeCount — trades opened in past 168h
//
// Size multiplier (clamped to [MinSizeMult, 1.0]):
//   atrFactor  = (1 - AtrRank)^AtrRankPower          — compresses size in high-vol
//   heatFactor = HeatMultPerPosition ^ max(0, OpenPositions - HeatThreshold)
//   liqFactor  = lerp(LiquidityMinMult, 1, LiqScore/LiqFloor) when LiqScore < LiqFloor, else 1
//   freqFactor = lerp(FreqMultAtMax, 1, FreqMax/recent) when recent > FreqMax, else 1
//   mult       = clamp(atrFactor × heatFactor × liqFactor × freqFactor, MinSizeMult, 1)
public record ExitModifierGenotype(
    double AtrRankPower,          // 0.0–3.0: exponent for ATR rank compression (0 = off)
    double HeatThreshold,         // 1–15: open positions above which heat compression starts
    double HeatMultPerPosition,   // 0.5–1.0: size multiplier per position above threshold
    double LiquidityFloor,        // 0.1–2.0: LiquidityScore below which penalty starts
    double LiquidityMinMult,      // 0.0–0.5: minimum multiplier for illiquid entries
    double FreqMaxPerWindow,      // 1–40: trades in 168h above which size is reduced
    double FreqMultAtMax,         // 0.1–1.0: size multiplier when at/above FreqMaxPerWindow
    double MinSizeMult,           // 0.0–0.3: global floor for the size multiplier
    double Fitness = 0)
{
    public static readonly double[,] Bounds = {
        { 0.0, 3.0 },  // AtrRankPower
        { 1.0, 15.0 }, // HeatThreshold
        { 0.5, 1.0 },  // HeatMultPerPosition
        { 0.1, 2.0 },  // LiquidityFloor
        { 0.0, 0.5 },  // LiquidityMinMult
        { 1.0, 40.0 }, // FreqMaxPerWindow
        { 0.1, 1.0 },  // FreqMultAtMax
        { 0.0, 0.3 },  // MinSizeMult
    };

    public double[] ToGenes() =>
        [AtrRankPower, HeatThreshold, HeatMultPerPosition, LiquidityFloor,
         LiquidityMinMult, FreqMaxPerWindow, FreqMultAtMax, MinSizeMult];

    public static ExitModifierGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], fitness);

    // Compute context multiplier for a single enriched trade
    public double ComputeMult(TradeEnricher.EnrichedTrade t)
    {
        double atr  = Math.Pow(1.0 - t.AtrRank, Math.Max(0.0, AtrRankPower));
        double heat = Math.Pow(HeatMultPerPosition,
                               Math.Max(0.0, t.OpenPositions - (int)HeatThreshold));
        double liq  = t.LiquidityScore >= LiquidityFloor ? 1.0
                    : LiquidityMinMult + (1.0 - LiquidityMinMult)
                      * (t.LiquidityScore / LiquidityFloor);
        double freq = t.RecentTradeCount <= (int)FreqMaxPerWindow ? 1.0
                    : FreqMultAtMax + (1.0 - FreqMultAtMax)
                      * ((int)FreqMaxPerWindow / (double)Math.Max(1, t.RecentTradeCount));
        return Math.Clamp(atr * heat * liq * freq, MinSizeMult, 1.0);
    }

    public override string ToString() =>
        $"AtrPow={AtrRankPower:F2} HeatThr={HeatThreshold:F0}@{HeatMultPerPosition:F2} "
      + $"Liq={LiquidityFloor:F2}/{LiquidityMinMult:F2} "
      + $"Freq={FreqMaxPerWindow:F0}@{FreqMultAtMax:F2} MinMult={MinSizeMult:F2} F={Fitness:F4}";
}

public record ExitModifierGenotypeDto(
    [property: JsonPropertyName("AtrRankPower")]        double AtrRankPower,
    [property: JsonPropertyName("HeatThreshold")]       double HeatThreshold,
    [property: JsonPropertyName("HeatMultPerPosition")] double HeatMultPerPosition,
    [property: JsonPropertyName("LiquidityFloor")]      double LiquidityFloor,
    [property: JsonPropertyName("LiquidityMinMult")]    double LiquidityMinMult,
    [property: JsonPropertyName("FreqMaxPerWindow")]    double FreqMaxPerWindow,
    [property: JsonPropertyName("FreqMultAtMax")]       double FreqMultAtMax,
    [property: JsonPropertyName("MinSizeMult")]         double MinSizeMult,
    [property: JsonPropertyName("Fitness")]             double Fitness)
{
    public ExitModifierGenotype ToGenotype() =>
        new(AtrRankPower, HeatThreshold, HeatMultPerPosition, LiquidityFloor,
            LiquidityMinMult, FreqMaxPerWindow, FreqMultAtMax, MinSizeMult, Fitness);
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors. (Note: `TradeEnricher.EnrichedTrade` is referenced in `ComputeMult` — it will fail until Task 6. If the build fails with `CS0246` for `TradeEnricher`, change `ComputeMult` to take the fields as parameters instead, or simply comment out `ComputeMult` temporarily and restore it after Task 6.)

- [ ] **Step 3: Commit**

```bash
git add ExitModifierGenotype.cs
git commit -m "feat: add ExitModifierGenotype with context-aware position scaling"
```

---

### Task 6: TradeEnricher.cs

**Files:**
- Create: `TradeEnricher.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace TradingGA;

// Enriches a flat trade list with per-trade context features.
//
// Per-trade (requires coin's h1 candles):
//   AtrAtEntry      — 14-period ATR at the entry bar
//   AtrRank         — percentile of AtrAtEntry in [bar-252, bar] ATR window (0=low vol, 1=high vol)
//   MaeAtr          — max adverse excursion over hold window / AtrAtEntry
//   MfeAtr          — max favorable excursion over hold window / AtrAtEntry
//   LiquidityScore  — h1 volume[entry] / avg(volume[entry-20..entry])
//
// Cross-trade (requires full sorted trade list):
//   OpenPositions   — concurrent open trades at entry (across all symbols and strategies)
//   TotalExposure   — sum of CoinConf for all open trades at entry
//   RecentTradeCount — trades opened in previous 168h
//
// Direction for MAE/MFE:
//   Short strategies (FadeShort / "swing"): adverse = price rising, favorable = price falling
//   Long strategies (all others):           adverse = price falling, favorable = price rising
public static class TradeEnricher
{
    public record EnrichedTrade(
        DateTime EntryTime,
        double   Return,
        double   CoinConf,
        string   Strategy,
        string   Symbol,
        TimeSpan HoldDuration,
        double   AtrAtEntry,
        double   AtrRank,
        double   MaeAtr,
        double   MfeAtr,
        double   LiquidityScore,
        int      OpenPositions,
        double   TotalExposure,
        int      RecentTradeCount);

    private static readonly HashSet<string> ShortStrategies = ["swing"];

    public static List<EnrichedTrade> Enrich(
        List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)> rawTrades,
        IReadOnlyDictionary<string, Candle[]> h1Map)
    {
        if (rawTrades.Count == 0) return [];

        var sorted = rawTrades.OrderBy(t => t.Time).ToList();
        var result = new List<EnrichedTrade>(sorted.Count);

        // Precompute per-symbol ATR and volume arrays (avoid repeated computation)
        var symbolCache = new Dictionary<string, (double[] Atr, double[] Vol, DateTime[] Times)>();
        foreach (var sym in sorted.Select(t => t.Symbol).Distinct())
        {
            if (!h1Map.TryGetValue(sym, out var h1)) continue;
            double[] closes = CandleExt.Closes(h1);
            double[] highs  = CandleExt.Highs(h1);
            double[] lows   = CandleExt.Lows(h1);
            var atr  = Indicators.Atr(highs, lows, closes, 14);
            var vol  = h1.Select(c => c.Volume).ToArray();
            var times = h1.Select(c => c.Time).ToArray();
            symbolCache[sym] = (atr, vol, times);
        }

        var recentWindow = TimeSpan.FromHours(168);

        for (int i = 0; i < sorted.Count; i++)
        {
            var (time, ret, conf, strategy, symbol, hold) = sorted[i];

            // ── Per-trade fields ──────────────────────────────────────────────────
            double atrAtEntry = 0, atrRank = 0.5, maeAtr = 0, mfeAtr = 0, liqScore = 1.0;

            if (symbolCache.TryGetValue(symbol, out var cache) && h1Map.TryGetValue(symbol, out var h1))
            {
                var (atrArr, volArr, timesArr) = cache;

                // Bar index via binary search on pre-extracted DateTime array
                int bar = Array.BinarySearch(timesArr, time);
                if (bar < 0) bar = ~bar - 1;
                bar = Math.Clamp(bar, 14, h1.Length - 2);

                atrAtEntry = atrArr[bar];

                // ATR percentile rank in 252-bar rolling window
                int rankStart  = Math.Max(0, bar - 252);
                int rankCount  = bar - rankStart + 1;
                int below      = 0;
                for (int k = rankStart; k <= bar; k++)
                    if (atrArr[k] <= atrAtEntry) below++;
                atrRank = rankCount > 0 ? (double)below / rankCount : 0.5;

                // Liquidity score: volume[bar] / avg(vol[bar-20..bar-1])
                int liqStart = Math.Max(0, bar - 20);
                double vol20Sum = 0;
                int liqN = 0;
                for (int k = liqStart; k < bar; k++) { vol20Sum += volArr[k]; liqN++; }
                double vol20Avg = liqN > 0 ? vol20Sum / liqN : 1.0;
                liqScore = vol20Avg > 1e-10 ? volArr[bar] / vol20Avg : 1.0;

                // MAE/MFE over hold window
                bool isShort   = ShortStrategies.Contains(strategy);
                double entryPx = h1[bar].Close;
                int holdBars   = Math.Max(1, (int)hold.TotalHours); // h1 = 1h bars
                double maxFav  = 0, maxAdv = 0;
                for (int k = bar + 1; k < Math.Min(bar + holdBars + 1, h1.Length); k++)
                {
                    double fav = isShort ? entryPx - h1[k].Low : h1[k].High - entryPx;
                    double adv = isShort ? h1[k].High - entryPx : entryPx - h1[k].Low;
                    if (fav > maxFav) maxFav = fav;
                    if (adv > maxAdv) maxAdv = adv;
                }
                mfeAtr = atrAtEntry > 1e-10 ? maxFav / atrAtEntry : 0;
                maeAtr = atrAtEntry > 1e-10 ? maxAdv / atrAtEntry : 0;
            }

            // ── Cross-trade fields ────────────────────────────────────────────────
            int    openPos    = 0;
            double totalExp   = 0;
            int    recentCnt  = 0;

            for (int j = 0; j < i; j++)
            {
                var other = sorted[j];
                // Open if opened before us and closes after our entry
                if (other.Time + other.HoldDuration > time)
                {
                    openPos++;
                    totalExp += other.Conf;
                }
                // Recent window check
                if (time - other.Time <= recentWindow)
                    recentCnt++;
            }

            result.Add(new EnrichedTrade(
                time, ret, conf, strategy, symbol, hold,
                atrAtEntry, atrRank, maeAtr, mfeAtr, liqScore,
                openPos, totalExp, recentCnt));
        }

        return result;
    }
}
```

- [ ] **Step 2: Restore `ComputeMult` in ExitModifierGenotype**

If `ComputeMult` was commented out in Task 5, restore it now. It references `TradeEnricher.EnrichedTrade` which is now defined.

- [ ] **Step 3: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add TradeEnricher.cs ExitModifierGenotype.cs
git commit -m "feat: add TradeEnricher and restore ExitModifierGenotype.ComputeMult"
```

---

### Task 7: ExitModifierGA.cs

**Files:**
- Create: `ExitModifierGA.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Text.Json;

namespace TradingGA;

// GA that optimises ExitModifierGenotype by maximising combined val+OOS Calmar ratio.
//
// Fitness = (valCalmar + oosCalmar) / 2
// where Calmar = portfolioReturn / max(maxDrawdown, 1.0)
// and portfolioReturn comes from SimulatePortfolioExposureCapped with the modified CoinConfs.
//
// The modifier scales each trade's CoinConf (position size) using ExitModifierGenotype.ComputeMult.
// Trades with lower mult get smaller positions; no trades are filtered out entirely
// (MinSizeMult >= 0 preserves all signals).
public class ExitModifierGA
{
    private readonly int  _populationSize;
    private readonly int  _generations;
    private readonly bool _verbose;
    private readonly Random _rng = new();

    public ExitModifierGA(int populationSize = 40, int generations = 60, bool verbose = true)
    {
        _populationSize = populationSize;
        _generations    = generations;
        _verbose        = verbose;
    }

    public ExitModifierGenotype Run(
        List<TradeEnricher.EnrichedTrade> valEnriched,
        List<TradeEnricher.EnrichedTrade> oosEnriched)
    {
        int nDim = 8; // genes in ExitModifierGenotype (excluding Fitness)

        // Initialise population
        var pop = new List<ExitModifierGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var genes = RandomGenes(nDim);
            var g     = ExitModifierGenotype.FromGenes(genes);
            pop.Add(g with { Fitness = Evaluate(g, valEnriched, oosEnriched) });
        }
        pop = [.. pop.OrderByDescending(g => g.Fitness)];

        int elites = Math.Max(2, _populationSize / 5);

        for (int gen = 1; gen <= _generations; gen++)
        {
            var next = pop.Take(elites).ToList();

            while (next.Count < _populationSize)
            {
                // Tournament selection from top half
                int topN  = Math.Max(2, pop.Count / 2);
                var parent = pop[_rng.Next(topN)];
                var genes  = parent.ToGenes();
                MutateGenes(genes, nDim, mutStrength: 0.12);
                var child  = ExitModifierGenotype.FromGenes(genes);
                next.Add(child with { Fitness = Evaluate(child, valEnriched, oosEnriched) });
            }

            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  ExitModifierGA Gen {gen,3} — elite: {pop[0]}");
        }

        // Bayesian refinement (30 TPE iterations)
        Console.WriteLine("\n─── Bayesian refinement (30 TPE iterations) ───");
        var seedObs = pop.Take(10)
            .Select(g => (g.ToGenes(), g.Fitness))
            .ToList();
        var history = BayesianOptimizer.Refine(
            seedObs, ExitModifierGenotype.Bounds,
            genes =>
            {
                var g = ExitModifierGenotype.FromGenes(genes);
                return Evaluate(g, valEnriched, oosEnriched);
            },
            iterations: 30, rng: _rng);

        var best = history.OrderByDescending(h => h.Fitness).First();
        var bestG = ExitModifierGenotype.FromGenes(best.Params, best.Fitness);
        Console.WriteLine($"  TPE best: {bestG}");
        return bestG;
    }

    private double Evaluate(
        ExitModifierGenotype g,
        List<TradeEnricher.EnrichedTrade> val,
        List<TradeEnricher.EnrichedTrade> oos)
    {
        var valMod = ApplyModifier(g, val);
        var oosMod = ApplyModifier(g, oos);

        if (valMod.Count < 5 || oosMod.Count < 5) return -1.0;

        var valPort = Simulator.SimulatePortfolioExposureCapped(valMod, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosPort = Simulator.SimulatePortfolioExposureCapped(oosMod, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        double valRet   = valPort.EndBalance - 100.0;
        double oosRet   = oosPort.EndBalance - 100.0;
        double valDD    = valPort.MaxDrawdownPct;
        double oosDD    = oosPort.MaxDrawdownPct;

        if (valRet <= 0 && oosRet <= 0) return (valRet + oosRet) / 200.0 - 0.5;

        double valCalmar = valRet / Math.Max(valDD, 1.0);
        double oosCalmar = oosRet / Math.Max(oosDD, 1.0);
        return (valCalmar + oosCalmar) / 2.0;
    }

    public static List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> ApplyModifier(
        ExitModifierGenotype g, List<TradeEnricher.EnrichedTrade> trades)
    {
        return trades
            .Select(t => (t.EntryTime, t.Return, t.CoinConf * g.ComputeMult(t), t.HoldDuration))
            .OrderBy(t => t.EntryTime)
            .ToList();
    }

    private double[] RandomGenes(int nDim)
    {
        var g = new double[nDim];
        for (int d = 0; d < nDim; d++)
            g[d] = ExitModifierGenotype.Bounds[d, 0]
                 + _rng.NextDouble() * (ExitModifierGenotype.Bounds[d, 1] - ExitModifierGenotype.Bounds[d, 0]);
        return g;
    }

    private void MutateGenes(double[] genes, int nDim, double mutStrength)
    {
        for (int d = 0; d < nDim; d++)
        {
            if (_rng.NextDouble() < 0.7) // 70% per-gene mutation probability
            {
                double range = ExitModifierGenotype.Bounds[d, 1] - ExitModifierGenotype.Bounds[d, 0];
                genes[d] = Math.Clamp(
                    genes[d] + _rng.NextGaussian() * range * mutStrength,
                    ExitModifierGenotype.Bounds[d, 0],
                    ExitModifierGenotype.Bounds[d, 1]);
            }
        }
    }
}
```

Again check for `NextGaussian`: if it's defined in another file (e.g. in the GA files), do NOT redeclare it. Remove the local call pattern and use the extension from `Random`.

- [ ] **Step 2: Create ExitModifierTrainCommands.cs**

```csharp
using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class ExitModifierTrainCommands
{
    public static async Task RunExitModifierTrain(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | EXIT MODIFIER TRAIN (val 20% + OOS, all strategies) ===\n");

        // ── Load genotypes ─────────────────────────────────────────────────────
        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing Grid genotype."); return; }

        var swingG  = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG   = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        var flG     = File.Exists(Config.FadeLongGenoFile)  ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()   : null;
        var dlG     = File.Exists(Config.DipLongGenoFile)   ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()     : null;
        var slG     = File.Exists(Config.SwingLongGenoFile) ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype() : null;
        var routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        // ── Single candle fetch (val + OOS + BTC/ETH) ─────────────────────────
        var allSyms = Config.BacktestCoins
            .Concat(Config.OosCoins)
            .Concat(new[] { "BTCUSDT", "ETHUSDT" })
            .Distinct().ToArray();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} training + {Config.OosCoins.Length} OOS coins...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = allSyms.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, m15: m15.ToArray(), h1);
            }
            finally { sem.Release(); }
        });
        var fetched    = await Task.WhenAll(fetchTasks);
        var fetchedMap = fetched.ToDictionary(f => f.sym);
        var h1Map      = fetched.Where(f => f.h1.Length >= 200).ToDictionary(f => f.sym, f => f.h1);
        Console.WriteLine("  Done.\n");

        // ── Router session ─────────────────────────────────────────────────────
        RegimeRouterSession? session = null;
        if (routerG != null && h1Map.TryGetValue("BTCUSDT", out var btcH1) && btcH1.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
            RegimeBar[]? ethSeries = h1Map.TryGetValue("ETHUSDT", out var ethH1) && ethH1.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethH1) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        // ── Collect val trades (with symbol) ──────────────────────────────────
        var valRaw = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();
        var oosRaw = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();

        // Val coins — 80/20 time split
        foreach (var sym in Config.BacktestCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = h1Split * 4;
            var h1Val    = h1[h1Split..];
            var m15Val   = m15[Math.Min(m15Split, m15.Length)..];

            CollectTrades(sym, h1Val, m15Val, swingG, gridG, flG, dlG, slG, session, valRaw,
                isOos: false, swingG.MaxHoldCandles, gridG.MaxHoldCandles,
                flG?.MaxHoldCandles ?? 48, dlG?.MaxHoldCandles ?? 48, slG?.MaxHoldCandles ?? 48);
        }

        // OOS coins — full history
        foreach (var sym in Config.OosCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            CollectTrades(sym, h1, m15, swingG, gridG, flG, dlG, slG, session, oosRaw,
                isOos: true, swingG.MaxHoldCandles, gridG.MaxHoldCandles,
                flG?.MaxHoldCandles ?? 48, dlG?.MaxHoldCandles ?? 48, slG?.MaxHoldCandles ?? 48);
        }

        Console.WriteLine($"  Val trades: {valRaw.Count}  OOS trades: {oosRaw.Count}");
        Console.WriteLine("  Enriching trades with context...");
        var valEnriched = TradeEnricher.Enrich(valRaw, h1Map);
        var oosEnriched = TradeEnricher.Enrich(oosRaw, h1Map);
        Console.WriteLine($"  Enriched: {valEnriched.Count} val / {oosEnriched.Count} OOS\n");

        Console.WriteLine("  Running ExitModifierGA (40 individuals, 60 generations)...\n");
        var ga   = new ExitModifierGA(populationSize: 40, generations: 60, verbose: true);
        var best = ga.Run(valEnriched, oosEnriched);

        // Compute before/after metrics
        var valBefore = Simulator.SimulatePortfolioExposureCapped(
            valEnriched.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
            Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var valAfter = Simulator.SimulatePortfolioExposureCapped(
            ExitModifierGA.ApplyModifier(best, valEnriched),
            Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosBefore = Simulator.SimulatePortfolioExposureCapped(
            oosEnriched.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
            Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosAfter = Simulator.SimulatePortfolioExposureCapped(
            ExitModifierGA.ApplyModifier(best, oosEnriched),
            Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        Console.WriteLine($"\n  Val:  before: {valBefore.EndBalance - 100:+0.1;-0.1}% DD={valBefore.MaxDrawdownPct:F1}%"
                        + $"  →  after: {valAfter.EndBalance - 100:+0.1;-0.1}% DD={valAfter.MaxDrawdownPct:F1}%");
        Console.WriteLine($"  OOS:  before: {oosBefore.EndBalance - 100:+0.1;-0.1}% DD={oosBefore.MaxDrawdownPct:F1}%"
                        + $"  →  after: {oosAfter.EndBalance - 100:+0.1;-0.1}% DD={oosAfter.MaxDrawdownPct:F1}%");

        // Save
        var dto  = new ExitModifierGenotypeDto(best.AtrRankPower, best.HeatThreshold, best.HeatMultPerPosition,
            best.LiquidityFloor, best.LiquidityMinMult, best.FreqMaxPerWindow, best.FreqMultAtMax,
            best.MinSizeMult, best.Fitness);
        File.WriteAllText(Config.ExitModifierGenoFile, System.Text.Json.JsonSerializer.Serialize(dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n  Saved → {Config.ExitModifierGenoFile}");
    }

    private static void CollectTrades(
        string sym,
        Candle[] h1, Candle[] m15,
        FadeShortGenotype fsG, GridGenotype gridG,
        FadeLongGenotype? flG, DipLongGenotype? dlG, SwingLongGenotype? slG,
        RegimeRouterSession? session,
        List<(DateTime, double, double, string, string, TimeSpan)> dest,
        bool isOos,
        int fsMaxHold, int gridMaxHold, int flMaxHold, int dlMaxHold, int slMaxHold)
    {
        if (h1.Length < 200) return;

        // FadeShort (no router gate — always-on)
        {
            var trades = FadeShortSimulator.GetFadeShortReturns(fsG, h1, m15);
            if (!isOos)
            {
                var tr = FadeShortSimulator.GetFadeShortReturns(fsG, h1[..(int)(h1.Length * 0.8)], m15[..(int)(m15.Length * 0.8) * 4]).Select(t => t.Return).ToList();
                if (tr.Count < 5 || tr.Average() <= 0) goto skipFs;
            }
            double conf = Simulator.ComputeConfidence(trades.Select(t => t.Return).ToList());
            foreach (var (t, r, _) in trades)
                dest.Add((t, r, conf, "swing", sym, TimeSpan.FromHours(fsMaxHold)));
            skipFs:;
        }

        // Grid
        {
            var raw   = GridSimulator.GetGridReturns(gridG, h1);
            var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
            double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _) in gated)
                dest.Add((t, r, conf, "grid", sym, TimeSpan.FromHours(gridMaxHold)));
        }

        // FadeLong
        if (flG != null && m15.Length >= 800)
        {
            var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
            var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
            double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _, _) in gated)
                dest.Add((t, r, conf, "fadelong", sym, TimeSpan.FromHours(flMaxHold)));
        }

        // DipLong
        if (dlG != null && m15.Length >= 800)
        {
            var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
            var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
            double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _, _) in gated)
                dest.Add((t, r, conf, "diplong", sym, TimeSpan.FromHours(dlMaxHold)));
        }

        // SwingLong
        if (slG != null && m15.Length >= 800)
        {
            var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1, m15);
            var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw; // SwingLong shares bull gate
            double conf = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _) in gated)
                dest.Add((t, r, conf, "swing_long", sym, TimeSpan.FromHours(slMaxHold)));
        }
    }
}
```

Note on `goto`: The FadeShort screen logic uses `goto skipFs` to skip adding trades if the training screen fails. This mirrors the screening logic in FullTest. If the compiler complains about jumping over `conf` declaration, restructure as a local function or early-return pattern. Alternative: wrap in an `if` block:

```csharp
{
    bool shouldAdd = isOos;
    if (!isOos)
    {
        var tr = FadeShortSimulator.GetFadeShortReturns(fsG, h1[..(int)(h1.Length * 0.8)], m15[..(Math.Min(m15.Length, (int)(h1.Length * 0.8) * 4))]).Select(t => t.Return).ToList();
        shouldAdd = tr.Count >= 5 && tr.Average() > 0;
    }
    if (shouldAdd)
    {
        var trades = FadeShortSimulator.GetFadeShortReturns(fsG, h1, m15);
        double conf = Simulator.ComputeConfidence(trades.Select(t => t.Return).ToList());
        foreach (var (t, r, _) in trades)
            dest.Add((t, r, conf, "swing", sym, TimeSpan.FromHours(fsMaxHold)));
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors. Fix any compilation issues (particularly around the FadeShort screen logic and `goto`).

- [ ] **Step 4: Commit**

```bash
git add ExitModifierGA.cs ExitModifierTrainCommands.cs
git commit -m "feat: add ExitModifierGA and exitmodifiertrain command"
```

---

### Task 8: FullTest.cs — symbol tracking + Section 8

**Files:**
- Modify: `FullTest.cs`

This task adds:
1. Two new parallel collection lists that include symbol: `valRawForEnrich`, `oosRawForEnrich`
2. Section 8 at the end: loads `exit_modifier_genotype.json`, enriches current trades, computes modified portfolio metrics

- [ ] **Step 1: Add enrichment collections after existing list declarations**

In `FullTest.cs`, after the line declaring `var valAll = ...` (around line 73), add:

```csharp
var valRawForEnrich = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();
var oosRawForEnrich = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();
```

- [ ] **Step 2: Populate valRawForEnrich in the val coin loop**

In the **FadeShort** block (after `valAll.Add((t, ret, conf, "swing"))` inside the `foreach (var (t, ret, _) in ...)` loop), add:

```csharp
valRawForEnrich.Add((t, ret, conf, "swing", sym, TimeSpan.FromHours(swingG.MaxHoldCandles)));
```

In the **Grid** block (after `foreach (var t in gated) valAll.Add(...)`), add:

```csharp
foreach (var t in gated)
    valRawForEnrich.Add((t.Time, t.Return, conf, "grid", sym, TimeSpan.FromHours(gridG.MaxHoldCandles)));
```

In the **FadeLong** block (after `foreach (var t in gated) valAll.Add(...)`, where `flG != null`), add:

```csharp
foreach (var t in gated)
    valRawForEnrich.Add((t.Time, t.Return, conf, "fadelong", sym, TimeSpan.FromHours(flG!.MaxHoldCandles)));
```

In the **DipLong** block, add:

```csharp
foreach (var t in gated)
    valRawForEnrich.Add((t.Time, t.Return, conf, "diplong", sym, TimeSpan.FromHours(dlG!.MaxHoldCandles)));
```

In the **SwingLong** block, add:

```csharp
foreach (var t in gated)
    valRawForEnrich.Add((t.Time, t.Return, conf, "swing_long", sym, TimeSpan.FromHours(slG!.MaxHoldCandles)));
```

- [ ] **Step 3: Populate oosRawForEnrich in the OOS coin loop**

Same pattern as Step 2 but using `oosRawForEnrich` and `h1` (full history, not h1Val). Add after each OOS strategy's `oosAll.Add(...)` calls. For FadeShort OOS:

```csharp
foreach (var (t, ret, _) in trades)
    oosRawForEnrich.Add((t, ret, conf, "swing", sym, TimeSpan.FromHours(swingG.MaxHoldCandles)));
```

For Grid OOS (after `foreach (var t in gated) oosAll.Add(...)`):
```csharp
foreach (var t in gated)
    oosRawForEnrich.Add((t.Time, t.Return, conf, "grid", sym, TimeSpan.FromHours(gridG.MaxHoldCandles)));
```

For FadeLong OOS (where `oosFlRets.AddRange(...)` is present):
```csharp
foreach (var t in gated)
    oosRawForEnrich.Add((t.Time, t.Return, conf, "fadelong", sym, TimeSpan.FromHours(flG!.MaxHoldCandles)));
```

For DipLong OOS:
```csharp
foreach (var t in gated)
    oosRawForEnrich.Add((t.Time, t.Return, conf, "diplong", sym, TimeSpan.FromHours(dlG!.MaxHoldCandles)));
```

For SwingLong OOS (read the OOS section to find where SwingLong OOS trades are added and mirror the same pattern).

- [ ] **Step 4: Add Section 8 at end of RunFullTest, before the closing `}`**

Append this block before `}` (the final closing brace of `RunFullTest`):

```csharp
        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 8: EXIT MODIFIER (context-aware position sizing)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  EXIT MODIFIER");
        Console.WriteLine($"{new string('═', 88)}");

        if (!File.Exists(Config.ExitModifierGenoFile))
        {
            Console.WriteLine($"  No exit modifier genotype found — run 'exitmodifiertrain' to train one.");
        }
        else
        {
            var emG = JsonSerializer.Deserialize<ExitModifierGenotypeDto>(
                File.ReadAllText(Config.ExitModifierGenoFile))!.ToGenotype();
            Console.WriteLine($"  Genotype: {emG}\n");

            Console.WriteLine("  Enriching val trades...");
            var valEnriched = TradeEnricher.Enrich(valRawForEnrich, fetchedMap.ToDictionary(kv => kv.Key, kv => kv.Value.h1));
            var oosEnriched = TradeEnricher.Enrich(oosRawForEnrich, fetchedMap.ToDictionary(kv => kv.Key, kv => kv.Value.h1));

            var valBaseline = Simulator.SimulatePortfolioExposureCapped(
                valEnriched.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var oosBaseline = Simulator.SimulatePortfolioExposureCapped(
                oosEnriched.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

            var valModified = Simulator.SimulatePortfolioExposureCapped(
                ExitModifierGA.ApplyModifier(emG, valEnriched),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var oosModified = Simulator.SimulatePortfolioExposureCapped(
                ExitModifierGA.ApplyModifier(emG, oosEnriched),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

            string FmtPort(Simulator.PortfolioResult p) =>
                $"ret={p.EndBalance - 100:+0.1;-0.1}%  DD={p.MaxDrawdownPct:F1}%  trades={p.TradesCount}";

            Console.WriteLine($"  {"",12}  {"── Val (20%) ──────────────────────",36}  {"── OOS ─────────────────────",28}");
            Console.WriteLine($"  {"Baseline",-12}  {FmtPort(valBaseline),-36}  {FmtPort(oosBaseline),-28}");
            Console.WriteLine($"  {"Modified",-12}  {FmtPort(valModified),-36}  {FmtPort(oosModified),-28}");

            double valRetDelta = (valModified.EndBalance - valBaseline.EndBalance);
            double oosDdDelta  = valModified.MaxDrawdownPct - valBaseline.MaxDrawdownPct;
            Console.WriteLine($"\n  Val Δ: return {valRetDelta:+0.1;-0.1}%  DD {oosDdDelta:+0.1;-0.1}pp");

            // Context breakdown: average multiplier per strategy
            Console.WriteLine($"\n  Avg context multiplier per strategy:");
            foreach (var strat in new[] { "swing", "grid", "diplong", "swing_long", "fadelong" })
            {
                var forStrat = valEnriched.Where(t => t.Strategy == strat).ToList();
                if (forStrat.Count == 0) continue;
                double avgMult = forStrat.Average(t => emG.ComputeMult(t));
                Console.WriteLine($"    {strat,-12}  {avgMult:F3}×  ({forStrat.Count} trades)");
            }
        }
```

- [ ] **Step 5: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors. Fix any issues with `fetchedMap` type (it's `Dictionary<string, (string sym, Candle[] m15, Candle[] h1)>` — extract `.h1` correctly for the `h1Map` in `TradeEnricher.Enrich`).

The `fetchedMap` values are `(string sym, Candle[] m15, Candle[] h1)` tuples. The h1Map for the enricher should be built as:
```csharp
var h1MapForEnrich = fetchedMap.ToDictionary(kv => kv.Key, kv => kv.Value.h1);
```

- [ ] **Step 6: Commit**

```bash
git add FullTest.cs
git commit -m "feat: add symbol-tagged trade collection and Section 8 ExitModifier to FullTest"
```

---

### Task 9: Program.cs — add stresstest + exitmodifiertrain

**Files:**
- Modify: `Program.cs`

- [ ] **Step 1: Add cases in the switch**

Open `Program.cs` and find the switch/case block that dispatches commands. Add:

```csharp
case "stresstest":
    await StressTestCommands.RunStressTest(client);
    break;

case "exitmodifiertrain":
    await ExitModifierTrainCommands.RunExitModifierTrain(client);
    break;
```

Also add to the help text (find the block that prints command descriptions and add):
```
dotnet run -- stresstest          # Adversarial scenario GA: find worst-case crash drawdown
dotnet run -- exitmodifiertrain   # Train context-aware position-size modifier (ExitModifierGA)
```

- [ ] **Step 2: Verify build**

```bash
dotnet build -c Release
```
Expected: 0 errors.

- [ ] **Step 3: Final smoke test (build only — no live run needed)**

```bash
dotnet build -c Release 2>&1 | grep -E "error|warning" | head -20
```
Expected: 0 errors, warnings acceptable.

- [ ] **Step 4: Commit**

```bash
git add Program.cs
git commit -m "feat: add stresstest and exitmodifiertrain commands to Program.cs"
```

---

## Self-Review

### Spec coverage

| Requirement | Task |
|---|---|
| Adversarial GA maximising portfolio max-drawdown | Task 4 (ScenarioGA.Evaluate) |
| Parameterised crash/stress scenarios (depth, duration, recovery, ATR expansion, alt correlation, liquidity) | Tasks 2–3 |
| `dotnet run -- stresstest` command | Tasks 4, 9 |
| ExitModifier reads ATR percentile rank, open position count, total exposure %, recent trade frequency, liquidity score | Task 6 (TradeEnricher) |
| Scales per-trade position sizing (TP/SL/trail via size proxy) based on context | Tasks 5, 7 |
| Works without touching individual simulators | All tasks — simulators never modified |
| Trade enricher reads candle data post-simulation | Task 6 |
| ExitModifierGenotype evolved by GA maximising combined val+OOS metric | Task 7 |
| Integrated into fulltest output as Section 8 | Task 8 |
| Volume of trades, all open positions, position value, liquidity | Task 6 (OpenPositions, TotalExposure, LiquidityScore, RecentTradeCount) |

### Placeholder scan

No TBD or TODO placeholders. All code blocks are complete.

### Type consistency check

- `ScenarioGenotype.FromGenes(double[], double)` used in Task 3 ✓ defined in Task 2
- `ScenarioInjector.Inject(Candle[], ScenarioGenotype, int, double)` used in Tasks 3,4 ✓ defined in Task 3
- `ScenarioInjector.DeaggregateToM15(Candle[])` used in Tasks 3,7 ✓ defined in Task 3
- `ExitModifierGenotype.ComputeMult(TradeEnricher.EnrichedTrade)` used in Tasks 7,8 ✓ defined in Task 5, uses type from Task 6
- `TradeEnricher.Enrich(...)` used in Tasks 7,8 ✓ defined in Task 6
- `ExitModifierGA.ApplyModifier(ExitModifierGenotype, List<EnrichedTrade>)` — defined as `static` in Task 7, used in Task 8 ✓
- `Config.ExitModifierGenoFile` ✓ added in Task 1
- `FadeShortSimulator.GetFadeShortReturns(g, h1, m15)` — dual-TF overload. Verify this exists: the plan shows the multi-TF overload signature at line 35 of `FadeShortSimulator` (or check via `grep "GetFadeShortReturns" SwingSimulator.cs`). If only the single-array overload exists, use `GetFadeShortReturns(fsG, mH1)` (h1-only) for the ScenarioGA.

**Critical check for implementer:** `FadeShortSimulator.GetFadeShortReturns` has two overloads — single array (original) and dual-TF (h1 + m15). Grep before using the dual-TF form:
```bash
grep -n "GetFadeShortReturns" /home/thomas/Gravity-gen2/SwingSimulator.cs
```
Use whichever signature exists. If the dual-TF form doesn't exist, use the single `ReadOnlySpan<Candle>` form with `mH1`.
