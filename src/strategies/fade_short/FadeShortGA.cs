using System.Buffers;

namespace TradingGA;

// FadeShort GA: fades overbought rallies in uptrends. 1h setup + 15m entry/exit.
// Fitness: CVaR/mean blend over per-coin walk-forward folds with regime-sustained filtering.
public class FadeShortGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

    // Pre-computed fixed-period indicators (RSI/ADX/ATR are constants, same for all individuals).
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
    private readonly int          _tournamentK;
    private readonly int          _eliteCarryOver;
    private readonly int          _cataclysmStagnantGens;
    private readonly int          _seed;
    private readonly bool         _seedSupplied;
    private readonly Random       _rng;
    private readonly FitnessConfig _cfg;

    // Router gating: weights by exit time, set by CoevolveGA.
    private readonly Func<DateTime, double>? _tradeGate;

    private const int MinTradesPerFold = 30;
    private const int D                = 13;   // genotype parameter count (excl. Fitness) — see FadeShortGenotype.Bounds

    // eliteCount = reporting/BO pool size. eliteCarryOver = real elitism knob. seed=null = random.
    public FadeShortGA(
        int           populationSize    = 50,
        int           generations       = 80,
        int           eliteCount        = 15,
        int           migrationInterval = 10,
        bool          verbose           = true,
        FitnessConfig? cfg              = null,
        Func<DateTime, double>? tradeGate = null,
        int           tournamentK           = GaSearch.DefaultTournamentK,
        int           eliteCarryOver        = GaSearch.DefaultEliteCarryOver,
        int           cataclysmStagnantGens = GaSearch.DefaultCataclysmStagnantGens,
        int?          seed                  = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _cfg               = cfg ?? new FitnessConfig();
        _tradeGate         = tradeGate;
        _tournamentK           = tournamentK;
        _eliteCarryOver        = eliteCarryOver;
        _cataclysmStagnantGens = cataclysmStagnantGens;
        (_rng, _seed, _seedSupplied) = GaSearch.CreateRng(seed);
    }

    // Regime-sustained fold score. sustainedBars=0 = plain Canonical (no filtering).
    private static double FoldScore(List<(double Return, int RegimeBars)> returns, double posFrac,
                                    int sustainedBars, FitnessConfig cfg, double volWeight = 1.0,
                                    IReadOnlyList<double>? maePct = null)
        => FoldScoreHelper.CanonicalRegime(returns, posFrac, sustainedBars, MinTradesPerFold, cfg,
                                           volWeight, statBonusCeiling: 1.0, maePct: maePct);

    // Pooled per-coin folds with pre-computed indicators. EMA buffer rented per individual.
    private static double FitnessFromCache(
        FadeShortGenotype        ind,
        IReadOnlyList<CoinCache> caches,
        bool                     useFolds,
        FitnessConfig            cfg,
        Func<DateTime, double>?  gate,
        int                      folds = 5)
    {
        GaTrialCounter.Shared.Record("fade_short");
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
                var all = new List<(double Return, int RegimeBars)>(512);
                var allMae = new List<double>(512);
                foreach (var cache in caches)
                {
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    var coinMae = new List<double>();
                    var coinTrades = FadeShortSimulator.GetFadeShortReturnsPrecomputedWithRegime(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, 0, cache.Candles.Length, coinMae);
                    for (int ti = 0; ti < coinTrades.Count; ti++)
                    {
                        var t = coinTrades[ti];
                        double w = gate?.Invoke(t.Time) ?? 1.0;
                        if (w < 0.05) continue;
                        all.Add((t.Return * w, t.RegimeBarsActive));
                        allMae.Add((ti < coinMae.Count ? coinMae[ti] : 0.0) * w);
                    }
                }
                double volWeight = AverageVolCoverageFull(caches, cfg);
                return FoldScore(all, posFrac, ind.RegimeSustainBars, cfg, volWeight, allMae);
            }

            // Per-coin folds; k from median coin length (not shortest).
            int medianLen = MedianCandleLength(caches);
            int k         = Math.Min(folds, medianLen / 40);

            if (k < 2)
            {
                var all = new List<(double Return, int RegimeBars)>(512);
                var allMae = new List<double>(512);
                foreach (var cache in caches)
                {
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    var coinMae = new List<double>();
                    var coinTrades = FadeShortSimulator.GetFadeShortReturnsPrecomputedWithRegime(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, 0, cache.Candles.Length, coinMae);
                    for (int ti = 0; ti < coinTrades.Count; ti++)
                    {
                        var t = coinTrades[ti];
                        double w = gate?.Invoke(t.Time) ?? 1.0;
                        if (w < 0.05) continue;
                        all.Add((t.Return * w, t.RegimeBarsActive));
                        allMae.Add((ti < coinMae.Count ? coinMae[ti] : 0.0) * w);
                    }
                }
                double volWeight = AverageVolCoverageFull(caches, cfg);
                return FoldScore(all, posFrac, ind.RegimeSustainBars, cfg, volWeight, allMae);
            }

            var foldScores = new List<double>(k);
            var foldCounts = new List<int>(k);
            int attemptedFolds = 0;  // includes thin folds
            for (int f = 0; f < k; f++)
            {
                attemptedFolds++;
                var foldReturns = new List<(double Return, int RegimeBars)>(512);
                var foldMae     = new List<double>(512);
                foreach (var cache in caches)
                {
                    var (fStart, fEnd) = FoldScoreHelper.PerCoinFoldRange(
                        cache.Candles.Length, k, f, cfg.EmbargoPct);
                    if (fEnd - fStart < 40) continue;
                    Trend.EmaInto(cache.Closes, ind.EmaPeriod, emaBuffer);
                    var coinMae = new List<double>();
                    var coinTrades = FadeShortSimulator.GetFadeShortReturnsPrecomputedWithRegime(
                        ind, cache.Candles, cache.Closes, cache.Highs, cache.Lows,
                        cache.Rsi, cache.Adx, cache.Atr, emaBuffer, fStart, fEnd, coinMae);
                    for (int ti = 0; ti < coinTrades.Count; ti++)
                    {
                        var t = coinTrades[ti];
                        double w = gate?.Invoke(t.Time) ?? 1.0;
                        if (w < 0.05) continue;
                        foldReturns.Add((t.Return * w, t.RegimeBarsActive));
                        foldMae.Add((ti < coinMae.Count ? coinMae[ti] : 0.0) * w);
                    }
                }

                if (foldReturns.Count < MinTradesPerFold) continue;

                double volWeight = AverageVolCoverageFold(caches, k, f, cfg);
                foldScores.Add(FoldScore(foldReturns, posFrac, ind.RegimeSustainBars, cfg, volWeight, foldMae));
                foldCounts.Add(foldReturns.Count);
            }

            return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(emaBuffer);
        }
    }

    private static int MedianCandleLength(IReadOnlyList<CoinCache> caches)
    {
        if (caches.Count == 0) return 0;
        var lens = caches.Select(c => c.Candles.Length).OrderBy(n => n).ToArray();
        return lens[lens.Length / 2];
    }

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

        GaSearch.AnnounceSeed("FadeShortGA.Run", _seed, _seedSupplied);

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
            .Select(_ => FadeShortGenotype.Random(_rng, seed))
            .ToList();

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
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            // WARNING: parallel body must stay RNG-free.
            Parallel.ForEach(population, ind =>
                ind.Fitness = FitnessFromCache(ind, trainCaches, useFolds: true, _cfg, _tradeGate));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitnessSeen + 1e-6) { bestFitnessSeen = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens > 0 ? $" [stagnant×{stagnantGens}]" : "";
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

            if (GaSearch.ShouldCataclysm(stagnantGens, _cataclysmStagnantGens))
            {
                population = GaSearch.Cataclysm(population, _populationSize, _eliteCarryOver,
                                                () => FadeShortGenotype.Random(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<FadeShortGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
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
        }

        // BO refinement: same fitness function as GA selection.
        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     FadeShortGenotype.Bounds,
            evaluate:   v => { var g = FadeShortGenotype.FromVector(v);
                               g.Fitness = FitnessFromCache(g, trainCaches, useFolds: true, _cfg, _tradeGate);
                               return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = FadeShortGenotype.FromVector(boChampion.Params);
        boGeno.Fitness = FitnessFromCache(boGeno, trainCaches, useFolds: true, _cfg, _tradeGate);

        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO champion accepted: F={boGeno.Fitness:F3}");
        }
        else if (_verbose) Console.WriteLine($"  BO champion rejected (F={boGeno.Fitness:F3} <= elite floor {eliteIsland.Last().Fitness:F3})");

        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");
        var best = eliteIsland.First();
        double trainFit = best.Fitness;

        best.Fitness = FitnessFromCache(best, valCaches, useFolds: false, _cfg, _tradeGate);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    public FadeShortGenotype RunLowVol(IReadOnlyList<CoinData> coins, FadeShortGenotype? seed = null)
    {
        if (coins.Count == 0 || coins.All(c => c.TrainCandles.Length == 0))
            throw new ArgumentException("No training candles found.");

        GaSearch.AnnounceSeed("FadeShortGA.RunLowVol", _seed, _seedSupplied);

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
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            Parallel.ForEach(population, ind =>
                ind.Fitness = FitnessFromCache(ind, trainCaches, useFolds: true, _cfg, _tradeGate));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitnessSeen + 1e-6) { bestFitnessSeen = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens > 0 ? $" [stagnant×{stagnantGens}]" : "";
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

            if (GaSearch.ShouldCataclysm(stagnantGens, _cataclysmStagnantGens))
            {
                population = GaSearch.Cataclysm(population, _populationSize, _eliteCarryOver,
                                                () => FadeShortGenotype.RandomLowVol(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<FadeShortGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
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
        }

        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE, LowVol bounds)...");
        var boSeedLv = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistoryLv = BayesianOptimizer.Refine(
            seedObs:    boSeedLv,
            bounds:     FadeShortGenotype.BoundsLowVol,
            evaluate:   v => { var g = FadeShortGenotype.FromVectorLowVol(v);
                               g.Fitness = FitnessFromCache(g, trainCaches, useFolds: true, _cfg, _tradeGate);
                               return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampionLv = boHistoryLv.OrderByDescending(h => h.Fitness).First();
        var boGenoLv     = FadeShortGenotype.FromVectorLowVol(boChampionLv.Params);
        boGenoLv.Fitness = FitnessFromCache(boGenoLv, trainCaches, useFolds: true, _cfg, _tradeGate);

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

        best.Fitness = FitnessFromCache(best, valCaches, useFolds: false, _cfg, _tradeGate);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    internal FadeShortGenotype TournamentSelect(List<FadeShortGenotype> pop) =>
        GaSearch.Tournament(pop, _tournamentK, _rng, g => g.Fitness);
}
