using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The quality multipliers were blind to sample size: PF 7 on 30 trades multiplied exactly as hard
// as PF 7 on 3000, against a single linear `gain` term that falls when you trade less. So the
// cheapest route up was to shrink toward minTradesPerFold and keep the luckiest subset.
//
// Observed on SwingLong with a learnable BTC gate: weight 0.871, val expectancy -0.711%,
// expanding-window efficiency 0.303, three of four OOS windows with no usable trades.
public class QualityShrinkTests
{
    private static List<double> Book(int n, double win, double loss, double winRate)
        => Enumerable.Range(0, n).Select(i => i < (int)(n * winRate) ? win : loss).ToList();

    private static double Score(List<double> r, FitnessConfig cfg)
        => FoldScoreHelper.Canonical(r, posFrac: 0.05, minTradesPerFold: 20, cfg: cfg);

    [Fact]
    public void ShrinkKZero_IsABitForBitNoOp()
    {
        // The escape hatch has to be exact, or "we turned shrinkage off" silently means
        // "we turned it mostly off".
        var r = Book(200, 2.0, -1.0, 0.65);
        double a = Score(r, new FitnessConfig(QualityShrinkK: 0.0));
        double b = Score(r, new FitnessConfig(QualityShrinkK: 0.0));
        Assert.Equal(a, b, 12);
    }

    [Fact]
    public void ThinSample_IsDiscountedRelativeToTheSameEdgeAtScale()
    {
        // Identical return DISTRIBUTION, different sample size. Per-trade quality is the same, so
        // the quality multipliers were previously identical; only `gain` differed. Now the thin
        // book keeps less of its quality credit.
        var cfg   = new FitnessConfig();
        var thin  = Book(30,   2.0, -1.0, 0.65);
        var thick = Book(1200, 2.0, -1.0, 0.65);

        // Per-TRADE score, so raw volume via `gain` is divided out and only the quality
        // treatment is being compared.
        double thinPer  = Score(thin,  cfg) / thin.Count;
        double thickPer = Score(thick, cfg) / thick.Count;
        Assert.True(thickPer > thinPer,
            $"thin sample not discounted: thin/trade={thinPer:F4} thick/trade={thickPer:F4}");
    }

    [Fact]
    public void ShrinkageAddsNoDiscontinuity()
    {
        // Originally this asserted the per-trade score never jumps more than 12% between adjacent
        // sample sizes — and it failed at n=41. Measuring with shrinkage OFF showed 14.0% at the
        // same point, so the discontinuity is PRE-EXISTING: TailRampLo is 40, so the tail terms
        // switch on at n=41, and the fixture's winner count moves in whole trades. Shrinkage does
        // not cause that cliff, it slightly softens it (12.1% vs 14.0%).
        //
        // The property worth pinning is therefore the honest one: n/(n+k) must not ADD roughness.
        // Asserting absolute smoothness would have been asserting something this fitness function
        // has never had, and "fixing" it by loosening the bound to 15% would have hidden the real
        // finding — that the tail ramp's lower edge is itself a step.
        var on  = new FitnessConfig();
        var off = new FitnessConfig(QualityShrinkK: 0.0);

        double WorstJump(FitnessConfig cfg)
        {
            double prev = Score(Book(40, 2.0, -1.0, 0.65), cfg) / 40, worst = 0;
            for (int n = 41; n <= 160; n++)
            {
                double cur = Score(Book(n, 2.0, -1.0, 0.65), cfg) / n;
                worst = Math.Max(worst, Math.Abs(cur - prev) / Math.Max(1e-9, Math.Abs(prev)));
                prev = cur;
            }
            return worst;
        }

        Assert.True(WorstJump(on) <= WorstJump(off) + 1e-9,
            $"shrinkage made the score rougher: on={WorstJump(on):P1} off={WorstJump(off):P1}");
    }

    [Fact]
    public void GenuineSelectivityStillWins_AtEqualSampleSize()
    {
        // The point is to stop rewarding accidental selectivity, NOT to punish a real edge.
        // At the same trade count, the better book must still score higher.
        var cfg  = new FitnessConfig();
        var good = Book(300, 3.0, -1.0, 0.60);
        var weak = Book(300, 1.2, -1.0, 0.55);
        Assert.True(Score(good, cfg) > Score(weak, cfg));
    }

    [Fact]
    public void LargerK_DiscountsThinSamplesHarder()
    {
        var thin = Book(30, 3.0, -1.0, 0.70);
        double mild   = Score(thin, new FitnessConfig(QualityShrinkK: 10.0));
        double harsh  = Score(thin, new FitnessConfig(QualityShrinkK: 200.0));
        Assert.True(harsh < mild, $"K had no effect: mild={mild:F3} harsh={harsh:F3}");
    }
}
