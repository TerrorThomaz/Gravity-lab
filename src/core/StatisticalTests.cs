namespace TradingGA;

// Multiple-testing corrections for GA-optimised trading systems.
// DSR: Deflated Sharpe Ratio — adjusts for non-normality + selection bias across T trials.
// PBO: Probability of Backtest Overfitting — combinatorial purged CV.
// WRC: White's Reality Check — block-bootstrap test that no model beats zero.
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
    // Returns (DSR, PSR_vs_zero, E_max_SR, SR_hat).
    // numTrials: approx independent candidate evaluations (upper bound; GA trials are correlated).
    // effectiveN: independent observation count (not returns.Count — trades cluster by regime).
    // Defaults to returns.Count for backward compatibility.
    public static (double Dsr, double PsrVsZero, double EMaxSr, double SrHat)
        DeflatedSharpeRatio(List<double> returns, int numTrials, int? effectiveN = null)
    {
        int n = effectiveN ?? returns.Count;
        if (n < 10) return (0, 0, 0, 0);

        var m = Moments(returns);
        if (m.Std < 1e-14) return (1, 1, 0, 0);

        double srHat = m.Mean / m.Std;

        // Non-normality correction. m.Kurt is non-excess (Gaussian=3).
        double denom = Math.Sqrt(Math.Max(1e-10,
            1.0 - m.Skew * srHat + (m.Kurt - 1.0) / 4.0 * srHat * srHat));

        double psrVsZero = NormCdf(srHat * Math.Sqrt(n - 1) / denom);

        // E[max SR] over T i.i.d. candidates. γ_E = Euler-Mascheroni ≈ 0.5772.
        const double gammaE = 0.5772156649;
        double T      = Math.Max(2, numTrials);
        double eMaxZ  = (1 - gammaE) * NormInv(1 - 1.0 / T)
                      +      gammaE  * NormInv(1 - 1.0 / (T * Math.E));
        double eMaxSr = eMaxZ / Math.Sqrt(Math.Max(1, n - 1));

        double dsr = NormCdf((srHat - eMaxSr) * Math.Sqrt(n - 1) / denom);
        return (dsr, psrVsZero, eMaxSr, srHat);
    }

    // ── 2. Probability of Backtest Overfitting (CPCV) ─────────────────────
    // Enumerate C(S, S/2) IS/OOS splits; PBO = fraction where IS-best < OOS median.
    // Sparse configs (< S*2 trades) filtered out.
    public static (double Pbo, int Combos, double MeanLogitLambda)
        ProbabilityOfBacktestOverfitting(
            IReadOnlyList<(string Label, List<double> Returns)> configs,
            int numSplits = 10)
    {
        int S     = numSplits;
        int halfS = S / 2;

        // Filter sparse configs.
        var dense = configs.Where(c => c.Returns.Count >= S * 2).ToList();
        if (dense.Count < 2) return (double.NaN, 0, 0);

        int J = dense.Count;

        // Period boundaries per config (by trade count).
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

            // Logit(λ + 0.5): positive = IS-best above OOS median.
            double hi = Math.Clamp(lambda + 0.5, 1e-9, 1 - 1e-9);
            sumLogit += Math.Log(hi / (1 - hi));

            if (lambda < 0) below++;
            total++;
        }

        if (total == 0) return (double.NaN, 0, 0);
        return ((double)below / total, total, sumLogit / total);
    }

    // ── 3. White's Reality Check ──────────────────────────────────────────
    // H0: max_k E[f_k] ≤ 0. Null via circular block bootstrap. blockSize=0 → auto (√n).
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

                // Circular block bootstrap.
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
    // DSR at three T levels + PBO + WRC. gaTrials: nominal T for DSR (upper bound).
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

        // DSR at T = 1000 / gaTrials / 100000.
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

        // PBO: how reliably IS-optimal coin wins OOS.
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

        // WRC: does best coin's edge survive full-universe null?
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

    // ── Holm-Bonferroni step-down correction ──────────────────────────────
    // FWER control: threshold for i-th sorted p-value = α/(m-i+1).
    // Used in CombinedBacktest for 6-strategy family.
    public static bool[] HolmBonferroni(double[] pValues, double alpha = 0.05)
    {
        int m = pValues.Length;
        var indexed = pValues.Select((p, i) => (p, i)).OrderBy(x => x.p).ToArray();
        var rejected = new bool[m];

        for (int step = 0; step < m; step++)
        {
            double threshold = alpha / (m - step);
            if (indexed[step].p <= threshold)
                rejected[indexed[step].i] = true;
            else
                break;
        }
        return rejected;
    }

    public static double[] HolmBonferroniAdjustedPValues(double[] pValues)
    {
        int m = pValues.Length;
        var indexed = pValues.Select((p, i) => (p, i)).OrderBy(x => x.p).ToArray();
        var adjusted = new double[m];
        double cumMax = 0;

        for (int step = 0; step < m; step++)
        {
            double adj = indexed[step].p * (m - step);
            cumMax = Math.Max(cumMax, adj);
            adjusted[indexed[step].i] = Math.Min(cumMax, 1.0);
        }
        return adjusted;
    }

    // ── CVaR (Expected Shortfall) ─────────────────────────────────────────
    // Average loss in worst α% of returns. Coherent (subadditive).
    public static double CVaR(List<double> returns, double alpha = 0.05)
    {
        if (returns.Count == 0) return 0;
        var sorted = returns.OrderBy(r => r).ToList();
        int cutoff = Math.Max(1, (int)(sorted.Count * alpha));
        return sorted.Take(cutoff).Average();
    }

    // ── Combinations helper ───────────────────────────────────────────────

    private static IEnumerable<List<int>> Combinations(List<int> items, int k)
    {
        if (k == 0) { yield return []; yield break; }
        for (int i = 0; i <= items.Count - k; i++)
            foreach (var rest in Combinations(items[(i + 1)..], k - 1))
                yield return [items[i], .. rest];
    }

    // Effective independent sample size: counts contiguous blocks of trades separated by >gapHours.
    // Trades cluster by regime, so trade count overstates independence. Block count is a defensible lower bound.
    public static int EffectiveSampleSize(IReadOnlyList<DateTime> entryTimes, double gapHours = 24.0)
    {
        if (entryTimes.Count == 0) return 0;
        var sorted = entryTimes.OrderBy(t => t).ToList();
        int blocks = 1;
        for (int i = 1; i < sorted.Count; i++)
            if ((sorted[i] - sorted[i - 1]).TotalHours > gapHours) blocks++;
        return blocks;
    }

}
