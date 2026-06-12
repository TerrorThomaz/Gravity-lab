using System.Collections.Concurrent;

namespace TradingGA;

// Red-Queen cooperative coevolution:
//
//   Each round, three populations evolve IN PARALLEL against the previous round's peers:
//     • DipLong   ← gate = router[t-1] × guard[t-1]
//     • SwingLong ← gate = router[t-1] × guard[t-1]
//     • Guard     ← trade lists from DipLong[t-1] + SwingLong[t-1]
//
//   Then (sequentially):
//     • Router  ← fresh trade lists from DipLong[t] + SwingLong[t]
//     • Guard trades rebuilt for next round from Router[t]
//
// Because each component evolves against the OTHER's last-known state, both sides
// must keep adapting — strategies defend exits against a guard that tightens on their
// weak spots, and the guard calibrates on strategies that keep finding new exits.
//
// Round 1 is an unbiased warm-start (no gating, seeds from JSON files).
// Rounds 2–N use full gate pressure with short bursts (StratGens each).
// Console output from the three parallel tasks is suppressed; round summaries are printed.
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

    private const int RedQueenRounds = 8;
    private const int StratGens      = 10;   // gens per strategy per round (short burst)
    private const int RouterGens     = 20;
    private const int GuardGens      = 15;
    private const int GuardPopSize   = 30;

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

        // Pre-build initial guard trade lists from seeds so round 1 guard phase is not empty
        var (guardVal, guardOos) = data.BtcH1.Length >= 50
            ? BuildGuardTrades(fsSeed, dlBest, slBest, data.GridGeno, data, routerElite)
            : (new List<(DateTime, double, double, TimeSpan, bool)>(),
               new List<(DateTime, double, double, TimeSpan, bool)>());

        for (int round = 0; round < RedQueenRounds; round++)
        {
            bool useGating = round > 0;

            Console.WriteLine($"\n{'═',80}");
            Console.WriteLine($"  Red Queen Round {round + 1}/{RedQueenRounds}  (stratGens={StratGens}  routerGens={RouterGens}  guardGens={GuardGens})");
            Console.WriteLine($"{'═',80}");
            Console.WriteLine($"  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");

            // ── Build gate from PREVIOUS round's router + guard (red-queen: compete vs last state) ──
            RegimeRouterSession? session = useGating
                ? new RegimeRouterSession(data.BtcSeries, data.EthSeries, routerElite)
                : null;
            DynamicGuardSession? guardSession = (useGating && guardBest != null && data.BtcH1.Length > 0)
                ? new DynamicGuardSession(data.BtcH1, guardBest)
                : null;
            Func<DateTime, double>? bullGate = BuildBullGate(session, guardSession);

            Console.WriteLine(useGating
                ? "  Gate: DipLong/SwingLong = bull·conf × guard[t-1]  ║  Guard ← strats[t-1]  [PARALLEL]"
                : "  Gate: none (warm-start)  ║  Guard ← seeds  [PARALLEL]");

            // Capture loop variables for closures
            var dlCapture    = dlBest;
            var slCapture    = slBest;
            var guardValCap  = guardVal;
            var guardOosCap  = guardOos;
            var guardCapture = guardBest;

            // ── Parallel: DipLong || SwingLong || Guard ───────────────────────────
            var dlTask = Task.Run(() =>
                new DipLongGA(
                    populationSize:    60,
                    generations:       StratGens,
                    eliteCount:        10,
                    migrationInterval: 5,
                    verbose:           false,
                    tradeGate:         bullGate)
                .Run(data.DlCoins, dlCapture));

            var slTask = Task.Run(() =>
                new SwingLongGA(
                    populationSize:    60,
                    generations:       StratGens,
                    eliteCount:        10,
                    migrationInterval: 5,
                    verbose:           false,
                    tradeGate:         bullGate)
                .Run(data.SlCoins, slCapture));

            var guardTask = Task.Run(() =>
            {
                if (data.BtcH1.Length < 50 || (guardValCap.Count < 20 && guardOosCap.Count < 20))
                    return guardCapture;
                return (DynamicGuardGenotype?)new DynamicGuardGA(GuardPopSize, GuardGens)
                    .Run(data.BtcH1, guardValCap, guardOosCap);
            });

            Task.WaitAll(dlTask, slTask, guardTask);
            dlBest    = dlTask.Result;
            slBest    = slTask.Result;
            guardBest = guardTask.Result;

            Console.WriteLine($"\n  DipLong:   {dlBest}");
            Console.WriteLine($"  SwingLong: {slBest}");
            if (guardBest != null) Console.WriteLine($"  Guard:     {guardBest}");

            // ── Router phase (sequential; uses updated DipLong + SwingLong bests) ──
            Console.WriteLine("\n─── Router (fresh trade lists from updated strategies) ───");
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

            // ── Rebuild guard trade lists for the next round (uses updated Router + strategies) ──
            if (data.BtcH1.Length >= 50)
            {
                (guardVal, guardOos) = BuildGuardTrades(
                    fsSeed, dlBest, slBest, data.GridGeno, data, routerElite);
                Console.WriteLine($"  Guard trades rebuilt: val={guardVal.Count}  pseudo-oos={guardOos.Count}");
            }
        }

        return new CoevolveResult(flSeed!, dlBest!, slBest!, routerElite, guardBest!);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static Func<DateTime, double>? BuildBullGate(
        RegimeRouterSession?  session,
        DynamicGuardSession?  guardSession) =>
        session != null
            ? t => session.GetWeight(RegimeRouterGA.StrategyKind.DipLong, t)
                  * (guardSession?.GetMult(t) ?? 1.0)
            : guardSession != null
                ? t => guardSession.GetMult(t)
                : (Func<DateTime, double>?)null;

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
