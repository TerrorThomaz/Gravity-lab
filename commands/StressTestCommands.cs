using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class StressTestCommands
{
    private static readonly int[] Seeds = [42, 137, 271, 512, 999];

    public static async Task RunStressTest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | STRESS TEST ({Seeds.Length} seeds · {ScenarioGA.ScenarioCoins.Length} coins each) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing Grid genotype."); return; }

        var fsG     = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG   = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        var flG     = (FadeLongGenotype?)null;  // disabled — net drag in OOS
        var dlG     = File.Exists(Config.DipLongGenoFile)       ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()           : null;
        var slG     = File.Exists(Config.SwingLongGenoFile)     ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype()       : null;
        var routerG     = File.Exists(Config.RouterGenoFile)      ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()     : null;
        var guardG      = File.Exists(Config.DrawdownGuardGenoFile) ? JsonSerializer.Deserialize<DrawdownGuardGenotypeDto>(File.ReadAllText(Config.DrawdownGuardGenoFile))!.ToGenotype() : null;
        var dynamicGuardG = File.Exists(Config.DynamicGuardGenoFile) ? JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype() : null;

        if (dynamicGuardG != null) Console.WriteLine($"  Dynamic guard: {dynamicGuardG}");
        else if (guardG   != null) Console.WriteLine($"  Panic manager: {guardG}");

        var allSyms = ScenarioGA.ScenarioCoins.Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        Console.WriteLine($"\n  Fetching {allSyms.Length} coins (1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = allSyms.Select(async sym =>
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
        var fetched = await Task.WhenAll(fetchTasks);
        var h1Map   = fetched.Where(f => f.h1.Length >= 200).ToDictionary(f => f.sym, f => f.h1);
        Console.WriteLine($"  Done ({h1Map.Count} coins loaded).\n");

        // Router session (shared across all runs)
        RegimeRouterSession? session = null;
        if (routerG != null && h1Map.TryGetValue("BTCUSDT", out var btcForSession) && btcForSession.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcForSession);
            RegimeBar[]? ethSeries = h1Map.TryGetValue("ETHUSDT", out var ethForSession) && ethForSession.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethForSession) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        double baselineDd = ComputeBaselineDD(h1Map, fsG, gridG, flG, dlG, slG, session);
        Console.WriteLine($"  Baseline DD (unmorphed): {baselineDd:F1}%\n");

        Console.WriteLine($"  {"Seed",5}  {"Worst DD",9}  {"Guarded DD",11}  Scenario");
        Console.WriteLine($"  {new string('─', 70)}");

        Console.WriteLine($"  Running {Seeds.Length} seeds in parallel...");
        var seedTasks = Seeds.Select(seed => Task.Run(() =>
        {
            var ga   = new ScenarioGA(populationSize: 40, generations: 60, verbose: false, seed: seed);
            var best = ga.Run(h1Map, fsG, gridG, flG, dlG, slG, routerG);
            double guardedDd = dynamicGuardG != null
                ? ScenarioGA.EvaluateScenario(best, h1Map, fsG, gridG, flG, dlG, slG, session, null, dynamicGuardG)
                : guardG != null
                    ? ScenarioGA.EvaluateScenario(best, h1Map, fsG, gridG, flG, dlG, slG, session, guardG)
                    : best.Fitness;
            return (seed, best, guardedDd);
        })).ToList();
        var results = (await Task.WhenAll(seedTasks))
            .OrderBy(r => r.seed).ToList();

        h1Map.TryGetValue("BTCUSDT", out var btcH1);
        foreach (var (seed, best, guardedDd) in results)
        {
            int injBar  = btcH1 != null ? (int)(btcH1.Length * best.InjectionOffsetFrac) : 0;
            var injDate = btcH1 != null && injBar < btcH1.Length ? btcH1[injBar].Time : DateTime.MinValue;
            string guardStr = (dynamicGuardG != null || guardG != null) ? $"{guardedDd,7:F1}%" : "   n/a  ";
            Console.WriteLine($"  {seed,5}  {best.Fitness,7:F1}%    {guardStr}    "
                + $"depth={best.CrashDepthPct:P0} dur={best.CrashDurationHours:F0}h "
                + $"beta={best.AltBetaPct:F2} atr={best.AtrExpansionPeak:F1}x  ({injDate:yyyy-MM-dd})");
        }

        // Summary
        var dds   = results.Select(r => r.best.Fitness).OrderBy(x => x).ToList();
        var gDds  = results.Select(r => r.guardedDd).OrderBy(x => x).ToList();
        var worst = results.MaxBy(r => r.best.Fitness)!;

        Console.WriteLine($"\n  {new string('─', 70)}");
        Console.WriteLine($"  Unguarded DD  —  min {dds[0]:F1}%  median {dds[dds.Count/2]:F1}%  max {dds[^1]:F1}%");
        bool hasGuard = dynamicGuardG != null || guardG != null;
        if (hasGuard)
            Console.WriteLine($"  Guarded DD    —  min {gDds[0]:F1}%  median {gDds[gDds.Count/2]:F1}%  max {gDds[^1]:F1}%");

        // Full detail on the single worst scenario found
        int worstBar  = btcH1 != null ? (int)(btcH1.Length * worst.best.InjectionOffsetFrac) : 0;
        var worstDate = btcH1 != null && worstBar < btcH1.Length ? btcH1[worstBar].Time : DateTime.MinValue;

        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  HARDEST SCENARIO FOUND  (seed {worst.seed})");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  Crash depth:       {worst.best.CrashDepthPct:P0}  (alt beta x{worst.best.AltBetaPct:F2})");
        Console.WriteLine($"  Crash duration:    {worst.best.CrashDurationHours:F0}h  →  recovery {worst.best.RecoveryHours:F0}h");
        Console.WriteLine($"  ATR expansion:     {worst.best.AtrExpansionPeak:F1}x  at trough");
        Console.WriteLine($"  Liquidity squeeze: {worst.best.LiquiditySqueezeHours:F0}h of reduced volume");
        Console.WriteLine($"  Injection point:   bar {worstBar} / {btcH1?.Length ?? 0}  ({worstDate:yyyy-MM-dd})");
        Console.WriteLine($"\n  Portfolio max-drawdown:  {worst.best.Fitness:F1}%  (baseline {baselineDd:F1}%)");
        string guardLabel = dynamicGuardG != null ? "With dynamic guard:  " : "With panic manager:  ";
        if (hasGuard)
            Console.WriteLine($"  {guardLabel}    {worst.guardedDd:F1}%  ({worst.guardedDd - worst.best.Fitness:+0.0;-0.0}pp vs unguarded)");
        Console.WriteLine($"  Stress multiplier:       {worst.best.Fitness / Math.Max(baselineDd, 1.0):F2}x");
    }

    private static double ComputeBaselineDD(
        IReadOnlyDictionary<string, Candle[]> h1Map,
        FadeShortGenotype    fsG,
        GridGenotype         gridG,
        FadeLongGenotype?    flG,
        DipLongGenotype?     dlG,
        SwingLongGenotype?   slG,
        RegimeRouterSession? session)
    {
        var trades = new List<(DateTime, double, double, TimeSpan)>();
        foreach (var sym in ScenarioGA.ScenarioCoins.Where(h1Map.ContainsKey))
        {
            var h1  = h1Map[sym];
            var m15 = ScenarioInjector.DeaggregateToM15(h1);

            var fsT  = FadeShortSimulator.GetFadeShortReturns(fsG, h1, m15);
            double fc = fsT.Count > 0 ? Simulator.ComputeConfidence(fsT.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _) in fsT) trades.Add((t, r, fc, TimeSpan.FromHours(fsG.MaxHoldCandles)));

            var gRaw   = GridSimulator.GetGridReturns(gridG, h1);
            var gGated = session != null ? gRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : gRaw;
            double gc  = gGated.Count > 0 ? Simulator.ComputeConfidence(gGated.Select(t => t.Return).ToList()) : 0.03;
            foreach (var (t, r, _) in gGated) trades.Add((t, r, gc, TimeSpan.FromHours(gridG.MaxHoldCandles)));

            if (flG != null && h1.Length >= 200 && m15.Length >= 800)
            {
                var flRaw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
                var flGated = session != null ? flRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : flRaw;
                double lc   = flGated.Count > 0 ? Simulator.ComputeConfidence(flGated.Select(t => t.Return).ToList()) : 0.03;
                foreach (var (t, r, _, _) in flGated) trades.Add((t, r, lc, TimeSpan.FromHours(flG.MaxHoldCandles)));
            }
            if (dlG != null && h1.Length >= 200 && m15.Length >= 800)
            {
                var dlRaw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
                var dlGated = session != null ? dlRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : dlRaw;
                double dc   = dlGated.Count > 0 ? Simulator.ComputeConfidence(dlGated.Select(t => t.Return).ToList()) : 0.03;
                foreach (var (t, r, _, _) in dlGated) trades.Add((t, r, dc, TimeSpan.FromHours(dlG.MaxHoldCandles)));
            }
            if (slG != null && h1.Length >= 200 && m15.Length >= 800)
            {
                var slRaw   = SwingLongSimulator.GetSwingLongReturns(slG, h1, m15);
                var slGated = session != null ? slRaw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : slRaw;
                double sc   = slGated.Count > 0 ? Simulator.ComputeConfidence(slGated.Select(t => t.Return).ToList()) : 0.03;
                foreach (var (t, r, _) in slGated) trades.Add((t, r, sc, TimeSpan.FromHours(slG.MaxHoldCandles)));
            }
        }

        if (trades.Count < 5) return 0;
        return Simulator.SimulatePortfolioExposureCapped(
            trades.OrderBy(t => t.Item1).ToList(), Config.MaxTotalExposurePct, maxPositionFrac: 0.05).MaxDrawdownPct;
    }
}
