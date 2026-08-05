using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class MonteCarloTests
{
    // 75% winners of +2%, 25% losers of -3%  ->  mean 0.75, std ~2.165,
    // per-trade Sharpe ~0.346 which is ~6.9 standard errors from zero at n = 400.
    private static List<double> StronglyProfitableSeries(int n = 400)
    {
        var returns = new List<double>(n);
        for (int i = 0; i < n; i++) returns.Add(i % 4 != 3 ? 2.0 : -3.0);
        return returns;
    }

    // Deterministic, non-degenerate, and recentred so the mean is exactly zero:
    // no edge whatsoever, but plenty of dispersion.
    private static List<double> SymmetricNoiseSeries(int n = 400)
    {
        var returns = new List<double>(n);
        for (int i = 0; i < n; i++) returns.Add(Math.Sin(i * 1.7) * 3.0);
        double mean = returns.Average();
        for (int i = 0; i < returns.Count; i++) returns[i] -= mean;
        return returns;
    }

    // Regression guard for the original bug: the old implementation shuffled the return
    // LIST and recomputed an order-invariant statistic, so the "null distribution" was a
    // single value repeated N times (total spread ~1e-15, pure floating-point summation
    // noise). Any real null must be dispersed on the scale of 1/sqrt(n).
    [Fact]
    public void NullDistribution_HasNonTrivialSpread()
    {
        var result = MonteCarloTest.Run(SymmetricNoiseSeries(), permutations: 500, rng: new Random(7));

        Assert.Equal(500, result.Resamples);
        Assert.True(result.StdNullStatistic > 1e-3,
            $"Null distribution is degenerate (std {result.StdNullStatistic:E3}) — the null does not destroy the edge");
        // n = 400 -> the sampling std of the per-trade Sharpe under H0 is ~1/sqrt(n) = 0.05.
        Assert.True(result.StdNullStatistic > 0.01 && result.StdNullStatistic < 0.30,
            $"Null spread {result.StdNullStatistic:F4} is not on the expected 1/sqrt(n) scale");
    }

    [Fact]
    public void NullDistribution_IsCentredNearZero()
    {
        var result = MonteCarloTest.Run(StronglyProfitableSeries(), permutations: 500, rng: new Random(11));

        // Mean-centred bootstrap => E[statistic | H0] ~ 0, regardless of how good the
        // strategy actually is. An UNcentred bootstrap would instead centre the null on
        // the actual statistic and pin every p-value near 0.5.
        Assert.True(Math.Abs(result.MeanNullStatistic) < 0.05,
            $"Null should be centred on ~0 under H0: E[r]=0, got {result.MeanNullStatistic:F4}");
    }

    [Fact]
    public void StronglyProfitableSeries_YieldsLowPValue()
    {
        var result = MonteCarloTest.Run(StronglyProfitableSeries(), permutations: 1000, rng: new Random(42));

        Assert.True(result.ActualStatistic > 0.3,
            $"Expected a clearly positive per-trade Sharpe, got {result.ActualStatistic:F4}");
        Assert.True(result.PValue <= 0.05, $"Expected a significant p-value, got {result.PValue:F4}");
        Assert.True(result.IsSignificant);
        Assert.True(
            result.ActualStatistic > result.MeanNullStatistic + 3 * result.StdNullStatistic,
            "Actual statistic should sit far in the right tail of the null distribution");
    }

    [Fact]
    public void SymmetricNoiseSeries_YieldsHighPValue()
    {
        var result = MonteCarloTest.Run(SymmetricNoiseSeries(), permutations: 1000, rng: new Random(42));

        Assert.True(Math.Abs(result.ActualStatistic) < 1e-9,
            $"Recentred noise should have a ~0 statistic, got {result.ActualStatistic:E3}");
        Assert.False(result.IsSignificant);
        Assert.True(result.PValue > 0.05, $"Pure noise must not be significant, got p={result.PValue:F4}");
        Assert.True(result.PValue > 0.25 && result.PValue < 0.80,
            $"A zero-edge series should land mid-distribution, got p={result.PValue:F4}");
    }

    [Fact]
    public void LosingSeries_YieldsVeryHighPValue()
    {
        // Mirror image of the profitable series: 75% losers of -2%, 25% winners of +3%.
        var returns = new List<double>();
        for (int i = 0; i < 400; i++) returns.Add(i % 4 != 3 ? -2.0 : 3.0);

        var result = MonteCarloTest.Run(returns, permutations: 500, rng: new Random(42));

        Assert.True(result.ActualStatistic < 0);
        Assert.False(result.IsSignificant);
        Assert.True(result.PValue > 0.9, $"A losing series should be deep in the left tail, got p={result.PValue:F4}");
    }

    [Fact]
    public void PValue_IsNeverExactlyZero()
    {
        // Davison-Hinkley (count + 1) / (N + 1): the floor is 1/(N+1), never 0, so the
        // report cannot claim more confidence than the resampling resolution supports.
        var result = MonteCarloTest.Run(StronglyProfitableSeries(), permutations: 200, rng: new Random(42));

        Assert.True(result.PValue >= 1.0 / 201.0 - 1e-12, $"p-value below the estimator floor: {result.PValue:E3}");
        Assert.True(result.PValue > 0);
    }

    [Fact]
    public void TooFewTrades_ReturnsInconclusiveResult()
    {
        var returns = new List<double> { 1, -1, 2, -2, 3, 1, -1, 0, 1 }; // 9 trades

        var result = MonteCarloTest.Run(returns, permutations: 1000, rng: new Random(42));

        Assert.Equal(0, result.Resamples);
        Assert.Equal(1.0, result.PValue);
        Assert.False(result.IsSignificant);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalResult()
    {
        var a = MonteCarloTest.Run(StronglyProfitableSeries(), permutations: 300, rng: new Random(123));
        var b = MonteCarloTest.Run(StronglyProfitableSeries(), permutations: 300, rng: new Random(123));

        Assert.Equal(a.PValue, b.PValue);
        Assert.Equal(a.ActualStatistic, b.ActualStatistic);
        Assert.Equal(a.StdNullStatistic, b.StdNullStatistic);
    }
}
