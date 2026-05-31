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
// Fitness  = mean(fold_scores) − 0.6 × std(fold_scores)   — penalises time-period fragility
public class SwingGeneticAlgorithm
{
    public record CoinData(Candle[] TrainCandles, Candle[] ValCandles, double Weight = 1.0);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    private const int MinTradesPerFold = 5;   // 4h candles: ~7-15 swing trades per fold is realistic

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

    private double Fitness(SwingGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        double weightedSum = 0, totalWeight = 0;

        foreach (var coin in coins)
        {
            var candles = useValidation ? coin.ValCandles : coin.TrainCandles;
            if (candles.Length < 100) continue;

            double coinScore;

            if (!useValidation && folds > 1)
            {
                int k = Math.Min(folds, candles.Length / 40);
                if (k < 2)
                {
                    var returns = SwingSimulator.GetSwingReturns(ind, candles)
                                               .Select(t => t.Return).ToList();
                    coinScore = FoldScore(returns);
                }
                else
                {
                    int foldSize = candles.Length / k;
                    var scores   = new double[k];
                    for (int f = 0; f < k; f++)
                    {
                        int start   = f * foldSize;
                        int end     = f == k - 1 ? candles.Length : start + foldSize;
                        var chunk   = candles[start..end];
                        var returns = SwingSimulator.GetSwingReturns(ind, chunk)
                                                   .Select(t => t.Return).ToList();
                        scores[f]   = FoldScore(returns);
                    }
                    double mean = scores.Average();
                    double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
                    coinScore   = mean - 0.60 * std;
                }
            }
            else
            {
                var returns = SwingSimulator.GetSwingReturns(ind, candles)
                                           .Select(t => t.Return).ToList();
                coinScore = FoldScore(returns);
            }

            weightedSum += coin.Weight * coinScore;
            totalWeight += coin.Weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : 0;
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
