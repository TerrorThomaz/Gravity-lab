namespace TradingGA;

/// <summary>
/// Maintains a population of genotypes and evaluates them on a rolling window
/// of live candle data across all coins. Evolves the population every K cycles.
///
/// Overfitting mitigation:
///   - Fitness = mean Sharpe across ALL coins minus 0.4 × cross-coin std.
///     Genes that only work on one coin are penalised; universal genes survive.
///   - Rolling window (~10 days) rewards genes that adapt to current conditions,
///     not historical patterns.
///   - Population diversity: top 5 elites preserved, rest replaced with evolved
///     offspring each hour.
/// </summary>
public class LiveTrainer
{
    // ── Config ────────────────────────────────────────────────────────────────
    private const int MinTradesPerCoin = 5;   // skip coin if too few trades
    private const int EliteCount       = 5;   // survivors per evolution step
    private const int EvolveEvery     = 12;   // cycles between GA ops (~1 h at 5 min/cycle)

    private readonly int    _popSize;
    private readonly Random _rng = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private List<Genotype>                     _population;
    private List<(Genotype Geno, double Score)> _ranked = [];
    private int      _cycleCount          = 0;
    private Genotype? _sessionBest        = null;
    private double    _sessionBestScore   = double.MinValue;
    private bool      _evolvedThisCycle   = false;

    public int      CycleCount         => _cycleCount;
    public Genotype? SessionBest        => _sessionBest;
    public double    SessionBestScore   => _sessionBestScore;

    // ── Construction ──────────────────────────────────────────────────────────
    public LiveTrainer(int popSize = 20, Genotype? seed = null)
    {
        _popSize    = popSize;
        _population = Enumerable.Range(0, popSize)
                                .Select(_ => Genotype.Random(_rng, atrMode: true))
                                .ToList();

        if (seed != null)
        {
            _population[0] = seed;                         // pure seed copy
            int variants   = Math.Min(popSize / 4, popSize - 1);
            for (int i = 1; i <= variants; i++)
                _population[i] = seed.Mutate(_rng, 0.3, atrMode: true);
        }
    }

    // ── Fitness ───────────────────────────────────────────────────────────────
    // Returns mean Sharpe across coins − 0.4 × std (cross-coin variance penalty).
    // Genes that are universal score high; WIF-specific overfits score low.
    private static double ScoreOne(Genotype g, IReadOnlyDictionary<string, Candle[]> candles)
    {
        var coinShares = new List<double>();
        foreach (var (_, arr) in candles)
        {
            if (arr.Length < 100) continue;
            var returns = Simulator.GetUnifiedReturns(g, arr, useAtr: true)
                                   .Select(t => t.Return).ToList();
            if (returns.Count < MinTradesPerCoin) continue;
            coinShares.Add(Simulator.SharpeRatio(returns));
        }
        if (coinShares.Count == 0) return 0;

        double mean = coinShares.Average();
        double std  = coinShares.Count > 1
            ? Math.Sqrt(coinShares.Select(s => (s - mean) * (s - mean)).Average())
            : 0;
        return mean - 0.4 * std;
    }

    // ── Cycle ─────────────────────────────────────────────────────────────────
    public void EvaluateAll(IReadOnlyDictionary<string, Candle[]> recentCandles)
    {
        _cycleCount++;
        _evolvedThisCycle = false;

        Parallel.ForEach(_population, g => g.Fitness = ScoreOne(g, recentCandles));

        _ranked = _population
            .OrderByDescending(g => g.Fitness)
            .Select(g => (g, g.Fitness))
            .ToList();

        if (_ranked.Count > 0 && _ranked[0].Score > _sessionBestScore)
        {
            _sessionBestScore = _ranked[0].Score;
            _sessionBest      = _ranked[0].Geno;
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

    // ── Console output ────────────────────────────────────────────────────────
    public void PrintRanking(IReadOnlyDictionary<string, Candle[]> candles)
    {
        int cyclesLeft = EvolveEvery - (_cycleCount % EvolveEvery);
        if (cyclesLeft == EvolveEvery) cyclesLeft = 0; // just evolved

        string evolvedNote = _evolvedThisCycle ? "  ← evolved this cycle" : "";
        Console.WriteLine($"  Cycle {_cycleCount}  |  Next evolution in " +
                          $"{(cyclesLeft == 0 ? EvolveEvery : cyclesLeft)} cycle(s) " +
                          $"(~{(cyclesLeft == 0 ? EvolveEvery : cyclesLeft) * 5} min){evolvedNote}");
        Console.WriteLine($"  Coins evaluated: {candles.Count}  |  " +
                          $"Min trades/coin: {MinTradesPerCoin}  |  " +
                          $"Variance penalty: 0.4×σ\n");

        Console.WriteLine($"  {"#",-4} {"Score",7}  {"Genotype",-70}");
        Console.WriteLine($"  {new string('─', 84)}");

        for (int i = 0; i < _ranked.Count; i++)
        {
            var (g, score) = _ranked[i];
            string star = i == 0 ? "★" : " ";
            Console.WriteLine($"  {star} {i + 1,-3} {score,7:F3}  {g}");
        }

        if (_sessionBest != null)
        {
            Console.WriteLine($"\n  Session best: F={_sessionBestScore:F3}  {_sessionBest}");
        }
    }
}
