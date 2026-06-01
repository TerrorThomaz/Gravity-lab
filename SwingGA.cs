namespace TradingGA;

// Genetic algorithm for swing trading (1h setup + 15m entry/exit).
// Uses 5-fold walk-forward CV; fitness directly optimises portfolio outcomes.
//
// FoldScore simulates a portfolio at a fixed 3% position size through each fold:
//   - Main signal  : compounded portfolio gain (not per-trade expectancy)
//   - Win rate     : multiplier — linearly penalises WR < 40%, bonus above 40%
//   - Drawdown     : divisor   — sharply penalises peak-to-trough drawdown
//   - Frequency    : log bonus — mild incentive to generate more trades
//
// FoldScore = (port_gain × 100) × wr_mult × freq_bonus / dd_div
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

    private const int    MinTradesPerFold = 15;
    private const double FitPosFrac       = 0.03;  // fixed 3% per trade in fold portfolio sim

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

    // Portfolio-based fold score: simulate a fixed 3% position through the fold,
    // then weight by win rate and divide by drawdown.
    private static double FoldScore(List<double> returns)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        double wr = (double)returns.Count(r => r > 0) / returns.Count;

        double grossWins = returns.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss = Math.Abs(returns.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf        = grossLoss > 1e-10 ? grossWins / grossLoss : (grossWins > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;  // continuous negative signal for losing folds

        // Simulate portfolio at fixed 3% per trade (no fold-Kelly lookahead bias)
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

        // Win rate: ramps from 0 at WR=0 to 1.0 at WR=40%, bonus above 40%
        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0;

        // Drawdown penalty: 5% max DD halves the score
        double ddDiv = 1.0 + maxDd * 10.0;

        // Frequency bonus: mild log incentive for more trades
        double freqBonus = 1.0 + 0.15 * Math.Log(Math.Max(1.0, returns.Count / (double)MinTradesPerFold));

        return gain * 100.0 * wrMult * freqBonus / ddDiv;
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
