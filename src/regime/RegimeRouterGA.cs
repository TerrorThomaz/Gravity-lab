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
    private const int RouterGeneCountLegacy = 12;
    private const int RouterGeneCountHmm = 63;

    // Matched-fitness A/B switches. GRAVITY_ROUTER_NOBO=1 disables the legacy TPE refinement;
    // GRAVITY_ROUTER_NOREG=1 disables the HMM diversification+retention regularizers. Setting both
    // makes the legacy and HMM routers optimize the IDENTICAL objective (calmar×coverage over the
    // same 5-fold CV, no extra refinement), so the only remaining variable is the gating mechanism
    // itself (thresholds vs favorability). Read live (not static-readonly) so tests can toggle them.
    private static bool NoBO  => Environment.GetEnvironmentVariable("GRAVITY_ROUTER_NOBO") == "1";
    private static bool NoReg => Environment.GetEnvironmentVariable("GRAVITY_ROUTER_NOREG") == "1";
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

        bool hmmMode = RegimeRouter.HmmEnabled && btcSeries.Any(b => b.HmmProbs != null);
        int geneCount = hmmMode ? RouterGeneCountHmm : RouterGeneCountLegacy;

        // In hmmMode a legacy (threshold-only) seed carries no favorability genes, so the
        // population would have nothing to evolve. Upgrade it once, up front.
        if (hmmMode && seed is { IsHmmGenotype: false })
            seed = seed.WithHmmInit(_rng);

        // Freeze threshold genes in hmmMode: routing there reads only favorability, so threshold
        // genes never affect fitness and (left to mutate) drift to random values, silently
        // corrupting the GRAVITY_HMM=0 fallback. Template = the seed's trained thresholds.
        RegimeRouterGenotype? thresholdTemplate = null;
        if (hmmMode && seed != null)
        {
            thresholdTemplate = new RegimeRouterGenotype();
            thresholdTemplate.CopyThresholdsFrom(seed);
        }

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
            if (hmmMode) Console.WriteLine("  HMM mode: ON — favorability matrix active, BO skipped");
            if (seed != null) Console.WriteLine($"  Seeding from: {seed}");
        }


        int trainCutBar = DataSplit.Bounds(btcSeries.Length).TrainEnd;
        int folds       = 5;


        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => RegimeRouterGenotype.Random(_rng, seed, hmmMode))
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

            // hmmMode: re-freeze threshold genes after init/crossover/mutation/cataclysm so they
            // never drift (they carry no selection signal in this mode).
            if (thresholdTemplate != null)
                foreach (var ind in population) ind.CopyThresholdsFrom(thresholdTemplate);

            // WARNING: parallel body must stay RNG-free (System.Random is not thread-safe).
            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, btcSeries, ethSeries, btcTimeIndex,
                                      validTrades, useValidation: false,
                                      trainCutBar, folds, hmmMode, geneCount));

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
                                                () => RegimeRouterGenotype.Random(_rng, seed, hmmMode), ref stagnantGens);
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


        if (!hmmMode && !NoBO)
        {
            if (_verbose) Console.WriteLine("\n  BO refinement (30 iterations, TPE)...");
            var boSeed = eliteIsland.Select(g => (g.ToVector(), g.Fitness)).ToList();
            var boHistory = BayesianOptimizer.Refine(
                seedObs:    boSeed,
                bounds:     RegimeRouterGenotype.Bounds,
                evaluate:   v =>
                {
                    var g = RegimeRouterGenotype.FromVector(v);
                    g.Fitness = Fitness(g, btcSeries, ethSeries, btcTimeIndex,
                                        validTrades, useValidation: false, trainCutBar, folds, hmmMode, geneCount);
                    return g.Fitness;
                },
                iterations: 60,
                rng:        _rng);

            var boChamp = boHistory.OrderByDescending(h => h.Fitness).First();
            var boGeno  = RegimeRouterGenotype.FromVector(boChamp.Params);
            boGeno.Fitness = Fitness(boGeno, btcSeries, ethSeries, btcTimeIndex,
                                     validTrades, useValidation: false, trainCutBar, folds, hmmMode, geneCount);
            if (boGeno.Fitness > eliteIsland[^1].Fitness)
            {
                eliteIsland[^1] = boGeno;
                eliteIsland = [.. eliteIsland.OrderByDescending(g => g.Fitness)];
                if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
            }
        }
        else if (_verbose)
        {
            string why = hmmMode ? "HMM mode: 48+ dims too high for TPE" : "GRAVITY_ROUTER_NOBO=1";
            Console.WriteLine($"\n  BO refinement skipped ({why})");
        }


        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");


        var best = eliteIsland[0];
        double trainFit = best.Fitness;


        best.Fitness = Fitness(best, btcSeries, ethSeries, btcTimeIndex,
                              validTrades, useValidation: true, trainCutBar, folds, hmmMode, geneCount);

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
        int                            folds,
        bool                           hmmMode,
        int                            geneCount)
    {
        GaTrialCounter.Shared.Record("regime_router");

        var windowTrades = trades
            .Where(t =>
            {
                int bar = LookupBar(btcTimeIndex, t.Time, btcSeries.Length);
                return useValidation ? bar >= trainCutBar : bar < trainCutBar;
            })
            .ToList();

        if (windowTrades.Count < MinTrades) return -1.0;

        int poolStrategies = hmmMode ? CountPoolStrategies(windowTrades) : 0;

        if (useValidation)
        {
            var active = FilterActive(router, windowTrades, btcSeries, ethSeries, btcTimeIndex, hmmMode);
            double score = ScorePortfolio(active, hmmMode, btcSeries, poolStrategies);
            if (hmmMode && !NoReg) score *= RetentionFactor(windowTrades, active);
            return score;
        }

        // Time-sequential CV with embargo, aggregated by AggregateFoldScores.
        var sorted    = windowTrades.OrderBy(t => t.Time).ToList();
        // Anti-extinction: one full-window filter pass measures whether the router drops whole
        // strategies globally (per-fold gating stays free — only global extinction is penalised).
        double retention = (hmmMode && !NoReg)
            ? RetentionFactor(sorted, FilterActive(router, sorted, btcSeries, ethSeries, btcTimeIndex, hmmMode))
            : 1.0;

        int foldSize  = sorted.Count / folds;
        if (foldSize < MinTrades) return ScorePortfolio(FilterActive(router, sorted, btcSeries, ethSeries, btcTimeIndex, hmmMode), hmmMode, btcSeries, poolStrategies) * retention;

        int embargo = Math.Max(0, (int)(foldSize * new FitnessConfig().EmbargoPct));
        var foldScores = new List<double>(folds);
        var foldCounts = new List<int>(folds);
        for (int f = 0; f < folds; f++)
        {
            int start = f * foldSize + (f > 0 ? embargo : 0);
            int end   = f == folds - 1 ? sorted.Count : (f + 1) * foldSize;
            if (end - start < MinTrades) continue;
            var fold  = sorted[start..end];
            var active = FilterActive(router, fold, btcSeries, ethSeries, btcTimeIndex, hmmMode);
            foldScores.Add(ScorePortfolio(active, hmmMode, btcSeries, poolStrategies));
            foldCounts.Add(active.Count);
        }
        if (foldScores.Count == 0) return FoldScoreHelper.DeadFoldFitness;

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, geneCount, folds) * retention;
    }

    private static List<TradeRecord> FilterActive(
        RegimeRouterGenotype       router,
        IEnumerable<TradeRecord>   trades,
        RegimeBar[]                btcSeries,
        RegimeBar[]?               ethSeries,
        Dictionary<long, int>  btcTimeIndex,
        bool                       hmmMode)
    {
        var result = new List<TradeRecord>();
        foreach (var t in trades)
        {
            int bar = LookupBar(btcTimeIndex, t.Time, btcSeries.Length);
            var btc = btcSeries[bar];

            if (hmmMode && router.IsHmmGenotype && btc.HmmProbs != null)
            {
                double hmmBlendedConf = BlendConf(router, btc, ethSeries, bar);
                bool hmmInBullTransition = btc.Regime == MarketRegime.Bull
                                        && btc.Duration < (int)router.BullMinBars;
                bool hmmInTransition     = hmmInBullTransition
                                     || (btc.Regime == MarketRegime.Bear
                                         && btc.Duration < (int)router.BearMinBars);

                MarketRegime hmmPrevRegime = MarketRegime.Ranging;
                if (hmmInBullTransition)
                {
                    int prevBar = Math.Max(0, bar - btc.Duration);
                    hmmPrevRegime  = btcSeries[prevBar].Regime;
                }

                var hmmActivation = RegimeRouter.ComputeActivation(btc.Regime, hmmBlendedConf, btc.Duration, hmmPrevRegime, router);
                bool legacyActive = t.Kind switch
                {
                    StrategyKind.FadeShort         => hmmActivation.FadeShort,
                    StrategyKind.Grid              => hmmActivation.Grid,
                    StrategyKind.GridShort         => hmmActivation.GridShort,
                    StrategyKind.DipLong           => hmmActivation.DipLong,
                    StrategyKind.FadeLong          => hmmActivation.FadeLong,
                    StrategyKind.RipShort          => hmmActivation.RipShort,
                    StrategyKind.SwingLong         => hmmActivation.SwingLong,
                    StrategyKind.AccumulationGrid  => hmmActivation.AccumulationGrid,
                    _                              => false,
                };

                if (!legacyActive) continue;

                var weights = RegimeRouter.ComputeWeights(btc.HmmProbs, router);
                int idx = BaseStrategyIndex(t.Kind);
                if (idx < 0) continue;
                double w = weights[idx];
                // Matches RegimeRouterSession.SizeGate exactly: size IS the conviction weight,
                // already floored at StrategyFloorPct by ComputeWeights. The retired `w<0.5 → 1.0`
                // rule made size peak at the router's LOWEST conviction, so the GA was selected to
                // drive favorability under 0.5 and neutralise its own sizing layer.
                result.Add(t with { Frac = t.Frac * w });
                continue;
            }

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
            else if (t.Kind == StrategyKind.SwingLong)
                frac *= GradedScale(blendedConf);
            result.Add(t with { Frac = frac });
        }
        return result;
    }

    private static int BaseStrategyIndex(StrategyKind kind) => kind switch
    {
        StrategyKind.FadeShort         => 0,
        StrategyKind.Grid              => 1,
        StrategyKind.GridShort         => 2,
        StrategyKind.DipLong           => 3,
        StrategyKind.FadeLong          => 4,
        StrategyKind.RipShort          => 5,
        StrategyKind.SwingLong         => 6,
        StrategyKind.AccumulationGrid  => 7,
        StrategyKind.FadeShortLowVol   => 0,
        StrategyKind.DipLongLowVol     => 3,
        StrategyKind.SwingLongLowVol   => 6,
        StrategyKind.RipShortLowVol    => 5,
        StrategyKind.FadeShortHighVol  => 0,
        StrategyKind.DipLongHighVol    => 3,
        StrategyKind.SwingLongHighVol  => 6,
        StrategyKind.RipShortHighVol   => 5,
        _ => -1,
    };

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


    private static double ScorePortfolio(List<TradeRecord> trades, bool hmmMode, RegimeBar[] btcSeries, int poolStrategies)
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

        double score = calmar * coverageRatio;

        if (hmmMode && !NoReg)
            score *= DiversificationMult(trades, btcSeries, poolStrategies);

        return score;
    }

    private static double DiversificationMult(List<TradeRecord> trades, RegimeBar[] btcSeries, int poolStrategies)
    {
        if (btcSeries.Length < 2) return 1.0;

        DateTime start = btcSeries[0].Time;
        DateTime end   = btcSeries[^1].Time;
        var bucket = TimeSpan.FromHours(24);

        var strategyBuckets = new Dictionary<StrategyKind, double[]>();
        foreach (var t in trades)
        {
            int baseIdx = BaseStrategyIndex(t.Kind);
            if (baseIdx < 0) continue;
            var kind = RegimeRouterGenotype.HmmStrategies[baseIdx];
            if (!strategyBuckets.ContainsKey(kind))
                strategyBuckets[kind] = CovarianceSizing.ToGrid(
                    trades.Where(x => BaseStrategyIndex(x.Kind) == baseIdx)
                          .Select(x => (x.Time, x.Return)).ToList(),
                    start, end, bucket);
        }

        int nStrategies = strategyBuckets.Count;
        if (nStrategies < 2) return 1.0;

        int nBuckets = strategyBuckets.Values.First().Length;
        if (nBuckets < 30) return 1.0;

        var series = strategyBuckets.Values.ToArray();
        var cov = CovarianceMatrix.Sample(series);
        if (cov == null) return 1.0;
        var shrunk = CovarianceMatrix.Shrink(cov, nStrategies, 0.3);
        if (shrunk == null) return 1.0;
        double effBets = CovarianceMatrix.EffectiveBets(shrunk, nStrategies);
        if (effBets < 1.0) return 1.0;

        // Normalise by the strategies AVAILABLE in the pool, not just those that survived the
        // gate — otherwise gating a strategy out shrinks the denominator with the numerator and
        // concentration is free. This is what makes extinguishing a whole strategy costly.
        int denom = Math.Max(nStrategies, poolStrategies);
        return Math.Pow(effBets / denom, 0.5);
    }

    // Anti-extinction pressure. For each strategy with a real presence in the pool (>=
    // RetentionMinPoolTrades), the router must keep at least RetentionFloor of its trades across
    // the whole window; below that, fitness is damped linearly toward a 0.25 floor per strategy.
    // Measured on the full window (not per fold) so regime-conditional gating within the window
    // stays free — only globally dropping a strategy is penalised.
    private const int    RetentionMinPoolTrades = 30;
    private const double RetentionFloor         = 0.10;

    private static double RetentionFactor(List<TradeRecord> pool, List<TradeRecord> active)
    {
        double factor = 1.0;
        for (int idx = 0; idx < RegimeRouterGenotype.HmmStrategyRows; idx++)
        {
            int i = idx;
            int poolN = pool.Count(t => BaseStrategyIndex(t.Kind) == i);
            if (poolN < RetentionMinPoolTrades) continue;
            int activeN = active.Count(t => BaseStrategyIndex(t.Kind) == i);
            double share = (double)activeN / poolN;
            factor *= 0.25 + 0.75 * Math.Min(1.0, share / RetentionFloor);
        }
        return factor;
    }

    private static int CountPoolStrategies(List<TradeRecord> pool)
    {
        int n = 0;
        for (int idx = 0; idx < RegimeRouterGenotype.HmmStrategyRows; idx++)
        {
            int i = idx;
            if (pool.Count(t => BaseStrategyIndex(t.Kind) == i) >= RetentionMinPoolTrades) n++;
        }
        return n;
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
