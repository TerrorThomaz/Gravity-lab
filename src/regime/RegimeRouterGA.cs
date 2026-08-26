namespace TradingGA;

// Trains RegimeRouterGenotype thresholds. Fitness: 5-fold time-sequential CV via AggregateFoldScores.
// Only pass strategies with positive fitness — the router cannot learn from a genotype with no edge.
public class RegimeRouterGA
{

    public enum StrategyKind { FadeShort, Grid, DipLong, FadeLong, RipShort, GridShort, SwingLong, AccumulationGrid, FadeShortLowVol, DipLongLowVol, SwingLongLowVol, RipShortLowVol, FadeShortHighVol, DipLongHighVol, SwingLongHighVol, RipShortHighVol }

    public record TradeRecord(StrategyKind Kind, DateTime Time, double Return, double Frac);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly int    _tournamentK;
    private readonly int    _eliteCarryOver;
    private readonly int    _cataclysmStagnantGens;
    private readonly int    _seed;
    private readonly bool   _seedSupplied;
    private readonly Random _rng;

    private const int MinTrades = 30;
    private const int RouterGeneCount = 12;
    public RegimeRouterGA(
        int  populationSize    = 50,
        int  generations       = 150,
        int  eliteCount        = 10,
        int  migrationInterval = 10,
        bool verbose           = true,
        int  tournamentK           = GaSearch.DefaultTournamentK,
        int  eliteCarryOver        = 3,
        int  cataclysmStagnantGens = GaSearch.DefaultCataclysmStagnantGens,
        int? seed                  = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _tournamentK           = tournamentK;
        _eliteCarryOver        = eliteCarryOver;
        _cataclysmStagnantGens = cataclysmStagnantGens;
        (_rng, _seed, _seedSupplied) = GaSearch.CreateRng(seed);
    }

    public RegimeRouterGenotype Run(
        RegimeBar[]              btcSeries,
        RegimeBar[]?             ethSeries,
        IReadOnlyList<TradeRecord> trades,
        RegimeRouterGenotype?    seed = null)
    {
        if (btcSeries.Length < 500)
            throw new ArgumentException("Need at least 500 BTC h1 bars for router training.");

        GaSearch.AnnounceSeed("RegimeRouterGA.Run", _seed, _seedSupplied);


        var btcTimeIndex = BuildTimeIndex(btcSeries);


        var validTrades = trades
            .Where(t => btcTimeIndex.ContainsKey(HourKey(t.Time)))
            .ToList();

        if (_verbose)
        {
            Console.WriteLine($"  Regime series: {btcSeries.Length} BTC bars" +
                              (ethSeries != null ? $" + {ethSeries.Length} ETH bars" : " (BTC only)"));
            Console.WriteLine($"  Trade records: {validTrades.Count} total " +
                string.Join(" ", Enum.GetValues<StrategyKind>()
                    .Select(k => $"({k}={validTrades.Count(t => t.Kind == k)})")));
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }


        int trainCutBar = DataSplit.Bounds(btcSeries.Length).TrainEnd;
        int folds       = 5;


        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => RegimeRouterGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            population[0] = seed;
            for (int s = 1; s <= Math.Min(_populationSize / 5, _populationSize - 1); s++)
                population[s] = seed.Mutate(_rng, 0.25);
        }

        List<RegimeRouterGenotype> eliteIsland = [];
        double bestFitness  = double.MinValue;
        int    stagnantGens = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            // Mutation rate anneals 0.65 → 0.05.
            double mutationRate = 0.6 * (1.0 - (double)gen / _generations) + 0.05;

            // WARNING: parallel body must stay RNG-free (System.Random is not thread-safe).
            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, btcSeries, ethSeries, btcTimeIndex,
                                      validTrades, useValidation: false,
                                      trainCutBar, folds));

            population  = [.. population.OrderByDescending(g => g.Fitness)];
            eliteIsland = [.. population.Take(_eliteCount)];

            double topFitness = eliteIsland[0].Fitness;
            if (topFitness > bestFitness + 1e-6) { bestFitness = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens > 0 ? $" [stagnant×{stagnantGens}]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland[0]}{tag}");
            }

            if (GaSearch.ShouldCataclysm(stagnantGens, _cataclysmStagnantGens))
            {
                // CHC restart: keep top eliteCarryOver, redraw rest from full bounds.
                population = GaSearch.Cataclysm(population, _populationSize, _eliteCarryOver,
                                                () => RegimeRouterGenotype.Random(_rng), ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"Gen {gen + 1,3} — CATACLYSM: kept top {Math.Min(_eliteCarryOver, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCarryOver, _populationSize)} at random");
            }
            else
            {
                var nextGen = new List<RegimeRouterGenotype>();
                nextGen.AddRange(eliteIsland.Take(_eliteCarryOver));
                while (nextGen.Count < _populationSize)
                {
                    var child = RegimeRouterGenotype.Crossover(
                                    TournamentSelect(population),
                                    TournamentSelect(population), _rng)
                                .Mutate(_rng, mutationRate);
                    nextGen.Add(child);
                }
                population = nextGen;
            }
        }


        if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
        var boSeed = eliteIsland.Select(g => (g.ToVector(), g.Fitness)).ToList();
        var boHistory = BayesianOptimizer.Refine(
            seedObs:    boSeed,
            bounds:     RegimeRouterGenotype.Bounds,
            evaluate:   v =>
            {
                var g = RegimeRouterGenotype.FromVector(v);
                g.Fitness = Fitness(g, btcSeries, ethSeries, btcTimeIndex,
                                    validTrades, useValidation: false, trainCutBar, folds);
                return g.Fitness;
            },
            iterations: 60,
            rng:        _rng);

        var boChamp = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno  = RegimeRouterGenotype.FromVector(boChamp.Params);
        boGeno.Fitness = Fitness(boGeno, btcSeries, ethSeries, btcTimeIndex,
                                 validTrades, useValidation: false, trainCutBar, folds);
        if (boGeno.Fitness > eliteIsland[^1].Fitness)
        {
            eliteIsland[^1] = boGeno;
            eliteIsland = [.. eliteIsland.OrderByDescending(g => g.Fitness)];
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }


        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");


        var best = eliteIsland[0];
        double trainFit = best.Fitness;


        best.Fitness = Fitness(best, btcSeries, ethSeries, btcTimeIndex,
                              validTrades, useValidation: true, trainCutBar, folds);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    private double Fitness(
        RegimeRouterGenotype           router,
        RegimeBar[]                    btcSeries,
        RegimeBar[]?                   ethSeries,
        Dictionary<long, int>      btcTimeIndex,
        IReadOnlyList<TradeRecord>     trades,
        bool                           useValidation,
        int                            trainCutBar,
        int                            folds)
    {

        var windowTrades = trades
            .Where(t =>
            {
                int bar = LookupBar(btcTimeIndex, t.Time, btcSeries.Length);
                return useValidation ? bar >= trainCutBar : bar < trainCutBar;
            })
            .ToList();

        if (windowTrades.Count < MinTrades) return -1.0;

        if (useValidation)
        {
            var active = FilterActive(router, windowTrades, btcSeries, ethSeries, btcTimeIndex);
            return ScorePortfolio(active);
        }

        // Time-sequential CV with embargo, aggregated by AggregateFoldScores.
        var sorted    = windowTrades.OrderBy(t => t.Time).ToList();
        int foldSize  = sorted.Count / folds;
        if (foldSize < MinTrades) return ScorePortfolio(FilterActive(router, sorted, btcSeries, ethSeries, btcTimeIndex));

        int embargo = Math.Max(0, (int)(foldSize * new FitnessConfig().EmbargoPct));
        var foldScores = new List<double>(folds);
        var foldCounts = new List<int>(folds);
        for (int f = 0; f < folds; f++)
        {
            int start = f * foldSize + (f > 0 ? embargo : 0);
            int end   = f == folds - 1 ? sorted.Count : (f + 1) * foldSize;
            if (end - start < MinTrades) continue;
            var fold  = sorted[start..end];
            var active = FilterActive(router, fold, btcSeries, ethSeries, btcTimeIndex);
            foldScores.Add(ScorePortfolio(active));
            foldCounts.Add(active.Count);
        }
        if (foldScores.Count == 0) return FoldScoreHelper.DeadFoldFitness;

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, RouterGeneCount, folds);
    }

    // Keep only trades where the router would have activated that strategy at trade time.
    private static List<TradeRecord> FilterActive(
        RegimeRouterGenotype       router,
        IEnumerable<TradeRecord>   trades,
        RegimeBar[]                btcSeries,
        RegimeBar[]?               ethSeries,
        Dictionary<long, int>  btcTimeIndex)
    {
        var result = new List<TradeRecord>();
        foreach (var t in trades)
        {
            int bar = LookupBar(btcTimeIndex, t.Time, btcSeries.Length);
            var btc = btcSeries[bar];

            double blendedConf = BlendConf(router, btc, ethSeries, bar);

            bool inBullTransition = btc.Regime == MarketRegime.Bull
                                    && btc.Duration < (int)router.BullMinBars;
            bool inTransition     = inBullTransition
                                 || (btc.Regime == MarketRegime.Bear
                                     && btc.Duration < (int)router.BearMinBars);


            MarketRegime prevRegime = MarketRegime.Ranging;
            if (inBullTransition)
            {
                int prevBar = Math.Max(0, bar - btc.Duration);
                prevRegime  = btcSeries[prevBar].Regime;
            }

            bool earlyFromBear    = inBullTransition && prevRegime == MarketRegime.Bear
                                    && blendedConf >= router.BullMinConf && router.EarlyBullFromBearMult > 0;
            bool earlyFromRanging = inBullTransition && prevRegime == MarketRegime.Ranging
                                    && blendedConf >= router.BullMinConf && router.EarlyBullFromRangingMult > 0;
            bool bearCarry        = inBullTransition && prevRegime == MarketRegime.Bear
                                    && router.EarlyBullBearCarry > 0;

            // Delegate to ComputeActivation (single source of truth shared with live Route + session).
            var activation = RegimeRouter.ComputeActivation(btc.Regime, blendedConf, btc.Duration, prevRegime, router);
            bool active = t.Kind switch
            {
                StrategyKind.FadeShort         => activation.FadeShort,
                StrategyKind.Grid              => activation.Grid,
                StrategyKind.GridShort         => activation.GridShort,
                StrategyKind.DipLong           => activation.DipLong,
                StrategyKind.FadeLong          => activation.FadeLong,
                StrategyKind.RipShort          => activation.RipShort,
                StrategyKind.SwingLong         => activation.SwingLong,
                StrategyKind.AccumulationGrid  => activation.AccumulationGrid,
                _                              => false,
            };

            if (!active) continue;

            double frac = t.Frac;
            if ((t.Kind == StrategyKind.Grid || t.Kind == StrategyKind.GridShort) && inTransition && router.TransitionSizeMult > 0)
                frac *= router.TransitionSizeMult;
            else if (t.Kind == StrategyKind.DipLong && earlyFromBear)
                frac *= router.EarlyBullFromBearMult;
            else if (t.Kind == StrategyKind.DipLong && earlyFromRanging)
                frac *= router.EarlyBullFromRangingMult;
            else if (t.Kind == StrategyKind.FadeLong && bearCarry)
                frac *= router.EarlyBullBearCarry;
            // SwingLong is the one strategy whose per-trade confidence predicts returns
            // (measured IC Spearman +0.154, p<0.001 on OOS — positive; the other four ≈ 0).
            // Size it with the router's blended confidence so the fitness pays it to take
            // high-conviction SwingLong entries at full size and low-conviction ones smaller,
            // rather than flat-sizing every entry regardless of conviction.
            else if (t.Kind == StrategyKind.SwingLong)
                frac *= GradedScale(blendedConf);
            result.Add(t with { Frac = frac });
        }
        return result;
    }

    private static double BlendConf(RegimeRouterGenotype router, RegimeBar btc, RegimeBar[]? ethSeries, int bar)
    {
        if (ethSeries == null || router.EthBlendWeight < 1e-6) return btc.Confidence;

        int ethBar = Math.Clamp(bar, 0, ethSeries.Length - 1);
        var eth    = ethSeries[ethBar];
        double w   = router.EthBlendWeight;

        return eth.Regime == btc.Regime
            ? (1 - w) * btc.Confidence + w * eth.Confidence          // agreement → boost
            : Math.Max(0, (1 - w) * btc.Confidence - w * eth.Confidence); // disagreement → dampen
    }


    private static double ScorePortfolio(List<TradeRecord> trades)
    {
        if (trades.Count < MinTrades) return -1.0;

        var returns = trades.Select(t => t.Return).ToList();
        var portSeq = trades.Select(t => (t.Return, t.Frac)).ToList();

        double pf = Simulator.ProfitFactor(returns);
        if (pf < 1.0) return pf - 2.0;  // quick reject before full sim

        var port   = Simulator.SimulatePortfolio(portSeq);
        double gain = (port.EndBalance - port.StartBalance) / port.StartBalance;
        if (gain <= 0) return gain - 0.5;

        double calmar = Simulator.CalmarRatio(returns);

        // Penalise low coverage — prevents gating everything out.
        double coverageRatio = (double)trades.Count / Math.Max(trades.Count, 50);

        return calmar * coverageRatio;
    }

    // Confidence → size multiplier, matching the live session's graded sizing
    // (ramps from GradedSizeFloor to 1.0 across [GradedConfStart, GradedConfFull]).
    private static double GradedScale(double conf)
    {
        double t = (conf - Config.GradedConfStart) / (Config.GradedConfFull - Config.GradedConfStart);
        return Config.GradedSizeFloor + (1.0 - Config.GradedSizeFloor) * Math.Clamp(t, 0.0, 1.0);
    }

    // Hour-level ticks as dictionary key — avoids DateTimeKind mismatches.
    private static long HourKey(DateTime t) => t.Ticks / TimeSpan.TicksPerHour;

    private static Dictionary<long, int> BuildTimeIndex(RegimeBar[] series)
    {
        var idx = new Dictionary<long, int>(series.Length);
        for (int i = 0; i < series.Length; i++)
            idx.TryAdd(HourKey(series[i].Time), i);
        return idx;
    }

    private static int LookupBar(Dictionary<long, int> idx, DateTime t, int maxBar)
    {
        long key = HourKey(t);
        if (idx.TryGetValue(key, out int bar)) return bar;
        for (int delta = 1; delta <= 4; delta++)
        {
            if (idx.TryGetValue(key - delta, out bar)) return bar;
        }
        return Math.Clamp(maxBar - 1, 0, maxBar - 1);
    }


    internal RegimeRouterGenotype TournamentSelect(List<RegimeRouterGenotype> pop) =>
        GaSearch.Tournament(pop, _tournamentK, _rng, g => g.Fitness);
}
