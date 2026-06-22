using System.Collections.Concurrent;

namespace TradingGA;

// SwingLong GA — mirrors DipLongGA structure but uses SwingLongGenotype.
// 5-fold walk-forward CV; FoldScore includes retentionMult to penalise giving back gains.
// No protection-mode genes (unlike DipLong): SwingLong is simpler and router-gated.
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

    public SwingLongGA(
        int populationSize    = 60,
        int generations       = 100,
        int eliteCount        = 10,
        int migrationInterval = 10,
        bool verbose          = false,
        Func<DateTime, double>? tradeGate = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _tradeGate         = tradeGate;
    }

    private double FoldScore(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var rawTrades = SwingLongSimulator.GetSwingLongReturns(g, h1, m15);

        // Apply gate (router soft-gate × guard mult): scale returns by gate weight.
        // Trades with near-zero gate are excluded — they contribute no real signal.
        var trades = rawTrades
            .Select(t => {
                double w = _tradeGate != null ? _tradeGate(t.Time) : 1.0;
                return (Return: t.Return * w, w);
            })
            .Where(t => t.w >= 0.05)
            .ToList();
        if (trades.Count < 3) return -1.0;

        double balance = 1.0, peak = 1.0;
        foreach (var (ret, _) in trades)
        {
            balance *= 1.0 + ret / 100.0 * g.PositionSizePct;
            if (balance > peak) peak = balance;
        }

        double gain    = balance - 1.0;
        double peakGain = peak - 1.0;
        double wins    = trades.Count(t => t.Return > 0);
        double wr      = wins / trades.Count;
        double wrMult  = Math.Pow(wr / 0.50, 2.0);
        double ddDiv   = peak > 1e-10 ? Math.Max(1.0, (peak - Math.Min(balance, 1.0)) / peak * 10.0) : 1.0;
        double freqBonus  = Math.Min(1.5, 1.0 + (trades.Count - 3) * 0.05);
        double qualityMult = trades.Count > 0
            ? trades.Average(t => t.Return > 0 ? 1.0 + t.Return * 0.01 : 1.0 / (1.0 - t.Return * 0.01))
            : 1.0;

        double retentionMult = peakGain > 0.01
            ? Math.Max(0.2, gain / peakGain)
            : 1.0;

        return gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
    }

    private double Fitness(SwingLongGenotype g, IReadOnlyList<CoinData> coins, bool useValidation)
    {
        var scores = new ConcurrentBag<double>();
        Parallel.ForEach(coins, coin =>
        {
            var h1  = useValidation ? coin.ValH1.Span  : coin.TrainH1.Span;
            var m15 = useValidation ? coin.ValM15.Span : coin.TrainM15.Span;
            if (h1.Length < 50) return;
            double s = FoldScore(g, h1, m15);
            if (!double.IsNaN(s)) scores.Add(s * coin.Weight);
        });
        if (scores.IsEmpty) return -1.0;
        var list = scores.ToList();
        double mean = list.Average();
        double std  = Math.Sqrt(list.Select(s => (s - mean) * (s - mean)).Average());
        return mean - 0.75 * std;
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

        // Final rescore on val set
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));
        return eliteIsland.OrderByDescending(g => g.Fitness).First();
    }
}
