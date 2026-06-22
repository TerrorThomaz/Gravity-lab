namespace TradingGA;

// TPE (Tree-structured Parzen Estimator) — the same algorithm used by Optuna's default sampler.
//
// How it works:
//   1. All evaluated points are split into "good" (top γ=25% by fitness) and "bad" (rest).
//   2. For each parameter dimension, two Gaussian KDEs are fitted: l(x) over good points,
//      g(x) over bad points.
//   3. The acquisition function is EI = l(x) / g(x).  Maximising this finds points that
//      are likely in the good region AND unlikely in the bad region.
//   4. Candidates are drawn by sampling from good-point distributions, then ranked by EI.
//
// Usage: call Suggest() to get the next point; call Observe() after evaluating it.
// Refine() runs a full loop: seed from GA elites → N BO iterations → return best.
//
// Bounds: double[nGenes, 2] where [i,0]=min  [i,1]=max for gene i.
// Integers: pass as doubles; caller rounds when converting back to genotype.
public static class BayesianOptimizer
{
    private const double GoodFraction = 0.25;
    private const int    NCandidates  = 48;

    // Suggest the next point to evaluate given past observations.
    public static double[] Suggest(
        List<(double[] Params, double Fitness)> history,
        double[,] bounds,
        Random rng)
    {
        int nDims = bounds.GetLength(0);
        if (history.Count < 12)
            return RandomPoint(bounds, rng);

        var sorted = history.OrderByDescending(h => h.Fitness).ToList();
        int nGood  = Math.Max(2, (int)(sorted.Count * GoodFraction));
        var good   = sorted.Take(nGood).Select(h => h.Params).ToList();
        var bad    = sorted.Skip(nGood).Select(h => h.Params).ToList();

        // Generate candidates by perturbing random good points
        var candidates = new List<double[]>(NCandidates);
        for (int i = 0; i < NCandidates; i++)
        {
            var basePoint = good[rng.Next(good.Count)];
            var candidate = new double[nDims];
            for (int d = 0; d < nDims; d++)
            {
                double lo = bounds[d, 0], hi = bounds[d, 1];
                double bw  = GoodBandwidth(good, d, lo, hi);
                candidate[d] = Math.Clamp(basePoint[d] + rng.NextGaussian() * bw, lo, hi);
            }
            candidates.Add(candidate);
        }

        // Rank by EI = log l(x) - log g(x)  (log-space for numerical stability)
        return candidates
            .OrderByDescending(c => LogKde(c, good, bounds) - LogKde(c, bad, bounds))
            .First();
    }

    // Run a full refinement loop seeded from initial observations.
    // Returns all history (seed + new evaluations); caller extracts the best.
    public static List<(double[] Params, double Fitness)> Refine(
        IEnumerable<(double[] Params, double Fitness)> seedObs,
        double[,] bounds,
        Func<double[], double> evaluate,
        int iterations,
        Random rng)
    {
        var history = seedObs.ToList();
        for (int i = 0; i < iterations; i++)
        {
            var next    = Suggest(history, bounds, rng);
            double fit  = evaluate(next);
            history.Add((next, fit));
        }
        return history;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    // Log-space product-of-1D-KDEs (TPE assumes independence across dimensions)
    private static double LogKde(double[] x, List<double[]> points, double[,] bounds)
    {
        if (points.Count == 0) return Math.Log(1e-10);
        int nDims = x.Length;
        double logDensity = 0;
        for (int d = 0; d < nDims; d++)
        {
            double lo = bounds[d, 0], hi = bounds[d, 1];
            double bw  = Bandwidth(points, d, lo, hi);
            double sum  = 0;
            foreach (var p in points)
                sum += NormalPdf((x[d] - p[d]) / bw);
            logDensity += Math.Log(sum / points.Count + 1e-10);
        }
        return logDensity;
    }

    // Bandwidth = Silverman's rule on each dimension, clamped to [3%, 20%] of range.
    private static double GoodBandwidth(List<double[]> pts, int d, double lo, double hi) =>
        Bandwidth(pts, d, lo, hi);

    private static double Bandwidth(List<double[]> pts, int d, double lo, double hi)
    {
        if (pts.Count < 2) return (hi - lo) * 0.10;
        double mean = pts.Average(p => p[d]);
        double std  = Math.Sqrt(pts.Select(p => (p[d] - mean) * (p[d] - mean)).Average());
        double bw   = 1.06 * std * Math.Pow(pts.Count, -0.2);
        double range = hi - lo;
        return range > 1e-10 ? Math.Clamp(bw, range * 0.03, range * 0.20) : std * 0.10;
    }

    private static double NormalPdf(double z) => Math.Exp(-0.5 * z * z) / 2.5066282746;

    private static double[] RandomPoint(double[,] bounds, Random rng)
    {
        int n = bounds.GetLength(0);
        var p = new double[n];
        for (int d = 0; d < n; d++)
            p[d] = bounds[d, 0] + rng.NextDouble() * (bounds[d, 1] - bounds[d, 0]);
        return p;
    }
}

// Box-Muller Gaussian sampler extension
internal static class RandomExtensions
{
    public static double NextGaussian(this Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
