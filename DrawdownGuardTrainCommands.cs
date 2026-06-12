using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class DrawdownGuardTrainCommands
{
    public static async Task RunDrawdownGuardTrain(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | DRAWDOWN GUARD TRAIN (val 20% + OOS, all strategies) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing Grid genotype.");      return; }

        var swingG  = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG   = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        var flG     = File.Exists(Config.FadeLongGenoFile)  ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()   : null;
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

        RegimeRouterSession? session = null;
        if (routerG != null && fetchedMap.TryGetValue("BTCUSDT", out var btcEntry) && btcEntry.h1.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
            RegimeBar[]? ethSeries = fetchedMap.TryGetValue("ETHUSDT", out var ethEntry) && ethEntry.h1.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        var valTrades = new List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, bool IsGuarded)>();
        var oosTrades = new List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, bool IsGuarded)>();

        // Val coins — 80/20 time split with FadeShort screen
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

            // FadeShort — screen on train, exempt from guard
            {
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var fsTr = FadeShortSimulator.GetFadeShortReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
                if (fsTr.Count >= 5 && fsTr.Average() > 0
                    && Simulator.ProfitFactor(fsTr) >= 1.2
                    && Simulator.SortinoRatio(fsTr, screenH1.Length * 12) >= 0.3)
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
            // FadeLong — exempt from guard
            if (flG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        valTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(flG.MaxHoldCandles), false));
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

        // OOS coins — full history
        foreach (var sym in Config.OosCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300 || m15.Length == 0) continue;

            // FadeShort — exempt
            {
                var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15);
                if (trades.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(trades.Select(t => t.Return).ToList());
                    foreach (var (t, ret, _) in trades)
                        oosTrades.Add((t, ret, conf, TimeSpan.FromHours(swingG.MaxHoldCandles), false));
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
            // FadeLong — exempt
            if (flG != null && m15.Length >= 1200)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    foreach (var t in gated)
                        oosTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(flG.MaxHoldCandles), false));
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
            Console.WriteLine("  Insufficient trades — aborting.");
            return;
        }

        Console.WriteLine("\n  Running DrawdownGuardGA (40 individuals, 60 generations)...\n");
        var ga   = new DrawdownGuardGA(populationSize: 40, generations: 60);
        var best = ga.Run(valTrades, oosTrades);

        var valBaseline = Simulator.SimulatePortfolioExposureCapped(
            valTrades.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
            Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosBaseline = Simulator.SimulatePortfolioExposureCapped(
            oosTrades.Select(t => (t.EntryTime, t.Return, t.CoinConf, t.HoldDuration)).OrderBy(t => t.EntryTime).ToList(),
            Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var valGuarded = Simulator.SimulateWithDrawdownGuard(valTrades, best, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosGuarded = Simulator.SimulateWithDrawdownGuard(oosTrades, best, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        Console.WriteLine($"\n  Val:  before {valBaseline.EndBalance - 100:+0.1;-0.1}% DD={valBaseline.MaxDrawdownPct:F1}%"
                        + $"  →  after {valGuarded.EndBalance - 100:+0.1;-0.1}% DD={valGuarded.MaxDrawdownPct:F1}%");
        Console.WriteLine($"  OOS:  before {oosBaseline.EndBalance - 100:+0.1;-0.1}% DD={oosBaseline.MaxDrawdownPct:F1}%"
                        + $"  →  after {oosGuarded.EndBalance - 100:+0.1;-0.1}% DD={oosGuarded.MaxDrawdownPct:F1}%");

        var dto = new DrawdownGuardGenotypeDto(best.ActivationDD, best.FullDD, best.SizeFloor, best.Fitness);
        File.WriteAllText(Config.DrawdownGuardGenoFile,
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n  Saved → {Config.DrawdownGuardGenoFile}");
    }
}
