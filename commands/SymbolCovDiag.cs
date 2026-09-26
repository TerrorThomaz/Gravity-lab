using Bybit.Net.Clients;

namespace TradingGA;

// How many independent bets does this universe actually contain?
//
// Every OOS claim in this repo is quoted in coins — "42 never-seen coins", "190 symbols". That
// counts observations as if the symbols were independent, and in crypto they are emphatically not:
// one market factor drives most of the variance, so N coins can carry closer to 2 or 3 genuinely
// independent observations. Every confidence interval computed on the coin count is then far too
// tight, walkforward's included.
//
// This command measures the number instead of assuming it. It is READ-ONLY and descriptive: no
// genotype is loaded, no simulator runs, nothing is sized or routed. Wired into Program.cs as
// `symbolcov`.
//
// What it reports:
//   1. Ledoit-Wolf shrinkage intensity actually chosen for this matrix (analytic, not hand-picked)
//   2. PC1's share of variance — in crypto this is the market factor, and if it dominates then the
//      BTC anchor is being validated rather than replaced
//   3. Effective bets (participation ratio) for the whole universe AND for the OOS subset alone
//   4. Stress vs calm correlations — crypto correlations converge toward 1 in exactly the
//      drawdowns DynamicGuard exists to handle, so a family structure fitted on average conditions
//      overstates diversification when it matters most. This quantifies that on your own data.
//   5. Deterministic co-movement families
//
// Statistics are computed on the CORRELATION matrix, not covariance, so shares and the
// participation ratio are scale-free and a high-ATR coin does not dominate by volatility alone.
public static class SymbolCovDiag
{
    private const int    MinWindowDays  = 365;
    private const int    StaleDays      = 30;
    private const double FamilyMaxDist  = 0.40;   // merge while average correlation > 0.60
    private const double StressQuantile = 0.75;   // top quartile of |BTC daily return| = "stress"

    public static async Task Run(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | SYMBOL COVARIANCE DIAGNOSTIC (read-only) ===\n");

        var syms = Config.BacktestCoins
            .Concat(Config.OosCoins)
            .Concat(new[] { "BTCUSDT", "ETHUSDT" })
            .Distinct()
            .ToArray();
        Console.WriteLine($"  Universe: {syms.Length} symbols (BacktestCoins + OosCoins + BTC/ETH)");

        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, syms, batches: 113);

        var daily = fetched
            .Select(f => SymbolCovariance.ToDaily(f.sym, f.h1))
            .Where(d => d != null)
            .Select(d => d!)
            .ToList();
        Console.WriteLine($"  Reduced to daily closes: {daily.Count} symbols\n");

        var window = SymbolCovariance.SelectWindow(daily, MinWindowDays, StaleDays);
        if (window == null) { Console.WriteLine("  No common window of sufficient length — nothing to report."); return; }

        var rm = SymbolCovariance.BuildReturns(daily, window);
        if (rm == null || rm.Symbols.Length < 2) { Console.WriteLine("  Too few symbols span the window."); return; }

        int k = rm.Symbols.Length;
        int n = rm.Returns[0].Length;
        Console.WriteLine($"  Window: {window.Start:yyyy-MM-dd} → {window.End:yyyy-MM-dd}  ({window.Days} days, {n} daily returns)");
        Console.WriteLine($"  Kept {k} symbols · dropped {window.Dropped.Length} (listed after the window start, or stale)");
        if (window.Dropped.Length > 0)
            Console.WriteLine($"    dropped: {string.Join(" ", window.Dropped.OrderBy(s => s))}");
        Console.WriteLine($"  Forward-filled missing days: {rm.FilledGaps}\n");

        // ── Full universe ────────────────────────────────────────────────────────────────────
        var full = Analyse(rm.Returns);
        if (full == null) { Console.WriteLine("  Degenerate covariance — nothing to report."); return; }

        Console.WriteLine("── Full universe ──────────────────────────────────────────────");
        PrintBlock(full, k, n);

        double[] ev = full.Eigenvalues;
        double totalVar = ev.Sum();
        if (totalVar > 1e-12)
        {
            Console.Write("  Variance share by component: ");
            for (int i = 0; i < Math.Min(5, ev.Length); i++)
                Console.Write($"PC{i + 1} {ev[i] / totalVar * 100,5:F1}%   ");
            Console.WriteLine();
            double cum5 = ev.Take(Math.Min(5, ev.Length)).Sum() / totalVar * 100;
            Console.WriteLine($"  PC1–PC5 cumulative: {cum5:F1}%");
            Console.WriteLine("    (in crypto PC1 is the market factor — a dominant PC1 means the BTC anchor is being");
            Console.WriteLine("     confirmed by the data, not replaced by it; the rotation content is in PC2 onward)");
        }
        Console.WriteLine();

        // ── OOS subset: the denominator behind every "never-seen coins" claim ─────────────────
        var oosSet = new HashSet<string>(Config.OosCoins);
        var oosIdx = Enumerable.Range(0, k).Where(i => oosSet.Contains(rm.Symbols[i])).ToArray();
        if (oosIdx.Length >= 2)
        {
            var oosSeries = oosIdx.Select(i => rm.Returns[i]).ToArray();
            var oos = Analyse(oosSeries);
            if (oos != null)
            {
                Console.WriteLine("── OOS coins only (walkforward / oosbacktest universe) ────────");
                PrintBlock(oos, oosIdx.Length, n);
                Console.WriteLine($"  → an OOS result quoted over {oosIdx.Length} never-seen coins carries roughly");
                Console.WriteLine($"    {oos.EffectiveBets:F1} independent observations, not {oosIdx.Length}.");
                Console.WriteLine("    Intervals computed on the coin count are too tight by about");
                Console.WriteLine($"    sqrt({oosIdx.Length}/{oos.EffectiveBets:F1}) ≈ {Math.Sqrt(oosIdx.Length / Math.Max(oos.EffectiveBets, 1e-9)):F1}×.\n");
            }
        }

        // ── Stress vs calm ───────────────────────────────────────────────────────────────────
        int btc = Array.IndexOf(rm.Symbols, "BTCUSDT");
        if (btc >= 0)
        {
            var mag = rm.Returns[btc].Select(Math.Abs).OrderBy(v => v).ToArray();
            double cut = mag[Math.Min(mag.Length - 1, (int)(mag.Length * StressQuantile))];

            var stressDays = Enumerable.Range(0, n).Where(d => Math.Abs(rm.Returns[btc][d]) >= cut).ToArray();
            var calmDays   = Enumerable.Range(0, n).Where(d => Math.Abs(rm.Returns[btc][d]) <  cut).ToArray();

            if (stressDays.Length >= 30 && calmDays.Length >= 30)
            {
                double stressCorr = MeanCorrOn(rm.Returns, k, stressDays);
                double calmCorr   = MeanCorrOn(rm.Returns, k, calmDays);

                Console.WriteLine("── Stress vs calm (split on |BTC daily return|) ───────────────");
                Console.WriteLine($"  threshold: |BTC daily| ≥ {cut * 100:F2}%  (top {(1 - StressQuantile) * 100:F0}% of days)");
                Console.WriteLine($"  calm   n={calmDays.Length,5}  mean off-diagonal correlation {calmCorr:F3}");
                Console.WriteLine($"  stress n={stressDays.Length,5}  mean off-diagonal correlation {stressCorr:F3}");
                Console.WriteLine($"  → correlation rises {(stressCorr - calmCorr) * 100:+0.0;-0.0} pp on the worst BTC days");
                Console.WriteLine("    (mean off-diagonal is used here rather than effective bets: it is pairwise, so each");
                Console.WriteLine("     entry is estimated from n observations and it stays meaningful when n < k)");
                if (stressDays.Length < k)
                    Console.WriteLine($"    NOTE: n={stressDays.Length} < k={k}, so a full-matrix statistic on the stress\n"
                                    + "     subset alone would be rank-deficient — that is why none is quoted here.");
                Console.WriteLine();
            }
        }

        // ── Families ─────────────────────────────────────────────────────────────────────────
        var labels = SymbolCovariance.Families(full.Correlation, k, FamilyMaxDist);
        int nFam = labels.Length == 0 ? 0 : labels.Max() + 1;
        Console.WriteLine($"── Co-movement families (average linkage, merge while avg corr > {1 - FamilyMaxDist:F2}) ──");
        for (int f = 0; f < nFam; f++)
        {
            var mem = Enumerable.Range(0, k).Where(i => labels[i] == f).Select(i => rm.Symbols[i]).ToArray();
            Console.WriteLine($"  family {f} (n={mem.Length,3}): {string.Join(" ", mem.Take(18))}{(mem.Length > 18 ? " …" : "")}");
        }
        Console.WriteLine($"\n  {nFam} families across {k} symbols.");
        Console.WriteLine("  (Config.OosCoins is already hand-grouped by sector in its own comments — L1s, DeFi,");
        Console.WriteLine("   infra/oracle. Compare those groupings against these: they have never been checked.)\n");

        Console.WriteLine("  Covariance describes CO-MOVEMENT, not direction — these families say who moves together,");
        Console.WriteLine("  never which way. Nothing here can replace the regime classifier; it belongs at the");
        Console.WriteLine("  portfolio layer, where Config.MaxDirectionalConcurrent currently counts heads and treats");
        Console.WriteLine("  every same-direction position as equally correlated.");
    }

    private sealed record Block(double Lambda, double[] Correlation, double[] Eigenvalues,
                                double EffectiveBets, double MeanOffDiag);

    private static Block? Analyse(double[][] series)
    {
        int k = series.Length;
        var shrunk = CovarianceMatrix.ShrinkAuto(series);
        if (shrunk == null) return null;

        var corr = CovarianceMatrix.Correlations(shrunk.Value.Cov, k);
        if (corr == null) return null;

        var eig = CovarianceMatrix.Eigenvalues(corr, k);
        double s1 = eig.Sum(), s2 = eig.Sum(e => e * e);
        double effBets = s2 > 1e-24 ? s1 * s1 / s2 : 0.0;

        return new Block(shrunk.Value.Lambda, corr, eig, effBets, SymbolCovariance.MeanOffDiagonal(corr, k));
    }

    private static void PrintBlock(Block b, int k, int n)
    {
        Console.WriteLine($"  symbols k={k}   observations n={n}   free parameters k(k+1)/2 = {k * (k + 1) / 2:N0}");
        Console.WriteLine($"  Ledoit-Wolf shrinkage intensity λ: {b.Lambda:F3}"
                        + (b.Lambda > 0.5 ? "   (heavy — the sample matrix is poorly conditioned)" : ""));
        Console.WriteLine($"  Mean off-diagonal correlation:     {b.MeanOffDiag:F3}");
        Console.WriteLine($"  Effective bets (participation):    {b.EffectiveBets:F1} of {k}");
    }

    // Mean off-diagonal Pearson correlation using only the given day indices.
    private static double MeanCorrOn(double[][] series, int k, int[] days)
    {
        var mean = new double[k];
        var sd   = new double[k];
        for (int a = 0; a < k; a++)
        {
            double m = 0;
            foreach (int d in days) m += series[a][d];
            m /= days.Length;
            double v = 0;
            foreach (int d in days) { double t = series[a][d] - m; v += t * t; }
            mean[a] = m;
            sd[a]   = Math.Sqrt(v / Math.Max(1, days.Length - 1));
        }

        double sum = 0; int cnt = 0;
        for (int a = 0; a < k; a++)
            for (int b = a + 1; b < k; b++)
            {
                if (sd[a] < 1e-12 || sd[b] < 1e-12) continue;
                double c = 0;
                foreach (int d in days) c += (series[a][d] - mean[a]) * (series[b][d] - mean[b]);
                c /= (days.Length - 1) * sd[a] * sd[b];
                sum += Math.Clamp(c, -1.0, 1.0);
                cnt++;
            }
        return cnt > 0 ? sum / cnt : 0.0;
    }
}
