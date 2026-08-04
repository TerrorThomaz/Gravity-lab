using System.Buffers;

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
public class FadeShortGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

    // Pre-computed fixed-period indicators for a single candle array.
    // RSI/ADX/ATR periods are constants in FadeShortSimulator, so these arrays
    // are the same for every individual and only need to be built once per Run().
    private sealed record CoinCache(
        Candle[] Candles,
        double[] Closes,
        double[] Highs,
        double[] Lows,
        double[] Rsi,
        double[] Adx,
        double[] Atr,
        double   Weight);

    private static CoinCache BuildCache(ReadOnlyMemory<Candle> mem, double weight)
    {
        var candles = mem.ToArray();
        var span    = mem.Span;
        var closes  = new double[span.Length];
        var highs   = new double[span.Length];
        var lows    = new double[span.Length];
        for (int i = 0; i < span.Length; i++)
        {
            closes[i] = span[i].Close;
            highs[i]  = span[i].High;
            lows[i]   = span[i].Low;
        }
        return new CoinCache(
            candles, closes, highs, lows,
            Momentum.Rsi(closes, FadeShortSimulator.RsiPeriod),
            Trend.Adx(highs, lows, closes, FadeShortSimulator.AdxPeriod),
            Volatility.Atr(highs, lows, closes, FadeShortSimulator.AtrPeriod),
            weight);
    }

    private readonly int          _populationSize;
    private readonly int          _generations;
    private readonly int          _eliteCount;
    private readonly int          _migrationInterval;
    private readonly bool         _verbose;
    private readonly Random       _rng = new();
    private readonly FitnessConfig _cfg;

    private const int MinTradesPerFold = 30;

    public FadeShortGA(
        int           populationSize    = 50,
        int           generations       = 80,
        int           eliteCount        = 15,
        int           migrationInterval = 10,
        bool          verbose           = true,
        FitnessConfig? cfg              = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _cfg               = cfg ?? new FitnessConfig();
    }

    private static double FoldScore(List<double> returns, double posFrac, FitnessConfig cfg, double volWeight = 1.0)
        => FoldScoreHelper.Canonical(returns, posFrac, MinTradesPerFold, cfg, volWeight, statBonusCeiling: 1.0);

    // Pool returns across ALL coins within each fold time-slot.
    // Per-coin fitness was flat (-1 everywhere) because each coin individually
    // produced too few trades per fold. Pooling 12 coins gives ~12× more trades
    // per fold while fold-to-fold std still guards temporal overfitting.
    //
    // Uses pre-computed RSI/ADX/ATR caches and a caller-rented EMA buffer so that
    // per-individual allocations are limited to the single EMA array per coin per fold.
    private static double FitnessFromCache(
        FadeShortGenotype        ind,
        IReadOnlyList<CoinCache> caches,
        bool                     useFolds,
        FitnessConfig            cfg,
        int                      folds = 5)
    {
        if (caches.Count == 0) return 0;

        double posFrac = Math.Clamp(ind.PositionSizePct, 0.01, 0.05);

        // Rent one EMA buffer large enough for any coin; reused across all coins for this individual.
        int     maxLen    = caches.Max(c => c.Candles.Length);
        double[] emaBuffer = ArrayPool<double>.Shared.Rent(maxLen);
        try
        {
            if (!useFolds)
            {
                // Full run on all caches (validation path)
                var all = new List<double>(512);
                foreach (var cache in caches)
                {
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, 0, cache.Candles.Length))
                        all.Add(t.Return);
                }
                double volWeight = AverageVolCoverage(caches, 0, caches.Min(c => c.Candles.Length), cfg);
                return FoldScore(all, posFrac, cfg, volWeight);
            }

            int minLen = caches.Min(c => c.Candles.Length);
            int k      = Math.Min(folds, minLen / 40);

            if (k < 2)
            {
                var all = new List<double>(512);
                foreach (var cache in caches)
                {
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, 0, cache.Candles.Length))
                        all.Add(t.Return);
                }
                double volWeight = AverageVolCoverage(caches, 0, caches.Min(c => c.Candles.Length), cfg);
                return FoldScore(all, posFrac, cfg, volWeight);
            }

            var bounds = FoldScoreHelper.ComputeFoldBoundaries(minLen, k, cfg.EmbargoPct);
            double[] scores = new double[k];
            for (int f = 0; f < k; f++)
            {
                int fStart      = bounds[f].Start;
                int fEnd        = bounds[f].End;
                var foldReturns = new List<double>(512);
                foreach (var cache in caches)
                {
                    if (cache.Candles.Length < fEnd) continue;
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, fStart, fEnd))
                        foldReturns.Add(t.Return);
                }
                double volWeight = AverageVolCoverage(caches, fStart, fEnd, cfg);
                scores[f] = FoldScore(foldReturns, posFrac, cfg, volWeight);
            }

            double mean = scores.Average();
            double std  = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
            return mean - 0.75 * std;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(emaBuffer);
        }
    }

    private static double AverageVolCoverage(IReadOnlyList<CoinCache> caches, int start, int end, FitnessConfig cfg)
    {
        if (cfg.AtrLow <= 0.0 && cfg.AtrHigh >= 9999.0) return 1.0;
        double sum = 0; int count = 0;
        foreach (var c in caches)
        {
            if (c.Atr.Length < end) continue;
            sum += VariantRouter.VolCoverage(c.Atr, start, end, cfg.AtrLow, cfg.AtrHigh);
            count++;
        }
        return count == 0 ? 1.0 : sum / count;
    }

    public FadeShortGenotype Run(IReadOnlyList<CoinData> coins, FadeShortGenotype? seed = null)
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

        // Pre-compute fixed-period indicators once per coin before the GA loop.
        // Only EMA (EmaPeriod is a gene) is computed per individual inside FitnessFromCache.
        var trainCaches = coins
            .Select(c => BuildCache(c.TrainCandles, c.Weight))
            .Where(c => c.Candles.Length >= 100)
            .ToList();
        var valCaches = coins
            .Select(c => BuildCache(c.ValCandles, c.Weight))
            .Where(c => c.Candles.Length >= 100)
            .ToList();

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => FadeShortGenotype.Random(_rng, seed))
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

        List<FadeShortGenotype> eliteIsland = new();
        double bestFitnessSeen = double.MinValue;
        int    stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = FitnessFromCache(ind, trainCaches, useFolds: true, _cfg));

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
                    strategy    = "FadeShort",
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

            var nextGen = new List<FadeShortGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = FadeShortGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .Mutate(_rng, mutationRate);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        // Report-only validation (not used for selection)
        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");

        // Select winner by train fitness (best taken from elite sorted by train fitness)
        var best = eliteIsland.First();
        double trainFit = best.Fitness;

        // Compute validation score for the winner only
        best.Fitness = FitnessFromCache(best, valCaches, useFolds: false, _cfg);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    public FadeShortGenotype RunLowVol(IReadOnlyList<CoinData> coins, FadeShortGenotype? seed = null)
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

        var trainCaches = coins
            .Select(c => BuildCache(c.TrainCandles, c.Weight))
            .Where(c => c.Candles.Length >= 100)
            .ToList();
        var valCaches = coins
            .Select(c => BuildCache(c.ValCandles, c.Weight))
            .Where(c => c.Candles.Length >= 100)
            .ToList();

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => FadeShortGenotype.RandomLowVol(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBoundsLowVol();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.MutateLowVol(_rng, 0.25);
        }

        List<FadeShortGenotype> eliteIsland = new();
        double bestFitnessSeen = double.MinValue;
        int    stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = FitnessFromCache(ind, trainCaches, useFolds: true, _cfg));

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
                    strategy    = "FadeShortLowVol",
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

            var nextGen = new List<FadeShortGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = FadeShortGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .MutateLowVol(_rng, mutationRate);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");

        var best = eliteIsland.First();
        double trainFit = best.Fitness;

        best.Fitness = FitnessFromCache(best, valCaches, useFolds: false, _cfg);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    private FadeShortGenotype TournamentSelect(List<FadeShortGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
