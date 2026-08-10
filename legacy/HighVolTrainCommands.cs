using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class HighVolTrainCommands
{
    private const int MinTradesPerFold = 20;
    private const double PosFrac = 0.10;

    // Historical hardcoded seed of the four inline GAs in this file. Kept as the default so an
    // unseeded highvoltrain reproduces the previously committed high-vol genotypes exactly.
    internal const int DefaultHighVolSeed = 42;

    public static async Task RunHighVolTrain(BybitRestClient client, string[]? args = null)
    {
        Console.WriteLine("=== Gravity-gen2 | HIGHVOLTRAIN (high-volatility optimized variants) ===");
        Console.WriteLine("Training FadeShort-HV + DipLong-HV + SwingLong-HV + RipShort-HV (5-fold WFV, 5% embargo)");

        // The four inline GAs below were already deterministic — they hardcoded `new Random(42)`
        // — so the defect here was not irreproducibility but that the seed was neither settable
        // nor printed. DefaultHighVolSeed keeps 42 as the default so an unseeded rerun reproduces
        // every previously trained high-vol genotype bit-for-bit; --seed N overrides it.
        //
        // NOTE ON SELECTION: these loops use truncation selection (both parents drawn uniformly
        // from the top half of the population) with elitism 15/100, not the tournament the
        // strategy GAs use, and they carry no stagnation handling at all. They are therefore
        // outside the scope of the tournament-k and cataclysm changes; converting them to the
        // shared GaSearch operators is a separate, validated change.
        int seed = GaSearch.ResolveSeed(args) ?? DefaultHighVolSeed;
        GaSearch.AnnounceCommandSeed("highvoltrain", seed);
        Console.WriteLine();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, h1, m15: m15.ToArray());
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);
        Console.WriteLine($"  Done.\n");

        var passed = new List<(string Sym, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1, m15) in fetched)
        {
            if (h1.Length < 300) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;
            passed.Add((sym, h1, m15));
        }
        Console.WriteLine($"  {passed.Count} coins pass volume filter\n");

        var cfg = FitnessConfig.Load();

        Console.WriteLine("══ FADESHORT HIGHVOL ══════════════════════════════════════════════════");
        var fsBest = TrainFadeShortHighVol(passed, cfg, seed);
        if (fsBest != null)
        {
            string fsPath = "genotypes/fade_short_highvol_genotype.json";
            File.WriteAllText(fsPath, JsonSerializer.Serialize(FadeShortGenotypeDto.From(fsBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {fsPath}\n");
        }

        Console.WriteLine("══ DIPLONG HIGHVOL ════════════════════════════════════════════════════");
        var dlBest = TrainDipLongHighVol(passed, cfg, seed);
        if (dlBest != null)
        {
            string dlPath = "genotypes/dip_long_highvol_genotype.json";
            File.WriteAllText(dlPath, JsonSerializer.Serialize(DipLongGenotypeDto.From(dlBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {dlPath}\n");
        }

        Console.WriteLine("══ SWINGLONG HIGHVOL ══════════════════════════════════════════════════");
        var slBest = TrainSwingLongHighVol(passed, cfg, seed);
        if (slBest != null)
        {
            string slPath = "genotypes/swing_long_highvol_genotype.json";
            File.WriteAllText(slPath, JsonSerializer.Serialize(SwingLongGenotypeDto.From(slBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {slPath}\n");
        }

        Console.WriteLine("══ RIPSHORT HIGHVOL ═══════════════════════════════════════════════════");
        var rsBest = TrainRipShortHighVol(passed, cfg, seed);
        if (rsBest != null)
        {
            string rsPath = "genotypes/rip_short_highvol_genotype.json";
            File.WriteAllText(rsPath, JsonSerializer.Serialize(RipShortGenotypeDto.From(rsBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {rsPath}\n");
        }

        Console.WriteLine("All high-vol variants trained. Next: dotnet run -- combinedbacktest");
    }

    // This was a fourth private copy of the old `mean - stdMult*std` aggregator. It carried both
    // defects that were fixed in FoldScoreHelper.AggregateFoldScores:
    //   1. NON-MONOTONE — past a z-score of 1/stdMult, raising a fold's score LOWERED fitness,
    //      so the GA selected against its own best folds.
    //   2. SENTINEL PADDING — it read a fixed double[folds], so a fold that never reached
    //      MinTradesPerFold kept Canonical's constant -1.0 and was averaged in as if it were a
    //      real score, which is what made the variance term inject a constant.
    // Both are gone: callers now collect only the folds that produced a score, and pass how many
    // were ATTEMPTED so partial coverage is penalised instead of rewarded.
    //
    // `d` is the genotype's parameter count, read from its own Bounds table rather than the
    // hardcoded 14 this used to assume — that was wrong for every strategy here except by
    // coincidence, and it feeds the VC-proportional lambda.
    private static double ComputeFitness(
        List<double> foldScores, List<int> foldTradeCounts, int d, int attemptedFolds)
        => FoldScoreHelper.AggregateFoldScores(foldScores, foldTradeCounts, d, attemptedFolds);

    private static FadeShortGenotype? TrainFadeShortHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg, int seed)
    {
        var rng = new Random(seed);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => FadeShortGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
            // WARNING: this parallel body must stay RNG-FREE. System.Random is not thread-safe;
            // a single `rng` draw added here would silently corrupt its internal state (and
            // destroy reproducibility) with no exception to point at it.
            Parallel.ForEach(population, g =>
            {
                g.Fitness = EvaluateFadeShortHighVol(g, coins, cfg);
            });

            var sorted = population.OrderByDescending(g => g.Fitness).ToList();
            var next = sorted.Take(eliteCount).ToList();

            while (next.Count < popSize)
            {
                var p1 = sorted[rng.Next(sorted.Count / 2)];
                var p2 = sorted[rng.Next(sorted.Count / 2)];
                var child = FadeShortGenotype.Crossover(p1, p2, rng).Mutate(rng, 0.3);
                next.Add(child);
            }
            population = next;

            if (gen % 50 == 0)
            {
                var top = sorted[0];
                Console.WriteLine($"  Gen {gen}: best F={top.Fitness:F4} {top}");
            }
        }

        var bestFs = population.OrderByDescending(g => g.Fitness).First();
        Console.WriteLine($"  Final: {bestFs}");
        return bestFs.Fitness > 0 ? bestFs : null;
    }

    private static double EvaluateFadeShortHighVol(FadeShortGenotype g, List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        int folds = 5;
        var foldScores = new List<double>();
        var foldCounts = new List<int>();

        for (int fold = 0; fold < folds; fold++)
        {
            var allReturns = new List<double>();
            foreach (var (sym, h1, m15) in coins)
            {
                if (h1.Length < 300) continue;
                int totalBars = h1.Length;
                int foldSize = totalBars / folds;
                int embargo = (int)(totalBars * 0.05);
                int testStart = fold * foldSize + (fold > 0 ? embargo : 0);
                int testEnd = Math.Min((fold + 1) * foldSize, totalBars);
                if (testEnd - testStart < 50) continue;

                var testH1 = h1[testStart..testEnd];
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15End = Math.Min(testEnd * 4, m15.Length);
                if (m15End - m15Start < 200) continue;
                var testM15 = m15[m15Start..m15End];

                var trades = FadeShortSimulator.GetFadeShortReturns(g, testH1, testM15);
                allReturns.AddRange(trades.Select(t => t.Return));
            }

            // A fold below MinTradesPerFold gets Canonical's constant -1.0 sentinel. Feeding that
            // into the aggregate injects a constant into the spread term, so drop it and record
            // only that it was attempted.
            if (allReturns.Count < MinTradesPerFold) continue;
            foldScores.Add(FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.0));
            foldCounts.Add(allReturns.Count);
        }

        return ComputeFitness(foldScores, foldCounts, FadeShortGenotype.Bounds.GetLength(0), folds);
    }

    private static DipLongGenotype? TrainDipLongHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg, int seed)
    {
        var rng = new Random(seed);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => DipLongGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
            // WARNING: this parallel body must stay RNG-FREE. System.Random is not thread-safe;
            // a single `rng` draw added here would silently corrupt its internal state (and
            // destroy reproducibility) with no exception to point at it.
            Parallel.ForEach(population, g =>
            {
                g.Fitness = EvaluateDipLongHighVol(g, coins, cfg);
            });

            var sorted = population.OrderByDescending(g => g.Fitness).ToList();
            var next = sorted.Take(eliteCount).ToList();

            while (next.Count < popSize)
            {
                var p1 = sorted[rng.Next(sorted.Count / 2)];
                var p2 = sorted[rng.Next(sorted.Count / 2)];
                var child = DipLongGenotype.Crossover(p1, p2, rng).Mutate(rng, 0.3);
                next.Add(child);
            }
            population = next;

            if (gen % 50 == 0)
            {
                var top = sorted[0];
                Console.WriteLine($"  Gen {gen}: best F={top.Fitness:F4} {top}");
            }
        }

        var bestDl = population.OrderByDescending(g => g.Fitness).First();
        Console.WriteLine($"  Final: {bestDl}");
        return bestDl.Fitness > 0 ? bestDl : null;
    }

    private static double EvaluateDipLongHighVol(DipLongGenotype g, List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        int folds = 5;
        var foldScores = new List<double>();
        var foldCounts = new List<int>();

        for (int fold = 0; fold < folds; fold++)
        {
            var allReturns = new List<double>();
            foreach (var (sym, h1, m15) in coins)
            {
                if (h1.Length < 300) continue;
                int totalBars = h1.Length;
                int foldSize = totalBars / folds;
                int embargo = (int)(totalBars * 0.05);
                int testStart = fold * foldSize + (fold > 0 ? embargo : 0);
                int testEnd = Math.Min((fold + 1) * foldSize, totalBars);
                if (testEnd - testStart < 50) continue;

                var testH1 = h1[testStart..testEnd];
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15End = Math.Min(testEnd * 4, m15.Length);
                if (m15End - m15Start < 200) continue;
                var testM15 = m15[m15Start..m15End];

                var trades = DipLongSimulator.GetDipLongReturns(g, testH1, testM15);
                allReturns.AddRange(trades.Select(t => t.Return));
            }

            // A fold below MinTradesPerFold gets Canonical's constant -1.0 sentinel. Feeding that
            // into the aggregate injects a constant into the spread term, so drop it and record
            // only that it was attempted.
            if (allReturns.Count < MinTradesPerFold) continue;
            foldScores.Add(FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.5));
            foldCounts.Add(allReturns.Count);
        }

        return ComputeFitness(foldScores, foldCounts, DipLongGenotype.Bounds.GetLength(0), folds);
    }

    private static SwingLongGenotype? TrainSwingLongHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg, int seed)
    {
        var rng = new Random(seed);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => SwingLongGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
            // WARNING: this parallel body must stay RNG-FREE. System.Random is not thread-safe;
            // a single `rng` draw added here would silently corrupt its internal state (and
            // destroy reproducibility) with no exception to point at it.
            Parallel.ForEach(population, g =>
            {
                g.Fitness = EvaluateSwingLongHighVol(g, coins, cfg);
            });

            var sorted = population.OrderByDescending(g => g.Fitness).ToList();
            var next = sorted.Take(eliteCount).ToList();

            while (next.Count < popSize)
            {
                var p1 = sorted[rng.Next(sorted.Count / 2)];
                var p2 = sorted[rng.Next(sorted.Count / 2)];
                var child = SwingLongGenotype.Crossover(p1, p2, rng).Mutate(rng, 0.3);
                next.Add(child);
            }
            population = next;

            if (gen % 50 == 0)
            {
                var top = sorted[0];
                Console.WriteLine($"  Gen {gen}: best F={top.Fitness:F4} {top}");
            }
        }

        var bestSl = population.OrderByDescending(g => g.Fitness).First();
        Console.WriteLine($"  Final: {bestSl}");
        return bestSl.Fitness > 0 ? bestSl : null;
    }

    private static double EvaluateSwingLongHighVol(SwingLongGenotype g, List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        int folds = 5;
        var foldScores = new List<double>();
        var foldCounts = new List<int>();

        for (int fold = 0; fold < folds; fold++)
        {
            var allReturns = new List<double>();
            foreach (var (sym, h1, m15) in coins)
            {
                if (h1.Length < 300) continue;
                int totalBars = h1.Length;
                int foldSize = totalBars / folds;
                int embargo = (int)(totalBars * 0.05);
                int testStart = fold * foldSize + (fold > 0 ? embargo : 0);
                int testEnd = Math.Min((fold + 1) * foldSize, totalBars);
                if (testEnd - testStart < 50) continue;

                var testH1 = h1[testStart..testEnd];
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15End = Math.Min(testEnd * 4, m15.Length);
                if (m15End - m15Start < 200) continue;
                var testM15 = m15[m15Start..m15End];

                var trades = SwingLongSimulator.GetSwingLongReturns(g, testH1, testM15);
                allReturns.AddRange(trades.Select(t => t.Return));
            }

            // A fold below MinTradesPerFold gets Canonical's constant -1.0 sentinel. Feeding that
            // into the aggregate injects a constant into the spread term, so drop it and record
            // only that it was attempted.
            if (allReturns.Count < MinTradesPerFold) continue;
            foldScores.Add(FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.5));
            foldCounts.Add(allReturns.Count);
        }

        return ComputeFitness(foldScores, foldCounts, SwingLongGenotype.Bounds.GetLength(0), folds);
    }

    private static RipShortGenotype? TrainRipShortHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg, int seed)
    {
        var rng = new Random(seed);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => RipShortGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
            // WARNING: this parallel body must stay RNG-FREE. System.Random is not thread-safe;
            // a single `rng` draw added here would silently corrupt its internal state (and
            // destroy reproducibility) with no exception to point at it.
            Parallel.ForEach(population, g =>
            {
                g.Fitness = EvaluateRipShortHighVol(g, coins, cfg);
            });

            var sorted = population.OrderByDescending(g => g.Fitness).ToList();
            var next = sorted.Take(eliteCount).ToList();

            while (next.Count < popSize)
            {
                var p1 = sorted[rng.Next(sorted.Count / 2)];
                var p2 = sorted[rng.Next(sorted.Count / 2)];
                var child = RipShortGenotype.Crossover(p1, p2, rng).Mutate(rng, 0.3);
                next.Add(child);
            }
            population = next;

            if (gen % 50 == 0)
            {
                var top = sorted[0];
                Console.WriteLine($"  Gen {gen}: best F={top.Fitness:F4} {top}");
            }
        }

        var bestRs = population.OrderByDescending(g => g.Fitness).First();
        Console.WriteLine($"  Final: {bestRs}");
        return bestRs.Fitness > 0 ? bestRs : null;
    }

    private static double EvaluateRipShortHighVol(RipShortGenotype g, List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        int folds = 5;
        var foldScores = new List<double>();
        var foldCounts = new List<int>();

        for (int fold = 0; fold < folds; fold++)
        {
            var allReturns = new List<double>();
            foreach (var (sym, h1, m15) in coins)
            {
                if (h1.Length < 300) continue;
                int totalBars = h1.Length;
                int foldSize = totalBars / folds;
                int embargo = (int)(totalBars * 0.05);
                int testStart = fold * foldSize + (fold > 0 ? embargo : 0);
                int testEnd = Math.Min((fold + 1) * foldSize, totalBars);
                if (testEnd - testStart < 50) continue;

                var testH1 = h1[testStart..testEnd];
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15End = Math.Min(testEnd * 4, m15.Length);
                if (m15End - m15Start < 200) continue;
                var testM15 = m15[m15Start..m15End];

                var trades = RipShortSimulator.GetRipShortReturns(g, testH1, testM15);
                allReturns.AddRange(trades.Select(t => t.Return));
            }

            // A fold below MinTradesPerFold gets Canonical's constant -1.0 sentinel. Feeding that
            // into the aggregate injects a constant into the spread term, so drop it and record
            // only that it was attempted.
            if (allReturns.Count < MinTradesPerFold) continue;
            // Ceiling 1.0, matching RipShortGA rather than the 1.5 this used to pass. RipShort's
            // ceiling is deliberately tighter: its bear-window sample is sparse enough that an
            // uncapped stat-bonus stack compounds a single lucky fold (train F=15718 against an
            // OOS PF of 0.88). That argument is at least as strong in high-volatility windows,
            // and running one strategy under two different objectives is the divergence this
            // whole pass exists to remove.
            foldScores.Add(FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.0));
            foldCounts.Add(allReturns.Count);
        }

        return ComputeFitness(foldScores, foldCounts, RipShortGenotype.Bounds.GetLength(0), folds);
    }
}
