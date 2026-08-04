using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class HighVolTrainCommands
{
    private const int MinTradesPerFold = 20;
    private const double PosFrac = 0.10;

    public static async Task RunHighVolTrain(BybitRestClient client, string[]? args = null)
    {
        Console.WriteLine("=== Gravity-gen2 | HIGHVOLTRAIN (high-volatility optimized variants) ===");
        Console.WriteLine("Training FadeShort-HV + DipLong-HV + SwingLong-HV + RipShort-HV (5-fold WFV, 5% embargo)\n");

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
        var fsBest = TrainFadeShortHighVol(passed, cfg);
        if (fsBest != null)
        {
            string fsPath = "genotypes/fade_short_highvol_genotype.json";
            File.WriteAllText(fsPath, JsonSerializer.Serialize(FadeShortGenotypeDto.From(fsBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {fsPath}\n");
        }

        Console.WriteLine("══ DIPLONG HIGHVOL ════════════════════════════════════════════════════");
        var dlBest = TrainDipLongHighVol(passed, cfg);
        if (dlBest != null)
        {
            string dlPath = "genotypes/dip_long_highvol_genotype.json";
            File.WriteAllText(dlPath, JsonSerializer.Serialize(DipLongGenotypeDto.From(dlBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {dlPath}\n");
        }

        Console.WriteLine("══ SWINGLONG HIGHVOL ══════════════════════════════════════════════════");
        var slBest = TrainSwingLongHighVol(passed, cfg);
        if (slBest != null)
        {
            string slPath = "genotypes/swing_long_highvol_genotype.json";
            File.WriteAllText(slPath, JsonSerializer.Serialize(SwingLongGenotypeDto.From(slBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {slPath}\n");
        }

        Console.WriteLine("══ RIPSHORT HIGHVOL ═══════════════════════════════════════════════════");
        var rsBest = TrainRipShortHighVol(passed, cfg);
        if (rsBest != null)
        {
            string rsPath = "genotypes/rip_short_highvol_genotype.json";
            File.WriteAllText(rsPath, JsonSerializer.Serialize(RipShortGenotypeDto.From(rsBest, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {rsPath}\n");
        }

        Console.WriteLine("All high-vol variants trained. Next: dotnet run -- combinedbacktest");
    }

    private static double ComputeFitness(double[] foldScores, int totalFoldTrades)
    {
        int k = foldScores.Length;
        double mean = foldScores.Average();
        double std = Math.Sqrt(foldScores.Select(s => (s - mean) * (s - mean)).Average());
        double avgN = (double)totalFoldTrades / k;
        double stdMult = Math.Clamp(7.5 / Math.Max(1.0, avgN / 14.0), 0.75, 2.0);
        return mean - stdMult * std;
    }

    private static FadeShortGenotype? TrainFadeShortHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        var rng = new Random(42);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => FadeShortGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
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
        double[] foldScores = new double[folds];
        int totalTrades = 0;

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

            totalTrades += allReturns.Count;
            foldScores[fold] = FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.0);
        }

        return ComputeFitness(foldScores, totalTrades);
    }

    private static DipLongGenotype? TrainDipLongHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        var rng = new Random(42);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => DipLongGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
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
        double[] foldScores = new double[folds];
        int totalTrades = 0;

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

            totalTrades += allReturns.Count;
            foldScores[fold] = FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.5);
        }

        return ComputeFitness(foldScores, totalTrades);
    }

    private static SwingLongGenotype? TrainSwingLongHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        var rng = new Random(42);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => SwingLongGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
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
        double[] foldScores = new double[folds];
        int totalTrades = 0;

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

            totalTrades += allReturns.Count;
            foldScores[fold] = FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.5);
        }

        return ComputeFitness(foldScores, totalTrades);
    }

    private static RipShortGenotype? TrainRipShortHighVol(List<(string Sym, Candle[] H1, Candle[] M15)> coins, FitnessConfig cfg)
    {
        var rng = new Random(42);
        int popSize = 100, generations = 200, eliteCount = 15;
        var population = Enumerable.Range(0, popSize)
            .Select(_ => RipShortGenotype.RandomHighVol(rng))
            .ToList();

        for (int gen = 0; gen < generations; gen++)
        {
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
        double[] foldScores = new double[folds];
        int totalTrades = 0;

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

            totalTrades += allReturns.Count;
            foldScores[fold] = FoldScoreHelper.Canonical(allReturns, PosFrac, MinTradesPerFold, cfg, statBonusCeiling: 1.5);
        }

        return ComputeFitness(foldScores, totalTrades);
    }
}
