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
// FoldScoreHelper.AggregateFoldScores has had three shapes. These tests pin the third and
// the two properties the first two lacked.
//
//   (a) `mean - stdMult*std` over the SURVIVING folds. NON-MONOTONE:
//       d(mean - c*sigma)/d(s_i) = (1/k)(1 - c*z_i) goes NEGATIVE once fold i's z-score
//       exceeds 1/c, reachable at every k >= 3 inside the stdMult clamp range [0.75, 2.0].
//       Improving the best fold could LOWER fitness. It also injected Canonical's -1.0
//       thin-fold sentinel into the variance term.
//
//   (b) `coverage * [lambda*CVaR + (1-lambda)*mean]`, coverage = surviving/attempted.
//       Monotone, but it did NOT remove the concentration incentive it was added for.
//       Coverage is a BOUNDED LINEAR haircut; the gain from deleting a bad fold is
//       UNBOUNDED. At K = 5, four folds at g and one at b, lambda = 0.4:
//           keep = 0.32b + 0.68g,  drop = 0.8g   =>   dropping wins iff b < 0.375g
//       At g = 30 a genotype was better off dropping any fold scoring +11.25 or LESS —
//       including profitable folds — and at lambda = 0.8 (thin samples) the threshold
//       widened to 0.545g. Every call site `continue`s past a fold under MinTradesPerFold,
//       so the move was one threshold-gene tightening away.
//
//   (c) CURRENT: `lambda*CVaR_alpha({s_f}) + (1-lambda)*mean({s_f})` over a CONSTANT-LENGTH
//       fold vector of attemptedFolds entries, with FoldScoreHelper.ThinFoldScore
//       substituted for every attempted fold that did not reach MinTradesPerFold. There is
//       no "drop a fold" move left in the search space — withdrawing replaces a fold's
//       score with a strictly worse one — so the coverage multiplier had nothing left to
//       price and was removed rather than kept as a redundant knob.
//
// The constant is NOT a return of the -1.0 sentinel bug. Under `mean - c*std` a constant
// inflates std and therefore SUBTRACTS, inverting the gradient (measured d(fitness)/d(fold
// score) = -0.38 with 2 dead folds, -0.60 with 4). Under `lambda*CVaR + (1-lambda)*mean`
// both terms are non-decreasing in every entry and the constant is simply a low entry, i.e.
// a penalty. That distinction is what AggregateFoldScores_IsMonotone_* and
// AggregateFoldScores_RaisingARealFold_NeverLowersFitness_WhenOtherFoldsSitAtTheFloor pin.
public class FoldAggregationTests
{
    private const int D = 14;

    // ── The floor constant ───────────────────────────────────────────────────────────

    [Fact]
    public void ThinFoldScore_SitsBelowEveryScoreCanonicalCanProduce()
    {
        // The floor is only sound if it is worse than every REAL fold score — otherwise
        // withdrawing from a bad fold would still pay, which is precisely how the original
        // -1.0 sentinel failed: it sat ABOVE the [-2.0, -1.0) losing band, so "produce no
        // trades here" beat "trade and lose here".
        //
        // Canonical's range is [-2.0, +inf):
        //   pf < 1.0                -> pf - 2.0, and pf >= 0, so in [-2.0, -1.0);
        //   pf >= 1.0, gain <= 0    -> gain*100 - 0.5, but pf >= 1 forces sum(returns) >= 0
        //                              hence gain >= 0, so this is only reachable at
        //                              gain == 0, returning -0.5;
        //   otherwise               -> a product of non-negative factors, so >= 0.
        // -2.0 is therefore the hard minimum, and the floor must clear it.
        Assert.True(FoldScoreHelper.ThinFoldScore < -2.0,
            $"floor {FoldScoreHelper.ThinFoldScore} must be worse than Canonical's -2.0 minimum");

        var rng = new Random(20260808);
        var cfg = new FitnessConfig();
        double min = double.MaxValue;
        for (int trial = 0; trial < 60_000; trial++)
        {
            int n = rng.Next(10, 60);
            var r = new List<double>(n);
            // Deliberately spans losing, break-even and winning folds, and both the
            // grossLoss ~= 0 and grossWins == 0 degenerate branches.
            double scale = new[] { 0.01, 1.0, 30.0 }[rng.Next(3)];
            double bias  = new[] { -1.0, -0.5, 0.0, 0.5 }[rng.Next(4)];
            for (int i = 0; i < n; i++) r.Add((rng.NextDouble() + bias) * scale);
            double s = FoldScoreHelper.Canonical(r, 0.03, 10, cfg);
            Assert.False(double.IsNaN(s), "Canonical must never return NaN");
            min = Math.Min(min, s);
        }
        Assert.True(min >= -2.0 - 1e-12, $"Canonical produced {min}, below its documented -2.0 floor");
        Assert.True(min > FoldScoreHelper.ThinFoldScore,
            $"a real fold scored {min}, at or below the thin-fold floor {FoldScoreHelper.ThinFoldScore}");
    }

    [Fact]
    public void ThinFoldScore_IsAPenalty_NotADominatingSentinel()
    {
        // The other half of the calibration: the floor must not swamp the score. One floored
        // fold out of five costs lambda*(floor/m) + (1-lambda)*(floor/K), which must stay the
        // same order of magnitude as a real fold's contribution — otherwise fitness collapses
        // to "how many folds are non-thin" and the gradient on the surviving folds' quality
        // is erased. DeadFoldFitness in that slot is the failure mode being avoided.
        double allReal   = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0, 30.0], [300, 300, 300, 300, 300], D, 5);
        double oneFloored = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0], [300, 300, 300, 300], D, 5);

        Assert.Equal(30.0, allReal, 6);
        Assert.Equal(18.8, oneFloored, 6);      // 0.4*12.5 + 0.6*23.0
        Assert.True(oneFloored < allReal, "a floored fold must cost something");

        // Still a live gradient on the surviving folds despite the floor beside them.
        double betterSurvivors = FoldScoreHelper.AggregateFoldScores(
            [40.0, 40.0, 40.0, 40.0], [300, 300, 300, 300], D, 5);
        Assert.True(betterSurvivors > oneFloored + 5.0,
            $"the floor flattened the landscape: 4x40 scored {betterSurvivors} against 4x30's {oneFloored}");

        // And it is nowhere near DeadFoldFitness, which WOULD flatten it.
        Assert.True(FoldScoreHelper.ThinFoldScore > FoldScoreHelper.DeadFoldFitness / 100.0,
            "the floor is deep enough to behave like the dead-fold sentinel");
    }

    [Fact]
    public void AggregateFoldScores_EveryAttemptedFoldThin_ReturnsTheFloor()
    {
        // The all-floor vector. Worse than any genotype that traded at all — including one
        // that traded and lost in every fold — but on the same scale, so the ordering
        // between "no trades" and "bad trades" is sane rather than a 1000-point cliff.
        double none = FoldScoreHelper.AggregateFoldScores([], [], D, 5);
        Assert.Equal(FoldScoreHelper.ThinFoldScore, none, 9);

        double tradedAndLost = FoldScoreHelper.AggregateFoldScores(
            [-2.0, -2.0, -2.0, -2.0, -2.0], [300, 300, 300, 300, 300], D, 5);
        Assert.True(none < tradedAndLost,
            $"trading and losing ({tradedAndLost}) must still beat never filling a fold ({none})");

        // …and one real fold beats none at all, at every level of coverage.
        double oneRealFold = FoldScoreHelper.AggregateFoldScores([-2.0], [150], D, 5);
        Assert.True(oneRealFold > none, $"one losing fold ({oneRealFold}) must beat no folds ({none})");
    }

    [Fact]
    public void AggregateFoldScores_NoFoldsAttemptedAtAll_ReturnsDeadFoldFitness()
    {
        // Degenerate caller state — no walk-forward loop ran, so there is nothing to
        // aggregate and nothing is known about the genotype. Unreachable from the GAs (every
        // call site passes its own fold count); it exists so the function is total.
        Assert.Equal(FoldScoreHelper.DeadFoldFitness,
            FoldScoreHelper.AggregateFoldScores([], [], D, 0), 6);
    }

    [Fact]
    public void AggregateFoldScores_SingleFoldAtFullCoverage_ReturnsThatScoreVerbatim()
    {
        // K == 1 means CVaR == mean == that fold, and no floors are padded in.
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
    public void AggregateFoldScores_IsMonotone_InTheHardCases()
    {
        // The cases the random sweep above does not reliably reach: k = 1, attempted < k,
        // all-equal scores (worst-set selection fully ambiguous), all-negative scores, ties
        // straddling the CVaR boundary, heavy floor padding (attempted = 50 against k = 1),
        // and a fine step straight through 0 — where the removed coverage map used to switch
        // between its multiply and divide branches.
        var rng = new Random(20260808);
        int comparisons = 0;

        var countPatterns = new List<int[]>
        {
            new int[] { 1 }, new int[] { 5 }, new int[] { 10_000 },
            new int[] { 30, 30 }, new int[] { 1000, 1000 },
            new int[] { 20, 5000 }, new int[] { 100, 100, 100 },
            new int[] { 1, 1, 1, 1, 1, 1, 1, 1 },
        };

        foreach (var counts in countPatterns)
        {
            int k = counts.Length;
            foreach (int d in new[] { 1, 8, 13, 14, 200 })
            foreach (int attempted in new[] { 0, 1, k, k + 1, 50 })
            foreach (var others in EqualAndTiedPatterns(k, rng))
            {
                for (int slot = 0; slot < k; slot++)
                {
                    double prev = double.NegativeInfinity;
                    for (double s = -50.0; s <= 50.0; s += 0.05)
                    {
                        var scores = new List<double>(others);
                        scores.Insert(slot, s);
                        double fit = FoldScoreHelper.AggregateFoldScores(scores, counts, d, attempted);
                        Assert.True(fit >= prev - 1e-9,
                            $"NON-MONOTONE: fitness fell {prev} -> {fit} at s={s}, slot={slot}, " +
                            $"k={k}, d={d}, attempted={attempted}, others=[{string.Join(",", others)}]");
                        prev = fit;
                        comparisons++;
                    }
                }
            }
        }

        Assert.True(comparisons > 100_000, $"only {comparisons} comparisons");
    }

    private static IEnumerable<List<double>> EqualAndTiedPatterns(int k, Random rng)
    {
        // all-equal (every fold tied — worst-set selection is fully ambiguous)
        yield return Enumerable.Repeat(0.0, k - 1).ToList();
        yield return Enumerable.Repeat(7.5, k - 1).ToList();
        // all-negative
        yield return Enumerable.Repeat(-3.0, k - 1).ToList();
        // every other fold pinned exactly at the thin-fold floor
        yield return Enumerable.Repeat(FoldScoreHelper.ThinFoldScore, k - 1).ToList();
        // random
        yield return Enumerable.Range(0, k - 1).Select(_ => rng.NextDouble() * 60 - 30).ToList();
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

        // Exact values: K=3 -> m = ceil(0.4*3) = 2 worst folds; avgN/D = 71 -> lambda 0.4.
        //   (10,10,30): CVaR = 10, mean = 16.667 -> 0.4*10 + 0.6*16.667 = 14.0
        //   (10,10,40): CVaR = 10, mean = 20.000 -> 0.4*10 + 0.6*20.000 = 16.0
        Assert.Equal(14.0, worse,  6);
        Assert.Equal(16.0, better, 6);
    }

    [Fact]
    public void AggregateFoldScores_RaisingARealFold_NeverLowersFitness_WhenOtherFoldsSitAtTheFloor()
    {
        // THE regression test for "do not recreate the original sentinel bug". Under
        // `mean - c*std` this exact configuration had d(fitness)/d(score) = -0.60 with four
        // dead folds, because the constant inflated std. Under the CVaR/mean blend the
        // gradient must be >= 0 for every fold count, and STRICTLY positive once the varied
        // fold clears the floor (it then leaves the worst-set and moves the mean).
        foreach (int attempted in new[] { 1, 2, 3, 5, 8 })
        {
            double prev = double.NegativeInfinity;
            double first = 0, last = 0;
            for (double s = -3.0; s <= 120.0; s += 0.25)
            {
                double fit = FoldScoreHelper.AggregateFoldScores([s], [150], D, attempted);
                Assert.True(fit >= prev - 1e-12,
                    $"fitness fell from {prev} to {fit} while the one real fold rose to {s} " +
                    $"({attempted - 1} folds at the floor)");
                if (double.IsNegativeInfinity(prev)) first = fit;
                last = fit;
                prev = fit;
            }
            Assert.True(last > first,
                $"no gradient at all with {attempted - 1} floored folds ({first} -> {last})");
        }
    }

    [Fact]
    public void AggregateFoldScores_HasNoStepAroundZero()
    {
        // The removed coverage factor was piecewise (`x*cov` above 0, `x/cov` below), which
        // put a kink at the aggregate's zero. Nothing switches on the sign any more, so the
        // aggregate is smooth through 0 for every coverage level.
        foreach (int attempted in new[] { 1, 2, 5 })
        {
            double below = FoldScoreHelper.AggregateFoldScores([-1e-9], [300], D, attempted);
            double at    = FoldScoreHelper.AggregateFoldScores([0.0],   [300], D, attempted);
            double above = FoldScoreHelper.AggregateFoldScores([+1e-9], [300], D, attempted);

            Assert.True(below <= at && at <= above, $"not monotone across 0 at attempted={attempted}");
            Assert.True(above - below < 1e-6,
                $"step of {above - below} across 0 at attempted={attempted}");
        }
    }

    [Fact]
    public void AggregateFoldScores_NaNFoldScore_Propagates()
    {
        // Documented, not desired: a NaN fold score poisons mean and CVaR, and
        // OrderByDescending over NaN fitness is order-undefined because NaN compares false
        // against everything. Canonical is what has to stop NaN from ever getting here —
        // see QualityTermTests.Canonical_NeverReturnsNaN_OverRandomDistributions.
        double f = FoldScoreHelper.AggregateFoldScores(
            [10.0, double.NaN, 10.0], [300, 300, 300], D, 3);
        Assert.True(double.IsNaN(f), $"expected NaN to propagate, got {f}");
    }

    // ── (b) Coverage: withdrawing from a fold is not a move ──────────────────────────

    [Fact]
    public void AggregateFoldScores_WithdrawingFromAnyFold_NeverRaisesFitness()
    {
        // GENERALISED from the old AggregateFoldScores_FullCoverage_BeatsSingleFoldConcentration,
        // which tested the single configuration (5 consistent folds vs one concentrated fold)
        // where the coverage factor happened to work. The property is not about that one
        // shape: for ANY fold vector, and ANY fold in it, ceasing to trade that window must
        // not pay. Scores are drawn from Canonical's reachable range [-2.0, +inf).
        var rng = new Random(4242);
        int comparisons = 0;
        double worstMargin = double.MaxValue;

        for (int trial = 0; trial < 20_000; trial++)
        {
            int attempted = rng.Next(2, 9);
            int k         = rng.Next(1, attempted + 1);

            var scores = new List<double>();
            var counts = new List<int>();
            for (int i = 0; i < k; i++)
            {
                // ~15% of folds land in the losing band [-2, 0), the rest are profitable.
                scores.Add(rng.NextDouble() < 0.15
                    ? -2.0 + rng.NextDouble() * 2.0
                    : rng.NextDouble() * 120.0);
                counts.Add(rng.Next(10, 3000));
            }
            int d = new[] { 1, 8, 13, 14, 25 }[rng.Next(5)];

            double keep = FoldScoreHelper.AggregateFoldScores(scores, counts, d, attempted);
            for (int i = 0; i < k; i++)
            {
                var s2 = new List<double>(scores); s2.RemoveAt(i);
                var c2 = new List<int>(counts);    c2.RemoveAt(i);
                double drop = FoldScoreHelper.AggregateFoldScores(s2, c2, d, attempted);

                Assert.True(drop <= keep + 1e-12,
                    $"WITHDRAWAL PAID: dropping fold {i} (score {scores[i]}) moved fitness " +
                    $"{keep} -> {drop} (attempted={attempted}, d={d}, " +
                    $"scores=[{string.Join(",", scores)}])");
                worstMargin = Math.Min(worstMargin, keep - drop);
                comparisons++;
            }
        }

        Assert.True(comparisons > 50_000, $"only {comparisons} comparisons — sweep did not run");
        Assert.True(worstMargin > 0, $"withdrawal was free somewhere (margin {worstMargin})");
    }

    [Fact]
    public void AggregateFoldScores_DroppingTheWorstFold_LowersFitness()
    {
        // The review's headline exploit, at the numbers it was measured with. Under the
        // coverage factor: keep 19.76, drop 24.00 — tightening a filter until the bad window
        // fell under MinTradesPerFold RAISED fitness by 21%. The kept case is unchanged
        // (nothing was padded); the dropped case now pads a floor in instead of shrinking.
        double keepsBadFold = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0, -2.0], [300, 300, 300, 300, 300], D, 5);
        double withdrawsFromIt = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0], [300, 300, 300, 300], D, 5);

        Assert.Equal(19.76, keepsBadFold, 6);      // 0.4*14.0 + 0.6*23.6, unchanged
        Assert.Equal(18.80, withdrawsFromIt, 6);   // 0.4*12.5 + 0.6*23.0, was 24.00

        Assert.True(withdrawsFromIt < keepsBadFold,
            $"withdrawing from the losing fold scored {withdrawsFromIt}, at or above the " +
            $"{keepsBadFold} for trading all five");
    }

    [Fact]
    public void AggregateFoldScores_DroppingAProfitableFold_LowersFitness()
    {
        // Solving 0.8g > 0.32b + 0.68g gave b < 0.375g, so at a +30 baseline the coverage
        // factor paid to throw away any fold under +11.25 — clearly PROFITABLE folds. The
        // exclusion threshold was not near zero, it was 37.5% of the other folds' level.
        double keep = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0, 11.0], [300, 300, 300, 300, 300], D, 5);
        double drop = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0], [300, 300, 300, 300], D, 5);

        Assert.True(drop < keep,
            $"a fold scoring +11.0 is still worth throwing away: drop={drop} >= keep={keep}");
    }

    [Fact]
    public void AggregateFoldScores_ThinSample_DoesNotWidenTheDropIncentive()
    {
        // Thin samples used to make it WORSE: lambda rises to 0.8 and the same algebra gave
        // b < 0.545g — over half the baseline. The VC-proportional curve meant to make thin
        // samples more conservative instead widened the range of folds a genotype was paid to
        // abandon. Now the floor is weighted MORE at high lambda, so thin samples punish
        // withdrawal harder rather than less.
        Assert.Equal(0.8, FoldScoreHelper.FoldLambda([30, 30, 30, 30, 30], D), 9);

        double keep = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0, 16.0], [30, 30, 30, 30, 30], D, 5);
        double drop = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0], [30, 30, 30, 30], D, 5);

        Assert.True(drop < keep,
            $"thin sample: dropping a +16.0 fold out of a +30 baseline still pays " +
            $"(drop={drop} >= keep={keep})");

        // …and the penalty for withdrawing is LARGER on the thin sample than on a thick one.
        double thickKeep = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0, 16.0], [3000, 3000, 3000, 3000, 3000], D, 5);
        double thickDrop = FoldScoreHelper.AggregateFoldScores(
            [30.0, 30.0, 30.0, 30.0], [3000, 3000, 3000, 3000], D, 5);
        Assert.True(keep - drop > thickKeep - thickDrop,
            $"thin sample penalises withdrawal by {keep - drop}, less than the thick sample's " +
            $"{thickKeep - thickDrop}");
    }

    [Fact]
    public void AggregateFoldScores_NegativeAggregate_StillPenalisesWithdrawal()
    {
        // The old coverage map's divide-when-negative branch did not close the hole either:
        // it applied to the AGGREGATE, so a genotype whose folds were all losing still gained
        // by abandoning its worst window (-7.08 -> -1.25 in the review's fixture). Scores here
        // are inside Canonical's reachable band [-2.0, 0) — the review's -20.0 fold is not a
        // value Canonical can emit, and the floor is calibrated against the real range.
        double keep = FoldScoreHelper.AggregateFoldScores(
            [-1.0, -1.0, -1.0, -1.0, -2.0], [300, 300, 300, 300, 300], D, 5);
        double drop = FoldScoreHelper.AggregateFoldScores(
            [-1.0, -1.0, -1.0, -1.0], [300, 300, 300, 300], D, 5);

        Assert.Equal(-1.32, keep, 6);      // 0.4*(-1.5) + 0.6*(-1.2)
        Assert.Equal(-2.28, drop, 6);      // 0.4*(-3.0) + 0.6*(-1.8)
        Assert.True(drop < keep,
            $"all-losing genotype still gains by abandoning its worst window ({keep} -> {drop})");
    }

    [Fact]
    public void AggregateFoldScores_FullCoverage_BeatsConcentration_ForEveryFoldVector()
    {
        // The original dominance case, generalised: it is not enough that ONE consistent
        // vector beats ONE concentrated fold. For any vector, trading all K windows must beat
        // trading only its BEST window — including when the other folds are mediocre, when
        // they are losing, and at every K.
        var rng = new Random(99);
        for (int trial = 0; trial < 5000; trial++)
        {
            int K = rng.Next(2, 9);
            var scores = new List<double>();
            var counts = new List<int>();
            for (int i = 0; i < K; i++)
            {
                scores.Add(-2.0 + rng.NextDouble() * 60.0);
                counts.Add(rng.Next(20, 1500));
            }
            double consistent = FoldScoreHelper.AggregateFoldScores(scores, counts, D, K);

            int best = 0;
            for (int i = 1; i < K; i++) if (scores[i] > scores[best]) best = i;
            double concentrated = FoldScoreHelper.AggregateFoldScores(
                [scores[best]], [counts[best]], D, K);

            Assert.True(consistent > concentrated,
                $"concentration into the best window ({concentrated}) beat full coverage " +
                $"({consistent}) at K={K}, scores=[{string.Join(",", scores)}]");
        }

        // The exact original numbers, for continuity with the review:
        //   K=5 -> m = 2 worst folds -> CVaR = 22.5, mean = 30, lambda = 0.4 -> 27.0
        //   concentrated: [30, floor x4] -> CVaR = -5, mean = 2.0, lambda 0.72 -> -3.04
        Assert.Equal(27.0, FoldScoreHelper.AggregateFoldScores(
            [20.0, 25.0, 30.0, 35.0, 40.0], [300, 300, 300, 300, 300], D, 5), 6);
        Assert.Equal(-3.04, FoldScoreHelper.AggregateFoldScores([30.0], [300], D, 5), 6);
    }

    [Fact]
    public void AggregateFoldScores_UnderReportedAttempts_CannotShrinkTheFoldVector()
    {
        // A caller that under-reports attempts must not be able to manufacture a bonus by
        // making the vector shorter than the folds it actually scored.
        double honest = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [300, 300], D, 2);
        double lying  = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [300, 300], D, 0);
        Assert.Equal(honest, lying, 9);
        Assert.Equal(20.0, honest, 6);

        // A caller with FEWER trade counts than scores must not throw or read out of range.
        double ragged = FoldScoreHelper.AggregateFoldScores([20.0, 20.0], [300], D, 2);
        Assert.False(double.IsNaN(ragged));
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

// ── The risk/reward leg of the quality term ──────────────────────────────────────────
//
// Canonical's quality term used to be Sqrt(Max(0, pfMult) * Max(0, rrMult)) with
// rrMult = (rr - 1)/1.5. That ramp is NEGATIVE for every fold with rr < 1.0, so the Max
// floored it to 0, qualityMult to 0 at the default QualityW = 1.0, and — because base_ is a
// pure PRODUCT and the stat/tail terms are multiplicative — the WHOLE FOLD SCORE to exactly
// 0.0. Measured on 100-trade folds with default config:
//     80 wins @ +1.0 / 20 losses @ -2.0  ->  pf 2.00, wr 80%, rr 0.50  ->  score 0.000
//     40 wins @ +3.0 / 60 losses @ -1.5  ->  pf 1.33, wr 40%, rr 2.00  ->  score 0.147
// so the strictly worse strategy ranked higher, and the entire rr < 1 half-space was one
// flat plateau. rr < 1 is structurally reachable (pf >= 1 only needs wins/losses > 1/rr, and
// the ATR take-profit / stop-loss multipliers are genes), and only the Grid family escaped
// via GridShape's QualityW = 0.
//
// FoldScoreHelper.RrMultiplier replaces the ramp with a saturating hyperbola that is
// strictly positive, strictly increasing and bounded on (0, inf).
public class QualityTermTests
{
    private const double PosFrac   = 0.03;
    private const int    MinTrades = 30;

    // 100 trades, 80 wins of +1.0%, 20 losses of -2.0%: pf 2.00, wr 0.80, rr 0.50.
    private static List<double> HighWinRateSmallWins()
    {
        var r = new List<double>();
        for (int i = 0; i < 80; i++) r.Add(1.0);
        for (int i = 0; i < 20; i++) r.Add(-2.0);
        return r;
    }

    // 100 trades, 40 wins of +3.0%, 60 losses of -1.5%: pf 1.33, wr 0.40, rr 2.00.
    private static List<double> LowWinRateBigWins()
    {
        var r = new List<double>();
        for (int i = 0; i < 40; i++) r.Add(3.0);
        for (int i = 0; i < 60; i++) r.Add(-1.5);
        return r;
    }

    // The grid-shaped fixture: wins often and small against an occasional big stop-out.
    private static List<double> GridLike()
    {
        var r = new List<double>();
        for (int i = 0; i < 60; i++) r.Add(i % 5 == 0 ? -2.0 : 0.8);
        return r;
    }

    [Fact]
    public void Canonical_ProfitFactorTwoAtLowPayoffRatio_DoesNotCollapseToZero()
    {
        double s = FoldScoreHelper.Canonical(HighWinRateSmallWins(), PosFrac, MinTrades, new FitnessConfig());
        Assert.True(s > 0.0,
            $"a fold with pf = 2.0 and wr = 80% scored {s}; the rr leg is zeroing the whole product");
    }

    [Fact]
    public void Canonical_HasAGradient_ThroughoutTheSubOnePayoffRatioRegion()
    {
        var cfg = new FitnessConfig();

        // pf = 2.00, rr = 0.50
        double good = FoldScoreHelper.Canonical(HighWinRateSmallWins(), PosFrac, MinTrades, cfg);

        // pf = 1.05, rr = 0.35 — far worse on every axis, still rr < 1
        var poor = new List<double>();
        for (int i = 0; i < 75; i++) poor.Add(0.7);
        for (int i = 0; i < 25; i++) poor.Add(-2.0);
        double bad = FoldScoreHelper.Canonical(poor, PosFrac, MinTrades, cfg);

        Assert.True(good > bad,
            $"FLAT PLATEAU: pf=2.0 fold scored {good}, pf~1.05 fold scored {bad} — selection " +
            "inside the rr < 1 half-space is a coin flip");
    }

    [Fact]
    public void Canonical_RanksTheBetterStrategyHigher_EvenWhenItsPayoffRatioIsBelowOne()
    {
        var cfg = new FitnessConfig();
        double pf200 = FoldScoreHelper.Canonical(HighWinRateSmallWins(), PosFrac, MinTrades, cfg);
        double pf133 = FoldScoreHelper.Canonical(LowWinRateBigWins(),    PosFrac, MinTrades, cfg);

        Assert.True(pf200 > pf133,
            $"pf = 2.00 / wr = 80% fold scored {pf200}; pf = 1.33 / wr = 40% fold scored {pf133}");
    }

    [Fact]
    public void Canonical_IsStrictlyIncreasingInPayoffRatio_AcrossRrEqualsOne()
    {
        // The reason the "fall back to the no-op 1.0 below rr = 1" alternative was rejected:
        // the old ramp passes through ZERO at rr = 1, so that fallback would have replaced
        // the plateau with a CLIFF — full score at rr = 0.999, ~0 at rr = 1.001 — which is
        // defect (3)'s pathology one axis over. The replacement has no such point.
        var cfg = new FitnessConfig();
        double prev = double.NegativeInfinity;
        for (double win = 0.70; win <= 3.0; win += 0.005)
        {
            var r = new List<double>();
            for (int i = 0; i < 60; i++) r.Add(win);      // rr == win, since every loss is 1.0
            for (int i = 0; i < 40; i++) r.Add(-1.0);
            double s = FoldScoreHelper.Canonical(r, PosFrac, MinTrades, cfg);
            Assert.True(s > prev,
                $"fold score did not rise (prev {prev}, now {s}) as the payoff ratio reached {win}");
            prev = s;
        }
    }

    [Fact]
    public void Canonical_GridShapedFold_ScoresAboveZero_AtTheDefaultQualityWeight()
    {
        // This is the fixture the shipped NaN test uses. Its score used to be EXACTLY 0.0.
        double score = FoldScoreHelper.Canonical(GridLike(), 0.03, 10, new FitnessConfig());
        Assert.True(score > 0.0, $"the grid-shaped fold scores {score} at the default QualityW");
    }

    [Fact]
    public void GridShape_QualityOff_IsStillAValidEscapeHatch()
    {
        var grid = FoldScoreHelper.GridShape(new FitnessConfig());
        Assert.Equal(0.0, grid.QualityW);

        double score = FoldScoreHelper.Canonical(HighWinRateSmallWins(), PosFrac, MinTrades, grid);
        Assert.True(score > 0.0, $"with QualityW = 0 the same fold scores {score}");
    }

    // ── RrMultiplier's contract ──────────────────────────────────────────────────────

    [Fact]
    public void RrMultiplier_IsNeutralAtItsNeutralPoint()
    {
        // RrNeutral = 2.5 is the old ramp's own no-op point, so the term's calibration
        // ("quality 1.0 at pf 1.5 / rr 2.5") is unchanged.
        Assert.Equal(1.0, FoldScoreHelper.RrMultiplier(FoldScoreHelper.RrNeutral), 12);
        Assert.Equal(2.5, FoldScoreHelper.RrNeutral, 12);
    }

    [Fact]
    public void RrMultiplier_IsStrictlyPositiveAndStrictlyIncreasing_OnThePositiveReals()
    {
        double prev = 0.0;
        for (double rr = 1e-6; rr < 1000.0; rr *= 1.02)
        {
            double m = FoldScoreHelper.RrMultiplier(rr);
            Assert.True(m > 0.0, $"RrMultiplier({rr}) = {m} — the sqrt argument can be zeroed");
            Assert.True(m > prev, $"RrMultiplier is not strictly increasing at rr = {rr}");
            Assert.True(m < FoldScoreHelper.MaxRrMult, $"RrMultiplier({rr}) = {m} breached its bound");
            prev = m;
        }
        Assert.Equal(1.6, FoldScoreHelper.MaxRrMult, 12);
    }

    [Fact]
    public void RrMultiplier_IsBounded_SoAFreakPayoffRatioCannotDominate()
    {
        // The old ramp grew linearly without limit above rr = 2.5 and leaned entirely on the
        // downstream Clamp(..., 0, 2.5) to contain it.
        Assert.True(FoldScoreHelper.RrMultiplier(1e12) < FoldScoreHelper.MaxRrMult);
        Assert.Equal(FoldScoreHelper.MaxRrMult, FoldScoreHelper.RrMultiplier(double.PositiveInfinity), 12);
    }

    [Fact]
    public void RrMultiplier_NeverReturnsNaN_ForAnyInput()
    {
        foreach (double rr in new[]
        {
            double.NaN, double.NegativeInfinity, double.PositiveInfinity,
            -1e300, -1.0, -1e-300, 0.0, 1e-300, 1e-6, 1.0, 1e300
        })
        {
            double m = FoldScoreHelper.RrMultiplier(rr);
            Assert.False(double.IsNaN(m), $"RrMultiplier({rr}) returned NaN");
            Assert.InRange(m, 0.0, FoldScoreHelper.MaxRrMult);
        }
    }

    [Fact]
    public void Canonical_NeverReturnsNaN_OverRandomDistributions()
    {
        // NaN fitness makes the GA's sort order undefined, because NaN compares false against
        // everything. Canonical is the boundary that has to stop it.
        var rng = new Random(1234);
        var cfg = new FitnessConfig();
        for (int trial = 0; trial < 40_000; trial++)
        {
            int n = rng.Next(1, 40);
            var r = new List<double>(n);
            double scale = new[] { 0.0, 1e-9, 1.0, 1e6 }[rng.Next(4)];
            for (int i = 0; i < n; i++) r.Add((rng.NextDouble() - 0.5) * scale);
            foreach (var c in new[] { cfg, FoldScoreHelper.GridShape(cfg), cfg with { QualityW = 4.0 } })
                Assert.False(double.IsNaN(FoldScoreHelper.Canonical(r, 0.03, 5, c)),
                    $"NaN from a {n}-trade fold at scale {scale}");
        }
    }
}

// ── The tail-estimator sample gate is a ramp, not a step ─────────────────────────────
//
// MinTailSampleSize used to be a HARD gate inside two MULTIPLICATIVE terms ranging
// [0.5, 1.0] (CVaRPenalty) and [1.0, 1.5] (TailRatioBonus). Measured on a 60/35/5 fold:
// at n = 100 the score was 4.970, and DELETING ONE WINNING TRADE — strictly less profit,
// n = 99 — scored 6.750, +35.8%, because the CVaR penalty switched off at the gate. Every
// MinTradesPerFold in the repo is 10-30, so the 10..99 band is inside the normal operating
// range of every strategy.
//
// Both terms now blend toward their neutral 1.0 with w(n) over [TailRampLo, TailRampHi] =
// [40, 100]: the bucket both statistics read is 1-in-20, so it holds one observation below
// 40 (fully off — the estimator is a single order statistic) and five at 100 (fully on, the
// point the gate was already calibrated at).
public class TailSampleRampTests
{
    private const double PosFrac   = 0.03;
    private const int    MinTrades = 30;

    // `wins` wins of +4.0, 35 losses of -1.0, 5 losses of -8.0 -> a 5% left tail at -8.0.
    private static List<double> FatTailFold(int wins)
    {
        var r = new List<double>();
        for (int i = 0; i < wins; i++) r.Add(4.0);
        for (int i = 0; i < 35; i++)   r.Add(-1.0);
        for (int i = 0; i < 5; i++)    r.Add(-8.0);
        return r;
    }

    [Fact]
    public void Canonical_GivingUpAWinningTradeToFallBelowTheGate_LowersTheFoldScore()
    {
        var cfg = new FitnessConfig();

        var at100 = FatTailFold(60);
        var at99  = FatTailFold(60);
        at99.RemoveAt(0);                       // remove a +4.0 WINNER: strictly less profit

        Assert.Equal(100, at100.Count);
        Assert.Equal(99,  at99.Count);

        // Both sides of the old gate now charge for the same fat tail, to within one ramp step.
        double p100 = FoldScoreHelper.CVaRPenalty(at100, cfg);
        double p99  = FoldScoreHelper.CVaRPenalty(at99,  cfg);
        Assert.Equal(0.700, p100, 9);
        Assert.Equal(0.705, p99,  9);           // was exactly 1.0 — the cliff
        Assert.True(p99 - p100 < 0.01, $"tail penalty still steps by {p99 - p100} at the gate");

        double s100 = FoldScoreHelper.Canonical(at100, PosFrac, MinTrades, cfg);
        double s99  = FoldScoreHelper.Canonical(at99,  PosFrac, MinTrades, cfg);

        Assert.True(s99 < s100,
            $"TAIL-GATE CLIFF: giving up a winning trade to fall from 100 to 99 trades " +
            $"moved the fold score {s100} -> {s99} ({(s99 / s100 - 1) * 100:F1}%)");
    }

    [Fact]
    public void Canonical_AddingAWinningTrade_NeverLowersTheFoldScore_AcrossTheWholeRamp()
    {
        // The general form of the defect: anywhere in 1..200 trades, one more winner must not
        // cost the genotype anything. The old gate broke this at exactly one point.
        var cfg = new FitnessConfig();
        double prev = double.NegativeInfinity;
        for (int wins = 1; wins <= 200; wins++)
        {
            double s = FoldScoreHelper.Canonical(FatTailFold(wins), PosFrac, MinTrades, cfg);
            Assert.True(s >= prev - 1e-12,
                $"adding a winning trade LOWERED the fold score at n = {wins + 40} ({prev} -> {s})");
            prev = s;
        }
    }

    [Fact]
    public void TailTermWeight_IsContinuousMonotoneAndMeetsBothFlatRegions()
    {
        Assert.Equal(40,  FoldScoreHelper.TailRampLo);
        Assert.Equal(100, FoldScoreHelper.TailRampHi);
        Assert.Equal(FoldScoreHelper.MinTailSampleSize, FoldScoreHelper.TailRampHi);

        // Flat and neutral at and below the low bound — no step where the ramp starts.
        for (int n = -5; n <= FoldScoreHelper.TailRampLo; n++)
            Assert.Equal(0.0, FoldScoreHelper.TailTermWeight(n), 12);

        // Flat and fully live at and above the high bound — no step where it ends.
        for (int n = FoldScoreHelper.TailRampHi; n <= FoldScoreHelper.TailRampHi + 500; n += 7)
            Assert.Equal(1.0, FoldScoreHelper.TailTermWeight(n), 12);

        // Monotone in between, and the largest single-trade step is one ramp increment.
        double maxStep = 0.0, prev = 0.0;
        for (int n = 0; n <= 200; n++)
        {
            double w = FoldScoreHelper.TailTermWeight(n);
            Assert.InRange(w, 0.0, 1.0);
            Assert.True(w >= prev - 1e-12, $"weight fell at n = {n}");
            maxStep = Math.Max(maxStep, w - prev);
            prev = w;
        }
        Assert.Equal(1.0 / (FoldScoreHelper.TailRampHi - FoldScoreHelper.TailRampLo), maxStep, 9);
    }

    [Fact]
    public void TailTerms_AreFullyNeutralWhereTheEstimatorIsASingleObservation()
    {
        // Below TailRampLo the 1-in-20 bucket holds one trade, so "expected shortfall at 5%"
        // is the single worst trade and "p95/p5" is best-single / worst-single. Both terms
        // must be exactly 1.0 there — the historical behaviour, preserved.
        var cfg = new FitnessConfig();
        for (int n = 20; n <= FoldScoreHelper.TailRampLo; n += 4)
        {
            var r = new List<double>();
            for (int i = 0; i < n - 1; i++) r.Add(1.0);
            r.Add(-40.0);                        // a brutal one-trade tail
            Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(r, cfg), 12);

            var g = new List<double>();
            for (int i = 0; i < n - 1; i++) g.Add(i % 3 == 0 ? -1.0 : 1.0);
            g.Add(120.0);                        // one enormous winner
            Assert.Equal(1.0, FoldScoreHelper.TailRatioBonus(g, cfg), 12);
        }
    }

    [Fact]
    public void TailTerms_AreFullyLiveAtAndAboveMinTailSampleSize()
    {
        // At and above the high bound the terms are exactly what they were before the ramp,
        // so nothing that already cleared the gate has moved.
        var cfg = new FitnessConfig();

        var fat = new List<double>();
        for (int i = 0; i < 190; i++) fat.Add(1.0);
        for (int i = 0; i < 10; i++)  fat.Add(-30.0);
        Assert.Equal(Math.Clamp(1.0 - 1.0 * cfg.CVaRW, 0.5, 1.0), FoldScoreHelper.CVaRPenalty(fat, cfg), 12);

        var skew = new List<double>();
        for (int i = 0; i < 190; i++) skew.Add(i % 4 == 0 ? -1.0 : 1.0);
        for (int i = 0; i < 10; i++)  skew.Add(50.0);
        double expected = 1.0 + (FoldScoreHelper.MaxTailRatio - 1.5) * 0.1 * cfg.TailRatioW;
        Assert.Equal(expected, FoldScoreHelper.TailRatioBonus(skew, cfg), 9);
    }

    [Fact]
    public void TailTerms_StayInsideTheirRanges_AtEveryPointOnTheRamp()
    {
        // The blend must not let either term escape the range its clamp guarantees — a
        // CVaRPenalty at or below 0 would zero or flip the sign of the whole fold score.
        var cfg = new FitnessConfig() with { CVaRW = 5.0, TailRatioW = 50.0 };
        for (int n = 10; n <= 200; n++)
        {
            var r = new List<double>();
            int tail = Math.Max(1, n / 20);
            for (int i = 0; i < n - tail; i++) r.Add(1.0);
            for (int i = 0; i < tail; i++)     r.Add(-500.0);
            Assert.InRange(FoldScoreHelper.CVaRPenalty(r, cfg), 0.5, 1.0);

            var g = new List<double>();
            for (int i = 0; i < n - tail; i++) g.Add(i % 4 == 0 ? -0.1 : 0.2);
            for (int i = 0; i < tail; i++)     g.Add(500.0);
            Assert.InRange(FoldScoreHelper.TailRatioBonus(g, cfg), 1.0, FoldScoreHelper.MaxTailRatioBonus);
        }
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

        // HALF frequency bonus, reinstated from 0.
        //
        // The original FreqW = 0 was justified as "grid session count is driven by coin
        // volatility, not strategy quality, so a frequency bonus would just rank coins". Evidence
        // now contradicts it: all four of Grid's entry-gate genes trained to their PERMISSIVE
        // bounds (AdxThreshold 20/20, BbWidthMaxPct 2.499/2.5, BbPeriod 10/10, EmaPeriod 10/10).
        // Session count is gene-controlled — the GA was straining to fire more often and the box
        // stopped it, not coin volatility.
        //
        // Held at half rather than full because the original concern is not baseless: a volatile
        // coin genuinely does produce more sessions regardless of genotype. Half lets the GA buy
        // volume without letting coin selection dominate the fold score.
        Assert.Equal(0.5, g.FreqW, 12);
        Assert.Equal(0.5, FoldScoreHelper.GridShape(new FitnessConfig()).FreqW, 12);
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
    public void GridShape_KeepsTheQualityTermOff_AsACalibrationChoice()
    {
        // The ORIGINAL reason was soundness: rrMult = (rr - 1)/1.5 is negative over a grid's
        // whole operating range (many small mean-reversion fills against an occasional
        // whole-ladder stop-out => rr < 1.0), so the term was pinned at a constant 0 and
        // enabling it would have handed the Grid GA a flat fitness landscape. That reason is
        // gone — RrMultiplier is strictly positive and strictly increasing on (0, inf).
        //
        // It stays off as a CALIBRATION choice: RrNeutral = 2.5 makes the term a no-op at a
        // payoff ratio a grid never reaches, so switching it on would multiply every grid
        // genotype by roughly the same sub-1 factor — a scale shift, not a signal — and would
        // make Grid fitness incomparable with the genotypes trained before it.
        Assert.Equal(0.0, FoldScoreHelper.GridShape(Neutral).QualityW, 12);

        // Demonstrate the operating range: high win rate, payoff below 1, pf comfortably
        // above 1 — the region the old ramp could not express.
        var gridLike = new List<double>();
        for (int i = 0; i < 60; i++) gridLike.Add(i % 5 == 0 ? -2.0 : 0.8);
        double wins = gridLike.Count(r => r > 0), losses = gridLike.Count(r => r <= 0);
        double rr = (gridLike.Where(r => r > 0).Sum() / wins) / Math.Abs(gridLike.Where(r => r <= 0).Sum() / losses);
        Assert.True(rr < 1.0, $"fixture should sit below rr 1.0, got {rr}");
        Assert.True(Simulator.ProfitFactor(gridLike) > 1.0);
        Assert.True(FoldScoreHelper.RrMultiplier(rr) > 0.0, "the rr leg must carry a value below rr = 1");

        // With the quality term off the score is a real, positive, non-NaN number.
        double score = FoldScoreHelper.Canonical(
            gridLike, 0.03, 10, FoldScoreHelper.GridShape(Neutral), 1.0, statBonusCeiling: 1.0);
        Assert.False(double.IsNaN(score));
        Assert.True(score > 0);
    }

    [Fact]
    public void Canonical_RanksOnQuality_WhenRiskRewardIsBelowOne()
    {
        // REPLACES Canonical_DoesNotReturnNaN_WhenRiskRewardIsBelowOne, which asserted only
        // `!IsNaN && score >= 0.0`. This fixture's actual value was EXACTLY 0.0, so the
        // assertion was satisfied BY the collapse it was meant to catch — it could not
        // distinguish "guarded" from "zeroed". The corrected behaviour is that the fold is
        // scored, not annihilated, and that quality still ORDERS folds inside rr < 1.
        var r = new List<double>();
        for (int i = 0; i < 60; i++) r.Add(i % 5 == 0 ? -2.0 : 0.8);   // pf 1.60, rr 0.40

        double score = FoldScoreHelper.Canonical(r, 0.03, 10, Neutral);
        Assert.False(double.IsNaN(score), "rr < 1.0 must not produce a NaN fold score");
        Assert.True(score > 0.0, $"rr < 1.0 must not annihilate the fold score (got {score})");

        // A strictly better fold at the same trade count and the same sub-1 payoff ratio must
        // score strictly higher — the gradient the 0-floor plateau destroyed.
        var better = new List<double>();
        for (int i = 0; i < 60; i++) better.Add(i % 5 == 0 ? -2.0 : 1.2);   // same rr shape, higher pf
        Assert.True(FoldScoreHelper.Canonical(better, 0.03, 10, Neutral) > score);

        // And a strictly worse one must score strictly lower.
        var worse = new List<double>();
        for (int i = 0; i < 60; i++) worse.Add(i % 5 == 0 ? -2.0 : 0.55);
        Assert.True(FoldScoreHelper.Canonical(worse, 0.03, 10, Neutral) < score);
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
        // null = no per-trade excursions supplied, which must reproduce the pre-MAE score exactly.
        double actual = (double)method.Invoke(null, [returns, Neutral, 1.0, null])!;

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
        double Score(FitnessConfig c) => (double)method.Invoke(null, [returns, c, 1.0, null])!;

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
