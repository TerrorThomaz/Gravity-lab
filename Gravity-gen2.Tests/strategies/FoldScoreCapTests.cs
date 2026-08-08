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

// ── Fold aggregation ─────────────────────────────────────────────────────────────────
//
// FoldScoreHelper.AggregateFoldScores used to be `mean - stdMult*std` over the surviving
// folds. That had two defects, and these tests pin the replacement:
//
//   (a) NON-MONOTONE. d(mean - c*sigma)/d(s_i) = (1/k)(1 - c*z_i) goes NEGATIVE once fold
//       i's z-score exceeds 1/c, which is reachable at every k >= 3 within the stdMult
//       clamp range [0.75, 2.0]. Improving the best fold could LOWER fitness.
//   (b) NO COVERAGE PENALTY. One surviving fold was returned verbatim, so concentrating
//       all activity into the single most favourable market window strictly dominated
//       trading consistently across all of them.
//
// The replacement is  coverage (x) [lambda*CVaR_alpha({s_f}) + (1-lambda)*mean({s_f})],
// with CVaR_alpha the mean of the worst ceil(alpha*k) fold scores. Both terms are
// non-decreasing in every fold score and lambda does not depend on the scores, so
// monotonicity holds by construction for any k and any lambda.
public class FoldAggregationTests
{
    private const int D = 14;

    [Fact]
    public void AggregateFoldScores_NoSurvivingFolds_IsWorseThanAnyRealFold()
    {
        double dead = FoldScoreHelper.AggregateFoldScores([], [], D, 5);
        Assert.Equal(FoldScoreHelper.DeadFoldFitness, dead, 6);
        // Canonical()'s worst real return is pf - 2.0 >= -2.0, so a genotype that trades and
        // loses must still outrank one that never fills a single fold.
        Assert.True(dead < -2.0, $"dead-fold fitness {dead} must be worse than the worst real fold score");
    }

    [Fact]
    public void AggregateFoldScores_SingleFoldAtFullCoverage_ReturnsThatScoreVerbatim()
    {
        // k == 1 means CVaR == mean == that fold, and coverage 1/1 == 1.
        Assert.Equal(12.5, FoldScoreHelper.AggregateFoldScores([12.5], [150], D, 1), 6);
        Assert.Equal(-1.75, FoldScoreHelper.AggregateFoldScores([-1.75], [30], D, 1), 6);
    }

    // ── (a) Monotonicity ─────────────────────────────────────────────────────────────

    [Fact]
    public void AggregateFoldScores_IsMonotone_AcrossManyFoldConfigurations()
    {
        // Sweep ONE fold's score upward across many (k, other-fold, trade-count, coverage)
        // configurations and require that fitness never falls. This is the property the
        // old aggregator lacked: it is not enough to check a single shape, because the
        // inversion region of mean - c*sigma depends on k, on c and on where the varied
        // fold sits in the distribution.
        var rng = new Random(7);
        int comparisons = 0;

        for (int trial = 0; trial < 3000; trial++)
        {
            int k = rng.Next(1, 9);
            var others = new List<double>();
            for (int j = 0; j < k - 1; j++) others.Add(rng.NextDouble() * 150.0 - 50.0);

            var counts = new List<int>();
            for (int j = 0; j < k; j++) counts.Add(rng.Next(10, 2000));

            int d         = new[] { 8, 10, 13, 14 }[rng.Next(4)];
            int attempted = k + rng.Next(0, 6);
            int slot      = rng.Next(k);

            double prev = double.NegativeInfinity;
            for (double s = -200.0; s <= 400.0; s += 3.0)
            {
                var scores = new List<double>(others);
                scores.Insert(slot, s);
                double fit = FoldScoreHelper.AggregateFoldScores(scores, counts, d, attempted);
                Assert.True(fit >= prev - 1e-9,
                    $"fitness FELL from {prev} to {fit} while fold {slot}'s score rose to {s} " +
                    $"(k={k}, attempted={attempted}, d={d}, others=[{string.Join(",", others)}])");
                prev = fit;
                comparisons++;
            }
        }

        Assert.True(comparisons > 500_000, $"only {comparisons} comparisons — sweep did not run");
    }

    [Fact]
    public void AggregateFoldScores_ImprovingTheBestFold_RaisesFitness_WhereTheOldFormInverted()
    {
        // The concrete counter-example from the review: at c = 0.75, k = 3,
        //   mean - 0.75*sigma over (10,10,30) = 9.596   but over (10,10,40) = 9.393.
        // Improving the best fold by a third LOWERED fitness. It must now rise.
        double worse  = FoldScoreHelper.AggregateFoldScores([10.0, 10.0, 30.0], [1000, 1000, 1000], D, 3);
        double better = FoldScoreHelper.AggregateFoldScores([10.0, 10.0, 40.0], [1000, 1000, 1000], D, 3);

        Assert.True(better > worse, $"(10,10,40) scored {better}, below (10,10,30)'s {worse}");

        // Exact values: k=3 -> m = ceil(0.4*3) = 2 worst folds; avgN/D = 71 -> lambda 0.4.
        //   (10,10,30): CVaR = 10, mean = 16.667 -> 0.4*10 + 0.6*16.667 = 14.0
        //   (10,10,40): CVaR = 10, mean = 20.000 -> 0.4*10 + 0.6*20.000 = 16.0
        Assert.Equal(14.0, worse,  6);
        Assert.Equal(16.0, better, 6);
    }

    [Fact]
    public void AggregateFoldScores_MonotonicInSurvivingFoldScore_WhenOtherFoldsEmpty()
    {
        // Only one fold produced trades; the other four were attempted and came back thin.
        // Fitness must never fall as that fold's score rises (the pre-fix code went NEGATIVE
        // in this gradient: d(fitness)/d(score) = -0.60 with four dead folds at 150 trades).
        double prev = double.NegativeInfinity;
        foreach (double s in new[] { -5.0, -1.0, 0.0, 1.0, 5.0, 25.0, 100.0, 1000.0 })
        {
            double fit = FoldScoreHelper.AggregateFoldScores([s], [150], D, 5);
            Assert.True(fit >= prev - 1e-9,
                $"fitness fell from {prev} to {fit} while the surviving fold's score rose to {s}");
            prev = fit;
        }
    }

    // ── (b) Coverage ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AggregateFoldScores_FullCoverage_BeatsSingleFoldConcentration()
    {
        // The dominance case from the review. Under the old aggregator the concentrated
        // genotype won outright: (20,25,30,35,40) -> 24.70 against a lone 30.0 -> 30.0.
        double consistent   = FoldScoreHelper.AggregateFoldScores(
            [20.0, 25.0, 30.0, 35.0, 40.0], [300, 300, 300, 300, 300], D, 5);
        double concentrated = FoldScoreHelper.AggregateFoldScores([30.0], [300], D, 5);

        Assert.True(consistent > concentrated,
            $"5/5 coverage at (20,25,30,35,40) scored {consistent}, below a single 30.0 fold at 1/5 ({concentrated})");

        // k=5 -> m = ceil(2) = 2 worst folds -> CVaR = 22.5, mean = 30, lambda = 0.4
        //   -> 0.4*22.5 + 0.6*30 = 27.0, coverage 5/5.
        // Concentrated: 30.0 x (1/5) = 6.0.
        Assert.Equal(27.0, consistent,   6);
        Assert.Equal(6.0,  concentrated, 6);
    }

    [Fact]
    public void AggregateFoldScores_CoveragePenalty_IsSignSafe()
    {
        // Coverage is applied as x*cov for x >= 0 and x/cov for x < 0. A plain multiply
        // would make a NEGATIVE aggregate LESS negative under partial coverage — i.e. it
        // would pay a losing genotype to trade in fewer folds, the same concentration
        // incentive with the sign flipped.
        double fullNeg    = FoldScoreHelper.AggregateFoldScores([-20.0], [300], D, 1);
        double partialNeg = FoldScoreHelper.AggregateFoldScores([-20.0], [300], D, 5);

        Assert.Equal(-20.0,  fullNeg,    6);
        Assert.Equal(-100.0, partialNeg, 6);
        Assert.True(partialNeg < fullNeg,
            $"partial coverage on a negative aggregate must be WORSE, not better ({partialNeg} vs {fullNeg})");
    }

    [Fact]
    public void AggregateFoldScores_CoverageCannotExceedOne()
    {
        // A caller that under-reports attempts must not be able to manufacture a bonus.
        double honest = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [300, 300], D, 2);
        double lying  = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [300, 300], D, 0);
        Assert.Equal(honest, lying, 9);
        Assert.Equal(20.0, honest, 6);
    }

    // ── Dispersion penalty (what CVaR replaces stdMult with) ─────────────────────────

    [Fact]
    public void AggregateFoldScores_TwoSurvivingFolds_PenalisesSpread()
    {
        // At equal mean, the spread-out pair has a lower worst-40% mean, so CVaR still
        // supplies the dispersion penalty the std term used to.
        double consistent   = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [200, 200], D, 2);
        double inconsistent = FoldScoreHelper.AggregateFoldScores([0.0, 40.0], [200, 200], D, 2);
        Assert.Equal(20.0, consistent, 6);        // zero spread -> no penalty
        Assert.Equal(12.0, inconsistent, 6);      // 0.4*0 + 0.6*20
        Assert.True(inconsistent < consistent,
            $"spread across folds must be penalised ({inconsistent} vs {consistent})");
    }

    [Fact]
    public void AggregateFoldScores_ThinSample_LeansHarderOnTheWorstFold()
    {
        // stdMult's VC-proportional intent is preserved as lambda: thinness runs over the
        // SAME clamp(7.5 / max(1, avgN/d), 0.75, 2.0) curve, remapped onto [0.4, 0.8].
        // A thin sample therefore weights the worst fold more, exactly as a large stdMult
        // used to weight the spread more — but monotonically.
        double thick = FoldScoreHelper.AggregateFoldScores([0.0, 40.0], [1000, 1000], D, 2); // lambda 0.4
        double thin  = FoldScoreHelper.AggregateFoldScores([0.0, 40.0], [30, 30], D, 2);     // lambda 0.8

        Assert.Equal(0.4, FoldScoreHelper.FoldLambda([1000, 1000], D), 9);
        Assert.Equal(0.8, FoldScoreHelper.FoldLambda([30, 30], D), 9);
        Assert.Equal(0.4 * 0.0 + 0.6 * 20.0, thick, 6);
        Assert.Equal(0.8 * 0.0 + 0.2 * 20.0, thin,  6);
        Assert.True(thin < thick);
    }

    [Fact]
    public void FoldLambda_StaysInsideZeroOne_ForEveryTradeCount()
    {
        // The monotonicity proof needs lambda in [0, 1]. Pin the endpoints and the range.
        foreach (int n in new[] { 0, 1, 5, 25, 100, 500, 5000, 1_000_000 })
        {
            double lambda = FoldScoreHelper.FoldLambda([n], D);
            Assert.InRange(lambda, FoldScoreHelper.LambdaThick, FoldScoreHelper.LambdaThin);
            Assert.InRange(lambda, 0.0, 1.0);
        }
        Assert.Equal(FoldScoreHelper.LambdaThin, FoldScoreHelper.FoldLambda([], D), 9);
    }

    [Fact]
    public void WorstFoldMean_TakesTheWorstCeilAlphaK()
    {
        Assert.Equal(20.0, FoldScoreHelper.WorstFoldMean([20.0], 0.4), 6);                 // k=1, m=1
        Assert.Equal(10.0, FoldScoreHelper.WorstFoldMean([10.0, 30.0], 0.4), 6);           // k=2, m=ceil(0.8)=1
        Assert.Equal(15.0, FoldScoreHelper.WorstFoldMean([30.0, 10.0, 20.0], 0.4), 6);     // k=3, m=2 -> (10+20)/2
        Assert.Equal(22.5, FoldScoreHelper.WorstFoldMean(
            [20.0, 25.0, 30.0, 35.0, 40.0], 0.4), 6);                                      // k=5, m=2 -> (20+25)/2
        // alpha = 1 degenerates to the plain mean; alpha -> 0 to the single worst fold.
        Assert.Equal(30.0, FoldScoreHelper.WorstFoldMean([20.0, 25.0, 30.0, 35.0, 40.0], 1.0), 6);
        Assert.Equal(20.0, FoldScoreHelper.WorstFoldMean([20.0, 25.0, 30.0, 35.0, 40.0], 0.0), 6);
    }
}

// ── The Grid family no longer bypasses the canonical fitness ─────────────────────────
//
// GridGA, GridShortGA and AccumulationGridGA each inlined their own copy of the fold-score
// formula. Consequences: the six FitnessConfig term weights were INERT for them,
// CVaRPenalty/TailRatioBonus never applied, and the SHAPE itself diverged (win-rate slope
// 0.5 vs 3.0, drawdown divisor x20 vs x10, no quality/retention/frequency terms) — while
// CoevolveGA and RegimeRouterGA combined their outputs with the other strategies' as if
// the scales were comparable. All three now call FoldScoreHelper.Canonical under
// FoldScoreHelper.GridShape.
public class GridShapeTests
{
    private static readonly FitnessConfig Neutral = new();

    [Fact]
    public void GridShape_PreservesTheThreeDeliberateDivergences()
    {
        var g = FoldScoreHelper.GridShape(Neutral);

        // wrMult slope: canonical 3.0 x WrW; Grid's historical slope was 0.5 => WrW = 1/6.
        Assert.Equal(1.0 / 6.0, g.WrW, 12);
        Assert.Equal(0.5, 3.0 * g.WrW, 12);

        // ddDiv: canonical 1 + maxDd*10*DdPenalty; Grid's historical divisor was x20.
        Assert.Equal(2.0, g.DdPenalty, 12);
        Assert.Equal(20.0, 10.0 * g.DdPenalty, 12);

        // No frequency bonus: FreqW = 0 makes Canonical's freqBonus exactly 1.0.
        Assert.Equal(0.0, g.FreqW, 12);
    }

    [Fact]
    public void GridShape_ReEnablesTheTermsThatWereMerelyMissing()
    {
        // Retention and gain were absent from the inlined Grid formula with no documented
        // reason — their absence is part of what made Grid's scale incomparable. They pass
        // through from the caller's config at their neutral 1.0.
        var g = FoldScoreHelper.GridShape(Neutral);
        Assert.Equal(1.0, g.RetentionW, 12);
        Assert.Equal(1.0, g.GainW, 12);

        // And the tail terms, which the inlined copy skipped entirely, are now live.
        Assert.Equal(Neutral.CVaRW, g.CVaRW, 12);
        Assert.Equal(Neutral.TailRatioW, g.TailRatioW, 12);
    }

    [Fact]
    public void GridShape_KeepsTheQualityTermOff_BecauseGridOperatesBelowRr1()
    {
        // Canonical's quality term is sqrt(pfMult * rrMult) with rrMult = (rr - 1)/1.5.
        // A grid wins often and small against an occasional whole-ladder stop-out, so it
        // sits at rr < 1.0, where rrMult is negative and the term is pinned at its 0
        // floor. Enabling it would give the Grid GA a constant-zero multiplier — a flat
        // fitness landscape, not a quality signal.
        Assert.Equal(0.0, FoldScoreHelper.GridShape(Neutral).QualityW, 12);

        // Demonstrate the operating range: high win rate, payoff below 1, pf comfortably
        // above 1 — exactly the region where the term is undefined before the NaN floor.
        var gridLike = new List<double>();
        for (int i = 0; i < 60; i++) gridLike.Add(i % 5 == 0 ? -2.0 : 0.8);
        double wins = gridLike.Count(r => r > 0), losses = gridLike.Count(r => r <= 0);
        double rr = (gridLike.Where(r => r > 0).Sum() / wins) / Math.Abs(gridLike.Where(r => r <= 0).Sum() / losses);
        Assert.True(rr < 1.0, $"fixture should sit below rr 1.0, got {rr}");
        Assert.True(Simulator.ProfitFactor(gridLike) > 1.0);

        // With the quality term off the score is a real, positive, non-NaN number.
        double score = FoldScoreHelper.Canonical(
            gridLike, 0.03, 10, FoldScoreHelper.GridShape(Neutral), 1.0, statBonusCeiling: 1.0);
        Assert.False(double.IsNaN(score));
        Assert.True(score > 0);
    }

    [Fact]
    public void Canonical_DoesNotReturnNaN_WhenRiskRewardIsBelowOne()
    {
        // sqrt(pfMult * rrMult) went NaN for every fold with rr < 1.0 — reachable by ANY
        // strategy that wins often and small, since pf >= 1.0 only requires
        // wins/losses > 1/rr. A NaN fitness makes the GA's sort order undefined, because
        // NaN compares false against everything.
        var r = new List<double>();
        for (int i = 0; i < 60; i++) r.Add(i % 5 == 0 ? -2.0 : 0.8);

        double score = FoldScoreHelper.Canonical(r, 0.03, 10, Neutral);
        Assert.False(double.IsNaN(score), "rr < 1.0 must not produce a NaN fold score");
        Assert.True(score >= 0.0);
    }

    [Fact]
    public void GridShape_IsAShapeDelta_NotAnOverride()
    {
        // A hand-tuned fitness_config.json must still have an effect through the
        // transform — the Grid divergences are relative, not absolute.
        var tuned = new FitnessConfig() with { WrW = 3.0, DdPenalty = 0.5 };
        var g = FoldScoreHelper.GridShape(tuned);
        Assert.Equal(3.0 / 6.0, g.WrW, 12);
        Assert.Equal(0.5 * 2.0, g.DdPenalty, 12);
    }

    [Fact]
    public void GridFoldScore_IsCanonicalUnderTheShapeTransform()
    {
        // Pin the wiring itself: GridGA.FoldScore must equal Canonical(GridShape(cfg)).
        var returns = new List<double>();
        for (int i = 0; i < 60; i++) returns.Add(i % 5 == 0 ? -2.0 : 0.8);

        var method = typeof(GridGeneticAlgorithm).GetMethod("FoldScore",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        double actual = (double)method.Invoke(null, [returns, Neutral, 1.0])!;

        double expected = FoldScoreHelper.Canonical(
            returns, 0.03, 10, FoldScoreHelper.GridShape(Neutral), 1.0, statBonusCeiling: 1.0);

        Assert.Equal(expected, actual, 12);
        Assert.True(actual > 0);
    }

    [Fact]
    public void GridFoldScore_NowRespondsToTheCanonicalTermWeights()
    {
        // These six weights were completely inert for the Grid family before.
        var returns = new List<double>();
        for (int i = 0; i < 60; i++) returns.Add(i % 5 == 0 ? -2.0 : 0.8);

        var method = typeof(GridGeneticAlgorithm).GetMethod("FoldScore",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        double Score(FitnessConfig c) => (double)method.Invoke(null, [returns, c, 1.0])!;

        double neutral = Score(Neutral);
        Assert.True(neutral > 0);
        // GainW scales linearly; DdPenalty compounds on top of GridShape's x2.
        Assert.Equal(neutral * 1.5, Score(Neutral with { GainW = 1.5 }), 9);
        Assert.True(Score(Neutral with { DdPenalty = 4.0 }) < neutral,
            "DdPenalty was inert for the Grid family before it went through Canonical");
        // WrW feeds GridShape's /6, so it must still move the score.
        Assert.True(Score(Neutral with { WrW = 4.0 }) > neutral,
            "WrW was inert for the Grid family before it went through Canonical");
    }
}
