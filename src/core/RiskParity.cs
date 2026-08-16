namespace TradingGA;

// Risk-parity allocation over the strategy universe. Given a covariance matrix, allocate capital so
// each strategy contributes EQUAL marginal risk (contribution-to-risk, CTR) to the portfolio — not
// equal notional (which is what "diversification" naively means and what the count-based caps impose).
//
// Two approaches:
//   - Equal-Risk-Contribution (ERC): numerically find w with CTR_a = σ_p / k for all a. This is the
//     canonical "risk parity" — no need for expected returns, only the covariance. Robust to the
//     corrupting effect of return-estimation error (the most common source of MVO fragility).
//   - Weak risk parity / inverse-vol fallback: w_a ∝ 1/σ_a. This is ERC's closed-form approximation
//     when correlations are ~zero; provided as a cheap starting point / comparison.
//
// The theme is used as the `strategyWeight` hook (per-strategy multiplier on the Kelly-conf size),
// so these weights are NOT mean-normalised by default — they genuinely change relative exposure so a
// higher-vol strategy cannot quietly dominate the book's risk.
public static class RiskParity
{
    // Equal-risk-contribution weights. Returns (weights, contribution-to-risk, achieved_std_p).
    // weights sum to 1.0. scaleToConstant=1 normalises to a constant so affine scaling by the caller
    // (e.g. mean-normalise to ~1 for the strategyWeight hook) is possible.
    public record ErmResult(double[] Weights, double[] Contribution, double PortfolioVol);

    public static ErmResult EqualRiskContribution(
        double[] cov, int k,
        int maxIter = 500,
        double tol = 1e-6,
        bool normalizeToMeanOne = false)
    {
        // Start from inverse-vol (equal-ish risk), then iterate a multiplicative update toward equal
        // CTR. The update `w_a *= targetCTR_a / actualCTR_a` with target = σ_p/k is a well-known
        // convergent fixed-point for ERC when the covariance is positive-semidefinite.
        var vol = new double[k];
        for (int a = 0; a < k; a++) vol[a] = Math.Max(1e-9, Math.Sqrt(Math.Abs(cov[a * k + a])));

        double sumInv = vol.Sum(v => 1.0 / v);
        var w = new double[k];
        for (int a = 0; a < k; a++) w[a] = (1.0 / vol[a]) / sumInv;

        for (int it = 0; it < maxIter; it++)
        {
            double sigP = CovarianceMatrix.Volatility(cov, k, w);
            if (sigP < 1e-12) break;
            var (_, ctr) = CovarianceMatrix.MarginalRisk(cov, k, w) ?? (Array.Empty<double>(), Array.Empty<double>());
            if (ctr.Length == 0) break;
            double target = sigP / k;
            double total = 0;
            for (int a = 0; a < k; a++)
            {
                // Newton-style: w ← w · (target/CTR_a)^δ, δ damped for stability.
                double delta = 0.7;
                w[a] = w[a] * Math.Pow(target / Math.Max(ctr[a], 1e-12), delta);
                total += w[a];
            }
            // Renormalise to sum 1.
            if (total > 1e-12) for (int a = 0; a < k; a++) w[a] /= total;

            if (it > 0 && Converged(cov, k, w, tol)) break;
        }

        double sig = CovarianceMatrix.Volatility(cov, k, w);
        var (mcrF, ctrF) = CovarianceMatrix.MarginalRisk(cov, k, w) ?? (Array.Empty<double>(), Array.Empty<double>());

        var outW = (double[])w.Clone();
        if (normalizeToMeanOne)
        {
            double mean = outW.Average();
            if (mean > 1e-12) for (int a = 0; a < k; a++) outW[a] /= mean;
        }
        return new ErmResult(outW, ctrF, sig);
    }

    // Inverse-vol weights (weak risk parity, correlation-agnostic). w_a ∝ 1/σ_a, sum to 1.
    public static double[] InverseVol(double[] cov, int k)
    {
        var vol = new double[k];
        for (int a = 0; a < k; a++) vol[a] = Math.Max(1e-9, Math.Sqrt(Math.Abs(cov[a * k + a])));
        double sum = vol.Sum(v => 1.0 / v);
        var w = new double[k];
        for (int a = 0; a < k; a++) w[a] = (1.0 / vol[a]) / sum;
        return w;
    }

    private static bool Converged(double[] cov, int k, double[] w, double tol)
    {
        double sigP = CovarianceMatrix.Volatility(cov, k, w);
        if (sigP < 1e-12) return true;
        var (_, ctr) = CovarianceMatrix.MarginalRisk(cov, k, w) ?? (Array.Empty<double>(), Array.Empty<double>());
        if (ctr.Length == 0) return true;
        double target = sigP / k;
        double maxRel = 0;
        for (int a = 0; a < k; a++) maxRel = Math.Max(maxRel, Math.Abs(ctr[a] - target) / Math.Max(target, 1e-12));
        return maxRel < tol;
    }
}