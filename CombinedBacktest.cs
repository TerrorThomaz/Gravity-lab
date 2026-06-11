using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class CombinedBacktest
{
    public static async Task RunCombinedBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | COMBINED BACKTEST (all strategies, router-gated, {Config.BacktestCoins.Length} coins, val 20%) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine($"Missing FadeShort genotype — run 'train' first.");     return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine($"Missing grid genotype — run 'gridtrain' first."); return; }

        var swingG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG  = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();

        FadeLongGenotype?     flG     = File.Exists(Config.FadeLongGenoFile)  ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()   : null;
        DipLongGenotype?      dlG     = File.Exists(Config.DipLongGenoFile)   ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()     : null;
        SwingLongGenotype?    slG     = File.Exists(Config.SwingLongGenoFile) ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype() : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"FadeShort: {swingG}");
        Console.WriteLine($"Grid:      {gridG}");
        if (flG     != null) Console.WriteLine($"FadeLong:  {flG}{(flG.Fitness < 0 ? " ⚠ negative fitness" : "")}");
        else                 Console.WriteLine("FadeLong:  not found — skipping");
        if (dlG     != null) Console.WriteLine($"DipLong:   {dlG}");
        else                 Console.WriteLine("DipLong:   not found — skipping");
        if (slG     != null) Console.WriteLine($"SwingLong: {slG}");
        else                 Console.WriteLine("SwingLong: not found — skipping");
        if (routerG != null) Console.WriteLine($"Router:    {routerG}");
        else                 Console.WriteLine("Router:    not found — strategies run without regime gate");
        Console.WriteLine();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
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
        var fetched = await Task.WhenAll(fetchTasks);
        Console.WriteLine($"  Done.\n");

        RegimeRouterSession? session  = null;
        RegimeBar[]?         btcRegimeSeries = null;
        if (routerG != null)
        {
            var btcEntry = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
            if (btcEntry.h1 != null && btcEntry.h1.Length >= 200)
            {
                btcRegimeSeries   = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
                var ethEntry      = fetched.FirstOrDefault(f => f.sym == "ETHUSDT");
                RegimeBar[]? ethSeries = ethEntry.h1 != null && ethEntry.h1.Length >= 200
                    ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
                session = new RegimeRouterSession(btcRegimeSeries, ethSeries, routerG);
                Console.WriteLine($"  Router session: BTC {btcRegimeSeries.Length} bars  ETH {(ethSeries != null ? ethSeries.Length.ToString() : "none")} bars\n");
            }
            else Console.WriteLine("  ⚠ BTC data insufficient for regime session — router gate disabled\n");
        }

        var swingTrades       = new List<(DateTime Time, double Return, double Conf)>();
        var gridTrades        = new List<(DateTime Time, double Return, double Conf)>();
        var flTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var dlTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var slTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var allTrades         = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var allTradesNoRouter = new List<(DateTime Time, double Return, double Conf, string Strategy)>();

        var swingCoinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var gridCoinStats  = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var flCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var dlCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var slCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();

        int swingTotalVCC = 0;
        int gridTotalVCC  = 0;
        int flTotalVCC    = 0;
        int dlTotalVCC    = 0;
        int slTotalVCC    = 0;

        var swingFullCoins = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var gridFullCoins  = new List<(string Sym, Candle[] H1, double Conf)>();
        var flFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var dlFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var slFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();

        Console.WriteLine($"══ SWING (1h setup + 15m exec) ══════════════════════════════════════════════");
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, m15, h1) in fetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

            {
                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM)
                {
                    Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${Config.MinMedianVolUsdM:F1}M)");
                    continue;
                }
            }

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
            var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
            var tRet  = FadeShortSimulator.GetFadeShortReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 5 ? tRet.Average() : double.NegativeInfinity;
            double tSort = tRet.Count >= 5 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12) : double.NegativeInfinity;
            double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;
            if (tExp <= 0 || tSort < 0.3 || tPF < 1.2)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% sort={tSort:F2} pf={tPF:F2})");
                continue;
            }

            double conf   = Simulator.ComputeConfidence(tRet);
            var    vSwing = FadeShortSimulator.GetFadeShortReturns(swingG, h1Val, m15Val);
            var    vRet   = vSwing.Select(t => t.Return).ToList();
            int    vCC    = h1Val.Length * 12;
            swingTotalVCC += vCC;

            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
            double avg  = vRet.Count > 0 ? vRet.Average() : 0;

            Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            swingCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            swingFullCoins.Add((sym, h1, m15, conf));
            foreach (var (t, ret, _) in vSwing)
            {
                swingTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "swing"));
                allTradesNoRouter.Add((t, ret, conf, "swing"));
            }
        }

        Console.WriteLine($"\n══ GRID (1h candles, ranging-market long) ════════════════════════════════════");
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, _, h1) in fetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

            {
                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM)
                {
                    Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${Config.MinMedianVolUsdM:F1}M)");
                    continue;
                }
            }

            int split   = (int)(h1.Length * 0.8);
            var h1Train = h1[..split];
            var h1Val   = h1[split..];

            var tRet  = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 5 ? tRet.Average()               : double.NegativeInfinity;
            double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet)  : 0;
            double tSort = tRet.Count >= 5 ? Simulator.SortinoRatio(tRet, h1Train.Length)  : double.NegativeInfinity;
            if (tExp <= 0 || tSort < 0.3 || tPF < 1.2)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% pf={tPF:F2} sort={tSort:F2})");
                continue;
            }

            double conf  = Simulator.ComputeConfidence(tRet);
            var    vGrid = GridSimulator.GetGridReturns(gridG, h1Val);
            var    vRet  = vGrid.Select(t => t.Return).ToList();
            int    vCC   = h1Val.Length * 12;
            gridTotalVCC += vCC;

            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
            double avg  = vRet.Count > 0 ? vRet.Average() : 0;

            Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            gridCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            gridFullCoins.Add((sym, h1, conf));
            foreach (var (t, ret, _) in vGrid)
            {
                allTradesNoRouter.Add((t, ret, conf, "grid"));
                if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                gridTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "grid"));
            }
        }

        if (flG != null)
        {
            Console.WriteLine($"\n══ FADELONG (bear-regime bounce, router-gated) ═══════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) { continue; }

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                flTotalVCC += vCC;

                var raw    = FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val);
                var gated  = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                var vRet   = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                flCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                flFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "fadelong"));
                foreach (var t in gated)
                {
                    flTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "fadelong"));
                }
            }
        }

        if (dlG != null)
        {
            Console.WriteLine($"\n══ DIPLONG (bull-regime pullback, router-gated) ══════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                dlTotalVCC += vCC;

                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                dlCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                dlFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "diplong"));
                foreach (var t in gated)
                {
                    dlTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "diplong"));
                }
            }
        }

        if (slG != null)
        {
            Console.WriteLine($"\n══ SWINGLONG (bull-regime bullish-BoS long, router-gated) ═══════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                slTotalVCC += vCC;

                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                slCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                slFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "swing_long"));
                foreach (var t in gated)
                {
                    slTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "swing_long"));
                }
            }
        }

        if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

        swingTrades.Sort((a, b)       => a.Time.CompareTo(b.Time));
        gridTrades.Sort((a, b)        => a.Time.CompareTo(b.Time));
        flTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        dlTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        slTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        allTrades.Sort((a, b)         => a.Time.CompareTo(b.Time));
        allTradesNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));

        var swingRet = swingTrades.Select(t => t.Return).ToList();
        var gridRet  = gridTrades.Select(t => t.Return).ToList();
        var flRet    = flTrades.Select(t => t.Return).ToList();
        var dlRet    = dlTrades.Select(t => t.Return).ToList();
        var slRet    = slTrades.Select(t => t.Return).ToList();
        var allRet   = allTrades.Select(t => t.Return).ToList();

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  SWING SUMMARY  (val 20%, {swingCoinStats.Count} coins, {swingRet.Count} trades)");
        Console.WriteLine($"{new string('═', 70)}");
        if (swingRet.Count > 0)
        {
            int sw = swingRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)sw / swingRet.Count:P1}  ({sw}W / {swingRet.Count - sw}L)");
            Console.WriteLine($"  Avg return:   {swingRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(swingRet, swingTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(swingRet, swingTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(swingRet):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(swingRet):F2}");
            var swingPort = Simulator.SimulatePortfolio(swingTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine();
            Console.WriteLine($"  ── Portfolio sim (€100 start · half-Kelly · 5% max) ──");
            Console.WriteLine($"    End balance:   €{swingPort.EndBalance:F2}");
            Console.WriteLine($"    Return:        {(swingPort.EndBalance - swingPort.StartBalance) / swingPort.StartBalance * 100:+0.0;-0.0}%");
            Console.WriteLine($"    Avg position:  €{swingPort.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {swingPort.MaxDrawdownPct:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine($"  Per-coin (sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in swingCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  GRID SUMMARY  (val 20%, {gridCoinStats.Count} coins, {gridRet.Count} trades)");
        Console.WriteLine($"{new string('═', 70)}");
        if (gridRet.Count > 0)
        {
            int gw = gridRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)gw / gridRet.Count:P1}  ({gw}W / {gridRet.Count - gw}L)");
            Console.WriteLine($"  Avg return:   {gridRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(gridRet, gridTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(gridRet, gridTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(gridRet):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(gridRet):F2}");
            var gridPort = Simulator.SimulatePortfolio(gridTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine();
            Console.WriteLine($"  ── Portfolio sim (€100 start · half-Kelly · 5% max) ──");
            Console.WriteLine($"    End balance:   €{gridPort.EndBalance:F2}");
            Console.WriteLine($"    Return:        {(gridPort.EndBalance - gridPort.StartBalance) / gridPort.StartBalance * 100:+0.0;-0.0}%");
            Console.WriteLine($"    Avg position:  €{gridPort.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {gridPort.MaxDrawdownPct:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine($"  Per-coin (sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in gridCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        if (flRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  FADELONG SUMMARY  (val 20%, {flCoinStats.Count} coins, {flRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int fw = flRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)fw / flRet.Count:P1}  ({fw}W / {flRet.Count - fw}L)");
            Console.WriteLine($"  Avg return:   {flRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(flRet, flTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(flRet, flTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(flRet):F2}");
            var flPort = Simulator.SimulatePortfolio(flTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{flPort.EndBalance:F2}  ({(flPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={flPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in flCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (dlRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  DIPLONG SUMMARY  (val 20%, {dlCoinStats.Count} coins, {dlRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int dw = dlRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)dw / dlRet.Count:P1}  ({dw}W / {dlRet.Count - dw}L)");
            Console.WriteLine($"  Avg return:   {dlRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(dlRet, dlTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(dlRet, dlTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(dlRet):F2}");
            var dlPort = Simulator.SimulatePortfolio(dlTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{dlPort.EndBalance:F2}  ({(dlPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={dlPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in dlCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (slRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  SWINGLONG SUMMARY  (val 20%, {slCoinStats.Count} coins, {slRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int sw2 = slRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)sw2 / slRet.Count:P1}  ({sw2}W / {slRet.Count - sw2}L)");
            Console.WriteLine($"  Avg return:   {slRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(slRet, slTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(slRet, slTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(slRet):F2}");
            var slPort = Simulator.SimulatePortfolio(slTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{slPort.EndBalance:F2}  ({(slPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={slPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in slCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        int totalWins = allRet.Count(r => r > 0);
        int totalVCC  = new[] { swingTotalVCC, gridTotalVCC, flTotalVCC, dlTotalVCC, slTotalVCC }.Max();

        static TimeSpan StrategyHold(string strat, FadeShortGenotype swG, GridGenotype grG, FadeLongGenotype? flG, DipLongGenotype? dlG, SwingLongGenotype? slG) => strat switch
        {
            "swing"      => TimeSpan.FromHours(swG.MaxHoldCandles),
            "fadelong"   => TimeSpan.FromHours(flG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "diplong"    => TimeSpan.FromHours(dlG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "swing_long" => TimeSpan.FromHours(slG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            _            => TimeSpan.FromHours(grG.MaxHoldCandles),
        };

        // Per-strategy concurrent cap — prevents catastrophic correlation clustering
        if (allTrades.Count > 0)
        {
            var capInput = allTrades.Select(t => new PortfolioReplay.Trade(
                t.Strategy,
                t.Time,
                t.Strategy switch {
                    "swing"      => TimeSpan.FromHours(48),
                    "swing_long" => TimeSpan.FromHours(48),
                    "diplong"    => TimeSpan.FromHours(48),
                    "fadelong"   => TimeSpan.FromHours(72),
                    "grid"       => TimeSpan.FromHours(72),
                    _            => TimeSpan.FromHours(48),
                },
                t.Return,
                t.Conf)).ToList();
            var capFiltered = PortfolioReplay.FilterByConcurrentCap(capInput);
            int skipped = allTrades.Count - capFiltered.Count;
            if (skipped > 0)
                Console.WriteLine($"  Concurrent cap removed {skipped} trades");
            allTrades = capFiltered.Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
        }

        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, StrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG)))
            .ToList();

        var port5cap   = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var portKelly  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct);

        string Pct(List<double> r) => r.Count > 0 ? $"WR={(double)r.Count(x => x > 0)/r.Count:P0}  Avg={r.Average():+0.00}%" : "no trades";
        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  COMBINED SUMMARY  (all strategies, {allRet.Count} trades total)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  FadeShort: {swingRet.Count,4} trades  PF={Simulator.ProfitFactor(swingRet):F2}  {Pct(swingRet)}");
        Console.WriteLine($"  Grid:      {gridRet.Count,4} trades  PF={Simulator.ProfitFactor(gridRet):F2}  {Pct(gridRet)}");
        if (flRet.Count > 0)
            Console.WriteLine($"  FadeLong:  {flRet.Count,4} trades  PF={Simulator.ProfitFactor(flRet):F2}  {Pct(flRet)}");
        if (dlRet.Count > 0)
            Console.WriteLine($"  DipLong:   {dlRet.Count,4} trades  PF={Simulator.ProfitFactor(dlRet):F2}  {Pct(dlRet)}");
        if (slRet.Count > 0)
            Console.WriteLine($"  SwingLong: {slRet.Count,4} trades  PF={Simulator.ProfitFactor(slRet):F2}  {Pct(slRet)}");
        Console.WriteLine($"  Total:     {allRet.Count,4} trades  PF={Simulator.ProfitFactor(allRet):F2}  {Pct(allRet)}");
        Console.WriteLine();
        Console.WriteLine($"  Sharpe (combined):  {Simulator.SharpeRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Sortino (combined): {Simulator.SortinoRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Calmar (combined):  {Simulator.CalmarRatio(allRet):F2}");

        void PrintCombinedPort(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"\n  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
            if (p.TradesToTenPct > 0) Console.WriteLine($"    Trades to +10%:{p.TradesToTenPct}");
        }

        PrintCombinedPort($"5% per position · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", port5cap);
        PrintCombinedPort($"half-Kelly · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", portKelly);

        if (allTrades.Count >= 2)
        {
            double valDays   = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
            double annFactor = valDays > 0 ? 365.0 / valDays : 1.0;
            void AnnLine(string tag, Simulator.PortfolioResult p)
            {
                double r   = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
                double ann = (Math.Pow(1 + r / 100.0, annFactor) - 1) * 100;
                Console.WriteLine($"  {tag}: {valDays:F0}d  ann {ann:+0.0;-0.0}%  DD {p.MaxDrawdownPct:F1}%");
            }
            Console.WriteLine();
            AnnLine("5% cap ", port5cap);
            AnnLine("Kelly  ", portKelly);
        }

        {
            var byMonth = allTrades
                .GroupBy(t => new DateTime(t.Time.Year, t.Time.Month, 1))
                .OrderBy(g => g.Key)
                .ToList();

            bool hasGrid     = gridRet.Count > 0;
            bool hasDipLong  = dlRet.Count  > 0;
            bool hasFadeLong = flRet.Count  > 0;

            Dictionary<DateTime, (string Label, int HVPct)> monthRegime = new();
            if (btcRegimeSeries != null)
            {
                foreach (var grp2 in btcRegimeSeries.GroupBy(b => new DateTime(b.Time.Year, b.Time.Month, 1)))
                {
                    var counts = grp2.GroupBy(b => b.Regime)
                                     .ToDictionary(g => g.Key, g => g.Count());
                    int total = grp2.Count();
                    var dom   = counts.OrderByDescending(kv => kv.Value).First().Key;
                    int hvPct = counts.TryGetValue(MarketRegime.HighVol, out int hv) ? hv * 100 / total : 0;
                    monthRegime[grp2.Key] = (dom.ToString()[..4], hvPct);
                }
            }

            Console.WriteLine($"\n── Monthly trade activity (val window, router-gated) ────────────────────────");
            Console.WriteLine($"  {"Month",-9}  {"Regime",7}  {"Swing",5}  {"Grid",5}  {"DipLong",8}  {"FadeLong",9}  {"Total",5}");
            Console.WriteLine($"  {new string('-', 60)}");

            int gridGapMonths   = 0;
            int totalSilentMonths = 0;
            foreach (var grp in byMonth)
            {
                int sw = grp.Count(t => t.Strategy == "swing");
                int gr = grp.Count(t => t.Strategy == "grid");
                int dl = grp.Count(t => t.Strategy == "diplong");
                int fl = grp.Count(t => t.Strategy == "fadelong");

                string regLabel = monthRegime.TryGetValue(grp.Key, out var rm)
                    ? (rm.HVPct >= 20 ? $"{rm.Label}+HV" : rm.Label) : "?";

                string flag = "";
                if (hasGrid && gr == 0) { flag += " ← Grid dark"; gridGapMonths++; }
                if (grp.Count() <= 5)   { flag += " ← sparse"; totalSilentMonths++; }

                Console.WriteLine($"  {grp.Key:yyyy-MM}  {regLabel,7}  {sw,5}  {gr,5}  {dl,8}  {fl,9}  {grp.Count(),5}{flag}");
            }

            if (hasGrid && gridGapMonths > 0)
                Console.WriteLine($"\n  ⚠ Grid dark in {gridGapMonths}/{byMonth.Count} months — router GridMaxConf={routerG?.GridMaxConf:F2}");
            else if (hasGrid)
                Console.WriteLine($"\n  ✓ Grid active every month — router gate is not causing inactivity");

            if (totalSilentMonths > 0)
                Console.WriteLine($"  ⚠ {totalSilentMonths} month(s) sparse (≤5 trades) — potential coverage gap");

            if (btcRegimeSeries != null)
            {
                var regimeCounts = btcRegimeSeries.GroupBy(b => b.Regime)
                    .Select(g => (Regime: g.Key, Pct: g.Count() * 100.0 / btcRegimeSeries.Length))
                    .OrderByDescending(x => x.Pct).ToList();
                Console.WriteLine($"\n── BTC regime distribution (full history, {btcRegimeSeries.Length} h1 bars) ──────────────────");
                foreach (var (regime, pct) in regimeCounts)
                {
                    string gap = regime switch
                    {
                        MarketRegime.HighVol => "FadeShort only (half-size) — no long strategy active",
                        MarketRegime.Bear    => $"FadeLong needs ≥{routerG?.BearMinBars ?? 276}h sustained — {pct:F0}% of history uncovered by longs",
                        _                   => ""
                    };
                    Console.WriteLine($"  {regime,-10} {pct,5:F1}%{(gap.Length > 0 ? $"  ← {gap}" : "")}");
                }
            }
        }

        {
            var nrExposure = allTradesNoRouter
                .Select(t => (t.Time, t.Return, t.Conf, StrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG)))
                .ToList();

            var nrPort5cap  = Simulator.SimulatePortfolioExposureCapped(nrExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var nrPortKelly = Simulator.SimulatePortfolioExposureCapped(nrExposure, Config.MaxTotalExposurePct);

            int nrGrid      = allTradesNoRouter.Count(t => t.Strategy == "grid");
            int nrDipLong   = allTradesNoRouter.Count(t => t.Strategy == "diplong");
            int nrFadeLong  = allTradesNoRouter.Count(t => t.Strategy == "fadelong");
            int nrSwingLong = allTradesNoRouter.Count(t => t.Strategy == "swing_long");

            double r5R   = (port5cap.EndBalance   - 100) / 100 * 100;
            double r5NR  = (nrPort5cap.EndBalance  - 100) / 100 * 100;
            double rKR   = (portKelly.EndBalance   - 100) / 100 * 100;
            double rKNR  = (nrPortKelly.EndBalance - 100) / 100 * 100;

            Console.WriteLine($"\n── Router impact: with vs without router (val window) ───────────────────────");
            Console.WriteLine($"  {"Strategy",-12}  {"With router",11}  {"No router",9}  {"Δ extra",7}");
            Console.WriteLine($"  {new string('-', 46)}");
            Console.WriteLine($"  {"FadeShort",-12}  {swingRet.Count,11}  {swingRet.Count,9}  {"—",7}");
            Console.WriteLine($"  {"Grid",-12}  {gridRet.Count,11}  {nrGrid,9}  {nrGrid - gridRet.Count,+7}");
            Console.WriteLine($"  {"DipLong",-12}  {dlRet.Count,11}  {nrDipLong,9}  {nrDipLong - dlRet.Count,+7}");
            Console.WriteLine($"  {"FadeLong",-12}  {flRet.Count,11}  {nrFadeLong,9}  {nrFadeLong - flRet.Count,+7}");
            if (slRet.Count > 0 || nrSwingLong > 0)
                Console.WriteLine($"  {"SwingLong",-12}  {slRet.Count,11}  {nrSwingLong,9}  {nrSwingLong - slRet.Count,+7}");
            Console.WriteLine($"  {"Total",-12}  {allTrades.Count,11}  {allTradesNoRouter.Count,9}");
            Console.WriteLine();
            Console.WriteLine($"  {"Scenario",-30}  {"Return",8}  {"DD",6}");
            Console.WriteLine($"  {new string('-', 48)}");
            Console.WriteLine($"  {"5% cap · with router",-30}  {r5R,+7:F1}%  {port5cap.MaxDrawdownPct,5:F1}%");
            Console.WriteLine($"  {"5% cap · no router",-30}  {r5NR,+7:F1}%  {nrPort5cap.MaxDrawdownPct,5:F1}%");
            Console.WriteLine($"  {"Kelly · with router",-30}  {rKR,+7:F1}%  {portKelly.MaxDrawdownPct,5:F1}%");
            Console.WriteLine($"  {"Kelly · no router",-30}  {rKNR,+7:F1}%  {nrPortKelly.MaxDrawdownPct,5:F1}%");

            string edgeSign5 = r5R >= r5NR ? "+" : "";
            string edgeSignK = rKR >= rKNR ? "+" : "";
            Console.WriteLine($"  Router edge: 5% cap {edgeSign5}{r5R - r5NR:F1}pp  /  Kelly {edgeSignK}{rKR - rKNR:F1}pp");
        }

        {
            var lastDate = allTradesForExposure[^1].Time;
            var cutDate  = lastDate - TimeSpan.FromDays(90);
            var slice90  = allTradesForExposure.Where(t => t.Time >= cutDate).ToList();

            if (slice90.Count >= 10)
            {
                double spanDays   = (lastDate - slice90[0].Time).TotalDays;
                var    p5cap      = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var    pKelly     = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct);

                double r5   = (p5cap.EndBalance  - 100.0) / 100.0;
                double rK   = (pKelly.EndBalance  - 100.0) / 100.0;
                double ann5 = spanDays > 0 ? (Math.Pow(1 + r5, 365.0 / spanDays) - 1) * 100 : 0;
                double annK = spanDays > 0 ? (Math.Pow(1 + rK, 365.0 / spanDays) - 1) * 100 : 0;

                Console.WriteLine($"\n── Forward projection: last 90d run-rate → 1yr ─────────────────────────────");
                Console.WriteLine($"  Slice: {slice90.Count} trades  period: {slice90[0].Time:yyyy-MM-dd} → {lastDate:yyyy-MM-dd}  ({spanDays:F0}d)");
                Console.WriteLine($"  5% cap:  {r5 * 100:+0.0;-0.0}% in {spanDays:F0}d  →  projected {ann5:+0.0;-0.0}%/yr  (DD in slice: {p5cap.MaxDrawdownPct:F1}%)");
                Console.WriteLine($"  Kelly:   {rK * 100:+0.0;-0.0}% in {spanDays:F0}d  →  projected {annK:+0.0;-0.0}%/yr  (DD in slice: {pKelly.MaxDrawdownPct:F1}%)");
                Console.WriteLine($"  Full-window ann for reference:  5% cap {(allTrades.Count >= 2 ? $"{(Math.Pow(1 + (port5cap.EndBalance - 100) / 100, 365.0 / (allTrades[^1].Time - allTrades[0].Time).TotalDays) - 1) * 100:+0.0}%" : "n/a")}  Kelly {(allTrades.Count >= 2 ? $"{(Math.Pow(1 + (portKelly.EndBalance - 100) / 100, 365.0 / (allTrades[^1].Time - allTrades[0].Time).TotalDays) - 1) * 100:+0.0}%" : "n/a")}");
                Console.WriteLine($"  ⚠ Assumes stable regime — recent bull market may not persist.");
            }
        }

        Console.WriteLine($"\n── Full 3yr history DD (train + val, router-gated) ──────────────────────────");
        var fullHistTrades = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold)>();

        foreach (var (_, h1f, m15f, conf) in swingFullCoins)
            foreach (var (t, ret, _) in FadeShortSimulator.GetFadeShortReturns(swingG, h1f, m15f))
                fullHistTrades.Add((t, ret, conf, TimeSpan.FromHours(swingG.MaxHoldCandles)));

        foreach (var (_, h1f, conf) in gridFullCoins)
            foreach (var (t, ret, _) in GridSimulator.GetGridReturns(gridG, h1f))
            {
                if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                fullHistTrades.Add((t, ret, conf, TimeSpan.FromHours(gridG.MaxHoldCandles)));
            }

        if (flG != null)
            foreach (var (_, h1f, m15f, conf) in flFullCoins)
                foreach (var t in FadeLongSimulator.GetFadeLongReturns(flG, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(flG.MaxHoldCandles)));
                }

        if (dlG != null)
            foreach (var (_, h1f, m15f, conf) in dlFullCoins)
                foreach (var t in DipLongSimulator.GetDipLongReturns(dlG, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(dlG.MaxHoldCandles)));
                }

        if (slG != null)
            foreach (var (_, h1f, m15f, conf) in slFullCoins)
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(slG, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(slG.MaxHoldCandles)));
                }

        fullHistTrades.Sort((a, b) => a.Time.CompareTo(b.Time));

        if (fullHistTrades.Count >= 10)
        {
            var fhPort5cap = Simulator.SimulatePortfolioExposureCapped(fullHistTrades, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var fhPortKel  = Simulator.SimulatePortfolioExposureCapped(fullHistTrades, Config.MaxTotalExposurePct);
            double fhDays  = (fullHistTrades[^1].Time - fullHistTrades[0].Time).TotalDays;
            double fhAnnF  = fhDays > 0 ? 365.0 / fhDays : 1.0;
            double fhRet5  = (fhPort5cap.EndBalance - 100) / 100 * 100;
            double fhRetE  = (fhPortKel.EndBalance  - 100) / 100 * 100;
            double fhAnn5  = (Math.Pow(1 + fhRet5  / 100.0, fhAnnF) - 1) * 100;
            double fhAnnE  = (Math.Pow(1 + fhRetE  / 100.0, fhAnnF) - 1) * 100;
            Console.WriteLine($"  Trades: {fullHistTrades.Count}  Period: {fhDays:F0} days");
            Console.WriteLine($"  5% cap · {Config.MaxTotalExposurePct:P0} total:  End {fhRet5:+0.0;-0.0}%  ann {fhAnn5:+0.0;-0.0}%  DD {fhPort5cap.MaxDrawdownPct:F1}%");
            Console.WriteLine($"  Kelly  · {Config.MaxTotalExposurePct:P0} total:  End {fhRetE:+0.0;-0.0}%  ann {fhAnnE:+0.0;-0.0}%  DD {fhPortKel.MaxDrawdownPct:F1}%");
        }
        else Console.WriteLine("  Not enough trades for full-history simulation.");
    }
}
