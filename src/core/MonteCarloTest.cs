namespace TradingGA;

/// <summary>
/// Monte Carlo significance test via mean-centred bootstrap. H0: E[r] = 0.
/// Draws n returns WITH REPLACEMENT from recentred series (r_i - mean).
/// Uncentred bootstrap would yield p~0.5 for every strategy (confidence interval, not test).
/// </summary>
public static class MonteCarloTest
{
    public record MonteCarloResult(
        double ActualStatistic,
        double MeanNullStatistic,
        double StdNullStatistic,
        double PValue,
        bool IsSignificant,
        int Resamples);

    /// <summary>
    /// Resample count for family-wise correction (Holm block). Default 1000 is fine for
    /// standalone α=0.05 tests; 20000 gives ~167 lattice points below Holm's strictest
    /// step (α/6 = 0.00833) with se(p̂) ≈ 0.00064 (7.7% of threshold).
    /// </summary>
    public const int FamilyWiseResamples = 20_000;

    /// <summary>
    /// Deterministic RNG seed from strategy name (FNV-1a, not GetHashCode which is per-process randomised).
    /// Makes p-value a pure function of that strategy's returns, independent of family membership.
    /// </summary>
    public static int SeedForStrategy(string strategyName)
    {
        unchecked
        {
            uint h = 2166136261u;                       // FNV offset basis
            foreach (char c in strategyName)
            {
                h ^= c;
                h *= 16777619u;                          // FNV prime
            }
            return (int)(h & 0x7FFFFFFF);                // non-negative; Random accepts any int
        }
    }

    /// <summary>
    /// Per-trade Sharpe (mean/pop-stdev). NOT Simulator.SharpeRatio — that returns 0 when PF&lt;1.3,
    /// and mean-centred resamples have PF~1.0, which would flatten the null to zero.
    /// Proportional to one-sample t-statistic; n is identical for actual and resamples.
    /// </summary>
    private static double PerTradeSharpe(IReadOnlyList<double> returns)
    {
        int n = returns.Count;
        if (n < 5) return 0;

        double mean = 0;
        for (int i = 0; i < n; i++) mean += returns[i];
        mean /= n;

        double ss = 0;
        for (int i = 0; i < n; i++)
        {
            double d = returns[i] - mean;
            ss += d * d;
        }
        double std = Math.Sqrt(ss / n);
        return std < 1e-12 ? 0 : mean / std;
    }

    /// <summary>
    /// Mean-centred bootstrap: one-sided p-value for H0: E[r] = 0.
    /// </summary>
    /// <param name="returns">Realised per-trade returns (percent).</param>
    /// <param name="permutations">Bootstrap resamples (name retained from old permutation impl).</param>
    /// <param name="rng">Optional RNG; defaults to fixed seed for reproducibility.</param>
    public static MonteCarloResult Run(
        List<double> returns,
        int permutations = 1000,
        Random? rng = null)
    {
        rng ??= new Random(42);

        if (returns.Count < 10 || permutations < 1)
            return new(0, 0, 0, 1.0, false, 0);

        double actualStat = PerTradeSharpe(returns);

        int n = returns.Count;
        double mean = returns.Average();
        var centred = new double[n];
        for (int i = 0; i < n; i++) centred[i] = returns[i] - mean;

        int countAtOrAbove = 0;
        var nullStats = new double[permutations];
        var resample = new double[n];

        for (int p = 0; p < permutations; p++)
        {
            for (int i = 0; i < n; i++) resample[i] = centred[rng.Next(n)];
            double stat = PerTradeSharpe(resample);
            nullStats[p] = stat;
            if (stat >= actualStat) countAtOrAbove++;
        }

        // Davison-Hinkley: (count+1)/(N+1), never reports exactly 0.
        double pValue = (countAtOrAbove + 1.0) / (permutations + 1.0);
        double meanNull = nullStats.Average();
        double stdNull = Math.Sqrt(nullStats.Select(s => (s - meanNull) * (s - meanNull)).Average());

        return new(actualStat, meanNull, stdNull, pValue, pValue <= 0.05, permutations);
    }

    // Moving-block bootstrap: draws contiguous blocks instead of independent observations.
    // Trades cluster (regime-gated entries on correlated perps), so iid resampling understates
    // null spread and gives optimistic p-values. blockSize=1 degenerates to Run()'s iid bootstrap.
    public static MonteCarloResult RunBlockBootstrap(
        List<double> returns,
        int blockSize,
        int permutations = 1000,
        Random? rng = null)
    {
        rng ??= new Random(42);

        if (returns.Count < 10 || permutations < 1)
            return new(0, 0, 0, 1.0, false, 0);

        double actualStat = PerTradeSharpe(returns);
        int n = returns.Count;
        blockSize = Math.Clamp(blockSize, 1, n);

        double mean = returns.Average();
        var centred = new double[n];
        for (int i = 0; i < n; i++) centred[i] = returns[i] - mean;

        int countAtOrAbove = 0;
        var nullStats = new double[permutations];
        var resample = new double[n];

        for (int p = 0; p < permutations; p++)
        {
            int idx = 0;
            while (idx < n)
            {
                // Circular wrap so edge observations are not under-sampled.
                int start = rng.Next(n);
                int len = Math.Min(blockSize, n - idx);
                for (int i = 0; i < len; i++) resample[idx++] = centred[(start + i) % n];
            }
            double stat = PerTradeSharpe(resample);
            nullStats[p] = stat;
            if (stat >= actualStat) countAtOrAbove++;
        }

        double pValue = (countAtOrAbove + 1.0) / (permutations + 1.0);
        double meanNull = nullStats.Average();
        double stdNull = Math.Sqrt(nullStats.Select(s => (s - meanNull) * (s - meanNull)).Average());

        return new(actualStat, meanNull, stdNull, pValue, pValue <= 0.05, permutations);
    }

    public static void PrintReport(MonteCarloResult result, string strategyName)
    {
        Console.WriteLine($"\n── Monte Carlo Bootstrap Test: {strategyName} ──");
        if (result.Resamples == 0)
        {
            Console.WriteLine("  Not run — fewer than 10 trades (no usable null distribution)");
            return;
        }
        Console.WriteLine($"  Actual statistic (per-trade Sharpe): {result.ActualStatistic:F4}");
        Console.WriteLine($"  Mean under null (E[r]=0):            {result.MeanNullStatistic:F4}");
        Console.WriteLine($"  Std under null:                      {result.StdNullStatistic:F4}");
        Console.WriteLine($"  p-value:                             {result.PValue:F4}  ({result.Resamples} mean-centred bootstrap resamples)");
        Console.WriteLine($"  Significant (p≤0.05):                {(result.IsSignificant ? "YES" : "NO")}");
        if (!result.IsSignificant)
            Console.WriteLine("  !! Edge may not be statistically significant — strategy could be a data artifact");
    }
}
