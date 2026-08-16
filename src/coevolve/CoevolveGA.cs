using System.Collections.Concurrent;

namespace TradingGA;

// Red-Queen coevolution: Router + Guard + 6 strategies co-adapt over 8 rounds.
// Each round: Router and Guard evolve in parallel, then strategies retrain against the new router.
public class CoevolveGA
{
    public record AllData(
        IReadOnlyList<FadeLongGA.CoinData>         FlCoins,
        IReadOnlyList<DipLongGA.CoinData>          DlCoins,
        IReadOnlyList<SwingLongGA.CoinData>        SlCoins,
        IReadOnlyList<RipShortGA.CoinData>         RsCoins,
        IReadOnlyList<(Candle[] H1, Candle[] M15)> AllCoins,
        IReadOnlyList<FadeShortGA.CoinData>        FsCoins,
        IReadOnlyList<GridGeneticAlgorithm.CoinData> GridCoins,
        RegimeBar[]                                BtcSeries,
        RegimeBar[]?                               EthSeries,
        Candle[]                                   BtcH1,
        GridGenotype?                              GridGeno,
        GridGenotype?                              GridShortGeno);

    public record CoevolveResult(
        FadeShortGenotype?    FadeShort,      // re-adapted each round against Router[t]
        GridGenotype?         Grid,           // re-adapted each round against Router[t]
        FadeLongGenotype      FadeLong,       // re-adapted each round against Router[t]
        DipLongGenotype       DipLong,        // re-adapted each round against Router[t]
        SwingLongGenotype     SwingLong,      // re-adapted each round against Router[t]
        RipShortGenotype      RipShort,       // re-adapted each round against Router[t]
        RegimeRouterGenotype  Router,
        DynamicGuardGenotype  DynamicGuard);

    // Soft gate floor — out-of-window trades still contribute at this weight.
    private const double GateFloor = 0.10;

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
        RipShortGenotype?     rsSeed,
        RegimeRouterGenotype? routerSeed,
        DynamicGuardGenotype? dgSeed)
    {
        var routerElite = routerSeed ?? RegimeRouterGenotype.Random(new Random(), null);
        var guardBest   = dgSeed;

        // Strategies co-adapt: retrain each round against the router's current gating.
        // Gates on t.Time (exit bar), matching how backtests gate.
        Console.WriteLine("  Building strategy trade lists (round 0, ungated)...");
        var rawTrades = BuildTradeLists(fsSeed, dlSeed, slSeed, rsSeed, data.GridGeno, data.GridShortGeno, data);
        Console.WriteLine(
            $"  {rawTrades.Count} total — " +
            string.Join("  ",
                Enum.GetValues<RegimeRouterGA.StrategyKind>()
                    .Select(k => $"{k}: {rawTrades.Count(t => t.Kind == k)}")));


        var emptyGuard = (
            val: new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)>(),
            oos: new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)>());
        var (guardTrain, guardHold) = data.BtcH1.Length >= 50
            ? BuildGuardTrades(fsSeed, dlSeed, slSeed, data.GridGeno, data.GridShortGeno, data, routerElite)
            : emptyGuard;

        for (int round = 0; round < RedQueenRounds; round++)
        {
            Console.WriteLine($"\n{'═',80}");
            Console.WriteLine($"  Red Queen Round {round + 1}/{RedQueenRounds}  (routerGens={RouterGens}  guardGens={GuardGens})");
            Console.WriteLine($"{'═',80}");
            Console.WriteLine($"  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");
            Console.WriteLine("  [PARALLEL] Router ← all trades  ║  Guard ← Router[t-1]-gated trades");


            var routerCap   = routerElite;
            var guardCap    = guardBest;
            var guardTrainCap = guardTrain;   // the GA objective — earlier slice only
            var guardHoldCap  = guardHold;    // reported, never optimised against


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
                if (data.BtcH1.Length < 50 || guardTrainCap.Count < 20)
                    return guardCap;
                // Objective is the earlier slice only.
                return (DynamicGuardGenotype?)new DynamicGuardGA(GuardPopSize, GuardGens)
                    .Run(data.BtcH1, guardTrainCap);
            });

            Task.WaitAll(routerTask, guardTask);
            routerElite = routerTask.Result;
            guardBest   = guardTask.Result;

            Console.WriteLine($"\n  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");

            // Soft gate (floor=0.10) — hard gate could starve a strategy into an unrecoverable score.
            var gateSession = new RegimeRouterSession(data.BtcSeries, data.EthSeries, routerElite);
            Func<RegimeRouterGA.StrategyKind, Func<DateTime, double>> gateFor =
                kind => t => gateSession.IsActive(kind, t) ? 1.0 : GateFloor;

            int stratGens = Math.Max(20, RouterGens / 2);
            Console.WriteLine($"  [ADAPT] retraining strategies against Router[t] (gens={stratGens}, floor={GateFloor:F2})");

            if (dlSeed != null)
                dlSeed = new DipLongGA(generations: stratGens, verbose: false,
                    tradeGate: gateFor(RegimeRouterGA.StrategyKind.DipLong),
                    btcSeries: data.BtcSeries).Run(data.DlCoins, dlSeed);
            if (slSeed != null)
                slSeed = new SwingLongGA(generations: stratGens, verbose: false,
                    tradeGate: gateFor(RegimeRouterGA.StrategyKind.SwingLong)).Run(data.SlCoins, slSeed);
            if (rsSeed != null)
                rsSeed = new RipShortGA(generations: stratGens, verbose: false,
                    tradeGate: gateFor(RegimeRouterGA.StrategyKind.RipShort)).Run(data.RsCoins, rsSeed);
            if (flSeed != null)
                flSeed = new FadeLongGA(generations: stratGens, verbose: false,
                    tradeGate: gateFor(RegimeRouterGA.StrategyKind.FadeLong),
                    btcSeries: data.BtcSeries).Run(data.FlCoins, flSeed);

            // FadeShort and Grid also co-adapt (same train-then-gate misalignment).
            if (fsSeed != null && data.FsCoins.Count > 0)
                fsSeed = new FadeShortGA(generations: stratGens, verbose: false,
                    tradeGate: gateFor(RegimeRouterGA.StrategyKind.FadeShort)).Run(data.FsCoins, fsSeed);
            if (data.GridGeno != null && data.GridCoins.Count > 0)
                data = data with { GridGeno = new GridGeneticAlgorithm(generations: stratGens, verbose: false,
                    tradeGate: gateFor(RegimeRouterGA.StrategyKind.Grid)).Run(data.GridCoins, data.GridGeno) };

            Console.WriteLine($"  [ADAPT] DipLong F={dlSeed?.Fitness:F3}  SwingLong F={slSeed?.Fitness:F3}  " +
                              $"RipShort F={rsSeed?.Fitness:F3}  FadeLong F={flSeed?.Fitness:F3}  " +
                              $"FadeShort F={fsSeed?.Fitness:F3}  Grid F={data.GridGeno?.Fitness:F3}");


            rawTrades = BuildTradeLists(fsSeed, dlSeed, slSeed, rsSeed, data.GridGeno, data.GridShortGeno, data);


            if (data.BtcH1.Length >= 50)
            {
                (guardTrain, guardHold) = BuildGuardTrades(
                    fsSeed, dlSeed, slSeed, data.GridGeno, data.GridShortGeno, data, routerElite);
                Console.WriteLine($"  Guard trades: train={guardTrain.Count}  holdout={guardHold.Count} (holdout is report-only)");
            }
        }

        return new CoevolveResult(fsSeed, data.GridGeno, flSeed!, dlSeed!, slSeed!, rsSeed!, routerElite, guardBest!);
    }



    private static List<RegimeRouterGA.TradeRecord> BuildTradeLists(
        FadeShortGenotype?  fs,
        DipLongGenotype?    dl,
        SwingLongGenotype?  sl,
        RipShortGenotype?   rs,
        GridGenotype?       grid,
        GridGenotype?       gridShort,
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

            if (gridShort != null)
                foreach (var t in GridShortSimulator.GetGridShortReturns(gridShort, h1))
                    bag.Add(new(RegimeRouterGA.StrategyKind.GridShort, t.Time, t.Return, 0.05));
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

        if (rs is { Fitness: > 0 })
        {
            Parallel.ForEach(data.RsCoins, (cd) =>
            {
                Candle[] h1  = MemConcat(cd.TrainH1, cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) return;
                foreach (var t in RipShortSimulator.GetRipShortReturnsWithRegime(rs, h1, m15)
                             .Where(t => t.RegimeBarsActive >= rs.RegimeSustainedBars))
                    bag.Add(new(RegimeRouterGA.StrategyKind.RipShort, t.Time, t.Return, rs.PositionSizePct));
            });
        }

        return [.. bag.OrderBy(t => t.Time)];
    }

    private static (
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> val,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> oos)
    BuildGuardTrades(
        FadeShortGenotype?   fs,
        DipLongGenotype?     dl,
        SwingLongGenotype?   sl,
        GridGenotype?        grid,
        GridGenotype?        gridShort,
        AllData              data,
        RegimeRouterGenotype router)
    {
        var session = new RegimeRouterSession(data.BtcSeries, data.EthSeries, router);
        var bag     = new ConcurrentBag<(DateTime, double, double, TimeSpan, string)>();

        Parallel.ForEach(data.AllCoins, (coin) =>
        {
            var (h1, m15) = coin;
            if (h1.Length < 200) return;

            if (fs != null)
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fs, h1, m15))
                    bag.Add((t.Time, t.Return, fs.PositionSizePct,
                             TimeSpan.FromHours(fs.MaxHoldCandles), "swing"));

            if (grid != null)
                foreach (var t in GridSimulator.GetGridReturns(grid, h1)
                             .Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)))
                    bag.Add((t.Time, t.Return, 0.05,
                             TimeSpan.FromHours(grid.MaxHoldCandles), "grid"));

            if (gridShort != null)
                foreach (var t in GridShortSimulator.GetGridShortReturns(gridShort, h1)
                             .Where(t => session.IsActive(RegimeRouterGA.StrategyKind.GridShort, t.Time)))
                    bag.Add((t.Time, t.Return, 0.05,
                             TimeSpan.FromHours(gridShort.MaxHoldCandles), "gridshort"));
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
                             TimeSpan.FromHours(dl.MaxHoldCandles), "diplong"));
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
                             TimeSpan.FromHours(sl.MaxHoldCandles), "swing_long"));
            });
        }

        var all   = bag.OrderBy(t => t.Item1).ToList();
        int split = (int)(all.Count * DataSplit.TrainFraction);
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
