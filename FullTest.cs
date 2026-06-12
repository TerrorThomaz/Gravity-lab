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
                    && Simulator.ProfitFactor(screenRets) >= 1.3
                    && Simulator.SortinoRatio(screenRets, h1Screen.Length * 12) >= 0.5;
                if (oosScreenPass)
                {
                    var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15);
                    var vRet   = trades.Select(t => t.Return).ToList();
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

        var valSim = ToSim(valAll);
        var oosSim = ToSim(oosAll);

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

        var val5p  = valSim.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(valSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
        var valKel = valSim.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(valSim, Config.MaxTotalExposurePct) : default!;
        var oos5p  = oosSim.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
        var oosKel = oosSim.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSim, Config.MaxTotalExposurePct) : default!;

        var (vr5, va5, vd5, vs5)  = valSim.Count > 0 ? PortMetrics(valSim, val5p)  : ("—","—","—","—");
        var (vrK, vaK, vdK, vsK)  = valSim.Count > 0 ? PortMetrics(valSim, valKel) : ("—","—","—","—");
        var (or5, oa5, od5, os5)  = oosSim.Count > 0 ? PortMetrics(oosSim, oos5p)  : ("—","—","—","—");
        var (orK, oaK, odK, osK)  = oosSim.Count > 0 ? PortMetrics(oosSim, oosKel) : ("—","—","—","—");

        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  PORTFOLIO SIMULATION (€100 start · 30% total cap)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"",14}  {"Val 5%cap",12}  {"Val Kelly",12}  {"OOS 5%cap",12}  {"OOS Kelly",12}");
        Console.WriteLine($"  {new string('-', 70)}");
        Console.WriteLine($"  {"Return",-14}  {vr5,12}  {vrK,12}  {or5,12}  {orK,12}");
        Console.WriteLine($"  {"Annualised",-14}  {va5,12}  {vaK,12}  {oa5,12}  {oaK,12}");
        Console.WriteLine($"  {"Max DD",-14}  {vd5,12}  {vdK,12}  {od5,12}  {odK,12}");
        Console.WriteLine($"  {"Sharpe",-14}  {vs5,12}  {vsK,12}  {os5,12}  {osK,12}");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 3: YEAR-BY-YEAR (val window)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  YEAR-BY-YEAR (val window)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Year",-6}  {"Regime",7}  {"N",4}  {"5% w/router",12}  {"5% no router",13}  {"Router Δ",9}  {"Kelly w/r",10}  {"Kelly no r",11}");
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
            var btcYear = btcForCrash.h1[Math.Max(0, btcForCrash.h1.Length - 8760)..];
            CrashAnalyser.Report(CrashAnalyser.DetectCrashes(btcYear), crashTrades);
            CrashAnalyser.ReportRallies(CrashAnalyser.DetectRallies(btcYear), crashTrades);
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
        Console.WriteLine($"  {"Kelly · with router",-22}  {rKR,+7:F1}%  {(valSim.Count>0?valKel.MaxDrawdownPct:0),6:F1}%  |  {orKR,+7:F1}%  {(oosSim.Count>0?oosKel.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"Kelly · no router",-22}  {rKNR,+7:F1}%  {(nrFiltered.Count>0?nrPortK.MaxDrawdownPct:0),6:F1}%  |  {orKNR,+7:F1}%  {(oosNrFiltered.Count>0?oosNrPortK.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  Val  router edge: 5%cap {Sign(r5R-r5NR)}{r5R-r5NR:F1}pp  /  Kelly {Sign(rKR-rKNR)}{rKR-rKNR:F1}pp");
        Console.WriteLine($"  OOS  router edge: 5%cap {Sign(or5R-or5NR)}{or5R-or5NR:F1}pp  /  Kelly {Sign(orKR-orKNR)}{orKR-orKNR:F1}pp");

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
                Console.WriteLine($"  Kelly: {rK*100:+0.0;-0.0}% in {span:F0}d  →  projected {aK:+0.0;-0.0}%/yr  DD {spK.MaxDrawdownPct:F1}%");
                Console.WriteLine("  Warning: Assumes stable regime — recent bull market may not persist.");
            }
            else Console.WriteLine($"  Only {slice90.Count} trades in last 90d — insufficient for projection.");
        }
        else Console.WriteLine("  Insufficient val trades for projection.");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 8: EXIT MODIFIER
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  EXIT MODIFIER (context-aware position sizing)");
        Console.WriteLine($"{new string('═', 88)}");

        if (!File.Exists(Config.ExitModifierGenoFile))
        {
            Console.WriteLine("  No exit modifier genotype found — run 'exitmodifiertrain' to train one.");
        }
        else
        {
            var emG = JsonSerializer.Deserialize<ExitModifierGenotypeDto>(
                File.ReadAllText(Config.ExitModifierGenoFile))!.ToGenotype();
            Console.WriteLine($"  Genotype: {emG}\n");

            var h1MapForEnrich = fetchedMap.ToDictionary(kv => kv.Key, kv => kv.Value.h1);

            Console.WriteLine("  Enriching trades...");
            var valEnriched = TradeEnricher.Enrich(valRawForEnrich, h1MapForEnrich);
            var oosEnriched = TradeEnricher.Enrich(oosRawForEnrich, h1MapForEnrich);

            var valBaseline = Simulator.SimulatePortfolioExposureCapped(
                valEnriched.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var oosBaseline = Simulator.SimulatePortfolioExposureCapped(
                oosEnriched.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var valModified = Simulator.SimulatePortfolioExposureCapped(
                ExitModifierGA.ApplyModifier(emG, valEnriched),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var oosModified = Simulator.SimulatePortfolioExposureCapped(
                ExitModifierGA.ApplyModifier(emG, oosEnriched),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

            string FmtPort(Simulator.PortfolioResult p) =>
                $"ret={p.EndBalance - 100:+0.1;-0.1}%  DD={p.MaxDrawdownPct:F1}%  n={p.TradesCount}";

            Console.WriteLine($"  {"",12}  {"── Val (20%) ─────────────────────",35}  {"── OOS ─────────────────────",28}");
            Console.WriteLine($"  {"Baseline",-12}  {FmtPort(valBaseline),-35}  {FmtPort(oosBaseline),-28}");
            Console.WriteLine($"  {"Modified",-12}  {FmtPort(valModified),-35}  {FmtPort(oosModified),-28}");

            double valRetDelta = valModified.EndBalance - valBaseline.EndBalance;
            double valDdDelta  = valModified.MaxDrawdownPct - valBaseline.MaxDrawdownPct;
            Console.WriteLine($"\n  Val Δ: return {valRetDelta:+0.0;-0.0}%  DD {valDdDelta:+0.0;-0.0}pp");

            Console.WriteLine($"\n  Avg context multiplier per strategy (val):");
            foreach (var strat in new[] { "swing", "grid", "diplong", "swing_long", "fadelong" })
            {
                var forStrat = valEnriched.Where(t => t.Strategy == strat).ToList();
                if (forStrat.Count == 0) continue;
                double avgMult = forStrat.Average(t => emG.ComputeMult(t));
                Console.WriteLine($"    {strat,-12}  {avgMult:F3}×  ({forStrat.Count} trades)");
            }
        }

        // ── Section 9: Dynamic Guard ───────────────────────────────────────────────
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  DYNAMIC GUARD (BTC 4H ATR/momentum · proactive · Grid/DipLong/SwingLong)");
        Console.WriteLine($"{new string('═', 88)}");

        if (!File.Exists(Config.DynamicGuardGenoFile))
        {
            Console.WriteLine("  No dynamic guard genotype found — run 'dynamicguardtrain' to train one.");
        }
        else
        {
            var dgG = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(
                File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype();
            Console.WriteLine($"  Genotype: {dgG}\n");

            if (!fetchedMap.TryGetValue("BTCUSDT", out var btcForGuard) || btcForGuard.h1.Length < 50)
            {
                Console.WriteLine("  BTC H1 data unavailable — skipping.");
            }
            else
            {
                var guardSession = new DynamicGuardSession(btcForGuard.h1, dgG);

                // Baseline: raw confs from valRawForEnrich / oosRawForEnrich (already router-gated)
                var valBaseList = valRawForEnrich
                    .Select(t => (t.Time, t.Return, t.Conf, t.HoldDuration))
                    .OrderBy(t => t.Item1).ToList();
                var oosBaseList = oosRawForEnrich
                    .Select(t => (t.Time, t.Return, t.Conf, t.HoldDuration))
                    .OrderBy(t => t.Item1).ToList();

                // Guarded: multiply guarded-strategy confs by dynamic guard multiplier
                var valGuardList = valRawForEnrich
                    .Select(t => (t.Time, t.Return,
                        DynamicGuardSession.IsGuarded(t.Strategy) ? t.Conf * guardSession.GetMult(t.Time) : t.Conf,
                        t.HoldDuration))
                    .OrderBy(t => t.Item1).ToList();
                var oosGuardList = oosRawForEnrich
                    .Select(t => (t.Time, t.Return,
                        DynamicGuardSession.IsGuarded(t.Strategy) ? t.Conf * guardSession.GetMult(t.Time) : t.Conf,
                        t.HoldDuration))
                    .OrderBy(t => t.Item1).ToList();

                var valBase = Simulator.SimulatePortfolioExposureCapped(valBaseList, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var oosBase = Simulator.SimulatePortfolioExposureCapped(oosBaseList, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var valGrd  = Simulator.SimulatePortfolioExposureCapped(valGuardList, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var oosGrd  = Simulator.SimulatePortfolioExposureCapped(oosGuardList, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

                string FmtDG(Simulator.PortfolioResult p) =>
                    $"ret={p.EndBalance - 100:+0.1;-0.1}%  DD={p.MaxDrawdownPct:F1}%  Calmar={(p.EndBalance - 100) / Math.Max(p.MaxDrawdownPct, 1.0):F1}";

                Console.WriteLine($"  {"",12}  {"── Val (20%) ──────────────────────────────",43}  {"── OOS ──────────────────────────────",37}");
                Console.WriteLine($"  {"Baseline",-12}  {FmtDG(valBase),-43}  {FmtDG(oosBase),-37}");
                Console.WriteLine($"  {"Guarded",-12}  {FmtDG(valGrd),-43}  {FmtDG(oosGrd),-37}");

                static string Sgn(double v) => $"{(v >= 0 ? "+" : "")}{v:F1}";
                Console.WriteLine($"\n  Val Δ: return {Sgn(valGrd.EndBalance - valBase.EndBalance)}%  DD {Sgn(valGrd.MaxDrawdownPct - valBase.MaxDrawdownPct)}pp");
                Console.WriteLine($"  OOS Δ: return {Sgn(oosGrd.EndBalance - oosBase.EndBalance)}%  DD {Sgn(oosGrd.MaxDrawdownPct - oosBase.MaxDrawdownPct)}pp");

                // Show avg multiplier by strategy to diagnose how much the guard is active
                Console.WriteLine($"\n  Avg guard multiplier per strategy (val):");
                foreach (var strat in new[] { "grid", "diplong", "swing_long" })
                {
                    var forStrat = valRawForEnrich.Where(t => t.Strategy == strat).ToList();
                    if (forStrat.Count == 0) continue;
                    double avgMult = forStrat.Average(t => guardSession.GetMult(t.Time));
                    Console.WriteLine($"    {strat,-12}  {avgMult:F3}×  ({forStrat.Count} trades)");
                }
            }
        }
    }
}
