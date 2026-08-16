namespace TradingGA;

// Covariance-aware position sizing. Inverse-vol weights with correlation haircut.
// ON by default (GRAVITY_COVSIZE=0 disables). Composite metric = expectancy × Kelly × PF / downside.
public static class CovarianceSizing
{
    // ON by default. GRAVITY_COVSIZE=0 disables. Composite doubles return-per-unit-DD vs baseline.
    public static bool Enabled => Environment.GetEnvironmentVariable("GRAVITY_COVSIZE") != "0";

    // GRAVITY_SIZEMETRIC selects. InverseVol is wrong default for this book (defunds best edges).
    // Weights capped at MaxWeight; below MinSamples → neutral 1.0. Mean-normalised (redistributes only).
    public enum Metric
    {
        InverseVol,     // 1 / sigma — the classic risk-parity term
        Kelly,          // (p*b - q) / b  — win rate AND payoff ratio, the theoretically right one
        Expectancy,     // mean return per trade — pure edge, ignores dispersion entirely
        ProfitFactor,   // gross wins / gross losses
        Sharpe,         // expectancy / downside deviation — edge per unit of DOWNSIDE only
        Composite,      // all of the above, in FoldScoreHelper.Canonical's multiplicative shape
    }

    // Composite: expectancy × (1+Kelly) × (1+PF-1) / (1+DownsideDev). Same shape as FoldScoreHelper.
    // Caveat: 4 terms from same backtest = overfitting surface. Treat as hypothesis, not guarantee.
    public const double CompositeKellyW = 1.0;
    public const double CompositePfW    = 0.5;
    public const double CompositeDownW  = 0.5;

    public static double CompositeScore(double[] r)
    {
        if (r.Length < MinSamples) return 0.0;
        double exp = r.Average();
        if (exp <= 0) return 0.0;                      // no edge, no allocation

        double kelly = KellyFraction(r);
        double pf    = ProfitFactorOf(r);
        double down  = DownsideDev(r);

        return exp
             * (1.0 + CompositeKellyW * kelly)
             * (1.0 + CompositePfW    * Math.Max(0.0, pf - 1.0))
             / (1.0 + CompositeDownW  * down);
    }

    public static Metric SelectedMetric =>
        (Environment.GetEnvironmentVariable("GRAVITY_SIZEMETRIC") ?? "").ToLowerInvariant() switch
        {
            "kelly"       => Metric.Kelly,
            "expectancy"  => Metric.Expectancy,
            "pf"          => Metric.ProfitFactor,
            "sharpe"      => Metric.Sharpe,
            "composite"   => Metric.Composite,
            "inversevol"  => Metric.InverseVol,
            // Composite default. InverseVol wrong for this book — must be asked for by name.
            _             => Metric.Composite,
        };

    // Max weight = 3× mean. Concentration limit against point-estimate tail risk.
    public const double MaxWeight  = 3.0;
    public const int    MinSamples = 20;

    // Half-Kelly: uses win rate AND payoff ratio together. Halved for estimate uncertainty.
    public static double KellyFraction(double[] r)
    {
        if (r.Length < MinSamples) return 0.0;
        var wins = r.Where(x => x > 0).ToArray();
        var losses = r.Where(x => x <= 0).ToArray();
        if (wins.Length == 0 || losses.Length == 0) return 0.0;
        double p = (double)wins.Length / r.Length;
        double b = wins.Average() / Math.Abs(losses.Average());
        if (b <= 1e-9) return 0.0;
        return Math.Max(0.0, (p * b - (1 - p)) / b) / 2.0;
    }

    public static double ProfitFactorOf(double[] r)
    {
        double g = r.Where(x => x > 0).Sum(), l = Math.Abs(r.Where(x => x <= 0).Sum());
        return l > 1e-9 ? g / l : (g > 0 ? 10.0 : 0.0);
    }

    // Downside deviation below zero (semi-deviation). Only penalises shortfalls, not upside.
    public static double DownsideDev(double[] r)
    {
        if (r.Length < 2) return 0.0;
        double s = 0;
        foreach (double v in r) { double d = Math.Min(0.0, v); s += d * d; }
        return Math.Sqrt(s / r.Length);
    }

    public static double RawScore(double[] r, Metric m) => m switch
    {
        Metric.InverseVol   => StdDev(r) > 1e-9 ? 1.0 / StdDev(r) : 0.0,
        Metric.Kelly        => KellyFraction(r),
        Metric.Expectancy   => r.Length >= MinSamples ? Math.Max(0.0, r.Average()) : 0.0,
        Metric.ProfitFactor => r.Length >= MinSamples ? Math.Max(0.0, ProfitFactorOf(r) - 1.0) : 0.0,
        Metric.Sharpe       => r.Length >= MinSamples && DownsideDev(r) > 1e-9
                               ? Math.Max(0.0, r.Average()) / DownsideDev(r) : 0.0,
        Metric.Composite    => CompositeScore(r),
        _ => 0.0,
    };

    // Below this, correlation treated as zero (conservative: under-sizes rather than over-sizes).
    public const int MinPairObservations = 30;

    public record Weights(
        IReadOnlyDictionary<string, double> PerStrategy,   // multiplier, mean-normalised to ~1.0
        IReadOnlyDictionary<string, double> Volatility,
        double AvgCorrelation)
    {
        public double For(string strategy) => PerStrategy.TryGetValue(strategy, out var w) ? w : 1.0;
    }

    // series: bucketed onto common time grid (for correlation). rawTrades: raw returns (for metric).
    // Must be separate — bucketing destroys distributional stats like Kelly/win rate.
    public static Weights Compute(IReadOnlyDictionary<string, double[]> series,
                                  IReadOnlyDictionary<string, double[]>? rawTrades = null,
                                  Metric? metric = null)
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

        var chosen = metric ?? SelectedMetric;
        var raw = new Dictionary<string, double>();
        foreach (var n in names)
        {
            double[] sample = rawTrades != null && rawTrades.TryGetValue(n, out var rt) ? rt : series[n];
            double score = RawScore(sample, chosen);
            // Too little history → neutral 1.0 ("can't measure" ≠ "no edge").
            if (score <= 0) score = sample.Length < MinSamples ? 1.0 : 0.0;
            raw[n] = score / Math.Sqrt(1.0 + load[n]);
        }

        // Normalise to mean 1.0 — redistributes size, doesn't change gross exposure.
        double mean = raw.Values.Average();
        var final = new Dictionary<string, double>();
        foreach (var n in names) final[n] = mean > 1e-12 ? Math.Min(raw[n] / mean, MaxWeight) : 1.0;

        double m2 = final.Values.Average();
        if (m2 > 1e-12) foreach (var n in names) final[n] /= m2;

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

    // Bucket irregular trades onto a fixed grid for cross-strategy correlation.
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
        Console.WriteLine($"\n── Strategy sizing: {SelectedMetric} + correlation haircut ──────────────────");
        Console.WriteLine($"  average pairwise correlation: {w.AvgCorrelation:F3}");
        Console.WriteLine($"  {"strategy",-12}  {"vol",8}  {"weight",8}");
        Console.WriteLine($"  {new string('-', 34)}");
        foreach (var kv in w.PerStrategy.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-12}  {w.Volatility[kv.Key],8:F3}  {kv.Value,8:F3}");
    }
}
