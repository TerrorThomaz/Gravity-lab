namespace TradingGA;

// Genetic algorithm for grid trading (ranging-market long grid, 1h candles).
// Structure mirrors FadeShortGA.
//
// FoldScore operates on per-SESSION returns (mean of all level fills per activation),
// not per-fill returns. This prevents inflating WR and trade count when GridLevels > 1.
//
// FoldScore weights:
//   portfolio gain (compounded 3% per session) × win-rate multiplier
//   divided by drawdown penalty (2× stronger than swing to penalise stop-loss tails)
//   No frequency bonus — trade count is driven by coin volatility, not strategy quality.
// Fitness = mean(fold_scores) − 0.75 × std(fold_scores)
public class GridGeneticAlgorithm
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    private const int    MinTradesPerFold = 10;
    private const double FitPosFrac       = 0.03;

    public GridGeneticAlgorithm(
        int  populationSize    = 60,
        int  generations       = 100,
        int  eliteCount        = 15,
        int  migrationInterval = 10,
        bool verbose           = true)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
    }

    private static double FoldScore(List<double> returns)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        double wr       = (double)returns.Count(r => r > 0) / returns.Count;
        double grossWin = returns.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss= Math.Abs(returns.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf       = grossLoss > 1e-10 ? grossWin / grossLoss : (grossWin > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in returns)
        {
            balance += r / 100.0 * FitPosFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 0.5;
        double ddDiv  = 1.0 + maxDd * 20.0;

        return gain * 100.0 * wrMult / ddDiv;
    }

    private double Fitness(GridGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, arr: useValidation ? c.ValCandles : c.TrainCandles))
            .Where(x => x.arr.Length >= 100)
            .ToList();
        if (validCoins.Count == 0) return 0;

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => GridSimulator.GetGridSessionReturns(ind, x.arr.Span).Select(t => t.Return))
                .ToList();
            return FoldScore(all);
        }

        int minLen  = validCoins.Min(x => x.arr.Length);
        int k       = Math.Min(folds, minLen / 40);

        if (k < 2)
        {
            var all = validCoins
                .SelectMany(x => GridSimulator.GetGridSessionReturns(ind, x.arr.Span).Select(t => t.Return))
                .ToList();
            return FoldScore(all);
        }

        int      foldSize = minLen / k;
        double[] scores   = new double[k];
        for (int f = 0; f < k; f++)
        {
            int start       = f * foldSize;
            int end         = f == k - 1 ? minLen : start + foldSize;
            var foldReturns = new List<double>();
            foreach (var (coin, arr) in validCoins)
            {
                if (arr.Length < end) continue;
                foldReturns.AddRange(
                    GridSimulator.GetGridSessionReturns(ind, arr.Slice(start, end - start).Span).Select(t => t.Return));
            }
            scores[f] = FoldScore(foldReturns);
        }

        double mean = scores.Average();
        double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
        return mean - 0.75 * std;
    }

    public GridGenotype Run(IReadOnlyList<CoinData> coins, GridGenotype? seed = null, double adxCeiling = 20.0)
    {
        if (coins.Count == 0 || coins.All(c => c.TrainCandles.Length == 0))
            throw new ArgumentException("No training candles found.");

        if (_verbose)
        {
            foreach (var (coin, i) in coins.Select((c, i) => (c, i)))
                Console.WriteLine($"  Coin {i} (w={coin.Weight:F1})  " +
                    $"train={coin.TrainCandles.Length} candles  val={coin.ValCandles.Length} candles");
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => GridGenotype.Random(_rng, seed, adxCeiling))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds(adxCeiling);
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25, adxCeiling);
        }

        List<GridGenotype> eliteIsland    = new();
        double             bestFitness    = double.MinValue;
        int                stagnantGens   = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitness + 1e-6) { bestFitness = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens >= 15 ? $" [STAGNANT×{stagnantGens} boost]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First()}{tag}");
            }

            var nextGen = new List<GridGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = GridGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .Mutate(_rng, mutationRate, adxCeiling);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        // ── Bayesian refinement ──────────────────────────────────────────────────
        if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     GridGenotype.Bounds,
            evaluate:   v => { var g = GridGenotype.FromVector(v, adxCeiling); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 30,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = GridGenotype.FromVector(boChampion.Params, adxCeiling);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== Held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    private GridGenotype TournamentSelect(List<GridGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
