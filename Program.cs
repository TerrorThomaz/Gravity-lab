using TradingGA;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using System.Text.Json;

const string GenoFile     = "swing_best_genotype.json";
const string GridGenoFile = "grid_best_genotype.json";
const double MaxTotalExposurePct = 0.40;   // max % of capital deployed simultaneously across all open positions

// 45 coins used by both backtest and papertrade
string[] BacktestCoins =
[
    // large caps / training coins
    "SOLUSDT",  "ETHUSDT",   "BNBUSDT",   "XRPUSDT",    "DOGEUSDT",
    "AVAXUSDT", "LINKUSDT",  "ATOMUSDT",  "NEARUSDT",   "INJUSDT",
    "OPUSDT",   "ARBUSDT",   "ADAUSDT",   "DOTUSDT",    "MATICUSDT",
    "UNIUSDT",  "AAVEUSDT",  "RUNEUSDT",  "STXUSDT",    "FILUSDT",
    // mid-caps / memes
    "WIFUSDT",       "MEMEUSDT",     "1000BONKUSDT", "1000PEPEUSDT",
    "1000FLOKIUSDT", "SUIUSDT",      "APTUSDT",      "LDOUSDT",
    "TIAUSDT",       "SEIUSDT",      "WLDUSDT",      "JUPUSDT",
    "ENAUSDT",       "EIGENUSDT",    "ONDOUSDT",     "PYTHUSDT",
    "GMXUSDT",       "DYDXUSDT",     "SANDUSDT",     "MANAUSDT",
    "GALAUSDT",      "APEUSDT",
    // large/safe
    "BTCUSDT",       "LTCUSDT",      "BCHUSDT",
];

var client = new BybitRestClient();

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
switch (mode)
{
    case "train":        await RunTrain();        break;
    case "backtest":     await RunBacktest();     break;
    case "papertrade":   await RunPaperTrade();   break;
    case "gridtrain":       await RunGridTrain();       break;
    case "gridbacktest":    await RunGridBacktest();    break;
    case "combinedbacktest":await RunCombinedBacktest();break;
    case "test":            await RunTest();            break;
    default:
        Console.WriteLine("Gravity-gen2 — usage:");
        Console.WriteLine("  dotnet run -- train             Swing GA: 26 coins, 1h/15m dual-TF, ~3yr");
        Console.WriteLine("  dotnet run -- backtest          Swing backtest: 45 coins, val 20%");
        Console.WriteLine("  dotnet run -- papertrade        Live swing signals, refreshes every 4h");
        Console.WriteLine("  dotnet run -- gridtrain         Grid GA: ranging-market long grid, 1h candles");
        Console.WriteLine("  dotnet run -- gridbacktest      Grid backtest: 45 coins, val 20%");
        Console.WriteLine("  dotnet run -- combinedbacktest  Swing + grid simultaneous, shared capital");
        Console.WriteLine("  dotnet run -- test              Statistical edge validation (both strategies)");
        break;
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN — Swing GA on 1h candles (aggregated from 15m), 26 coins, ~3yr
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTrain()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN (1h setup + 15m entry/exit, 26 coins, ~3yr) ===\n");

    // Diverse set: large caps for reliable trend structure + volatile alts for swing amplitude.
    // Weighted down: coins with shorter history or noisier signals.
    var trainCoins = new[]
    {
        ("SOLUSDT",       1.0),
        ("ETHUSDT",       1.0),
        ("BNBUSDT",       1.0),
        ("XRPUSDT",       1.0),
        ("DOGEUSDT",      1.0),
        ("AVAXUSDT",      1.0),
        ("ADAUSDT",       1.0),
        ("LINKUSDT",      1.0),
        ("DOTUSDT",       1.0),
        ("MATICUSDT",     1.0),
        ("ATOMUSDT",      0.9),
        ("NEARUSDT",      0.9),
        ("INJUSDT",       0.9),
        ("OPUSDT",        0.9),
        ("ARBUSDT",       0.9),
        ("UNIUSDT",       0.9),
        ("AAVEUSDT",      0.9),
        ("RUNEUSDT",      0.9),
        ("WIFUSDT",       0.8),
        ("1000PEPEUSDT",  0.8),
        ("APTUSDT",       0.8),
        ("SUIUSDT",       0.8),
        ("TIAUSDT",       0.8),
        ("SEIUSDT",       0.8),
        ("STXUSDT",       0.8),
        ("JUPUSDT",       0.8),
    };

    Console.WriteLine($"  Fetching {trainCoins.Length} coins (15m candles → aggregated to 1h, ~3yr)...");
    var semTrain = new SemaphoreSlim(4);
    var fetchTasks = trainCoins.Select(async ((string sym, double weight) t) =>
    {
        await semTrain.WaitAsync();
        try
        {
            var m15 = await FetchFifteenMinCandlesCached(t.sym, batches: 113);
            var h1  = SwingSimulator.AggregateCandles(m15.ToArray(), 4);
            Console.WriteLine($"  {t.sym}: {m15.Count} 15m → {h1.Length} h1 candles (~{h1.Length / 24.0:F0}d)");
            return (t.sym, t.weight, h1);
        }
        finally { semTrain.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);

    var namedCoins = new List<(string Sym, SwingGeneticAlgorithm.CoinData Cd)>();
    foreach (var (sym, weight, h1) in fetched)
    {
        if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
        int split = (int)(h1.Length * 0.8);
        namedCoins.Add((sym, new SwingGeneticAlgorithm.CoinData(
            h1[..split],
            h1[split..],
            weight)));
    }

    if (namedCoins.Count == 0) { Console.WriteLine("No data."); return; }

    SwingGenotype? seed = null;
    if (File.Exists(GenoFile))
    {
        var candidate = JsonSerializer.Deserialize<SwingGenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        if (candidate.Fitness > 0)
        {
            seed = candidate;
            Console.WriteLine($"  Seeding from {GenoFile}: {seed}");
        }
        else
            Console.WriteLine($"  Skipping seed (fitness ≤ 0 — previous run failed)");
    }

    // Auto-screen: keep only coins where the seed shows positive expectancy on training data.
    if (seed != null)
    {
        Console.WriteLine("\n  Screening coins (seed expectancy on train data):");
        int before = namedCoins.Count;
        var screened = namedCoins
            .Where(nc =>
            {
                var returns = SwingSimulator.GetSwingReturns(seed, nc.Cd.TrainCandles)
                                           .Select(t => t.Return).ToList();
                double exp = returns.Count >= 10 ? returns.Average() : double.NegativeInfinity;
                double pf  = returns.Count >= 10 ? Simulator.ProfitFactor(returns) : 0;
                bool pass  = exp > 0 && pf >= 1.1;
                Console.WriteLine($"    {(pass ? "✓" : "✗")} {nc.Sym,-20} Exp={exp:+0.00;-0.00}%  PF={pf:F2}  Tr={returns.Count}");
                return pass;
            })
            .ToList();

        if (screened.Count >= 4)
        {
            namedCoins = screened;
            Console.WriteLine($"  → {screened.Count}/{before} coins pass\n");
        }
        else
            Console.WriteLine($"  ⚠ Only {screened.Count}/{before} passed — keeping all {before} to avoid data starvation\n");
    }

    var coinData = namedCoins.Select(nc => nc.Cd).ToList();
    Console.WriteLine($"\n  Training on {coinData.Count} coins simultaneously\n");

    Console.WriteLine("─── Swing GA training ───");
    var best = new SwingGeneticAlgorithm(80, 150, verbose: true).Run(coinData, seed);

    Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
    File.WriteAllText(GenoFile, JsonSerializer.Serialize(SwingGenotypeDto.From(best),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  Saved → {GenoFile}");

    // Overfit check
    Console.WriteLine("\n─── Overfit check (train 80% vs val 20%) ───");
    var tRet = coinData.SelectMany(cd =>
        SwingSimulator.GetSwingReturns(best, cd.TrainCandles).Select(t => t.Return)).ToList();
    var vRet = coinData.SelectMany(cd =>
        SwingSimulator.GetSwingReturns(best, cd.ValCandles).Select(t => t.Return)).ToList();

    int tCC = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
    int vCC = coinData.Sum(cd => cd.ValCandles.Length) * 12;
    PrintSplitStats("Train 80%", tRet, tCC);
    PrintSplitStats("Val   20%", vRet, vCC);

    double vExp = vRet.Count > 0 ? vRet.Average() : 0;
    double tExp = tRet.Count > 0 ? tRet.Average() : 0;
    Console.WriteLine(vExp < tExp * 0.4 || vExp <= 0
        ? "\n  !! Possible overfit — val expectancy < 40% of train"
        : "\n  OK — val expectancy within acceptable range");

    Console.WriteLine($"\nNext: dotnet run -- backtest   ← verify on {BacktestCoins.Length} coins (1h/15m dual-TF)");
}

// ══════════════════════════════════════════════════════════════════════════════
//  BACKTEST — Swing backtest on 45 coins, val 20%, 1h setup + 15m entry/exit
// ══════════════════════════════════════════════════════════════════════════════
async Task RunBacktest()
{
    Console.WriteLine($"=== Gravity-gen2 | BACKTEST (~3yr, {BacktestCoins.Length} coins, 1h setup + 15m entry/exit) ===\n");

    if (!File.Exists(GenoFile))
    {
        Console.WriteLine($"No genotype at '{GenoFile}'. Run 'dotnet run -- train' first.");
        return;
    }
    var g = JsonSerializer.Deserialize<SwingGenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
    Console.WriteLine($"Genotype: {g}\n");

    var testCoins = BacktestCoins;

    Console.WriteLine($"  Fetching {testCoins.Length} coins (15m candles, ~3yr — first run builds cache)...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = testCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try
        {
            var m15 = await FetchFifteenMinCandlesCached(sym, batches: 113);
            Console.WriteLine($"  {sym}: {m15.Count} 15m candles (~{m15.Count / 96.0:F0}d)");
            return (sym, m15);
        }
        finally { sem.Release(); }
    });
    var fetchedArr = await Task.WhenAll(fetchTasks);
    Console.WriteLine();

    var allTrades     = new List<(string Coin, DateTime Time, double Return, string Kind, double CoinConf)>();
    var coinStats     = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
    int totalVCC      = 0;

    Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine(new string('-', 82));

    foreach (var (sym, m15List) in fetchedArr)
    {
        if (m15List.Count < 600) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

        var m15 = m15List.ToArray();
        var h1  = SwingSimulator.AggregateCandles(m15, 4);

        int h1Split  = (int)(h1.Length * 0.8);
        int m15Split = h1Split * 4;
        var h1Train  = h1[..h1Split];
        var h1Val    = h1[h1Split..];
        var m15Train = m15[..m15Split];
        var m15Val   = m15[m15Split..];

        var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
        var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
        var tRet  = SwingSimulator.GetSwingReturns(g, screenH1, screenM15).Select(t => t.Return).ToList();
        double tExp  = tRet.Count >= 5 ? tRet.Average() : double.NegativeInfinity;
        double tSort = tRet.Count >= 5 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12) : double.NegativeInfinity;
        double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;
        if (tExp <= 0 || tSort < 0.3 || tPF < 1.1)
        {
            Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% sort={tSort:F2} pf={tPF:F2})");
            continue;
        }

        double coinConf = Simulator.ComputeConfidence(tRet);

        var vTrades = SwingSimulator.GetSwingReturns(g, h1Val, m15Val);
        var vRet    = vTrades.Select(t => t.Return).ToList();

        int vCC = h1Val.Length * 12;
        totalVCC += vCC;

        double sh   = Simulator.SharpeRatio(vRet, vCC);
        double sort = Simulator.SortinoRatio(vRet, vCC);
        double pf   = Simulator.ProfitFactor(vRet);
        double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
        double avg  = vRet.Count > 0 ? vRet.Average() : 0;

        foreach (var (t, ret, kind) in vTrades)
            allTrades.Add((sym, t, ret, kind, coinConf));

        Console.WriteLine($"  {sym,-16} {coinConf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
        coinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, coinConf));
    }

    if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

    allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
    var allRet     = allTrades.Select(t => t.Return).ToList();
    double portSharpe  = Simulator.SharpeRatio(allRet, totalVCC);
    double portSortino = Simulator.SortinoRatio(allRet, totalVCC);
    double portPf      = Simulator.ProfitFactor(allRet);
    double portCalmar  = Simulator.CalmarRatio(allRet);
    int    totalWins   = allRet.Count(r => r > 0);

    var tradesWithConf = allTrades.Select(t => (t.Return, t.CoinConf)).ToList();
    var port = Simulator.SimulatePortfolio(tradesWithConf);

    Console.WriteLine($"\n{new string('═', 70)}");
    Console.WriteLine($"  BACKTEST SUMMARY  (all coins, val 20%, 1h setup + 15m exec)");
    Console.WriteLine($"{new string('═', 70)}");
    Console.WriteLine($"  Total trades: {allRet.Count}  ({totalWins}W / {allRet.Count - totalWins}L)");
    Console.WriteLine($"  Win rate:     {(allRet.Count > 0 ? (double)totalWins / allRet.Count : 0):P1}");
    Console.WriteLine($"  Avg return:   {(allRet.Count > 0 ? allRet.Average() : 0):+0.00}%");
    Console.WriteLine($"  Sharpe:       {portSharpe:F2}");
    Console.WriteLine($"  Sortino:      {portSortino:F2}");
    Console.WriteLine($"  Profit factor:{portPf:F2}");
    Console.WriteLine($"  Calmar:       {portCalmar:F2}");
    Console.WriteLine($"  Shorts:       {allTrades.Count}");
    Console.WriteLine();
    Console.WriteLine($"  ── Portfolio sim (€100 start · per-coin half-Kelly · 5% max · full compounding) ──");
    Console.WriteLine($"    End balance:   €{port.EndBalance:F2}");
    Console.WriteLine($"    Return:        {(port.EndBalance - port.StartBalance) / port.StartBalance * 100:+0.0;-0.0}%");
    Console.WriteLine($"    Avg position:  €{port.AvgPositionEur:F2}");
    Console.WriteLine($"    Max drawdown:  {port.MaxDrawdownPct:F1}%");
    if (port.TradesToTenPct > 0)
        Console.WriteLine($"    Trades to +10%:{port.TradesToTenPct}");
    Console.WriteLine();

    Console.WriteLine($"  Per-coin (sorted by Sharpe):");
    Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine($"  {new string('-', 75)}");
    foreach (var r in coinStats.OrderByDescending(c => c.Sharpe))
        Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
}

// ══════════════════════════════════════════════════════════════════════════════
//  PAPER TRADE — live swing signals on 4h candles, refreshes every 4h
// ══════════════════════════════════════════════════════════════════════════════
async Task RunPaperTrade()
{
    Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (4h candles) — Ctrl+C to stop ===\n");

    if (!File.Exists(GenoFile))
    {
        Console.WriteLine($"No genotype at '{GenoFile}'. Run 'dotnet run -- train' first.");
        return;
    }
    var g = JsonSerializer.Deserialize<SwingGenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
    Console.WriteLine($"Genotype: {g}\n");

    var coins = BacktestCoins;

    const int RefreshSeconds = 14400;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    while (!cts.Token.IsCancellationRequested)
    {
        Console.Clear();
        Console.WriteLine($"=== Gravity-gen2 | PAPER TRADE  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]  Ctrl+C to stop ===\n");
        Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
        Console.WriteLine(new string('-', 105));

        foreach (var sym in coins)
        {
            if (cts.Token.IsCancellationRequested) break;

            var candles = await FetchSwingCandles(sym, batches: 1);
            if (candles.Count < 100) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }

            var (passes, atrPct, volM) = CheckSwingCriteria(candles);
            if (!passes)
            {
                Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M");
                continue;
            }

            double px = candles[^1].Close;
            var    st = SwingSimulator.GetSwingTradeState(g, candles.ToArray());

            string stateStr  = st.InTrade
                ? (st.TrailArmed ? "TRAIL ARMED" : $"SHORT b{st.HoldCount}")
                : "watching";
            string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
            string unreal    = st.InTrade
                ? $"{(st.Entry - px) / st.Entry * 100.0:+0.00}%"
                : "—";
            string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
            string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
            string barsStr   = st.InTrade ? $"{st.HoldCount}" : "—";

            Console.WriteLine($"  {sym,-18}  {stateStr,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {barsStr,5}  {stopStr,12}  {targetStr,12}");
        }

        if (cts.Token.IsCancellationRequested) break;

        for (int s = RefreshSeconds; s > 0; s--)
        {
            if (cts.Token.IsCancellationRequested) break;
            Console.Write($"\r  Next refresh in {s / 3600}h {s % 3600 / 60}m {s % 60:00}s  ");
            await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
        }
    }

    Console.WriteLine("\n\n  Paper trade stopped.");
}

// ── Helpers ───────────────────────────────────────────────────────────────────

// Fetch 4h candles from Bybit. batches=7 → ~3.2yr; batches=1 sufficient for papertrade warmup.
async Task<List<Candle>> FetchSwingCandles(string symbol, int batches = 7)
{
    var all = new List<Candle>();
    DateTime? endTime = null;
    for (int batch = 0; batch < batches; batch++)
    {
        bool success = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500 * attempt);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await client.V5Api.ExchangeData
                .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FourHours,
                    endTime: endTime, limit: 1000, ct: cts.Token);

            if (!result.Success || result.Data?.List == null)
            {
                bool rateLimit = result.Error?.ToString().Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                              || result.Error?.ToString().Contains("429") == true;
                if (rateLimit && attempt < 3) { Console.Write("↺"); continue; }
                Console.WriteLine($"\n  [{symbol}] 4h batch {batch + 1} failed: {result.Error}");
                break;
            }

            var bc = result.Data.List
                .Select(k => new Candle(k.StartTime, (double)k.OpenPrice, (double)k.HighPrice,
                                        (double)k.LowPrice, (double)k.ClosePrice, (double)k.Volume))
                .ToList();
            if (!bc.Any()) { success = true; break; }
            all.AddRange(bc);
            endTime = bc.Min(c => c.Time).AddHours(-4);
            success = true;
            break;
        }
        if (!success) break;
        await Task.Delay(300);
    }
    return all.GroupBy(c => c.Time).Select(g => g.First()).OrderBy(c => c.Time).ToList();
}

// 15m candles with disk cache. batches=113 ≈ 3.2yr. Cache: candle_cache/{symbol}_15m.csv.
async Task<List<Candle>> FetchFifteenMinCandlesCached(string symbol, int batches = 113)
{
    const string CacheDir = "candle_cache";
    Directory.CreateDirectory(CacheDir);
    string cacheFile = Path.Combine(CacheDir, $"{symbol}_15m.csv");

    var cached = new SortedDictionary<DateTime, Candle>();
    if (File.Exists(cacheFile))
    {
        foreach (var line in File.ReadLines(cacheFile))
        {
            var p = line.Split(',');
            if (p.Length < 6) continue;
            if (!long.TryParse(p[0], out long ms)) continue;
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            cached[dt] = new Candle(dt,
                double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    bool dirty = false;

    async Task<bool> FetchBatch15m(DateTime? endTime)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500 * attempt);
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await client.V5Api.ExchangeData
                .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FifteenMinutes,
                    endTime: endTime, limit: 1000, ct: cts2.Token);
            if (!result.Success || result.Data?.List == null)
            {
                if ((result.Error?.ToString().Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                     || result.Error?.ToString().Contains("429") == true) && attempt < 3) continue;
                return false;
            }
            foreach (var k in result.Data.List)
            {
                var dt = k.StartTime;
                if (!cached.ContainsKey(dt))
                {
                    cached[dt] = new Candle(dt, (double)k.OpenPrice, (double)k.HighPrice,
                                            (double)k.LowPrice, (double)k.ClosePrice, (double)k.Volume);
                    dirty = true;
                }
            }
            await Task.Delay(500);
            return true;
        }
        return false;
    }

    bool cacheIsFresh = cached.Count > 0 &&
        (DateTime.UtcNow - cached.Keys.Max()).TotalHours < 1.0;
    if (!cacheIsFresh)
    {
        DateTime stopAt = cached.Count > 0 ? cached.Keys.Max() : DateTime.MinValue;
        DateTime? endTime = null;
        for (int i = 0; i < 5; i++)
        {
            int beforeCount = cached.Count;
            await FetchBatch15m(endTime);
            if (cached.Count == beforeCount) break;
            var fetched = cached.Keys.Min();
            if (fetched >= stopAt) break;
            endTime = fetched.AddMinutes(-15);
        }
    }

    int needed = batches * 1000;
    if (cached.Count < needed)
    {
        DateTime? endTime = cached.Count > 0 ? cached.Keys.Min().AddMinutes(-15) : null;
        int maxOldBatches = (needed - cached.Count) / 800 + 10;
        for (int i = 0; i < maxOldBatches && cached.Count < needed; i++)
        {
            int beforeCount = cached.Count;
            bool ok = await FetchBatch15m(endTime);
            if (!ok || cached.Count == beforeCount) break;
            endTime = cached.Keys.Min().AddMinutes(-15);
        }
    }

    if (dirty)
    {
        using var sw = new System.IO.StreamWriter(cacheFile);
        foreach (var c in cached.Values)
        {
            long ms = new DateTimeOffset(c.Time, TimeSpan.Zero).ToUnixTimeMilliseconds();
            sw.WriteLine(FormattableString.Invariant(
                $"{ms},{c.Open},{c.High},{c.Low},{c.Close},{c.Volume}"));
        }
    }

    return cached.Values.ToList();
}

// Coin filter: median ATR% and USD volume over the candle window.
static (bool Passes, double AtrPct, double VolUsdM) CheckSwingCriteria(
    IReadOnlyList<Candle> candles, double minAtrPct = 1.5, double minVolUsdM = 1.0)
{
    if (candles.Count < 50) return (false, 0, 0);

    var recent = candles.ToArray();

    var trPcts = new List<double>(recent.Length);
    for (int i = 1; i < recent.Length; i++)
    {
        double tr = Math.Max(recent[i].High - recent[i].Low,
                    Math.Max(Math.Abs(recent[i].High - recent[i - 1].Close),
                             Math.Abs(recent[i].Low  - recent[i - 1].Close)));
        if (recent[i].Close > 0) trPcts.Add(tr / recent[i].Close * 100.0);
    }
    trPcts.Sort();
    double medAtrPct = trPcts.Count > 0 ? trPcts[trPcts.Count / 2] : 0;

    var volUsd = recent.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(x => x).ToList();
    double medVolM = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;

    return (medAtrPct >= minAtrPct && medVolM >= minVolUsdM, medAtrPct, medVolM);
}

static void PrintSplitStats(string label, List<double> r, int candleCount)
{
    if (r.Count == 0) { Console.WriteLine($"  {label,-12} (no trades)"); return; }
    double sh   = Simulator.SharpeRatio(r, candleCount);
    double sort = Simulator.SortinoRatio(r, candleCount);
    double pf   = Simulator.ProfitFactor(r);
    double wr   = (double)r.Count(x => x > 0) / r.Count;
    double avg  = r.Average();
    Console.WriteLine($"  {label,-12} Sh={sh:F2}  Sort={sort:F2}  PF={pf:F2}  WR={wr:P0}  Tr={r.Count}  Avg={avg:+0.00}%");
}

// ══════════════════════════════════════════════════════════════════════════════
//  TEST — statistical edge validation for both strategies on training coins
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTest()
{
    Console.WriteLine("=== Gravity-gen2 | STATISTICAL TEST (swing + grid, 26 training coins, val 20%) ===\n");

    // Load genotypes
    SwingGenotype? sg = null;
    GridGenotype?  gg = null;
    if (File.Exists(GenoFile))
        sg = JsonSerializer.Deserialize<SwingGenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
    if (File.Exists(GridGenoFile))
        gg = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype();

    if (sg == null && gg == null) { Console.WriteLine("No genotypes found. Run train and gridtrain first."); return; }

    var trainCoinSyms = new[]
    {
        "SOLUSDT", "ETHUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT",
        "AVAXUSDT", "ADAUSDT", "LINKUSDT", "DOTUSDT", "MATICUSDT",
        "ATOMUSDT", "NEARUSDT", "INJUSDT", "OPUSDT", "ARBUSDT",
        "UNIUSDT", "AAVEUSDT", "RUNEUSDT", "WIFUSDT", "1000PEPEUSDT",
        "APTUSDT", "SUIUSDT", "TIAUSDT", "SEIUSDT", "STXUSDT", "JUPUSDT",
    };

    Console.WriteLine($"  Fetching {trainCoinSyms.Length} coins (15m candles, reading from cache)...");
    var sem = new SemaphoreSlim(4);
    var tasks = trainCoinSyms.Select(async sym =>
    {
        await sem.WaitAsync();
        try { return (sym, await FetchFifteenMinCandlesCached(sym, batches: 113)); }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(tasks);
    Console.WriteLine();

    var swingRet = new List<double>();
    var gridRet  = new List<double>();
    int totalH1Val = 0;

    foreach (var (sym, m15List) in fetched)
    {
        if (m15List.Count < 600) continue;
        var m15  = m15List.ToArray();
        var h1   = SwingSimulator.AggregateCandles(m15, 4);
        int split = (int)(h1.Length * 0.8);
        int m15Split = split * 4;
        var h1Val  = h1[split..];
        var m15Val = m15[m15Split..];
        totalH1Val += h1Val.Length;

        if (sg != null)
            swingRet.AddRange(SwingSimulator.GetSwingReturns(sg, h1Val, m15Val).Select(t => t.Return));
        if (gg != null)
            gridRet.AddRange(GridSimulator.GetGridReturns(gg, h1Val).Select(t => t.Return));
    }

    int candleCount = totalH1Val * 12; // convert h1 to 5m-equivalent for Sharpe normalisation

    if (sg != null)
    {
        Console.WriteLine($"Swing genotype: {sg}");
        StrategyStats.Report("Swing", swingRet, candleCount);
    }

    if (gg != null)
    {
        Console.WriteLine($"\nGrid genotype: {gg}");
        StrategyStats.Report("Grid", gridRet, candleCount);
    }

    if (sg != null && gg != null && swingRet.Count >= 5 && gridRet.Count >= 5)
        StrategyStats.Compare("Swing", swingRet, "Grid", gridRet, candleCount);
}

// ══════════════════════════════════════════════════════════════════════════════
//  GRID TRAIN — ranging-market long grid, 1h candles, 26 coins
// ══════════════════════════════════════════════════════════════════════════════
async Task RunGridTrain()
{
    Console.WriteLine("=== Gravity-gen2 | GRID TRAIN (ranging long grid, 1h candles, 26 coins) ===\n");

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
            var m15 = await FetchFifteenMinCandlesCached(t.sym, batches: 113);
            var h1  = SwingSimulator.AggregateCandles(m15.ToArray(), 4);
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

    // Load swing genotype to enforce regime partition: grid ADX ceiling = swing threshold − 1.
    double adxCeiling = 20.0;
    if (File.Exists(GenoFile))
    {
        var swingGeno = JsonSerializer.Deserialize<SwingGenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        adxCeiling = Math.Min(swingGeno.AdxThreshold - 1.0, 20.0);
        Console.WriteLine($"  Swing AdxThreshold={swingGeno.AdxThreshold:F0} → grid ceiling={adxCeiling:F0} (clean partition)");
    }

    GridGenotype? seed = null;
    if (File.Exists(GridGenoFile))
    {
        var candidate = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype();
        // Re-clamp seed with updated ceiling so stale AdxThreshold can't exceed swing's partition
        var clamped = candidate.ClampToBounds(adxCeiling);
        if (clamped.Fitness > 0) { seed = clamped; Console.WriteLine($"  Seeding from {GridGenoFile}: {seed}"); }
        else Console.WriteLine("  Skipping seed (fitness ≤ 0)");
    }

    Console.WriteLine($"\n  Training on {coinData.Count} coins\n");
    Console.WriteLine("─── Grid GA training ───");
    var best = new GridGeneticAlgorithm(60, 100, verbose: true).Run(coinData, seed, adxCeiling);

    Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
    File.WriteAllText(GridGenoFile, JsonSerializer.Serialize(GridGenotypeDto.From(best),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  Saved → {GridGenoFile}");

    Console.WriteLine("\n─── Overfit check (train 80% vs val 20%, session-level) ───");
    var tRet = coinData.SelectMany(cd => GridSimulator.GetGridSessionReturns(best, cd.TrainCandles).Select(t => t.Return)).ToList();
    var vRet = coinData.SelectMany(cd => GridSimulator.GetGridSessionReturns(best, cd.ValCandles).Select(t => t.Return)).ToList();
    int tCC  = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
    int vCC  = coinData.Sum(cd => cd.ValCandles.Length)   * 12;
    PrintSplitStats("Train 80%", tRet, tCC);
    PrintSplitStats("Val   20%", vRet, vCC);

    Console.WriteLine($"\nNext: dotnet run -- gridbacktest");
}

// ══════════════════════════════════════════════════════════════════════════════
//  GRID BACKTEST — 45 coins, val 20%, 1h candles
// ══════════════════════════════════════════════════════════════════════════════
async Task RunGridBacktest()
{
    Console.WriteLine($"=== Gravity-gen2 | GRID BACKTEST ({BacktestCoins.Length} coins, val 20%, 1h candles) ===\n");

    if (!File.Exists(GridGenoFile))
    {
        Console.WriteLine($"No grid genotype at '{GridGenoFile}'. Run 'dotnet run -- gridtrain' first.");
        return;
    }
    var g = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype();
    Console.WriteLine($"Genotype: {g}\n");

    Console.WriteLine($"  Fetching {BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = BacktestCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try
        {
            var m15 = await FetchFifteenMinCandlesCached(sym, batches: 113);
            var h1  = SwingSimulator.AggregateCandles(m15.ToArray(), 4);
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
    var portExposure = Simulator.SimulatePortfolioExposureCapped(tradesForExposure, MaxTotalExposurePct);

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
    PrintPortSim($"half-Kelly, {MaxTotalExposurePct:P0} max total exposure (hard concurrent cap)", portExposure);
    Console.WriteLine();
    Console.WriteLine($"  Per-coin (sorted by Sharpe):");
    Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine($"  {new string('-', 75)}");
    foreach (var r in coinStats.OrderByDescending(c => c.Sharpe))
        Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
}

// ══════════════════════════════════════════════════════════════════════════════
//  COMBINED BACKTEST — swing + grid simultaneously, shared capital pool
// ══════════════════════════════════════════════════════════════════════════════
async Task RunCombinedBacktest()
{
    Console.WriteLine($"=== Gravity-gen2 | COMBINED BACKTEST (swing + grid, {BacktestCoins.Length} coins, val 20%) ===\n");

    if (!File.Exists(GenoFile))     { Console.WriteLine($"Missing swing genotype — run 'train' first.");     return; }
    if (!File.Exists(GridGenoFile)) { Console.WriteLine($"Missing grid genotype — run 'gridtrain' first."); return; }

    var swingG = JsonSerializer.Deserialize<SwingGenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
    var gridG  = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype();
    Console.WriteLine($"Swing: {swingG}");
    Console.WriteLine($"Grid:  {gridG}\n");
    Console.WriteLine($"ADX partition: grid < {gridG.AdxThreshold:F0}  |  gap {gridG.AdxThreshold:F0}–{swingG.AdxThreshold:F0}  |  swing ≥ {swingG.AdxThreshold:F0}\n");

    Console.WriteLine($"  Fetching {BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = BacktestCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try
        {
            var m15 = await FetchFifteenMinCandlesCached(sym, batches: 113);
            var h1  = SwingSimulator.AggregateCandles(m15.ToArray(), 4);
            return (sym, m15: m15.ToArray(), h1);
        }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);
    Console.WriteLine($"  Done.\n");

    var swingTrades = new List<(DateTime Time, double Return, double Conf)>();
    var gridTrades  = new List<(DateTime Time, double Return, double Conf)>();
    var allTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy)>();

    var swingCoinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
    var gridCoinStats  = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();

    int swingTotalVCC = 0;
    int gridTotalVCC  = 0;

    // ── SWING per-coin table ──────────────────────────────────────────────────
    Console.WriteLine($"══ SWING (1h setup + 15m exec) ══════════════════════════════════════════════");
    Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine(new string('-', 82));

    foreach (var (sym, m15, h1) in fetched)
    {
        if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

        int h1Split  = (int)(h1.Length * 0.8);
        int m15Split = h1Split * 4;
        var h1Train  = h1[..h1Split];
        var h1Val    = h1[h1Split..];
        var m15Train = m15[..m15Split];
        var m15Val   = m15[m15Split..];

        var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
        var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
        var tRet  = SwingSimulator.GetSwingReturns(swingG, screenH1, screenM15).Select(t => t.Return).ToList();
        double tExp  = tRet.Count >= 5 ? tRet.Average() : double.NegativeInfinity;
        double tSort = tRet.Count >= 5 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12) : double.NegativeInfinity;
        double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;
        if (tExp <= 0 || tSort < 0.3 || tPF < 1.1)
        {
            Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% sort={tSort:F2} pf={tPF:F2})");
            continue;
        }

        double conf   = Simulator.ComputeConfidence(tRet);
        var    vSwing = SwingSimulator.GetSwingReturns(swingG, h1Val, m15Val);
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
        foreach (var (t, ret, _) in vSwing)
        {
            swingTrades.Add((t, ret, conf));
            allTrades.Add((t, ret, conf, "swing"));
        }
    }

    // ── GRID per-coin table ───────────────────────────────────────────────────
    Console.WriteLine($"\n══ GRID (1h candles, ranging-market long) ════════════════════════════════════");
    Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine(new string('-', 82));

    foreach (var (sym, _, h1) in fetched)
    {
        if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

        int split   = (int)(h1.Length * 0.8);
        var h1Train = h1[..split];
        var h1Val   = h1[split..];

        var tRet = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
        double tExp = tRet.Count >= 5 ? tRet.Average() : double.NegativeInfinity;
        double tPF  = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;
        if (tExp <= 0 || tPF < 1.15)
        {
            Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% pf={tPF:F2})");
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
        foreach (var (t, ret, _) in vGrid)
        {
            gridTrades.Add((t, ret, conf));
            allTrades.Add((t, ret, conf, "grid"));
        }
    }

    if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

    swingTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
    gridTrades.Sort((a, b)  => a.Time.CompareTo(b.Time));
    allTrades.Sort((a, b)   => a.Time.CompareTo(b.Time));

    var swingRet = swingTrades.Select(t => t.Return).ToList();
    var gridRet  = gridTrades.Select(t => t.Return).ToList();
    var allRet   = allTrades.Select(t => t.Return).ToList();

    // ── Swing-only summary ────────────────────────────────────────────────────
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

    // ── Grid-only summary ─────────────────────────────────────────────────────
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

    // ── Combined portfolio ─────────────────────────────────────────────────────
    int totalWins = allRet.Count(r => r > 0);
    int totalVCC  = Math.Max(swingTotalVCC, gridTotalVCC);

    // Build trades lists with hold durations for exposure-capped sim
    var allTradesForExposure = allTrades
        .Select(t => (
            t.Time,
            t.Return,
            t.Conf,
            t.Strategy == "swing"
                ? TimeSpan.FromHours(swingG.MaxHoldCandles)
                : TimeSpan.FromHours(gridG.MaxHoldCandles)))
        .ToList();

    var port5pct     = Simulator.SimulatePortfolio(allTrades.Select(t => (t.Return, t.Conf)).ToList());
    var portHalfKel  = Simulator.SimulatePortfolio(allTrades.Select(t => (t.Return, t.Conf)).ToList(), maxPositionPct: 1.0);
    var portExposure = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, MaxTotalExposurePct);

    Console.WriteLine($"\n{new string('═', 70)}");
    Console.WriteLine($"  COMBINED SUMMARY  (swing + grid, {allRet.Count} trades total)");
    Console.WriteLine($"{new string('═', 70)}");
    Console.WriteLine($"  Swing:  {swingRet.Count,4} trades  PF={Simulator.ProfitFactor(swingRet):F2}  " +
                      $"WR={(swingRet.Count > 0 ? (double)swingRet.Count(r => r > 0)/swingRet.Count : 0):P0}  " +
                      $"Avg={(swingRet.Count > 0 ? swingRet.Average() : 0):+0.00}%");
    Console.WriteLine($"  Grid:   {gridRet.Count,4} trades  PF={Simulator.ProfitFactor(gridRet):F2}  " +
                      $"WR={(gridRet.Count > 0 ? (double)gridRet.Count(r => r > 0)/gridRet.Count : 0):P0}  " +
                      $"Avg={(gridRet.Count > 0 ? gridRet.Average() : 0):+0.00}%");
    Console.WriteLine($"  Total:  {allRet.Count,4} trades  PF={Simulator.ProfitFactor(allRet):F2}  " +
                      $"WR={(double)totalWins / allRet.Count:P0}  " +
                      $"Avg={allRet.Average():+0.00}%");
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

    PrintCombinedPort("5% cap (conservative)", port5pct);
    PrintCombinedPort("half-Kelly, uncapped", portHalfKel);
    PrintCombinedPort($"half-Kelly, {MaxTotalExposurePct:P0} max total exposure (hard concurrent cap)", portExposure);

    if (allTrades.Count >= 2)
    {
        double valDays   = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
        double annFactor = valDays > 0 ? 365.0 / valDays : 1.0;
        double totalRet  = (portExposure.EndBalance - portExposure.StartBalance) / portExposure.StartBalance * 100;
        double annRet    = (Math.Pow(1 + totalRet / 100.0, annFactor) - 1) * 100;
        double perDay    = (Math.Pow(1 + totalRet / 100.0, 1.0 / Math.Max(valDays, 1)) - 1) * 100;
        Console.WriteLine();
        Console.WriteLine($"  Val window: {valDays:F0} days  →  annualised {annRet:+0.0;-0.0}%  ({perDay:+0.000;-0.000}%/day)  [exposure-capped]");
    }
}

// ── Type declarations ─────────────────────────────────────────────────────────

class SwingGenotypeDto
{
    public int    EmaPeriod      { get; set; }
    public int    RsiPeriod      { get; set; }
    public int    AdxPeriod      { get; set; }
    public double AdxThreshold   { get; set; }
    public int    LookbackCandles  { get; set; }
    public double RsiOverbought    { get; set; }
    public double RsiDivThreshold  { get; set; }
    public double MinRallyAtrMult  { get; set; }
    public double StopLossAtrMult           { get; set; }
    public double TakeProfitAtrMult         { get; set; }
    public double TrailingActivationAtrMult { get; set; }
    public double TrailingStopAtrMult       { get; set; }
    public int    MaxHoldCandles            { get; set; }
    public double Fitness                   { get; set; }

    public static SwingGenotypeDto From(SwingGenotype g) => new()
    {
        EmaPeriod      = g.EmaPeriod,
        RsiPeriod      = 7,   // fixed constant — kept in JSON for readability
        AdxPeriod      = 7,   // fixed constant — kept in JSON for readability
        AdxThreshold   = g.AdxThreshold,
        LookbackCandles  = g.LookbackCandles,
        RsiOverbought    = g.RsiOverbought,
        RsiDivThreshold  = g.RsiDivThreshold,
        MinRallyAtrMult  = g.MinRallyAtrMult,
        StopLossAtrMult           = g.StopLossAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        Fitness                   = g.Fitness,
    };

    // RsiPeriod and AdxPeriod are now fixed constants in SwingSimulator — ignored on load.
    public SwingGenotype ToGenotype() => new SwingGenotype
    {
        EmaPeriod        = EmaPeriod,
        AdxThreshold     = AdxThreshold,
        LookbackCandles  = LookbackCandles  > 0 ? LookbackCandles  : 15,
        RsiOverbought    = RsiOverbought    > 0 ? RsiOverbought    : 68.0,
        RsiDivThreshold  = RsiDivThreshold  > 0 ? RsiDivThreshold  : 8.0,
        MinRallyAtrMult  = MinRallyAtrMult  > 0 ? MinRallyAtrMult  : 5.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? Math.Min(StopLossAtrMult, 2.0) : 0.8,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 4.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 3.0,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 1.5,
        MaxHoldCandles            = MaxHoldCandles            > 0 ? MaxHoldCandles            : 42,
        Fitness                   = Fitness,
    }.ClampToBounds();
}

class GridGenotypeDto
{
    public double AdxThreshold      { get; set; }
    public int    BbPeriod          { get; set; }
    public double BbWidthMaxPct     { get; set; }
    public int    EmaPeriod         { get; set; }
    public double GridStepAtrMult   { get; set; }
    public int    GridLevels        { get; set; }
    public double TakeProfitAtrMult { get; set; }
    public double HardStopAtrMult   { get; set; }
    public int    MaxHoldCandles    { get; set; }
    public double Fitness           { get; set; }

    public static GridGenotypeDto From(GridGenotype g) => new()
    {
        AdxThreshold     = g.AdxThreshold,
        BbPeriod         = g.BbPeriod,
        BbWidthMaxPct    = g.BbWidthMaxPct,
        EmaPeriod        = g.EmaPeriod,
        GridStepAtrMult  = g.GridStepAtrMult,
        GridLevels       = g.GridLevels,
        TakeProfitAtrMult= g.TakeProfitAtrMult,
        HardStopAtrMult  = g.HardStopAtrMult,
        MaxHoldCandles   = g.MaxHoldCandles,
        Fitness          = g.Fitness,
    };

    public GridGenotype ToGenotype() => new GridGenotype
    {
        AdxThreshold     = AdxThreshold     > 0 ? AdxThreshold     : 16.0,
        BbPeriod         = BbPeriod         > 0 ? BbPeriod         : 20,
        BbWidthMaxPct    = BbWidthMaxPct    > 0 ? BbWidthMaxPct    : 1.8,
        EmaPeriod        = EmaPeriod        > 0 ? EmaPeriod        : 50,
        GridStepAtrMult  = GridStepAtrMult  > 0 ? GridStepAtrMult  : 0.8,
        GridLevels       = GridLevels       > 0 ? GridLevels       : 2,
        TakeProfitAtrMult= TakeProfitAtrMult> 0 ? TakeProfitAtrMult: 1.5,
        HardStopAtrMult  = HardStopAtrMult  > 0 ? HardStopAtrMult  : 2.2,
        MaxHoldCandles   = MaxHoldCandles   > 0 ? MaxHoldCandles   : 96,
        Fitness          = Fitness,
    }.ClampToBounds();
}
