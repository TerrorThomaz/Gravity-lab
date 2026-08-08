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
// Fitness = lambda x CVaR_0.4(fold_scores) + (1 - lambda) x mean(fold_scores)
// over the SURVIVING walk-forward folds (those that reached MinTradesPerFold trades), where
// lambda is VC-proportional on avg N/d. The fold vector is CONSTANT LENGTH: a fold that
// never reached MinTradesPerFold enters as ThinFoldScore rather than vanishing. Monotone
// non-decreasing in every fold score by construction — see FoldScoreHelper.AggregateFoldScores
// for why `mean - stdMult x std` was not.
// Folds are cut PER COIN on that coin's own array — see FitnessFromCache.
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
    private const int D                = 13;   // genotype parameter count (excl. Fitness) — see FadeShortGenotype.Bounds

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

    // Pool returns across ALL coins within each fold slot (fold f = the f-th equal slice of
    // each coin's OWN history, so the pooled slot is the same relative position for every
    // symbol even though the absolute dates differ).
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
                double volWeight = AverageVolCoverageFull(caches, cfg);
                return FoldScore(all, posFrac, cfg, volWeight);
            }

            // Walk-forward folds, k of them, cut PER COIN on that coin's OWN array.
            //
            // Index-space fold bounds must never be shared across coins: each coin's
            // training array is a percentage split of its own (variable-length) history, so
            // index i is a different calendar date on every symbol. The previous code cut
            // the folds on minLen — the SHORTEST coin — which both misaligned "fold f"
            // across symbols and silently discarded every bar past minLen on every longer
            // coin. FadeShort is single-timeframe here (h1 only; the m15 execution leg lives
            // in the backtester, not in this GA), so there is no h1→m15 index mapping to
            // maintain — the fold range indexes cache.Candles/Closes/Rsi/Adx/Atr directly.
            //
            // FURTHER IMPROVEMENT: if a BTC RegimeBar series is ever threaded into this GA,
            // switch to FoldScoreHelper.ComputeRegimeAwareFoldWindows + RangeForWindow so
            // that fold f covers the same calendar stretch of market history for every
            // symbol — that is what the regime-gated GAs (DipLong/SwingLong/FadeLong/
            // RipShort) now do. No BTC series reaches this constructor today, so per-coin
            // percentage folds are the alignment-safe option available here.
            //
            // k is derived from the MEDIAN coin length rather than the shortest, so a single
            // short symbol can no longer cap the fold count for the whole run. Coins whose
            // own fold range comes out under 40 bars are skipped for that fold instead.
            int medianLen = MedianCandleLength(caches);
            int k         = Math.Min(folds, medianLen / 40);

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
                double volWeight = AverageVolCoverageFull(caches, cfg);
                return FoldScore(all, posFrac, cfg, volWeight);
            }

            var foldScores = new List<double>(k);
            var foldCounts = new List<int>(k);
            // Folds ATTEMPTED, including the thin ones skipped below — the aggregator
            // scales by surviving/attempted so that concentrating all activity into one
            // favourable window can no longer beat trading consistently across all of them.
            int attemptedFolds = 0;
            for (int f = 0; f < k; f++)
            {
                attemptedFolds++;
                var foldReturns = new List<double>(512);
                foreach (var cache in caches)
                {
                    var (fStart, fEnd) = FoldScoreHelper.PerCoinFoldRange(
                        cache.Candles.Length, k, f, cfg.EmbargoPct);
                    if (fEnd - fStart < 40) continue;
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    foreach (var t in FadeShortSimulator.GetFadeShortReturnsPrecomputed(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, fStart, fEnd))
                        foldReturns.Add(t.Return);
                }

                // Only folds that actually reached MinTradesPerFold trades take part in the
                // aggregation. A thin fold returns the constant -1.0 sentinel from
                // Canonical(), and mixing constants into the aggregate would let a
                // no-trade fold masquerade as a real (merely bad) one. It still counts
                // toward attemptedFolds, so skipping folds costs coverage.
                if (foldReturns.Count < MinTradesPerFold) continue;

                double volWeight = AverageVolCoverageFold(caches, k, f, cfg);
                foldScores.Add(FoldScore(foldReturns, posFrac, cfg, volWeight));
                foldCounts.Add(foldReturns.Count);
            }

            return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(emaBuffer);
        }
    }

    // Median candle count across the coin caches. Used to size the fold count without
    // letting the single shortest symbol dictate k for everyone (folds are per-coin now,
    // so k no longer has to fit inside the shortest array).
    private static int MedianCandleLength(IReadOnlyList<CoinCache> caches)
    {
        if (caches.Count == 0) return 0;
        var lens = caches.Select(c => c.Candles.Length).OrderBy(n => n).ToArray();
        return lens[lens.Length / 2];
    }

    // Vol coverage over each coin's FULL array (no minLen truncation — coverage is a
    // per-coin average, so longer coins contribute their whole history).
    private static double AverageVolCoverageFull(IReadOnlyList<CoinCache> caches, FitnessConfig cfg)
    {
        if (cfg.AtrLow <= 0.0 && cfg.AtrHigh >= 9999.0) return 1.0;
        double sum = 0; int count = 0;
        foreach (var c in caches)
        {
            if (c.Atr.Length == 0) continue;
            sum += VariantRouter.VolCoverage(c.Atr, 0, c.Atr.Length, cfg.AtrLow, cfg.AtrHigh);
            count++;
        }
        return count == 0 ? 1.0 : sum / count;
    }

    // Vol coverage for fold f, sliced per coin exactly as the trade loop slices it — the
    // ATR array is built from the same candle array, so it shares that index space.
    private static double AverageVolCoverageFold(
        IReadOnlyList<CoinCache> caches, int folds, int fold, FitnessConfig cfg)
    {
        if (cfg.AtrLow <= 0.0 && cfg.AtrHigh >= 9999.0) return 1.0;
        double sum = 0; int count = 0;
        foreach (var c in caches)
        {
            var (start, end) = FoldScoreHelper.PerCoinFoldRange(c.Atr.Length, folds, fold, cfg.EmbargoPct);
            if (end - start < 40) continue;
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

        // ── Bayesian refinement: TPE polishes the GA's elite region ─────────────
        // Mirrors DipLongGA. FadeShort was the only GA without this stage, so when the broken
        // post-GA BO pass in TrainCommands was deleted it was left with no refinement at all.
        // Critically, this evaluates the SAME function the GA selects on (FitnessFromCache over
        // trainCaches with folds) and accepts against a value from that same function — which is
        // exactly what the deleted outer pass failed to do: it optimised mean per-trade return
        // and compared it against a held-out fold-aggregated score.
        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     FadeShortGenotype.Bounds,
            evaluate:   v => { var g = FadeShortGenotype.FromVector(v);
                               g.Fitness = FitnessFromCache(g, trainCaches, useFolds: true, _cfg);
                               return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = FadeShortGenotype.FromVector(boChampion.Params);
        boGeno.Fitness = FitnessFromCache(boGeno, trainCaches, useFolds: true, _cfg);

        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO champion accepted: F={boGeno.Fitness:F3}");
        }
        else if (_verbose) Console.WriteLine($"  BO champion rejected (F={boGeno.Fitness:F3} <= elite floor {eliteIsland.Last().Fitness:F3})");

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

        // ── Bayesian refinement, LowVol variant ────────────────────────────────
        // Same rationale as Run's. Note this path MUST use the LowVol bounds and the LowVol
        // vector reader: BoundsLowVol is a genuinely different box (see FadeShortGenotype), so
        // refining against the normal Bounds here would propose genotypes outside the region
        // this variant is defined on and then clamp them back in, biasing toward the boundary.
        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE, LowVol bounds)...");
        var boSeedLv = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistoryLv = BayesianOptimizer.Refine(
            seedObs:    boSeedLv,
            bounds:     FadeShortGenotype.BoundsLowVol,
            evaluate:   v => { var g = FadeShortGenotype.FromVectorLowVol(v);
                               g.Fitness = FitnessFromCache(g, trainCaches, useFolds: true, _cfg);
                               return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampionLv = boHistoryLv.OrderByDescending(h => h.Fitness).First();
        var boGenoLv     = FadeShortGenotype.FromVectorLowVol(boChampionLv.Params);
        boGenoLv.Fitness = FitnessFromCache(boGenoLv, trainCaches, useFolds: true, _cfg);

        if (boGenoLv.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGenoLv;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO champion accepted: F={boGenoLv.Fitness:F3}");
        }
        else if (_verbose) Console.WriteLine($"  BO champion rejected (F={boGenoLv.Fitness:F3} <= elite floor {eliteIsland.Last().Fitness:F3})");

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
