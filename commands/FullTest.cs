using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class FullTest
{
    public static async Task RunFullTest(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | FULL TEST (val 20% + 28 OOS · all strategies · condensed) ===\n");

        // ── Genotypes ─────────────────────────────────────────────────────────────
        var swingG  = File.Exists(Config.FadeShortGenoFile)  ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype()   : (FadeShortGenotype?)null;
        var gridG   = File.Exists(Config.GridGenoFile)        ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()             : (GridGenotype?)null;
        var flG     = (FadeLongGenotype?)null;  // disabled — PF=0.06 OOS, net drag
        var dlG     = File.Exists(Config.DipLongGenoFile)     ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()       : (DipLongGenotype?)null;
        var slG     = File.Exists(Config.SwingLongGenoFile)   ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype()   : (SwingLongGenotype?)null;
        var routerG = File.Exists(Config.RouterGenoFile)      ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()   : (RegimeRouterGenotype?)null;

        if (swingG == null) { Console.WriteLine("Missing FadeShort genotype — run 'train' first."); return; }
        if (gridG  == null) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first."); return; }

        Console.WriteLine($"  FadeShort: {swingG}");
        Console.WriteLine($"  Grid:      {gridG}");
        if (flG     != null) Console.WriteLine($"  FadeLong:  {flG}");
        if (dlG     != null) Console.WriteLine($"  DipLong:   {dlG}");
        if (slG     != null) Console.WriteLine($"  SwingLong: {slG}");
        if (routerG != null) Console.WriteLine($"  Router:    {routerG}");
        Console.WriteLine();

        // ── Single candle fetch: training + OOS + BTC/ETH ─────────────────────────
        var allSyms = Config.BacktestCoins
            .Concat(Config.OosCoins)
            .Concat(new[] { "BTCUSDT", "ETHUSDT" })
            .Distinct().ToArray();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} training + {Config.OosCoins.Length} OOS coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = allSyms.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, m15: m15.ToArray(), h1);
            }
            finally { sem.Release(); }
        });
        var fetched    = await Task.WhenAll(fetchTasks);
        var fetchedMap = fetched.ToDictionary(f => f.sym);
        Console.WriteLine("  Done.\n");

        // ── Router session ─────────────────────────────────────────────────────────
        RegimeRouterSession? session   = null;
        RegimeBar[]?         btcSeries = null;
        if (routerG != null && fetchedMap.TryGetValue("BTCUSDT", out var btcEntry) && btcEntry.h1.Length >= 200)
        {
            btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
            RegimeBar[]? ethSeries = null;
            if (fetchedMap.TryGetValue("ETHUSDT", out var ethEntry) && ethEntry.h1.Length >= 200)
                ethSeries = RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1);
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
            Console.WriteLine($"  Router session: BTC {btcSeries.Length} bars  ETH {(ethSeries != null ? ethSeries.Length.ToString() : "none")} bars\n");
        }

        // ── Funding rate session (BTC as market-wide proxy) ───────────────────────────
        FundingRateSession? fundingSession = null;
        if (fetchedMap.ContainsKey("BTCUSDT"))
        {
            var btcFunding = await CandleFetcher.FetchFundingRateCachedAsync(client, "BTCUSDT");
            if (btcFunding.Length > 0)
            {
                fundingSession = new FundingRateSession(btcFunding);
                Console.WriteLine($"  BTC funding: {btcFunding.Length} 8h records · current={fundingSession.CurrentRate:+0.0000%;-0.0000%;0.0000%}/8h\n");
            }
        }

        // ── Dynamic guard session (built once, used in Section 2 and 9) ─────────────
        DynamicGuardSession? dgSession = null;
        DynamicGuardGenotype? dgGeno   = null;
        if (File.Exists(Config.DynamicGuardGenoFile) && fetchedMap.TryGetValue("BTCUSDT", out var btcDG) && btcDG.h1.Length >= 50)
        {
            dgGeno   = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype();
            dgSession = new DynamicGuardSession(btcDG.h1, dgGeno);
        }

        // ── Val trade collection (training coins, 80/20 time split) ───────────────
        var valSwingRets = new List<double>();
        var valGridRets  = new List<double>();
        var valFlRets    = new List<double>();
        var valDlRets    = new List<double>();
        var valSlRets    = new List<double>();
        var valAll         = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var valNoRouter    = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var crashTrades    = new List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)>();
        int valCandleCount = 0;
        var valRawForEnrich = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();
        var oosRawForEnrich = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();

        foreach (var sym in Config.BacktestCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
            valCandleCount += h1Val.Length * 12;

            // FadeShort — screen on train, test on val
            {
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var fsTr = FadeShortSimulator.GetFadeShortReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
                if (fsTr.Count >= 5 && fsTr.Average() > 0
                    && Simulator.ProfitFactor(fsTr) >= 1.3
                    && Simulator.SortinoRatio(fsTr, screenH1.Length * 12) >= 0.5)
                {
                    double conf = Simulator.ComputeConfidence(fsTr);
                    var (_, hk) = StrategyStats.KellyFraction(fsTr);
                    double fsHk = Math.Min(hk, 0.05);
                    foreach (var (t, ret, _) in FadeShortSimulator.GetFadeShortReturns(swingG, h1Val, m15Val))
                    {
                        if (fundingSession?.IsCrowdedShort(t) == true) continue;
                        valSwingRets.Add(ret);
                        valAll.Add((t, ret, conf, "swing"));
                        valNoRouter.Add((t, ret, conf, "swing"));
                        valRawForEnrich.Add((t, ret, conf, "swing", sym, TimeSpan.FromHours(swingG.MaxHoldCandles)));
                        crashTrades.Add((t - TimeSpan.FromHours(swingG.MaxHoldCandles), t, ret, fsHk, "FadeShort"));
                    }
                }
            }

            // Grid
            if (h1Train.Length >= 100)
            {
                var gTr = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
                if (gTr.Count >= 5 && gTr.Average() > 0
                    && Simulator.ProfitFactor(gTr) >= 1.2
                    && Simulator.SortinoRatio(gTr, h1Train.Length) >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(gTr);
                    var raw = GridSimulator.GetGridReturns(gridG, h1Val);
                    var gated = session != null
                        ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList()
                        : raw;
                    valGridRets.AddRange(gated.Select(t => t.Return));
                    foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in gated)
                        valRawForEnrich.Add((t.Time, t.Return, conf, "grid", sym, TimeSpan.FromHours(gridG.MaxHoldCandles)));
                }
            }

            // FadeLong
            if (flG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                double conf = Simulator.ComputeConfidence(
                    FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val).Select(t => t.Return).ToList());
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                valFlRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "fadelong"));
                foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "fadelong"));
                foreach (var t in gated)
                    valRawForEnrich.Add((t.Time, t.Return, conf, "fadelong", sym, TimeSpan.FromHours(flG!.MaxHoldCandles)));
            }

            // DipLong
            if (dlG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                double conf = Simulator.ComputeConfidence(
                    DipLongSimulator.GetDipLongReturns(dlG, h1Val, m15Val).Select(t => t.Return).ToList());
                double dlHk = Math.Min(dlG.PositionSizePct, 0.05);
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                valDlRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated)
                {
                    valAll.Add((t.Time, t.Return, conf, "diplong"));
                    crashTrades.Add((t.Time - TimeSpan.FromHours(dlG.MaxHoldCandles), t.Time, t.Return, dlHk, "DipLong"));
                }
                foreach (var t in raw) valNoRouter.Add((t.Time, t.Return, conf, "diplong"));
                foreach (var t in gated)
                    valRawForEnrich.Add((t.Time, t.Return, conf, "diplong", sym, TimeSpan.FromHours(dlG!.MaxHoldCandles)));
            }

            // SwingLong
            if (slG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                double conf = Simulator.ComputeConfidence(
                    SwingLongSimulator.GetSwingLongReturns(slG, h1Val, m15Val).Select(t => t.Return).ToList());
                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()  // SwingLong shares bull-regime gate with DipLong
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                valSlRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "swing_long"));
                foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "swing_long"));
                foreach (var t in gated)
                    valRawForEnrich.Add((t.Time, t.Return, conf, "swing_long", sym, TimeSpan.FromHours(slG!.MaxHoldCandles)));
            }
        }

        // ── OOS trade collection (28 OOS coins, full history) ─────────────────────
        var oosSwingRets = new List<double>();
        var oosGridRets  = new List<double>();
        var oosFlRets    = new List<double>();
        var oosDlRets    = new List<double>();
        var oosSlRets    = new List<double>();
        var oosAll       = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var oosNoRouter  = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        int oosCandleCount = 0;
        const double oosMinVol = 0.05;

        foreach (var sym in Config.OosCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < oosMinVol) continue;
            oosCandleCount += h1.Length * 12;

            // FadeShort — screen on first 80% of OOS coin history
            {
                int oosSplit   = (int)(h1.Length * 0.8);
                int oosM15Spl  = oosSplit * 4;
                var h1Screen   = h1[..oosSplit];
                var m15Screen  = m15[..Math.Min(oosM15Spl, m15.Length)];
                var screenRets = FadeShortSimulator.GetFadeShortReturns(swingG, h1Screen, m15Screen)
                    .Select(t => t.Return).ToList();
                bool oosScreenPass = screenRets.Count >= 5 && screenRets.Average() > 0
                    && Simulator.ProfitFactor(screenRets) >= 1.1;
                if (oosScreenPass)
                {
                    var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15)
                        .Where(t => fundingSession?.IsCrowdedShort(t.Time) != true).ToList();
                    var vRet = trades.Select(t => t.Return).ToList();
                    if (vRet.Count >= 5)
                    {
                        double conf = Simulator.ComputeConfidence(vRet);
                        oosSwingRets.AddRange(vRet);
                        foreach (var (t, ret, _) in trades) oosAll.Add((t, ret, conf, "swing"));
                        foreach (var (t, ret, _) in trades)
                            oosRawForEnrich.Add((t, ret, conf, "swing", sym, TimeSpan.FromHours(swingG.MaxHoldCandles)));
                    }
                }
            }

            // Grid
            {
                var raw   = GridSimulator.GetGridReturns(gridG, h1);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosGridRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "grid", sym, TimeSpan.FromHours(gridG.MaxHoldCandles)));
                }
            }

            // FadeLong
            if (flG != null && m15.Length >= 1200)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosFlRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "fadelong"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "fadelong"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "fadelong", sym, TimeSpan.FromHours(flG!.MaxHoldCandles)));
                }
            }

            // DipLong
            if (dlG != null && m15.Length >= 1200)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosDlRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "diplong"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "diplong"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "diplong", sym, TimeSpan.FromHours(dlG!.MaxHoldCandles)));
                }
            }

            // SwingLong
            if (slG != null && m15.Length >= 1200)
            {
                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()  // SwingLong shares bull-regime gate with DipLong
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosSlRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "swing_long"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "swing_long"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "swing_long", sym, TimeSpan.FromHours(slG!.MaxHoldCandles)));
                }
            }
        }

        // ── Apply concurrent cap ───────────────────────────────────────────────────
        static TimeSpan StratHold(string s, FadeShortGenotype sw, GridGenotype gr,
            FadeLongGenotype? fl, DipLongGenotype? dl, SwingLongGenotype? sl) => s switch
        {
            "swing"      => TimeSpan.FromHours(sw.MaxHoldCandles),
            "swing_long" => TimeSpan.FromHours(sl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "fadelong"   => TimeSpan.FromHours(fl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "diplong"    => TimeSpan.FromHours(dl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            _            => TimeSpan.FromHours(gr.MaxHoldCandles),
        };

        static List<(DateTime Time, double Return, double Conf, string Strategy)> ApplyCap(
            List<(DateTime Time, double Return, double Conf, string Strategy)> trades)
        {
            if (trades.Count == 0) return trades;
            trades.Sort((a, b) => a.Time.CompareTo(b.Time));
            var capIn = trades.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time,
                t.Strategy switch {
                    "swing"      => TimeSpan.FromHours(48),
                    "swing_long" => TimeSpan.FromHours(48),
                    "diplong"    => TimeSpan.FromHours(48),
                    "fadelong"   => TimeSpan.FromHours(72),
                    _            => TimeSpan.FromHours(72),
                }, t.Return, t.Conf)).ToList();
            return PortfolioReplay.FilterByConcurrentCap(capIn)
                .Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
        }

        valAll = ApplyCap(valAll);
        oosAll = ApplyCap(oosAll);

        // Helper: trades → exposure sim input
        List<(DateTime, double, double, TimeSpan)> ToSim(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t) =>
            t.Select(x => (x.Time, x.Return, x.Conf, StratHold(x.Strategy, swingG, gridG, flG, dlG, slG))).ToList();

        // Guarded variant: ATR entry gate first (blocks high-ATR DipLong/SwingLong),
        // then confidence scaling. Strategy preserved for the portfolio-DD gate inside the simulator.
        List<(DateTime, double, double, TimeSpan, string)> ToSimGuarded(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t,
            DynamicGuardSession gs) =>
            t.Where(x => !gs.IsEntryBlocked(x.Time, x.Strategy))
             .Select(x => (x.Time, x.Return,
                DynamicGuardSession.IsGuarded(x.Strategy) ? x.Conf * gs.GetMult(x.Time, x.Strategy) : x.Conf,
                StratHold(x.Strategy, swingG, gridG, flG, dlG, slG), x.Strategy)).ToList();

        // 5-tuple version of ToSim (adds Strategy) — used for DD-gate-aware guarded simulation.
        List<(DateTime, double, double, TimeSpan, string)> ToSim5(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t) =>
            t.Select(x => (x.Time, x.Return, x.Conf,
                StratHold(x.Strategy, swingG, gridG, flG, dlG, slG), x.Strategy)).ToList();

        // Strip Strategy from 5-tuple for callers that only need 4-tuple (PortMetrics, etc.)
        static List<(DateTime, double, double, TimeSpan)> Drop5(
            List<(DateTime, double, double, TimeSpan, string)> t) =>
            t.Select(x => (x.Item1, x.Item2, x.Item3, x.Item4)).ToList();

        var valSim  = ToSim(valAll);
        var oosSim  = ToSim(oosAll);
        var valSimG = dgSession != null ? ToSimGuarded(valAll,  dgSession) : ToSim5(valAll);
        var oosSimG = dgSession != null ? ToSimGuarded(oosAll, dgSession) : ToSim5(oosAll);

        // Pre-compute no-router sims (needed by Section 3 year-by-year and Section 6)
        static TimeSpan HoldFor(string s) => s switch {
            "swing" or "swing_long" or "diplong" => TimeSpan.FromHours(48),
            _ => TimeSpan.FromHours(72),
        };
        valNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));
        oosNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));
        var nrFilteredTrades    = PortfolioReplay.FilterByConcurrentCap(valNoRouter.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time, HoldFor(t.Strategy), t.Return, t.Conf)).ToList()).ToList();
        var nrFiltered          = nrFilteredTrades.Select(t => (t.EntryTime, t.Return, t.Conf, StratHold(t.Strategy, swingG, gridG, flG, dlG, slG))).ToList();
        var oosNrFilteredTrades = PortfolioReplay.FilterByConcurrentCap(oosNoRouter.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time, HoldFor(t.Strategy), t.Return, t.Conf)).ToList()).ToList();
        var oosNrFiltered       = oosNrFilteredTrades.Select(t => (t.EntryTime, t.Return, t.Conf, StratHold(t.Strategy, swingG, gridG, flG, dlG, slG))).ToList();

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 1: STRATEGY PERFORMANCE
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  STRATEGY PERFORMANCE");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Strategy",-12}  {"Trades",6}  {"WR",5}  {"PF",5}  {"Avg%",8}  |  {"Trades",6}  {"WR",5}  {"PF",5}  {"Avg%",8}");
        Console.WriteLine($"  {"",12}  {"── Val (20%) ─────────────────",30}  |  {"── OOS (28 coins) ────────────",29}");
        Console.WriteLine($"  {new string('-', 86)}");

        static string FmtRow(List<double> r) => r.Count > 0
            ? $"{r.Count,6}  {(double)r.Count(x => x > 0)/r.Count,5:P0}  {Simulator.ProfitFactor(r),5:F2}  {r.Average(),+8:F2}%"
            : $"{"—",6}  {"—",5}  {"—",5}  {"—",8}";

        Console.WriteLine($"  {"FadeShort",-12}  {FmtRow(valSwingRets)}  |  {FmtRow(oosSwingRets)}");
        Console.WriteLine($"  {"Grid",-12}  {FmtRow(valGridRets)}  |  {FmtRow(oosGridRets)}");
        Console.WriteLine($"  {"DipLong",-12}  {FmtRow(valDlRets)}  |  {FmtRow(oosDlRets)}");
        Console.WriteLine($"  {"SwingLong",-12}  {FmtRow(valSlRets)}  |  {FmtRow(oosSlRets)}");
        Console.WriteLine($"  {"FadeLong",-12}  {FmtRow(valFlRets)}  |  {FmtRow(oosFlRets)}");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 2: PORTFOLIO SIMULATIONS
        // ══════════════════════════════════════════════════════════════════════════
        static (string Ret, string Ann, string DD, string Sh) PortMetrics(
            List<(DateTime Time, double Return, double Conf, TimeSpan Hold)> trades,
            Simulator.PortfolioResult p)
        {
            if (trades.Count < 2) return ("—", "—", "—", "—");
            double r    = (p.EndBalance - 100.0) / 100.0 * 100;
            double days = (trades[^1].Time - trades[0].Time).TotalDays;
            double ann  = days > 0 ? (Math.Pow(1 + r / 100.0, 365.0 / days) - 1) * 100 : 0;
            var rets    = trades.Select(t => t.Return).ToList();
            double sh   = rets.Count >= 5 ? Simulator.SharpeRatio(rets, (int)(days * 24)) : 0;
            return ($"{r:+0.0;-0.0}%", $"{ann:+0.0;-0.0}%/yr", $"{p.MaxDrawdownPct:F1}%", $"{sh:F2}");
        }

        double ddGate      = dgGeno?.DdEntryGatePct         ?? 1.0;
        double capMin      = dgGeno?.ConfLossCapMin          ?? 1.0;
        double capMax      = dgGeno?.ConfLossCapMax          ?? 1.0;
        double ppThreshold = dgGeno?.ProfitProtectThreshold  ?? 1.0;
        double ppDrawback  = dgGeno?.ProfitProtectDrawback   ?? 0.10;
        double ppFactor    = dgGeno?.ProfitProtectFactor     ?? 1.0;

        var val5p   = valSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(valSim,  Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
        var valKel  = valSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(valSim,  Config.MaxTotalExposurePct) : default!;
        var oos5p   = oosSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSim,  Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
        var oosKel  = oosSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSim,  Config.MaxTotalExposurePct) : default!;
        var val5pG  = valSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(valSimG, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;
        var valKelG = valSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(valSimG, Config.MaxTotalExposurePct, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;
        var oos5pG  = oosSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSimG, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;
        var oosKelG = oosSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSimG, Config.MaxTotalExposurePct, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;

        var valSimG4 = Drop5(valSimG);
        var oosSimG4 = Drop5(oosSimG);

        var (vr5, va5, vd5, vs5)    = valSim.Count   > 0 ? PortMetrics(valSim,   val5p)   : ("—","—","—","—");
        var (vrK, vaK, vdK, vsK)    = valSim.Count   > 0 ? PortMetrics(valSim,   valKel)  : ("—","—","—","—");
        var (or5, oa5, od5, os5)    = oosSim.Count   > 0 ? PortMetrics(oosSim,   oos5p)   : ("—","—","—","—");
        var (orK, oaK, odK, osK)    = oosSim.Count   > 0 ? PortMetrics(oosSim,   oosKel)  : ("—","—","—","—");
        var (vr5G, va5G, vd5G, vs5G)  = valSimG4.Count > 0 ? PortMetrics(valSimG4, val5pG)  : ("—","—","—","—");
        var (vrKG, vaKG, vdKG, vsKG)  = valSimG4.Count > 0 ? PortMetrics(valSimG4, valKelG) : ("—","—","—","—");
        var (or5G, oa5G, od5G, os5G)  = oosSimG4.Count > 0 ? PortMetrics(oosSimG4, oos5pG)  : ("—","—","—","—");
        var (orKG, oaKG, odKG, osKG)  = oosSimG4.Count > 0 ? PortMetrics(oosSimG4, oosKelG) : ("—","—","—","—");

        bool hasGuard = dgSession != null;
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  PORTFOLIO SIMULATION (€100 start · 30% total cap)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"",16}  {"Val 5%cap",12}  {"Val Kelly(15%)",12}  {"OOS 5%cap",12}  {"OOS Kelly(15%)",12}");
        Console.WriteLine($"  {new string('-', 72)}");
        string lbl  = "No guard";
        string lblG = "Dyn guard";
        Console.WriteLine($"  {lbl,-16}  {vr5,12}  {vrK,12}  {or5,12}  {orK,12}");
        if (hasGuard) Console.WriteLine($"  {lblG,-16}  {vr5G,12}  {vrKG,12}  {or5G,12}  {orKG,12}");
        Console.WriteLine($"  {new string('-', 72)}");
        Console.WriteLine($"  {"Annualised",16}  {va5,12}  {vaK,12}  {oa5,12}  {oaK,12}");
        if (hasGuard) Console.WriteLine($"  {"",16}  {va5G,12}  {vaKG,12}  {oa5G,12}  {oaKG,12}");
        Console.WriteLine($"  {"Max DD",16}  {vd5,12}  {vdK,12}  {od5,12}  {odK,12}");
        if (hasGuard) Console.WriteLine($"  {"",16}  {vd5G,12}  {vdKG,12}  {od5G,12}  {odKG,12}");
        Console.WriteLine($"  {"Sharpe",16}  {vs5,12}  {vsK,12}  {os5,12}  {osK,12}");
        if (hasGuard) Console.WriteLine($"  {"",16}  {vs5G,12}  {vsKG,12}  {os5G,12}  {osKG,12}");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 3: YEAR-BY-YEAR (val window)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  YEAR-BY-YEAR (val window)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Year",-6}  {"Regime",7}  {"N",4}  {"5% w/router",12}  {"5% no router",13}  {"Router Δ",9}  {"Kelly(15%) w/r",10}  {"Kelly(15%) no r",11}");
        Console.WriteLine($"  {new string('-', 84)}");

        foreach (int yr in valSim.Select(t => t.Item1.Year).Distinct().OrderBy(y => y))
        {
            var yrSim = valSim.Where(t => t.Item1.Year == yr).ToList();
            if (yrSim.Count == 0) continue;
            var yrNr  = nrFiltered.Where(t => t.Item1.Year == yr).ToList();
            var p5    = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var pK    = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct);
            var p5nr  = yrNr.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(yrNr, Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
            var pKnr  = yrNr.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(yrNr, Config.MaxTotalExposurePct) : default!;
            double r5   = p5.EndBalance - 100;
            double r5nr = yrNr.Count > 0 ? p5nr.EndBalance - 100 : double.NaN;
            double rK   = pK.EndBalance - 100;
            double rKnr = yrNr.Count > 0 ? pKnr.EndBalance - 100 : double.NaN;
            string reg  = btcSeries != null
                ? btcSeries.Where(b => b.Time.Year == yr).GroupBy(b => b.Regime)
                    .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key.ToString()[..4] ?? "?"
                : "?";
            string nr5str  = double.IsNaN(r5nr)  ? "—" : $"{r5nr,+6:F1}%";
            string nrKstr  = double.IsNaN(rKnr)  ? "—" : $"{rKnr,+6:F1}%";
            string delta5  = double.IsNaN(r5nr)  ? "—" : $"{r5-r5nr,+6:F1}pp";
            Console.WriteLine($"  {yr,-6}  {reg,7}  {yrSim.Count,4}  {r5,+10:F1}%    {nr5str,10}    {delta5,8}  {rK,+8:F1}%    {nrKstr,9}");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 4: STATISTICAL EDGE (val window)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  STATISTICAL EDGE (val window)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Strategy",-12}  {"N",5}  {"Mean%",8}  {"t-stat",7}  {"p-val",7}  {"DSR",5}  {"P(μ>0)",7}");
        Console.WriteLine($"  {new string('-', 65)}");

        void PrintEdge(string name, List<double> rets)
        {
            if (rets.Count < 10) { Console.WriteLine($"  {name,-12}  {rets.Count,5}  (insufficient trades)"); return; }
            var tt    = StrategyStats.OneSampleT(rets);
            var boot  = StrategyStats.Bootstrap(rets);
            double dsr = StrategyStats.DeflatedSharpe(rets, valCandleCount);
            string sig = tt.Sig99 ? "***" : tt.Sig95 ? "*" : "";
            Console.WriteLine($"  {name,-12}  {rets.Count,5}  {tt.Mean,+8:F3}  {tt.T,7:F2}  {tt.PValue,7:F4}  {dsr,5:F3}  {boot.ProbPositive,7:P1}  {sig}");
        }

        PrintEdge("FadeShort",  valSwingRets);
        PrintEdge("Grid",       valGridRets);
        PrintEdge("DipLong",    valDlRets);
        PrintEdge("SwingLong",  valSlRets);
        PrintEdge("FadeLong",   valFlRets);

        Console.WriteLine($"\n  ── Strategy comparisons ──");
        if (valDlRets.Count >= 10 && valSwingRets.Count >= 10)
            StrategyStats.Compare("DipLong", valDlRets, "FadeShort", valSwingRets, valCandleCount);
        if (valSlRets.Count >= 10 && valSwingRets.Count >= 10)
            StrategyStats.Compare("SwingLong", valSlRets, "FadeShort", valSwingRets, valCandleCount);
        if (valDlRets.Count >= 10 && valSlRets.Count >= 10)
            StrategyStats.Compare("DipLong", valDlRets, "SwingLong", valSlRets, valCandleCount);

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 5: CRASH / RALLY ANALYSIS
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  CRASH / RALLY ANALYSIS  ({crashTrades.Count} trades in crash windows)");
        Console.WriteLine($"{new string('═', 88)}");

        if (fetchedMap.TryGetValue("BTCUSDT", out var btcForCrash))
        {
            // Use full BTC history — cache goes back to 2020, covers COVID/LUNA/FTX
            CrashAnalyser.Report(CrashAnalyser.DetectCrashes(btcForCrash.h1), crashTrades);
            CrashAnalyser.ReportRallies(CrashAnalyser.DetectRallies(btcForCrash.h1), crashTrades);
            CrashAnalyser.SyntheticWorstCase(crashTrades);
        }
        else Console.WriteLine("  BTC data unavailable — skipping crash/rally analysis");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 6: ROUTER IMPACT (val + OOS)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  ROUTER IMPACT");
        Console.WriteLine($"{new string('═', 88)}");

        // Val no-router (nrFilteredTrades / nrFiltered already computed above)
        var nrPort5 = nrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(nrFiltered, Config.MaxTotalExposurePct, maxPositionFrac: 0.05)
            : default!;
        var nrPortK = nrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(nrFiltered, Config.MaxTotalExposurePct)
            : default!;

        // OOS no-router (oosNrFilteredTrades / oosNrFiltered already computed above)
        var oosNrPort5 = oosNrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(oosNrFiltered, Config.MaxTotalExposurePct, maxPositionFrac: 0.05)
            : default!;
        var oosNrPortK = oosNrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(oosNrFiltered, Config.MaxTotalExposurePct)
            : default!;

        double r5R   = valSim.Count > 0        ? (val5p.EndBalance   - 100) / 100 * 100 : 0;
        double r5NR  = nrFiltered.Count > 0    ? (nrPort5.EndBalance  - 100) / 100 * 100 : 0;
        double rKR   = valSim.Count > 0        ? (valKel.EndBalance   - 100) / 100 * 100 : 0;
        double rKNR  = nrFiltered.Count > 0    ? (nrPortK.EndBalance  - 100) / 100 * 100 : 0;
        double or5R  = oosSim.Count > 0        ? (oos5p.EndBalance    - 100) / 100 * 100 : 0;
        double or5NR = oosNrFiltered.Count > 0 ? (oosNrPort5.EndBalance - 100) / 100 * 100 : 0;
        double orKR  = oosSim.Count > 0        ? (oosKel.EndBalance   - 100) / 100 * 100 : 0;
        double orKNR = oosNrFiltered.Count > 0 ? (oosNrPortK.EndBalance - 100) / 100 * 100 : 0;

        string Sign(double v) => v >= 0 ? "+" : "";

        Console.WriteLine($"  {"",12}  {"── Val (20%) ──────────────",26}  {"── OOS (28 coins) ─────────",26}");
        Console.WriteLine($"  {"Strategy",-12}  {"W/ router",9}  {"No router",9}  {"Δ",5}  |  {"W/ router",9}  {"No router",9}  {"Δ",5}");
        Console.WriteLine($"  {new string('-', 78)}");
        int cntSwing    = valAll.Count(t => t.Strategy == "swing");
        int oosCntSwing = oosAll.Count(t => t.Strategy == "swing");
        Console.WriteLine($"  {"FadeShort",-12}  {cntSwing,9}  {cntSwing,9}  {"—",5}  |  {oosCntSwing,9}  {oosCntSwing,9}  {"—",5}");
        foreach (var (strat, label) in new[] { ("grid","Grid"), ("diplong","DipLong"), ("fadelong","FadeLong"), ("swing_long","SwingLong") })
        {
            int vW = valAll.Count(t => t.Strategy == strat);
            int vN = nrFilteredTrades.Count(t => t.Strategy == strat);
            int oW = oosAll.Count(t => t.Strategy == strat);
            int oN = oosNrFilteredTrades.Count(t => t.Strategy == strat);
            Console.WriteLine($"  {label,-12}  {vW,9}  {vN,9}  {vN-vW,+5}  |  {oW,9}  {oN,9}  {oN-oW,+5}");
        }
        Console.WriteLine($"  {"Total",-12}  {valAll.Count,9}  {nrFiltered.Count,9}  {"",5}  |  {oosAll.Count,9}  {oosNrFiltered.Count,9}");
        Console.WriteLine();
        Console.WriteLine($"  {"Scenario",-22}  {"Val ret",8}  {"Val DD",7}  |  {"OOS ret",8}  {"OOS DD",7}");
        Console.WriteLine($"  {new string('-', 64)}");
        Console.WriteLine($"  {"5%cap · with router",-22}  {r5R,+7:F1}%  {(valSim.Count>0?val5p.MaxDrawdownPct:0),6:F1}%  |  {or5R,+7:F1}%  {(oosSim.Count>0?oos5p.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"5%cap · no router",-22}  {r5NR,+7:F1}%  {(nrFiltered.Count>0?nrPort5.MaxDrawdownPct:0),6:F1}%  |  {or5NR,+7:F1}%  {(oosNrFiltered.Count>0?oosNrPort5.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"Kelly(15%) · with router",-22}  {rKR,+7:F1}%  {(valSim.Count>0?valKel.MaxDrawdownPct:0),6:F1}%  |  {orKR,+7:F1}%  {(oosSim.Count>0?oosKel.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"Kelly(15%) · no router",-22}  {rKNR,+7:F1}%  {(nrFiltered.Count>0?nrPortK.MaxDrawdownPct:0),6:F1}%  |  {orKNR,+7:F1}%  {(oosNrFiltered.Count>0?oosNrPortK.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  Val  router edge: 5%cap {Sign(r5R-r5NR)}{r5R-r5NR:F1}pp  /  Kelly(15%) {Sign(rKR-rKNR)}{rKR-rKNR:F1}pp");
        Console.WriteLine($"  OOS  router edge: 5%cap {Sign(or5R-or5NR)}{or5R-or5NR:F1}pp  /  Kelly(15%) {Sign(orKR-orKNR)}{orKR-orKNR:F1}pp");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 7: FORWARD PROJECTION
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  FORWARD PROJECTION (last 90d val run-rate → 1yr)");
        Console.WriteLine($"{new string('═', 88)}");

        if (valSim.Count >= 10)
        {
            var lastDate = valSim[^1].Item1;
            var slice90  = valSim.Where(t => t.Item1 >= lastDate - TimeSpan.FromDays(90)).ToList();
            if (slice90.Count >= 10)
            {
                double span = (lastDate - slice90[0].Item1).TotalDays;
                var sp5  = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var spK  = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct);
                double r5  = (sp5.EndBalance - 100.0) / 100.0;
                double rK  = (spK.EndBalance - 100.0) / 100.0;
                double a5  = span > 0 ? (Math.Pow(1 + r5, 365.0 / span) - 1) * 100 : 0;
                double aK  = span > 0 ? (Math.Pow(1 + rK, 365.0 / span) - 1) * 100 : 0;
                Console.WriteLine($"  Slice: {slice90.Count} trades  {slice90[0].Item1:yyyy-MM-dd} → {lastDate:yyyy-MM-dd}  ({span:F0}d)");
                Console.WriteLine($"  5%cap: {r5*100:+0.0;-0.0}% in {span:F0}d  →  projected {a5:+0.0;-0.0}%/yr  DD {sp5.MaxDrawdownPct:F1}%");
                Console.WriteLine($"  Kelly(15%): {rK*100:+0.0;-0.0}% in {span:F0}d  →  projected {aK:+0.0;-0.0}%/yr  DD {spK.MaxDrawdownPct:F1}%");
                Console.WriteLine("  Warning: Assumes stable regime — recent bull market may not persist.");
            }
            else Console.WriteLine($"  Only {slice90.Count} trades in last 90d — insufficient for projection.");
        }
        else Console.WriteLine("  Insufficient val trades for projection.");

        // ── Section 8: Dynamic Guard ───────────────────────────────────────────────
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  DYNAMIC GUARD (BTC 4H ATR/momentum · proactive · Grid/DipLong/SwingLong)");
        Console.WriteLine($"{new string('═', 88)}");

        if (dgSession == null || dgGeno == null)
        {
            Console.WriteLine("  No dynamic guard genotype found — run 'dynamicguardtrain' to train one.");
        }
        else
        {
            Console.WriteLine($"  Genotype: {dgGeno}\n");

            // Reuse the already-computed 5%cap portfolio results from the portfolio section.
            // Baseline = val5p / oos5p (no guard, cap-filtered), Guarded = val5pG / oos5pG.
            // This ensures the guard section baseline exactly matches the "No guard" portfolio row.
            string FmtDG(Simulator.PortfolioResult p) =>
                $"ret={p.EndBalance - 100:+0.0;-0.0}%  DD={p.MaxDrawdownPct:F1}%  Calmar={(p.EndBalance - 100) / Math.Max(p.MaxDrawdownPct, 1.0):F1}";

            Console.WriteLine($"  {"",12}  {"── Val (20%) ──────────────────────────────",43}  {"── OOS ──────────────────────────────",37}");
            Console.WriteLine($"  {"Baseline",-12}  {FmtDG(val5p),-43}  {FmtDG(oos5p),-37}");
            Console.WriteLine($"  {"Guarded",-12}  {FmtDG(val5pG),-43}  {FmtDG(oos5pG),-37}");

            static string Sgn(double v) => $"{(v >= 0 ? "+" : "")}{v:F1}";
            Console.WriteLine($"\n  Val Δ: return {Sgn(val5pG.EndBalance - val5p.EndBalance)}%  DD {Sgn(val5pG.MaxDrawdownPct - val5p.MaxDrawdownPct)}pp");
            Console.WriteLine($"  OOS Δ: return {Sgn(oos5pG.EndBalance - oos5p.EndBalance)}%  DD {Sgn(oos5pG.MaxDrawdownPct - oos5p.MaxDrawdownPct)}pp");

            // Avg multiplier per strategy (uses post-cap valAll which matches portfolio section)
            Console.WriteLine($"\n  Avg guard multiplier per strategy (val):");
            foreach (var strat in new[] { "grid", "diplong", "swing_long" })
            {
                var forStrat = valAll.Where(t => t.Strategy == strat).ToList();
                if (forStrat.Count == 0) continue;
                double avgMult = forStrat.Average(t => dgSession.GetMult(t.Time));
                Console.WriteLine($"    {strat,-12}  {avgMult:F3}×  ({forStrat.Count} trades)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 10: OOS KELLY DD INVESTIGATION
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  OOS KELLY DD INVESTIGATION");
        Console.WriteLine($"{new string('═', 88)}");

        if (oosSim.Count > 0)
        {
            // Per-year OOS Kelly breakdown
            Console.WriteLine($"\n  ── OOS Kelly by year ──");
            Console.WriteLine($"  {"Year",-6}  {"N",4}  {"Return",8}  {"DD",6}  Strategies");
            Console.WriteLine($"  {new string('-', 70)}");
            foreach (int yr in oosSim.Select(t => t.Item1.Year).Distinct().OrderBy(y => y))
            {
                var yrSim = oosSim.Where(t => t.Item1.Year == yr).ToList();
                if (yrSim.Count == 0) continue;
                var pK = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct);
                double rK = pK.EndBalance - 100;
                var stratCounts = oosAll
                    .Where(t => t.Time.Year == yr)
                    .GroupBy(t => t.Strategy)
                    .Select(g => $"{g.Key}:{g.Count()}")
                    .ToArray();
                Console.WriteLine($"  {yr,-6}  {yrSim.Count,4}  {rK,+7:F1}%  {pK.MaxDrawdownPct,5:F1}%  {string.Join("  ", stratCounts)}");
            }

            // Worst-DD event: peak/trough timestamps + trade decomposition
            var (_, ddEvent, _) = Simulator.SimulateExposureCappedWithCurve(oosSim, Config.MaxTotalExposurePct);

            if (ddEvent != null)
            {
                double spanDays = (ddEvent.TroughTime - ddEvent.PeakTime).TotalDays;
                Console.WriteLine($"\n  ── Worst DD event ──");
                Console.WriteLine($"  Peak    {ddEvent.PeakTime:yyyy-MM-dd HH:mm}  balance={ddEvent.PeakBalance:F3}");
                Console.WriteLine($"  Trough  {ddEvent.TroughTime:yyyy-MM-dd HH:mm}  balance={ddEvent.TroughBalance:F3}");
                Console.WriteLine($"  Drop    {ddEvent.DropPct:F2}%  over {spanDays:F0}d");

                // Trades that entered during the DD window (post-cap oosAll matches oosSim)
                var ddTrades = oosAll
                    .Where(t => t.Time >= ddEvent.PeakTime && t.Time <= ddEvent.TroughTime)
                    .OrderBy(t => t.Time)
                    .ToList();
                Console.WriteLine($"\n  Trades entering in DD window ({ddTrades.Count} total):");
                Console.WriteLine($"  {"Strategy",-12}  {"N",4}  {"WR",5}  {"Avg%",8}  {"Sum%",8}");
                Console.WriteLine($"  {new string('-', 48)}");
                foreach (var grp in ddTrades.GroupBy(t => t.Strategy).OrderBy(g => g.Key))
                {
                    var rets = grp.Select(t => t.Return).ToList();
                    double wr  = (double)rets.Count(r => r > 0) / rets.Count;
                    Console.WriteLine($"  {grp.Key,-12}  {grp.Count(),4}  {wr,5:P0}  {rets.Average(),+8:F2}%  {rets.Sum(),+8:F1}%");
                }

                // 10 worst individual trades in the window
                var worst = ddTrades.OrderBy(t => t.Return).Take(10).ToList();
                if (worst.Count > 0)
                {
                    Console.WriteLine($"\n  Worst individual trades in DD window:");
                    Console.WriteLine($"  {"Date",-18}  {"Strategy",-12}  {"Return",8}  {"Conf",6}");
                    foreach (var t in worst)
                        Console.WriteLine($"  {t.Time:yyyy-MM-dd HH:mm}  {t.Strategy,-12}  {t.Return,+8:F2}%  {t.Conf,6:F4}");
                }

                // ATR + funding context at peak and trough
                Console.WriteLine();
                if (dgSession != null)
                {
                    double atrPeak   = dgSession.GetAtrRatio(ddEvent.PeakTime);
                    double atrTrough = dgSession.GetAtrRatio(ddEvent.TroughTime);
                    string gate      = dgGeno != null ? $"  entryGate={dgGeno.EntryAtrGate:F2}" : "";
                    Console.WriteLine($"  BTC 4H ATR ratio at peak:   {atrPeak:F3}×{gate}");
                    Console.WriteLine($"  BTC 4H ATR ratio at trough: {atrTrough:F3}×");
                    bool wouldBlock = dgSession.IsEntryBlocked(ddEvent.TroughTime, "diplong");
                    Console.WriteLine($"  Entry gate would block DipLong/SwingLong at trough: {wouldBlock}");
                }
                if (fundingSession != null)
                {
                    double rateAtPeak   = fundingSession.GetRate(ddEvent.PeakTime);
                    double rateAtTrough = fundingSession.GetRate(ddEvent.TroughTime);
                    Console.WriteLine($"  BTC funding at peak:   {rateAtPeak:+0.0000%;-0.0000%;0.0000%}/8h  " +
                        $"(crowded long: {fundingSession.IsCrowdedLong(ddEvent.PeakTime)})");
                    Console.WriteLine($"  BTC funding at trough: {rateAtTrough:+0.0000%;-0.0000%;0.0000%}/8h  " +
                        $"(crowded long: {fundingSession.IsCrowdedLong(ddEvent.TroughTime)})");
                }
            }
        }
        else Console.WriteLine("  No OOS trades — skipping.");

        // Write fulltest_results.json
        static object FtStratStats(List<double> r) => r.Count == 0
            ? new { trades = 0, winRate = 0.0, pf = 0.0, avgRet = 0.0 }
            : new
            {
                trades  = r.Count,
                winRate = Math.Round((double)r.Count(x => x > 0) / r.Count * 100, 2),
                pf      = Math.Round(Simulator.ProfitFactor(r), 4),
                avgRet  = Math.Round(r.Average(), 4),
            };

        static object FtPortStats(Simulator.PortfolioResult p, bool hasData) => !hasData
            ? new { ret = 0.0, maxDD = 0.0 }
            : new
            {
                ret   = Math.Round(p.EndBalance - 100.0, 2),
                maxDD = Math.Round(p.MaxDrawdownPct, 2),
            };

        // Portfolio-level aggregate for the backtest object
        var allValRets = valAll.Select(t => t.Return).ToList();
        var allOosRets = oosAll.Select(t => t.Return).ToList();
        var combinedRets = allValRets.Concat(allOosRets).ToList();
        int combinedVCC = Math.Max(valCandleCount, oosCandleCount);
        double btTrades  = combinedRets.Count;
        double btWinRate = combinedRets.Count > 0 ? Math.Round((double)combinedRets.Count(r => r > 0) / combinedRets.Count * 100, 2) : 0;
        double btSharpe  = combinedRets.Count >= 5 ? Math.Round(Simulator.SharpeRatio(combinedRets, combinedVCC), 4) : 0;
        // Use val 5%cap portfolio for maxDD and CAGR (primary backtest result)
        double btMaxDD   = valSim.Count > 0 ? Math.Round(-val5p.MaxDrawdownPct, 2) : 0;
        double btCagr    = 0;
        if (valSim.Count >= 2)
        {
            double valDays = (valSim[^1].Item1 - valSim[0].Item1).TotalDays;
            double valRet  = (val5p.EndBalance - 100.0) / 100.0;
            btCagr = valDays > 0 ? Math.Round((Math.Pow(1 + valRet, 365.0 / valDays) - 1) * 100, 2) : 0;
        }

        var fulltestOutput = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            backtest = new
            {
                trades  = (int)btTrades,
                winRate = btWinRate,
                sharpe  = btSharpe,
                maxDD   = btMaxDD,
                cagr    = btCagr,
            },
            val = new
            {
                FadeShort = FtStratStats(valSwingRets),
                Grid      = FtStratStats(valGridRets),
                DipLong   = FtStratStats(valDlRets),
                SwingLong = FtStratStats(valSlRets),
                FadeLong  = FtStratStats(valFlRets),
            },
            oos = new
            {
                FadeShort = FtStratStats(oosSwingRets),
                Grid      = FtStratStats(oosGridRets),
                DipLong   = FtStratStats(oosDlRets),
                SwingLong = FtStratStats(oosSlRets),
                FadeLong  = FtStratStats(oosFlRets),
            },
            portfolio = new
            {
                val5pct   = FtPortStats(val5p,  valSim.Count > 0),
                valKelly  = FtPortStats(valKel, valSim.Count > 0),
                oos5pct   = FtPortStats(oos5p,  oosSim.Count > 0),
                oosKelly  = FtPortStats(oosKel, oosSim.Count > 0),
            },
            routerImpact = new
            {
                valEdge5pct   = Math.Round(r5R  - r5NR,  2),
                valEdgeKelly  = Math.Round(rKR  - rKNR,  2),
                oosEdge5pct   = Math.Round(or5R - or5NR, 2),
                oosEdgeKelly  = Math.Round(orKR - orKNR, 2),
            },
        };
        File.WriteAllText("fulltest_results.json",
            System.Text.Json.JsonSerializer.Serialize(fulltestOutput,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("fulltest_results.json written.");
    }
}
