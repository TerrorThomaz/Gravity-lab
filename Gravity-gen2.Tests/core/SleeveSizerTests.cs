using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// SleeveSizer mirrors research/cov_sizing.py. The reference numbers below are the Python
// implementation's output on the same covariance (corr_shrink, erc_weights): if either side
// changes, both break.
public class SleeveSizerTests
{
    private static readonly double[] Cov =
    {
        0.0004,   0.00003,   -0.00001,
        0.00003,  0.0000196,  0.000002,
        -0.00001, 0.000002,   0.0025,
    };

    [Fact]
    public void CorrShrink_KeepsVariances_ShrinksCovariances()
    {
        var c = SleeveSizer.CorrShrink(Cov, 3, 0.3);
        double[] py = { 4.00e-04, 2.10e-05, -7.00e-06, 2.10e-05, 1.96e-05, 1.40e-06, -7.00e-06, 1.40e-06, 2.50e-03 };
        for (int i = 0; i < 9; i++) Assert.Equal(py[i], c[i], 12);
    }

    [Fact]
    public void Erc_MatchesThePythonMirror()
    {
        var corr = SleeveSizer.RiskWeights(Cov, 3, SleeveSizer.Method.ErcCorrShrink);
        Assert.Equal(0.16870466, corr[0], 5);
        Assert.Equal(0.75650117, corr[1], 5);
        Assert.Equal(0.07479417, corr[2], 5);
        var md = SleeveSizer.RiskWeights(Cov, 3, SleeveSizer.Method.ErcMeanDiagShrink);
        Assert.Equal(0.344358, md[0], 5);
        Assert.Equal(0.46905029, md[1], 5);
        Assert.Equal(0.1865917, md[2], 5);
    }

    private static (DateTime[] days, double[][] pnl, double[][] gross) Fixture(int n = 200)
    {
        var rng = new Random(3);
        var days = Enumerable.Range(0, n).Select(i => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)).ToArray();
        double[] Noise(double sd) => Enumerable.Range(0, n).Select(_ => sd * (rng.NextDouble() - 0.5)).ToArray();
        var pnl = new[] { Noise(0.02), Noise(0.002) };
        var gross = new[] { Enumerable.Repeat(1.0, n).ToArray(), Enumerable.Range(0, n).Select(i => i == 150 ? 0.6 : 0.1).ToArray() };
        return (days, pnl, gross);
    }

    [Fact]
    public void CapitalSplit_SumsToOne_AndNeverLevers()
    {
        var (days, pnl, gross) = Fixture();
        var r = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.InverseVol, scaleToGross: false);
        foreach (var w in r.Weights) Assert.Equal(1.0, w.Sum(), 9);
        Assert.All(r.Gross, g => Assert.True(g <= 1.0 + 1e-12));
    }

    [Fact]
    public void ScaledBook_HardCapHoldsOnAFlushTheTrailingWindowMissed()
    {
        var (days, pnl, gross) = Fixture();
        var r = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.InverseVol, scaleToGross: true);
        Assert.All(r.Gross, g => Assert.True(g <= 1.0 + 1e-12));                 // day 150's spike is cut
        Assert.True(r.Weights[120].Sum() > 1.0, "scaling should put idle capital to work");
        var capped = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.InverseVol, scaleToGross: true, maxWeight: 1.0);
        Assert.All(capped.Weights, w => Assert.All(w, x => Assert.True(x <= 1.0 + 1e-12)));
    }

    [Fact]
    public void Weights_UseOnlyDaysBeforeTheMonth()
    {
        var (days, pnl, gross) = Fixture();
        var a = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.ErcCorrShrink, scaleToGross: true);
        int firstOfMay = Array.FindIndex(days, d => d.Month == 5 && d.Day == 1);
        pnl[0][firstOfMay + 5] = 0.5;                                              // a shock inside May
        var b = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.ErcCorrShrink, scaleToGross: true);
        Assert.Equal(a.Weights[firstOfMay + 10], b.Weights[firstOfMay + 10]);       // May weights unchanged
    }

    [Fact]
    public void Alpha_UncorrelatedSleeves_AreSizedByMeanOverVariance()
    {
        var w = SleeveSizer.AlphaWeights(new[] { 0.0004, 0, 0, 0.0001 }, new[] { 0.002, 0.001 }, 2);
        double a = 0.002 / 0.0004, b = 0.001 / 0.0001;
        Assert.Equal(a / (a + b), w[0], 9);
        Assert.Equal(b / (a + b), w[1], 9);
    }

    [Fact]
    public void Alpha_ZeroMeanHedgeEarnsWeight_ZeroMeanUncorrelatedDoesNot()
    {
        double[] mu = { 0.002, 0.0 };
        var hedge = SleeveSizer.AlphaWeights(new[] { 0.0004, -0.0001, -0.0001, 0.0001 }, mu, 2);
        Assert.True(hedge[1] > 0.1, $"a hedge of the earning sleeve should be held, got {hedge[1]}");
        var idle = SleeveSizer.AlphaWeights(new[] { 0.0004, 0.0, 0.0, 0.0001 }, mu, 2);
        Assert.Equal(0.0, idle[1], 12);
    }

    [Fact]
    public void Alpha_NothingPositive_FallsBackToInverseVol()
    {
        double[] cov = { 0.0004, 0, 0, 0.0001 };
        Assert.Equal(RiskParity.InverseVol(cov, 2), SleeveSizer.AlphaWeights(cov, new[] { -0.001, -0.002 }, 2));
    }

    [Fact]
    public void SharpeShrink_NoEvidenceIsCommonSharpe_LotsIsOwnSharpe()
    {
        double[] sr = { 0.2, 0.0 };
        Assert.All(SleeveSizer.SharpeShrink(sr, 0, 365), p => Assert.Equal(0.1, p, 12));
        var many = SleeveSizer.SharpeShrink(sr, 1_000_000, 365);
        Assert.Equal(0.2, many[0], 3);
        Assert.Equal(0.0, many[1], 3);
    }

    [Fact]
    public void Alpha_WeightsUseOnlyDaysBeforeTheMonth()
    {
        var (days, pnl, gross) = Fixture(400);
        for (int i = 0; i < 400; i++) pnl[1][i] += 0.0005;                          // give one sleeve a mean
        var a = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.Alpha, scaleToGross: true);
        int firstOfDec = Array.FindIndex(days, d => d.Month == 12 && d.Day == 1);
        pnl[0][firstOfDec + 5] = 0.5;
        var b = SleeveSizer.Size(days, pnl, gross, SleeveSizer.Method.Alpha, scaleToGross: true);
        Assert.Equal(a.Weights[firstOfDec + 10], b.Weights[firstOfDec + 10]);
        Assert.NotEqual(a.Weights[firstOfDec + 10], a.Weights[firstOfDec - 40]);   // and it does move monthly
    }
}
