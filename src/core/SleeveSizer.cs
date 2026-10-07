namespace TradingGA;

// Book-level sizing across SLEEVES (carry, Grid, GridShort, trend, ...), from their daily P&L per
// unit of sleeve capital. Mirrors research/cov_sizing.py (parity: SleeveSizerTests).
//
// Monthly, from the `lookback` days strictly BEFORE the month:
//   1. risk weights, summing to 1: inverse vol, or ERC on a shrunk covariance — either the C# rule
//      (CovarianceMatrix.Shrink, toward mean(diag)·I, which inflates low-vol sleeves and pulls the
//      weights toward equal) or a correlation-only shrink that keeps each sleeve's own variance —
//      or Alpha (see AlphaWeights), the only one that asks what each sleeve EARNS;
//   2. optionally scaled up until the book's trailing PEAK gross notional, Σ w_i·g_i(d), reaches
//      the gross limit: a capital split leaves most of a grid sleeve's capital idle (mean gross ~0.2);
//   3. each weight capped at maxWeight (the freed budget is NOT redistributed: conservative).
// Daily, a hard cap: a day whose gross exceeds the limit is shrunk to it (P&L and gross alike), so the
// book never runs levered even when the trailing estimate under-predicted a flush.
public static class SleeveSizer
{
    public enum Method { InverseVol, ErcMeanDiagShrink, ErcCorrShrink, Alpha }

    public record Result(double[] Daily, double[] Gross, double[][] Weights);

    // Shrink correlations toward 0 by lambda; variances unchanged.
    public static double[] CorrShrink(double[] cov, int k, double lambda = 0.3)
    {
        var o = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
                o[a * k + b] = a == b ? cov[a * k + a] : (1.0 - lambda) * cov[a * k + b];
        return o;
    }

    public static double[] RiskWeights(double[] cov, int k, Method m, double lambda = 0.3) => m switch
    {
        Method.InverseVol        => RiskParity.InverseVol(cov, k),
        Method.ErcMeanDiagShrink => RiskParity.EqualRiskContribution(CovarianceMatrix.Shrink(cov, k, lambda) ?? cov, k, maxIter: 5000, tol: 1e-10).Weights,
        Method.ErcCorrShrink     => RiskParity.EqualRiskContribution(CorrShrink(cov, k, lambda), k, maxIter: 5000, tol: 1e-10).Weights,
        _ => throw new ArgumentException("Alpha needs means: use AlphaWeights or Size", nameof(m)),
    };

    // Alpha: w ∝ Σ⁻¹μ, clipped at 0. Component i of Σ⁻¹μ is exactly α_i / σ²_ε,i — sleeve i's alpha
    // against the best combination of the OTHER sleeves, over its residual variance (Treynor-Black).
    // So a low-Sharpe sleeve that is uncorrelated with, or hedges, the rest is not starved the way
    // ERC starves it (power check: ERC lowers book Sharpe for any uncorrelated sleeve with SR < ~0.67),
    // and a sleeve the others already replicate gets nothing however good it looks alone.
    // μ is the noisy part. A sleeve at Sharpe 1 reaches only t ≈ 1 in a year of daily data, so shrinking
    // each mean toward 0 by its own t zeroed every sleeve but the one that was briefly lucky (measured:
    // the 2022 book went ~85% trend at full gross, Sharpe 0.29, maxDD −35%). Instead SharpeShrink pulls
    // each sleeve's Sharpe toward the sleeves' COMMON Sharpe (empirical Bayes): with no evidence μ ∝ σ and
    // Σ⁻¹μ is the maximum-diversification portfolio; the alpha tilt grows only as evidence accumulates.
    // Σ is correlation-shrunk. Nothing positive → inverse vol.
    public static double[] AlphaWeights(double[] cov, double[] mu, int k, double lambda = 0.3)
    {
        var x = Solve(CorrShrink(cov, k, lambda), mu, k);
        var w = x?.Select(v => Math.Max(0.0, v)).ToArray();
        double sum = w?.Sum() ?? 0;
        return sum > 1e-300 ? w!.Select(v => v / sum).ToArray() : RiskParity.InverseVol(cov, k);
    }

    // Posterior Sharpe per sleeve: (n·SR_i + n0·mean(SR)) / (n + n0), a prior worth n0 days.
    public static double[] SharpeShrink(double[] sharpe, int n, double n0)
    {
        double bar = sharpe.Average();
        return sharpe.Select(s => (n * s + n0 * bar) / (n + n0)).ToArray();
    }

    // Gaussian elimination with partial pivoting; k is the number of sleeves (≤ ~5). Null if singular.
    private static double[]? Solve(double[] a, double[] b, int k)
    {
        var m = new double[k, k + 1];
        for (int i = 0; i < k; i++) { for (int j = 0; j < k; j++) m[i, j] = a[i * k + j]; m[i, k] = b[i]; }
        for (int c = 0; c < k; c++)
        {
            int p = c;
            for (int r = c + 1; r < k; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-300) return null;
            for (int j = c; j <= k; j++) (m[c, j], m[p, j]) = (m[p, j], m[c, j]);
            for (int r = 0; r < k; r++)
            {
                if (r == c) continue;
                double f = m[r, c] / m[c, c];
                for (int j = c; j <= k; j++) m[r, j] -= f * m[c, j];
            }
        }
        return Enumerable.Range(0, k).Select(i => m[i, k] / m[i, i]).ToArray();
    }

    // pnl[i][d], gross[i][d]: sleeve i on day d (aligned to `days`).
    public static Result Size(DateTime[] days, double[][] pnl, double[][] gross, Method m, bool scaleToGross,
                              double grossLimit = 1.0, double maxWeight = double.PositiveInfinity,
                              int lookback = 90, int minDays = 60, double lambda = 0.3,
                              double alphaPriorDays = 365)
    {
        int k = pnl.Length, n = days.Length;
        var daily = new double[n];
        var gr = new double[n];
        var weights = new double[n][];
        double[]? w = null;
        int month = -1;
        for (int d = 0; d < n; d++)
        {
            int key = days[d].Year * 12 + days[d].Month;
            if (key != month)
            {
                month = key;
                var start = new DateTime(days[d].Year, days[d].Month, 1, 0, 0, 0, days[d].Kind);
                var past = Enumerable.Range(0, d).Where(i => days[i] >= start.AddDays(-lookback)).ToArray();
                if (past.Length < minDays) w = Enumerable.Repeat(1.0 / k, k).ToArray();
                else
                {
                    var cov = CovarianceMatrix.Sample(pnl.Select(s => past.Select(i => s[i]).ToArray()).ToArray());
                    if (cov == null) w = Enumerable.Repeat(1.0 / k, k).ToArray();
                    else if (m == Method.Alpha)
                    {
                        // Sharpe from ALL days before the month (a 90-day mean is mostly noise); σ from Σ.
                        var sr = new double[k];
                        for (int s = 0; s < k; s++)
                        {
                            var x = pnl[s].Take(d).ToArray();
                            double mu = x.Average(), sd = Math.Sqrt(x.Sum(v => (v - mu) * (v - mu)) / Math.Max(1, x.Length - 1));
                            sr[s] = sd > 1e-15 ? mu / sd : 0.0;
                        }
                        var post = SharpeShrink(sr, d, alphaPriorDays);
                        w = AlphaWeights(cov, post.Select((p, s) => p * Math.Sqrt(cov[s * k + s])).ToArray(), k, lambda);
                    }
                    else w = RiskWeights(cov, k, m, lambda);
                    double sum = w.Sum();
                    w = w.Select(x => x / sum).ToArray();
                    if (scaleToGross)
                    {
                        double peak = past.Max(i => Enumerable.Range(0, k).Sum(s => w[s] * gross[s][i]));
                        if (peak > 1e-12) w = w.Select(x => x * grossLimit / peak).ToArray();
                    }
                }
                w = w.Select(x => Math.Min(x, maxWeight)).ToArray();
            }
            double r = 0, g = 0;
            for (int s = 0; s < k; s++) { r += w![s] * pnl[s][d]; g += w[s] * gross[s][d]; }
            if (g > grossLimit) { r *= grossLimit / g; g = grossLimit; }
            daily[d] = r; gr[d] = g; weights[d] = w!;
        }
        return new Result(daily, gr, weights);
    }
}
