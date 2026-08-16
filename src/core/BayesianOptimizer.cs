namespace TradingGA;

// TPE Bayesian optimiser. Splits points into good (top 25%) / bad, fits KDEs, maximises EI = l(x)/g(x).
// Refine(): seed from GA elites → N iterations → return best. Bounds: [nGenes, 2].
public static class BayesianOptimizer
{
    private const double GoodFraction = 0.25;
    private const int    NCandidates  = 48;


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


        return candidates
            .OrderByDescending(c => LogKde(c, good, bounds) - LogKde(c, bad, bounds))
            .First();
    }


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

    // Log-space product-of-1D-KDEs (independence across dimensions assumed).
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

    // Silverman's rule, clamped to [3%, 20%] of range.
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

// Box-Muller Gaussian sampler.
internal static class RandomExtensions
{
    public static double NextGaussian(this Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
