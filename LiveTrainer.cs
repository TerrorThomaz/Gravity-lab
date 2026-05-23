namespace TradingGA;

/// <summary>
/// Maintains a population of genotypes and evaluates them on a rolling window
/// of live candle data across all coins. Evolves the population every K cycles.
///
/// Overfitting mitigation:
///   - Fitness = min(score_20%, score_50%, score_100%) across window sizes.
///     A genotype must generalise across recent AND longer-term data.
///   - Score per window = mean(0.7×Sharpe + 0.3×Clamp(Calmar,−2,3)) across ALL coins
///     minus 0.6 × cross-coin std. Calmar penalises DCA-recovery drawdown patterns.
///   - Population diversity: top 5 elites preserved, rest replaced with evolved
///     offspring each hour.
/// </summary>
public class LiveTrainer
{
    // ── Config ────────────────────────────────────────────────────────────────
    private const int    MinTradesPerCoin  = 5;    // skip coin if too few trades (fitness)
    private const int    MinHoldOutTrades  = 2;    // skip coin if too few trades (holdout)
    private const int    MinHoldOutCoins   = 4;    // skip holdout gate if fewer coins qualify
    private const int    EliteCount        = 5;    // survivors per evolution step
    private const int    EvolveEvery       = 12;   // cycles between GA ops (~1 h at 5 min/cycle)
    private const int    PromoteStreak     = 5;    // consecutive improvements required for auto-promote
    private const double HoldOutFraction   = 0.20; // most recent fraction of window reserved for out-of-sample gate

    private readonly int    _popSize;
    private readonly Random _rng = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private List<Genotype>                      _population;
    private List<(Genotype Geno, double Score)> _ranked = [];
    private int      _cycleCount        = 0;
    private Genotype? _sessionBest      = null;
    private double    _sessionBestScore = double.MinValue;
    private bool      _evolvedThisCycle = false;
    private int       _promoteStreak    = 0;   // consecutive cycles beating saved fitness
    private bool      _shouldPromote    = false;
    private double    _holdOutScore     = double.MinValue;
    private int       _holdOutCoins     = 0;

    // The saved-file fitness is set by the caller so we know when to auto-promote.
    private double _savedFitness = double.MinValue;

    public int      CycleCount        => _cycleCount;
    public Genotype? SessionBest       => _sessionBest;
    public double    SessionBestScore  => _sessionBestScore;
    public Genotype? CurrentBest       => _ranked.Count > 0 ? _ranked[0].Geno : null;
    public bool      ShouldPromote     => _shouldPromote;
    public double    HoldOutScore      => _holdOutScore;
    public int       HoldOutCoins      => _holdOutCoins;

    public void SetSavedFitness(double f)
    {
        _savedFitness = f;
        _promoteStreak = 0;
        _shouldPromote = false;
    }

    public void AcknowledgePromotion()
    {
        _promoteStreak = 0;
        _shouldPromote = false;
    }

    // ── Construction ──────────────────────────────────────────────────────────
    public LiveTrainer(int popSize = 20, Genotype? seed = null)
    {
        _popSize    = popSize;
        _population = Enumerable.Range(0, popSize)
                                .Select(_ => Genotype.Random(_rng, atrMode: true))
                                .ToList();

        if (seed != null)
        {
            _population[0] = seed;
            int variants   = Math.Min(popSize / 4, popSize - 1);
            for (int i = 1; i <= variants; i++)
                _population[i] = seed.Mutate(_rng, 0.3, atrMode: true);
        }
    }

    // ── Fitness ───────────────────────────────────────────────────────────────
    // Scores a genotype on a fraction of the training portion of the window.
    // The most recent HoldOutFraction is excluded here — reserved for ValidateHoldOut.
    private static double ScoreWindow(Genotype g, IReadOnlyDictionary<string, Candle[]> candles, double fraction)
    {
        var coinShares = new List<double>();
        foreach (var (_, arr) in candles)
        {
            // Exclude hold-out tail so the GA never sees it during evolution
            int trainLen = Math.Max(100, (int)(arr.Length * (1.0 - HoldOutFraction)));
            var trainArr = arr[..trainLen];
            int take     = Math.Max(100, (int)(trainArr.Length * fraction));
            var slice    = take >= trainArr.Length ? trainArr : trainArr[(trainArr.Length - take)..];
            var returns  = Simulator.GetUnifiedReturns(g, slice, useAtr: true)
                                    .Select(t => t.Return).ToList();
            if (returns.Count < MinTradesPerCoin) continue;
            double sharpe = Simulator.SharpeRatio(returns);
            double calmar = Math.Clamp(Simulator.CalmarRatio(returns), -2.0, 3.0);
            coinShares.Add(0.7 * sharpe + 0.3 * calmar);
        }
        if (coinShares.Count == 0) return 0;

        double mean = coinShares.Average();
        double std  = coinShares.Count > 1
            ? Math.Sqrt(coinShares.Select(s => (s - mean) * (s - mean)).Average())
            : 0;
        return mean - 0.6 * std;
    }

    // Multi-window fitness = minimum across 20% / 50% / 100% of the training portion.
    private static double ScoreOne(Genotype g, IReadOnlyDictionary<string, Candle[]> candles)
    {
        double s20  = ScoreWindow(g, candles, 0.20);
        double s50  = ScoreWindow(g, candles, 0.50);
        double s100 = ScoreWindow(g, candles, 1.00);
        return Math.Min(s20, Math.Min(s50, s100));
    }

    // Scores a genotype on only the held-out most-recent slice (never used during evolution).
    // Returns (score, number of qualifying coins).
    private static (double Score, int Coins) ValidateHoldOut(
        Genotype g, IReadOnlyDictionary<string, Candle[]> candles)
    {
        var coinShares = new List<double>();
        foreach (var (_, arr) in candles)
        {
            int holdOutStart = (int)(arr.Length * (1.0 - HoldOutFraction));
            var slice        = arr[holdOutStart..];
            if (slice.Length < 50) continue;
            var returns = Simulator.GetUnifiedReturns(g, slice, useAtr: true)
                                   .Select(t => t.Return).ToList();
            if (returns.Count < MinHoldOutTrades) continue;
            double sharpe = Simulator.SharpeRatio(returns);
            double calmar = Math.Clamp(Simulator.CalmarRatio(returns), -2.0, 3.0);
            coinShares.Add(0.7 * sharpe + 0.3 * calmar);
        }
        if (coinShares.Count == 0) return (0, 0);
        double mean = coinShares.Average();
        double std  = coinShares.Count > 1
            ? Math.Sqrt(coinShares.Select(s => (s - mean) * (s - mean)).Average())
            : 0;
        return (mean - 0.6 * std, coinShares.Count);
    }

    // ── Cycle ─────────────────────────────────────────────────────────────────
    public void EvaluateAll(IReadOnlyDictionary<string, Candle[]> recentCandles)
    {
        _cycleCount++;
        _evolvedThisCycle = false;
        _shouldPromote    = false;

        Parallel.ForEach(_population, g => g.Fitness = ScoreOne(g, recentCandles));

        _ranked = _population
            .OrderByDescending(g => g.Fitness)
            .Select(g => (g, g.Fitness))
            .ToList();

        if (_ranked.Count > 0)
        {
            double topScore = _ranked[0].Score;
            if (topScore > _sessionBestScore)
            {
                _sessionBestScore = topScore;
                _sessionBest      = _ranked[0].Geno;
            }

            // Auto-promote tracking: require sustained improvement over saved baseline
            if (topScore > _savedFitness + 1e-4)
                _promoteStreak++;
            else
                _promoteStreak = 0;

            if (_promoteStreak >= PromoteStreak)
            {
                // Hold-out gate: genotype must also score positively on the out-of-sample slice
                (_holdOutScore, _holdOutCoins) = ValidateHoldOut(_ranked[0].Geno, recentCandles);
                bool holdOutPass = _holdOutCoins < MinHoldOutCoins || _holdOutScore > 0;
                if (holdOutPass)
                    _shouldPromote = true;
            }
            else
            {
                _holdOutScore = double.MinValue;
                _holdOutCoins = 0;
            }
        }
    }

    public void MaybeEvolve()
    {
        if (_cycleCount % EvolveEvery != 0 || _ranked.Count == 0) return;

        _evolvedThisCycle = true;
        var sorted  = _ranked.Select(r => r.Geno).ToList();
        var nextGen = sorted.Take(EliteCount).ToList();

        while (nextGen.Count < _popSize)
        {
            var child = Genotype
                .Crossover(Tournament(sorted), Tournament(sorted), _rng)
                .Mutate(_rng, 0.35, atrMode: true);
            nextGen.Add(child);
        }
        _population = nextGen;
    }

    private Genotype Tournament(List<Genotype> pool, int k = 4) =>
        Enumerable.Range(0, k)
                  .Select(_ => pool[_rng.Next(pool.Count)])
                  .OrderByDescending(g => g.Fitness)
                  .First();

    // ── Convergence warning ───────────────────────────────────────────────────
    // Checks if top-5 genotypes are suspiciously similar across all continuous genes.
    // Returns a warning string if converged (normalised avg variance < threshold).
    public string? GetConvergenceWarning()
    {
        if (_ranked.Count < 5) return null;

        var top5 = _ranked.Take(5).Select(r => r.Geno).ToList();

        // Gene ranges for normalisation (same bounds as Genotype.Random)
        var genes = new (Func<Genotype, double> get, double min, double max)[]
        {
            (g => g.RsiPeriod,          7,  21),
            (g => g.RsiOverbought,     65,  80),
            (g => g.GridStepAtrMult,  0.3,  5.0),
            (g => g.DcaTriggerAtrMult,0.3,  8.0),
            (g => g.EmaPeriod,         10,  50),
            (g => g.BreakEvenAtrMult,  0.0, 2.0),
            (g => g.BosThreshold,    0.990,0.999),
            (g => g.VolumeMultiplier,  1.0, 2.5),
            (g => g.RegimeAdxThreshold,15, 35),
        };

        double totalNormVar = 0;
        foreach (var (get, min, max) in genes)
        {
            double range = max - min;
            if (range < 1e-9) continue;
            var vals = top5.Select(g => (get(g) - min) / range).ToList();
            double mean = vals.Average();
            double var_ = vals.Select(v => (v - mean) * (v - mean)).Average();
            totalNormVar += var_;
        }

        double avgNormVar = totalNormVar / genes.Length;
        if (avgNormVar < 0.005)
            return $"⚠ Population converged (avg normalised variance = {avgNormVar:F4}) — consider restart";

        return null;
    }

    // ── Console output ────────────────────────────────────────────────────────
    public void PrintRanking(IReadOnlyDictionary<string, Candle[]> candles)
    {
        int cyclesLeft = EvolveEvery - (_cycleCount % EvolveEvery);
        if (cyclesLeft == EvolveEvery) cyclesLeft = 0;

        string evolvedNote = _evolvedThisCycle ? "  ← evolved this cycle" : "";
        Console.WriteLine($"  Cycle {_cycleCount}  |  Next evolution in " +
                          $"{(cyclesLeft == 0 ? EvolveEvery : cyclesLeft)} cycle(s) " +
                          $"(~{(cyclesLeft == 0 ? EvolveEvery : cyclesLeft) * 5} min){evolvedNote}");
        Console.WriteLine($"  Coins evaluated: {candles.Count}  |  " +
                          $"Min trades/coin: {MinTradesPerCoin}  |  " +
                          $"Fitness: min(20%/50%/100%) · 0.7×Sharpe+0.3×Calmar − 0.6×std  |  Hold-out: last 20%\n");

        Console.WriteLine($"  {"#",-4} {"Score",7}  {"Genotype",-70}");
        Console.WriteLine($"  {new string('─', 84)}");

        for (int i = 0; i < _ranked.Count; i++)
        {
            var (g, score) = _ranked[i];
            string star = i == 0 ? "★" : " ";
            Console.WriteLine($"  {star} {i + 1,-3} {score,7:F3}  {g}");
        }

        if (_sessionBest != null)
            Console.WriteLine($"\n  Session best: F={_sessionBestScore:F3}  {_sessionBest}");

        // Show hold-out result when streak is in progress
        if (_promoteStreak > 0 && _holdOutScore > double.MinValue)
            Console.WriteLine($"\n  Hold-out ({HoldOutFraction*100:F0}% tail, {_holdOutCoins} coins): {_holdOutScore:F3}" +
                              (_holdOutCoins < MinHoldOutCoins ? "  (gate bypassed — insufficient data)" : ""));
        else if (_promoteStreak > 0)
            Console.WriteLine($"\n  Promote streak: {_promoteStreak}/{PromoteStreak}");

        string? conv = GetConvergenceWarning();
        if (conv != null)
            Console.WriteLine($"\n  {conv}");

        if (_shouldPromote)
            Console.WriteLine($"\n  ★ AUTO-PROMOTE: {PromoteStreak} consecutive improvements + hold-out passed " +
                              $"(F={_ranked[0].Score:F4} > saved F={_savedFitness:F4}, hold-out={_holdOutScore:F3})");
    }
}
