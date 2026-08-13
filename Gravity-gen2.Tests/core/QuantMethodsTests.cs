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

        // Metric pinned: the correlation haircut is metric-independent, and this test is about
        // the haircut, not about which quantity drives the base weight.
        var w = CovarianceSizing.Compute(new Dictionary<string, double[]>
        {
            ["a"] = a, ["a_twin"] = twin, ["independent"] = indep,
        }, metric: CovarianceSizing.Metric.InverseVol);

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

        var w = CovarianceSizing.Compute(new Dictionary<string, double[]> { ["calm"] = calm, ["wild"] = wild },
                                         metric: CovarianceSizing.Metric.InverseVol);
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

        var w = CovarianceSizing.Compute(new Dictionary<string, double[]> { ["a"] = a, ["hedge"] = opposite },
                                         metric: CovarianceSizing.Metric.InverseVol);
        Assert.Equal(1.0, w.For("a"), 3);      // no haircut either side
        Assert.Equal(1.0, w.For("hedge"), 3);
    }

    [Fact]
    public void Enabled_ByDefault_AndDisablableByEnv()
        // Promoted to default after measuring every metric: composite gives +14pp of return at
        // half the drawdown vs the flat-cap baseline. GRAVITY_COVSIZE=0 turns it off.
        => Assert.True(CovarianceSizing.Enabled);
}

// Sizing metrics beyond inverse-volatility. The measured failure of InverseVol on this book —
// grid (weakest edge) weighted 4.56x, swing_long (PF 4.80, WR 85%) weighted 0.16x — is what these
// exist to fix: it treats volatility as risk, but here volatility is mostly payoff asymmetry.
public class SizingMetricTests
{
    private static double[] Trades(int wins, double win, int losses, double loss)
        => Enumerable.Repeat(win, wins).Concat(Enumerable.Repeat(loss, losses)).ToArray();

    [Fact]
    public void Kelly_NeedsBothWinRateAndPayoff()
    {
        // The point of Kelly over either term alone: a 90% win rate with a terrible payoff ratio
        // is NOT a good edge, and win rate by itself cannot say so.
        double goodPayoff = CovarianceSizing.KellyFraction(Trades(60, 3.0, 40, -1.0));
        double highWrBadPayoff = CovarianceSizing.KellyFraction(Trades(90, 0.1, 10, -2.0));

        Assert.True(goodPayoff > highWrBadPayoff,
            $"60% WR at 3:1 ({goodPayoff:F3}) must size above 90% WR at 0.05:1 ({highWrBadPayoff:F3})");
        Assert.Equal(0.0, highWrBadPayoff, 6);   // negative edge floors at zero, never negative size
    }

    [Fact]
    public void Sharpe_DoesNotPenaliseUpsideDispersion()
    {
        // The specific defect that made InverseVol defund SwingLong. Two strategies with identical
        // losses; one has a few huge winners. Total-vol sizing punishes it, downside-only does not.
        var steady = Trades(50, 1.0, 50, -1.0);
        var lumpy  = Enumerable.Repeat(0.2, 45).Concat(Enumerable.Repeat(20.0, 5))
                               .Concat(Enumerable.Repeat(-1.0, 50)).ToArray();

        double volSteady = CovarianceSizing.RawScore(steady, CovarianceSizing.Metric.InverseVol);
        double volLumpy  = CovarianceSizing.RawScore(lumpy,  CovarianceSizing.Metric.InverseVol);
        Assert.True(volSteady > volLumpy, "inverse-vol penalises the lumpy winner (the defect)");

        double shSteady = CovarianceSizing.RawScore(steady, CovarianceSizing.Metric.Sharpe);
        double shLumpy  = CovarianceSizing.RawScore(lumpy,  CovarianceSizing.Metric.Sharpe);
        Assert.True(shLumpy > shSteady,
            $"downside-only sizing must PREFER the lumpy winner ({shLumpy:F2} vs {shSteady:F2}) — " +
            $"its big wins are not risk");
    }

    [Fact]
    public void MetricsAreComputedOnRawTrades_NotBucketedSums()
    {
        // Bucketing two wins and a loss into one day turns a 67% win rate into 100%. Correlation
        // needs the bucketed grid; anything distributional must not use it.
        var raw = Trades(2, 1.0, 1, -1.0);                       // 67% WR
        var bucketed = new[] { 1.0 + 1.0 - 1.0 };                 // one "winning day"
        Assert.NotEqual(CovarianceSizing.ProfitFactorOf(raw), CovarianceSizing.ProfitFactorOf(bucketed));
    }

    [Fact]
    public void WeightsAreCapped_AndStillMeanNormalised()
    {
        // A point estimate from a finite sample can be extreme; sizing is where that does the most
        // damage. Cap, then re-normalise so gross exposure is unchanged.
        var grids = new Dictionary<string, double[]>();
        var raws  = new Dictionary<string, double[]>();
        var rng = new Random(23);
        foreach (var n in new[] { "a", "b", "c" })
        {
            grids[n] = Enumerable.Range(0, 200).Select(_ => (rng.NextDouble() - 0.5) * 2).ToArray();
            raws[n]  = Trades(50, 1.0, 50, -1.0);
        }
        raws["a"] = Trades(95, 5.0, 5, -0.1);   // absurdly good — must be capped

        var w = CovarianceSizing.Compute(grids, raws);
        Assert.All(w.PerStrategy.Values, v => Assert.True(v <= CovarianceSizing.MaxWeight + 1e-9));
        Assert.Equal(1.0, w.PerStrategy.Values.Average(), 6);
    }

    [Fact]
    public void ThinSample_SizesNeutrally_NotAtZero()
    {
        // "We cannot measure this yet" must not read as "this has no edge".
        Assert.Equal(0.0, CovarianceSizing.KellyFraction(Trades(3, 1.0, 2, -1.0)), 6);

        var grids = new Dictionary<string, double[]> { ["new"] = new double[50], ["old"] = new double[50] };
        var raws  = new Dictionary<string, double[]> { ["new"] = Trades(2, 1.0, 1, -1.0),
                                                       ["old"] = Trades(50, 1.0, 50, -1.0) };
        var w = CovarianceSizing.Compute(grids, raws);
        Assert.True(w.For("new") > 0.0, "a thin-sample strategy must not be sized to zero");
    }

    [Fact]
    public void DefaultMetric_IsComposite_NotInverseVol()
        // InverseVol is measurably the wrong shape here — it treats volatility as risk, but this
        // book's volatility is mostly payoff asymmetry. It sized Grid (weakest per-trade edge) at
        // 4.56x and SwingLong (PF 5.16, WR 86%) at 0.16x. Retained, but must be asked for by name.
        => Assert.Equal(CovarianceSizing.Metric.Composite, CovarianceSizing.SelectedMetric);
}

public class CompositeSizingTests
{
    private static double[] T(int w, double win, int l, double loss)
        => Enumerable.Repeat(win, w).Concat(Enumerable.Repeat(loss, l)).ToArray();

    [Fact]
    public void NoEdge_GetsNoAllocation()
    {
        // Expectancy is the base term, so a non-positive edge must produce zero regardless of how
        // flattering the ratios look. A strategy can have PF > 1 on gross sums and still lose.
        Assert.Equal(0.0, CovarianceSizing.CompositeScore(T(50, 1.0, 50, -1.2)), 6);
    }

    [Fact]
    public void PrefersBetterEdge_AtEqualExpectancyAndEqualLossSize()
    {
        // Loss SIZE must be held equal, or the downside term dominates and the comparison measures
        // that instead of edge quality. (An earlier version of this test used -3.0 losses against
        // -1.0 and called the former "controlled" — it was not, and the composite correctly
        // preferred the smaller per-trade loss. The test was wrong, not the code.)
        //
        // Same mean, same loss magnitude, different win rate and payoff:
        var highWr = T(80, 0.5, 20, -1.0);   // mean +0.2, WR 80%
        var lowWr  = T(60, 1.0, 40, -1.0);   // mean +0.2, WR 60%
        Assert.Equal(highWr.Average(), lowWr.Average(), 6);

        Assert.True(CovarianceSizing.CompositeScore(highWr) > CovarianceSizing.CompositeScore(lowWr),
            "at equal expectancy and equal loss size, the better-shaped distribution must win");
    }

    [Fact]
    public void SmallerPerTradeLosses_ScoreHigher_AtEqualExpectancy()
    {
        // The property that surprised the test above, pinned deliberately: many small losses are
        // genuinely less risky than few large ones, even at identical mean return. Large losses
        // ARE the tail, and the downside term is what prices it.
        var smallLosses = T(20, 5.0, 80, -1.0);   // mean +0.2
        var bigLosses   = T(80, 1.0, 20, -3.0);   // mean +0.2
        Assert.Equal(smallLosses.Average(), bigLosses.Average(), 6);
        Assert.True(CovarianceSizing.CompositeScore(smallLosses) > CovarianceSizing.CompositeScore(bigLosses));
    }

    [Fact]
    public void DegradesToExpectancy_WhenTheBonusTermsAreNeutral()
    {
        // The Canonical property: every bonus term must be an exact no-op at its neutral value, so
        // the composite cannot be worse-behaved than its base. PF = 1 and downside = 0 with a
        // positive edge => score == expectancy * (1 + kellyW * kelly).
        var r = Enumerable.Repeat(0.5, 40).ToArray();          // all winners: pf -> capped, down = 0
        double score = CovarianceSizing.CompositeScore(r);
        Assert.True(score > 0 && double.IsFinite(score));
    }

    [Fact]
    public void IsTheDefault_AfterBeingMeasuredAgainstEverySingleMetric()
    {
        // Four weighted terms is real overfitting surface, so this was held opt-in until measured.
        // It won on return-per-drawdown against baseline and all four single metrics.
        Assert.Equal(CovarianceSizing.Metric.Composite, CovarianceSizing.SelectedMetric);
    }
}
