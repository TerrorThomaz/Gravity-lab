using System.Reflection;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class FoldScoreCapTests
{
    private static double InvokeFoldScore<TGA>(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int sustainedBars,
        FitnessConfig cfg)
    {
        var method = typeof(TGA).GetMethod("FoldScore",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"FoldScore not found on {typeof(TGA).Name}");
        return (double)method.Invoke(null, [returns, posFrac, sustainedBars, cfg, 1.0])!;
    }

    private static List<(double Return, int RegimeBars)> MakeReturns(
        int winCount, double winPct, int lossCount, double lossPct, int regimeBars = 999)
    {
        var list = new List<(double, int)>();
        for (int i = 0; i < winCount; i++) list.Add((winPct, regimeBars));
        for (int i = 0; i < lossCount; i++) list.Add((-lossPct, regimeBars));
        return list;
    }

    [Fact]
    public void DipLong_QualityMult_CappedAt2Point5()
    {
        var extremeReturns = MakeReturns(50, 20.0, 5, 1.0);
        double score = InvokeFoldScore<DipLongGA>(extremeReturns, 0.03, 0, new FitnessConfig());
        var cappedReturns = MakeReturns(50, 8.0, 5, 1.0);
        double cappedScore = InvokeFoldScore<DipLongGA>(cappedReturns, 0.03, 0, new FitnessConfig());
        Assert.True(score > 0);
        Assert.True(cappedScore > 0);
        double ratio = score / cappedScore;
        Assert.True(ratio < 5.0,
            $"Extreme returns should not produce >5x score vs moderate (got {ratio:F2}x) — qualityMult cap not working");
    }

    [Fact]
    public void FadeLong_QualityMult_CappedAt2Point5()
    {
        var extremeReturns = MakeReturns(50, 20.0, 5, 1.0);
        double score = InvokeFoldScore<FadeLongGA>(extremeReturns, 0.03, 0, new FitnessConfig());
        var cappedReturns = MakeReturns(50, 8.0, 5, 1.0);
        double cappedScore = InvokeFoldScore<FadeLongGA>(cappedReturns, 0.03, 0, new FitnessConfig());
        Assert.True(score > 0);
        Assert.True(cappedScore > 0);
        double ratio = score / cappedScore;
        Assert.True(ratio < 5.0,
            $"Extreme returns should not produce >5x score vs moderate (got {ratio:F2}x) — qualityMult cap not working");
    }

    [Fact]
    public void RipShort_QualityMult_AlreadyCapped()
    {
        var extremeReturns = MakeReturns(50, 20.0, 5, 1.0);
        double score = InvokeFoldScore<RipShortGA>(extremeReturns, 0.03, 0, new FitnessConfig());
        var cappedReturns = MakeReturns(50, 8.0, 5, 1.0);
        double cappedScore = InvokeFoldScore<RipShortGA>(cappedReturns, 0.03, 0, new FitnessConfig());
        Assert.True(score > 0);
        Assert.True(cappedScore > 0);
        double ratio = score / cappedScore;
        Assert.True(ratio < 5.0,
            $"RipShort ratio should be <5x (got {ratio:F2}x)");
    }

    [Fact]
    public void DipLong_StatBonusCeiling_NotExceeding1Point5()
    {
        var cfg = new FitnessConfig(SharpeW: 1.0, CalmarW: 1.0, PfW: 1.0, SortinoW: 1.0);
        var returns = MakeReturns(40, 5.0, 10, 2.0);
        double withBonuses = InvokeFoldScore<DipLongGA>(returns, 0.03, 0, cfg);
        double noBonuses = InvokeFoldScore<DipLongGA>(returns, 0.03, 0, new FitnessConfig());
        double maxRatio = Math.Pow(1.0 + 1.5, 4);
        double actualRatio = withBonuses / noBonuses;
        Assert.True(actualRatio <= maxRatio + 0.01,
            $"Stat bonus ratio {actualRatio:F2} exceeds max possible {maxRatio:F2} (ceiling > 1.5)");
    }

    [Fact]
    public void FadeLong_StatBonusCeiling_NotExceeding1Point5()
    {
        var cfg = new FitnessConfig(SharpeW: 1.0, CalmarW: 1.0, PfW: 1.0, SortinoW: 1.0);
        var returns = MakeReturns(40, 5.0, 10, 2.0);
        double withBonuses = InvokeFoldScore<FadeLongGA>(returns, 0.03, 0, cfg);
        double noBonuses = InvokeFoldScore<FadeLongGA>(returns, 0.03, 0, new FitnessConfig());
        double maxRatio = Math.Pow(1.0 + 1.5, 4);
        double actualRatio = withBonuses / noBonuses;
        Assert.True(actualRatio <= maxRatio + 0.01,
            $"Stat bonus ratio {actualRatio:F2} exceeds max possible {maxRatio:F2} (ceiling > 1.5)");
    }
}
