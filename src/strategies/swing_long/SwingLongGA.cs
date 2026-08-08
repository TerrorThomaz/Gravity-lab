using System.Collections.Concurrent;

namespace TradingGA;

// SwingLong GA — bull-regime RSI bullish divergence + bullish BoS.
// 5-fold walk-forward CV on TIME-based fold windows shared across coins, falling back to
// per-coin percentage folds when no BTC regime series is supplied (same pattern as DipLongGA).
// Uses canonical FoldScore via FoldScoreHelper.
// Fitness = coverage x [lambda x CVaR_0.4(fold_scores) + (1 - lambda) x mean(fold_scores)]
// over the SURVIVING walk-forward folds (those that reached MinTradesPerFold trades), where
// coverage = surviving/attempted folds and lambda is VC-proportional on avg N/d. Monotone
// non-decreasing in every fold score by construction — see FoldScoreHelper.AggregateFoldScores
// for why `mean - stdMult x std` was not.
public class SwingLongGA
{
    public record CoinData(
        ReadOnlyMemory<Candle> TrainH1,  ReadOnlyMemory<Candle> ValH1,
        ReadOnlyMemory<Candle> TrainM15, ReadOnlyMemory<Candle> ValM15,
        double Weight = 1.0);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();
    private readonly Func<DateTime, double>? _tradeGate;
    private readonly FitnessConfig _cfg;
    private readonly RegimeBar[]? _btcSeries;

    private const int MinTradesPerFold = 20;
    private const int D                = 14;

    public SwingLongGA(
        int populationSize    = 60,
        int generations       = 100,
        int eliteCount        = 10,
        int migrationInterval = 10,
        bool verbose          = false,
        Func<DateTime, double>? tradeGate = null,
        FitnessConfig? cfg = null,
        RegimeBar[]? btcSeries = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _tradeGate         = tradeGate;
        _cfg               = cfg ?? new FitnessConfig();
        _btcSeries         = btcSeries;
    }

    private double Fitness(SwingLongGenotype g, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, h1: useValidation ? c.ValH1 : c.TrainH1,
                             m15: useValidation ? c.ValM15 : c.TrainM15))
            .Where(x => x.h1.Length >= 100 && x.m15.Length >= 400)
            .ToList();
        if (validCoins.Count == 0) return 0;

        double posFrac = Math.Clamp(g.PositionSizePct, 0.01, 0.05);

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => SwingLongSimulator.GetSwingLongReturns(g, x.h1.Span, x.m15.Span)
                    .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, w); })
                    .Where(t => t.w >= 0.05)
                    .Select(t => t.Return))
                .ToList();
            return FoldScoreHelper.Canonical(all, posFrac, MinTradesPerFold, _cfg, statBonusCeiling: 1.5);
        }

        // Walk-forward folds, k of them, cut on CALENDAR TIME rather than array indices.
        //
        // With a BTC regime series available, fold boundaries are computed once on BTC
        // (balanced on the number of active bull bars, with an embargo gap) and handed to
        // every coin as [Start, End) time windows; each coin then binary-searches that
        // window into its own h1/m15 arrays. Index-space bounds can NOT be shared across
        // coins — a coin's training array is an 80% split of its own (variable-length)
        // history, so index i is a different calendar date on every symbol, and coins
        // shorter than a fold's start index would drop out of every later fold and dump
        // their entire history into fold 0.
        //
        // Without a BTC series, fall back to per-coin percentage folds: each coin slices
        // its own history into k equal parts, which is alignment-safe by construction.
        int k = folds;

        (DateTime Start, DateTime End)[]? windows = _btcSeries != null
            ? FoldScoreHelper.ComputeRegimeAwareFoldWindows(_btcSeries, r => r == MarketRegime.Bull, k, _cfg.EmbargoPct)
            : null;

        var foldScores = new List<double>();
        var foldCounts = new List<int>();
        // Folds ATTEMPTED, including the thin ones skipped below — the aggregator scales
        // by surviving/attempted so that concentrating all activity into one favourable
        // market window can no longer beat trading consistently across all of them.
        int attemptedFolds = 0;

        for (int f = 0; f < k; f++)
        {
            attemptedFolds++;
            var foldReturns = new List<double>();
            foreach (var (coin, h1, m15) in validCoins)
            {
                int startH1, endH1, startM15, endM15;
                if (windows != null)
                {
                    var (winStart, winEnd) = windows[f];
                    (startH1,  endH1)  = FoldScoreHelper.RangeForWindow(h1.Span,  winStart, winEnd);
                    (startM15, endM15) = FoldScoreHelper.RangeForWindow(m15.Span, winStart, winEnd);
                }
                else
                {
                    (startH1, endH1) = FoldScoreHelper.PerCoinFoldRange(h1.Length, k, f, _cfg.EmbargoPct);
                    startM15 = Math.Min(startH1 * 4, m15.Length);
                    endM15   = Math.Min(endH1   * 4, m15.Length);
                }
                if (endH1  - startH1  < 40) continue;
                if (endM15 - startM15 < 40) continue;
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(g, h1.Slice(startH1, endH1 - startH1).Span, m15.Slice(startM15, endM15 - startM15).Span))
                {
                    double w = _tradeGate?.Invoke(t.Time) ?? 1.0;
                    if (w >= 0.05) foldReturns.Add(t.Return * w);
                }
            }

            // Only folds that actually reached MinTradesPerFold trades take part in the
            // aggregation. A thin fold returns the constant -1.0 sentinel, and mixing
            // constants into the aggregate would let a no-trade fold masquerade as a real
            // (merely bad) one. It still counts toward attemptedFolds, so skipping costs coverage.
            if (foldReturns.Count < MinTradesPerFold) continue;

            foldScores.Add(FoldScoreHelper.Canonical(foldReturns, posFrac, MinTradesPerFold, _cfg, statBonusCeiling: 1.5));
            foldCounts.Add(foldReturns.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    public SwingLongGenotype Run(IReadOnlyList<CoinData> coins, SwingLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        if (_verbose && seed != null) Console.WriteLine($"  Seeding from: {seed}");

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => SwingLongGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<SwingLongGenotype> eliteIsland    = new();
        double                  bestFitnessSeen = double.MinValue;
        int                     stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitnessSeen + 1e-6) { bestFitnessSeen = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens >= 15 ? $" [STAGNANT×{stagnantGens} boost]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First()}{tag}");
            }

            if (gen % 10 == 0)
            {
                static double safe(double v) => double.IsFinite(v) ? v : 0.0;
                var progress = new
                {
                    strategy    = "SwingLong",
                    variant     = _cfg.VariantId,
                    generation  = gen,
                    bestFitness = safe(eliteIsland.First().Fitness),
                    meanFitness = safe(Math.Round(population.Average(g => g.Fitness), 4)),
                    population  = population.Take(20).Select(g => safe(Math.Round(g.Fitness, 3))).ToArray(),
                };
                File.WriteAllText("training_progress.json",
                    System.Text.Json.JsonSerializer.Serialize(progress,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
            }

            var nextGen = new List<SwingLongGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var a = eliteIsland[_rng.Next(eliteIsland.Count)];
                var b = population[_rng.Next(Math.Min(population.Count, _populationSize / 2))];
                nextGen.Add(SwingLongGenotype.Crossover(a, b, _rng).Mutate(_rng, mutationRate));
            }
            population = nextGen;
        }

        // BO refinement
        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     SwingLongGenotype.Bounds,
            evaluate:   v => { var geno = SwingLongGenotype.FromVector(v); geno.Fitness = Fitness(geno, coins, useValidation: false); return geno.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = SwingLongGenotype.FromVector(boChampion.Params);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        // Final rescore on val set
        if (_verbose) Console.WriteLine("\n=== SwingLong held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));
        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    public SwingLongGenotype RunLowVol(IReadOnlyList<CoinData> coins, SwingLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        if (_verbose && seed != null) Console.WriteLine($"  Seeding from: {seed}");

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => SwingLongGenotype.RandomLowVol(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBoundsLowVol();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.MutateLowVol(_rng, 0.25);
        }

        List<SwingLongGenotype> eliteIsland    = new();
        double                  bestFitnessSeen = double.MinValue;
        int                     stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitnessSeen + 1e-6) { bestFitnessSeen = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens >= 15 ? $" [STAGNANT×{stagnantGens} boost]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First()}{tag}");
            }

            if (gen % 10 == 0)
            {
                static double safe(double v) => double.IsFinite(v) ? v : 0.0;
                var progress = new
                {
                    strategy    = "SwingLongLowVol",
                    variant     = _cfg.VariantId,
                    generation  = gen,
                    bestFitness = safe(eliteIsland.First().Fitness),
                    meanFitness = safe(Math.Round(population.Average(g => g.Fitness), 4)),
                    population  = population.Take(20).Select(g => safe(Math.Round(g.Fitness, 3))).ToArray(),
                };
                File.WriteAllText("training_progress.json",
                    System.Text.Json.JsonSerializer.Serialize(progress,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
            }

            var nextGen = new List<SwingLongGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var a = eliteIsland[_rng.Next(eliteIsland.Count)];
                var b = population[_rng.Next(Math.Min(population.Count, _populationSize / 2))];
                nextGen.Add(SwingLongGenotype.Crossover(a, b, _rng).MutateLowVol(_rng, mutationRate));
            }
            population = nextGen;
        }

        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     SwingLongGenotype.BoundsLowVol,
            evaluate:   v => { var geno = SwingLongGenotype.FromVectorLowVol(v); geno.Fitness = Fitness(geno, coins, useValidation: false); return geno.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = SwingLongGenotype.FromVectorLowVol(boChampion.Params);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== SwingLongLowVol held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));
        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }
}
