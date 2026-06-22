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
            Indicators.Rsi(closes, FadeShortSimulator.RsiPeriod),
            Indicators.Adx(highs, lows, closes, FadeShortSimulator.AdxPeriod),
            Indicators.Atr(highs, lows, closes, FadeShortSimulator.AtrPeriod),
            weight);
    }

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    private const int MinTradesPerFold = 30;

    public FadeShortGA(
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

    // Portfolio-based fold score: simulate a genotype-evolved position size through the fold,
    // then weight by win rate and divide by drawdown.
    private static double FoldScore(List<double> returns, double posFrac)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        // Single pass for win-rate, gross wins/losses, and avg win/loss
        int    wins = 0;
        double grossWins = 0, grossLoss = 0;
        foreach (var r in returns)
        {
            if (r > 0) { wins++; grossWins += r; }
            else         grossLoss -= r;
        }

        double wr = (double)wins / returns.Count;
        double pf = grossLoss > 1e-10 ? grossWins / grossLoss : (grossWins > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;  // continuous negative signal for losing folds

        // Simulate portfolio at evolved position size per trade (no fold-Kelly lookahead bias)
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

        // Win rate: ramps from 0 at WR=0 to 1.0 at WR=40%, bonus above 40%
        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0;

        // PF multiplier: gross win/loss ratio — rewards net profitability across the fold.
        // 0 at PF=1.0, ramps to 1.0 at PF=1.5, bonus above.
        double pfMult = pf < 1.5 ? (pf - 1.0) / 0.5 : 1.0 + (pf - 1.5) * 0.5;

        // R:R multiplier: avgWin / avgLoss — pure size asymmetry, independent of WR.
        // 0 at R:R=1.0, ramps to 1.0 at R:R=2.5, bonus above.
        int    losses  = returns.Count - wins;
        double avgWin  = wins   > 0 ? grossWins / wins   : 0;
        double avgLoss = losses > 0 ? grossLoss / losses : avgWin;
        double rr      = avgLoss > 1e-10 ? avgWin / avgLoss : (avgWin > 0 ? 5.0 : 1.0);
        double rrMult  = rr < 2.5 ? (rr - 1.0) / 1.5 : 1.0 + (rr - 2.5) * 0.3;

        // Combined: geometric mean keeps scale stable, rewards both dimensions equally.
        double qualityMult = Math.Sqrt(pfMult * rrMult);

        // Drawdown penalty: 5% max DD halves the score
        double ddDiv = 1.0 + maxDd * 10.0;

        // Frequency bonus: mild log incentive for more trades
        double freqBonus = 1.0 + 0.15 * Math.Log(Math.Max(1.0, returns.Count / (double)MinTradesPerFold));

        // Kept-profits: penalise giving back peak gains before period end
        double peakGain      = peak - 1.0;
        double retentionMult = peakGain > 0.01
            ? Math.Max(0.2, (balance - 1.0) / peakGain)
            : 1.0;

        return gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
    }

    // Pool returns across ALL coins within each fold time-slot.
    // Per-coin fitness was flat (-1 everywhere) because each coin individually
    // produced too few trades per fold. Pooling 12 coins gives ~12× more trades
    // per fold while fold-to-fold std still guards temporal overfitting.
    //
    // Uses pre-computed RSI/ADX/ATR caches and a caller-rented EMA buffer so that
    // per-individual allocations are limited to the single EMA array per coin per fold.
    private static double FitnessFromCache(
        FadeShortGenotype      ind,
        IReadOnlyList<CoinCache> caches,
        bool                   useFolds,
        int                    folds = 5)
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
                    Indicators.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, 0, cache.Candles.Length))
                        all.Add(t.Return);
                }
                return FoldScore(all, posFrac);
            }

            // Walk-forward fold CV on train caches
            int minLen = caches.Min(c => c.Candles.Length);
            int k      = Math.Min(folds, minLen / 40);

            if (k < 2)
            {
                var all = new List<double>(512);
                foreach (var cache in caches)
                {
                    Indicators.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, 0, cache.Candles.Length))
                        all.Add(t.Return);
                }
                return FoldScore(all, posFrac);
            }

            int      foldSize = minLen / k;
            double[] scores   = new double[k];
            for (int f = 0; f < k; f++)
            {
                int fStart      = f * foldSize;
                int fEnd        = f == k - 1 ? minLen : fStart + foldSize;
                var foldReturns = new List<double>(512);
                foreach (var cache in caches)
                {
                    if (cache.Candles.Length < fEnd) continue;
                    Indicators.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, fStart, fEnd))
                        foldReturns.Add(t.Return);
                }
                scores[f] = FoldScore(foldReturns, posFrac);
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
                ind.Fitness = FitnessFromCache(ind, trainCaches, useFolds: true));

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

        // Re-score elite on held-out validation candles
        if (_verbose) Console.WriteLine("\n=== Held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = FitnessFromCache(ind, valCaches, useFolds: false));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    private FadeShortGenotype TournamentSelect(List<FadeShortGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
