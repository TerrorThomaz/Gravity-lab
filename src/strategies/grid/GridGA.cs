namespace TradingGA;

// Genetic algorithm for grid trading (ranging-market long grid, 1h candles).
// Structure mirrors FadeShortGA.
//
// FoldScore operates on per-SESSION returns (mean of all level fills per activation),
// not per-fill returns. This prevents inflating WR and trade count when GridLevels > 1.
//
// FoldScore is FoldScoreHelper.Canonical under FoldScoreHelper.GridShape — the SAME formula
// every other strategy uses, with Grid's justified divergences expressed as a config
// delta rather than a second hand-inlined formula:
//   win-rate slope 0.5 not 3.0 (per-session WR is structurally high)
//   drawdown penalty 2× stronger than swing (a grid's tail risk is the whole-ladder stop-out)
//   no frequency bonus — session count is driven by coin volatility, not strategy quality
//   no quality term — Canonical's rr ramp is calibrated for rr >= 1.0; a grid runs below it.
// Fitness = lambda x CVaR_0.4(fold_scores) + (1 - lambda) x mean(fold_scores)
// over the SURVIVING walk-forward folds (those that reached MinTradesPerFold trades), where
// lambda is VC-proportional on avg N/d. The fold vector is CONSTANT LENGTH: a fold that
// never reached MinTradesPerFold enters as ThinFoldScore rather than vanishing. Monotone
// non-decreasing in every fold score by construction — see FoldScoreHelper.AggregateFoldScores
// for why `mean - stdMult x std` was not.
// Folds are cut PER COIN on that coin's own array — see Fitness().
public class GridGeneticAlgorithm
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

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

    // Router gating weight; null = ungated. Weights by t.Time (the EXIT bar), matching the
    // field CombinedBacktest gates on, so train and serve agree. Set by CoevolveGA.
    private readonly Func<DateTime, double>? _tradeGate;

    private const int    MinTradesPerFold = 10;
    private const double FitPosFrac       = 0.03;
    private const int    D                = 10;   // genotype parameter count (excl. Fitness) — see GridGenotype.Bounds

    // eliteCount sizes the reporting slice / BO seed set / final-winner pool ONLY.
    // eliteCarryOver is the real elitism knob (was a hardcoded literal 5). See GaSearch for the
    // tournamentK = 2 rationale and for why stagnation now triggers a cataclysmic restart.
    // seed: null draws one explicitly and prints it, so any run can be reproduced.
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

    // Canonical fold score under the Grid-family shape transform.
    //
    // This used to be a hand-inlined copy of the fitness formula, which left the six
    // FitnessConfig term weights inert, skipped CVaRPenalty/TailRatioBonus entirely, and
    // put Grid on a different SCALE from every other strategy — a scale CoevolveGA and
    // RegimeRouterGA then combined with the others as if they were comparable.
    //
    // FoldScoreHelper.GridShape carries the justified divergences forward as a config
    // delta instead: win-rate slope 0.5 not 3.0, drawdown divisor x20 not x10, no
    // frequency bonus, and no quality term (Canonical's rr ramp is calibrated for
    // rr >= 1.0, while a grid operates below it — see GridShape). The retention and gain
    // terms are re-enabled at their neutral 1.0, and the tail terms now apply.
    // statBonusCeiling stays at 1.0 as before.
    private static double FoldScore(List<double> returns, FitnessConfig cfg, double volWeight = 1.0)
        => FoldScoreHelper.Canonical(
            returns, FitPosFrac, MinTradesPerFold,
            FoldScoreHelper.GridShape(cfg), volWeight, statBonusCeiling: 1.0);

    private double Fitness(GridGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, arr: useValidation ? c.ValCandles : c.TrainCandles))
            .Where(x => x.arr.Length >= 100)
            .ToList();
        if (validCoins.Count == 0) return 0;

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => GridSimulator.GetGridSessionReturns(ind, x.arr.Span)
                    .Select(t => (t.Return, w: _tradeGate?.Invoke(t.Time) ?? 1.0))
                    .Where(t => t.w >= 0.05).Select(t => t.Return * t.w))
                .ToList();
            return FoldScore(all, _cfg);
        }

        // Walk-forward folds, k of them, cut PER COIN on that coin's OWN array.
        //
        // Index-space fold bounds must never be shared across coins: each coin's training
        // array is a percentage split of its own (variable-length) history, so index i is a
        // different calendar date on every symbol. The previous code cut the folds on
        // minLen — the SHORTEST coin — which both misaligned "fold f" across symbols and
        // silently discarded every bar past minLen on every longer coin. Grid is
        // single-timeframe (h1 only; CoinData carries one candle array), so there is no
        // h1→m15 index mapping to maintain here.
        //
        // FURTHER IMPROVEMENT: if a BTC RegimeBar series is ever threaded into this GA,
        // switch to FoldScoreHelper.ComputeRegimeAwareFoldWindows + RangeForWindow so that
        // fold f covers the same calendar stretch of market history for every symbol — that
        // is what the regime-gated GAs (DipLong/SwingLong/FadeLong/RipShort) now do. No BTC
        // series reaches this constructor today, so per-coin percentage folds are the
        // alignment-safe option available here.
        //
        // k is derived from the MEDIAN coin length rather than the shortest, so a single
        // short symbol can no longer cap the fold count for the whole run. Coins whose own
        // fold range comes out under 40 bars are skipped for that fold instead.
        int medianLen = MedianLength(validCoins.Select(x => x.arr.Length));
        int k         = Math.Min(folds, medianLen / 40);

        if (k < 2)
        {
            var all = validCoins
                .SelectMany(x => GridSimulator.GetGridSessionReturns(ind, x.arr.Span)
                    .Select(t => (t.Return, w: _tradeGate?.Invoke(t.Time) ?? 1.0))
                    .Where(t => t.w >= 0.05).Select(t => t.Return * t.w))
                .ToList();
            return FoldScore(all, _cfg);
        }

        var foldScores = new List<double>(k);
        var foldCounts = new List<int>(k);
        // Folds ATTEMPTED, including the thin ones skipped below — the aggregator scales
        // Every attempted fold is scored -- a thin one enters at ThinFoldScore -- so that
        // concentrating all activity into one favourable
        // market window can no longer beat trading consistently across all of them.
        int attemptedFolds = 0;
        for (int f = 0; f < k; f++)
        {
            attemptedFolds++;
            var foldReturns = new List<double>();
            foreach (var (coin, arr) in validCoins)
            {
                var (start, end) = FoldScoreHelper.PerCoinFoldRange(arr.Length, k, f, _cfg.EmbargoPct);
                if (end - start < 40) continue;
                foldReturns.AddRange(
                    GridSimulator.GetGridSessionReturns(ind, arr.Slice(start, end - start).Span)
                        .Select(t => (t.Return, w: _tradeGate?.Invoke(t.Time) ?? 1.0))
                        .Where(t => t.w >= 0.05).Select(t => t.Return * t.w));
            }

            // Only folds that actually reached MinTradesPerFold sessions take part in the
            // aggregation. A thin fold returns the constant -1.0 sentinel from FoldScore,
            // and mixing constants into the aggregate would let a no-trade fold masquerade as
            // a real (merely bad) one. It still counts toward attemptedFolds, so
            // skipping a fold costs coverage.
            if (foldReturns.Count < MinTradesPerFold) continue;

            foldScores.Add(FoldScore(foldReturns, _cfg));
            foldCounts.Add(foldReturns.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    // Median of a set of array lengths. Used to size the fold count without letting the
    // single shortest symbol dictate k for everyone (folds are per-coin now, so k no longer
    // has to fit inside the shortest array).
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
            // Mutation rate anneals 0.65 → 0.05 across the run. The old
            // `stagnantGens >= 15 ? rate * 2` boost is gone — see GaSearch.Cataclysm for why
            // doubling a per-gene PROBABILITY on a fixed creep step cannot escape a basin, and
            // why the boost latched on permanently once it fired.
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;

            // WARNING: this parallel body must stay RNG-FREE. System.Random is not thread-safe;
            // a single _rng draw added here would silently corrupt its internal state (and
            // destroy reproducibility) with no exception to point at it.
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
                // CHC restart. `population` is already sorted best-first, so the survivors are
                // exactly the individuals the normal path would have carried over — the
                // best-so-far genotype lives through the restart, and eliteIsland (captured
                // above from the same sorted list) still holds the champion regardless.
                // Random(_rng, seed: null, adxCeiling) — NO genotype seed, so the restart
                // samples the whole bounds box rather than a neighbourhood of the incumbent.
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

        // ── Bayesian refinement ──────────────────────────────────────────────────
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

        // Select winner by train fitness (best taken from elite sorted by train fitness)
        var best = eliteIsland.First();
        double trainFit = best.Fitness;

        // Compute validation score for the winner only
        best.Fitness = Fitness(best, coins, useValidation: true);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    // Tournament size comes from the constructor (default GaSearch.DefaultTournamentK = 2), not
    // from a defaulted method parameter that no call site ever overrode. internal so the
    // selection-pressure test can exercise the REAL selector rather than a copy of it.
    internal GridGenotype TournamentSelect(List<GridGenotype> pop) =>
        GaSearch.Tournament(pop, _tournamentK, _rng, g => g.Fitness);
}
