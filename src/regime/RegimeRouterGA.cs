namespace TradingGA;

// Genetic algorithm that trains the RegimeRouter thresholds.
//
// Input:  pre-computed strategy trade lists + BTC/ETH regime series
// Output: RegimeRouterGenotype — the routing thresholds that maximise
//         combined portfolio Calmar when strategies are gated by BTC regime state
//
// Fitness (5-fold time-sequential CV):
//   For each router candidate, filter trades to only those where the router
//   would have activated that strategy at that timestamp.
//   Score the combined portfolio with Simulator.CalmarRatio.
//   Final = mean(fold_scores) − 0.75 × std(fold_scores)
//
// Only strategies with positive saved fitness should be passed in.
// DipLong with F<0 should be excluded from the trade list — the router
// cannot learn useful DipLong thresholds from a genotype that has no edge.
public class RegimeRouterGA
{
    // RipShort appended at the end so existing serialized StrategyKind values don't shift.
    public enum StrategyKind { FadeShort, Grid, DipLong, FadeLong, RipShort }

    public record TradeRecord(StrategyKind Kind, DateTime Time, double Return, double Frac);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    private const int MinTrades = 30;   // min combined active trades to score a fold

    // Recency-weighted fold scoring: later folds (more recent market regime) count more.
    // Weights are applied in time order (index 0 = oldest fold, last = most recent).
    private static readonly double[] FoldWeights = [1.0, 1.0, 1.0, 1.5, 2.0];

    public RegimeRouterGA(
        int  populationSize    = 50,
        int  generations       = 150,
        int  eliteCount        = 10,
        int  migrationInterval = 10,
        bool verbose           = true)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public RegimeRouterGenotype Run(
        RegimeBar[]              btcSeries,
        RegimeBar[]?             ethSeries,
        IReadOnlyList<TradeRecord> trades,
        RegimeRouterGenotype?    seed = null)
    {
        if (btcSeries.Length < 500)
            throw new ArgumentException("Need at least 500 BTC h1 bars for router training.");

        // Build timestamp → BTC bar index lookup (hour-rounded)
        var btcTimeIndex = BuildTimeIndex(btcSeries);

        // Trim trades to bars that are inside BTC series range
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

        // Train / val split: last 20% of BTC series is held-out validation
        int trainCutBar = (int)(btcSeries.Length * 0.80);
        int folds       = 5;

        // Initialise population
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
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.90) : baseMutRate;

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
                string tag = stagnantGens >= 15 ? $" [STAGNANT×{stagnantGens}]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland[0]}{tag}");
            }

            var nextGen = new List<RegimeRouterGenotype>();
            nextGen.AddRange(eliteIsland.Take(3));
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

        // ── Bayesian refinement ───────────────────────────────────────────────
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
            iterations: 30,
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

        // ── Val scoring ───────────────────────────────────────────────────────
        if (_verbose) Console.WriteLine("\n=== Held-out validation (report-only, not used for selection) ===");

        // Select winner by train fitness (best taken from elite sorted by train fitness)
        var best = eliteIsland[0];
        double trainFit = best.Fitness;

        // Compute validation score for the winner only
        best.Fitness = Fitness(best, btcSeries, ethSeries, btcTimeIndex,
                              validTrades, useValidation: true, trainCutBar, folds);

        if (_verbose) Console.WriteLine($"Best (selected on train): train={trainFit:F3}  val={best.Fitness:F3}");
        if (_verbose) Console.WriteLine($"  {best}");
        return best;
    }

    // ── Fitness ───────────────────────────────────────────────────────────────

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
        // Determine which trades fall in the right split window
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
            // Single pass on val window
            var active = FilterActive(router, windowTrades, btcSeries, ethSeries, btcTimeIndex);
            return ScorePortfolio(active);
        }

        // 5-fold time-sequential CV on training window
        // Sort by time, then split into folds
        var sorted    = windowTrades.OrderBy(t => t.Time).ToList();
        int foldSize  = sorted.Count / folds;
        if (foldSize < MinTrades) return ScorePortfolio(FilterActive(router, sorted, btcSeries, ethSeries, btcTimeIndex));

        double[] scores = new double[folds];
        for (int f = 0; f < folds; f++)
        {
            int start = f * foldSize;
            int end   = f == folds - 1 ? sorted.Count : start + foldSize;
            var fold  = sorted[start..end];
            var active = FilterActive(router, fold, btcSeries, ethSeries, btcTimeIndex);
            scores[f] = ScorePortfolio(active);
        }

        var    weights      = FoldWeights.Take(folds).ToArray();
        double totalW       = weights.Sum();
        double weightedMean = scores.Select((s, i) => s * weights[i]).Sum() / totalW;
        double weightedVar  = scores.Select((s, i) => weights[i] * Math.Pow(s - weightedMean, 2)).Sum() / totalW;
        double weightedStd  = Math.Sqrt(weightedVar);
        return weightedMean - 0.75 * weightedStd;
    }

    // Keep only trades where the router would have activated that strategy at trade time.
    // Early-bull window (duration < BullMinBars):
    //   - Grid: forced ON at TransitionSizeMult
    //   - DipLong/SwingLong: EarlyBullFromBearMult if from Bear, EarlyBullFromRangingMult if from Ranging
    //   - FadeLong: EarlyBullBearCarry fraction if source was Bear (bearish carryover)
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

            // Previous regime: look back to the bar before this regime run started
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

            bool active = t.Kind switch
            {
                StrategyKind.FadeShort => !(btc.Regime == MarketRegime.Bull
                                            && btc.Duration >= (int)router.BullMinBars
                                            && blendedConf >= router.BullMinConf),
                StrategyKind.Grid      => btc.Regime == MarketRegime.Ranging
                                          || blendedConf < router.GridMaxConf
                                          || (inTransition && router.TransitionSizeMult > 0),
                StrategyKind.DipLong   => (btc.Regime == MarketRegime.Bull
                                           && btc.Duration >= (int)router.BullMinBars
                                           && blendedConf >= router.BullMinConf)
                                          || earlyFromBear || earlyFromRanging,
                StrategyKind.FadeLong  => (btc.Regime == MarketRegime.Bear
                                           && btc.Duration >= (int)router.BearMinBars
                                           && blendedConf >= router.BearMinConf)
                                          || bearCarry,
                _                      => false,
            };

            if (!active) continue;

            double frac = t.Frac;
            if (t.Kind == StrategyKind.Grid && inTransition && router.TransitionSizeMult > 0)
                frac *= router.TransitionSizeMult;
            else if (t.Kind == StrategyKind.DipLong && earlyFromBear)
                frac *= router.EarlyBullFromBearMult;
            else if (t.Kind == StrategyKind.DipLong && earlyFromRanging)
                frac *= router.EarlyBullFromRangingMult;
            else if (t.Kind == StrategyKind.FadeLong && bearCarry)
                frac *= router.EarlyBullBearCarry;
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

    // Score a filtered trade list as a combined portfolio.
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

        // Penalise solutions that leave many strategy trades unused (poor coverage)
        // — prevents the router from simply gating everything out
        double coverageRatio = (double)trades.Count / Math.Max(trades.Count, 50);

        return calmar * coverageRatio;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Use long (hour-level ticks) as dictionary key so DateTimeKind differences
    // between candle sources (Utc vs Unspecified) never cause lookup failures.
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
        // Only search backwards — never return a future bar whose candle has not yet closed
        for (int delta = 1; delta <= 4; delta++)
        {
            if (idx.TryGetValue(key - delta, out bar)) return bar;
        }
        return Math.Clamp(maxBar - 1, 0, maxBar - 1);
    }

    private RegimeRouterGenotype TournamentSelect(List<RegimeRouterGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
