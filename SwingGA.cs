namespace TradingGA;

// Genetic algorithm for swing trading on 4h candles.
// Uses 5-fold walk-forward CV; fitness is expectancy + profit factor (not Sharpe).
//
// Why not Sharpe?  Swing trades are infrequent with high per-trade variance — that variance
// is the goal (big winners), not a defect.  Sharpe penalises it.  Expectancy (avg % per
// trade) directly rewards "bigger profits per trade."  Profit factor (gross wins / gross
// losses) rewards "more sure" by penalising strategies that rely on a lucky outlier win.
//
// FoldScore = expectancy
//           + 0.4 × ln(profit_factor)    — quality modifier, log-scaled to avoid dominating
//           + 0.2 × (win_rate − 0.45)    — mild bias toward >45% WR
//
// Fitness  = mean(fold_scores) − 0.75 × std(fold_scores)  — penalises time-period fragility
public class SwingGeneticAlgorithm
{
    public record CoinData(Candle[] TrainCandles, Candle[] ValCandles, double Weight = 1.0);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    private const int MinTradesPerFold = 5;   // 4h candles: reject dead params; fold penalty (0.75×std) handles consistency

    public SwingGeneticAlgorithm(
        int  populationSize    = 50,
        int  generations       = 80,
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

    // Expectancy-based fold score — rewards bigger per-trade profit and consistency.
    private static double FoldScore(List<double> returns)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        double expectancy = returns.Average();
        double grossWins  = returns.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss  = Math.Abs(returns.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf         = grossLoss > 1e-10 ? grossWins / grossLoss
                          : (grossWins > 0    ? 5.0 : 0.0);
        double wr         = (double)returns.Count(r => r > 0) / returns.Count;

        if (expectancy <= 0) return expectancy;   // losing strategy → raw negative expectancy

        return expectancy
             + 0.4 * Math.Log(Math.Clamp(pf, 0.2, 8.0))
             + 0.2 * (wr - 0.45);
    }

    // Pool returns across ALL coins within each fold time-slot.
    // Per-coin fitness was flat (-1 everywhere) because each coin individually
    // produced too few trades per fold. Pooling 12 coins gives ~12× more trades
    // per fold while fold-to-fold std still guards temporal overfitting.
    private double Fitness(SwingGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, arr: useValidation ? c.ValCandles : c.TrainCandles))
            .Where(x => x.arr.Length >= 100)
            .ToList();
        if (validCoins.Count == 0) return 0;

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => SwingSimulator.GetSwingReturns(ind, x.arr).Select(t => t.Return))
                .ToList();
            return FoldScore(all);
        }

        int minLen   = validCoins.Min(x => x.arr.Length);
        int k        = Math.Min(folds, minLen / 40);

        if (k < 2)
        {
            var all = validCoins
                .SelectMany(x => SwingSimulator.GetSwingReturns(ind, x.arr).Select(t => t.Return))
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
                    SwingSimulator.GetSwingReturns(ind, arr[start..end]).Select(t => t.Return));
            }
            scores[f] = FoldScore(foldReturns);
        }

        double mean = scores.Average();
        double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
        return mean - 0.75 * std;
    }

    public SwingGenotype Run(IReadOnlyList<CoinData> coins, SwingGenotype? seed = null)
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
            .Select(_ => SwingGenotype.Random(_rng, seed))
            .ToList();

        // Inject seed variants into first 20% of population
        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<SwingGenotype> eliteIsland = new();
        double bestFitnessSeen = double.MinValue;
        int    stagnantGens    = 0;

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

            var nextGen = new List<SwingGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = SwingGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .Mutate(_rng, mutationRate);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        // Re-score elite on held-out validation candles
        if (_verbose) Console.WriteLine("\n=== Held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    private SwingGenotype TournamentSelect(List<SwingGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
