namespace TradingGA;

// Genetic algorithm for the dip-long strategy.
// Regime-gated bull pullback: fires only in detected bull markets (dual-EMA
// slope filter), so the GA only needs to generalise over bull periods.
//
// FoldScore filters trades to those where the bull regime has been confirmed
// for at least RegimeSustainedBars consecutive h1 bars at entry. This prevents
// trades taken on the first bar of a new regime (noise) from polluting the score.
//
// Protection mode (profit protect) is handled by DynamicGuardGenotype — moved there
// so the guard trains on all strategies combined (better N/d ratio).
//
// Fitness = lambda x CVaR_0.4(fold_scores) + (1 - lambda) x mean(fold_scores)
// over the SURVIVING walk-forward folds (those that reached MinTradesPerFold trades), where
// lambda is VC-proportional on avg N/d. The fold vector is CONSTANT LENGTH: a fold that
// never reached MinTradesPerFold enters as ThinFoldScore rather than vanishing. Monotone
// non-decreasing in every fold score by construction — see FoldScoreHelper.AggregateFoldScores
// for why `mean - stdMult x std` was not.
public class DipLongGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainH1, ReadOnlyMemory<Candle> ValH1, ReadOnlyMemory<Candle> TrainM15, ReadOnlyMemory<Candle> ValM15, double Weight = 1.0);

    private readonly int                   _populationSize;
    private readonly int                   _generations;
    private readonly int                   _eliteCount;
    private readonly int                   _migrationInterval;
    private readonly bool                  _verbose;
    private readonly Func<DateTime, double>? _tradeGate;  // null = no gating; returns weight 0..1 (set by CoevolveGA)
    private readonly RegimeBar[]?          _btcSeries;    // optional: for regime diversity bonus
    private readonly int                   _tournamentK;
    private readonly int                   _eliteCarryOver;
    private readonly int                   _cataclysmStagnantGens;
    private readonly int                   _seed;
    private readonly bool                  _seedSupplied;
    private readonly Random _rng;
    private readonly FitnessConfig _cfg;

    private const int MinTradesPerFold = 25;   // VC theory requires N > d per fold; d=14 after protection mode moved to guard
    private const int D                = 14;   // genotype parameter count (excl. Fitness)

    // populationSize / generations / migrationInterval keep their historical meaning.
    //
    // eliteCount is NOT the elitism knob — it sizes the reporting slice, the BO seed set and
    // the final-winner pool only. eliteCarryOver is the number of individuals actually copied
    // unchanged into the next generation, and it was a hardcoded literal 5 before this
    // parameter existed (a live mis-tuning trap: raising eliteCount changed nothing about the
    // search). Defaults are the shared GaSearch constants — see that file for why
    // tournamentK = 2 rather than the old 4, and why the stagnation response is now a
    // cataclysmic restart rather than a mutation-rate boost.
    public DipLongGA(
        int  populationSize    = 50,
        int  generations       = 80,
        int  eliteCount        = 15,
        int  migrationInterval = 10,
        bool verbose           = true,
        Func<DateTime, double>? tradeGate = null,
        FitnessConfig? cfg = null,
        RegimeBar[]? btcSeries = null,
        int  tournamentK           = GaSearch.DefaultTournamentK,
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
        _tournamentK           = tournamentK;
        _eliteCarryOver        = eliteCarryOver;
        _cataclysmStagnantGens = cataclysmStagnantGens;
        (_rng, _seed, _seedSupplied) = GaSearch.CreateRng(seed);
    }

    private static double FoldScore(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int    sustainedBars,
        FitnessConfig cfg,
        double volWeight = 1.0)
        => FoldScoreHelper.CanonicalRegime(returns, posFrac, sustainedBars, MinTradesPerFold, cfg, volWeight, statBonusCeiling: 1.5);

    private double FoldScoreRegime(
        List<(double Return, int RegimeBars, MarketRegime Regime)> returns,
        double posFrac,
        int    sustainedBars,
        FitnessConfig cfg,
        double volWeight = 1.0)
        => FoldScoreHelper.CanonicalRegimeStratified(returns, posFrac, sustainedBars, MinTradesPerFold, cfg, volWeight, statBonusCeiling: 1.5);

    private double Fitness(DipLongGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, h1: useValidation ? c.ValH1 : c.TrainH1,
                             m15: useValidation ? c.ValM15 : c.TrainM15))
            .Where(x => x.h1.Length >= 100 && x.m15.Length >= 400)
            .ToList();
        if (validCoins.Count == 0) return 0;

        double posFrac = Math.Clamp(ind.PositionSizePct, 0.01, 0.05);

        // Compute vol coverage from m15 candles (all coins combined)
        double volWeight = 1.0;
        if (_cfg.AtrLow > 0.0 || _cfg.AtrHigh < 9999.0)
        {
            var m15Arr = validCoins.SelectMany(x => x.m15.ToArray()).ToArray();
            var atr = Volatility.Atr(
                m15Arr.Select(c => c.High).ToArray(),
                m15Arr.Select(c => c.Low).ToArray(),
                m15Arr.Select(c => c.Close).ToArray(), 14);
            volWeight = VariantRouter.VolCoverage(atr, 0, atr.Length, _cfg.AtrLow, _cfg.AtrHigh);
        }

        if (useValidation || folds <= 1)
        {
            if (_btcSeries != null && _cfg.RegimeDiversityW > 0)
            {
                var allRegime = validCoins
                    .SelectMany(x => DipLongSimulator.GetDipLongReturns(ind, x.h1.Span, x.m15.Span)
                        .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, Time: t.Time, w); })
                        .Where(t => t.w >= 0.05))
                    .ToList();
                var tradeTags = RegimeBarLookup.TagRegimes(_btcSeries, allRegime.Select(t => t.Time).ToList());
                var allTagged = allRegime.Select((t, i) => (t.Return, t.RegimeBars, tradeTags[i])).ToList();
                return FoldScoreRegime(allTagged, posFrac, ind.RegimeSustainedBars, _cfg, volWeight);
            }
            else
            {
                var all = validCoins
                    .SelectMany(x => DipLongSimulator.GetDipLongReturns(ind, x.h1.Span, x.m15.Span)
                        .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, w); })
                        .Where(t => t.w >= 0.05)
                        .Select(t => (t.Return, t.RegimeBars)))
                    .ToList();
                return FoldScore(all, posFrac, ind.RegimeSustainedBars, _cfg, volWeight);
            }
        }

        // Walk-forward folds, k of them, cut on CALENDAR TIME rather than array indices.
        //
        // With a BTC regime series available, fold boundaries are computed once on BTC
        // (balanced on the number of active-regime bars, with an embargo gap) and handed
        // to every coin as [Start, End) time windows; each coin then binary-searches that
        // window into its own h1/m15 arrays. Index-space bounds can NOT be shared across
        // coins — a coin's training array is an 80% split of its own (variable-length)
        // history, so index i is a different calendar date on every symbol, and coins
        // shorter than a fold's start index would drop out of every later fold and dump
        // their entire history into fold 0.
        //
        // Without a BTC series, fall back to per-coin percentage folds: each coin slices
        // its own history into k equal parts, which is alignment-safe by construction.
        int k = folds;

        (DateTime Start, DateTime End)[]? windows = _btcSeries != null
            ? FoldScoreHelper.ComputeRegimeAwareFoldWindows(_btcSeries, r => r == MarketRegime.Bull, k, _cfg.EmbargoPct)
            : null;

        var foldScores = new List<double>();
        var foldCounts = new List<int>();
        // Folds ATTEMPTED, including the thin ones skipped below — the aggregator scales
        // Every attempted fold is scored -- a thin one enters at ThinFoldScore -- so that
        // concentrating all activity into one favourable
        // market window can no longer beat trading consistently across all of them.
        int attemptedFolds = 0;

        for (int f = 0; f < k; f++)
        {
            attemptedFolds++;
            var foldRet = new List<(double Return, int RegimeBars)>();
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
                foldRet.AddRange(
                    DipLongSimulator.GetDipLongReturns(ind, h1.Slice(startH1, endH1 - startH1).Span, m15.Slice(startM15, endM15 - startM15).Span)
                                    .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, w); })
                                    .Where(t => t.w >= 0.05)
                                    .Select(t => (t.Return, t.RegimeBars)));
            }

            // Only folds that actually reached MinTradesPerFold scored trades take part in
            // the aggregation. A thin fold returns the constant -1.0 sentinel, and mixing
            // constants into the aggregate would let a no-trade fold masquerade as a real
            // (merely bad) one. It still counts toward attemptedFolds and enters the aggregate
            // at ThinFoldScore, so withdrawing from a window is strictly loss-making.
            int scoredTrades = foldRet.Count(t => t.RegimeBars >= ind.RegimeSustainedBars);
            if (scoredTrades < MinTradesPerFold) continue;

            foldScores.Add(FoldScore(foldRet, posFrac, ind.RegimeSustainedBars, _cfg, volWeight));
            foldCounts.Add(scoredTrades);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    public DipLongGenotype Run(IReadOnlyList<CoinData> coins, DipLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        GaSearch.AnnounceSeed("DipLongGA.Run", _seed, _seedSupplied);

        if (_verbose)
        {
            foreach (var (coin, i) in coins.Select((c, i) => (c, i)))
                Console.WriteLine($"  Coin {i} (w={coin.Weight:F1})  " +
                    $"trainH1={coin.TrainH1.Length}  valH1={coin.ValH1.Length}");
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => DipLongGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<DipLongGenotype> eliteIsland     = new();
        double                bestFitnessSeen = double.MinValue;
        int                   stagnantGens    = 0;

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
                    strategy    = "DipLong",
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
                // Random(_rng) with NO genotype seed: the restart must sample the whole bounds
                // box, not a neighbourhood of the incumbent.
                population = GaSearch.Cataclysm(population, _populationSize, _eliteCarryOver,
                                                () => DipLongGenotype.Random(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<DipLongGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
                while (nextGen.Count < _populationSize)
                {
                    var child = DipLongGenotype.Crossover(
                                    TournamentSelect(population),
                                    TournamentSelect(population), _rng)
                                .Mutate(_rng, mutationRate);
                    nextGen.Add(child);
                }
                population = nextGen;
            }
        }

        // ── Bayesian refinement: TPE polishes the GA's elite region ─────────────
        // Seed BO with all elite members (top-15) accumulated over all generations.
        // BO finds fine-grained improvements that crossover/mutation miss near the optimum.
        if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     DipLongGenotype.Bounds,
            evaluate:   v => { var g = DipLongGenotype.FromVector(v); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = DipLongGenotype.FromVector(boChampion.Params);

        // Inject BO champion into elite island if it's competitive
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== DipLong held-out validation ===");
        // WARNING: RNG-free parallel body — see the note in the generation loop.
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    public DipLongGenotype RunLowVol(IReadOnlyList<CoinData> coins, DipLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        GaSearch.AnnounceSeed("DipLongGA.RunLowVol", _seed, _seedSupplied);

        if (_verbose)
        {
            foreach (var (coin, i) in coins.Select((c, i) => (c, i)))
                Console.WriteLine($"  Coin {i} (w={coin.Weight:F1})  " +
                    $"trainH1={coin.TrainH1.Length}  valH1={coin.ValH1.Length}");
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => DipLongGenotype.RandomLowVol(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBoundsLowVol();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.MutateLowVol(_rng, 0.25);
        }

        List<DipLongGenotype> eliteIsland     = new();
        double                bestFitnessSeen = double.MinValue;
        int                   stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            // See Run() for why the stagnation mutation-rate boost was removed.
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;

            // WARNING: this parallel body must stay RNG-FREE — System.Random is not thread-safe.
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
                    strategy    = "DipLongLowVol",
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
                // Restart draws from the LowVol random factory (this run's own bounds box),
                // unseeded — see Run() for the full rationale.
                population = GaSearch.Cataclysm(population, _populationSize, _eliteCarryOver,
                                                () => DipLongGenotype.RandomLowVol(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<DipLongGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
                while (nextGen.Count < _populationSize)
                {
                    var child = DipLongGenotype.Crossover(
                                    TournamentSelect(population),
                                    TournamentSelect(population), _rng)
                                .MutateLowVol(_rng, mutationRate);
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
            bounds:     DipLongGenotype.BoundsLowVol,
            evaluate:   v => { var g = DipLongGenotype.FromVectorLowVol(v); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = DipLongGenotype.FromVectorLowVol(boChampion.Params);

        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== DipLongLowVol held-out validation ===");
        // WARNING: RNG-free parallel body — see the note in the generation loop.
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    // Tournament size comes from the constructor (default GaSearch.DefaultTournamentK = 2), not
    // from a defaulted method parameter that no call site ever overrode. internal so the
    // selection-pressure test can exercise the REAL selector rather than a copy of it.
    internal DipLongGenotype TournamentSelect(List<DipLongGenotype> pop) =>
        GaSearch.Tournament(pop, _tournamentK, _rng, g => g.Fitness);
}
