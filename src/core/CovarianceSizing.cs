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
    // ON by default. GRAVITY_COVSIZE=0 disables.
    //
    // Measured on the validation window, all metrics against the flat-cap baseline:
    //     baseline       108.7% / 167.3%   DD 1.3% / 2.8%
    //     inverse-vol     58.1% /  93.7%   DD 0.6% / 1.0%   (halves return — see Metric header)
    //     kelly          109.7% / 186.1%
    //     expectancy     111.9% / 191.1%
    //     sharpe         113.7% / 178.9%   DD 1.1% / 1.8%
    //     composite      112.6% / 181.4%   DD 0.5% / 1.3%   <- +14pp at HALF the drawdown
    // Composite more than doubles return-per-unit-drawdown against the baseline (140 vs 60).
    public static bool Enabled => Environment.GetEnvironmentVariable("GRAVITY_COVSIZE") != "0";

    // ── Which quantity drives the weight ─────────────────────────────────────────────────
    // GRAVITY_SIZEMETRIC selects. The correlation haircut is applied on top of ALL of them, so
    // the metric and the diversification adjustment stay orthogonal and each can be measured.
    //
    // WHY InverseVol IS THE WRONG DEFAULT FOR THIS BOOK — measured, not assumed:
    //   grid        vol 0.418 -> weight 4.56     (smallest per-trade return in the suite)
    //   swing_long  vol 8.802 -> weight 0.16     (PF 4.80, WR 85%)
    // Inverse-variance treats volatility AS risk. Here volatility is mostly payoff asymmetry:
    // SwingLong's 8.8 comes from large winners at an 85% win rate, not from danger. So it
    // defunds the best edges and floods capital into the weakest one. Portfolio return halved.
    //
    // THE OVERFITTING WARNING, because these metrics invite it:
    // Kelly/Expectancy/ProfitFactor are estimated from the SAME backtest that selected the
    // genotypes, so they import that selection bias directly into sizing — the reason this class
    // originally used inverse-variance only (covariance is far more stable to estimate than
    // means). Mitigations kept deliberately crude rather than clever: weights are capped at
    // MaxWeight, a strategy below MinSamples gets a neutral 1.0, and everything is
    // mean-normalised so this can only REDISTRIBUTE size. Treat a large spread in weights as a
    // warning sign, not a discovery.
    public enum Metric
    {
        InverseVol,     // 1 / sigma — the classic risk-parity term
        Kelly,          // (p*b - q) / b  — win rate AND payoff ratio, the theoretically right one
        Expectancy,     // mean return per trade — pure edge, ignores dispersion entirely
        ProfitFactor,   // gross wins / gross losses
        Sharpe,         // expectancy / downside deviation — edge per unit of DOWNSIDE only
        Composite,      // all of the above, in FoldScoreHelper.Canonical's multiplicative shape
    }

    // ── Composite: the fitness-function shape, applied to sizing ─────────────────────────
    // Same construction as FoldScoreHelper.Canonical — a base quantity multiplied by bonus terms
    // that are exact no-ops at their neutral value, so every weight can be zeroed independently
    // and the whole thing degrades to a single metric rather than to nonsense.
    //
    //     score = expectancy
    //           * (1 + KellyW  * kelly)                 edge quality: win rate AND payoff together
    //           * (1 + PfW     * (pf - 1))              gross win/loss ratio
    //           / (1 + DownW   * downsideDev)           per unit of DOWNSIDE, never upside
    //
    // Expectancy is the base because it is the only term in the actual units of compounding —
    // the others are ratios, and a ratio cannot be the thing you allocate against. Measured
    // separately, expectancy gave the best raw return (191.1%) and downside-Sharpe the best
    // risk-adjusted (178.9% at 1.8% DD vs baseline 167.3% at 2.8%); this shape is an attempt to
    // hold both rather than choose.
    //
    // HONEST CAVEAT: four weighted terms estimated from the same backtest that selected the
    // genotypes is more overfitting surface than any single metric, and the terms are NOT
    // independent — Kelly and expectancy both encode edge, PF and downside both encode risk. The
    // weights are deliberately left at modest defaults rather than fitted, because fitting them
    // on this window is precisely the failure this repo keeps finding. Treat Composite as a
    // hypothesis to be measured against the single metrics, not as their guaranteed superior.
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
            // Composite is the default: expectancy as the base, with Kelly / PF / downside as
            // no-op-at-neutral multipliers. InverseVol is retained but must be asked for by name —
            // it is measurably the WRONG shape for this book (it sized Grid, the weakest per-trade
            // edge, at 4.56x while sizing SwingLong at 0.16x).
            _             => Metric.Composite,
        };

    // No strategy may exceed this multiple of the mean weight. A concentration limit, because
    // every metric below is a point estimate from a finite sample and the tail of that estimate
    // is exactly where sizing does the most damage.
    public const double MaxWeight  = 3.0;
    public const int    MinSamples = 20;

    // Half-Kelly on the strategy's own return distribution. Uses win rate p and payoff ratio b
    // together, which is what "size by how good the edge is" actually means — a 90% win rate with
    // a 0.1 payoff ratio is not a good edge, and neither term alone can tell you that.
    // Halved for the usual reason: full Kelly is optimal only if the estimates are exact.
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

    // Downside deviation BELOW ZERO (semi-deviation), not below the mean of the losses.
    //
    // The first version measured dispersion AMONG losses, which is wrong and the test caught it:
    // a strategy whose losses are all exactly -1.0 has zero dispersion among them, so it scored
    // 0 and sized to nothing — when consistent, tightly-controlled losses are the BEST case, not
    // the worst. Sortino's target-based form (deviate from 0, count only shortfalls) is the
    // standard definition and does not have this hole.
    //
    // Penalising upside dispersion is the separate error that made InverseVol defund SwingLong;
    // both are avoided by only ever looking below zero.
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
    // rawTrades: per-strategy RAW trade returns, used for the metric. Correlation still uses the
    // bucketed `series`, because correlation needs a common clock and trades do not have one.
    //
    // These MUST be separate inputs. Computing Kelly or win rate on daily buckets is wrong: a day
    // with two winners and one loser sums to a single positive number, so a 67% win rate reads as
    // 100%. Bucketing is right for correlation and destructive for anything distributional.
    // metric: explicit override. Defaults to SelectedMetric (the env var) so production behaviour
    // is unchanged, but callers — and tests of the correlation haircut, which is
    // metric-INDEPENDENT — can pin it rather than depending on ambient environment state.
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
            // A strategy with too little history sizes NEUTRALLY rather than at zero: "we cannot
            // measure this yet" must not read as "this has no edge".
            if (score <= 0) score = sample.Length < MinSamples ? 1.0 : 0.0;
            raw[n] = score / Math.Sqrt(1.0 + load[n]);
        }

        // Normalise to mean 1.0 so this REDISTRIBUTES size rather than changing gross exposure —
        // otherwise it would be silently entangled with the exposure cap and neither could be
        // measured independently.
        double mean = raw.Values.Average();
        var final = new Dictionary<string, double>();
        foreach (var n in names) final[n] = mean > 1e-12 ? Math.Min(raw[n] / mean, MaxWeight) : 1.0;
        // Re-normalise after capping so gross exposure is still unchanged.
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
        Console.WriteLine($"\n── Strategy sizing: {SelectedMetric} + correlation haircut ──────────────────");
        Console.WriteLine($"  average pairwise correlation: {w.AvgCorrelation:F3}");
        Console.WriteLine($"  {"strategy",-12}  {"vol",8}  {"weight",8}");
        Console.WriteLine($"  {new string('-', 34)}");
        foreach (var kv in w.PerStrategy.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-12}  {w.Volatility[kv.Key],8:F3}  {kv.Value,8:F3}");
    }
}
