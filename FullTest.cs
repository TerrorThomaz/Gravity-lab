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
        var flG     = File.Exists(Config.FadeLongGenoFile)    ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()     : (FadeLongGenotype?)null;
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
                    && Simulator.ProfitFactor(fsTr) >= 1.2
                    && Simulator.SortinoRatio(fsTr, screenH1.Length * 12) >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(fsTr);
                    var (_, hk) = StrategyStats.KellyFraction(fsTr);
                    double fsHk = Math.Min(hk, 0.05);
                    foreach (var (t, ret, _) in FadeShortSimulator.GetFadeShortReturns(swingG, h1Val, m15Val))
                    {
                        valSwingRets.Add(ret);
                        valAll.Add((t, ret, conf, "swing"));
                        valNoRouter.Add((t, ret, conf, "swing"));
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
            }
        }

        // ── OOS trade collection (28 OOS coins, full history) ─────────────────────
        var oosSwingRets = new List<double>();
        var oosGridRets  = new List<double>();
        var oosFlRets    = new List<double>();
        var oosDlRets    = new List<double>();
        var oosSlRets    = new List<double>();
        var oosAll       = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
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

            // FadeShort
            {
                var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15);
                var vRet   = trades.Select(t => t.Return).ToList();
                if (vRet.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosSwingRets.AddRange(vRet);
                    foreach (var (t, ret, _) in trades) oosAll.Add((t, ret, conf, "swing"));
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

        valAll  = ApplyCap(valAll);
        oosAll  = ApplyCap(oosAll);
        valNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Helper: trades → exposure sim input
        List<(DateTime, double, double, TimeSpan)> ToSim(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t) =>
            t.Select(x => (x.Time, x.Return, x.Conf, StratHold(x.Strategy, swingG, gridG, flG, dlG, slG))).ToList();

        var valSim = ToSim(valAll);
        var oosSim = ToSim(oosAll);

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
        Console.WriteLine($"  YEAR-BY-YEAR (val window, router-gated)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Year",-6}  {"Regime",7}  {"Trades",6}  {"5%cap ret",10}  {"5%cap DD",9}  {"Kelly ret",10}  {"Kelly DD",9}");
        Console.WriteLine($"  {new string('-', 68)}");

        foreach (int yr in valSim.Select(t => t.Item1.Year).Distinct().OrderBy(y => y))
        {
            var yrSim = valSim.Where(t => t.Item1.Year == yr).ToList();
            if (yrSim.Count == 0) continue;
            var p5  = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var pK  = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct);
            string reg = btcSeries != null
                ? btcSeries.Where(b => b.Time.Year == yr).GroupBy(b => b.Regime)
                    .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key.ToString()[..4] ?? "?"
                : "?";
            Console.WriteLine($"  {yr,-6}  {reg,7}  {yrSim.Count,6}  {p5.EndBalance-100,+9:F1}%  {p5.MaxDrawdownPct,8:F1}%  {pK.EndBalance-100,+9:F1}%  {pK.MaxDrawdownPct,8:F1}%");
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
        // SECTION 6: ROUTER IMPACT (val window)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  ROUTER IMPACT (val window)");
        Console.WriteLine($"{new string('═', 88)}");

        var nrCapIn = valNoRouter.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time,
            t.Strategy switch {
                "swing"      => TimeSpan.FromHours(48),
                "swing_long" => TimeSpan.FromHours(48),
                "diplong"    => TimeSpan.FromHours(48),
                "fadelong"   => TimeSpan.FromHours(72),
                _            => TimeSpan.FromHours(72),
            }, t.Return, t.Conf)).ToList();
        var nrFilteredTrades = PortfolioReplay.FilterByConcurrentCap(nrCapIn).ToList();
        var nrFiltered = nrFilteredTrades
            .Select(t => (t.EntryTime, t.Return, t.Conf, StratHold(t.Strategy, swingG, gridG, flG, dlG, slG)))
            .ToList();

        var nrPort5 = nrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(nrFiltered, Config.MaxTotalExposurePct, maxPositionFrac: 0.05)
            : default!;
        var nrPortK = nrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(nrFiltered, Config.MaxTotalExposurePct)
            : default!;

        double r5R  = valSim.Count > 0     ? (val5p.EndBalance  - 100) / 100 * 100 : 0;
        double r5NR = nrFiltered.Count > 0 ? (nrPort5.EndBalance - 100) / 100 * 100 : 0;
        double rKR  = valSim.Count > 0     ? (valKel.EndBalance  - 100) / 100 * 100 : 0;
        double rKNR = nrFiltered.Count > 0 ? (nrPortK.EndBalance - 100) / 100 * 100 : 0;

        Console.WriteLine($"  {"Strategy",-12}  {"With router",11}  {"No router",9}  {"Δ",6}");
        Console.WriteLine($"  {new string('-', 44)}");
        int cntSwing = valAll.Count(t => t.Strategy == "swing");
        Console.WriteLine($"  {"FadeShort",-12}  {cntSwing,11}  {cntSwing,9}  {"—",6}");
        Console.WriteLine($"  {"Grid",-12}  {valAll.Count(t=>t.Strategy=="grid"),11}  {nrFilteredTrades.Count(t=>t.Strategy=="grid"),9}  {nrFilteredTrades.Count(t=>t.Strategy=="grid")-valAll.Count(t=>t.Strategy=="grid"),+6}");
        Console.WriteLine($"  {"DipLong",-12}  {valAll.Count(t=>t.Strategy=="diplong"),11}  {nrFilteredTrades.Count(t=>t.Strategy=="diplong"),9}  {nrFilteredTrades.Count(t=>t.Strategy=="diplong")-valAll.Count(t=>t.Strategy=="diplong"),+6}");
        Console.WriteLine($"  {"FadeLong",-12}  {valAll.Count(t=>t.Strategy=="fadelong"),11}  {nrFilteredTrades.Count(t=>t.Strategy=="fadelong"),9}  {nrFilteredTrades.Count(t=>t.Strategy=="fadelong")-valAll.Count(t=>t.Strategy=="fadelong"),+6}");
        Console.WriteLine($"  {"SwingLong",-12}  {valAll.Count(t=>t.Strategy=="swing_long"),11}  {nrFilteredTrades.Count(t=>t.Strategy=="swing_long"),9}  {nrFilteredTrades.Count(t=>t.Strategy=="swing_long")-valAll.Count(t=>t.Strategy=="swing_long"),+6}");
        Console.WriteLine($"  {"Total",-12}  {valAll.Count,11}  {nrFiltered.Count,9}");
        Console.WriteLine();
        string Sign(double v) => v >= 0 ? "+" : "";
        Console.WriteLine($"  {"Scenario",-30}  {"Return",8}  {"DD",6}");
        Console.WriteLine($"  {new string('-', 50)}");
        Console.WriteLine($"  {"5%cap · with router",-30}  {r5R,+7:F1}%  {(valSim.Count>0?val5p.MaxDrawdownPct:0),5:F1}%");
        Console.WriteLine($"  {"5%cap · no router",-30}  {r5NR,+7:F1}%  {(nrFiltered.Count>0?nrPort5.MaxDrawdownPct:0),5:F1}%");
        Console.WriteLine($"  {"Kelly · with router",-30}  {rKR,+7:F1}%  {(valSim.Count>0?valKel.MaxDrawdownPct:0),5:F1}%");
        Console.WriteLine($"  {"Kelly · no router",-30}  {rKNR,+7:F1}%  {(nrFiltered.Count>0?nrPortK.MaxDrawdownPct:0),5:F1}%");
        Console.WriteLine($"  Router edge: 5%cap {Sign(r5R-r5NR)}{r5R-r5NR:F1}pp  /  Kelly {Sign(rKR-rKNR)}{rKR-rKNR:F1}pp");

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
    }
}
