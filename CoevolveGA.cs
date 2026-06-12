using System.Collections.Concurrent;

namespace TradingGA;

// Cooperative coevolution: DipLong · SwingLong · RegimeRouter · DynamicGuard.
//
// Each cycle:
//   1. DipLong evolves with router bull-gate × guard multiplier.
//   2. SwingLong evolves with router bull-gate × guard multiplier.
//   3. Router evolves over fresh trade lists from all strategy elites.
//   4. DynamicGuard calibrates on router-gated trade lists.
//
// With guard pressure in fitness, DipLong/SwingLong discover exits that perform
// across the full volatility range. The guard recalibrates each cycle. The router
// adjusts its mid-range thresholds to the evolved strategy+guard landscape.
//
// Cycle 1 is an unbiased warm-start (no gating, no guard). Cycles 2-N apply
// progressive pressure with halved generation budgets (already near optimum).
//
// FadeLong is excluded — it adds a ~20% time cost and is disabled in production
// (PF=0.06 OOS). FadeShort is always-on and not coevolved.
public class CoevolveGA
{
    public record AllData(
        IReadOnlyList<FadeLongGA.CoinData>         FlCoins,   // kept for compatibility; not evolved
        IReadOnlyList<DipLongGA.CoinData>          DlCoins,
        IReadOnlyList<SwingLongGA.CoinData>        SlCoins,
        IReadOnlyList<(Candle[] H1, Candle[] M15)> AllCoins,
        RegimeBar[]                                BtcSeries,
        RegimeBar[]?                               EthSeries,
        Candle[]                                   BtcH1,
        GridGenotype?                              GridGeno);

    public record CoevolveResult(
        FadeLongGenotype      FadeLong,       // passed through unchanged
        DipLongGenotype       DipLong,
        SwingLongGenotype     SwingLong,
        RegimeRouterGenotype  Router,
        DynamicGuardGenotype  DynamicGuard);

    private const int Cycles          = 4;
    private const int WarmStartGens   = 40;   // cycle 1: full exploration budget
    private const int RefinementGens  = 20;   // cycles 2-4: already seeded, just adapt to gate
    private const int RouterGens      = 40;
    private const int GuardGens       = 30;
    private const int GuardPopSize    = 40;

    public CoevolveResult Run(
        AllData               data,
        FadeShortGenotype?    fsSeed,
        FadeLongGenotype?     flSeed,         // not trained; passed through to result
        DipLongGenotype?      dlSeed,
        SwingLongGenotype?    slSeed,
        RegimeRouterGenotype? routerSeed,
        DynamicGuardGenotype? dgSeed)
    {
        var routerElite = routerSeed ?? RegimeRouterGenotype.Random(new Random(), null);
        var dlBest      = dlSeed;
        var slBest      = slSeed;
        var guardBest   = dgSeed;

        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            bool useGating = cycle > 0;
            int  stratGens = useGating ? RefinementGens : WarmStartGens;

            Console.WriteLine($"\n{'═',80}");
            Console.WriteLine($"  Coevolve Cycle {cycle + 1}/{Cycles}  (stratGens={stratGens})");
            Console.WriteLine($"{'═',80}");
            Console.WriteLine($"  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");

            RegimeRouterSession? session = useGating
                ? new RegimeRouterSession(data.BtcSeries, data.EthSeries, routerElite)
                : null;

            DynamicGuardSession? guardSession = (useGating && guardBest != null && data.BtcH1.Length > 0)
                ? new DynamicGuardSession(data.BtcH1, guardBest)
                : null;

            // Combined bull gate: router soft-gate × guard multiplier.
            // Strategies see full weight in calm bull periods, reduced in volatile.
            Func<DateTime, double>? bullGate = session != null
                ? t => session.GetWeight(RegimeRouterGA.StrategyKind.DipLong, t)
                      * (guardSession?.GetMult(t) ?? 1.0)
                : guardSession != null
                    ? t => guardSession.GetMult(t)
                    : null;

            Console.WriteLine(useGating
                ? "  Gate: DipLong/SwingLong = bull·conf × guard"
                : "  Gate: none (warm-start cycle)");

            // ── DipLong phase ─────────────────────────────────────────────────
            Console.WriteLine("\n─── DipLong (bull-gated × guard) ───");
            dlBest = new DipLongGA(
                    populationSize:    60,
                    generations:       stratGens,
                    eliteCount:        10,
                    migrationInterval: 10,
                    verbose:           true,
                    tradeGate:         bullGate)
                .Run(data.DlCoins, dlBest);

            // ── SwingLong phase ───────────────────────────────────────────────
            Console.WriteLine("\n─── SwingLong (bull-gated × guard) ───");
            slBest = new SwingLongGA(
                    populationSize:    60,
                    generations:       stratGens,
                    eliteCount:        10,
                    migrationInterval: 10,
                    verbose:           true,
                    tradeGate:         bullGate)
                .Run(data.SlCoins, slBest);

            // ── Router phase ──────────────────────────────────────────────────
            Console.WriteLine("\n─── Router (fresh trade lists) ───");
            var routerTrades = BuildTradeLists(fsSeed, dlBest, slBest, data.GridGeno, data);
            Console.WriteLine(
                $"  Trade records: {routerTrades.Count} total — " +
                string.Join("  ",
                    Enum.GetValues<RegimeRouterGA.StrategyKind>()
                        .Select(k => $"{k}: {routerTrades.Count(t => t.Kind == k)}")));

            if (routerTrades.Count >= 50)
            {
                routerElite = new RegimeRouterGA(
                        populationSize:    50,
                        generations:       RouterGens,
                        eliteCount:        8,
                        migrationInterval: 10,
                        verbose:           true)
                    .Run(data.BtcSeries, data.EthSeries, routerTrades, routerElite);
            }
            else
            {
                Console.WriteLine("  !! Too few trades — skipping router phase.");
            }

            // ── DynamicGuard phase ────────────────────────────────────────────
            Console.WriteLine("\n─── DynamicGuard (calibrate on router-gated trades) ───");
            if (data.BtcH1.Length >= 50)
            {
                var (guardVal, guardOos) = BuildGuardTrades(
                    fsSeed, dlBest, slBest, data.GridGeno, data, routerElite);

                if (guardVal.Count >= 20 || guardOos.Count >= 20)
                {
                    guardBest = new DynamicGuardGA(GuardPopSize, GuardGens)
                        .Run(data.BtcH1, guardVal, guardOos);
                }
                else
                {
                    Console.WriteLine("  !! Too few trades — skipping guard phase.");
                }
            }
        }

        return new CoevolveResult(flSeed!, dlBest!, slBest!, routerElite, guardBest!);
    }

    // ── Trade list builders (parallelised over coins) ─────────────────────────

    private static List<RegimeRouterGA.TradeRecord> BuildTradeLists(
        FadeShortGenotype?  fs,
        DipLongGenotype?    dl,
        SwingLongGenotype?  sl,
        GridGenotype?       grid,
        AllData             data)
    {
        var bag = new ConcurrentBag<RegimeRouterGA.TradeRecord>();

        Parallel.ForEach(data.AllCoins, (coin) =>
        {
            var (h1, m15) = coin;
            if (h1.Length < 200) return;

            if (fs != null)
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fs, h1, m15))
                    bag.Add(new(RegimeRouterGA.StrategyKind.FadeShort, t.Time, t.Return, fs.PositionSizePct));

            if (grid != null)
                foreach (var t in GridSimulator.GetGridReturns(grid, h1))
                    bag.Add(new(RegimeRouterGA.StrategyKind.Grid, t.Time, t.Return, 0.05));
        });

        if (dl is { Fitness: > 0 })
        {
            Parallel.ForEach(data.DlCoins, (cd) =>
            {
                Candle[] h1  = MemConcat(cd.TrainH1, cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) return;
                foreach (var t in DipLongSimulator.GetDipLongReturns(dl, h1, m15)
                             .Where(t => t.RegimeBarsActive >= dl.RegimeSustainedBars))
                    bag.Add(new(RegimeRouterGA.StrategyKind.DipLong, t.Time, t.Return, dl.PositionSizePct));
            });
        }

        if (sl != null)
        {
            Parallel.ForEach(data.SlCoins, (cd) =>
            {
                Candle[] h1  = MemConcat(cd.TrainH1, cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) return;
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(sl, h1, m15))
                    bag.Add(new(RegimeRouterGA.StrategyKind.DipLong, t.Time, t.Return, sl.PositionSizePct));
            });
        }

        return [.. bag.OrderBy(t => t.Time)];
    }

    private static (
        List<(DateTime, double, double, TimeSpan, bool)> val,
        List<(DateTime, double, double, TimeSpan, bool)> oos)
    BuildGuardTrades(
        FadeShortGenotype?   fs,
        DipLongGenotype?     dl,
        SwingLongGenotype?   sl,
        GridGenotype?        grid,
        AllData              data,
        RegimeRouterGenotype router)
    {
        var session = new RegimeRouterSession(data.BtcSeries, data.EthSeries, router);
        var bag     = new ConcurrentBag<(DateTime, double, double, TimeSpan, bool)>();

        Parallel.ForEach(data.AllCoins, (coin) =>
        {
            var (h1, m15) = coin;
            if (h1.Length < 200) return;

            if (fs != null)
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fs, h1, m15))
                    bag.Add((t.Time, t.Return, fs.PositionSizePct,
                             TimeSpan.FromHours(fs.MaxHoldCandles), false));

            if (grid != null)
                foreach (var t in GridSimulator.GetGridReturns(grid, h1)
                             .Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)))
                    bag.Add((t.Time, t.Return, 0.05,
                             TimeSpan.FromHours(grid.MaxHoldCandles), true));
        });

        if (dl is { Fitness: > 0 })
        {
            Parallel.ForEach(data.DlCoins, (cd) =>
            {
                Candle[] h1  = MemConcat(cd.TrainH1, cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) return;
                foreach (var t in DipLongSimulator.GetDipLongReturns(dl, h1, m15)
                             .Where(t => t.RegimeBarsActive >= dl.RegimeSustainedBars
                                      && session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)))
                    bag.Add((t.Time, t.Return, dl.PositionSizePct,
                             TimeSpan.FromHours(dl.MaxHoldCandles), true));
            });
        }

        if (sl != null)
        {
            Parallel.ForEach(data.SlCoins, (cd) =>
            {
                Candle[] h1  = MemConcat(cd.TrainH1, cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) return;
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(sl, h1, m15)
                             .Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)))
                    bag.Add((t.Time, t.Return, sl.PositionSizePct,
                             TimeSpan.FromHours(sl.MaxHoldCandles), true));
            });
        }

        var all   = bag.OrderBy(t => t.Item1).ToList();
        int split = (int)(all.Count * 0.75);
        return (all[..split], all[split..]);
    }

    private static Candle[] MemConcat(ReadOnlyMemory<Candle> a, ReadOnlyMemory<Candle> b)
    {
        if (a.IsEmpty) return b.ToArray();
        var r = new Candle[a.Length + b.Length];
        a.Span.CopyTo(r);
        b.Span.CopyTo(r.AsSpan(a.Length));
        return r;
    }
}
