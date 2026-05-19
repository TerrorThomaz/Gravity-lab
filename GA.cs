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
    // Fitness = weighted mean of per-coin (mean_fold_sharpe − 0.5 × std_fold_sharpe).
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
                    coinScore = Simulator.SharpeRatio(returns);
                }
                else
                {
                    int foldSize = candles.Length / k;
                    var sharpes  = new double[k];

                    for (int f = 0; f < k; f++)
                    {
                        int start   = f * foldSize;
                        int end     = f == k - 1 ? candles.Length : start + foldSize;
                        var chunk   = candles[start..end];
                        var returns = Simulator.GetUnifiedReturns(ind, chunk, _useAtr)
                                               .Select(t => t.Return).ToList();
                        sharpes[f]  = Simulator.SharpeRatio(returns);
                    }

                    double mean = sharpes.Average();
                    double std  = Math.Sqrt(sharpes.Select(s => (s - mean) * (s - mean)).Average());
                    coinScore   = mean - 0.5 * std;
                }
            }
            else
            {
                var returns = Simulator.GetUnifiedReturns(ind, candles, _useAtr)
                                       .Select(t => t.Return).ToList();
                coinScore = Simulator.SharpeRatio(returns);
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

        // Inject seed into 20% of initial population as mutated variants + one pure copy
        if (seed != null)
        {
            population[0] = seed;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = seed.Mutate(_rng, 0.25, _useAtr);
        }

        List<Genotype> eliteIsland    = new();
        List<Genotype> struggleIsland = new();

        for (int gen = 0; gen < _generations; gen++)
        {
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;

            Parallel.ForEach(population, individual =>
                individual.Fitness = Fitness(individual, coins, useValidation: false));

            population    = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland    = population.Take(_eliteCount).ToList();
            struggleIsland = population.Skip(_eliteCount).ToList();

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First().ToString(_useAtr)}");

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
