namespace TradingGA;

// Covariance-aware position sizing — optional, measured, off by default.
//
// WHY
// Sizing today is per-trade Kelly plus flat caps: PortfolioReplay's per-strategy concurrency caps,
// Config.MaxDirectionalConcurrent, and Config.MaxTotalExposurePct. Every one of those is a COUNT
// or a GROSS limit, and none of them knows that two positions might be the same bet.
//
// CorrelatedShock already showed correlation is the dominant risk here: a -40% simultaneous move
// costs 12.4% of equity at the 0.30 cap and 41.7% at 1.00, while ordinary drawdown does not move
// at all between those settings. The flat cap is a crude proxy for a covariance constraint — it
// bounds total size because it cannot bound correlated size.
//
// THE MODEL
// Inverse-variance (risk parity) weights with a correlation haircut:
//
//     w_i  proportional to  1 / sigma_i          then scaled by  1 / sqrt(1 + sum_j!=i rho_ij)
//
// A strategy that is volatile OR heavily correlated with the rest of the book gets less size. Two
// strategies with rho = 1 split roughly one strategy's worth of risk between them, which is the
// behaviour the count-based caps cannot express.
//
// Deliberately NOT full mean-variance optimisation. Markowitz needs expected returns, and expected
// returns estimated from the same backtest that selected the genotypes is precisely the input most
// prone to overfitting — it would import every selection bias in this repo directly into sizing.
// Inverse-variance uses only the covariance matrix, which is far more stable to estimate than
// means, and is the standard practitioner response to exactly that problem.
//
// OFF BY DEFAULT. Turning it on changes position sizing on every trade, so it is measured against
// the flat-cap baseline rather than assumed better. GRAVITY_COVSIZE=1.
public static class CovarianceSizing
{
    public static bool Enabled => Environment.GetEnvironmentVariable("GRAVITY_COVSIZE") == "1";

    // Minimum overlapping observations before a pairwise correlation is trusted. Below this the
    // estimate is noise and is treated as ZERO correlation rather than as measured — assuming
    // independence on thin data is the conservative error here (it under-sizes rather than
    // over-sizes, because a spurious high correlation would shrink weights arbitrarily).
    public const int MinPairObservations = 30;

    public record Weights(
        IReadOnlyDictionary<string, double> PerStrategy,   // multiplier, mean-normalised to ~1.0
        IReadOnlyDictionary<string, double> Volatility,
        double AvgCorrelation)
    {
        public double For(string strategy) => PerStrategy.TryGetValue(strategy, out var w) ? w : 1.0;
    }

    // series: per-strategy return series, bucketed onto a COMMON time grid by the caller. They must
    // be aligned index-for-index — element t of every series must describe the same period, or the
    // correlations describe nothing.
    public static Weights Compute(IReadOnlyDictionary<string, double[]> series)
    {
        var names = series.Keys.OrderBy(k => k).ToArray();
        var vol   = new Dictionary<string, double>();
        foreach (var n in names) vol[n] = StdDev(series[n]);

        // Pairwise correlations, and each strategy's total correlation load.
        var load = new Dictionary<string, double>();
        double corrSum = 0; int corrN = 0;
        foreach (var a in names)
        {
            double sum = 0;
            foreach (var b in names)
            {
                if (a == b) continue;
                double rho = Correlation(series[a], series[b]);
                sum += Math.Max(0.0, rho);   // negative correlation is a HEDGE; do not charge for it
                corrSum += rho; corrN++;
            }
            load[a] = sum;
        }

        var raw = new Dictionary<string, double>();
        foreach (var n in names)
        {
            double v = vol[n] > 1e-9 ? vol[n] : 1e-9;
            raw[n] = 1.0 / v / Math.Sqrt(1.0 + load[n]);
        }

        // Normalise to mean 1.0 so this REDISTRIBUTES size rather than changing gross exposure —
        // otherwise it would be silently entangled with the exposure cap and neither could be
        // measured independently.
        double mean = raw.Values.Average();
        var final = new Dictionary<string, double>();
        foreach (var n in names) final[n] = mean > 1e-12 ? raw[n] / mean : 1.0;

        return new Weights(final, vol, corrN > 0 ? corrSum / corrN : 0.0);
    }

    public static double Correlation(double[] a, double[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n < MinPairObservations) return 0.0;

        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;

        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        double den = Math.Sqrt(saa * sbb);
        return den > 1e-12 ? Math.Clamp(sab / den, -1.0, 1.0) : 0.0;
    }

    public static double StdDev(double[] x)
    {
        if (x.Length < 2) return 0.0;
        double m = x.Average(), s = 0;
        foreach (double v in x) { double d = v - m; s += d * d; }
        return Math.Sqrt(s / (x.Length - 1));
    }

    // Bucket irregular trades onto a fixed grid so cross-strategy correlation is even defined.
    // Trades are irregularly spaced and overlapping; correlation needs a common clock.
    public static double[] ToGrid(
        IReadOnlyList<(DateTime Time, double ReturnPct)> trades,
        DateTime start, DateTime end, TimeSpan bucket)
    {
        int n = Math.Max(1, (int)((end - start).TotalSeconds / bucket.TotalSeconds) + 1);
        var g = new double[n];
        foreach (var t in trades)
        {
            int i = (int)((t.Time - start).TotalSeconds / bucket.TotalSeconds);
            if ((uint)i < (uint)n) g[i] += t.ReturnPct;   // sum within a bucket: additive P&L
        }
        return g;
    }

    public static void Print(Weights w)
    {
        Console.WriteLine("\n── Covariance sizing (inverse-variance with correlation haircut) ────────────");
        Console.WriteLine($"  average pairwise correlation: {w.AvgCorrelation:F3}");
        Console.WriteLine($"  {"strategy",-12}  {"vol",8}  {"weight",8}");
        Console.WriteLine($"  {new string('-', 34)}");
        foreach (var kv in w.PerStrategy.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-12}  {w.Volatility[kv.Key],8:F3}  {kv.Value,8:F3}");
    }
}
