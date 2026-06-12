using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class DynamicGuardTrainCommands
{
    public static async Task RunDynamicGuardTrain(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | DYNAMIC GUARD TRAIN (val 20% + OOS, BTC 4H ATR/momentum) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing Grid genotype.");      return; }

        var swingG  = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG   = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        var dlG     = File.Exists(Config.DipLongGenoFile)   ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()     : null;
        var slG     = File.Exists(Config.SwingLongGenoFile) ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype() : null;
        var routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        var allSyms = Config.BacktestCoins
            .Concat(Config.OosCoins)
            .Concat(new[] { "BTCUSDT", "ETHUSDT" })
            .Distinct().ToArray();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} training + {Config.OosCoins.Length} OOS coins...");
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

        if (!fetchedMap.TryGetValue("BTCUSDT", out var btcEntry) || btcEntry.h1.Length < 200)
        {
            Console.WriteLine("  BTC H1 data unavailable — aborting."); return;
        }
        var btcH1 = btcEntry.h1;

        RegimeRouterSession? session = null;
        if (routerG != null && btcH1.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
            RegimeBar[]? ethSeries = fetchedMap.TryGetValue("ETHUSDT", out var ethEntry) && ethEntry.h1.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        var valTrades = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, bool IsGuarded)>();
        var oosTrades = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, bool IsGuarded)>();

        foreach (var sym in Config.BacktestCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = Math.Min(h1Split * 4, m15.Length);
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            // FadeShort — exempt from guard; tighter screen matches FullTest
            {
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var fsTr = FadeShortSimulator.GetFadeShortReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
                if (fsTr.Count >= 5 && fsTr.Average() > 0
                    && Simulator.ProfitFactor(fsTr) >= 1.3
                    && Simulator.SortinoRatio(fsTr, screenH1.Length * 12) >= 0.5)
                {
                    double conf = Simulator.ComputeConfidence(fsTr);
                    foreach (var (t, ret, _) in FadeShortSimulator.GetFadeShortReturns(swingG, h1Val, m15Val))
                        valTrades.Add((t, ret, conf, TimeSpan.FromHours(swingG.MaxHoldCandles), false));
                }
            }
            // Grid — guarded
            if (h1Train.Length >= 100)
            {
                var gTr = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
                if (gTr.Count >= 5 && gTr.Average() > 0 && Simulator.ProfitFactor(gTr) >= 1.2)
                {
                    double conf = Simulator.ComputeConfidence(gTr);
                    var raw   = GridSimulator.GetGridReturns(gridG, h1Val);
                    var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
                    foreach (var t in gated)
                        valTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(gridG.MaxHoldCandles), true));
                }
            }
            // DipLong — guarded
            if (dlG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1Val, m15Val);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        valTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(dlG.MaxHoldCandles), true));
                }
            }
            // SwingLong — guarded
            if (slG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1Val, m15Val);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        valTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(slG.MaxHoldCandles), true));
                }
            }
        }

        foreach (var sym in Config.OosCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            // FadeShort — exempt; OOS screen on first 80%
            {
                int split   = (int)(h1.Length * 0.8);
                var scrRets = FadeShortSimulator.GetFadeShortReturns(swingG, h1[..split], m15[..Math.Min(split*4, m15.Length)])
                    .Select(t => t.Return).ToList();
                if (scrRets.Count >= 5 && scrRets.Average() > 0
                    && Simulator.ProfitFactor(scrRets) >= 1.3
                    && Simulator.SortinoRatio(scrRets, split * 12) >= 0.5)
                {
                    var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15);
                    if (trades.Count >= 5)
                    {
                        double conf = Simulator.ComputeConfidence(trades.Select(t => t.Return).ToList());
                        foreach (var (t, ret, _) in trades)
                            oosTrades.Add((t, ret, conf, TimeSpan.FromHours(swingG.MaxHoldCandles), false));
                    }
                }
            }
            // Grid — guarded
            {
                var raw   = GridSimulator.GetGridReturns(gridG, h1);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
                if (gated.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        oosTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(gridG.MaxHoldCandles), true));
                }
            }
            // DipLong — guarded
            if (dlG != null && m15.Length >= 1200)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        oosTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(dlG.MaxHoldCandles), true));
                }
            }
            // SwingLong — guarded
            if (slG != null && m15.Length >= 1200)
            {
                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1, m15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        oosTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(slG.MaxHoldCandles), true));
                }
            }
        }

        Console.WriteLine($"  Val trades: {valTrades.Count}  OOS trades: {oosTrades.Count}");
        Console.WriteLine($"  Guarded: val={valTrades.Count(t => t.IsGuarded)}  oos={oosTrades.Count(t => t.IsGuarded)}");
        if (valTrades.Count < 20 || oosTrades.Count < 20)
        {
            Console.WriteLine("  Insufficient trades — aborting."); return;
        }

        Console.WriteLine("\n  Running DynamicGuardGA (40 individuals, 60 generations)...\n");
        var ga   = new DynamicGuardGA(populationSize: 40, generations: 60);
        var best = ga.Run(btcH1, valTrades, oosTrades);

        // Show before/after
        var baselineSim = (List<(DateTime, double, double, TimeSpan)> t) =>
            Simulator.SimulatePortfolioExposureCapped(
                t.Select(x => (x.Item1, x.Item2, x.Item3, x.Item4)).OrderBy(x => x.Item1).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        var valRaw = valTrades.Select(t => (t.Time, t.Return, t.Conf, t.Hold)).OrderBy(t => t.Item1).ToList();
        var oosRaw = oosTrades.Select(t => (t.Time, t.Return, t.Conf, t.Hold)).OrderBy(t => t.Item1).ToList();

        var valBase = Simulator.SimulatePortfolioExposureCapped(valRaw, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosBase = Simulator.SimulatePortfolioExposureCapped(oosRaw, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        var dgSession = new DynamicGuardSession(btcH1, best);
        var valGuarded = Simulator.SimulatePortfolioExposureCapped(
            DynamicGuardGA.ApplyGuard(valTrades, dgSession), Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosGuarded = Simulator.SimulatePortfolioExposureCapped(
            DynamicGuardGA.ApplyGuard(oosTrades, dgSession), Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        Console.WriteLine($"\n  Val:  baseline ret={valBase.EndBalance - 100:+0.1;-0.1}% DD={valBase.MaxDrawdownPct:F1}%"
                        + $"  →  guarded ret={valGuarded.EndBalance - 100:+0.1;-0.1}% DD={valGuarded.MaxDrawdownPct:F1}%");
        Console.WriteLine($"  OOS:  baseline ret={oosBase.EndBalance - 100:+0.1;-0.1}% DD={oosBase.MaxDrawdownPct:F1}%"
                        + $"  →  guarded ret={oosGuarded.EndBalance - 100:+0.1;-0.1}% DD={oosGuarded.MaxDrawdownPct:F1}%");

        var dto = new DynamicGuardGenotypeDto(best.AtrLookback, best.AtrTrigger,
            best.MomLookback, best.MomThreshold, best.SizeFloor, best.Fitness,
            best.PanicTrigger, best.RecoveryBars);
        File.WriteAllText(Config.DynamicGuardGenoFile,
            System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n  Saved → {Config.DynamicGuardGenoFile}");
    }
}
