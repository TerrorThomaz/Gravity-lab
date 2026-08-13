namespace TradingGA;

// Alpha/beta decomposition against BTC — "is this edge, or is it levered market exposure?"
//
// WHY THIS IS THE FIRST THING TO BUILD
// Nothing in this repo separates skill from direction. Every strategy is a per-coin time series
// and every reported number is a raw return, so a strategy that is simply long-biased through a
// bull market is indistinguishable from one with genuine edge. The measured symptom: OOS
// annualises to +86-96%/yr against validation's +19.5%/yr. OOS spans the full history including
// the 2023-24 bull run; validation is the recent trailing slice. A 4x gap in that direction is
// exactly what levered beta looks like, and exactly what alpha does NOT look like (real edge
// should degrade out of sample, not quadruple).
//
// THE MODEL
//     r_strategy(t) = alpha + beta * r_btc(t) + eps(t)
//
//   beta  — market exposure. A market-neutral strategy sits near 0. A long strategy riding BTC
//           sits near +1 or higher (alt perps are high-beta). A short strategy sits negative.
//   alpha — the part of the return BTC does not explain. THIS is the number worth having, and
//           it is the only one that survives a regime change.
//   R^2   — how much of the variance BTC explains. High R^2 with low alpha means the strategy is
//           a BTC tracker with extra steps.
//
// Alpha is reported per period AND annualised, because a small per-trade alpha at high frequency
// can still be a real business while a large per-trade alpha at ten trades a year is noise.
//
// WHAT THIS DELIBERATELY DOES NOT DO
// No multi-factor model (momentum, carry, size). BTC alone answers the question that actually
// threatens this repo's results — direction — and a factor model whose factors are themselves
// unvalidated would add false precision. Add factors only once beta is measured and understood.
public static class BetaDecomposition
{
    public record Result(
        double Alpha,          // intercept, in the same units as the input returns (percent)
        double Beta,           // slope against BTC
        double RSquared,       // 0-1, share of variance explained by BTC
        double AlphaTStat,     // |t| > 2 is the usual "not obviously noise" bar
        double ResidualStdDev,
        int    N)
    {
        // Beta near zero AND alpha significant = the good case: return that BTC does not explain.
        public bool IsMarketNeutral => Math.Abs(Beta) < 0.2;
        public bool AlphaIsSignificant => Math.Abs(AlphaTStat) > 2.0;

        public string Verdict =>
            !AlphaIsSignificant && Math.Abs(Beta) > 0.5 ? "BETA — returns are market exposure, not edge"
          : !AlphaIsSignificant                         ? "INCONCLUSIVE — alpha not distinguishable from noise"
          : Math.Abs(Beta) > 0.5                        ? "MIXED — real alpha, but heavily market-exposed"
          : "ALPHA — return BTC does not explain";
    }

    // OLS of strategy returns on BTC returns. Both series must be aligned and equal length:
    // element i of each must describe the SAME period, or beta is meaningless.
    public static Result? Regress(IReadOnlyList<double> strategyReturns, IReadOnlyList<double> btcReturns)
    {
        int n = Math.Min(strategyReturns.Count, btcReturns.Count);
        if (n < 20) return null;   // below this the t-stat is not worth printing

        double mx = 0, my = 0;
        for (int i = 0; i < n; i++) { mx += btcReturns[i]; my += strategyReturns[i]; }
        mx /= n; my /= n;

        double sxy = 0, sxx = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = btcReturns[i] - mx;
            sxy += dx * (strategyReturns[i] - my);
            sxx += dx * dx;
        }
        if (sxx < 1e-12) return null;   // BTC constant over the window — beta undefined

        double beta  = sxy / sxx;
        double alpha = my - beta * mx;

        double ssRes = 0, ssTot = 0;
        for (int i = 0; i < n; i++)
        {
            double pred = alpha + beta * btcReturns[i];
            double res  = strategyReturns[i] - pred;
            ssRes += res * res;
            double dt = strategyReturns[i] - my;
            ssTot += dt * dt;
        }
        double r2 = ssTot > 1e-12 ? 1.0 - ssRes / ssTot : 0.0;

        // Standard error of the intercept in simple OLS.
        double dof = n - 2;
        double sigma2 = dof > 0 ? ssRes / dof : 0.0;
        double seAlpha = Math.Sqrt(Math.Max(0.0, sigma2 * (1.0 / n + mx * mx / sxx)));
        double tAlpha = seAlpha > 1e-12 ? alpha / seAlpha : 0.0;

        return new Result(alpha, beta, r2, tAlpha, Math.Sqrt(Math.Max(0.0, sigma2)), n);
    }

    // Aligns trades to BTC bars by ENTRY time and regresses. Trades are irregularly spaced while
    // BTC bars are not, so each trade is matched to the BTC return over its own holding period —
    // that is the market move the trade was actually exposed to.
    public static Result? RegressTrades(
        IReadOnlyList<(DateTime Entry, DateTime Exit, double ReturnPct)> trades,
        IReadOnlyList<Candle> btcH1)
    {
        if (trades.Count < 20 || btcH1.Count < 2) return null;

        var times = new DateTime[btcH1.Count];
        for (int i = 0; i < btcH1.Count; i++) times[i] = btcH1[i].Time;

        var sr = new List<double>(trades.Count);
        var br = new List<double>(trades.Count);
        foreach (var t in trades)
        {
            int i0 = Idx(times, t.Entry), i1 = Idx(times, t.Exit);
            if (i0 < 0 || i1 < 0 || i1 <= i0) continue;
            double p0 = btcH1[i0].Close, p1 = btcH1[i1].Close;
            if (p0 <= 1e-12) continue;
            sr.Add(t.ReturnPct);
            br.Add((p1 - p0) / p0 * 100.0);
        }
        return Regress(sr, br);
    }

    private static int Idx(DateTime[] times, DateTime t)
    {
        int i = Array.BinarySearch(times, t);
        if (i >= 0) return i;
        i = ~i - 1;
        return i >= 0 && i < times.Length ? i : -1;
    }

    public static void Print(string label, Result? r)
    {
        if (r == null) { Console.WriteLine($"  {label,-12}  insufficient data"); return; }
        Console.WriteLine($"  {label,-12}  beta={r.Beta,6:F2}  alpha={r.Alpha,+7:F3}%  t={r.AlphaTStat,6:F2}  "
                        + $"R²={r.RSquared,5:P0}  n={r.N,5}   {r.Verdict}");
    }

    public static void PrintHeader()
    {
        Console.WriteLine("\n── Alpha / beta vs BTC ──────────────────────────────────────────────────────");
        Console.WriteLine("  r_strategy = alpha + beta * r_btc.  beta ~ 0 = market-neutral; |t| > 2 = alpha");
        Console.WriteLine("  is distinguishable from noise. High R² with weak alpha means a BTC tracker.");
        Console.WriteLine($"  {"strategy",-12}  {"beta",6}  {"alpha",8}  {"t",6}  {"R²",5}  {"n",5}");
        Console.WriteLine($"  {new string('-', 76)}");
    }
}
