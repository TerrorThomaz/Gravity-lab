using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class BetaDecompositionTests
{
    [Fact]
    public void PureBetaStrategy_ShowsBetaOne_AndNoAlpha()
    {
        // A strategy that is literally 1x BTC must report beta 1, alpha 0, R^2 1 — and the verdict
        // must say so. This is the case the repo currently cannot detect: raw returns look great
        // in a bull run and nothing distinguishes them from skill.
        var rng = new Random(7);
        var btc = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.45) * 4).ToArray();
        var strat = btc.ToArray();

        var r = BetaDecomposition.Regress(strat, btc)!;
        Assert.Equal(1.0, r.Beta, 6);
        Assert.Equal(0.0, r.Alpha, 6);
        Assert.Equal(1.0, r.RSquared, 6);
        Assert.False(r.IsMarketNeutral);
    }

    [Fact]
    public void MarketNeutralStrategyWithEdge_ShowsAlpha_AndNearZeroBeta()
    {
        // Constant +0.5% per period, independent of BTC. beta ~ 0, alpha ~ 0.5, and the t-stat
        // must clear 2 so the verdict reads ALPHA rather than INCONCLUSIVE.
        var rng = new Random(11);
        var btc = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 6).ToArray();
        var strat = Enumerable.Range(0, 200).Select(i => 0.5 + (rng.NextDouble() - 0.5) * 0.1).ToArray();

        var r = BetaDecomposition.Regress(strat, btc)!;
        Assert.InRange(r.Beta, -0.2, 0.2);
        Assert.InRange(r.Alpha, 0.4, 0.6);
        Assert.True(r.AlphaIsSignificant, $"t={r.AlphaTStat:F2} should clear 2 for a constant edge");
        Assert.True(r.IsMarketNeutral);
        Assert.StartsWith("ALPHA", r.Verdict);
    }

    [Fact]
    public void LeveredLongStrategy_IsFlaggedAsBeta_NotEdge()
    {
        // 2x BTC with a little noise: large returns in a bull run, zero skill. The verdict must
        // NOT reward it.
        var rng = new Random(13);
        var btc = Enumerable.Range(0, 300).Select(_ => (rng.NextDouble() - 0.40) * 5).ToArray();
        var strat = btc.Select(b => 2.0 * b + (rng.NextDouble() - 0.5) * 0.2).ToArray();

        var r = BetaDecomposition.Regress(strat, btc)!;
        Assert.InRange(r.Beta, 1.8, 2.2);
        Assert.False(r.IsMarketNeutral);
        Assert.Contains("BETA", r.Verdict);
    }

    [Fact]
    public void TooFewObservations_ReturnsNull_RatherThanAConfidentNumber()
        => Assert.Null(BetaDecomposition.Regress(new double[5], new double[5]));
}

public class PurgedKFoldTests
{
    [Fact]
    public void TrainTradeOverlappingTestWindow_IsPurged()
    {
        // The leak this exists to stop: a trade that opens before the test window and is still
        // open inside it has an outcome partly determined by test-window prices.
        var s = PurgedKFold.MakeSplit(length: 1000, k: 5, testFold: 2, embargoPct: 0.0);
        Assert.Equal(400, s.TestStart);
        Assert.Equal(600, s.TestEnd);

        Assert.False(PurgedKFold.IsTrainAdmissible(entryBar: 380, exitBar: 420, s),
            "a trade spanning the test boundary must be purged");
        Assert.False(PurgedKFold.IsTrainAdmissible(entryBar: 590, exitBar: 700, s),
            "a trade opening inside the test window is not training data");
        Assert.True(PurgedKFold.IsTrainAdmissible(entryBar: 100, exitBar: 200, s),
            "a trade wholly before the test window is admissible");
        Assert.True(PurgedKFold.IsTrainAdmissible(entryBar: 700, exitBar: 800, s),
            "a trade wholly after, with no embargo, is admissible");
    }

    [Fact]
    public void EmbargoRejectsTradesImmediatelyAfterTheTestWindow()
    {
        // Serial correlation: bars just after the test window still carry information about it,
        // which purging alone does not remove.
        var s = PurgedKFold.MakeSplit(length: 1000, k: 5, testFold: 2, embargoPct: 0.05);
        Assert.Equal(650, s.EmbargoEnd);   // 600 + 5% of 1000

        Assert.False(PurgedKFold.IsTrainAdmissible(entryBar: 620, exitBar: 640, s),
            "a trade opening inside the embargo tail must be dropped");
        Assert.True(PurgedKFold.IsTrainAdmissible(entryBar: 660, exitBar: 700, s),
            "past the embargo it is admissible again");
    }

    [Fact]
    public void LongHoldingPeriods_PurgeMoreOfTheTrainingSet()
    {
        // Quantifies the contamination the unpurged scheme carried. This repo's holds run to 250
        // h1 bars, so a naive fold boundary leaks over ten days of test information.
        var s = PurgedKFold.MakeSplit(1000, 5, 2, 0.0);
        var shortHolds = Enumerable.Range(0, 100).Select(i => (i * 10, i * 10 + 5)).ToList();
        var longHolds  = Enumerable.Range(0, 100).Select(i => (i * 10, i * 10 + 250)).ToList();

        var rShort = PurgedKFold.Partition(shortHolds, s);
        var rLong  = PurgedKFold.Partition(longHolds,  s);

        Assert.True(rLong.PurgedOverlap > rShort.PurgedOverlap * 3,
            $"long holds ({rLong.PurgedOverlap}) must purge far more than short ones " +
            $"({rShort.PurgedOverlap}) — that gap IS the leakage the old scheme carried");
    }

    [Fact]
    public void LastFold_AbsorbsTheRemainder_SoNoBarsAreOrphaned()
    {
        var s = PurgedKFold.MakeSplit(length: 1003, k: 5, testFold: 4, embargoPct: 0.0);
        Assert.Equal(1003, s.TestEnd);
    }
}

public class CovarianceSizingTests
{
    [Fact]
    public void PerfectlyCorrelatedStrategies_AreDownweighted()
    {
        // The property flat count-based caps cannot express: two strategies that are the SAME bet
        // should not each get a full allocation.
        var rng = new Random(3);
        var a = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 2).ToArray();
        var twin = a.ToArray();
        var indep = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 2).ToArray();

        var w = CovarianceSizing.Compute(new Dictionary<string, double[]>
        {
            ["a"] = a, ["a_twin"] = twin, ["independent"] = indep,
        });

        Assert.True(w.For("independent") > w.For("a"),
            $"the uncorrelated strategy ({w.For("independent"):F3}) must outweigh a correlated one " +
            $"({w.For("a"):F3})");
    }

    [Fact]
    public void HigherVolatility_MeansLowerWeight()
    {
        var rng = new Random(5);
        var calm  = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 1).ToArray();
        var wild  = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 10).ToArray();

        var w = CovarianceSizing.Compute(new Dictionary<string, double[]> { ["calm"] = calm, ["wild"] = wild });
        Assert.True(w.For("calm") > w.For("wild"));
    }

    [Fact]
    public void WeightsAreMeanNormalised_SoGrossExposureIsUnchanged()
    {
        // This must REDISTRIBUTE size, not change it — otherwise it is silently entangled with the
        // exposure cap and neither can be measured on its own.
        var rng = new Random(9);
        var d = new Dictionary<string, double[]>();
        foreach (var n in new[] { "x", "y", "z" })
            d[n] = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 3).ToArray();

        var w = CovarianceSizing.Compute(d);
        Assert.Equal(1.0, w.PerStrategy.Values.Average(), 6);
    }

    [Fact]
    public void ThinOverlap_AssumesIndependence_RatherThanTrustingNoise()
    {
        var a = new double[10]; var b = new double[10];
        Assert.Equal(0.0, CovarianceSizing.Correlation(a, b));
    }

    [Fact]
    public void NegativeCorrelation_IsNotPenalised()
    {
        // A hedge should not be charged a correlation haircut — max(0, rho) exists for this.
        var rng = new Random(17);
        var a = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 2).ToArray();
        var opposite = a.Select(v => -v).ToArray();
        Assert.True(CovarianceSizing.Correlation(a, opposite) < -0.9);

        var w = CovarianceSizing.Compute(new Dictionary<string, double[]> { ["a"] = a, ["hedge"] = opposite });
        Assert.Equal(1.0, w.For("a"), 3);      // no haircut either side
        Assert.Equal(1.0, w.For("hedge"), 3);
    }

    [Fact]
    public void Disabled_ByDefault()
        => Assert.False(CovarianceSizing.Enabled, "covariance sizing changes every position size; opt-in");
}
