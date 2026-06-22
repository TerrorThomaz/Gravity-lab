namespace TradingGA;

// Multiple-testing corrections for GA-optimised trading systems.
//
// DSR  — Deflated Sharpe Ratio (Bailey & López de Prado, 2014).
//         Adjusts the reported Sharpe for (a) non-normality via skewness/kurtosis and
//         (b) selection bias across T independent GA trials.  DSR > 0.95 means the
//         edge survives both corrections.
//
// PBO  — Probability of Backtest Overfitting (Bailey, Borwein, LdP, Zhu, 2015).
//         Combinatorial Purged CV: across all C(S,S/2) IS/OOS time-split
//         combinations, fraction of splits where the IS-optimal coin falls below the
//         OOS median.  PBO < 0.25 is low-risk; PBO > 0.50 = likely overfit.
//
// WRC  — White's Reality Check (White, 2000).
//         Circular block-bootstrap test of H₀: no model in the universe beats zero.
//         p < 0.05 = genuine edge unlikely to be a data-snooping artifact.
public static class StatisticalTests
{
    // ── Normal distribution ───────────────────────────────────────────────

    // Standard normal CDF via complementary error function.
    private static double NormCdf(double x)
    {
        if (x < -8) return 0.0;
        if (x >  8) return 1.0;
        return 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));
    }

    // Erf via A&S 7.1.26 Horner polynomial, max error ≈ 1.5e-7.
    private static double Erf(double x)
    {
        const double a1 =  0.254829592, a2 = -0.284496736, a3 = 1.421413741;
        const double a4 = -1.453152027, a5 =  1.061405429, p  = 0.3275911;
        int    sign = x < 0 ? -1 : 1;
        double t    = 1.0 / (1.0 + p * Math.Abs(x));
        double y    = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t
                          * Math.Exp(-x * x);
        return sign * y;
    }

    // Inverse normal CDF — A&S 26.2.23 rational approximation, max error ≈ 4.5e-4.
    // Adequate for Φ⁻¹(1 − 1/T) at T up to ~10⁷.
    private static double NormInv(double p)
    {
        if (p <= 1e-12) return -7.0;
        if (p >= 1 - 1e-12) return  7.0;
        double q = p < 0.5 ? p : 1.0 - p;
        double t = Math.Sqrt(-2.0 * Math.Log(q));
        double x = t - (2.515517 + t * (0.802853 + t * 0.010328))
                     / (1.0 + t * (1.432788 + t * (0.189269 + t * 0.001308)));
        return p < 0.5 ? -x : x;
    }

    // ── Moment estimation ─────────────────────────────────────────────────

    private readonly record struct RetMoments(double Mean, double Std, double Skew, double Kurt);

    private static RetMoments Moments(List<double> r)
    {
        int n = r.Count;
        if (n < 4) return new(n > 0 ? r.Average() : 0, 0, 0, 3);
        double mean = r.Average();
        double var  = r.Sum(x => (x - mean) * (x - mean)) / n;
        double std  = Math.Sqrt(var);
        if (std < 1e-14) return new(mean, 0, 0, 3);
        double sk = r.Sum(x => Math.Pow((x - mean) / std, 3)) / n;   // skewness
        double ku = r.Sum(x => Math.Pow((x - mean) / std, 4)) / n;   // kurtosis (non-excess; N=3)
        return new(mean, std, sk, ku);
    }

    // Per-trade Sharpe: SR̂ = μ/σ (not annualised).
    private static double PerTradeSharpe(List<double> r)
    {
        if (r.Count < 5) return 0;
        var m = Moments(r);
        return m.Std < 1e-14 ? 0 : m.Mean / m.Std;
    }

    // ── 1. Deflated Sharpe Ratio ──────────────────────────────────────────

    // Returns (DSR, PSR_vs_zero, E_max_SR_hat, SR_hat).
    //
    // SR_hat            per-trade Sharpe of the observed return series
    // E_max_SR          expected max per-trade Sharpe from T i.i.d. null trials
    // PSR_vs_zero       P(SR_true > 0) ignoring selection (classical significance)
    // DSR               P(SR_true > 0) after non-normality + selection correction
    //
    // numTrials: approximate independent candidate evaluations during optimisation
    // (e.g. GA population × generations).  GA trials are correlated, so treat this
    // as an upper bound — the true effective T is lower.
    public static (double Dsr, double PsrVsZero, double EMaxSr, double SrHat)
        DeflatedSharpeRatio(List<double> returns, int numTrials)
    {
        int n = returns.Count;
        if (n < 10) return (0, 0, 0, 0);

        var m = Moments(returns);
        if (m.Std < 1e-14) return (1, 1, 0, 0);

        double srHat = m.Mean / m.Std;

        // Non-normality correction (BLP 2014, eq. 3).
        // m.Kurt is the non-excess kurtosis: Gaussian = 3 → (3-1)/4 = 0.5.
        double denom = Math.Sqrt(Math.Max(1e-10,
            1.0 - m.Skew * srHat + (m.Kurt - 1.0) / 4.0 * srHat * srHat));

        double psrVsZero = NormCdf(srHat * Math.Sqrt(n - 1) / denom);

        // E[max SR̂] over numTrials i.i.d. candidates (BLP 2014, eq. 11).
        // E[max Z_T] ≈ (1−γ_E)·Φ⁻¹(1−1/T) + γ_E·Φ⁻¹(1−1/(T·e))
        // γ_E = Euler–Mascheroni constant ≈ 0.5772
        // Divide by √(n−1) to convert from Z-score units to per-trade SR units.
        const double gammaE = 0.5772156649;
        double T      = Math.Max(2, numTrials);
        double eMaxZ  = (1 - gammaE) * NormInv(1 - 1.0 / T)
                      +      gammaE  * NormInv(1 - 1.0 / (T * Math.E));
        double eMaxSr = eMaxZ / Math.Sqrt(Math.Max(1, n - 1));

        double dsr = NormCdf((srHat - eMaxSr) * Math.Sqrt(n - 1) / denom);
        return (dsr, psrVsZero, eMaxSr, srHat);
    }

    // ── 2. Probability of Backtest Overfitting (CPCV) ─────────────────────

    // configs[j] = (label, time-ordered val returns for one coin or config).
    // Algorithm: enumerate C(S, S/2) IS/OOS splits; for each split identify the
    // IS-optimal config and record whether its OOS rank falls below the OOS median.
    // PBO = fraction of splits where IS-best < OOS median.
    // Sparse configs (< S*2 trades) are filtered out before evaluation.
    public static (double Pbo, int Combos, double MeanLogitLambda)
        ProbabilityOfBacktestOverfitting(
            IReadOnlyList<(string Label, List<double> Returns)> configs,
            int numSplits = 10)
    {
        int S     = numSplits;
        int halfS = S / 2;

        // Only include configs with enough trades for meaningful splits.
        var dense = configs.Where(c => c.Returns.Count >= S * 2).ToList();
        if (dense.Count < 2) return (double.NaN, 0, 0);

        int J = dense.Count;

        // Period boundaries per config (split by trade count, not calendar time).
        int[][] bounds = dense.Select(c =>
            Enumerable.Range(0, S + 1).Select(i => i * c.Returns.Count / S).ToArray()
        ).ToArray();

        double sumLogit = 0;
        int    below    = 0;
        int    total    = 0;

        foreach (var isIdx in Combinations(Enumerable.Range(0, S).ToList(), halfS))
        {
            var oosIdx = Enumerable.Range(0, S).Where(i => !isIdx.Contains(i)).ToList();

            double[] isSr  = new double[J];
            double[] oosSr = new double[J];
            for (int j = 0; j < J; j++)
            {
                var r = dense[j].Returns;
                var b = bounds[j];
                isSr[j]  = PerTradeSharpe(isIdx .SelectMany(p => r[b[p]..b[p + 1]]).ToList());
                oosSr[j] = PerTradeSharpe(oosIdx.SelectMany(p => r[b[p]..b[p + 1]]).ToList());
            }

            int    winner   = Array.IndexOf(isSr, isSr.Max());
            double winOosSr = oosSr[winner];
            double rank     = oosSr.Count(s => s <= winOosSr);     // 1..J
            double lambda   = rank / J - 0.5;                       // ∈ (−0.5, +0.5]

            // Logit(λ + 0.5): positive → IS-best is above OOS median; negative → below.
            double hi = Math.Clamp(lambda + 0.5, 1e-9, 1 - 1e-9);
            sumLogit += Math.Log(hi / (1 - hi));

            if (lambda < 0) below++;
            total++;
        }

        if (total == 0) return (double.NaN, 0, 0);
        return ((double)below / total, total, sumLogit / total);
    }

    // ── 3. White's Reality Check ──────────────────────────────────────────

    // H₀: max_k E[f_k] ≤ 0 (no model beats zero return).
    // Test statistic: V = max_k(f̄_k).
    // Null distribution via circular block bootstrap (preserves serial correlation).
    // Centred statistic (White 2000): V* = max_k(f̄*_k − f̄_k).
    // blockSize = 0 → auto (√n of the largest config, Politis & Romano 1994).
    public static double WhitesRealityCheck(
        IReadOnlyList<(string Label, List<double> Returns)> configs,
        int     bootstrapSamples = 1000,
        int     blockSize        = 0,
        Random? rng              = null)
    {
        rng ??= new Random(42);
        int J = configs.Count;
        if (J == 0) return double.NaN;

        double[] fBar = configs.Select(c =>
            c.Returns.Count > 0 ? c.Returns.Average() : 0.0).ToArray();
        double V = fBar.Max();

        int N = configs.Max(c => c.Returns.Count);
        if (N < 5) return double.NaN;
        if (blockSize <= 0) blockSize = Math.Max(1, (int)Math.Round(Math.Sqrt(N)));

        int exceeds = 0;
        for (int b = 0; b < bootstrapSamples; b++)
        {
            double Vstar = double.NegativeInfinity;
            for (int j = 0; j < J; j++)
            {
                var ret = configs[j].Returns;
                int n   = ret.Count;
                if (n == 0) continue;

                // Circular block bootstrap resample.
                double sum = 0;
                int    cnt = 0;
                int    nb  = (int)Math.Ceiling((double)n / blockSize);
                for (int i = 0; i < nb && cnt < n; i++)
                {
                    int start = rng.Next(n);
                    for (int t = 0; t < blockSize && cnt < n; t++, cnt++)
                        sum += ret[(start + t) % n];
                }
                double fBarStar = cnt > 0 ? sum / cnt : 0;
                double excess   = fBarStar - fBar[j];    // centred: removes non-zero mean under H₀
                if (excess > Vstar) Vstar = excess;
            }
            if (Vstar >= V) exceeds++;
        }

        return (double)exceeds / bootstrapSamples;
    }

    // ── Formatted multi-strategy report ──────────────────────────────────

    // Runs DSR (at three T levels), PBO, and WRC and prints results.
    // gaTrials: approximate number of candidate genotype evaluations during training
    //   used as the nominal T in DSR.  GA generations are correlated so treat as
    //   an upper bound; T=1,000 (optimistic lower bound) is also shown.
    public static void PrintReport(
        IReadOnlyList<(string Label, List<double> Returns)> configs,
        string  strategyName,
        int     gaTrials = 10_000,
        Random? rng      = null)
    {
        rng ??= new Random(42);
        var allRet = configs.SelectMany(c => c.Returns).ToList();

        int denseCount = configs.Count(c => c.Returns.Count >= 20);
        Console.WriteLine($"\n── {strategyName}  ({configs.Count} coins  {allRet.Count} trades  {denseCount} dense) ────────────────────");

        if (allRet.Count < 10)
        {
            Console.WriteLine("  (too few trades — skipped)");
            return;
        }

        // DSR at three T values: optimistic / nominal / conservative upper bound.
        Console.WriteLine("  DSR  (H₀: true SR ≤ 0 after selection from T trials)");
        Console.WriteLine($"  {"T (trials)",12}  {"SR̂",7}  {"E[maxSR]",9}  {"PSR₀",6}  {"DSR",6}  verdict");
        foreach (int T in new[] { 1_000, gaTrials, 100_000 })
        {
            var (dsr, psr0, eMaxSr, srHat) = DeflatedSharpeRatio(allRet, T);
            string v = dsr >= 0.95 ? "✓ significant"
                     : dsr >= 0.80 ? "⚠ borderline"
                     : "✗ not significant";
            Console.WriteLine($"  {T,12:N0}  {srHat,+7:F4}  {eMaxSr,+9:F4}  {psr0,6:F3}  {dsr,6:F3}  {v}");
        }

        // PBO across coins — shows how reliably IS-optimal coin wins OOS.
        {
            var (pbo, combos, logitMean) = ProbabilityOfBacktestOverfitting(configs);
            if (!double.IsNaN(pbo))
            {
                string v = pbo <= 0.25 ? "✓ low"
                         : pbo <= 0.50 ? "⚠ moderate"
                         : "✗ high";
                Console.WriteLine($"  PBO  ({combos} CPCV splits, {denseCount} dense configs):  " +
                                  $"PBO={pbo:F3}  E[logit(λ)]={logitMean:+0.000;-0.000}  overfit risk: {v}");
            }
            else
            {
                Console.WriteLine($"  PBO  skipped — configs too sparse (need ≥20 trades each, have {denseCount} dense)");
            }
        }

        // WRC — tests whether the best coin's edge survives the full-universe null.
        {
            double pVal = WhitesRealityCheck(configs, rng: rng);
            if (!double.IsNaN(pVal))
            {
                string v = pVal < 0.05 ? "✓ rejects H₀ (genuine edge)"
                         : pVal < 0.20 ? "⚠ weak evidence"
                         : "✗ H₀ not rejected";
                Console.WriteLine($"  WRC  (1000 bootstrap, block=√n):  p={pVal:F3}  [{v}]");
            }
            else Console.WriteLine("  WRC  skipped (insufficient data)");
        }
    }

    // ── Combinations helper ───────────────────────────────────────────────

    private static IEnumerable<List<int>> Combinations(List<int> items, int k)
    {
        if (k == 0) { yield return []; yield break; }
        for (int i = 0; i <= items.Count - k; i++)
            foreach (var rest in Combinations(items[(i + 1)..], k - 1))
                yield return [items[i], .. rest];
    }
}
