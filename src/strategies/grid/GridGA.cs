namespace TradingGA;

// Genetic algorithm for grid trading (ranging-market long grid, 1h candles).
// Structure mirrors FadeShortGA.
//
// FoldScore operates on per-SESSION returns (mean of all level fills per activation),
// not per-fill returns. This prevents inflating WR and trade count when GridLevels > 1.
//
// FoldScore weights:
//   portfolio gain (compounded 3% per session) × win-rate multiplier
//   divided by drawdown penalty (2× stronger than swing to penalise stop-loss tails)
//   No frequency bonus — trade count is driven by coin volatility, not strategy quality.
// Fitness = mean(fold_scores) − stdMult × std(fold_scores) over the SURVIVING walk-forward
// folds (those that reached MinTradesPerFold trades); stdMult is VC-proportional.
// Folds are cut PER COIN on that coin's own array — see Fitness().
public class GridGeneticAlgorithm
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

    private readonly int           _populationSize;
    private readonly int           _generations;
    private readonly int           _eliteCount;
    private readonly int           _migrationInterval;
    private readonly bool          _verbose;
    private readonly Random        _rng = new();
    private readonly FitnessConfig _cfg;

    private const int    MinTradesPerFold = 10;
    private const double FitPosFrac       = 0.03;
    private const int    D                = 10;   // genotype parameter count (excl. Fitness) — see GridGenotype.Bounds

    public GridGeneticAlgorithm(
        int            populationSize    = 60,
        int            generations       = 100,
        int            eliteCount        = 15,
        int            migrationInterval = 10,
        bool           verbose           = true,
        FitnessConfig? cfg               = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _cfg               = cfg ?? new FitnessConfig();
    }

    private static double FoldScore(List<double> returns, FitnessConfig cfg, double volWeight = 1.0)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        double wr       = (double)returns.Count(r => r > 0) / returns.Count;
        double grossWin = returns.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss= Math.Abs(returns.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf       = grossLoss > 1e-10 ? grossWin / grossLoss : (grossWin > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in returns)
        {
            balance += r / 100.0 * FitPosFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 0.5;
        double ddDiv  = 1.0 + maxDd * 20.0;

        double baseScore = gain * 100.0 * wrMult / ddDiv;
        int    n         = returns.Count;
        double sharpe    = Simulator.SharpeRatio(returns, n);
        double calmar    = Simulator.CalmarRatio(returns);
        double pfStat    = Simulator.ProfitFactor(returns);
        double sortino   = Simulator.SortinoRatio(returns, n);
        return baseScore
            * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe   / 3.0,  1.0)))
            * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar   / 2.0,  1.0)))
            * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pfStat - 1.0,    1.0)))
            * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino  / 4.0,  1.0)))
            * volWeight;
    }

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
                .SelectMany(x => GridSimulator.GetGridSessionReturns(ind, x.arr.Span).Select(t => t.Return))
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
                .SelectMany(x => GridSimulator.GetGridSessionReturns(ind, x.arr.Span).Select(t => t.Return))
                .ToList();
            return FoldScore(all, _cfg);
        }

        var foldScores = new List<double>(k);
        var foldCounts = new List<int>(k);
        for (int f = 0; f < k; f++)
        {
            var foldReturns = new List<double>();
            foreach (var (coin, arr) in validCoins)
            {
                var (start, end) = FoldScoreHelper.PerCoinFoldRange(arr.Length, k, f, _cfg.EmbargoPct);
                if (end - start < 40) continue;
                foldReturns.AddRange(
                    GridSimulator.GetGridSessionReturns(ind, arr.Slice(start, end - start).Span).Select(t => t.Return));
            }

            // Only folds that actually reached MinTradesPerFold sessions take part in the
            // aggregation. A thin fold returns the constant -1.0 sentinel from FoldScore,
            // and mixing constants into mean − stdMult×std inverts the gradient (see
            // FoldScoreHelper.AggregateFoldScores).
            if (foldReturns.Count < MinTradesPerFold) continue;

            foldScores.Add(FoldScore(foldReturns, _cfg));
            foldCounts.Add(foldReturns.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D);
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
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitness + 1e-6) { bestFitness = topFitness; stagnantGens = 0; }
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

            var nextGen = new List<GridGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
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

    private GridGenotype TournamentSelect(List<GridGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
