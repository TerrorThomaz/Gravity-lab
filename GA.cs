namespace TradingGA;

public class GeneticAlgorithm
{
    // CoinData now carries full candle arrays — the unified simulator handles everything.
    public record CoinData(
        Candle[] TrainCandles,
        Candle[] ValCandles,
        double   Weight = 1.0);

    private readonly int  _populationSize;
    private readonly int  _generations;
    private readonly int  _eliteCount;
    private readonly int  _migrationInterval;
    private readonly bool _useAtr;
    private readonly bool _verbose;
    private readonly Random _rng = new();

    public GeneticAlgorithm(
        int  populationSize    = 60,
        int  generations       = 80,
        int  eliteCount        = 20,
        int  migrationInterval = 10,
        bool useAtr            = true,
        bool verbose           = true)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _useAtr            = useAtr;
        _verbose           = verbose;
    }

    // Walk-forward temporal cross-validation on full candle series (5 folds).
    // Each fold runs GetUnifiedReturns (pump + grid, regime-gated).
    // Fitness = weighted mean of per-coin (mean_fold_score − 0.75 × std_fold_score).
    // Per-fold score = 0.9×Sharpe + 0.1×Clamp(Calmar,−2,3). Folds with <5 trades score −1.
    // Calmar weight capped at 0.1: higher weights reward tight stops + force wide entry gates.
    private const int MinTradesPerFold = 5;

    private static double FoldScore(List<double> returns)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;
        double sharpe = Simulator.SharpeRatio(returns);
        double calmar = Math.Clamp(Simulator.CalmarRatio(returns), -2.0, 3.0);
        return 0.9 * sharpe + 0.1 * calmar;
    }

    private double Fitness(Genotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        double weightedSum = 0, totalWeight = 0;

        foreach (var coin in coins)
        {
            var candles = useValidation ? coin.ValCandles : coin.TrainCandles;
            if (candles.Length < 500) continue;

            double coinScore;

            if (!useValidation && folds > 1)
            {
                int k        = Math.Min(folds, candles.Length / 200);
                if (k < 2)
                {
                    var returns = Simulator.GetUnifiedReturns(ind, candles, _useAtr)
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
                        var returns = Simulator.GetUnifiedReturns(ind, chunk, _useAtr)
                                               .Select(t => t.Return).ToList();
                        scores[f]   = FoldScore(returns);
                    }

                    double mean = scores.Average();
                    double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
                    coinScore   = mean - 0.75 * std;
                }
            }
            else
            {
                var returns = Simulator.GetUnifiedReturns(ind, candles, _useAtr)
                                       .Select(t => t.Return).ToList();
                coinScore = FoldScore(returns);
            }

            weightedSum  += coin.Weight * coinScore;
            totalWeight  += coin.Weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : 0;
    }

    public Genotype Run(IReadOnlyList<CoinData> coins, Genotype? seed = null)
    {
        if (coins.Count == 0 || coins.All(c => c.TrainCandles.Length == 0))
            throw new ArgumentException("No training candles found.");

        if (_verbose)
        {
            foreach (var (coin, i) in coins.Select((c, i) => (c, i)))
                Console.WriteLine($"  Coin {i} (w={coin.Weight:F1})  " +
                    $"train={coin.TrainCandles.Length} candles  val={coin.ValCandles.Length} candles");
            if (seed != null) Console.WriteLine($"  Seeding population from: {seed.ToString(_useAtr)}");
        }

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => Genotype.Random(_rng, _useAtr))
            .ToList();

        // Inject seed into 20% of initial population as mutated variants + one clamped copy.
        // Clamp first so out-of-range values from old saved files don't bypass the new bounds.
        if (seed != null)
        {
            var clampedSeed = seed.ClampToBounds(_useAtr);
            population[0] = clampedSeed;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clampedSeed.Mutate(_rng, 0.25, _useAtr);
        }

        List<Genotype> eliteIsland    = new();
        List<Genotype> struggleIsland = new();

        double bestFitnessSeen = double.MinValue;
        int    stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            // Stagnation boost: if no improvement for 15 gens, double mutation rate to escape local optima
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutationRate * 2.0, 0.9) : baseMutationRate;

            Parallel.ForEach(population, individual =>
                individual.Fitness = Fitness(individual, coins, useValidation: false));

            population    = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland    = population.Take(_eliteCount).ToList();
            struggleIsland = population.Skip(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitnessSeen + 1e-6)
            {
                bestFitnessSeen = topFitness;
                stagnantGens    = 0;
            }
            else
            {
                stagnantGens++;
            }

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string stagnantTag = stagnantGens >= 15 ? $" [STAGNANT×{stagnantGens} boost]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First().ToString(_useAtr)}{stagnantTag}");

                population     = eliteIsland.Concat(struggleIsland).OrderByDescending(g => g.Fitness).ToList();
                eliteIsland    = population.Take(_eliteCount).ToList();
                struggleIsland = population.Skip(_eliteCount).ToList();
            }

            var nextGen = new List<Genotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = Genotype.Crossover(TournamentSelect(population), TournamentSelect(population), _rng)
                                    .Mutate(_rng, mutationRate, _useAtr);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        // Re-score elite on held-out validation candles
        if (_verbose) Console.WriteLine("\n=== Held-out validation ===");
        Parallel.ForEach(eliteIsland, individual =>
            individual.Fitness = Fitness(individual, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best.ToString(_useAtr)}");
        return best;
    }

    private Genotype TournamentSelect(List<Genotype> population, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => population[_rng.Next(population.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
