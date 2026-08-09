using System.Text.Json;

namespace TradingGA;

// ── Shared application of the trained DynamicGuard to a portfolio trade list ──────────────
//
// WHY THIS EXISTS
// The guard is trained (`dynamicguardtrain`), saved to genotypes/dynamic_guard_genotype.json,
// printed in papertrade and compared in `fulltest` — but until now it was applied NOWHERE in
// `combinedbacktest` / `oosbacktest`, the two commands CLAUDE.md tells you to run after any
// simulator change. `grep -rn DynamicGuardSession commands/` returned hits only in FullTest.cs
// and PapertradeCommands.cs. So the headline backtest numbers were unguarded while the guard
// was reported as part of the system.
//
// The application logic previously lived as a local lambda `ToSimGuarded` inside FullTest.cs
// (~:690). It is hoisted here verbatim so all three commands share ONE definition of "what the
// guard does to a trade list". FullTest.cs still carries its own copy (that file is owned by
// another change in flight); it should be pointed at GuardedPortfolio.Apply and the local
// helper deleted — see the handoff note at the bottom of this file.
//
// TWO SEPARATE MECHANISMS, BOTH PART OF "THE GUARD"
//   1. Trade-list level (`Apply`):   ATR entry gate (drops trades outright) + confidence
//                                    scaling by the 4H stress multiplier.
//   2. Simulator level (`PortfolioGuardConfig`): portfolio-DD long-entry gate, confidence-
//                                    scaled loss cap and profit-protection sizing, all passed
//                                    into Simulator.SimulatePortfolioExposureCapped.
// Applying only (1) understates the guard; applying only (2) misses the ATR gate entirely.
// `Compare` always applies both, or neither.
//
// REPORTING RULE — NEVER SILENTLY REPLACE THE HEADLINE
// The guard has never run in these commands, so switching the headline number to the guarded
// one would be indistinguishable from a performance regression in the diff. `Compare` prints
// both columns and the delta, and leaves the caller's existing headline untouched.
public static class GuardedPortfolio
{
    // The trade shape every backtest command already has on hand before it builds its
    // exposure-sim input: entry time, realised return %, sizing confidence, hold duration,
    // strategy label. Strategy must survive into the simulator — the portfolio-DD gate and
    // the loss cap key off it.
    public readonly record struct Trade(
        DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy);

    // The guard's PORTFOLIO-level knobs, already in the units
    // Simulator.SimulatePortfolioExposureCapped expects (DdLongEntryGatePct is a FRACTION).
    public readonly record struct PortfolioGuardConfig(
        double DdLongEntryGatePct,
        double ConfLossCapMin,
        double ConfLossCapMax,
        double ProfitProtectThreshold,
        double ProfitProtectDrawback,
        double ProfitProtectFactor)
    {
        // Every field at its no-op value. Feeding these to the 5-tuple overload of
        // SimulatePortfolioExposureCapped makes it bit-for-bit equal to the 4-tuple overload:
        // `currentDd > 1.0` is never true, `confLossCapMin < 1.0` is false, and
        // `profitProtectThreshold < 1.0` is false, so no branch fires. That equality is what
        // lets the "unguarded" column below be compared against the caller's own headline
        // number without re-deriving it.
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

    // Loads genotypes/dynamic_guard_genotype.json and builds the 4H stress session from BTC h1.
    // Returns null (with a printed reason) when either half is unavailable — a missing guard
    // must degrade to "no guarded column", never to a silently half-applied guard.
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

    // Hoisted from FullTest.cs's local `ToSimGuarded`. Order matters and is load-bearing:
    // the ATR entry gate REMOVES trades first (a blocked entry never happens, so it must not
    // contribute a zero-size position to the exposure ledger), then the surviving trades are
    // scaled by the 4H stress multiplier. Strategy is preserved so the simulator-level gates
    // can still see it.
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

    // Runs the four simulations (unguarded/guarded × 5%-cap/Kelly) over the SAME trade list and
    // the SAME 5-tuple simulator overload, so the only difference between the columns is the
    // guard itself — not a change of code path.
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

    // Prints both columns and the delta. Never mutates or replaces the caller's headline.
    // `ctx == null` prints the reason the guarded column is absent instead of printing nothing,
    // so "no guarded numbers" is always visible rather than inferred from silence.
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

    // ── HANDOFF ──────────────────────────────────────────────────────────────────────────
    // FullTest.cs still defines its own `ToSimGuarded` lambda (~:690) with identical semantics.
    // It was not edited here because that file is outside this change's ownership. The follow-up
    // is mechanical: replace the lambda body with GuardedPortfolio.Apply(...) over the same
    // trades, and replace its six hand-threaded ddGate/capMin/capMax/pp* locals with
    // PortfolioGuardConfig.From(dgGeno). No number should move.
}
