using System.Collections.Concurrent;

namespace TradingGA;

// Red-Queen coevolution between Router and Guard only.
// Strategies (DipLong, SwingLong, FadeLong) are FIXED at their seed values.
//
//   Each round, in parallel:
//     Router[t] ← raw strategy trade lists (finds profitable bull windows)
//     Guard[t]  ← Router[t-1]-gated trade lists (manages risk in those windows)
//
//   Router maximises profit by routing trades through the best bull/bear windows.
//   Guard manages risk by reducing sizing when BTC volatility signals danger.
//   They co-adapt: guard pressure changes what "profitable" routing looks like;
//   router pressure changes which trades the guard must protect against.
//
//   Strategies never change — they provide the fixed signal pool that Router and Guard
//   compete to exploit safely.
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
        DipLongGenotype       DipLong,        // passed through unchanged
        SwingLongGenotype     SwingLong,      // passed through unchanged
        RegimeRouterGenotype  Router,
        DynamicGuardGenotype  DynamicGuard);

    private const int RedQueenRounds = 8;
    private const int RouterGens     = 40;
    private const int GuardGens      = 30;
    private const int GuardPopSize   = 40;

    public CoevolveResult Run(
        AllData               data,
        FadeShortGenotype?    fsSeed,
        FadeLongGenotype?     flSeed,
        DipLongGenotype?      dlSeed,
        SwingLongGenotype?    slSeed,
        RegimeRouterGenotype? routerSeed,
        DynamicGuardGenotype? dgSeed)
    {
        var routerElite = routerSeed ?? RegimeRouterGenotype.Random(new Random(), null);
        var guardBest   = dgSeed;

        // Strategies are frozen — build their trade lists once and reuse every round.
        Console.WriteLine("  Building strategy trade lists (fixed)...");
        var rawTrades = BuildTradeLists(fsSeed, dlSeed, slSeed, data.GridGeno, data);
        Console.WriteLine(
            $"  {rawTrades.Count} total — " +
            string.Join("  ",
                Enum.GetValues<RegimeRouterGA.StrategyKind>()
                    .Select(k => $"{k}: {rawTrades.Count(t => t.Kind == k)}")));

        // Initial guard trade lists (router-gated)
        var (guardVal, guardOos) = data.BtcH1.Length >= 50
            ? BuildGuardTrades(fsSeed, dlSeed, slSeed, data.GridGeno, data, routerElite)
            : (new List<(DateTime, double, double, TimeSpan, bool)>(),
               new List<(DateTime, double, double, TimeSpan, bool)>());

        for (int round = 0; round < RedQueenRounds; round++)
        {
            Console.WriteLine($"\n{'═',80}");
            Console.WriteLine($"  Red Queen Round {round + 1}/{RedQueenRounds}  (routerGens={RouterGens}  guardGens={GuardGens})");
            Console.WriteLine($"{'═',80}");
            Console.WriteLine($"  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");
            Console.WriteLine("  [PARALLEL] Router ← all trades  ║  Guard ← Router[t-1]-gated trades");

            // Capture for closures
            var routerCap   = routerElite;
            var guardCap    = guardBest;
            var guardValCap = guardVal;
            var guardOosCap = guardOos;

            // ── Parallel: Router maximises profit || Guard minimises risk ──────────
            var routerTask = Task.Run(() =>
            {
                if (rawTrades.Count < 50) return routerCap;
                return new RegimeRouterGA(
                        populationSize:    50,
                        generations:       RouterGens,
                        eliteCount:        8,
                        migrationInterval: 10,
                        verbose:           false)
                    .Run(data.BtcSeries, data.EthSeries, rawTrades, routerCap);
            });

            var guardTask = Task.Run(() =>
            {
                if (data.BtcH1.Length < 50 || (guardValCap.Count < 20 && guardOosCap.Count < 20))
                    return guardCap;
                return (DynamicGuardGenotype?)new DynamicGuardGA(GuardPopSize, GuardGens)
                    .Run(data.BtcH1, guardValCap, guardOosCap);
            });

            Task.WaitAll(routerTask, guardTask);
            routerElite = routerTask.Result;
            guardBest   = guardTask.Result;

            Console.WriteLine($"\n  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");

            // Rebuild guard trade lists using the just-evolved Router for the next round
            if (data.BtcH1.Length >= 50)
            {
                (guardVal, guardOos) = BuildGuardTrades(
                    fsSeed, dlSeed, slSeed, data.GridGeno, data, routerElite);
                Console.WriteLine($"  Guard trades: val={guardVal.Count}  pseudo-oos={guardOos.Count}");
            }
        }

        return new CoevolveResult(flSeed!, dlSeed!, slSeed!, routerElite, guardBest!);
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
