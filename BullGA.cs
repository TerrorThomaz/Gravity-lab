namespace TradingGA;

// Genetic algorithm for the bull long strategy.
// Mirrors SwingGeneticAlgorithm exactly — same 5-fold walk-forward CV,
// same fitness formula (portfolio gain × WR mult × freq bonus / DD div).
public class BullGeneticAlgorithm
{
    public record CoinData(Candle[] TrainH1, Candle[] ValH1, Candle[] TrainM15, Candle[] ValM15, double Weight = 1.0);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    private const int MinTradesPerFold = 15;

    public BullGeneticAlgorithm(
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

    private static double FoldScore(List<double> returns, double posFrac)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        double wr        = (double)returns.Count(r => r > 0) / returns.Count;
        double grossWins = returns.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss = Math.Abs(returns.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf        = grossLoss > 1e-10 ? grossWins / grossLoss : (grossWins > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in returns)
        {
            balance += r / 100.0 * posFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        double wrMult   = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0;
        double ddDiv    = 1.0 + maxDd * 10.0;
        double freqBonus= 1.0 + 0.15 * Math.Log(Math.Max(1.0, returns.Count / (double)MinTradesPerFold));

        return gain * 100.0 * wrMult * freqBonus / ddDiv;
    }

    private double Fitness(BullGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, h1: useValidation ? c.ValH1 : c.TrainH1,
                             m15: useValidation ? c.ValM15 : c.TrainM15))
            .Where(x => x.h1.Length >= 100 && x.m15.Length >= 400)
            .ToList();
        if (validCoins.Count == 0) return 0;

        double posFrac = Math.Clamp(ind.PositionSizePct, 0.01, 0.05);

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => BullSimulator.GetBullReturns(ind, x.h1, x.m15).Select(t => t.Return))
                .ToList();
            return FoldScore(all, posFrac);
        }

        int minLen = validCoins.Min(x => x.h1.Length);
        int k      = Math.Min(folds, minLen / 40);

        if (k < 2)
        {
            var all = validCoins
                .SelectMany(x => BullSimulator.GetBullReturns(ind, x.h1, x.m15).Select(t => t.Return))
                .ToList();
            return FoldScore(all, posFrac);
        }

        int      foldSize = minLen / k;
        double[] scores   = new double[k];
        for (int f = 0; f < k; f++)
        {
            int startH1  = f * foldSize;
            int endH1    = f == k - 1 ? minLen : startH1 + foldSize;
            int startM15 = startH1 * 4;
            int endM15   = endH1   * 4;
            var foldRet  = new List<double>();
            foreach (var (coin, h1, m15) in validCoins)
            {
                if (h1.Length < endH1 || m15.Length < endM15) continue;
                foldRet.AddRange(
                    BullSimulator.GetBullReturns(ind, h1[startH1..endH1], m15[startM15..endM15])
                                 .Select(t => t.Return));
            }
            scores[f] = FoldScore(foldRet, posFrac);
        }

        double mean = scores.Average();
        double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
        return mean - 0.75 * std;
    }

    public BullGenotype Run(IReadOnlyList<CoinData> coins, BullGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        if (_verbose)
        {
            foreach (var (coin, i) in coins.Select((c, i) => (c, i)))
                Console.WriteLine($"  Coin {i} (w={coin.Weight:F1})  " +
                    $"trainH1={coin.TrainH1.Length}  valH1={coin.ValH1.Length}");
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => BullGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<BullGenotype> eliteIsland      = new();
        double              bestFitnessSeen = double.MinValue;
        int                 stagnantGens   = 0;

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

            var nextGen = new List<BullGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = BullGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .Mutate(_rng, mutationRate);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        if (_verbose) Console.WriteLine("\n=== Bull held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    private BullGenotype TournamentSelect(List<BullGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
