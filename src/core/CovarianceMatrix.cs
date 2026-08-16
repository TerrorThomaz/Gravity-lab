namespace TradingGA;

// Full covariance / correlation matrix over the strategy universe, with Ledoit-Wolf shrinkage and
// marginal-risk decomposition. This fills the gap left by CovarianceSizing, which only does pairwise
// Pearson correlations on daily grids and never forms a joint matrix.
//
// A joint covariance matrix is what enables:
//   - portfolio variance σ_p² = wᵀΣw  (the quantity a mean-variance optimizer optimises)
//   - marginal / contribution-to-risk decomposition (the basis of risk-parity allocation)
//   - a proper eigenvalue view of how many independent bets the book actually carries
//
// SHRinkage (Ledoit-Wolf): sample covariance is unstable for few observations vs assets. The LW
// estimator shrinks toward the diagonal-constant target  diag(mean variance) I, which is the standard
// fix used by quants precisely because raw sample covariance is almost never well-conditioned on
// trade-level series. Parameter lambda ∈ [0,1] balances sample vs target; callers may pass a fixed
// lambda or rely on the analytic intensity estimate.
public static class CovarianceMatrix
{
    // Compute the sample covariance matrix from per-strategy return series (aligned by index).
    // Input:  double[nStrategies][] series, each the same length n (aligned daily/monthly buckets),
    //         or a jagged list where a common length is enforced by the caller.
    // Returns the n×n covariance matrix (row-major flat array, [i*n + j]), or null if degenerate.
    public static double[]? Sample(double[][] series)
    {
        int k = series.Length;
        if (k == 0) return null;
        int n = series[0].Length;
        for (int a = 1; a < k; a++) if (series[a].Length != n) return null;  // must be aligned
        if (n < 2) return null;

        var means = new double[k];
        for (int a = 0; a < k; a++) means[a] = series[a].Average();

        var cov = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double s = 0;
                for (int i = 0; i < n; i++) s += (series[a][i] - means[a]) * (series[b][i] - means[b]);
                cov[a * k + b] = s / (n - 1);
            }
        return cov;
    }

    // Correlation matrix from a covariance matrix.
    public static double[]? Correlations(double[] cov, int k)
    {
        var corr = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double sa = Math.Sqrt(Math.Abs(cov[a * k + a]));
                double sb = Math.Sqrt(Math.Abs(cov[b * k + b]));
                if (sa < 1e-12 || sb < 1e-12) corr[a * k + b] = (a == b) ? 1.0 : 0.0;
                else corr[a * k + b] = Math.Clamp(cov[a * k + b] / (sa * sb), -1.0, 1.0);
            }
        return corr;
    }

    // Ledoit-Wolf shrinkage toward the diagonal-constant target  diag(avg_diag) I.
    //   Σ_shrunk = (1-λ)·Σ_sample + λ·mean(diag(Σ_sample))·I
    // λ ∈ [0,1]; 0 = raw sample, 1 = total shrinkage to the scalar-diagonal target. A callable may
    // pass its own λ; the analytic intensity (OAS/LW closed form) is not re-exported here — callers
    // typically fix λ around 0.2–0.5 for trade-level data. Returns null if sample is null.
    public static double[]? Shrink(double[]? cov, int k, double lambda = 0.3)
    {
        if (cov == null || k == 0) return null;
        double avgDiag = 0;
        for (int a = 0; a < k; a++) avgDiag += cov[a * k + a];
        avgDiag /= k;
        if (!(avgDiag > 1e-12)) return null;

        var out_ = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double target = (a == b) ? avgDiag : 0.0;
                out_[a * k + b] = (1.0 - lambda) * cov[a * k + b] + lambda * target;
            }
        return out_;
    }

    // Portfolio variance σ_p² = wᵀΣw., for weights w (length k).
    public static double Variance(double[] cov, int k, double[] w)
    {
        double v = 0;
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
                v += w[a] * w[b] * cov[a * k + b];
        return v;
    }

    public static double Volatility(double[] cov, int k, double[] w)
        => Math.Sqrt(Math.Max(0, Variance(cov, k, w)));

    // Marginal contribution to risk of each asset: MCR_a = (Σw)_a / σ_p. Contribution-to-risk (CTR):
    // CTR_a = w_a · MCR_a, and Σ CTR = σ_p. This is the decomposition risk-parity optimises toward
    // equal CTR. Returns (MCR, CTR) both length k; null on degenerate σ_p.
    public static (double[] MCR, double[] CTR)? MarginalRisk(double[] cov, int k, double[] w)
    {
        double sig_p = Volatility(cov, k, w);
        if (sig_p < 1e-12) return null;
        var sw = new double[k];
        for (int a = 0; a < k; a++) { double s = 0; for (int b = 0; b < k; b++) s += cov[a * k + b] * w[b]; sw[a] = s; }
        var mcr = new double[k]; var ctr = new double[k];
        for (int a = 0; a < k; a++) { mcr[a] = sw[a] / sig_p; ctr[a] = w[a] * mcr[a]; }
        return (mcr, ctr);
    }

    // Number of effectively-independent bets (participation ratio). For the diagonal eigenvalue
    // decomposition this is (Σλᵢ)² / Σλᵢ² ∈ [1, k]: 1 = totally correlated (one bet), k = orthogonal.
    public static double EffectiveBets(double[] cov, int k)
    {
        var eig = Eigenvalues(cov, k);
        double s1 = 0, s2 = 0;
        foreach (var e in eig) { s1 += e; s2 += e * e; }
        return s2 > 1e-24 ? s1 * s1 / s2 : 0.0;
    }

    // Symmetric-difference estimate of eigenvalues via Jacobi rotation (k small, exact for our use).
    public static double[] Eigenvalues(double[] cov, int k)
    {
        var A = (double[])cov.Clone();
        var V = new double[k * k];
        for (int i = 0; i < k; i++) V[i * k + i] = 1.0;
        const int MaxIter = 100;
        for (int iter = 0; iter < MaxIter; iter++)
        {
            // Find largest off-diagonal.
            int p = 0, q1 = 1; double maxAbs = 0;
            for (int a = 0; a < k; a++)
                for (int b = a + 1; b < k; b++)
                    if (Math.Abs(A[a * k + b]) > maxAbs) { maxAbs = Math.Abs(A[a * k + b]); p = a; q1 = b; }
            if (maxAbs < 1e-12 * (Math.Abs(A[0]) + Math.Abs(A[(k - 1) * k + k - 1]) + 1e-9)) break;

            double app = A[p * k + p], aqq = A[q1 * k + q1], apq = A[p * k + q1];
            double theta = 0.5 * Math.Atan2(2 * apq, aqq - app + 1e-12);
            double c = Math.Cos(theta), s = Math.Sin(theta);
            for (int i = 0; i < k; i++)
            {
                double aip = A[i * k + p], aiq = A[i * k + q1];
                A[i * k + p] = c * aip - s * aiq;
                A[p * k + i] = A[i * k + p];
                A[i * k + q1] = s * aip + c * aiq;
                A[q1 * k + i] = A[i * k + q1];
                double vip = V[i * k + p], viq = V[i * k + q1];
                V[i * k + p] = c * vip - s * viq;
                V[i * k + q1] = s * vip + c * viq;
            }
            A[p * k + q1] = A[q1 * k + p] = 0;
            A[p * k + p] = c * c * app - 2 * s * c * apq + s * s * aqq;
            A[q1 * k + q1] = s * s * app + 2 * s * c * apq + c * c * aqq;
        }
        var ev = new double[k];
        for (int i = 0; i < k; i++) ev[i] = A[i * k + i];
        Array.Sort(ev);
        Array.Reverse(ev);
        return ev;
    }
}