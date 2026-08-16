using System.Collections.Concurrent;

namespace TradingGA;

// SwingLong GA: bull-regime RSI bullish divergence + bullish BoS.
// Fitness: CVaR/mean blend over walk-forward folds. Uses truncation selection (not tournament).
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
    private readonly int    _eliteCarryOver;
    private readonly int    _cataclysmStagnantGens;
    private readonly int    _seed;
    private readonly bool   _seedSupplied;
    private readonly Random _rng;
    private readonly Func<DateTime, double>? _tradeGate;
    private readonly FitnessConfig _cfg;
    private readonly RegimeBar[]? _btcSeries;

    private const int MinTradesPerFold = 20;
    private const int D                = 14;

    // Truncation selection: parent A from elite, parent B from top half. Harsher than tournament.
    // eliteCount sizes parent-A pool + reporting/BO slice. eliteCarryOver = real elitism knob.
    public SwingLongGA(
        int populationSize    = 60,
        int generations       = 100,
        int eliteCount        = 10,
        int migrationInterval = 10,
        bool verbose          = false,
        Func<DateTime, double>? tradeGate = null,
        FitnessConfig? cfg = null,
        RegimeBar[]? btcSeries = null,
        int  eliteCarryOver        = GaSearch.DefaultEliteCarryOver,
        int  cataclysmStagnantGens = GaSearch.DefaultCataclysmStagnantGens,
        int? seed                  = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _tradeGate         = tradeGate;
        _cfg               = cfg ?? new FitnessConfig();
        _btcSeries         = btcSeries;

        // Expose BTC regime as signed alignment score for entry test. O(log n) binary search.
        if (btcSeries is { Length: > 0 })
        {
            var bars = btcSeries;
            SwingLongSimulator.BtcRegimeProbe = t =>
            {
                long ticks = t.Ticks;
                if (ticks <= bars[0].Time.Ticks)  return Signed(bars[0]);
                if (ticks >= bars[^1].Time.Ticks) return Signed(bars[^1]);
                int lo = 0, hi = bars.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (bars[mid].Time.Ticks <= ticks) lo = mid; else hi = mid - 1;
                }
                return Signed(bars[lo]);
            };
        }

        static double Signed(RegimeBar b) => b.Regime switch
        {
            MarketRegime.Bull => +b.Confidence,
            MarketRegime.Bear => -b.Confidence,
            _                 => 0.0,
        };
        _eliteCarryOver        = eliteCarryOver;
        _cataclysmStagnantGens = cataclysmStagnantGens;
        (_rng, _seed, _seedSupplied) = GaSearch.CreateRng(seed);
    }

    private double Fitness(SwingLongGenotype g, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, h1: useValidation ? c.ValH1 : c.TrainH1,
                             m15: useValidation ? c.ValM15 : c.TrainM15))
            .Where(x => x.h1.Length >= 100 && x.m15.Length >= 400)
            .ToList();
        if (validCoins.Count == 0) return 0;

        double posFrac = Math.Clamp(g.PositionSizePct, 0.01, 0.05);

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => SwingLongSimulator.GetSwingLongReturns(g, x.h1.Span, x.m15.Span)
                    .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, w); })
                    .Where(t => t.w >= 0.05)
                    .Select(t => t.Return))
                .ToList();
            return FoldScoreHelper.Canonical(all, posFrac, MinTradesPerFold, _cfg, statBonusCeiling: 1.5);
        }

        // Walk-forward folds on CALENDAR TIME (BTC regime-aware when available, else per-coin %).
        int k = folds;

        (DateTime Start, DateTime End)[]? windows = _btcSeries != null
            ? FoldScoreHelper.ComputeRegimeAwareFoldWindows(_btcSeries, r => r == MarketRegime.Bull, k, _cfg.EmbargoPct)
            : null;

        var foldScores = new List<double>();
        var foldCounts = new List<int>();
        int attemptedFolds = 0;  // includes thin folds

        for (int f = 0; f < k; f++)
        {
            attemptedFolds++;
            var foldReturns = new List<double>();
            foreach (var (coin, h1, m15) in validCoins)
            {
                int startH1, endH1, startM15, endM15;
                if (windows != null)
                {
                    var (winStart, winEnd) = windows[f];
                    (startH1,  endH1)  = FoldScoreHelper.RangeForWindow(h1.Span,  winStart, winEnd);
                    (startM15, endM15) = FoldScoreHelper.RangeForWindow(m15.Span, winStart, winEnd);
                }
                else
                {
                    (startH1, endH1) = FoldScoreHelper.PerCoinFoldRange(h1.Length, k, f, _cfg.EmbargoPct);
                    startM15 = Math.Min(startH1 * 4, m15.Length);
                    endM15   = Math.Min(endH1   * 4, m15.Length);
                }
                if (endH1  - startH1  < 40) continue;
                if (endM15 - startM15 < 40) continue;
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(g, h1.Slice(startH1, endH1 - startH1).Span, m15.Slice(startM15, endM15 - startM15).Span))
                {
                    double w = _tradeGate?.Invoke(t.Time) ?? 1.0;
                    if (w >= 0.05) foldReturns.Add(t.Return * w);
                }
            }

            if (foldReturns.Count < MinTradesPerFold) continue;

            foldScores.Add(FoldScoreHelper.Canonical(foldReturns, posFrac, MinTradesPerFold, _cfg, statBonusCeiling: 1.5));
            foldCounts.Add(foldReturns.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    public SwingLongGenotype Run(IReadOnlyList<CoinData> coins, SwingLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        GaSearch.AnnounceSeed("SwingLongGA.Run", _seed, _seedSupplied);

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
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            // WARNING: parallel body must stay RNG-free.
            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

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
                    strategy    = "SwingLong",
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
                                                () => SwingLongGenotype.Random(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<SwingLongGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
                while (nextGen.Count < _populationSize)
                {
                    var a = eliteIsland[_rng.Next(eliteIsland.Count)];
                    var b = population[_rng.Next(Math.Min(population.Count, _populationSize / 2))];
                    nextGen.Add(SwingLongGenotype.Crossover(a, b, _rng).Mutate(_rng, mutationRate));
                }
                population = nextGen;
            }
        }

        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     SwingLongGenotype.Bounds,
            evaluate:   v => { var geno = SwingLongGenotype.FromVector(v); geno.Fitness = Fitness(geno, coins, useValidation: false); return geno.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = SwingLongGenotype.FromVector(boChampion.Params);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== SwingLong held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));
        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    public SwingLongGenotype RunLowVol(IReadOnlyList<CoinData> coins, SwingLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        GaSearch.AnnounceSeed("SwingLongGA.RunLowVol", _seed, _seedSupplied);

        if (_verbose && seed != null) Console.WriteLine($"  Seeding from: {seed}");

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => SwingLongGenotype.RandomLowVol(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBoundsLowVol();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.MutateLowVol(_rng, 0.25);
        }

        List<SwingLongGenotype> eliteIsland    = new();
        double                  bestFitnessSeen = double.MinValue;
        int                     stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

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
                    strategy    = "SwingLongLowVol",
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
                                                () => SwingLongGenotype.RandomLowVol(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<SwingLongGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
                while (nextGen.Count < _populationSize)
                {
                    var a = eliteIsland[_rng.Next(eliteIsland.Count)];
                    var b = population[_rng.Next(Math.Min(population.Count, _populationSize / 2))];
                    nextGen.Add(SwingLongGenotype.Crossover(a, b, _rng).MutateLowVol(_rng, mutationRate));
                }
                population = nextGen;
            }
        }

        if (_verbose) Console.WriteLine("\n  BO refinement (60 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     SwingLongGenotype.BoundsLowVol,
            evaluate:   v => { var geno = SwingLongGenotype.FromVectorLowVol(v); geno.Fitness = Fitness(geno, coins, useValidation: false); return geno.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = SwingLongGenotype.FromVectorLowVol(boChampion.Params);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== SwingLongLowVol held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));
        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }
}
