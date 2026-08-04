using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class StatisticalTestsNewTests
{
    [Fact]
    public void HolmBonferroni_AllSignificant_AllRejected()
    {
        var pValues = new double[] { 0.001, 0.005, 0.01 };
        var rejected = StatisticalTests.HolmBonferroni(pValues, alpha: 0.05);
        Assert.All(rejected, r => Assert.True(r));
    }

    [Fact]
    public void HolmBonferroni_NoneSignificant_NoneRejected()
    {
        var pValues = new double[] { 0.10, 0.20, 0.30 };
        var rejected = StatisticalTests.HolmBonferroni(pValues, alpha: 0.05);
        Assert.All(rejected, r => Assert.False(r));
    }

    [Fact]
    public void HolmBonferroni_MixedCorrectly()
    {
        var pValues = new double[] { 0.01, 0.04, 0.03 };
        var rejected = StatisticalTests.HolmBonferroni(pValues, alpha: 0.05);
        Assert.True(rejected[0]);
        Assert.False(rejected[1]);
        Assert.False(rejected[2]);
    }

    [Fact]
    public void HolmBonferroni_AdjustedPValues_Monotonic()
    {
        var pValues = new double[] { 0.01, 0.04, 0.03, 0.10 };
        var adjusted = StatisticalTests.HolmBonferroniAdjustedPValues(pValues);
        Assert.Equal(4, adjusted.Length);
        Assert.True(adjusted[0] <= adjusted[2]);
        Assert.True(adjusted[2] <= adjusted[1]);
        Assert.True(adjusted[1] <= adjusted[3]);
    }

    [Fact]
    public void HolmBonferroni_SingleTest_MatchesRaw()
    {
        var pValues = new double[] { 0.03 };
        var adjusted = StatisticalTests.HolmBonferroniAdjustedPValues(pValues);
        Assert.Equal(0.03, adjusted[0], 5);
    }

    [Fact]
    public void CVaR_NormalDistribution_NegativeTail()
    {
        var returns = new List<double> { 5, 3, -1, -2, 4, -3, 2, -4, 1, -5 };
        double cvar = StatisticalTests.CVaR(returns, alpha: 0.20);
        Assert.True(cvar < 0, $"CVaR should be negative for tail losses, got {cvar}");
        Assert.Equal(-4.5, cvar, 1);
    }

    [Fact]
    public void CVaR_AllPositive_StillReturnsLowest()
    {
        var returns = new List<double> { 1, 2, 3, 4, 5 };
        double cvar = StatisticalTests.CVaR(returns, alpha: 0.20);
        Assert.Equal(1.0, cvar, 1);
    }

    [Fact]
    public void CVaR_EmptyList_ReturnsZero()
    {
        Assert.Equal(0, StatisticalTests.CVaR(new List<double>()));
    }
}

public class TimeEmbargoTests
{
    [Fact]
    public void ComputeFoldBoundaries_NoEmbargo_ContiguousFolds()
    {
        var bounds = FoldScoreHelper.ComputeFoldBoundaries(1000, 5, embargoPct: 0.0);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.Equal(200, bounds[0].End);
        Assert.Equal(200, bounds[1].Start);
        Assert.Equal(400, bounds[1].End);
    }

    [Fact]
    public void ComputeFoldBoundaries_WithEmbargo_GapsBetweenFolds()
    {
        var bounds = FoldScoreHelper.ComputeFoldBoundaries(1000, 5, embargoPct: 0.10);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.Equal(200, bounds[0].End);
        Assert.True(bounds[1].Start > 200, $"Fold 1 should start after embargo gap, got {bounds[1].Start}");
        Assert.Equal(400, bounds[1].End);
    }

    [Fact]
    public void ComputeFoldBoundaries_LastFold_ExtendsToEnd()
    {
        var bounds = FoldScoreHelper.ComputeFoldBoundaries(1000, 5, embargoPct: 0.05);
        Assert.Equal(1000, bounds[4].End);
    }

    [Fact]
    public void ComputeRegimeAwareFoldBoundaries_AllBull_BehavesLikeRegular()
    {
        var series = Enumerable.Range(0, 1000)
            .Select(i => new RegimeBar(DateTime.UtcNow.AddHours(i), MarketRegime.Bull, 0.8, i))
            .ToArray();
        var bounds = FoldScoreHelper.ComputeRegimeAwareFoldBoundaries(series, r => r == MarketRegime.Bull, 5, 0.0);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.True(bounds[4].End >= 990, $"Last fold should extend near end, got {bounds[4].End}");
    }

    [Fact]
    public void ComputeRegimeAwareFoldBoundaries_MixedRegimes_FoldsSpanActivePeriods()
    {
        var series = new List<RegimeBar>();
        for (int i = 0; i < 1000; i++)
        {
            var regime = (i / 100) % 2 == 0 ? MarketRegime.Bull : MarketRegime.Bear;
            series.Add(new RegimeBar(DateTime.UtcNow.AddHours(i), regime, 0.8, i % 100));
        }
        var bounds = FoldScoreHelper.ComputeRegimeAwareFoldBoundaries(series.ToArray(), r => r == MarketRegime.Bull, 5, 0.05);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.True(bounds[4].End >= 900, $"Last fold should extend near end, got {bounds[4].End}");
    }

    [Fact]
    public void ComputeRegimeAwareFoldBoundaries_InsufficientActiveData_FallsBackToRegular()
    {
        var series = Enumerable.Range(0, 100)
            .Select(i => new RegimeBar(DateTime.UtcNow.AddHours(i), MarketRegime.Bear, 0.8, i))
            .ToArray();
        var bounds = FoldScoreHelper.ComputeRegimeAwareFoldBoundaries(series, r => r == MarketRegime.Bull, 5, 0.05);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.Equal(100, bounds[4].End);
    }
}
