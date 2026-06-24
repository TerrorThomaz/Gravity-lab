namespace TradingGA;

// Genetic algorithm for the fade-long strategy.
// Bear-regime oversold bounce: fires only in detected bear markets (EMA slope
// structure), so the GA only needs to generalise over bear periods.
//
// FoldScore filters trades to those where the bear regime has been confirmed
// for at least RegimeSustainedBars consecutive h1 bars at entry. Regime is
// measured by EMA conditions (close < EMA AND RegimeEma declining) — not ADX,
// which is entry-level. This prevents noise trades polluting the score.
//
// Protection mode (profit protect) is handled by DynamicGuardGenotype — moved there
// so the guard trains on all strategies combined (better N/d ratio).
//
// Fitness = mean(fold_scores) − stdMult × std(fold_scores)  (stdMult VC-proportional)
public class FadeLongGA
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

    private const int MinTradesPerFold = 25;   // VC theory requires N > d per fold; d=15 after protection mode moved to guard
    private const int D                = 15;   // genotype parameter count (excl. Fitness)

    public FadeLongGA(
        int  populationSize    = 50,
        int  generations       = 80,
        int  eliteCount        = 15,
        int  migrationInterval = 10,
        bool verbose           = true,
        Func<DateTime, double>? tradeGate = null,
        FitnessConfig? cfg = null)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
        _tradeGate         = tradeGate;
        _cfg               = cfg ?? new FitnessConfig();
    }

    private static double FoldScore(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int    sustainedBars,
        FitnessConfig cfg,
        double volWeight = 1.0)
    {
        // Only score trades that fired during a confirmed bear regime
        var valid = returns.Where(r => r.RegimeBars >= sustainedBars).Select(r => r.Return).ToList();
        if (valid.Count < MinTradesPerFold) return -1.0;

        double wr        = (double)valid.Count(r => r > 0) / valid.Count;
        double grossWins = valid.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss = Math.Abs(valid.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf        = grossLoss > 1e-10 ? grossWins / grossLoss : (grossWins > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in valid)
        {
            balance += r / 100.0 * posFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0;

        double pfMult = pf < 1.5 ? (pf - 1.0) / 0.5 : 1.0 + (pf - 1.5) * 0.5;
        var winList   = valid.Where(r => r > 0).ToList();
        var lossList  = valid.Where(r => r <= 0).ToList();
        double avgWin  = winList.Count  > 0 ? winList.Average()            : 0;
        double avgLoss = lossList.Count > 0 ? Math.Abs(lossList.Average()) : avgWin;
        double rr      = avgLoss > 1e-10 ? avgWin / avgLoss : (avgWin > 0 ? 5.0 : 1.0);
        double rrMult  = rr < 2.5 ? (rr - 1.0) / 1.5 : 1.0 + (rr - 2.5) * 0.3;
        double qualityMult = Math.Sqrt(pfMult * rrMult);

        double ddDiv     = 1.0 + maxDd * 10.0;
        double freqBonus = 1.0 + 0.15 * Math.Log(Math.Max(1.0, valid.Count / (double)MinTradesPerFold));

        // Kept-profits multiplier: penalise giving back peak gains before period end.
        double peakGain      = peak - 1.0;
        double retentionMult = peakGain > 0.01
            ? Math.Max(0.2, (balance - 1.0) / peakGain)
            : 1.0;

        int    n       = valid.Count;
        double sharpe  = Simulator.SharpeRatio(valid, n);
        double calmar  = Simulator.CalmarRatio(valid);
        double pfStat  = Simulator.ProfitFactor(valid);
        double sortino = Simulator.SortinoRatio(valid, n);
        double base_   = gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
        return base_
            * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  3.0)))
            * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  3.0)))
            * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pfStat - 1.0,   3.0)))
            * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  3.0)))
            * volWeight;
    }

    private double Fitness(FadeLongGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
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
                .SelectMany(x => FadeLongSimulator.GetFadeLongReturns(ind, x.h1.Span, x.m15.Span)
                    .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, w); })
                    .Where(t => t.w >= 0.05)
                    .Select(t => (t.Return, t.RegimeBars)))
                .ToList();
            return FoldScore(all, posFrac, ind.RegimeSustainedBars, _cfg, volWeight);
        }

        // Per-coin percentage folds: each coin slices its own history into k equal parts.
        // Avoids the minLen bottleneck where one short-history coin would cap all other
        // coins at that length, making every fold too narrow for MinTradesPerFold regime
        // trades.
        int k = folds;

        double[] scores = new double[k];
        int      totalFoldTrades = 0;
        for (int f = 0; f < k; f++)
        {
            var foldRet = new List<(double Return, int RegimeBars)>();
            foreach (var (coin, h1, m15) in validCoins)
            {
                int startH1  = h1.Length  * f     / k;
                int endH1    = h1.Length  * (f+1) / k;
                int startM15 = startH1 * 4;
                int endM15   = Math.Min(endH1 * 4, m15.Length);
                if (endH1 - startH1 < 40) continue;
                foldRet.AddRange(
                    FadeLongSimulator.GetFadeLongReturns(ind, h1.Slice(startH1, endH1 - startH1).Span, m15.Slice(startM15, endM15 - startM15).Span)
                                     .Select(t => { double w = _tradeGate?.Invoke(t.Time) ?? 1.0; return (Return: t.Return * w, RegimeBars: t.RegimeBarsActive, w); })
                                     .Where(t => t.w >= 0.05)
                                     .Select(t => (t.Return, t.RegimeBars)));
            }
            totalFoldTrades += foldRet.Count;
            scores[f] = FoldScore(foldRet, posFrac, ind.RegimeSustainedBars, _cfg, volWeight);
        }

        double mean    = scores.Average();
        double std     = Math.Sqrt(scores.Select(s => (s - mean) * (s - mean)).Average());
        // VC-proportional penalty: stdMult = 0.75 when avgN/d ≥ 10, scales up to 2.0 as
        // N/d falls — prevents the GA from over-trusting fold scores when sample is thin.
        double avgN    = (double)totalFoldTrades / k;
        double stdMult = Math.Clamp(7.5 / Math.Max(1.0, avgN / D), 0.75, 2.0);
        return mean - stdMult * std;
    }

    public FadeLongGenotype Run(IReadOnlyList<CoinData> coins, FadeLongGenotype? seed = null)
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
            .Select(_ => FadeLongGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<FadeLongGenotype> eliteIsland    = new();
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
                    strategy    = "FadeLong",
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

            var nextGen = new List<FadeLongGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var child = FadeLongGenotype.Crossover(
                                TournamentSelect(population),
                                TournamentSelect(population), _rng)
                            .Mutate(_rng, mutationRate);
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
            bounds:     FadeLongGenotype.Bounds,
            evaluate:   v => { var g = FadeLongGenotype.FromVector(v); g.Fitness = Fitness(g, coins, useValidation: false); return g.Fitness; },
            iterations: 30,
            rng:        _rng);

        var boChampion = boHistory.OrderByDescending(h => h.Fitness).First();
        var boGeno     = FadeLongGenotype.FromVector(boChampion.Params);
        boGeno.Fitness = Fitness(boGeno, coins, useValidation: false);
        if (boGeno.Fitness > eliteIsland.Last().Fitness)
        {
            eliteIsland[eliteIsland.Count - 1] = boGeno;
            eliteIsland = eliteIsland.OrderByDescending(g => g.Fitness).ToList();
            if (_verbose) Console.WriteLine($"  BO improved elite: {boGeno}");
        }

        if (_verbose) Console.WriteLine("\n=== FadeLong held-out validation ===");
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));

        var best = eliteIsland.OrderByDescending(g => g.Fitness).First();
        if (_verbose) Console.WriteLine($"Best: {best}");
        return best;
    }

    private FadeLongGenotype TournamentSelect(List<FadeLongGenotype> pop, int k = 4) =>
        Enumerable.Range(0, k)
            .Select(_ => pop[_rng.Next(pop.Count)])
            .OrderByDescending(g => g.Fitness)
            .First();
}
