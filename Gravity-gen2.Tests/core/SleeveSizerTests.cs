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
}
