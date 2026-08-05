using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class GridCommands
{
    public static async Task RunGridTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("grid_best", variant, Config.GridGenoFile);
        Console.WriteLine("=== Gravity-gen2 | GRID TRAIN (ranging long grid, 1h candles, 26 coins) ===");
        Console.WriteLine($"Training Grid / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        var trainCoins = new[]
        {
            ("SOLUSDT",  1.0), ("ETHUSDT",  1.0), ("BNBUSDT",  1.0), ("XRPUSDT",  1.0),
            ("DOGEUSDT", 1.0), ("AVAXUSDT", 1.0), ("ADAUSDT",  1.0), ("LINKUSDT", 1.0),
            ("DOTUSDT",  1.0), ("MATICUSDT",1.0), ("ATOMUSDT", 0.9), ("NEARUSDT", 0.9),
            ("INJUSDT",  0.9), ("OPUSDT",   0.9), ("ARBUSDT",  0.9), ("UNIUSDT",  0.9),
            ("AAVEUSDT", 0.9), ("RUNEUSDT", 0.9), ("WIFUSDT",  0.8), ("1000PEPEUSDT", 0.8),
            ("APTUSDT",  0.8), ("SUIUSDT",  0.8), ("TIAUSDT",  0.8), ("SEIUSDT",  0.8),
            ("STXUSDT",  0.8), ("JUPUSDT",  0.8),
        };

        Console.WriteLine($"  Fetching {trainCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = trainCoins.Select(async ((string sym, double weight) t) =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, t.sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                Console.WriteLine($"  {t.sym}: {h1.Length} h1 candles");
                return (t.sym, t.weight, h1);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coinData = new List<GridGeneticAlgorithm.CoinData>();
        foreach (var (sym, weight, h1) in fetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            int split = (int)(h1.Length * 0.8);
            coinData.Add(new GridGeneticAlgorithm.CoinData(h1[..split], h1[split..], weight));
        }
        if (coinData.Count == 0) { Console.WriteLine("No data."); return; }

        double adxCeiling = 20.0;
        if (File.Exists(Config.FadeShortGenoFile))
        {
            var swingGeno = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
            adxCeiling = Math.Min(swingGeno.AdxThreshold - 1.0, 20.0);
            Console.WriteLine($"  Swing AdxThreshold={swingGeno.AdxThreshold:F0} → grid ceiling={adxCeiling:F0} (clean partition)");
        }

        GridGenotype? seed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
            var clamped = candidate.ClampToBounds(adxCeiling);
            if (clamped.Fitness > 0) { seed = clamped; Console.WriteLine($"  Seeding from {genoPath}: {seed}"); }
            else Console.WriteLine("  Skipping seed (fitness ≤ 0)");
        }

        Console.WriteLine($"\n  Training on {coinData.Count} coins\n");
        Console.WriteLine("─── Grid GA training ───");
        var best = new GridGeneticAlgorithm(60, 100, verbose: true, cfg: cfg).Run(coinData, seed, adxCeiling);

        Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(GridGenotypeDto.From(best, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Validation suite ───");
        try
        {
            var mcResult = MonteCarloTest.Run(
                coinData.SelectMany(cd => GridSimulator.GetGridReturns(best, cd.TrainCandles.Span).Select(t => t.Return)).ToList(),
                permutations: 1000);
            MonteCarloTest.PrintReport(mcResult, "Grid");
        }
        catch (Exception ex) { Console.WriteLine($"  MonteCarloTest skipped: {ex.Message}"); }

        try
        {
            var ewReport = ExpandingWindowValidation.RunGrid(coinData, cfg);
            ExpandingWindowValidation.PrintReport(ewReport);
        }
        catch (Exception ex) { Console.WriteLine($"  ExpandingWindowValidation skipped: {ex.Message}"); }

        Console.WriteLine("\n─── Overfit check (train 80% vs val 20%, session-level) ───");
        var tRet = coinData.SelectMany(cd => GridSimulator.GetGridSessionReturns(best, cd.TrainCandles.Span).Select(t => t.Return)).ToList();
        var vRet = coinData.SelectMany(cd => GridSimulator.GetGridSessionReturns(best, cd.ValCandles.Span).Select(t => t.Return)).ToList();
        int tCC  = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
        int vCC  = coinData.Sum(cd => cd.ValCandles.Length)   * 12;
        CandleFetcher.PrintSplitStats("Train 80%", tRet, tCC);
        CandleFetcher.PrintSplitStats("Val   20%", vRet, vCC);

        Console.WriteLine($"\nNext: dotnet run -- gridbacktest");
    }

    // Direction-mirror of RunGridTrain — same coin pool, ADX-ceiling coordination, and
    // reused GridGenotype shape, but trains against GridShortSimulator (sell above EMA).
    public static async Task RunGridShortTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("grid_short", variant, Config.GridShortGenoFile);
        Console.WriteLine("=== Gravity-gen2 | GRIDSHORTTRAIN (ranging short grid, 1h candles, 26 coins) ===");
        Console.WriteLine($"Training GridShort / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        var trainCoins = new[]
        {
            ("SOLUSDT",  1.0), ("ETHUSDT",  1.0), ("BNBUSDT",  1.0), ("XRPUSDT",  1.0),
            ("DOGEUSDT", 1.0), ("AVAXUSDT", 1.0), ("ADAUSDT",  1.0), ("LINKUSDT", 1.0),
            ("DOTUSDT",  1.0), ("MATICUSDT",1.0), ("ATOMUSDT", 0.9), ("NEARUSDT", 0.9),
            ("INJUSDT",  0.9), ("OPUSDT",   0.9), ("ARBUSDT",  0.9), ("UNIUSDT",  0.9),
            ("AAVEUSDT", 0.9), ("RUNEUSDT", 0.9), ("WIFUSDT",  0.8), ("1000PEPEUSDT", 0.8),
            ("APTUSDT",  0.8), ("SUIUSDT",  0.8), ("TIAUSDT",  0.8), ("SEIUSDT",  0.8),
            ("STXUSDT",  0.8), ("JUPUSDT",  0.8),
        };

        Console.WriteLine($"  Fetching {trainCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = trainCoins.Select(async ((string sym, double weight) t) =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, t.sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                Console.WriteLine($"  {t.sym}: {h1.Length} h1 candles");
                return (t.sym, t.weight, h1);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coinData = new List<GridShortGA.CoinData>();
        foreach (var (sym, weight, h1) in fetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            int split = (int)(h1.Length * 0.8);
            coinData.Add(new GridShortGA.CoinData(h1[..split], h1[split..], weight));
        }
        if (coinData.Count == 0) { Console.WriteLine("No data."); return; }

        double adxCeiling = 20.0;
        if (File.Exists(Config.FadeShortGenoFile))
        {
            var swingGeno = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
            adxCeiling = Math.Min(swingGeno.AdxThreshold - 1.0, 20.0);
            Console.WriteLine($"  Swing AdxThreshold={swingGeno.AdxThreshold:F0} → grid ceiling={adxCeiling:F0} (clean partition)");
        }

        GridGenotype? seed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
            var clamped = candidate.ClampToBounds(adxCeiling);
            if (clamped.Fitness > 0) { seed = clamped; Console.WriteLine($"  Seeding from {genoPath}: {seed}"); }
            else Console.WriteLine("  Skipping seed (fitness ≤ 0)");
        }

        Console.WriteLine($"\n  Training on {coinData.Count} coins\n");
        Console.WriteLine("─── GridShort GA training ───");
        var best = new GridShortGA(60, 100, verbose: true, cfg: cfg).Run(coinData, seed, adxCeiling);

        Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(GridGenotypeDto.From(best, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Overfit check (train 80% vs val 20%, session-level) ───");
        var tRet = coinData.SelectMany(cd => GridShortSimulator.GetGridShortSessionReturns(best, cd.TrainCandles.Span).Select(t => t.Return)).ToList();
        var vRet = coinData.SelectMany(cd => GridShortSimulator.GetGridShortSessionReturns(best, cd.ValCandles.Span).Select(t => t.Return)).ToList();
        int tCC  = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
        int vCC  = coinData.Sum(cd => cd.ValCandles.Length)   * 12;
        CandleFetcher.PrintSplitStats("Train 80%", tRet, tCC);
        CandleFetcher.PrintSplitStats("Val   20%", vRet, vCC);

        Console.WriteLine($"\nNext: dotnet run -- fulltest");
    }

    public static async Task RunGridBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | GRID BACKTEST ({Config.BacktestCoins.Length} coins, val 20%, 1h candles) ===\n");

        if (!File.Exists(Config.GridGenoFile))
        {
            Console.WriteLine($"No grid genotype at '{Config.GridGenoFile}'. Run 'dotnet run -- gridtrain' first.");
            return;
        }
        var g = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        Console.WriteLine($"Genotype: {g}\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                Console.WriteLine($"  {sym}: {h1.Length} h1 candles");
                return (sym, h1);
            }
            finally { sem.Release(); }
        });
        var fetchedArr = await Task.WhenAll(fetchTasks);
        Console.WriteLine();

        var allTrades = new List<(string Coin, DateTime Time, double Return, double CoinConf)>();
        var coinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        int totalVCC  = 0;

        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, h1) in fetchedArr)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

            int split   = (int)(h1.Length * 0.8);
            var h1Train = h1[..split];
            var h1Val   = h1[split..];

            var tRet  = GridSimulator.GetGridReturns(g, h1Train).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 5 ? tRet.Average() : double.NegativeInfinity;
            double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;
            if (tExp <= 0 || tPF < 1.15)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% pf={tPF:F2})");
                continue;
            }

            double coinConf = Simulator.ComputeConfidence(tRet);
            var    vTrades  = GridSimulator.GetGridReturns(g, h1Val);
            var    vRet     = vTrades.Select(t => t.Return).ToList();

            int vCC = h1Val.Length * 12;
            totalVCC += vCC;

            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
            double avg  = vRet.Count > 0 ? vRet.Average() : 0;

            foreach (var (t, ret, _) in vTrades)
                allTrades.Add((sym, t, ret, coinConf));

            Console.WriteLine($"  {sym,-16} {coinConf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            coinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, coinConf));
        }

        if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

        allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
        var allRet = allTrades.Select(t => t.Return).ToList();
        int totalWins = allRet.Count(r => r > 0);

        var tradesWithConf     = allTrades.Select(t => (t.Return, t.CoinConf)).ToList();
        var tradesForExposure  = allTrades
            .Select(t => (t.Time, t.Return, t.CoinConf, TimeSpan.FromHours(g.MaxHoldCandles)))
            .ToList();

        var port5pct     = Simulator.SimulatePortfolio(tradesWithConf, maxPositionPct: 0.05);
        var portHalfKel  = Simulator.SimulatePortfolio(tradesWithConf, maxPositionPct: 1.0);
        var portExposure = Simulator.SimulatePortfolioExposureCapped(
            tradesForExposure, Config.MaxTotalExposurePct, slippageBps: Config.SlippageBps);

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  GRID BACKTEST SUMMARY  (val 20%, 1h candles)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  Total trades: {allRet.Count}  ({totalWins}W / {allRet.Count - totalWins}L)");
        Console.WriteLine($"  Win rate:     {(allRet.Count > 0 ? (double)totalWins / allRet.Count : 0):P1}");
        Console.WriteLine($"  Avg return:   {(allRet.Count > 0 ? allRet.Average() : 0):+0.00}%");
        Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(allRet):F2}");
        Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(allRet):F2}");
        Console.WriteLine();

        void PrintPortSim(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
            Console.WriteLine();
        }

        PrintPortSim("5% cap (conservative)", port5pct);
        PrintPortSim($"half-Kelly, uncapped", portHalfKel);
        PrintPortSim($"half-Kelly, {Config.MaxTotalExposurePct:P0} max total exposure (hard concurrent cap)", portExposure);
        Console.WriteLine();
        Console.WriteLine($"  Per-coin (sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in coinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
    }

    public static async Task RunRankedBacktest(BybitRestClient client)
    {
        const int MaxSwing = 5;
        const int MaxGrid  = 5;
        const double PosSizePct = 0.05;

        Console.WriteLine($"=== Gravity-gen2 | RANKED BACKTEST (max {MaxSwing} swing · {MaxGrid} grid · {PosSizePct:P0}/pos) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile))     { Console.WriteLine("Missing swing genotype — run 'train' first.");     return; }
        if (!File.Exists(Config.GridGenoFile)) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first."); return; }

        var swingG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var gridG  = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        Console.WriteLine($"Swing: {swingG}");
        Console.WriteLine($"Grid:  {gridG}\n");

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
        var fetched = (await Task.WhenAll(fetchTasks))
            .Where(f => f.h1.Length >= 300)
            .ToArray();
        Console.WriteLine($"  Done.\n");

        var allCandidates = new List<ScoredTrade>();
        int swingCoins = 0, gridCoins = 0;

        Console.WriteLine($"  Qualifying coins and generating scored trades...\n");
        Console.WriteLine($"  {"Coin",-16} {"Strategy",-8} {"Candidates",10}  {"AvgScore",9}  {"Screen"}");
        Console.WriteLine($"  {new string('-', 68)}");

        foreach (var (sym, m15, h1) in fetched)
        {
            {
                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;
            }

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            {
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var tRet  = FadeShortSimulator.GetFadeShortReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
                double tExp  = tRet.Count >= 5 ? tRet.Average()                                      : double.NegativeInfinity;
                double tSort = tRet.Count >= 5 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12)  : double.NegativeInfinity;
                double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet)                        : 0;

                if (tExp > 0 && tSort >= 0.3 && tPF >= 1.1)
                {
                    var scored = FadeShortSimulator.GetScoredSwingTrades(sym, swingG, h1Val, m15Val);
                    if (scored.Count > 0)
                    {
                        allCandidates.AddRange(scored);
                        swingCoins++;
                        double avgSc = scored.Average(t => t.Score);
                        Console.WriteLine($"  {sym,-16} {"swing",-8} {scored.Count,10}  {avgSc,9:F2}  pass (exp={tExp:+0.00}% sort={tSort:F2} pf={tPF:F2})");
                    }
                }
            }

            {
                var tRet = GridSimulator.GetGridSessionReturns(gridG, h1Train).Select(t => t.Return).ToList();
                double tExp = tRet.Count >= 5 ? tRet.Average()           : double.NegativeInfinity;
                double tPF  = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;

                if (tExp > 0 && tPF >= 1.15)
                {
                    var scored = GridSimulator.GetScoredGridTrades(sym, gridG, h1Val);
                    if (scored.Count > 0)
                    {
                        allCandidates.AddRange(scored);
                        gridCoins++;
                        double avgSc = scored.Average(t => t.Score);
                        Console.WriteLine($"  {sym,-16} {"grid",-8} {scored.Count,10}  {avgSc,9:F4}  pass (exp={tExp:+0.00}% pf={tPF:F2})");
                    }
                }
            }
        }

        if (allCandidates.Count == 0) { Console.WriteLine("\nNo candidates."); return; }

        var swingCands = allCandidates.Where(t => t.Strategy == "swing").ToList();
        var gridCands  = allCandidates.Where(t => t.Strategy == "grid").ToList();

        Console.WriteLine();
        Console.WriteLine($"  Total candidates — swing: {swingCands.Count} ({swingCoins} coins)  grid: {gridCands.Count} ({gridCoins} coins)");
        if (swingCands.Count > 0)
            Console.WriteLine($"  Swing score  — min: {swingCands.Min(t => t.Score):F2}  median: {swingCands.OrderBy(t=>t.Score).ToList()[swingCands.Count/2].Score:F2}  max: {swingCands.Max(t => t.Score):F2}");
        if (gridCands.Count > 0)
            Console.WriteLine($"  Grid  score  — min: {gridCands.Min(t => t.Score):F4}  median: {gridCands.OrderBy(t=>t.Score).ToList()[gridCands.Count/2].Score:F4}  max: {gridCands.Max(t => t.Score):F4}");

        Console.WriteLine();

        var sortedCands  = allCandidates.OrderBy(t => t.Entry).ToList();
        double valDays   = allCandidates.Count >= 2
                         ? (sortedCands[^1].Exit - sortedCands[0].Entry).TotalDays
                         : 1;
        double annFactor = valDays > 0 ? 365.0 / valDays : 1.0;
        int    valH1     = (int)(valDays * 24);

        void PrintPort(string label, RankedPortfolioSim.SimResult r)
        {
            int taken   = r.SwingTaken  + r.GridTaken;
            int skipped = r.SwingSkipped + r.GridSkipped;
            var allRet  = r.SwingReturns.Concat(r.GridReturns).ToList();
            double annRet = (Math.Pow(1 + r.ReturnPct / 100.0, annFactor) - 1) * 100;
            double pf     = Simulator.ProfitFactor(allRet);
            double sort   = allRet.Count > 0 ? Simulator.SortinoRatio(allRet, valH1) : 0;
            double wr     = allRet.Count > 0 ? (double)allRet.Count(x => x > 0) / allRet.Count : 0;
            Console.WriteLine($"  ── {label} ──");
            Console.WriteLine($"    Taken / skipped:  {taken} / {skipped}  ({100.0*taken/(taken+skipped):F0}% taken)");
            Console.WriteLine($"    Win rate:         {wr:P1}  |  Avg: {(allRet.Count>0?allRet.Average():0):+0.000;-0.000}%  |  PF: {pf:F2}  |  Sortino: {sort:F2}");
            Console.WriteLine($"    End balance:      €{r.EndBalance:F2}  ({r.ReturnPct:+0.0;-0.0}%  →  {annRet:+0.0;-0.0}% ann.)");
            Console.WriteLine($"    Max drawdown:     {r.MaxDD:F1}%");
            Console.WriteLine();
        }

        var unranked = RankedPortfolioSim.Run(allCandidates, maxSwing: 9999, maxGrid: 9999, PosSizePct);
        var ranked   = RankedPortfolioSim.Run(allCandidates, MaxSwing, MaxGrid, PosSizePct);

        Console.WriteLine($"══════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  RANKED BACKTEST SUMMARY  (val 20% · {PosSizePct:P0} fixed per position · {valDays:F0} val days)");
        Console.WriteLine($"══════════════════════════════════════════════════════════════════════");
        Console.WriteLine();
        PrintPort($"Unranked baseline (all {allCandidates.Count} signals, unlimited concurrent)", unranked);
        PrintPort($"Ranked (max {MaxSwing} swing + {MaxGrid} grid concurrent)", ranked);
    }
}
