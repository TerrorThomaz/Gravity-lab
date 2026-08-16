using System.Text.Json;

namespace TradingGA;

// Applies the trained DynamicGuard to a trade list. Two mechanisms: trade-list Apply (ATR gate +
// conf scaling) + simulator-level PortfolioGuardConfig. Prints side-by-side, never replaces headline.
// Handoff: FullTest.cs still has its own ToSimGuarded lambda — should point at GuardedPortfolio.Apply.
public static class GuardedPortfolio
{
    // Trade shape: strategy must survive into the simulator (DD gate and loss cap key off it).
    public readonly record struct Trade(
        DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy);

    // Portfolio-level knobs in the units Simulator.SimulatePortfolioExposureCapped expects.
    public readonly record struct PortfolioGuardConfig(
        double DdLongEntryGatePct,
        double ConfLossCapMin,
        double ConfLossCapMax,
        double ProfitProtectThreshold,
        double ProfitProtectDrawback,
        double ProfitProtectFactor)
    {
        // All fields at no-op values — makes the 5-tuple overload bit-equal to the 4-tuple.
        public static PortfolioGuardConfig Off =>
            new(DynamicGuardGenotype.DdGateDisabled, 1.0, 1.0, 1.0, 0.10, 1.0);

        public static PortfolioGuardConfig From(DynamicGuardGenotype? g) =>
            g == null
                ? Off
                : new(g.DdEntryGatePct, g.ConfLossCapMin, g.ConfLossCapMax,
                      g.ProfitProtectThreshold, g.ProfitProtectDrawback, g.ProfitProtectFactor);
    }

    public sealed record Context(
        DynamicGuardSession Session,
        DynamicGuardGenotype Genotype,
        PortfolioGuardConfig Portfolio);

    // Loads guard genotype + builds 4H session. Returns null (with reason) if either is unavailable.
    public static Context? TryLoad(Candle[]? btcH1, string? genoPath = null)
    {
        genoPath ??= Config.DynamicGuardGenoFile;
        if (!File.Exists(genoPath))
        {
            Console.WriteLine($"  Guard: {genoPath} not found — guarded comparison skipped (run 'dynamicguardtrain').");
            return null;
        }
        if (btcH1 is not { Length: >= 50 })
        {
            Console.WriteLine("  Guard: BTC h1 series unavailable (<50 bars) — guarded comparison skipped.");
            return null;
        }
        DynamicGuardGenotype geno;
        try
        {
            geno = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Guard: failed to read {genoPath} ({ex.GetType().Name}) — guarded comparison skipped.");
            return null;
        }
        return new Context(new DynamicGuardSession(btcH1, geno), geno, PortfolioGuardConfig.From(geno));
    }

    // ATR gate removes trades first, then surviving trades scaled by stress multiplier. Order matters.
    public static List<(DateTime, double, double, TimeSpan, string)> Apply(
        IEnumerable<Trade> trades, DynamicGuardSession gs) =>
        trades
            .Where(x => !gs.IsEntryBlocked(x.Time, x.Strategy))
            .Select(x => (x.Time, x.Return,
                DynamicGuardSession.IsGuarded(x.Strategy) ? x.Conf * gs.GetMult(x.Time, x.Strategy) : x.Conf,
                x.Hold, x.Strategy))
            .ToList();

    public static List<(DateTime, double, double, TimeSpan, string)> Passthrough(IEnumerable<Trade> trades) =>
        trades.Select(x => (x.Time, x.Return, x.Conf, x.Hold, x.Strategy)).ToList();

    public readonly record struct SideBySide(
        int UnguardedTrades,
        int GuardedTrades,
        Simulator.PortfolioResult UnguardedFivePct,
        Simulator.PortfolioResult GuardedFivePct,
        Simulator.PortfolioResult UnguardedKelly,
        Simulator.PortfolioResult GuardedKelly)
    {
        public int TradesRemoved => UnguardedTrades - GuardedTrades;
    }

    // Four simulations (unguarded/guarded × 5%-cap/Kelly). Same code path — only the guard differs.
    public static SideBySide Run(
        IReadOnlyList<Trade> trades,
        Context ctx,
        double maxTotalExposurePct)
    {
        var unguarded = Passthrough(trades);
        var guarded   = Apply(trades, ctx.Session);
        var off       = PortfolioGuardConfig.Off;
        var on        = ctx.Portfolio;

        return new SideBySide(
            unguarded.Count, guarded.Count,
            Sim(unguarded, off, maxTotalExposurePct, maxPositionFrac: 0.05),
            Sim(guarded,   on,  maxTotalExposurePct, maxPositionFrac: 0.05),
            Sim(unguarded, off, maxTotalExposurePct, maxPositionFrac: 0.15),
            Sim(guarded,   on,  maxTotalExposurePct, maxPositionFrac: 0.15));
    }

    static Simulator.PortfolioResult Sim(
        List<(DateTime, double, double, TimeSpan, string)> trades,
        PortfolioGuardConfig cfg,
        double maxTotalExposurePct,
        double maxPositionFrac) =>
        Simulator.SimulatePortfolioExposureCapped(
            trades,
            maxTotalExposurePct,
            maxPositionFrac:              maxPositionFrac,
            ddLongEntryGatePct:           cfg.DdLongEntryGatePct,
            confLossCapMin:               cfg.ConfLossCapMin,
            confLossCapMax:               cfg.ConfLossCapMax,
            profitProtectThreshold:       cfg.ProfitProtectThreshold,
            profitProtectDrawback:        cfg.ProfitProtectDrawback,
            profitProtectFactor:          cfg.ProfitProtectFactor);

    public static double ReturnPct(Simulator.PortfolioResult p) =>
        p.StartBalance > 0 ? (p.EndBalance - p.StartBalance) / p.StartBalance * 100.0 : 0.0;

    // Prints both columns + delta. Never replaces headline. ctx==null prints reason, not silence.
    public static void PrintComparison(
        string title,
        IReadOnlyList<Trade> trades,
        Context? ctx,
        double maxTotalExposurePct)
    {
        Console.WriteLine($"\n── {title}: DynamicGuard applied vs not ──────────────────────────────");

        if (ctx == null)
        {
            Console.WriteLine("  No guard available — the numbers above are UNGUARDED.");
            return;
        }
        if (trades.Count == 0)
        {
            Console.WriteLine("  No trades to compare.");
            return;
        }

        var r = Run(trades, ctx, maxTotalExposurePct);

        Console.WriteLine($"  Guard genotype: {ctx.Genotype}");
        Console.WriteLine($"  Guarded set:    {r.GuardedTrades} trades ({r.TradesRemoved} removed by the ATR entry gate; " +
                          $"sizes scaled for {{grid, gridshort, diplong, swing_long}})");
        Console.WriteLine();
        Console.WriteLine($"  {"Sizing",-18}  {"Unguarded",12}  {"Guarded",12}  {"Δ return",10}  {"Unguard DD",10}  {"Guard DD",9}");
        Console.WriteLine($"  {new string('-', 78)}");
        Row("5% per position", r.UnguardedFivePct, r.GuardedFivePct);
        Row("Kelly(15%)",      r.UnguardedKelly,   r.GuardedKelly);

        Console.WriteLine();
        Console.WriteLine("  The UNGUARDED column reproduces the headline portfolio numbers printed above;");
        Console.WriteLine("  the guard has never been applied in this command before, so the guarded column");
        Console.WriteLine("  is NEW information, not a regression.");
        Console.WriteLine($"  ⚠ Retrain the guard before trusting the guarded column: {Config.DynamicGuardGenoFile} was");
        Console.WriteLine("    selected while DdEntryGatePct was on a percent scale the simulator read as a");
        Console.WriteLine("    fraction, so the portfolio-DD long-entry gate was inert across its whole search");
        Console.WriteLine($"    space. It is now live and switchable (currently {(ctx.Genotype.DdGateIsLive ? $"ON at {ctx.Genotype.DdEntryGatePct:P1}" : "coerced OFF")}).");
        Console.WriteLine("    Run 'dynamicguardtrain' to get a threshold selected under the live gate.");

        void Row(string label, Simulator.PortfolioResult u, Simulator.PortfolioResult g)
        {
            double ru = ReturnPct(u), rg = ReturnPct(g);
            Console.WriteLine($"  {label,-18}  {ru,11:+0.0;-0.0}%  {rg,11:+0.0;-0.0}%  {rg - ru,9:+0.0;-0.0}pp  " +
                              $"{u.MaxDrawdownPct,9:F1}%  {g.MaxDrawdownPct,8:F1}%");
        }
    }

    // HANDOFF: FullTest.cs has its own ToSimGuarded lambda — replace with GuardedPortfolio.Apply
    // + PortfolioGuardConfig.From(dgGeno). No number should move.
}
