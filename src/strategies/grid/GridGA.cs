namespace TradingGA;

// Grid GA: ranging-market long grid, 1h candles. Per-session returns (not per-fill).
// Fitness: CVaR/mean blend with GridShape config delta (lower WR slope, 2x DD penalty, no freq bonus).
public class GridGeneticAlgorithm
{
    // Funding is per-symbol; null = floor fallback (not zero cost).
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles,
                           double Weight = 1.0, FundingRateSession? Funding = null);

    private readonly int           _populationSize;
    private readonly int           _generations;
    private readonly int           _eliteCount;
    private readonly int           _migrationInterval;
    private readonly bool          _verbose;
    private readonly int           _tournamentK;
    private readonly int           _eliteCarryOver;
    private readonly int           _cataclysmStagnantGens;
    private readonly int           _seed;
    private readonly bool          _seedSupplied;
    private readonly Random        _rng;
    private readonly FitnessConfig _cfg;

    // Router gating: weights by exit time, set by CoevolveGA.
    private readonly Func<DateTime, double>? _tradeGate;

    private const int    MinTradesPerFold = 10;
    private const double FitPosFrac       = 0.03;
    private const int    D                = 10;   // genotype parameter count (excl. Fitness) — see GridGenotype.Bounds

    public GridGeneticAlgorithm(
        int            populationSize    = 60,
        int            generations       = 100,
        int            eliteCount        = 15,
        int            migrationInterval = 10,
        bool           verbose           = true,
        FitnessConfig? cfg               = null,
        Func<DateTime, double>? tradeGate = null,
        int            tournamentK           = GaSearch.DefaultTournamentK,
        int            eliteCarryOver        = GaSearch.DefaultEliteCarryOver,
        int            cataclysmStagnantGens = GaSearch.DefaultCataclysmStagnantGens,
        int?           seed                  = null)
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

    // Canonical fold score with GridShape config delta.
    private static double FoldScore(List<double> returns, FitnessConfig cfg, double volWeight = 1.0,
                                    IReadOnlyList<double>? maePct = null)
        => FoldScoreHelper.Canonical(
            returns, FitPosFrac, MinTradesPerFold,
            FoldScoreHelper.GridShape(cfg), volWeight, statBonusCeiling: 1.0, maePct: maePct);

    // Collect gate-weighted returns AND their worst adverse excursions, kept index-aligned. The
    // gate both filters and scales, so the excursion has to be scaled by the same weight — a
    // half-sized position takes a half-sized drawdown.
    private static void Collect(
        GridGenotype ind, ReadOnlySpan<Candle> span, FundingRateSession? funding,
        Func<DateTime, double>? gate, List<double> rets, List<double> maes)
    {
        var mae    = new List<double>();
        var trades = GridSimulator.GetGridSessionReturns(ind, span, funding, mae);
        for (int i = 0; i < trades.Count; i++)
        {
            double w = gate?.Invoke(trades[i].Time) ?? 1.0;
            if (w < 0.05) continue;
            rets.Add(trades[i].Return * w);
            maes.Add(i < mae.Count ? mae[i] * w : 0.0);
        }
    }

    private double Fitness(GridGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        GaTrialCounter.Shared.Record("grid");
        var validCoins = coins
            .Select(c => (c, arr: useValidation ? c.ValCandles : c.TrainCandles))
            .Where(x => x.arr.Length >= 100)
            .ToList();
        if (validCoins.Count == 0) return 0;

        if (useValidation || folds <= 1)
        {
            var all = new List<double>(); var allMae = new List<double>();
            foreach (var x in validCoins) Collect(ind, x.arr.Span, x.c.Funding, _tradeGate, all, allMae);
            return FoldScore(all, _cfg, maePct: allMae);
        }

        // Per-coin folds; k from median coin length (not shortest).
        int medianLen = MedianLength(validCoins.Select(x => x.arr.Length));
        int k         = Math.Min(folds, medianLen / 40);

        if (k < 2)
        {
            var all = new List<double>(); var allMae = new List<double>();
            foreach (var x in validCoins) Collect(ind, x.arr.Span, x.c.Funding, _tradeGate, all, allMae);
            return FoldScore(all, _cfg, maePct: allMae);
        }

        var foldScores = new List<double>(k);
        var foldCounts = new List<int>(k);
        int attemptedFolds = 0;  // includes thin folds
        for (int f = 0; f < k; f++)
        {
            attemptedFolds++;
            var foldReturns = new List<double>();
            var foldMae     = new List<double>();
            foreach (var (coin, arr) in validCoins)
            {
                var (start, end) = FoldScoreHelper.PerCoinFoldRange(arr.Length, k, f, _cfg.EmbargoPct);
                if (end - start < 40) continue;
                Collect(ind, arr.Slice(start, end - start).Span, coin.Funding, _tradeGate,
                        foldReturns, foldMae);
            }

            if (foldReturns.Count < MinTradesPerFold) continue;

            foldScores.Add(FoldScore(foldReturns, _cfg, maePct: foldMae));
            foldCounts.Add(foldReturns.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    // POST-GA FINALIST SCREEN. The GA returns an argmax over tens of thousands of candidates; on
    // this sample the MinBTL budget is about five INDEPENDENT configurations, so the argmax is by
    // default the best of many draws from noise. These two screens cannot show a genotype is good —
    // they can show it is fragile, which is cheaper and more actionable. Report-only: nothing here
    // changes selection, because using them to select would make them one more fitted surface.
    public void ScreenFinalist(GridGenotype best, IReadOnlyList<CoinData> coins)
    {
        Console.WriteLine("\n=== Finalist screen (report-only — see docs/RIGOR_REWORK_2026-09.md §6) ===");

        // Neighbourhood shape. Mutate is the natural perturbation operator: it already respects
        // gene bounds, so a neighbour is always a genotype the GA could itself have produced.
        var rob = FinalistScreen.PerturbedFitness(
            best,
            g => Fitness(g, coins, useValidation: false),
            (g, rng, mag) => g.Mutate(rng, mag).ClampToBounds(),
            samplesPerMagnitude: 8);
        Console.WriteLine("  " + FinalistScreen.Format(rob));

        // Concentration of the edge. Pooled train returns under the winning genotype, scored with
        // the same fold score the GA selected on, so the two numbers are comparable.
        var rets = new List<double>(); var mae = new List<double>();
        foreach (var c in coins)
            if (c.TrainCandles.Length >= 100) Collect(best, c.TrainCandles.Span, c.Funding, null, rets, mae);
        if (rets.Count >= MinTradesPerFold)
            Console.WriteLine("  " + FinalistScreen.Format(
                FinalistScreen.OutlierSensitivity(rets, r => FoldScore(r, FinalistScreen.ScreenCfg(_cfg)))));
        else
            Console.WriteLine($"  outlier sensitivity: only {rets.Count} train trades — not scored");
    }

    private static int MedianLength(IEnumerable<int> lengths)
    {
        var sorted = lengths.OrderBy(n => n).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    public GridGenotype Run(IReadOnlyList<CoinData> coins, GridGenotype? seed = null, double adxCeiling = 20.0)
    {
        if (coins.Count == 0 || coins.All(c => c.TrainCandles.Length == 0))
            throw new ArgumentException("No training candles found.");

        GaSearch.AnnounceSeed("GridGeneticAlgorithm.Run", _seed, _seedSupplied);

        if (_verbose)
        {
            foreach (var (coin, i) in coins.Select((c, i) => (c, i)))
                Console.WriteLine($"  Coin {i} (w={coin.Weight:F1})  " +
                    $"train={coin.TrainCandles.Length} candles  val={coin.ValCandles.Length} candles");
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => GridGenotype.Random(_rng, seed, adxCeiling))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds(adxCeiling);
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25, adxCeiling);
        }

        List<GridGenotype> eliteIsland    = new();
        double             bestFitness    = double.MinValue;
        int                stagnantGens   = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            // WARNING: parallel body must stay RNG-free.
            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitness + 1e-6) { bestFitness = topFitness; stagnantGens = 0; }
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
                    strategy    = "Grid",
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
                                                () => GridGenotype.Random(_rng, null, adxCeiling), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<GridGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
                while (nextGen.Count < _populationSize)
                {
                    var child = GridGenotype.Crossover(
                                    TournamentSelect(population),
                                    TournamentSelect(population), _rng)
                                .Mutate(_rng, mutationRate, adxCeiling);
                    nextGen.Add(child);
                }
                population = nextGen;
            }
        }

        if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     GridGenotype.Bounds,
            evaluate:   v => { var g = GridGenotype.FromVector(v, adxCeiling); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = GridGenotype.FromVector(boChampion.Params, adxCeiling);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");

        var best = eliteIsland.First();
        double trainFit = best.Fitness;
        best.Fitness = Fitness(best, coins, useValidation: true);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");

        if (_verbose) ScreenFinalist(best, coins);
        return best;
    }

    internal GridGenotype TournamentSelect(List<GridGenotype> pop) =>
        GaSearch.Tournament(pop, _tournamentK, _rng, g => g.Fitness);
}
