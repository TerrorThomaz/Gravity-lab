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

// A walk-forward fold that never reached MinTradesPerFold returns the CONSTANT -1.0 sentinel
// from FoldScoreHelper.Canonical. Averaging that constant into mean − stdMult×std inverted the
// GA's gradient: with 2+ dead folds, raising a live fold's score LOWERED fitness, so the GA
// selected for genotypes that traded badly or not at all. The four long/short GAs now drop dead
// folds before aggregating; these tests pin the resulting behaviour.
public class FoldAggregationTests
{
    private const int D = 14;

    [Fact]
    public void AggregateFoldScores_NoSurvivingFolds_IsWorseThanAnyRealFold()
    {
        double dead = FoldScoreHelper.AggregateFoldScores([], [], D);
        Assert.Equal(FoldScoreHelper.DeadFoldFitness, dead, 6);
        // Canonical()'s worst real return is pf - 2.0 >= -2.0, so a genotype that trades and
        // loses must still outrank one that never fills a single fold.
        Assert.True(dead < -2.0, $"dead-fold fitness {dead} must be worse than the worst real fold score");
    }

    [Fact]
    public void AggregateFoldScores_SingleSurvivingFold_ReturnsThatScoreVerbatim()
    {
        Assert.Equal(12.5, FoldScoreHelper.AggregateFoldScores([12.5], [150], D), 6);
        Assert.Equal(-1.75, FoldScoreHelper.AggregateFoldScores([-1.75], [30], D), 6);
    }

    [Fact]
    public void AggregateFoldScores_MonotonicInSurvivingFoldScore_WhenOtherFoldsEmpty()
    {
        // Only one fold produced trades; the other four were empty and are not passed in.
        // Fitness must never fall as that fold's score rises (the pre-fix code went NEGATIVE
        // in this gradient: d(fitness)/d(score) = -0.60 with four dead folds at 150 trades).
        double prev = double.NegativeInfinity;
        foreach (double s in new[] { -5.0, -1.0, 0.0, 1.0, 5.0, 25.0, 100.0, 1000.0 })
        {
            double fit = FoldScoreHelper.AggregateFoldScores([s], [150], D);
            Assert.True(fit >= prev - 1e-9,
                $"fitness fell from {prev} to {fit} while the surviving fold's score rose to {s}");
            prev = fit;
        }
    }

    [Fact]
    public void AggregateFoldScores_DeadFoldsExcluded_BeatIdenticalRunWithSentinelPadding()
    {
        // What the buggy version effectively computed: one live fold plus four -1.0 sentinels.
        double[] padded = [40.0, -1.0, -1.0, -1.0, -1.0];
        double paddedMean = padded.Average();
        double paddedStd  = Math.Sqrt(padded.Select(x => (x - paddedMean) * (x - paddedMean)).Average());
        double paddedFit  = paddedMean - 0.75 * paddedStd;   // 0.75 is the stdMult clamp FLOOR

        double clean = FoldScoreHelper.AggregateFoldScores([40.0], [150], D);
        Assert.True(clean > paddedFit,
            $"sentinel padding must not be able to out-score the clean aggregation ({clean} vs {paddedFit})");
    }

    [Fact]
    public void AggregateFoldScores_TwoSurvivingFolds_PenalisesSpread()
    {
        // With 2+ surviving folds the VC-proportional variance penalty is preserved:
        // consistent folds beat wildly inconsistent ones with the same mean.
        double consistent   = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [200, 200], D);
        double inconsistent = FoldScoreHelper.AggregateFoldScores([0.0, 40.0], [200, 200], D);
        Assert.Equal(20.0, consistent, 6);        // zero spread -> no penalty
        Assert.True(inconsistent < consistent,
            $"spread across folds must be penalised ({inconsistent} vs {consistent})");
    }

    [Fact]
    public void AggregateFoldScores_ThinSample_RaisesStdMult()
    {
        // stdMult = clamp(7.5 / max(1, avgN/D), 0.75, 2.0) — thin folds get a harsher penalty.
        double thick = FoldScoreHelper.AggregateFoldScores([0.0, 40.0], [1000, 1000], D);  // avgN/D huge -> 0.75
        double thin  = FoldScoreHelper.AggregateFoldScores([0.0, 40.0], [30, 30], D);      // avgN/D ~2.1 -> 2.0
        Assert.Equal(20.0 - 0.75 * 20.0, thick, 6);
        Assert.Equal(20.0 - 2.00 * 20.0, thin, 6);
        Assert.True(thin < thick);
    }
}
