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

    // Eigenvalues, descending. See EigenDecompose for the contract and the k-scaling note.
    public static double[] Eigenvalues(double[] cov, int k)
        => EigenDecompose(cov, k)?.Values ?? new double[k];

    // Symmetric eigendecomposition by CYCLIC Jacobi. Values descending, Vectors[i*k + j] = component
    // i of eigenvector j, columns reordered to match Values.
    //
    // This used to pick the largest off-diagonal each iteration and cap at 100 iterations, which is
    // fine at k=8 (28 off-diagonals, and it breaks early once they are all small) but silently wrong
    // for a symbol-level matrix: at k=190 there are 17,955 off-diagonals, so 100 single rotations
    // zero at most 100 of them and the routine returns unconverged diagonal entries with no error.
    // The greedy search is also O(k²) per rotation, i.e. O(k⁴) overall.
    //
    // Cyclic sweeps fix both: each sweep visits every (p,q) pair once, so a sweep is O(k³) and
    // convergence is quadratic after the first few. Jacobi converges to the same diagonal whichever
    // order the pairs are taken in, and the rotation itself is unchanged, so small-k callers
    // (CombinedBacktest's EffectiveBets at k≈8) get the same numbers as before.
    public static (double[] Values, double[] Vectors)? EigenDecompose(double[]? cov, int k)
    {
        if (cov == null || k <= 0 || cov.Length < k * k) return null;

        var A = (double[])cov.Clone();
        var V = new double[k * k];
        for (int i = 0; i < k; i++) V[i * k + i] = 1.0;

        if (k > 1)
        {
            // Scale-relative convergence target: the off-diagonal Frobenius norm must fall to
            // (1e-14)² of the total, so the test does not depend on the matrix's units.
            double total = 0;
            for (int a = 0; a < k; a++)
                for (int b = 0; b < k; b++) total += A[a * k + b] * A[a * k + b];
            double tol = 1e-28 * Math.Max(total, 1e-300);

            const int MaxSweeps = 60;
            for (int sweep = 0; sweep < MaxSweeps; sweep++)
            {
                double off = 0;
                for (int a = 0; a < k; a++)
                    for (int b = a + 1; b < k; b++) off += A[a * k + b] * A[a * k + b];
                if (off <= tol) break;

                for (int p = 0; p < k - 1; p++)
                    for (int q = p + 1; q < k; q++)
                    {
                        double apq = A[p * k + q];
                        if (apq == 0.0) continue;

                        double app = A[p * k + p], aqq = A[q * k + q];
                        // tan(2θ) = 2·apq / (aqq − app); the epsilon only guards atan2(0, 0).
                        double theta = 0.5 * Math.Atan2(2 * apq, aqq - app + 1e-12);
                        double c = Math.Cos(theta), s = Math.Sin(theta);

                        for (int i = 0; i < k; i++)
                        {
                            double aip = A[i * k + p], aiq = A[i * k + q];
                            A[i * k + p] = c * aip - s * aiq;
                            A[p * k + i] = A[i * k + p];
                            A[i * k + q] = s * aip + c * aiq;
                            A[q * k + i] = A[i * k + q];
                            double vip = V[i * k + p], viq = V[i * k + q];
                            V[i * k + p] = c * vip - s * viq;
                            V[i * k + q] = s * vip + c * viq;
                        }
                        A[p * k + q] = A[q * k + p] = 0;
                        A[p * k + p] = c * c * app - 2 * s * c * apq + s * s * aqq;
                        A[q * k + q] = s * s * app + 2 * s * c * apq + c * c * aqq;
                    }
            }
        }

        // Sort descending by eigenvalue, carrying the eigenvector columns with them.
        var order = Enumerable.Range(0, k).ToArray();
        var diag  = new double[k];
        for (int i = 0; i < k; i++) diag[i] = A[i * k + i];
        Array.Sort(order, (x, y) => diag[y].CompareTo(diag[x]));

        var values  = new double[k];
        var vectors = new double[k * k];
        for (int j = 0; j < k; j++)
        {
            values[j] = diag[order[j]];
            for (int i = 0; i < k; i++) vectors[i * k + j] = V[i * k + order[j]];
        }
        return (values, vectors);
    }

    // Ledoit-Wolf shrinkage with the ANALYTIC intensity, computed from the observations rather than
    // hand-picked. Shrink()'s fixed lambda is defensible at k≈8; at k≈190 the matrix carries
    // k(k+1)/2 ≈ 18,145 free parameters and the intensity is the whole estimate, not a knob.
    //
    // Ledoit & Wolf (2004), identity target:
    //   m  = tr(S)/k                     (mean variance)
    //   d² = ‖S − m·I‖²_F / k            (dispersion of S about the target)
    //   b² = min( (1/n²)·Σ_t ‖x_t x_tᵀ − S‖²_F / k ,  d² )   (estimation error, capped)
    //   λ  = b²/d²,   Σ* = λ·m·I + (1−λ)·S
    // S here uses the 1/n convention the derivation assumes, not Sample()'s 1/(n−1).
    // Cost is O(n·k²). Returns null on degenerate input.
    public static (double[] Cov, double Lambda)? ShrinkAuto(double[][] series)
    {
        int k = series.Length;
        if (k == 0) return null;
        int n = series[0].Length;
        for (int a = 1; a < k; a++) if (series[a].Length != n) return null;
        if (n < 2) return null;

        var x = new double[k][];
        for (int a = 0; a < k; a++)
        {
            double mean = series[a].Average();
            x[a] = new double[n];
            for (int i = 0; i < n; i++) x[a][i] = series[a][i] - mean;
        }

        var s = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = a; b < k; b++)
            {
                double acc = 0;
                for (int i = 0; i < n; i++) acc += x[a][i] * x[b][i];
                s[a * k + b] = s[b * k + a] = acc / n;
            }

        double m = 0;
        for (int a = 0; a < k; a++) m += s[a * k + a];
        m /= k;

        double d2 = 0;
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double t = s[a * k + b] - (a == b ? m : 0.0);
                d2 += t * t;
            }
        d2 /= k;
        if (!(d2 > 1e-300)) return (s, 0.0);   // S already equals the target — nothing to shrink

        double bbar2 = 0;
        for (int i = 0; i < n; i++)
            for (int a = 0; a < k; a++)
            {
                double xa = x[a][i];
                for (int b = 0; b < k; b++)
                {
                    double t = xa * x[b][i] - s[a * k + b];
                    bbar2 += t * t;
                }
            }
        bbar2 /= (double)n * n * k;

        double lambda = Math.Clamp(Math.Min(bbar2, d2) / d2, 0.0, 1.0);

        var outCov = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
                outCov[a * k + b] = lambda * (a == b ? m : 0.0) + (1.0 - lambda) * s[a * k + b];
        return (outCov, lambda);
    }
}