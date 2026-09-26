using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// CovarianceMatrix's eigen solver used to pick the largest off-diagonal each iteration and stop
// after 100 of them. At k=8 (28 off-diagonals) that converges; at k=190 (17,955 off-diagonals) it
// returns unconverged diagonal entries and reports them as eigenvalues, with no error and no
// warning. These tests pin correctness at small k — where the previous behaviour must be preserved,
// because CombinedBacktest prints EffectiveBets from it — and convergence at a k large enough that
// the old iteration cap could not have reached it.
public class CovarianceMatrixTests
{
    // Identity: k orthogonal unit-variance assets. Every eigenvalue is 1 and effective bets is k.
    [Fact]
    public void Eigenvalues_Identity_AreAllOne()
    {
        const int k = 6;
        var I = Diag(k, 1.0);
        var ev = CovarianceMatrix.Eigenvalues(I, k);

        Assert.Equal(k, ev.Length);
        Assert.All(ev, e => Assert.Equal(1.0, e, 10));
        Assert.Equal(k, CovarianceMatrix.EffectiveBets(I, k), 6);
    }

    // Closed form: [[2,1],[1,2]] has eigenvalues 3 and 1.
    [Fact]
    public void Eigenvalues_TwoByTwo_MatchTheClosedForm()
    {
        var ev = CovarianceMatrix.Eigenvalues(new[] { 2.0, 1.0, 1.0, 2.0 }, 2);
        Assert.Equal(3.0, ev[0], 10);
        Assert.Equal(1.0, ev[1], 10);
    }

    [Fact]
    public void Eigenvalues_AreReturnedDescending()
    {
        var ev = CovarianceMatrix.Eigenvalues(new[] { 1.0, 0.8, 0.8, 4.0 }, 2);
        Assert.True(ev[0] >= ev[1]);
    }

    // A correlation matrix with every off-diagonal ρ has one eigenvalue 1+(k−1)ρ and k−1 of 1−ρ.
    // Trace is preserved at k either way, so this catches a solver that has stopped early: an
    // unconverged run leaves mass on the off-diagonals and the top eigenvalue comes back short.
    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(120)]
    public void Eigenvalues_EquicorrelationMatrix_MatchTheClosedFormAtEveryK(int k)
    {
        const double rho = 0.6;
        var m = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++) m[a * k + b] = a == b ? 1.0 : rho;

        var ev = CovarianceMatrix.Eigenvalues(m, k);

        Assert.Equal(1.0 + (k - 1) * rho, ev[0], 6);
        for (int i = 1; i < k; i++) Assert.Equal(1.0 - rho, ev[i], 6);
        Assert.Equal(k, ev.Sum(), 6);   // trace is invariant under rotation
    }

    // The statistic the diagnostic exists to report. Perfectly correlated assets are one bet
    // however many of them there are; orthogonal assets are k bets.
    [Fact]
    public void EffectiveBets_CollapsesToOneWhenEverythingIsPerfectlyCorrelated()
    {
        const int k = 50;
        var m = new double[k * k];
        for (int i = 0; i < k * k; i++) m[i] = 1.0;

        Assert.Equal(1.0, CovarianceMatrix.EffectiveBets(m, k), 4);
    }

    [Fact]
    public void EffectiveBets_IsBetweenOneAndK_ForAPartiallyCorrelatedBook()
    {
        const int k = 30;
        const double rho = 0.5;
        var m = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++) m[a * k + b] = a == b ? 1.0 : rho;

        double eb = CovarianceMatrix.EffectiveBets(m, k);
        Assert.InRange(eb, 1.0, k);
        Assert.True(eb < k / 2.0, $"a book correlated at {rho} should carry well under k/2 bets, got {eb:F2}");
    }

    // Eigenvectors must actually solve Σv = λv, not merely be an orthogonal basis.
    [Fact]
    public void EigenDecompose_VectorsSatisfyTheEigenEquation()
    {
        const int k = 5;
        var m = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++) m[a * k + b] = a == b ? 2.0 : 0.3;

        var dec = CovarianceMatrix.EigenDecompose(m, k);
        Assert.NotNull(dec);
        var (values, vectors) = dec!.Value;

        for (int j = 0; j < k; j++)
            for (int i = 0; i < k; i++)
            {
                double mv = 0;
                for (int b = 0; b < k; b++) mv += m[i * k + b] * vectors[b * k + j];
                Assert.Equal(values[j] * vectors[i * k + j], mv, 8);
            }
    }

    [Fact]
    public void EigenDecompose_RejectsDegenerateInput()
    {
        Assert.Null(CovarianceMatrix.EigenDecompose(null, 3));
        Assert.Null(CovarianceMatrix.EigenDecompose(new double[] { 1, 0, 0, 1 }, 3));   // too short
    }

    // ── Ledoit-Wolf analytic intensity ──────────────────────────────────────────────────────

    [Fact]
    public void ShrinkAuto_IntensityIsAProportionAndTraceIsPreserved()
    {
        var rng = new Random(7);
        int k = 12, n = 200;
        var series = new double[k][];
        for (int a = 0; a < k; a++)
        {
            series[a] = new double[n];
            for (int i = 0; i < n; i++) series[a][i] = rng.NextDouble() - 0.5;
        }

        var res = CovarianceMatrix.ShrinkAuto(series);
        Assert.NotNull(res);
        var (cov, lambda) = res!.Value;

        Assert.InRange(lambda, 0.0, 1.0);

        // Shrinking toward mean(diag)·I leaves the trace unchanged by construction.
        double trace = 0;
        for (int a = 0; a < k; a++) trace += cov[a * k + a];
        var sample = CovarianceMatrix.Sample(series)!;
        double sampleTrace = 0;
        for (int a = 0; a < k; a++) sampleTrace += sample[a * k + a];
        // Sample() uses 1/(n−1) and ShrinkAuto uses 1/n, so compare after rescaling.
        Assert.Equal(sampleTrace * (n - 1) / (double)n, trace, 8);
    }

    // The regime this exists for: few observations against many assets. The intensity is
    // (estimation error)/(true dispersion about the target); with a real factor structure the
    // denominator is a property of the data and the numerator falls as 1/n, so lambda must drop as
    // observations accumulate. A one-factor book with dispersed betas is used rather than iid noise
    // precisely because iid data IS the shrinkage target — there the denominator is noise too, both
    // sides pin near 1.0, and the test would measure nothing.
    [Fact]
    public void ShrinkAuto_ShrinksHarderWhenObservationsAreScarce()
    {
        static double Lambda(int k, int n, int seed)
        {
            var rng = new Random(seed);
            var series = new double[k][];
            for (int a = 0; a < k; a++) series[a] = new double[n];

            for (int i = 0; i < n; i++)
            {
                double factor = rng.NextDouble() - 0.5;          // the common market move
                for (int a = 0; a < k; a++)
                {
                    double beta = 0.25 + 1.5 * a / (double)k;    // dispersed loadings
                    series[a][i] = beta * factor + 0.10 * (rng.NextDouble() - 0.5);
                }
            }
            return CovarianceMatrix.ShrinkAuto(series)!.Value.Lambda;
        }

        double scarce = Lambda(20, 40, 1);
        double ample  = Lambda(20, 4000, 1);
        Assert.True(scarce > ample,
            $"40 observations must shrink harder than 4000 (got {scarce:F4} vs {ample:F4})");
    }

    [Fact]
    public void ShrinkAuto_RejectsRaggedOrTooShortInput()
    {
        Assert.Null(CovarianceMatrix.ShrinkAuto(new[] { new double[] { 1, 2, 3 }, new double[] { 1, 2 } }));
        Assert.Null(CovarianceMatrix.ShrinkAuto(new[] { new double[] { 1 }, new double[] { 2 } }));
        Assert.Null(CovarianceMatrix.ShrinkAuto(Array.Empty<double[]>()));
    }

    private static double[] Diag(int k, double v)
    {
        var m = new double[k * k];
        for (int i = 0; i < k; i++) m[i * k + i] = v;
        return m;
    }
}
