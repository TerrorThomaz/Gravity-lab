namespace TradingGA;

// Book-level sizing across SLEEVES (carry, Grid, GridShort, trend, ...), from their daily P&L per
// unit of sleeve capital. Mirrors research/cov_sizing.py (parity: SleeveSizerTests).
//
// Monthly, from the `lookback` days strictly BEFORE the month:
//   1. risk weights, summing to 1: inverse vol, or ERC on a shrunk covariance — either the C# rule
//      (CovarianceMatrix.Shrink, toward mean(diag)·I, which inflates low-vol sleeves and pulls the
//      weights toward equal) or a correlation-only shrink that keeps each sleeve's own variance;
//   2. optionally scaled up until the book's trailing PEAK gross notional, Σ w_i·g_i(d), reaches
//      the gross limit: a capital split leaves most of a grid sleeve's capital idle (mean gross ~0.2);
//   3. each weight capped at maxWeight (the freed budget is NOT redistributed: conservative).
// Daily, a hard cap: a day whose gross exceeds the limit is shrunk to it (P&L and gross alike), so the
// book never runs levered even when the trailing estimate under-predicted a flush.
public static class SleeveSizer
{
    public enum Method { InverseVol, ErcMeanDiagShrink, ErcCorrShrink }

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
        _                        => RiskParity.EqualRiskContribution(CorrShrink(cov, k, lambda), k, maxIter: 5000, tol: 1e-10).Weights,
    };

    // pnl[i][d], gross[i][d]: sleeve i on day d (aligned to `days`).
    public static Result Size(DateTime[] days, double[][] pnl, double[][] gross, Method m, bool scaleToGross,
                              double grossLimit = 1.0, double maxWeight = double.PositiveInfinity,
                              int lookback = 90, int minDays = 60, double lambda = 0.3)
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
                    w = cov == null ? Enumerable.Repeat(1.0 / k, k).ToArray() : RiskWeights(cov, k, m, lambda);
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
