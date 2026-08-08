namespace TradingGA;

// Genetic algorithm for the rip-short strategy.
// Regime-gated bear trend-continuation: fires only in detected bear markets
// (dual-EMA slope filter), so the GA only needs to generalise over bear periods.
//
// Direction-mirror of DipLongGA. FoldScore filters trades to those where the bear
// regime has been confirmed for at least RegimeSustainedBars consecutive h1 bars at
// entry — trades taken on the first bar of a new regime (noise) don't pollute the score.
//
// Training uses flat pessimistic funding (funding: null) so it never requires a
// funding dataset; live/backtest paths supply a FundingRateSession for real rates.
//
// Fitness = coverage x [lambda x CVaR_0.4(fold_scores) + (1 - lambda) x mean(fold_scores)]
// over the SURVIVING walk-forward folds (those that reached MinTradesPerFold trades), where
// coverage = surviving/attempted folds and lambda is VC-proportional on avg N/d. Monotone
// non-decreasing in every fold score by construction — see FoldScoreHelper.AggregateFoldScores
// for why `mean - stdMult x std` was not.
public class RipShortGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainH1, ReadOnlyMemory<Candle> ValH1, ReadOnlyMemory<Candle> TrainM15, ReadOnlyMemory<Candle> ValM15, double Weight = 1.0);

    private readonly int                   _populationSize;
    private readonly int                   _generations;
    private readonly int                   _eliteCount;
    private readonly int                   _migrationInterval;
    private readonly bool                  _verbose;
    private readonly Func<DateTime, double>? _tradeGate;  // null = no gating; returns weight 0..1 (set by CoevolveGA)
    private readonly Random _rng = new();
    private readonly FitnessConfig _cfg;
    private readonly RegimeBar[]? _btcSeries;

    private const int MinTradesPerFold = 25;   // VC theory requires N > d per fold; d=14
    private const int D                = 14;   // genotype parameter count (excl. Fitness)

    public RipShortGA(
        int  populationSize    = 50,
        int  generations       = 80,
        int  eliteCount        = 15,
        int  migrationInterval = 10,
        bool verbose           = true,
        Func<DateTime, double>? tradeGate = null,
        FitnessConfig? cfg = null,
        RegimeBar[]? btcSeries = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _tradeGate         = tradeGate;
        _cfg               = cfg ?? new FitnessConfig();
        _btcSeries         = btcSeries;
    }

    // Canonical fold score, filtered to trades that fired during a CONFIRMED bear regime
    // (RegimeBars >= sustainedBars) — CanonicalRegime does that filtering itself.
    //
    // This used to be a hand-inlined copy of the canonical formula. The copy had drifted
    // in three ways that all mattered: the six FitnessConfig term weights
    // (GainW/WrW/QualityW/FreqW/DdPenalty/RetentionW) were INERT here because the
    // coefficients were hardcoded, CVaRPenalty and TailRatioBonus were never applied at
    // all, and the fitness was therefore on a different scale from the other strategies —
    // which CoevolveGA and RegimeRouterGA then combine.
    //
    // The one divergence that was deliberate is PRESERVED: statBonusCeiling stays at 1.0
    // (max combined stat stack ~5x, not ~40x) because RipShort's sparse bear-window
    // sample let an uncapped stack compound a lucky fold into a fake edge
    // (train F=15718 vs held-out OOS PF=0.88, see the overfit check).
    private static double FoldScore(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int    sustainedBars,
        FitnessConfig cfg,
        double volWeight = 1.0)
        => FoldScoreHelper.CanonicalRegime(
            returns, posFrac, sustainedBars, MinTradesPerFold, cfg, volWeight, statBonusCeiling: 1.0);

    private double Fitness(RipShortGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
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
            var all = validCoins
                .SelectMany(x => RipShortSimulator.GetRipShortReturnsWithRegime(ind, x.h1.Span, x.m15.Span, funding: null)
                    .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, w); })
                    .Where(t => t.w >= 0.05)
                    .Select(t => (t.Return, t.RegimeBars)))
                .ToList();
            return FoldScore(all, posFrac, ind.RegimeSustainedBars, _cfg, volWeight);
        }

        // Walk-forward folds, k of them, cut on CALENDAR TIME rather than array indices.
        //
        // With a BTC regime series available, fold boundaries are computed once on BTC
        // (balanced on the number of active bear bars, with an embargo gap) and handed to
        // every coin as [Start, End) time windows; each coin then binary-searches that
        // window into its own h1/m15 arrays. Index-space bounds can NOT be shared across
        // coins — RipShort's training arrays are bear-window-FILTERED concatenations of a
        // few thousand bars, so index i is a completely different calendar date on every
        // symbol, and coins shorter than a fold's start index would drop out of every
        // later fold and dump their entire history into fold 0.
        //
        // Without a BTC series, fall back to per-coin percentage folds: each coin slices
        // its own history into k equal parts, which is alignment-safe by construction.
        int k = folds;

        (DateTime Start, DateTime End)[]? windows = _btcSeries != null
            ? FoldScoreHelper.ComputeRegimeAwareFoldWindows(_btcSeries, r => r == MarketRegime.Bear, k, _cfg.EmbargoPct)
            : null;

        var foldScores = new List<double>();
        var foldCounts = new List<int>();
        // Folds ATTEMPTED, including the thin ones skipped below — the aggregator scales
        // by surviving/attempted so that concentrating all activity into one favourable
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
                    RipShortSimulator.GetRipShortReturnsWithRegime(ind, h1.Slice(startH1, endH1 - startH1).Span, m15.Slice(startM15, endM15 - startM15).Span, funding: null)
                                      .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, w); })
                                      .Where(t => t.w >= 0.05)
                                      .Select(t => (t.Return, t.RegimeBars)));
            }

            // Only folds that actually reached MinTradesPerFold scored trades take part in
            // the aggregation. A thin fold returns the constant -1.0 sentinel, and mixing
            // constants into the aggregate would let a no-trade fold masquerade as a real
            // (merely bad) one. It still counts toward attemptedFolds, so skipping costs coverage.
            int scoredTrades = foldRet.Count(t => t.RegimeBars >= ind.RegimeSustainedBars);
            if (scoredTrades < MinTradesPerFold) continue;

            foldScores.Add(FoldScore(foldRet, posFrac, ind.RegimeSustainedBars, _cfg, volWeight));
            foldCounts.Add(scoredTrades);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    public RipShortGenotype Run(IReadOnlyList<CoinData> coins, RipShortGenotype? seed = null)
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
            .Select(_ => RipShortGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<RipShortGenotype> eliteIsland     = new();
        double                 bestFitnessSeen = double.MinValue;
        int                    stagnantGens    = 0;

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

            if (gen % 10 == 0)
            {
                static double safe(double v) => double.IsFinite(v) ? v : 0.0;
                var progress = new
                {
                    strategy    = "RipShort",
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

            var nextGen = new List<RipShortGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = RipShortGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .Mutate(_rng, mutationRate);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        // ── Bayesian refinement: TPE polishes the GA's elite region ─────────────
        if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     RipShortGenotype.Bounds,
            evaluate:   v => { var g = RipShortGenotype.FromVector(v); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = RipShortGenotype.FromVector(boChampion.Params);

        // Inject BO champion into elite island if it's competitive
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        // ── Final selection: winner chosen on TRAIN fitness (no validation re-selection) ──
        // Held-out validation is computed for the single winner as a report only.
        if (_verbose) Console.WriteLine("\n=== RipShort held-out validation (report-only, not used for selection) ===");

        var best = eliteIsland.First();   // elite sorted by train fitness
        double trainFit = best.Fitness;
        best.Fitness = Fitness(best, coins, useValidation: true);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    public RipShortGenotype RunLowVol(IReadOnlyList<CoinData> coins, RipShortGenotype? seed = null)
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
            .Select(_ => RipShortGenotype.RandomLowVol(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBoundsLowVol();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.MutateLowVol(_rng, 0.25);
        }

        List<RipShortGenotype> eliteIsland     = new();
        double                 bestFitnessSeen = double.MinValue;
        int                    stagnantGens    = 0;

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

            if (gen % 10 == 0)
            {
                static double safe(double v) => double.IsFinite(v) ? v : 0.0;
                var progress = new
                {
                    strategy    = "RipShortLowVol",
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

            var nextGen = new List<RipShortGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = RipShortGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .MutateLowVol(_rng, mutationRate);
                nextGen.Add(child);
            }
            population = nextGen;
        }

        if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
        var boSeed = eliteIsland
            .Select(g => (g.ToVector(), g.Fitness))
            .ToList();

        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     RipShortGenotype.BoundsLowVol,
            evaluate:   v => { var g = RipShortGenotype.FromVectorLowVol(v); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 60,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = RipShortGenotype.FromVectorLowVol(boChampion.Params);

        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== RipShortLowVol held-out validation (report-only, not used for selection) ===");

        var best = eliteIsland.First();
        double trainFit = best.Fitness;
        best.Fitness = Fitness(best, coins, useValidation: true);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    private RipShortGenotype TournamentSelect(List<RipShortGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
