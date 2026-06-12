namespace TradingGA;

// Cooperative coevolution: FadeLong · DipLong · SwingLong · RegimeRouter · DynamicGuard.
//
// The problem with sequential training:
//   Strategies train without knowing the router's gating thresholds, so they optimise for
//   trades the router would later reject. The router then trains on those fixed, misaligned
//   trade lists. The guard is calibrated on strategies that never adapted to it.
//
// What coevolution does — each cycle:
//   1. FadeLong evolves with the current router bear-gate (no guard: FadeLong is not guarded).
//   2. DipLong evolves with router bull-gate × guard multiplier.
//   3. SwingLong evolves with router bull-gate × guard multiplier.
//   4. Router evolves over fresh trade lists from all strategy elites.
//   5. DynamicGuard evolves on the router-gated trade lists.
//
// With guard pressure in fitness, DipLong/SwingLong discover exits that perform well even
// when positions are scaled down during volatile periods (broader TP/SL); the guard in
// turn calibrates to a tighter response. The router adjusts its mid-range thresholds to
// exploit the updated strategy + guard landscape.
//
// Cycle 1 is an unbiased warm-start (no gating, no guard) so strategies build baseline edge
// before the router and guard begin shaping them. Cycles 2-N apply progressive pressure.
//
// FadeShort is always-on and not router/guard-gated — it is not coevolved.
public class CoevolveGA
{
    public record AllData(
        IReadOnlyList<FadeLongGA.CoinData>         FlCoins,
        IReadOnlyList<DipLongGA.CoinData>          DlCoins,
        IReadOnlyList<SwingLongGA.CoinData>        SlCoins,   // same 80/20 split as DlCoins
        IReadOnlyList<(Candle[] H1, Candle[] M15)> AllCoins,  // full history for FadeShort + Grid trade gen
        RegimeBar[]                                BtcSeries,
        RegimeBar[]?                               EthSeries,
        Candle[]                                   BtcH1,     // raw BTC H1 for DynamicGuardSession
        GridGenotype?                              GridGeno); // pre-trained grid — included in router trade list

    public record CoevolveResult(
        FadeLongGenotype      FadeLong,
        DipLongGenotype       DipLong,
        SwingLongGenotype     SwingLong,
        RegimeRouterGenotype  Router,
        DynamicGuardGenotype  DynamicGuard);

    private const int Cycles       = 4;
    private const int StrategyGens = 40;
    private const int RouterGens   = 40;
    private const int GuardGens    = 40;

    public CoevolveResult Run(
        AllData               data,
        FadeShortGenotype?    fsSeed,      // fixed reference — not coevolved
        FadeLongGenotype?     flSeed,
        DipLongGenotype?      dlSeed,
        SwingLongGenotype?    slSeed,
        RegimeRouterGenotype? routerSeed,
        DynamicGuardGenotype? dgSeed)
    {
        var routerElite = routerSeed ?? RegimeRouterGenotype.Random(new Random(), null);
        var flBest      = flSeed;
        var dlBest      = dlSeed;
        var slBest      = slSeed;
        var guardBest   = dgSeed;

        for (int cycle = 0; cycle < Cycles; cycle++)
        {
            Console.WriteLine($"\n{'═',80}");
            Console.WriteLine($"  Coevolve Cycle {cycle + 1}/{Cycles}");
            Console.WriteLine($"{'═',80}");
            Console.WriteLine($"  Router: {routerElite}");
            if (guardBest != null) Console.WriteLine($"  Guard:  {guardBest}");

            // Cycle 1: warm-start with no gating — strategies build baseline edge first.
            bool useGating = cycle > 0;

            RegimeRouterSession? session = useGating
                ? new RegimeRouterSession(data.BtcSeries, data.EthSeries, routerElite)
                : null;

            // Guard session: applied to DipLong + SwingLong gates (not FadeLong — it's unguarded).
            DynamicGuardSession? guardSession = (useGating && guardBest != null && data.BtcH1.Length > 0)
                ? new DynamicGuardSession(data.BtcH1, guardBest)
                : null;

            Func<DateTime, double>? bearGate = session != null
                ? t => session.GetWeight(RegimeRouterGA.StrategyKind.FadeLong, t)
                : null;

            // Combined bull gate: router soft-gate × guard multiplier.
            // Strategies see full weight in calm bull periods, reduced weight when volatile.
            Func<DateTime, double>? bullGate = session != null
                ? t => session.GetWeight(RegimeRouterGA.StrategyKind.DipLong, t)
                      * (guardSession?.GetMult(t) ?? 1.0)
                : guardSession != null
                    ? t => guardSession.GetMult(t)
                    : null;

            if (useGating)
                Console.WriteLine("  Gate: FadeLong=bear·conf  DipLong/SwingLong=bull·conf × guard");
            else
                Console.WriteLine("  Gate: none (warm-start cycle)");

            // ── FadeLong phase ────────────────────────────────────────────────
            Console.WriteLine("\n─── FadeLong (bear-gated, no guard) ───");
            flBest = new FadeLongGA(
                    populationSize:    60,
                    generations:       StrategyGens,
                    eliteCount:        10,
                    migrationInterval: 10,
                    verbose:           true,
                    tradeGate:         bearGate)
                .Run(data.FlCoins, flBest);

            // ── DipLong phase ─────────────────────────────────────────────────
            Console.WriteLine("\n─── DipLong (bull-gated × guard) ───");
            dlBest = new DipLongGA(
                    populationSize:    60,
                    generations:       StrategyGens,
                    eliteCount:        10,
                    migrationInterval: 10,
                    verbose:           true,
                    tradeGate:         bullGate)
                .Run(data.DlCoins, dlBest);

            // ── SwingLong phase ───────────────────────────────────────────────
            Console.WriteLine("\n─── SwingLong (bull-gated × guard) ───");
            slBest = new SwingLongGA(
                    populationSize:    60,
                    generations:       StrategyGens,
                    eliteCount:        10,
                    migrationInterval: 10,
                    verbose:           true,
                    tradeGate:         bullGate)
                .Run(data.SlCoins, slBest);

            // ── Router phase ──────────────────────────────────────────────────
            Console.WriteLine("\n─── Router (fresh trade lists from current elites) ───");
            var routerTrades = BuildTradeLists(fsSeed, flBest, dlBest, slBest, data.GridGeno, data);
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
                Console.WriteLine("  !! Too few trades for router training — skipping router phase this cycle.");
            }

            // ── DynamicGuard phase ────────────────────────────────────────────
            // Rebuild session with updated router, then build guard trade list
            // (router-gated, chronologically split val/pseudo-oos for calibration).
            Console.WriteLine("\n─── DynamicGuard (calibrate on router-gated trades) ───");
            if (data.BtcH1.Length >= 50)
            {
                var (guardVal, guardOos) = BuildGuardTrades(
                    fsSeed, flBest, dlBest, slBest, data.GridGeno, data, routerElite);

                if (guardVal.Count >= 20 || guardOos.Count >= 20)
                {
                    guardBest = new DynamicGuardGA(
                            populationSize: _populationSize,
                            generations:    GuardGens)
                        .Run(data.BtcH1, guardVal, guardOos);
                }
                else
                {
                    Console.WriteLine("  !! Too few trades for guard training — skipping guard phase this cycle.");
                }
            }
            else
            {
                Console.WriteLine("  !! BTC H1 data too short for guard — skipping guard phase.");
            }
        }

        return new CoevolveResult(flBest!, dlBest!, slBest!, routerElite, guardBest!);
    }

    private const int _populationSize = 40;

    // Builds guard trade list from current strategy elites, router-gated.
    // Splits chronologically: first 75% → val, last 25% → pseudo-oos.
    // IsGuarded flag follows DynamicGuardSession.IsGuarded (Grid, DipLong, SwingLong).
    private static (
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, bool IsGuarded)> val,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, bool IsGuarded)> oos)
    BuildGuardTrades(
        FadeShortGenotype?   fs,
        FadeLongGenotype?    fl,
        DipLongGenotype?     dl,
        SwingLongGenotype?   sl,
        GridGenotype?        grid,
        AllData              data,
        RegimeRouterGenotype router)
    {
        var session = new RegimeRouterSession(data.BtcSeries, data.EthSeries, router);
        var all = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, bool IsGuarded)>();

        foreach (var (h1, m15) in data.AllCoins)
        {
            if (h1.Length < 200) continue;

            if (fs != null)
            {
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fs, h1, m15))
                    all.Add((t.Time, t.Return, fs.PositionSizePct, TimeSpan.FromHours(fs.MaxHoldCandles), false));
            }

            if (grid != null)
            {
                foreach (var t in GridSimulator.GetGridReturns(grid, h1)
                             .Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)))
                    all.Add((t.Time, t.Return, 0.05, TimeSpan.FromHours(grid.MaxHoldCandles), true));
            }

            if (fl is { Fitness: > 0 })
            {
                foreach (var t in FadeLongSimulator.GetFadeLongReturns(fl, h1, m15)
                             .Where(t => t.RegimeBarsActive >= fl.RegimeSustainedBars
                                      && session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)))
                    all.Add((t.Time, t.Return, fl.PositionSizePct, TimeSpan.FromHours(fl.MaxHoldCandles), false));
            }

            if (dl is { Fitness: > 0 })
            {
                foreach (var t in DipLongSimulator.GetDipLongReturns(dl, h1, m15)
                             .Where(t => t.RegimeBarsActive >= dl.RegimeSustainedBars
                                      && session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)))
                    all.Add((t.Time, t.Return, dl.PositionSizePct, TimeSpan.FromHours(dl.MaxHoldCandles), true));
            }

            if (sl != null)
            {
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(sl, h1, m15)
                             .Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)))
                    all.Add((t.Time, t.Return, sl.PositionSizePct, TimeSpan.FromHours(sl.MaxHoldCandles), true));
            }
        }

        all.Sort((a, b) => a.Time.CompareTo(b.Time));
        int split = (int)(all.Count * 0.75);
        return (all[..split], all[split..]);
    }

    // Concatenate two ReadOnlyMemory<Candle> segments into a single Candle[].
    private static Candle[] MemConcat(ReadOnlyMemory<Candle> a, ReadOnlyMemory<Candle> b)
    {
        if (a.IsEmpty) return b.ToArray();
        var r = new Candle[a.Length + b.Length];
        a.Span.CopyTo(r);
        b.Span.CopyTo(r.AsSpan(a.Length));
        return r;
    }

    // Build the combined trade list used for router fitness.
    // Reconstructs full chronological history per coin by concatenating train + val segments.
    private static List<RegimeRouterGA.TradeRecord> BuildTradeLists(
        FadeShortGenotype?  fs,
        FadeLongGenotype?   fl,
        DipLongGenotype?    dl,
        SwingLongGenotype?  sl,
        GridGenotype?       grid,
        AllData             data)
    {
        var trades = new List<RegimeRouterGA.TradeRecord>();

        if (fs != null)
        {
            foreach (var (h1, m15) in data.AllCoins)
            {
                if (h1.Length < 200) continue;
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fs, h1, m15))
                    trades.Add(new(RegimeRouterGA.StrategyKind.FadeShort, t.Time, t.Return, fs.PositionSizePct));
            }
        }

        if (fl is { Fitness: > 0 })
        {
            foreach (var cd in data.FlCoins)
            {
                Candle[] h1  = MemConcat(cd.TrainH1,  cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) continue;
                foreach (var t in FadeLongSimulator.GetFadeLongReturns(fl, h1, m15)
                             .Where(t => t.RegimeBarsActive >= fl.RegimeSustainedBars))
                    trades.Add(new(RegimeRouterGA.StrategyKind.FadeLong, t.Time, t.Return, fl.PositionSizePct));
            }
        }

        if (dl is { Fitness: > 0 })
        {
            foreach (var cd in data.DlCoins)
            {
                Candle[] h1  = MemConcat(cd.TrainH1,  cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) continue;
                foreach (var t in DipLongSimulator.GetDipLongReturns(dl, h1, m15)
                             .Where(t => t.RegimeBarsActive >= dl.RegimeSustainedBars))
                    trades.Add(new(RegimeRouterGA.StrategyKind.DipLong, t.Time, t.Return, dl.PositionSizePct));
            }
        }

        // SwingLong shares the bull-regime gate with DipLong in the router.
        if (sl != null)
        {
            foreach (var cd in data.SlCoins)
            {
                Candle[] h1  = MemConcat(cd.TrainH1,  cd.ValH1);
                Candle[] m15 = MemConcat(cd.TrainM15, cd.ValM15);
                if (h1.Length == 0) continue;
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(sl, h1, m15))
                    trades.Add(new(RegimeRouterGA.StrategyKind.DipLong, t.Time, t.Return, sl.PositionSizePct));
            }
        }

        if (grid != null)
        {
            foreach (var (h1, _) in data.AllCoins)
            {
                if (h1.Length < 200) continue;
                foreach (var t in GridSimulator.GetGridReturns(grid, h1))
                    trades.Add(new(RegimeRouterGA.StrategyKind.Grid, t.Time, t.Return, 0.05));
            }
        }

        return trades;
    }
}
