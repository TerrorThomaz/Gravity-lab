using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class OosBacktest
{
    public static async Task RunOosBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | OOS BACKTEST ({Config.OosCoins.Length} never-seen coins · full history · all strategies, router-gated) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype — run 'train' first.");    return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing grid genotype — run 'gridtrain' first.");     return; }

        var swingG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG  = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();

        FadeLongGenotype?     flG     = File.Exists(Config.FadeLongGenoFile)  ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()   : null;
        DipLongGenotype?      dlG     = File.Exists(Config.DipLongGenoFile)   ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()     : null;
        SwingLongGenotype?    slG     = File.Exists(Config.SwingLongGenoFile) ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype() : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"FadeShort: {swingG}");
        Console.WriteLine($"Grid:      {gridG}");
        if (flG     != null) Console.WriteLine($"FadeLong:  {flG}");
        else                 Console.WriteLine("FadeLong:  not found — skipping");
        if (dlG     != null) Console.WriteLine($"DipLong:   {dlG}");
        else                 Console.WriteLine("DipLong:   not found — skipping");
        if (slG     != null) Console.WriteLine($"SwingLong: {slG}");
        else                 Console.WriteLine("SwingLong: not found — skipping");
        if (routerG != null) Console.WriteLine($"Router:    {routerG}");
        else                 Console.WriteLine("Router:    not found — running ungated");
        Console.WriteLine();

        var allSyms = Config.OosCoins.Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        Console.WriteLine($"  Fetching {Config.OosCoins.Length} OOS coins + BTC/ETH for router (15m → 1h, ~3yr)...");
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
        var fetched = await Task.WhenAll(fetchTasks);
        Console.WriteLine($"  Done.\n");

        RegimeRouterSession? session = null;
        if (routerG != null)
        {
            var btcEntry = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
            if (btcEntry.h1 != null && btcEntry.h1.Length >= 200)
            {
                var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
                var ethEntry  = fetched.FirstOrDefault(f => f.sym == "ETHUSDT");
                RegimeBar[]? ethSeries = ethEntry.h1 != null && ethEntry.h1.Length >= 200
                    ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
                session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
                Console.WriteLine($"  Router session: BTC {btcSeries.Length} bars  ETH {(ethSeries != null ? ethSeries.Length.ToString() : "none")} bars\n");
            }
            else Console.WriteLine("  ⚠ BTC data insufficient for regime session — router gate disabled\n");
        }

        var oosFetched = fetched.Where(f => Config.OosCoins.Contains(f.sym)).ToArray();

        const double oosMinVol = 0.05;

        var swingTrades = new List<(DateTime Time, double Return, double Conf)>();
        var gridTrades  = new List<(DateTime Time, double Return, double Conf)>();
        var flTrades    = new List<(DateTime Time, double Return, double Conf)>();
        var dlTrades    = new List<(DateTime Time, double Return, double Conf)>();
        var slTrades    = new List<(DateTime Time, double Return, double Conf)>();
        var allTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy)>();

        var swingCoinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var gridCoinStats  = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var flCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var dlCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var slCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        int totalVCC = 0;

        // ── SWING ─────────────────────────────────────────────────────────────────
        Console.WriteLine($"══ SWING (full OOS history, no seed screen) ═════════════════════════════════");
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, m15, h1) in oosFetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (only {h1.Length} h1 bars)"); continue; }

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < oosMinVol) { Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h)"); continue; }

            var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15);
            var vRet   = trades.Select(t => t.Return).ToList();
            if (vRet.Count < 5) { Console.WriteLine($"  {sym,-16}  skip ({vRet.Count} trades)"); continue; }

            int    vCC  = h1.Length * 12;
            totalVCC    = Math.Max(totalVCC, vCC);
            double conf = Simulator.ComputeConfidence(vRet);
            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
            double avg  = vRet.Average();

            Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            swingCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            foreach (var (t, ret, _) in trades)
            {
                swingTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "swing"));
            }
        }

        // ── GRID ──────────────────────────────────────────────────────────────────
        Console.WriteLine($"\n══ GRID (full OOS history, router-gated) ════════════════════════════════════");
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, _, h1) in oosFetched)
        {
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < oosMinVol) continue;

            var raw   = GridSimulator.GetGridReturns(gridG, h1);
            var gated = session != null
                ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList()
                : raw;
            var vRet  = gated.Select(t => t.Return).ToList();
            if (vRet.Count < 5) { Console.WriteLine($"  {sym,-16}  skip ({vRet.Count} trades after gate)"); continue; }

            int    vCC  = h1.Length * 12;
            double conf = Simulator.ComputeConfidence(vRet);
            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
            double avg  = vRet.Average();

            Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            gridCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            foreach (var t in gated)
            {
                gridTrades.Add((t.Time, t.Return, conf));
                allTrades.Add((t.Time, t.Return, conf, "grid"));
            }
        }

        // ── FADELONG ──────────────────────────────────────────────────────────────
        if (flG != null)
        {
            Console.WriteLine($"\n══ FADELONG (full OOS history, router-gated) ════════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0) { Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)"); continue; }

                int    vCC  = h1.Length * 12;
                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                flCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in gated)
                {
                    flTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "fadelong"));
                }
            }
        }

        // ── DIPLONG ───────────────────────────────────────────────────────────────
        if (dlG != null)
        {
            Console.WriteLine($"\n══ DIPLONG (full OOS history, router-gated) ═════════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0) { Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)"); continue; }

                int    vCC  = h1.Length * 12;
                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                dlCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in gated)
                {
                    dlTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "diplong"));
                }
            }
        }

        // ── SWINGLONG ─────────────────────────────────────────────────────────────────
        if (slG != null)
        {
            Console.WriteLine($"\n══ SWINGLONG (full OOS history, router-gated) ═══════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()  // SwingLong shares bull-regime gate with DipLong
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0) { Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)"); continue; }

                int    vCC  = h1.Length * 12;
                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                slCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in gated)
                {
                    slTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "swing_long"));
                }
            }
        }

        if (allTrades.Count == 0) { Console.WriteLine("\nNo OOS trades generated."); return; }

        allTrades.Sort((a, b)   => a.Time.CompareTo(b.Time));
        swingTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
        gridTrades.Sort((a, b)  => a.Time.CompareTo(b.Time));
        flTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));
        dlTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));
        slTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));

        // Per-strategy concurrent cap
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
            var filtered = PortfolioReplay.FilterByConcurrentCap(capInput);
            Console.WriteLine($"  Concurrent cap: {allTrades.Count} → {filtered.Count} trades ({allTrades.Count - filtered.Count} removed)");
            allTrades = filtered.Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
        }

        var swingRet = swingTrades.Select(t => t.Return).ToList();
        var gridRet  = gridTrades.Select(t => t.Return).ToList();
        var flRet    = flTrades.Select(t => t.Return).ToList();
        var dlRet    = dlTrades.Select(t => t.Return).ToList();
        var slRet    = slTrades.Select(t => t.Return).ToList();
        var allRet   = allTrades.Select(t => t.Return).ToList();

        string Pct(List<double> r) => r.Count > 0 ? $"WR={(double)r.Count(x => x > 0)/r.Count:P0}  Avg={r.Average():+0.00}%" : "no trades";

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  OOS COMBINED SUMMARY  (full history · {allRet.Count} trades · {Config.OosCoins.Length} coins)");
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
        Console.WriteLine($"  Sharpe:  {Simulator.SharpeRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Sortino: {Simulator.SortinoRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Calmar:  {Simulator.CalmarRatio(allRet):F2}");

        static TimeSpan OosStrategyHold(string strat, FadeShortGenotype swG, GridGenotype grG, FadeLongGenotype? flG, DipLongGenotype? dlG, SwingLongGenotype? slG) => strat switch
        {
            "swing"      => TimeSpan.FromHours(swG.MaxHoldCandles),
            "swing_long" => TimeSpan.FromHours(slG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "fadelong"   => TimeSpan.FromHours(flG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "diplong"    => TimeSpan.FromHours(dlG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            _            => TimeSpan.FromHours(grG.MaxHoldCandles),
        };

        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, OosStrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG)))
            .ToList();

        var port5cap  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var portKelly = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct);

        void PrintPort(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"\n  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
        }

        PrintPort($"5% per position · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", port5cap);
        PrintPort($"half-Kelly · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", portKelly);

        if (allTrades.Count >= 2)
        {
            double days      = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
            double annFactor = days > 0 ? 365.0 / days : 1.0;
            void AnnLine(string tag, Simulator.PortfolioResult p)
            {
                double r   = (p.EndBalance - 100) / 100 * 100;
                double ann = (Math.Pow(1 + r / 100.0, annFactor) - 1) * 100;
                Console.WriteLine($"  {tag}: {days:F0}d  ann {ann:+0.0;-0.0}%  DD {p.MaxDrawdownPct:F1}%");
            }
            Console.WriteLine();
            AnnLine("5% cap ", port5cap);
            AnnLine("Kelly  ", portKelly);
        }

        Console.WriteLine($"\n  Per-coin breakdown (FadeShort · sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in swingCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        if (gridCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (Grid · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in gridCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (flCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (FadeLong · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in flCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (dlCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (DipLong · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in dlCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (slCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (SwingLong · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in slCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        // Merge OOS section into backtest_results.json
        static double OosComputeMaxDD(List<double> r)
        {
            if (r.Count == 0) return 0;
            double eq = 100.0, peak = 100.0, maxDd = 0;
            foreach (var ret in r) { eq += ret; if (eq > peak) peak = eq; double dd = (peak - eq) / peak * 100; if (dd > maxDd) maxDd = dd; }
            return maxDd;
        }

        static object OosStratStats(List<double> r, int vcc) => r.Count == 0
            ? new { trades = 0, winRate = 0.0, sharpe = 0.0, pf = 0.0, maxDD = 0.0, ret = 0.0 }
            : new
            {
                trades  = r.Count,
                winRate = Math.Round((double)r.Count(x => x > 0) / r.Count * 100, 2),
                sharpe  = Math.Round(Simulator.SharpeRatio(r, vcc), 4),
                pf      = Math.Round(Simulator.ProfitFactor(r), 4),
                maxDD   = Math.Round(-OosComputeMaxDD(r), 2),
                ret     = Math.Round(r.Sum(), 2),
            };

        var oosSection = new Dictionary<string, object>
        {
            ["FadeShort"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(swingRet, totalVCC) },
            ["Grid"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(gridRet, totalVCC) },
            ["FadeLong"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(flRet, totalVCC) },
            ["DipLong"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(dlRet, totalVCC) },
            ["SwingLong"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(slRet, totalVCC) },
        };

        string backtestPath = "backtest_results.json";
        var existing = File.Exists(backtestPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(backtestPath))
              ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();
        existing["oos"]       = oosSection;
        existing["timestamp"] = DateTime.UtcNow.ToString("O");
        File.WriteAllText(backtestPath, System.Text.Json.JsonSerializer.Serialize(existing,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("backtest_results.json updated with OOS results.");
    }

    public static async Task RunAllCoinsBacktest(BybitRestClient client)
    {
        int totalCoins = Config.BacktestCoins.Length + Config.OosCoins.Length;
        Console.WriteLine($"=== Gravity-gen2 | ALL-COINS PORTFOLIO SIM ({totalCoins} coins · BacktestCoins val 20% + OOS full history) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing grid genotype.");      return; }

        var swingG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG  = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        FadeLongGenotype?     flG     = File.Exists(Config.FadeLongGenoFile) ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()   : null;
        DipLongGenotype?      dlG     = File.Exists(Config.DipLongGenoFile)  ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()     : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)   ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"FadeShort: {swingG}\nGrid:      {gridG}");
        if (flG != null)     Console.WriteLine($"FadeLong:  {flG}");
        if (dlG != null)     Console.WriteLine($"DipLong:   {dlG}");
        if (routerG != null) Console.WriteLine($"Router:    {routerG}");
        Console.WriteLine();

        var allSyms = Config.BacktestCoins.Concat(Config.OosCoins).Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        Console.WriteLine($"  Fetching {allSyms.Length} symbols (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchedAll = await Task.WhenAll(allSyms.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, m15: m15.ToArray(), h1);
            }
            finally { sem.Release(); }
        }));
        Console.WriteLine($"  Done.\n");

        RegimeRouterSession? session = null;
        if (routerG != null)
        {
            var btcEntry = fetchedAll.FirstOrDefault(f => f.sym == "BTCUSDT");
            if (btcEntry.h1 != null && btcEntry.h1.Length >= 200)
            {
                var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
                var ethEntry  = fetchedAll.FirstOrDefault(f => f.sym == "ETHUSDT");
                RegimeBar[]? ethSeries = ethEntry.h1 != null && ethEntry.h1.Length >= 200
                    ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
                session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
                Console.WriteLine($"  Router session: BTC {btcSeries.Length} bars  ETH {(ethSeries != null ? ethSeries.Length.ToString() : "none")} bars\n");
            }
        }

        var allTrades = new List<(DateTime Time, double Return, double Conf, string Strategy)>();

        static TimeSpan AcHold(string s, FadeShortGenotype sw, GridGenotype gr, FadeLongGenotype? fl, DipLongGenotype? dl) => s switch
        {
            "swing"    => TimeSpan.FromHours(sw.MaxHoldCandles),
            "fadelong" => TimeSpan.FromHours(fl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "diplong"  => TimeSpan.FromHours(dl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            _          => TimeSpan.FromHours(gr.MaxHoldCandles),
        };

        // ── BacktestCoins — val 20%, seed-screened ─────────────────────────────
        var btFetched = fetchedAll.Where(f => Config.BacktestCoins.Contains(f.sym)).ToArray();
        int btSwing = 0, btGrid = 0, btFL = 0, btDL = 0;
        int btSwingT = 0, btGridT = 0, btFLT = 0, btDLT = 0;

        Console.WriteLine("── BacktestCoins (val 20%, seed-screened) ──────────────────────────────────");
        foreach (var (sym, m15, h1) in btFetched)
        {
            if (h1.Length < 300) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            // FadeShort
            {
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var tRet = FadeShortSimulator.GetFadeShortReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
                if (tRet.Count >= 5 && tRet.Average() > 0 && Simulator.ProfitFactor(tRet) >= 1.2 && Simulator.SortinoRatio(tRet, screenH1.Length * 12) >= 0.3)
                {
                    double conf  = Simulator.ComputeConfidence(tRet);
                    var    vRet  = FadeShortSimulator.GetFadeShortReturns(swingG, h1Val, m15Val);
                    btSwing++; btSwingT += vRet.Count;
                    foreach (var (t, ret, _) in vRet) allTrades.Add((t, ret, conf, "swing"));
                }
            }

            // Grid
            {
                var tRet = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
                if (tRet.Count >= 5 && tRet.Average() > 0 && Simulator.ProfitFactor(tRet) >= 1.2 && Simulator.SortinoRatio(tRet, h1Train.Length) >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(tRet);
                    var    vRet = GridSimulator.GetGridReturns(gridG, h1Val);
                    btGrid++; btGridT += vRet.Count(t => session == null || session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time));
                    foreach (var (t, ret, _) in vRet)
                    {
                        if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                        allTrades.Add((t, ret, conf, "grid"));
                    }
                }
            }

            // FadeLong
            if (flG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    btFL++; btFLT += gated.Count;
                    foreach (var t in gated) allTrades.Add((t.Time, t.Return, conf, "fadelong"));
                }
            }

            // DipLong
            if (dlG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1Val, m15Val);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    btDL++; btDLT += gated.Count;
                    foreach (var t in gated) allTrades.Add((t.Time, t.Return, conf, "diplong"));
                }
            }
        }
        Console.WriteLine($"  FadeShort: {btSwing,3} coins → {btSwingT,4} trades");
        Console.WriteLine($"  Grid:      {btGrid,3} coins → {btGridT,4} trades");
        if (flG != null) Console.WriteLine($"  FadeLong:  {btFL,3} coins → {btFLT,4} trades");
        if (dlG != null) Console.WriteLine($"  DipLong:   {btDL,3} coins → {btDLT,4} trades");

        // ── OosCoins — full history, no seed screen ───────────────────────────
        const double oosMinVol2 = 0.05;
        var oosFetched2 = fetchedAll.Where(f => Config.OosCoins.Contains(f.sym)).ToArray();
        int oSwing = 0, oGrid = 0, oFL = 0, oDL = 0;
        int oSwingT = 0, oGridT = 0, oFLT = 0, oDLT = 0;

        Console.WriteLine($"\n── OosCoins (full history, vol≥${oosMinVol2:F2}M, no screen) ─────────────────────");
        foreach (var (sym, m15, h1) in oosFetched2)
        {
            if (h1.Length < 300) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < oosMinVol2) continue;

            // FadeShort
            {
                var trades = FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15);
                if (trades.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(trades.Select(t => t.Return).ToList());
                    oSwing++; oSwingT += trades.Count;
                    foreach (var (t, ret, _) in trades) allTrades.Add((t, ret, conf, "swing"));
                }
            }

            // Grid
            {
                var raw   = GridSimulator.GetGridReturns(gridG, h1);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
                if (gated.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    oGrid++; oGridT += gated.Count;
                    foreach (var t in gated) allTrades.Add((t.Time, t.Return, conf, "grid"));
                }
            }

            // FadeLong
            if (flG != null && m15.Length >= 1200)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    oFL++; oFLT += gated.Count;
                    foreach (var t in gated) allTrades.Add((t.Time, t.Return, conf, "fadelong"));
                }
            }

            // DipLong
            if (dlG != null && m15.Length >= 1200)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, h1, m15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                if (gated.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(gated.Select(t => t.Return).ToList());
                    oDL++; oDLT += gated.Count;
                    foreach (var t in gated) allTrades.Add((t.Time, t.Return, conf, "diplong"));
                }
            }
        }
        Console.WriteLine($"  FadeShort: {oSwing,3} coins → {oSwingT,4} trades");
        Console.WriteLine($"  Grid:      {oGrid,3} coins → {oGridT,4} trades");
        if (flG != null) Console.WriteLine($"  FadeLong:  {oFL,3} coins → {oFLT,4} trades");
        if (dlG != null) Console.WriteLine($"  DipLong:   {oDL,3} coins → {oDLT,4} trades");

        if (allTrades.Count == 0) { Console.WriteLine("\nNo trades generated."); return; }
        allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Per-strategy concurrent cap
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
            var filtered = PortfolioReplay.FilterByConcurrentCap(capInput);
            Console.WriteLine($"  Concurrent cap: {allTrades.Count} → {filtered.Count} trades ({allTrades.Count - filtered.Count} removed)");
            allTrades = filtered.Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
        }

        var allRet   = allTrades.Select(t => t.Return).ToList();
        var swingRet = allTrades.Where(t => t.Strategy == "swing").Select(t => t.Return).ToList();
        var gridRet  = allTrades.Where(t => t.Strategy == "grid").Select(t => t.Return).ToList();
        var flRet    = allTrades.Where(t => t.Strategy == "fadelong").Select(t => t.Return).ToList();
        var dlRet    = allTrades.Where(t => t.Strategy == "diplong").Select(t => t.Return).ToList();
        int totalVCC = fetchedAll.Where(f => Config.BacktestCoins.Contains(f.sym) || Config.OosCoins.Contains(f.sym)).Max(f => f.h1.Length) * 12;

        string Pct(List<double> r) => r.Count > 0 ? $"WR={(double)r.Count(x => x > 0)/r.Count:P0}  Avg={r.Average():+0.00}%" : "no trades";

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  COMBINED  ({allRet.Count} trades · {btSwing+btGrid+btFL+btDL+oSwing+oGrid+oFL+oDL} coin-strategy pairs)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  FadeShort: {swingRet.Count,4} trades  PF={Simulator.ProfitFactor(swingRet):F2}  {Pct(swingRet)}");
        Console.WriteLine($"  Grid:      {gridRet.Count,4} trades  PF={Simulator.ProfitFactor(gridRet):F2}  {Pct(gridRet)}");
        if (flRet.Count > 0) Console.WriteLine($"  FadeLong:  {flRet.Count,4} trades  PF={Simulator.ProfitFactor(flRet):F2}  {Pct(flRet)}");
        if (dlRet.Count > 0) Console.WriteLine($"  DipLong:   {dlRet.Count,4} trades  PF={Simulator.ProfitFactor(dlRet):F2}  {Pct(dlRet)}");
        Console.WriteLine($"  Total:     {allRet.Count,4} trades  PF={Simulator.ProfitFactor(allRet):F2}  {Pct(allRet)}");
        Console.WriteLine($"  Sharpe: {Simulator.SharpeRatio(allRet, totalVCC):F2}  Sortino: {Simulator.SortinoRatio(allRet, totalVCC):F2}  Calmar: {Simulator.CalmarRatio(allRet):F2}");

        // ── Concurrency analysis ──────────────────────────────────────────────
        var openEvents = new List<(DateTime Time, int Delta)>();
        foreach (var t in allTrades)
        {
            var hold = AcHold(t.Strategy, swingG, gridG, flG, dlG);
            openEvents.Add((t.Time, +1));
            openEvents.Add((t.Time + hold, -1));
        }
        openEvents.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : b.Delta.CompareTo(a.Delta));
        int peakConc = 0, curConc = 0;
        long totalConcTicks = 0; DateTime prevTime = DateTime.MinValue; long concAcc = 0;
        foreach (var (evtTime, delta) in openEvents)
        {
            if (prevTime != DateTime.MinValue) concAcc += curConc * (evtTime - prevTime).Ticks;
            curConc += delta; if (curConc > peakConc) peakConc = curConc;
            prevTime = evtTime; totalConcTicks += (evtTime - openEvents[0].Time).Ticks > 0 ? 0 : 0;
        }
        double totalSpanTicks = allTrades.Count > 1
            ? (allTrades[^1].Time + AcHold(allTrades[^1].Strategy, swingG, gridG, flG, dlG) - allTrades[0].Time).Ticks
            : 1;
        double avgConc = totalSpanTicks > 0 ? concAcc / totalSpanTicks : 0;

        Console.WriteLine($"\n── Concurrency analysis (hold-duration aware) ───────────────────────────────");
        Console.WriteLine($"  Peak simultaneous open positions: {peakConc}");
        Console.WriteLine($"  Max exposure at 5%/trade cap:     {peakConc * 5}%  ({peakConc} × 5%)");
        Console.WriteLine($"  Avg concurrent open (time-weighted): {avgConc:F1}");
        Console.WriteLine($"  Avg exposure at 5%/trade cap:     {avgConc * 5:F1}%");

        // ── Portfolio simulations ──────────────────────────────────────────────
        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, AcHold(t.Strategy, swingG, gridG, flG, dlG)))
            .ToList();

        var port5cap = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var portKel  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct);

        void PrintSim(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"\n  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
            if (p.TradesToTenPct > 0) Console.WriteLine($"    Trades to +10%:{p.TradesToTenPct}");
        }

        Console.WriteLine($"\n── Portfolio simulations (€100 start · {allTrades.Count} trades · concurrent-aware) ──────");
        PrintSim($"5% per position · {Config.MaxTotalExposurePct:P0} total cap", port5cap);
        PrintSim($"half-Kelly · {Config.MaxTotalExposurePct:P0} total cap", portKel);

        if (allTrades.Count >= 2)
        {
            double days      = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
            double annFactor = days > 0 ? 365.0 / days : 1.0;
            void AnnLine(string tag, Simulator.PortfolioResult p)
            {
                double r   = (p.EndBalance - 100) / 100 * 100;
                double ann = (Math.Pow(1 + r / 100.0, annFactor) - 1) * 100;
                Console.WriteLine($"  {tag}: {days:F0}d  ann {ann:+0.0;-0.0}%  DD {p.MaxDrawdownPct:F1}%");
            }
            Console.WriteLine();
            AnnLine("5% cap ", port5cap);
            AnnLine("Kelly  ", portKel);
        }

        Console.WriteLine($"\n── Concurrency → 30% total cap explanation ──────────────────────────────────");
        Console.WriteLine($"  Peak {peakConc} simultaneous positions. Without a total cap,");
        Console.WriteLine($"  5% × {peakConc} = {peakConc * 5}% deployed at peak — far beyond safe leverage.");
        Console.WriteLine($"  The 30% total cap means each new entry gets min(sizing, remaining headroom).");
        Console.WriteLine($"  When 6 positions are open at 5% each, the 7th gets €0 until one closes.");
    }
}
