using System;
using System.Collections.Generic;
using System.Linq;

namespace TradingGA;

// Statistical validation for trading strategy returns.
// Answers: "Is this edge real, or could it be luck?"
//
// Usage:
//   StrategyStats.Report("Swing", swingReturns, candleCount);
//   StrategyStats.Compare("Grid", gridReturns, "Swing", swingReturns, candleCount);
public static class StrategyStats
{
    // ── Result records ────────────────────────────────────────────────────────────

    public record TTestResult(double Mean, double StdErr, double T, double PValue)
    {
        public bool Sig95 => PValue < 0.05;
        public bool Sig99 => PValue < 0.01;
        public override string ToString() =>
            $"μ={Mean:+0.000;-0.000}%  SE={StdErr:F3}  t={T:F2}  p={PValue:F4}{(Sig99 ? " ***" : Sig95 ? " *" : "")}";
    }

    public record BootstrapResult(double Mean, double Lo95, double Hi95, double ProbPositive)
    {
        public override string ToString() =>
            $"μ={Mean:+0.000;-0.000}%  95%CI [{Lo95:+0.000;-0.000}%, {Hi95:+0.000;-0.000}%]  P(μ>0)={ProbPositive:P1}";
    }

    public record DistStats(double Skewness, double ExKurtosis, double CVaR5, double TailRatio)
    {
        public override string ToString() =>
            $"Skew={Skewness:+0.00;-0.00}  ExKurt={ExKurtosis:+0.00;-0.00}  CVaR(5%)={CVaR5:+0.00;-0.00}%  TailRatio={TailRatio:F2}";
    }

    public record MannWhitneyResult(double U, double Z, double PValue)
    {
        public bool Sig95 => PValue < 0.05;
        public override string ToString() =>
            $"U={U:F0}  z={Z:F2}  p={PValue:F4}{(Sig95 ? " *" : "")}";
    }

    // ── 1. One-sample t-test: H₀: mean return = 0 ────────────────────────────────
    // Tests whether the average trade return is significantly different from zero.
    // Uses normal approximation for the p-value (accurate for n > 20).
    public static TTestResult OneSampleT(List<double> returns)
    {
        if (returns.Count < 5) return new(0, 0, 0, 1);
        double mean = returns.Average();
        double var  = returns.Select(r => (r - mean) * (r - mean)).Average();
        double se   = Math.Sqrt(var / returns.Count);
        double t    = se > 1e-12 ? mean / se : 0;
        double p    = TwoTailedP(t);
        return new(mean, se, t, p);
    }

    // ── 2. Bootstrap 95% CI for mean return ──────────────────────────────────────
    // Non-parametric: no assumption about return distribution shape.
    // P(μ>0) = fraction of bootstrap samples with positive mean.
    public static BootstrapResult Bootstrap(List<double> returns, int n = 5000, int seed = 42)
    {
        if (returns.Count < 5) return new(0, 0, 0, 0);
        var arr = returns.ToArray();
        var rng = new Random(seed);
        var means = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < arr.Length; j++)
                s += arr[rng.Next(arr.Length)];
            means[i] = s / arr.Length;
        }
        Array.Sort(means);
        double probPos = (double)means.Count(m => m > 0) / n;
        return new(
            returns.Average(),
            means[(int)(0.025 * n)],
            means[(int)(0.975 * n)],
            probPos);
    }

    // ── 3. Return distribution statistics ────────────────────────────────────────
    // Skewness: positive = right-tail fat (big wins more common than big losses) — good.
    // Excess kurtosis: > 0 = fat tails (more extreme outcomes than normal).
    // CVaR 5%: average loss in the worst 5% of trades (expected shortfall).
    // Tail ratio: |P95| / |P5| — how big wins are relative to losses at extremes.
    public static DistStats Distribution(List<double> returns)
    {
        if (returns.Count < 5) return new(0, 0, 0, 0);
        double mean = returns.Average();
        double n    = returns.Count;
        double m2   = returns.Select(r => Math.Pow(r - mean, 2)).Sum() / n;
        double m3   = returns.Select(r => Math.Pow(r - mean, 3)).Sum() / n;
        double m4   = returns.Select(r => Math.Pow(r - mean, 4)).Sum() / n;
        double std  = Math.Sqrt(m2);

        double skew     = std > 1e-10 ? m3 / Math.Pow(std, 3) : 0;
        double exKurt   = std > 1e-10 ? m4 / (m2 * m2) - 3.0 : 0;

        var sorted = returns.OrderBy(r => r).ToList();
        int tail5  = Math.Max(1, (int)(0.05 * returns.Count));
        double cvar = sorted.Take(tail5).Average();

        // P95 / |P5| — values above 1 mean wins at the 95th pct outsize losses at 5th
        double p5   = sorted[(int)(0.05 * (sorted.Count - 1))];
        double p95  = sorted[(int)(0.95 * (sorted.Count - 1))];
        double tail = Math.Abs(p5) > 1e-10 ? p95 / Math.Abs(p5) : 0;

        return new(skew, exKurt, cvar, tail);
    }

    // ── 4. Kelly fraction ─────────────────────────────────────────────────────────
    // Optimal fraction of capital per trade given observed win rate and W/L ratio.
    // Half-Kelly is the standard risk-adjusted recommendation.
    public static (double Kelly, double HalfKelly) KellyFraction(List<double> returns)
    {
        if (returns.Count < 5) return (0, 0);
        var wins   = returns.Where(r => r > 0).ToList();
        var losses = returns.Where(r => r <= 0).ToList();
        if (wins.Count == 0 || losses.Count == 0) return (0, 0);
        double p    = (double)wins.Count / returns.Count;
        double b    = wins.Average() / Math.Abs(losses.Average());
        double k    = Math.Max(0, (p * b - (1 - p)) / b);
        return (k, k / 2.0);
    }

    // ── 5. Mann-Whitney U test: do the two return distributions differ? ──────────
    // Non-parametric: does not assume normality.
    // Tests H₀: returns from A and B come from the same distribution.
    public static MannWhitneyResult MannWhitneyU(List<double> a, List<double> b)
    {
        int n1 = a.Count, n2 = b.Count;
        if (n1 < 5 || n2 < 5) return new(0, 0, 1);

        // Rank all values, handling ties with average rank
        var ranked = a.Select(v => (v, grp: 0))
                      .Concat(b.Select(v => (v, grp: 1)))
                      .OrderBy(x => x.v)
                      .ToList();

        double[] ranks = new double[ranked.Count];
        int i = 0;
        while (i < ranked.Count)
        {
            int j = i;
            while (j < ranked.Count && ranked[j].v == ranked[i].v) j++;
            double avgRank = (i + 1 + j) / 2.0;
            for (int k = i; k < j; k++) ranks[k] = avgRank;
            i = j;
        }

        double r1 = ranked.Select((x, idx) => x.grp == 0 ? ranks[idx] : 0).Sum();
        double u1 = r1 - n1 * (n1 + 1.0) / 2.0;
        double u  = Math.Min(u1, n1 * n2 - u1);

        // Tie correction for normal approximation
        var tieGroups = ranks.GroupBy(r => r).Select(g => (double)g.Count()).ToList();
        double tieCorr = tieGroups.Select(t => t * t * t - t).Sum();
        int N = n1 + n2;
        double stdU = Math.Sqrt((n1 * n2 / 12.0) * (N + 1 - tieCorr / (N * (N - 1))));

        double z = stdU > 1e-10 ? (u - n1 * n2 / 2.0) / stdU : 0;
        double p = TwoTailedP(z);
        return new(u, z, p);
    }

    // ── 6. Deflated Sharpe ratio ──────────────────────────────────────────────────
    // Adjusts the Sharpe downward to account for selection bias from GA search.
    // nTrials = effective number of parameter combinations tested (GA pop × gens ≈ 1000).
    // DSR < 0 means the edge is likely not significant after correcting for search.
    public static double DeflatedSharpe(List<double> returns, int candleCount, int nTrials = 1000)
    {
        double sr = Simulator.SharpeRatio(returns, candleCount);
        if (sr <= 0) return 0;
        // Expected max Sharpe under null via extreme value theory
        double expectedMax = Math.Sqrt(2 * Math.Log(nTrials)) -
                             (Math.Log(Math.Log(nTrials)) + Math.Log(4 * Math.PI)) /
                             (2 * Math.Sqrt(2 * Math.Log(nTrials)));
        double sdSharpe = Math.Sqrt((1 + 0.5 * sr * sr) / (returns.Count - 1));
        return NormalCDF((sr - expectedMax) / sdSharpe);
    }

    // ── Gene count per trained strategy (excludes Fitness field) ────────────────
    // Source: count of JSON fields in each *_genotype.json (Fitness excluded).
    // Used for VC-dimension-style generalization analysis (Abu-Mustafa / LFD).
    private static readonly Dictionary<string, int> GenesPerStrategy =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FadeShort"]    = 15,  // EmaPeriod..PositionSizePct
            ["FadeLong"]     = 15,  // protection mode moved to DynamicGuard
            ["DipLong"]      = 14,  // protection mode moved to DynamicGuard
            ["SwingLong"]    = 14,  // EmaPeriod..TimeStopLossPct
            ["Grid"]         =  9,  // AdxThreshold..MaxHoldCandles
            ["Router"]       = 10,  // BullMinBars..EarlyBullBearCarry
            ["DynamicGuard"] = 15,  // AtrLookback..ProfitProtectFactor
        };

    // N/d ratio — Abu-Mustafa rule of thumb for VC generalization:
    // need N >> d_vc. Dangerous < 5, borderline 5–10, ok 10–20, good > 30.
    private static (double Ratio, string Verdict) NdRatio(int n, int d)
    {
        double r = (double)n / d;
        string v = r < 5  ? "! severely undersampled" :
                   r < 10 ? "~ borderline" :
                   r < 20 ? "ok" : "good";
        return (r, v);
    }

    // Hoeffding bound on mean: given N iid bounded samples, the true mean
    // is within ±ε of the observed mean with probability ≥ 1-α.
    // ε = (max-min) · sqrt(ln(2/α) / (2N))
    private static double HoeffdingMeanBound(int n, double minR, double maxR, double alpha = 0.05)
    {
        if (n < 1) return double.PositiveInfinity;
        return (maxR - minR) * Math.Sqrt(Math.Log(2.0 / alpha) / (2.0 * n));
    }

    // Minimum N to certify |observed_mean - true_mean| ≤ epsilon with prob ≥ 1-α.
    // N = (max-min)² · ln(2/α) / (2ε²)
    public static int HoeffdingMinN(double minR, double maxR, double epsilon, double alpha = 0.05)
    {
        double range = maxR - minR;
        return (int)Math.Ceiling(range * range * Math.Log(2.0 / alpha) / (2.0 * epsilon * epsilon));
    }

    // ── 7. Full strategy report ────────────────────────────────────────────────────
    public static void Report(string name, List<double> returns, int candleCount)
    {
        Console.WriteLine($"\n── Statistical report: {name} ({returns.Count} trades) ──");
        if (returns.Count < 10) { Console.WriteLine("  Insufficient trades for statistics."); return; }

        var t    = OneSampleT(returns);
        var boot = Bootstrap(returns);
        var dist = Distribution(returns);
        var (kelly, halfKelly) = KellyFraction(returns);
        double dsr = DeflatedSharpe(returns, candleCount);

        Console.WriteLine($"  t-test:      {t}");
        Console.WriteLine($"  Bootstrap:   {boot}");
        Console.WriteLine($"  Dist:        {dist}");
        Console.WriteLine($"  Kelly:       full={kelly:P1}  half={halfKelly:P1}  (using 5% cap)");
        Console.WriteLine($"  DSR:         {dsr:F3}  {(dsr > 0.95 ? "✓ strong" : dsr > 0.5 ? "~ moderate" : "✗ weak after GA search bias")}");

        if (GenesPerStrategy.TryGetValue(name, out int d))
        {
            double minR   = returns.Min();
            double maxR   = returns.Max();
            double hBound = HoeffdingMeanBound(returns.Count, minR, maxR);
            int    minN05 = HoeffdingMinN(minR, maxR, epsilon: 0.5);
            int    minN10 = HoeffdingMinN(minR, maxR, epsilon: 1.0);
            var (ratio, verdict) = NdRatio(returns.Count, d);
            Console.WriteLine($"  VC:          d={d}  N/d={ratio:F1} {verdict}");
            Console.WriteLine($"  Hoeffding:   worst-case bound on mean: ±{hBound:F2}%  (range [{minR:+0.1f;-0.1f}%, {maxR:+0.1f}%])");
            Console.WriteLine($"               certify ±0.5%: need {minN05} trades  |  certify ±1.0%: need {minN10} trades");
            if (returns.Count < 10 * d)
                Console.WriteLine($"  ⚠  N < 10d ({returns.Count} < {10 * d}) — VC bound loose; rely on WFV + bootstrap, not raw E_in");
        }

        if (!t.Sig95 && boot.ProbPositive < 0.9)
            Console.WriteLine("  ⚠  Edge not statistically significant — could be luck");
        if (dist.Skewness < -0.5)
            Console.WriteLine("  ⚠  Negative skew — losses heavier than wins at extremes");
        if (dist.CVaR5 < -3.0)
            Console.WriteLine($"  ⚠  CVaR(5%) = {dist.CVaR5:F1}% — worst 5% of trades average this loss");
    }

    // ── 8. Bayesian bootstrap: P(A beats B) on mean and Sharpe ───────────────────
    // Uses Dirichlet(1,…,1) reweighting — the posterior under a flat prior.
    // Each draw samples a different weight vector over the observed data points
    // rather than resampling with replacement, giving a proper Bayesian posterior.
    public record BayesResult(double ProbMeanA, double ProbSharpeA, double ProbPFA)
    {
        public override string ToString() =>
            $"P(μ_A>μ_B)={ProbMeanA:P1}  P(Sh_A>Sh_B)={ProbSharpeA:P1}  P(PF_A>PF_B)={ProbPFA:P1}";
    }

    public static BayesResult BayesianComparison(
        List<double> a, List<double> b,
        int candleCount, int nDraws = 5000, int seed = 42)
    {
        if (a.Count < 5 || b.Count < 5) return new(0.5, 0.5, 0.5);
        var arrA = a.ToArray();
        var arrB = b.ToArray();
        var rng  = new Random(seed);

        int meanWins = 0, sharpeWins = 0, pfWins = 0;

        for (int d = 0; d < nDraws; d++)
        {
            // Dirichlet(1,…,1) weights = normalised Exponential(1) draws
            double[] wA = DirichletWeights(arrA.Length, rng);
            double[] wB = DirichletWeights(arrB.Length, rng);

            double mA = WeightedMean(arrA, wA);
            double mB = WeightedMean(arrB, wB);
            double vA = WeightedVariance(arrA, wA, mA);
            double vB = WeightedVariance(arrB, wB, mB);

            double srA = vA > 1e-12 ? mA / Math.Sqrt(vA) * Math.Sqrt(candleCount / 288.0) : 0;
            double srB = vB > 1e-12 ? mB / Math.Sqrt(vB) * Math.Sqrt(candleCount / 288.0) : 0;

            double gWA = 0, lWA = 0, gWB = 0, lWB = 0;
            for (int i = 0; i < arrA.Length; i++) { if (arrA[i] > 0) gWA += arrA[i] * wA[i]; else lWA += Math.Abs(arrA[i]) * wA[i]; }
            for (int i = 0; i < arrB.Length; i++) { if (arrB[i] > 0) gWB += arrB[i] * wB[i]; else lWB += Math.Abs(arrB[i]) * wB[i]; }
            double pfA = lWA > 1e-12 ? gWA / lWA : 0;
            double pfB = lWB > 1e-12 ? gWB / lWB : 0;

            if (mA  > mB)  meanWins++;
            if (srA > srB) sharpeWins++;
            if (pfA > pfB) pfWins++;
        }

        return new(
            (double)meanWins   / nDraws,
            (double)sharpeWins / nDraws,
            (double)pfWins     / nDraws);
    }

    private static double[] DirichletWeights(int n, Random rng)
    {
        var w = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++) { w[i] = -Math.Log(rng.NextDouble() + 1e-300); sum += w[i]; }
        for (int i = 0; i < n; i++) w[i] /= sum;
        return w;
    }

    private static double WeightedMean(double[] arr, double[] w)
    {
        double s = 0;
        for (int i = 0; i < arr.Length; i++) s += arr[i] * w[i];
        return s;
    }

    private static double WeightedVariance(double[] arr, double[] w, double mean)
    {
        double s = 0;
        for (int i = 0; i < arr.Length; i++) s += w[i] * (arr[i] - mean) * (arr[i] - mean);
        return s;
    }

    // ── 9. Compare two strategies ─────────────────────────────────────────────────
    public static void Compare(
        string nameA, List<double> a,
        string nameB, List<double> b,
        int candleCount)
    {
        Console.WriteLine($"\n── Comparison: {nameA} vs {nameB} ──");

        var tA   = OneSampleT(a);
        var tB   = OneSampleT(b);
        var mw   = MannWhitneyU(a, b);
        var bA   = Bootstrap(a);
        var bB   = Bootstrap(b);
        var bay  = BayesianComparison(a, b, candleCount);

        double srA = Simulator.SharpeRatio(a, candleCount);
        double srB = Simulator.SharpeRatio(b, candleCount);
        double pfA = Simulator.ProfitFactor(a);
        double pfB = Simulator.ProfitFactor(b);

        Console.WriteLine($"  {"Metric",-24} {nameA,12} {nameB,12}");
        Console.WriteLine($"  {"Trades",-24} {a.Count,12} {b.Count,12}");
        Console.WriteLine($"  {"Mean return",-24} {tA.Mean,+11:F3}% {tB.Mean,+11:F3}%");
        Console.WriteLine($"  {"t (vs 0)",-24} {tA.T,12:F2} {tB.T,12:F2}");
        Console.WriteLine($"  {"p-value",-24} {tA.PValue,12:F4} {tB.PValue,12:F4}");
        Console.WriteLine($"  {"P(μ>0) bootstrap",-24} {bA.ProbPositive,12:P1} {bB.ProbPositive,12:P1}");
        Console.WriteLine($"  {"Sharpe",-24} {srA,12:F3} {srB,12:F3}");
        Console.WriteLine($"  {"Profit factor",-24} {pfA,12:F3} {pfB,12:F3}");

        Console.WriteLine($"\n  Frequentist — Mann-Whitney: {mw}");
        if (mw.Sig95)
            Console.WriteLine($"  → Distributions are significantly different (p<0.05)");
        else
            Console.WriteLine($"  → No significant difference in distributions");

        Console.WriteLine($"\n  Bayesian — {bay}");
        string winner = bay.ProbSharpeA > 0.5 ? nameA : nameB;
        double conf   = Math.Max(bay.ProbSharpeA, 1 - bay.ProbSharpeA);
        Console.WriteLine($"  → Posterior favours {winner} on risk-adjusted return ({conf:P0} credibility)");
    }

    // ── Internal: normal CDF (Zelen & Severo 1964, max error ~7.5×10⁻⁸) ─────────
    private static double NormalCDF(double x)
    {
        double t    = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
        double poly = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 +
                      t * (-1.821255978 + t * 1.330274429))));
        double cdf  = 1.0 - (1.0 / Math.Sqrt(2 * Math.PI)) * Math.Exp(-0.5 * x * x) * poly;
        return x >= 0 ? cdf : 1.0 - cdf;
    }

    private static double TwoTailedP(double z) => 2.0 * (1.0 - NormalCDF(Math.Abs(z)));
}

// ── Crash / stress-test analyser ─────────────────────────────────────────────────────────
// Identifies historical crash windows from BTC price action, then for each window shows
// which positions were open, total concurrent exposure, and realised outcome.
// Also computes a synthetic "all stops hit simultaneously" worst-case scenario.
public static class CrashAnalyser
{
    public record CrashEvent(string Label, DateTime Start, DateTime End, double BtcDropPct, int DurationH);

    // Rolling-peak drawdown: crash = BTC close > minDropPct below its high over peakLookbackH bars.
    public static List<CrashEvent> DetectCrashes(
        Candle[] btcH1, double minDropPct = 0.15, int peakLookbackH = 168)
    {
        if (btcH1.Length <= peakLookbackH) return [];

        var inCrash = new bool[btcH1.Length];
        for (int i = peakLookbackH; i < btcH1.Length; i++)
        {
            double peak = 0;
            for (int j = i - peakLookbackH; j < i; j++)
                if (btcH1[j].Close > peak) peak = btcH1[j].Close;
            inCrash[i] = (btcH1[i].Close - peak) / peak < -minDropPct;
        }

        var raw = new List<CrashEvent>();
        int start = -1;
        for (int i = peakLookbackH; i <= btcH1.Length; i++)
        {
            bool flag = i < btcH1.Length && inCrash[i];
            if (flag && start < 0) start = i;
            else if (!flag && start >= 0)
            {
                int dur = i - start;
                if (dur >= 12)
                {
                    double peakClose = 0;
                    for (int j = Math.Max(0, start - peakLookbackH); j < start; j++)
                        if (btcH1[j].Close > peakClose) peakClose = btcH1[j].Close;
                    double trough  = btcH1.Skip(start).Take(dur).Min(c => c.Close);
                    double dropPct = (trough - peakClose) / peakClose * 100.0;
                    raw.Add(new(btcH1[start].Time.ToString("yyyy-MM-dd"),
                                btcH1[start].Time, btcH1[i - 1].Time, dropPct, dur));
                }
                start = -1;
            }
        }
        return MergeEvents(raw, mergeGapH: 72);
    }

    private static List<CrashEvent> MergeEvents(List<CrashEvent> events, int mergeGapH)
    {
        if (events.Count == 0) return events;
        var merged = new List<CrashEvent> { events[0] };
        for (int i = 1; i < events.Count; i++)
        {
            var prev = merged[^1];
            if ((events[i].Start - prev.End).TotalHours <= mergeGapH)
                merged[^1] = prev with
                {
                    End        = events[i].End,
                    BtcDropPct = Math.Min(prev.BtcDropPct, events[i].BtcDropPct),
                    DurationH  = (int)(events[i].End - prev.Start).TotalHours
                };
            else merged.Add(events[i]);
        }
        return merged;
    }

    // For each detected crash window: show which trades were open, total exposure, realised outcome.
    public static void Report(
        List<CrashEvent> crashes,
        List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)> trades)
    {
        Console.WriteLine("\n═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  CRASH STRESS TEST  (historical BTC ≥ 15% drawdown windows)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");

        if (crashes.Count == 0)
        {
            Console.WriteLine("\n  No crash windows detected in this data range.");
            Console.WriteLine("  Classic crashes outside the 3yr window:");
            PrintHistoricCrashes();
            return;
        }

        Console.WriteLine($"\n  {"Window",-14} {"BTC",8} {"Dur",6} {"Open pos",9} {"Exposure",9} {"W/L",6} {"Avg ret",8} {"Portfolio hit",14}");
        Console.WriteLine($"  {new string('─', 80)}");

        double totalPortHit = 0;
        foreach (var crash in crashes)
        {
            var active = trades
                .Where(t => t.Open <= crash.Start && t.Close >= crash.Start)
                .ToList();

            if (active.Count == 0)
            {
                Console.WriteLine($"  {crash.Label,-14} {crash.BtcDropPct,+7:F1}% {crash.DurationH,5}h  (no open positions)");
                continue;
            }

            double exposure = active.Sum(t => t.HalfKelly) * 100.0;

            // Trades that closed within the crash window — we know their actual outcome
            var resolved = trades
                .Where(t => t.Open <= crash.Start && t.Close >= crash.Start && t.Close <= crash.End.AddHours(24))
                .ToList();

            int wins   = resolved.Count(t => t.Return > 0);
            int losses = resolved.Count(t => t.Return <= 0);
            double avgRet = resolved.Count > 0 ? resolved.Average(t => t.Return) : double.NaN;

            // Rough portfolio hit: sum of (halfKelly × return) for resolved trades
            double portHit = resolved.Sum(t => t.HalfKelly * t.Return / 100.0) * 100.0;
            totalPortHit += portHit;

            string wl     = $"{wins}W/{losses}L";
            string avgStr = double.IsNaN(avgRet) ? "    n/a" : $"{avgRet,+7:F2}%";
            Console.WriteLine($"  {crash.Label,-14} {crash.BtcDropPct,+7:F1}% {crash.DurationH,5}h  {active.Count,8}  {exposure,8:F1}%  {wl,6}  {avgStr}  {portHit,+12:F2}€/100");
        }
        Console.WriteLine($"  {new string('─', 80)}");
        Console.WriteLine($"  {"Total across all crashes",-50} {totalPortHit,+12:F2}€/100");

        Console.WriteLine("\n  Classic crashes NOT in this dataset (3yr window ≈ 2023-2026):");
        PrintHistoricCrashes();
    }

    // Find the moment of maximum concurrent half-Kelly exposure and compute all-stop-out scenario.
    public static void SyntheticWorstCase(
        List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)> trades,
        double gridStopPct  = 1.5,   // 1.5 ATR × avg h1 ATR ~1%   → ~1.5% per grid position
        double swingStopPct = 4.9)   // 1.64 ATR × avg h4 ATR ~3%  → ~4.9% per swing position
    {
        if (trades.Count == 0) return;

        // Sweep line to find the peak concurrent total half-Kelly
        const double MaxKellyPerPosition = 0.15;
        var events = new List<(DateTime T, double D, bool IsGrid)>();
        foreach (var t in trades)
        {
            bool   grid   = t.Strategy == "grid";
            double capped = Math.Min(t.HalfKelly, MaxKellyPerPosition);
            events.Add((t.Open,  +capped, grid));
            events.Add((t.Close, -capped, grid));
        }
        events.Sort((a, b) => a.T.CompareTo(b.T));

        double total = 0, gridExp = 0, swingExp = 0;
        double peakTotal = 0, peakGrid = 0, peakSwing = 0;
        DateTime peakTime = events[0].T;

        foreach (var (t, d, isGrid) in events)
        {
            total += d;
            if (isGrid) gridExp += d; else swingExp += d;
            if (total > peakTotal)
            {
                peakTotal = total; peakGrid = gridExp; peakSwing = swingExp; peakTime = t;
            }
        }

        double maxLossEur = (peakGrid  * gridStopPct + peakSwing * swingStopPct);

        Console.WriteLine("\n  ── Synthetic worst case: all concurrent positions stop out at once ──");
        Console.WriteLine($"  Peak moment:      {peakTime:yyyy-MM-dd HH:mm UTC}");
        Console.WriteLine($"  Total exposure:   {peakTotal * 100:F1}%  (grid {peakGrid * 100:F1}%  swing {peakSwing * 100:F1}%)");
        Console.WriteLine($"  Grid stop loss:   {gridStopPct:F1}% avg per position  (1.5 ATR × h1 ATR ≈ 1%)");
        Console.WriteLine($"  Swing stop loss:  {swingStopPct:F1}% avg per position (1.64 ATR × h4 ATR ≈ 3%)");
        Console.WriteLine($"  Max portfolio hit (clean stops):  -{maxLossEur:F2}€  on €100  ({-maxLossEur:F1}%)");
        Console.WriteLine($"  With 3× ATR expansion (crash):   -{maxLossEur * 3:F2}€  on €100  ({-maxLossEur * 3:F1}%)");

        // Historical scenario projections using actual crash ATR expansion multiples
        Console.WriteLine("\n  ── Historical crash projections (at peak exposure above) ──");
        Console.WriteLine($"  {"Scenario",-20}  {"BTC drop",9}  {"Duration",9}  {"ATR ×",6}  {"Clean stops",12}  {"With slippage",14}");
        Console.WriteLine($"  {new string('─', 80)}");
        var historic = new (string Name, double DropPct, string Dur, double AtrMult)[]
        {
            ("COVID  Mar 2020", -50.0, "48h",  8.0),
            ("LUNA   May 2022", -40.0, "7d",   5.0),
            ("FTX    Nov 2022", -30.0, "5d",   3.0),
        };
        foreach (var (name, drop, dur, atrMult) in historic)
        {
            double clean = maxLossEur;
            double slip  = maxLossEur * atrMult;
            Console.WriteLine($"  {name,-20}  {drop,+8:F0}%  {dur,9}  {atrMult,5:F0}×  -{clean,10:F1}%  -{slip,12:F1}%");
        }
    }

    // Rolling-trough rally: rally = BTC close > minRisePct above its low over troughLookbackH bars.
    public static List<CrashEvent> DetectRallies(
        Candle[] btcH1, double minRisePct = 0.15, int troughLookbackH = 168)
    {
        if (btcH1.Length <= troughLookbackH) return [];

        var inRally = new bool[btcH1.Length];
        for (int i = troughLookbackH; i < btcH1.Length; i++)
        {
            double trough = double.MaxValue;
            for (int j = i - troughLookbackH; j < i; j++)
                if (btcH1[j].Close < trough) trough = btcH1[j].Close;
            inRally[i] = (btcH1[i].Close - trough) / trough > minRisePct;
        }

        var raw = new List<CrashEvent>();
        int start = -1;
        for (int i = troughLookbackH; i <= btcH1.Length; i++)
        {
            bool flag = i < btcH1.Length && inRally[i];
            if (flag && start < 0) start = i;
            else if (!flag && start >= 0)
            {
                int dur = i - start;
                if (dur >= 12)
                {
                    double troughClose = double.MaxValue;
                    for (int j = Math.Max(0, start - troughLookbackH); j < start; j++)
                        if (btcH1[j].Close < troughClose) troughClose = btcH1[j].Close;
                    double peak    = btcH1.Skip(start).Take(dur).Max(c => c.Close);
                    double risePct = (peak - troughClose) / troughClose * 100.0;
                    raw.Add(new(btcH1[start].Time.ToString("yyyy-MM-dd"),
                                btcH1[start].Time, btcH1[i - 1].Time, risePct, dur));
                }
                start = -1;
            }
        }
        return MergeEvents(raw, mergeGapH: 72);
    }

    // Same table format as Report() but for upward rally windows.
    public static void ReportRallies(
        List<CrashEvent> rallies,
        List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)> trades)
    {
        Console.WriteLine("\n═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  RALLY STRESS TEST  (historical BTC ≥ 15% rise from 7d trough)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");

        if (rallies.Count == 0)
        {
            Console.WriteLine("\n  No rally windows detected in this data range.");
            return;
        }

        Console.WriteLine($"\n  {"Window",-14} {"BTC",8} {"Dur",6} {"Open pos",9} {"Exposure",9} {"W/L",6} {"Avg ret",8} {"Portfolio hit",14}");
        Console.WriteLine($"  {new string('─', 80)}");

        double totalPortHit = 0;
        foreach (var rally in rallies)
        {
            var active = trades
                .Where(t => t.Open <= rally.Start && t.Close >= rally.Start)
                .ToList();

            if (active.Count == 0)
            {
                Console.WriteLine($"  {rally.Label,-14} {rally.BtcDropPct,+7:F1}% {rally.DurationH,5}h  (no open positions)");
                continue;
            }

            double exposure = active.Sum(t => t.HalfKelly) * 100.0;

            var resolved = trades
                .Where(t => t.Open <= rally.Start && t.Close >= rally.Start && t.Close <= rally.End.AddHours(24))
                .ToList();

            int wins   = resolved.Count(t => t.Return > 0);
            int losses = resolved.Count(t => t.Return <= 0);
            double avgRet  = resolved.Count > 0 ? resolved.Average(t => t.Return) : double.NaN;
            double portHit = resolved.Sum(t => t.HalfKelly * t.Return / 100.0) * 100.0;
            totalPortHit  += portHit;

            string wl     = $"{wins}W/{losses}L";
            string avgStr = double.IsNaN(avgRet) ? "    n/a" : $"{avgRet,+7:F2}%";
            Console.WriteLine($"  {rally.Label,-14} {rally.BtcDropPct,+7:F1}% {rally.DurationH,5}h  {active.Count,8}  {exposure,8:F1}%  {wl,6}  {avgStr}  {portHit,+12:F2}€/100");
        }
        Console.WriteLine($"  {new string('─', 80)}");
        Console.WriteLine($"  {"Total across all rallies",-50} {totalPortHit,+12:F2}€/100");
    }

    private static void PrintHistoricCrashes()
    {
        Console.WriteLine("    COVID  Mar 2020: BTC -50% in 48h  → all grid longs stop-out, ATR expanded 5-8×");
        Console.WriteLine("    LUNA   May 2022: BTC -40% in 7d   → sustained cascade, multiple waves of stops");
        Console.WriteLine("    FTX    Nov 2022: BTC -30% in 5d   → correlated stop-out, thin liquidity on alts");
        Console.WriteLine("  In these events: synthetic loss above × 3-5× due to gap slippage on ATR expansion.");
    }
}
