namespace TradingGA;

/// <summary>
/// Monte Carlo significance test for a realised per-trade return series.
///
/// WHY THIS IS NO LONGER A PERMUTATION TEST
/// ----------------------------------------
/// The previous implementation Fisher-Yates shuffled the return list and recomputed
/// Simulator.SharpeRatio on it. But Sharpe is built purely from the mean and the
/// standard deviation of the list, and both are order-invariant — every "permutation"
/// reproduced the actual Sharpe to within floating-point summation noise (~1e-15 total
/// spread over 1000 shuffles). Because the tail comparison is `>=`, countAtOrAbove was
/// ~always equal to the permutation count, the p-value pinned near 1.0 for every
/// strategy, and IsSignificant was effectively never true. The "edge may not be
/// statistically significant" warning therefore fired unconditionally and trained the
/// operator to ignore it.
///
/// WHY NOT JUST SWITCH TO A PATH-DEPENDENT STATISTIC (approach (a))
/// ---------------------------------------------------------------
/// Permuting order does move Calmar / max-drawdown, so the null distribution would at
/// least be dispersed. But it answers the wrong question: "is the ORDER of these trades
/// special?", not "is this edge real?". A strategy with a genuinely positive expectancy
/// and no serial structure — which is exactly what we hope for — lands in the middle of
/// that null (p ~ 0.5), while an all-winners series has zero drawdown in every ordering
/// (p = 1.0). It would replace an always-fails test with an always-inconclusive one.
///
/// THE NULL USED HERE: MEAN-CENTRED BOOTSTRAP (approach (b))
/// ---------------------------------------------------------
/// Draw n returns WITH REPLACEMENT from the recentred series (r_i - mean(r)). Both the
/// mean and the std of a resample genuinely vary, so order-invariance of Sharpe stops
/// mattering — the data itself changes. The recentred empirical distribution is the
/// strategy's own return shape (fat tails, win rate, loss clustering magnitude) under
/// H0: E[r] = 0, i.e. "the trade-level edge is zero and everything observed is sampling
/// luck". The recentring is the essential part: an UNcentred bootstrap resamples around
/// the observed mean, so its Sharpe distribution is centred on the actual Sharpe and the
/// p-value pins at ~0.5 for every strategy — that construction yields a confidence
/// interval, not a hypothesis test.
///
/// Sanity check by construction:
///   * strongly-trending / genuinely profitable series (mean >> 0, modest std): the
///     actual statistic sits far in the right tail of a null centred on ~0  ->  p small.
///   * symmetric noise (mean ~ 0): the actual statistic sits in the middle of the
///     null  ->  p ~ 0.5.
///   * losing series (mean &lt; 0): actual is in the left tail  ->  p near 1.0.
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
    /// Plain per-trade Sharpe: mean / population-stdev.
    ///
    /// Deliberately NOT Simulator.SharpeRatio — that one hard-returns 0 whenever the
    /// profit factor is under 1.3, and by construction every mean-centred resample has
    /// PF ~ 1.0. Using it would flatten the entire null distribution to exactly zero and
    /// make every strategy with a positive Sharpe look significant (p = 1/(N+1)), which
    /// is the mirror image of the original bug.
    ///
    /// This is proportional to the one-sample t-statistic (t = sharpe * sqrt(n)); n is
    /// identical for the actual series and for every resample, so the monotone scaling
    /// cannot change the p-value and is dropped for readability.
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
    /// Mean-centred bootstrap significance test. Returns the actual per-trade Sharpe, the
    /// null distribution's mean/std, and a one-sided p-value for H0: E[r] = 0.
    /// </summary>
    /// <param name="returns">Realised per-trade returns (percent).</param>
    /// <param name="permutations">
    /// Number of bootstrap resamples. The parameter name is retained from the old
    /// permutation implementation so existing named-argument call sites keep compiling.
    /// </param>
    /// <param name="rng">Optional RNG; defaults to a fixed seed so reports are reproducible.</param>
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

        // Davison-Hinkley estimator (count + 1) / (N + 1): never reports exactly 0, which
        // would claim more confidence than the resampling resolution can support.
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
