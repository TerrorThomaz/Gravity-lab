namespace TradingGA;

// THE evaluation. One trade list in, one verdict out.
//
// WHY THIS EXISTS
// Nine commands currently evaluate strategies — backtest, gridbacktest, combinedbacktest,
// oosbacktest, allcoinsbacktest, rankedbacktest, yearlybreakdown, fulltest, test — and they
// overlap heavily while disagreeing in detail. Each computes its own subset of statistics, in its
// own order, with its own thresholds, and prints them in its own format. Consequences observed in
// this codebase:
//
//   · combinedbacktest gated FadeShort through the router; oosbacktest did not. The two commands
//     were evaluating different strategy sets, so any val-vs-OOS gap partly measured that.
//   · fulltest excluded FadeLong behind a stale comment while combinedbacktest included it.
//   · The guard was applied in some commands and not others, with the headline row differing.
//   · A defect fixed in one command stayed live in the others (the FadeShort gate, twice).
//
// A single evaluator cannot drift from itself. Commands become thin adapters that produce a trade
// list and hand it here; what "evaluate" MEANS lives in one place.
//
// WHAT IT ADDS OVER THE EXISTING PIECES
// Risk of ruin, Value at Risk and a regime-switching bootstrap did not exist anywhere. The last is
// the important one: the repo's moving-block bootstrap preserves LOCAL autocorrelation but has no
// concept of a regime, so it cannot answer "what if the next bear cluster runs three times longer
// than any in the sample?" — and this book's returns are strongly regime-dependent (+5.80%/trade
// at max BTC stress against +2.08% in calm). Resampling that ignores clustering understates the
// tail of exactly the distribution that matters.
public static class StrategyEvaluation
{
    public readonly record struct Trade(
        DateTime Entry, DateTime Exit, double ReturnPct, string Strategy, string Symbol, MarketRegime Regime);

    public record Result(
        int    N,
        double WinRate,
        double ProfitFactor,
        double Expectancy,
        double Sharpe,
        double Sortino,
        double MaxDrawdownPct,
        double VaR95,             // 5th percentile single-trade return
        double CVaR95,            // mean of the worst 5%
        double RiskOfRuin,        // P(equity falls below RuinThreshold) across resampled paths
        double MedianPathReturn,
        double P05PathReturn,
        double P95PathReturn,
        IReadOnlyDictionary<MarketRegime, (int N, double Avg, double Pf)> ByRegime,
        Grade  Verdict);

    // A/B/C/D/F across the four axes that actually decide whether a backtest is believable. The
    // components all existed already, scattered across six reports — which is part of why it took
    // until now to notice a strategy carrying 20.7% of the book had an alpha t-stat of -0.08.
    public record Grade(char Edge, char Robustness, char Risk, char Sample, string Summary)
    {
        public char Overall
        {
            get
            {
                var g = new[] { Edge, Robustness, Risk, Sample };
                double mean = g.Average(c => c switch { 'A' => 4.0, 'B' => 3.0, 'C' => 2.0, 'D' => 1.0, _ => 0.0 });
                return mean >= 3.5 ? 'A' : mean >= 2.5 ? 'B' : mean >= 1.5 ? 'C' : mean >= 0.5 ? 'D' : 'F';
            }
        }
    }

    // Equity level below which a path counts as ruined, as a fraction of starting capital.
    public const double RuinThreshold = 0.5;
    public const int    BootstrapPaths = 2000;

    public static Result Evaluate(IReadOnlyList<Trade> trades, double posFrac = 0.05, int? seed = null)
    {
        if (trades.Count == 0)
            return new Result(0, 0, 0, 0, 0, 0, 0, 0, 0, 1.0, 0, 0, 0,
                              new Dictionary<MarketRegime, (int, double, double)>(),
                              new Grade('F', 'F', 'F', 'F', "no trades"));

        var r = trades.Select(t => t.ReturnPct).ToArray();
        int wins = r.Count(x => x > 0);
        double grossWin = r.Where(x => x > 0).Sum(), grossLoss = Math.Abs(r.Where(x => x <= 0).Sum());

        double wr  = (double)wins / r.Length;
        double pf  = grossLoss > 1e-9 ? grossWin / grossLoss : (grossWin > 0 ? 99.0 : 0.0);
        double exp = r.Average();

        var sorted = r.OrderBy(x => x).ToArray();
        double var95  = sorted[Math.Max(0, (int)(sorted.Length * 0.05))];
        int tailN     = Math.Max(1, (int)(sorted.Length * 0.05));
        double cvar95 = sorted.Take(tailN).Average();

        double sd = StdDev(r);
        double dd = DownsideDev(r);
        double sharpe  = sd > 1e-9 ? exp / sd  : 0.0;
        double sortino = dd > 1e-9 ? exp / dd  : 0.0;

        var (ruin, med, p05, p95, maxDd) = RegimeSwitchingBootstrap(trades, posFrac, seed);

        var byRegime = trades.GroupBy(t => t.Regime).ToDictionary(
            g => g.Key,
            g => (g.Count(), g.Average(t => t.ReturnPct), Pf(g.Select(t => t.ReturnPct))));

        return new Result(r.Length, wr, pf, exp, sharpe, sortino, maxDd, var95, cvar95,
                          ruin, med, p05, p95, byRegime, Score(r.Length, pf, exp, sharpe, ruin, p05, byRegime));
    }

    // ── Regime-switching bootstrap ────────────────────────────────────────────────────────
    // Resamples trades in REGIME BLOCKS rather than uniformly. Good and bad conditions cluster in
    // reality — a bear market is a run of correlated bad months, not bad days scattered through
    // good ones — and a bootstrap that breaks that clustering silently reports a thinner tail than
    // the strategy actually faces.
    //
    // The block is chosen by sampling a regime, then drawing a run of trades from that regime whose
    // length is geometrically distributed around the regime's observed mean run length. That
    // preserves BOTH the within-regime return distribution and the persistence of regimes, which a
    // fixed-length moving-block bootstrap does not.
    private static (double Ruin, double Median, double P05, double P95, double MaxDd)
        RegimeSwitchingBootstrap(IReadOnlyList<Trade> trades, double posFrac, int? seed)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random(12345);

        var byRegime = trades.GroupBy(t => t.Regime)
                             .ToDictionary(g => g.Key, g => g.Select(t => t.ReturnPct).ToArray());
        var regimes = byRegime.Keys.ToArray();
        if (regimes.Length == 0) return (1.0, 0, 0, 0, 0);

        // Observed mean run length per regime, from the actual ordering.
        var runLen = new Dictionary<MarketRegime, double>();
        foreach (var reg in regimes)
        {
            int runs = 0, cur = 0;
            foreach (var t in trades.OrderBy(t => t.Entry))
            {
                if (t.Regime == reg) cur++;
                else if (cur > 0) { runs++; cur = 0; }
            }
            if (cur > 0) runs++;
            runLen[reg] = runs > 0 ? (double)byRegime[reg].Length / runs : 1.0;
        }

        var finals = new double[BootstrapPaths];
        double worstDd = 0;
        int ruined = 0;

        for (int p = 0; p < BootstrapPaths; p++)
        {
            double bal = 1.0, peak = 1.0, maxDd = 0;
            int emitted = 0;
            bool isRuined = false;

            while (emitted < trades.Count)
            {
                var reg = regimes[rng.Next(regimes.Length)];
                var pool = byRegime[reg];
                double mean = Math.Max(1.0, runLen[reg]);
                // Geometric run length with the observed mean.
                int len = Math.Max(1, (int)Math.Ceiling(Math.Log(1 - rng.NextDouble()) / Math.Log(1 - 1.0 / mean)));

                for (int i = 0; i < len && emitted < trades.Count; i++, emitted++)
                {
                    bal += pool[rng.Next(pool.Length)] / 100.0 * posFrac * bal;
                    if (bal > peak) peak = bal;
                    double d = peak > 1e-9 ? (peak - bal) / peak : 0;
                    if (d > maxDd) maxDd = d;
                    if (bal <= RuinThreshold) { isRuined = true; break; }
                }
                if (isRuined) break;
            }

            if (isRuined) ruined++;
            finals[p] = (bal - 1.0) * 100.0;
            if (maxDd > worstDd) worstDd = maxDd;
        }

        Array.Sort(finals);
        return ((double)ruined / BootstrapPaths,
                finals[BootstrapPaths / 2],
                finals[(int)(BootstrapPaths * 0.05)],
                finals[(int)(BootstrapPaths * 0.95)],
                worstDd * 100.0);
    }

    private static Grade Score(int n, double pf, double exp, double sharpe, double ruin, double p05,
                               IReadOnlyDictionary<MarketRegime, (int N, double Avg, double Pf)> byRegime)
    {
        char edge = pf >= 2.0 && exp > 0 ? 'A' : pf >= 1.5 ? 'B' : pf >= 1.2 ? 'C' : pf >= 1.0 ? 'D' : 'F';

        // Robustness = does the edge survive in EVERY regime it trades, or is it one regime's luck?
        // FadeShort scored PF 5.28 in Bear and 0.38 in Bull; a blended number hid that for six
        // retrains.
        var meaningful = byRegime.Where(kv => kv.Value.N >= 20).ToList();
        int losing = meaningful.Count(kv => kv.Value.Pf < 1.0);
        char rob = meaningful.Count == 0 ? 'F'
                 : losing == 0 ? 'A' : losing == 1 && meaningful.Count >= 3 ? 'C' : 'D';

        char risk = ruin <= 0.001 && p05 > 0 ? 'A' : ruin <= 0.01 ? 'B' : ruin <= 0.05 ? 'C' : ruin <= 0.15 ? 'D' : 'F';
        char samp = n >= 1000 ? 'A' : n >= 300 ? 'B' : n >= 100 ? 'C' : n >= 30 ? 'D' : 'F';

        var flags = new List<string>();
        if (losing > 0) flags.Add($"{losing} regime(s) lose money");
        if (ruin > 0.01) flags.Add($"risk of ruin {ruin:P1}");
        if (p05 < 0) flags.Add("5th-percentile path is negative");
        if (n < 100) flags.Add("thin sample");
        return new Grade(edge, rob, risk, samp, flags.Count == 0 ? "no flags" : string.Join("; ", flags));
    }

    private static double Pf(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        double w = a.Where(x => x > 0).Sum(), l = Math.Abs(a.Where(x => x <= 0).Sum());
        return l > 1e-9 ? w / l : (w > 0 ? 99.0 : 0.0);
    }

    private static double StdDev(double[] x)
    {
        if (x.Length < 2) return 0;
        double m = x.Average(), s = 0;
        foreach (var v in x) { double d = v - m; s += d * d; }
        return Math.Sqrt(s / (x.Length - 1));
    }

    private static double DownsideDev(double[] x)
    {
        if (x.Length < 2) return 0;
        double s = 0;
        foreach (var v in x) { double d = Math.Min(0, v); s += d * d; }
        return Math.Sqrt(s / x.Length);
    }

    // ── The single entry point every backtest command reports through ─────────────────────
    // PortfolioReplay.Trade is the shape all three portfolio backtests already build on their way
    // into the concurrency cap, so this adapter sits where the trade list is final: routed, capped,
    // and about to be turned into equity. Reporting from anywhere earlier grades a book that is not
    // the one the sim runs.
    public static List<Trade> FromPortfolio(IReadOnlyList<PortfolioReplay.Trade> trades, RegimeBar[]? btcRegimes = null)
    {
        // Tagged at ENTRY, not exit. A trade opened in Bear and closed after the turn belongs to
        // the regime that produced the signal — tagging on exit would credit the new regime with a
        // decision made under the old one.
        var tags = btcRegimes is { Length: > 0 }
            ? RegimeBarLookup.TagRegimes(btcRegimes, trades.Select(t => t.EntryTime).ToList())
            : null;

        return trades.Select((t, i) => new Trade(
            t.EntryTime, t.EntryTime + t.HoldDuration, t.Return, t.Strategy, t.Symbol,
            tags is null ? MarketRegime.Ranging : tags[i])).ToList();
    }

    // Evaluate + Print + optional trade-log export, in one call, so a command adds statistics by
    // adding a line rather than by growing its own copy of them. Every previous report reimplemented
    // PF/WR/Sharpe locally, which is why fulltest, combinedbacktest and oosbacktest could disagree
    // about the same book and nothing flagged it.
    public static Result Report(string label, IReadOnlyList<PortfolioReplay.Trade> trades,
                                RegimeBar[]? btcRegimes = null, string? csvPath = null)
    {
        var t = FromPortfolio(trades, btcRegimes);
        var r = Evaluate(t);
        Print(label, r);
        if (csvPath != null) ExportCsv(t, csvPath);
        return r;
    }

    public static void Print(string label, Result r)
    {
        Console.WriteLine($"\n══ {label} ══════════════════════════════════════════════════");
        Console.WriteLine($"  VERDICT {r.Verdict.Overall}   edge={r.Verdict.Edge} robustness={r.Verdict.Robustness} "
                        + $"risk={r.Verdict.Risk} sample={r.Verdict.Sample}   [{r.Verdict.Summary}]");
        Console.WriteLine($"  n={r.N}  WR={r.WinRate:P0}  PF={r.ProfitFactor:F2}  exp={r.Expectancy:+0.00;-0.00}%  "
                        + $"Sharpe={r.Sharpe:F2}  Sortino={r.Sortino:F2}");
        Console.WriteLine($"  VaR95={r.VaR95:F2}%  CVaR95={r.CVaR95:F2}%  maxDD={r.MaxDrawdownPct:F1}%  "
                        + $"risk-of-ruin={r.RiskOfRuin:P2}");
        Console.WriteLine($"  regime-switching bootstrap ({BootstrapPaths} paths): "
                        + $"p05={r.P05PathReturn:+0.0;-0.0}%  median={r.MedianPathReturn:+0.0;-0.0}%  p95={r.P95PathReturn:+0.0;-0.0}%");
        if (r.ByRegime.Count > 0)
        {
            Console.WriteLine($"  {"regime",-10}{"n",7}{"avg%",9}{"PF",8}");
            foreach (var kv in r.ByRegime.OrderByDescending(k => k.Value.N))
                Console.WriteLine($"  {kv.Key,-10}{kv.Value.N,7}{kv.Value.Avg,9:F2}{kv.Value.Pf,8:F2}");
        }
    }

    // Trade-log export, in the shape third-party evaluators (QuantPad and similar) expect.
    // Deliberately raw per-trade rows rather than a summary: an external tool's value is that it
    // has no stake in the choices made here, and a summary would launder those choices into it.
    public static void ExportCsv(IReadOnlyList<Trade> trades, string path)
    {
        var lines = new List<string> { "entry_time,exit_time,symbol,strategy,regime,return_pct" };
        foreach (var t in trades.OrderBy(t => t.Entry))
            lines.Add($"{t.Entry:yyyy-MM-dd HH:mm:ss},{t.Exit:yyyy-MM-dd HH:mm:ss},{t.Symbol},"
                    + $"{t.Strategy},{t.Regime},{t.ReturnPct:F4}");
        File.WriteAllLines(path, lines);
        Console.WriteLine($"  Trade log → {path}  ({trades.Count} trades)");
    }
}
