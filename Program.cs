using TradingGA;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using System.Text.Json;

const string FadeShortGenoFile = "fade_short_genotype.json";
const string GridGenoFile = "grid_best_genotype.json";
const double MaxTotalExposurePct  = 0.40;   // max % of capital deployed simultaneously across all open positions
const double MinMedianVolUsdM     = 0.5;    // $0.5M per 1h candle — thin markets excluded from train + backtest

// 93 coins used by both backtest and papertrade
string[] BacktestCoins =
[
    // large caps
    "BTCUSDT",   "ETHUSDT",   "SOLUSDT",   "BNBUSDT",   "XRPUSDT",
    "DOGEUSDT",  "ADAUSDT",   "AVAXUSDT",  "TRXUSDT",   "XLMUSDT",
    "DOTUSDT",   "LINKUSDT",  "ATOMUSDT",  "NEARUSDT",  "LTCUSDT",
    "BCHUSDT",   "ETCUSDT",   "VETUSDT",   "HBARUSDT",  "ALGOUSDT",
    "ICPUSDT",   "FILUSDT",   "STXUSDT",

    // L2s / newer L1s
    "MATICUSDT", "OPUSDT",    "ARBUSDT",   "APTUSDT",   "SUIUSDT",
    "INJUSDT",   "SEIUSDT",   "TIAUSDT",   "TONUSDT",   "KASUSDT",
    "FTMUSDT",   "CFXUSDT",   "ROSEUSDT",  "KSMUSDT",   "STRKUSDT",

    // DeFi blue chips
    "UNIUSDT",   "AAVEUSDT",  "MKRUSDT",   "SNXUSDT",   "CRVUSDT",
    "COMPUSDT",  "GMXUSDT",   "DYDXUSDT",  "RUNEUSDT",  "LDOUSDT",
    "SUSHIUSDT", "1INCHUSDT", "PENDLEUSDT","JUPUSDT",   "ENAUSDT",

    // AI / infra / data
    "TAOUSDT",   "RNDRUSDT",  "FETUSDT",   "WLDUSDT",   "PYTHUSDT",
    "EIGENUSDT", "ONDOUSDT",  "ARUSDT",    "OCEANUSDT",

    // NFT / gaming / meta
    "SANDUSDT",  "MANAUSDT",  "AXSUSDT",   "IMXUSDT",   "GALAUSDT",
    "APEUSDT",   "CHZUSDT",   "AUDIOUSDT", "ENJUSDT",

    // mid-caps
    "ZILUSDT",   "ANKRUSDT",  "LRCUSDT",   "SKLUSDT",   "BANDUSDT",
    "CTSIUSDT",  "HNTUSDT",   "LPTUSDT",   "STORJUSDT", "BATUSDT",
    "CELRUSDT",  "QNTUSDT",

    // memes / high-beta
    "WIFUSDT",       "MEMEUSDT",     "1000BONKUSDT", "1000PEPEUSDT",
    "1000FLOKIUSDT", "BOMEUSDT",     "1000SHIBUSDT", "NOTUSDT",
    "ORDIUSDT",      "TURBOUSDT",
];

var client = new BybitRestClient();

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
switch (mode)
{
    case "train":            await RunFadeShortTrain();    break;
    case "backtest":         await RunBacktest();          break;
    case "papertrade":       await RunPaperTrade();        break;
    case "gridtrain":        await RunGridTrain();         break;
    case "gridbacktest":     await RunGridBacktest();      break;
    case "combinedbacktest": await RunCombinedBacktest();  break;
    case "rankedbacktest":   await RunRankedBacktest();    break;
    case "test":             await RunTest();              break;
    case "yearlybreakdown":  await RunYearlyBreakdown();   break;
    default:
        Console.WriteLine("Gravity-gen2 — usage:");
        Console.WriteLine("  dotnet run -- train              Train FadeShort GA (~10 min)");
        Console.WriteLine("  dotnet run -- backtest           FadeShort + grid backtest: 93 coins, val 20%");
        Console.WriteLine("  dotnet run -- papertrade         Live signals, refreshes every 4h");
        Console.WriteLine("  dotnet run -- gridtrain          Grid GA: ranging-market long grid");
        Console.WriteLine("  dotnet run -- gridbacktest       Grid backtest: 93 coins, val 20%");
        Console.WriteLine("  dotnet run -- combinedbacktest   FadeShort + grid, shared capital");
        Console.WriteLine("  dotnet run -- rankedbacktest     Ranked portfolio: top-N signals by quality");
        Console.WriteLine("  dotnet run -- test               Statistical edge validation");
        Console.WriteLine("  dotnet run -- yearlybreakdown    Per-year portfolio returns (full history)");
        break;
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN — FadeShort GA, 40 coins, ~3yr, with cluster GAs + double validation
// ══════════════════════════════════════════════════════════════════════════════
async Task RunFadeShortTrain()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN (1h setup + 15m entry/exit, 40 coins, ~3yr) ===\n");

    // 5 coins held out at the coin level — never seen during training or fold scoring.
    // Selected for diversity: large-cap benchmark, legacy alt, storage sector,
    // DeFi perps, and new synthetic-dollar protocol.
    string[] heldOutSyms = ["BTCUSDT", "LTCUSDT", "FILUSDT", "DYDXUSDT", "ENAUSDT"];

    // All 45 backtest coins minus the 5 held-out. MATICUSDT has no data and will be
    // filtered by the length check below.
    // Weight by data length: ≥1400d → 1.0 · 1000–1400d → 0.9 · <1000d → 0.8
    var trainCoins = new[]
    {
        ("SOLUSDT",         1.0),
        ("ETHUSDT",         1.0),
        ("BNBUSDT",         1.0),
        ("XRPUSDT",         1.0),
        ("DOGEUSDT",        1.0),
        ("AVAXUSDT",        1.0),
        ("ADAUSDT",         1.0),
        ("LINKUSDT",        1.0),
        ("DOTUSDT",         1.0),
        ("MATICUSDT",       1.0),
        ("ATOMUSDT",        1.0),
        ("NEARUSDT",        1.0),
        ("INJUSDT",         1.0),
        ("OPUSDT",          1.0),
        ("ARBUSDT",         0.9),
        ("UNIUSDT",         1.0),
        ("AAVEUSDT",        1.0),
        ("RUNEUSDT",        1.0),
        ("STXUSDT",         1.0),
        ("WIFUSDT",         0.8),
        ("MEMEUSDT",        0.8),
        ("1000BONKUSDT",    0.9),
        ("1000PEPEUSDT",    0.9),
        ("1000FLOKIUSDT",   0.9),
        ("SUIUSDT",         0.9),
        ("APTUSDT",         0.9),
        ("LDOUSDT",         1.0),
        ("TIAUSDT",         0.8),
        ("SEIUSDT",         0.8),
        ("WLDUSDT",         0.8),
        ("JUPUSDT",         0.8),
        // EIGENUSDT excluded: 664d history is too short, posts negative val in every run
        ("ONDOUSDT",        0.8),
        ("PYTHUSDT",        0.8),
        ("GMXUSDT",         0.9),
        ("SANDUSDT",        1.0),
        ("MANAUSDT",        1.0),
        ("GALAUSDT",        1.0),
        ("APEUSDT",         1.0),
        ("BCHUSDT",         1.0),
    };

    // Fetch training coins + held-out coins in one parallel pass.
    var allSymsToFetch = trainCoins.Select(t => (t.Item1, t.Item2))
        .Concat(heldOutSyms.Select(s => (s, 0.0)))  // weight 0 = held-out marker
        .ToArray();

    Console.WriteLine($"  Fetching {trainCoins.Length} training + {heldOutSyms.Length} held-out coins (15m → 1h, ~3yr)...");
    var semTrain = new SemaphoreSlim(4);
    var fetchTasks = allSymsToFetch.Select(async ((string sym, double weight) t) =>
    {
        await semTrain.WaitAsync();
        try
        {
            var m15 = await FetchFifteenMinCandlesCached(t.sym, batches: 113);
            var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
            Console.WriteLine($"  {t.sym}: {m15.Count} 15m → {h1.Length} h1 candles (~{h1.Length / 24.0:F0}d)");
            return (t.sym, t.weight, h1);
        }
        finally { semTrain.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);

    var namedCoins   = new List<(string Sym, FadeShortGA.CoinData Cd)>();
    var heldOutCoins = new List<(string Sym, Candle[] H1)>();
    var testBySymbol = new Dictionary<string, Candle[]>();

    foreach (var (sym, weight, h1) in fetched)
    {
        if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }

        if (heldOutSyms.Contains(sym))
        {
            heldOutCoins.Add((sym, h1));   // full candle history, never split
        }
        else
        {
            int trainSplit = (int)(h1.Length * 0.75);
            int valSplit   = (int)(h1.Length * 0.875);
            namedCoins.Add((sym, new FadeShortGA.CoinData(
                h1[..trainSplit],
                h1[trainSplit..valSplit],
                weight)));
            testBySymbol[sym] = h1[valSplit..];
        }
    }

    if (namedCoins.Count == 0) { Console.WriteLine("No data."); return; }

    // Volume filter: drop coins whose median 1h USD volume is below the floor.
    // Low-volume coins produce clean backtest patterns but won't execute at those
    // prices in practice — the ATR cost model underestimates slippage in thin books.
    {
        Console.WriteLine($"\n  Volume filter (min median 1h vol ≥ ${MinMedianVolUsdM:F1}M):");
        var volFiltered = new List<(string Sym, FadeShortGA.CoinData Cd)>();
        foreach (var nc in namedCoins)
        {
            var volUsd = nc.Cd.TrainCandles
                .Select(c => c.Close * c.Volume / 1_000_000.0)
                .OrderBy(v => v)
                .ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            bool pass = medVol >= MinMedianVolUsdM;
            Console.WriteLine($"    {(pass ? "✓" : "✗")} {nc.Sym,-20} medVol=${medVol:F2}M/h");
            if (pass) volFiltered.Add(nc);
        }
        namedCoins = volFiltered;
        Console.WriteLine($"  → {namedCoins.Count} coins pass volume filter\n");
    }

    if (namedCoins.Count == 0) { Console.WriteLine("No coins passed volume filter."); return; }

    FadeShortGenotype? seed = null;
    if (File.Exists(FadeShortGenoFile))
    {
        var candidate = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
        if (candidate.Fitness > 0)
        {
            seed = candidate;
            Console.WriteLine($"  Seeding from {FadeShortGenoFile}: {seed}");
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
                var returns = FadeShortSimulator.GetFadeShortReturns(seed, nc.Cd.TrainCandles)
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

    // ── Universal GA (all coins) ──────────────────────────────────────────────────
    Console.WriteLine("─── Swing GA training (universal — all coins) ───");
    var best = new FadeShortGA(80, 150, verbose: true).Run(coinData, seed);

    Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
    File.WriteAllText(FadeShortGenoFile, JsonSerializer.Serialize(FadeShortGenotypeDto.From(best),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  Saved → {FadeShortGenoFile}");

    // ── Per-cluster GAs ──────────────────────────────────────────────────────────
    Console.WriteLine("\n─── Per-cluster GA training ───");
    var clusterGroups = namedCoins
        .GroupBy(nc => CoinClusterHelper.Classify(nc.Cd.TrainCandles))
        .OrderBy(grp => (int)grp.Key)
        .ToList();

    foreach (var grp in clusterGroups)
    {
        var clusterType  = grp.Key;
        var clusterCoins = grp.Select(nc => nc.Cd).ToList();
        string clFile    = CoinClusterHelper.GenoFile(clusterType);
        string clLabel   = CoinClusterHelper.Label(clusterType);
        Console.WriteLine($"\n  [{clLabel}] {clusterCoins.Count} coins:");
        foreach (var nc in grp) Console.Write($"    {nc.Sym}");
        Console.WriteLine();

        if (clusterCoins.Count < 4)
        {
            Console.WriteLine($"  ⚠ Too few coins — skipping cluster GA, universal genotype will cover this cluster");
            File.WriteAllText(clFile, JsonSerializer.Serialize(FadeShortGenotypeDto.From(best),
                new JsonSerializerOptions { WriteIndented = true }));
            continue;
        }

        FadeShortGenotype? clusterSeed = File.Exists(clFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype() is { Fitness: > 0 } prev ? prev : best
            : best;   // first run: seed cluster from universal

        var clusterBest = new FadeShortGA(60, 100, verbose: false).Run(clusterCoins, clusterSeed);
        Console.WriteLine($"  [{clLabel}] best: {clusterBest}");
        File.WriteAllText(clFile, JsonSerializer.Serialize(FadeShortGenotypeDto.From(clusterBest),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {clFile}");
    }

    // Overfit check
    Console.WriteLine("\n─── Overfit check (train 75% vs val 12.5%) ───");
    var tRet = coinData.SelectMany(cd =>
        FadeShortSimulator.GetFadeShortReturns(best, cd.TrainCandles).Select(t => t.Return)).ToList();
    var vRet = coinData.SelectMany(cd =>
        FadeShortSimulator.GetFadeShortReturns(best, cd.ValCandles).Select(t => t.Return)).ToList();

    int tCC = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
    int vCC = coinData.Sum(cd => cd.ValCandles.Length) * 12;
    PrintSplitStats("Train 75%", tRet, tCC);
    PrintSplitStats("Val  12.5%", vRet, vCC);

    double vExp = vRet.Count > 0 ? vRet.Average() : 0;
    double tExp = tRet.Count > 0 ? tRet.Average() : 0;
    Console.WriteLine(vExp < tExp * 0.4 || vExp <= 0
        ? "\n  !! Possible overfit — val expectancy < 40% of train"
        : "\n  OK — val expectancy within acceptable range");

    // OOS test (last 12.5% — never seen by the GA; this is what backtest evaluates)
    Console.WriteLine("\n─── OOS test (last 12.5% — not seen by GA) ───");
    var oosRet = namedCoins
        .Where(nc => testBySymbol.ContainsKey(nc.Sym))
        .SelectMany(nc => FadeShortSimulator.GetFadeShortReturns(best, testBySymbol[nc.Sym]).Select(t => t.Return))
        .ToList();
    int oosCC = namedCoins
        .Where(nc => testBySymbol.ContainsKey(nc.Sym))
        .Sum(nc => testBySymbol[nc.Sym].Length * 12);
    PrintSplitStats("OOS  12.5%", oosRet, oosCC);

    // ── Double validation: coin-level holdout ─────────────────────────────────
    if (heldOutCoins.Count > 0)
    {
        Console.WriteLine($"\n─── Double validation ({heldOutCoins.Count} held-out coins — never seen during training) ───");
        Console.WriteLine($"  {"Coin",-20}  {"Trades",6}  {"WR",5}  {"AvgRet",8}  {"PF",6}  {"Sortino",8}");
        Console.WriteLine($"  {"────",-20}  {"──────",6}  {"──",5}  {"──────",8}  {"──",6}  {"───────",8}");

        var allHeldRet = new List<double>();
        int allHeldCC  = 0;
        foreach (var (sym, h1) in heldOutCoins)
        {
            var rets = FadeShortSimulator.GetFadeShortReturns(best, h1).Select(t => t.Return).ToList();
            allHeldRet.AddRange(rets);
            allHeldCC += h1.Length * 12;

            if (rets.Count == 0) { Console.WriteLine($"  {sym,-20}  no trades"); continue; }
            double exp  = rets.Average();
            double pf   = Simulator.ProfitFactor(rets);
            double wr   = rets.Count(r => r > 0) / (double)rets.Count * 100.0;
            double sort = Simulator.SortinoRatio(rets, h1.Length * 12);
            Console.WriteLine($"  {sym,-20}  {rets.Count,6}  {wr,4:F0}%  {exp,+7:F2}%  {pf,6:F2}  {sort,8:F2}");
        }

        Console.WriteLine();
        int heldCC = allHeldCC;
        PrintSplitStats("Held-out coins", allHeldRet, heldCC);

        double hExp = allHeldRet.Count > 0 ? allHeldRet.Average() : 0;
        bool coinOverfit = hExp < tExp * 0.3 || hExp <= 0;
        Console.WriteLine(coinOverfit
            ? "\n  !! Coin-level overfit — held-out expectancy < 30% of train"
            : "\n  OK — held-out coins show positive expectancy");
    }

    Console.WriteLine($"\nNext: dotnet run -- backtest   ← verify on {BacktestCoins.Length} coins (1h/15m dual-TF)");
}

// ══════════════════════════════════════════════════════════════════════════════
//  BACKTEST — Swing backtest on 93 coins, val 20%, 1h setup + 15m entry/exit
// ══════════════════════════════════════════════════════════════════════════════
async Task RunBacktest()
{
    Console.WriteLine($"=== Gravity-gen2 | BACKTEST (~3yr, {BacktestCoins.Length} coins, 1h setup + 15m entry/exit, test 12.5%) ===\n");

    if (!File.Exists(FadeShortGenoFile))
    {
        Console.WriteLine($"No genotype at '{FadeShortGenoFile}'. Run 'dotnet run -- train' first.");
        return;
    }
    var gUniversal = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
    Console.WriteLine($"Universal genotype: {gUniversal}\n");

    // Load cluster genotypes (fall back to universal if a cluster file is missing or not yet trained)
    var clusterGenos = new Dictionary<CoinCluster, FadeShortGenotype>();
    foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
    {
        string clFile = CoinClusterHelper.GenoFile(cl);
        clusterGenos[cl] = File.Exists(clFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
            : gUniversal;
        Console.WriteLine($"  [{CoinClusterHelper.Label(cl)}] genotype: {clusterGenos[cl]}");
    }
    Console.WriteLine();

    // Load grid genotype (optional — backtest still works without it)
    GridGenotype? gridG = null;
    if (File.Exists(GridGenoFile))
    {
        gridG = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype();
        Console.WriteLine($"Grid genotype:     {gridG}\n");
    }
    else Console.WriteLine($"  (no grid genotype — run 'dotnet run -- gridtrain' to include grid)\n");

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
    var gridTrades    = new List<(string Coin, DateTime Time, double Return, double CoinConf)>();
    var coinStats     = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
    var gridCoinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
    int totalVCC      = 0;
    int gridTotalVCC  = 0;

    Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine(new string('-', 82));

    foreach (var (sym, m15List) in fetchedArr)
    {
        if (m15List.Count < 600) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

        var m15 = m15List.ToArray();
        var h1  = FadeShortSimulator.AggregateCandles(m15, 4);

        // Volume filter — same floor as training screen
        {
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < MinMedianVolUsdM)
            {
                Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${MinMedianVolUsdM:F1}M)");
                continue;
            }
        }

        // Select the cluster genotype for this coin
        var coinCluster = CoinClusterHelper.Classify(h1);
        var g = clusterGenos[coinCluster];

        int h1Split  = (int)(h1.Length * 0.875);
        int m15Split = h1Split * 4;
        var h1Train  = h1[..h1Split];
        var h1Val    = h1[h1Split..];
        var m15Train = m15[..m15Split];
        var m15Val   = m15[m15Split..];

        var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
        var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
        var tRet  = FadeShortSimulator.GetFadeShortReturns(g, screenH1, screenM15).Select(t => t.Return).ToList();
        double tExp  = tRet.Count >= 5 ? tRet.Average() : double.NegativeInfinity;
        double tSort = tRet.Count >= 5 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12) : double.NegativeInfinity;
        double tPF   = tRet.Count >= 5 ? Simulator.ProfitFactor(tRet) : 0;
        if (tExp <= 0 || tSort < 0.3 || tPF < 1.1)
        {
            Console.WriteLine($"  {sym,-16}  [{CoinClusterHelper.Label(coinCluster)}]  skip (exp={tExp:+0.00;-0.00}% sort={tSort:F2} pf={tPF:F2})");
            continue;
        }

        double coinConf = Simulator.ComputeConfidence(tRet);

        var vTrades = FadeShortSimulator.GetFadeShortReturns(g, h1Val, m15Val);
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

        // Grid (h1 only — runs on same val slice)
        if (gridG != null)
        {
            var gtRet = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
            double gtExp = gtRet.Count >= 5 ? gtRet.Average() : double.NegativeInfinity;
            double gtPF  = gtRet.Count >= 5 ? Simulator.ProfitFactor(gtRet) : 0;
            if (gtExp > 0 && gtPF >= 1.15)
            {
                double gConf = Simulator.ComputeConfidence(gtRet);
                var    gvTr  = GridSimulator.GetGridReturns(gridG, h1Val);
                var    gvRet = gvTr.Select(t => t.Return).ToList();
                gridTotalVCC += vCC;
                double gSh   = Simulator.SharpeRatio(gvRet, vCC);
                double gSort = Simulator.SortinoRatio(gvRet, vCC);
                double gPf   = Simulator.ProfitFactor(gvRet);
                double gWr   = gvRet.Count > 0 ? (double)gvRet.Count(r => r > 0) / gvRet.Count : 0;
                double gAvg  = gvRet.Count > 0 ? gvRet.Average() : 0;
                foreach (var (t, ret, _) in gvTr)
                    gridTrades.Add((sym, t, ret, gConf));
                gridCoinStats.Add((sym, gSh, gSort, gPf, gvRet.Count, gWr, gAvg, gConf));
            }
        }
    }

    if (allTrades.Count == 0 && gridTrades.Count == 0) { Console.WriteLine("No trades."); return; }

    allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
    gridTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
    var allRet     = allTrades.Select(t => t.Return).ToList();
    double portSharpe  = Simulator.SharpeRatio(allRet, totalVCC);
    double portSortino = Simulator.SortinoRatio(allRet, totalVCC);
    double portPf      = Simulator.ProfitFactor(allRet);
    double portCalmar  = Simulator.CalmarRatio(allRet);
    int    totalWins   = allRet.Count(r => r > 0);

    var tradesWithConf = allTrades.Select(t => (t.Return, t.CoinConf)).ToList();
    var port = Simulator.SimulatePortfolio(tradesWithConf);

    Console.WriteLine($"\n{new string('═', 70)}");
    Console.WriteLine($"  BACKTEST SUMMARY  (all coins, test 12.5%, 1h setup + 15m exec)");
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

    // ── Grid summary ──────────────────────────────────────────────────────────
    if (gridTrades.Count > 0)
    {
        var gAllRet   = gridTrades.Select(t => t.Return).ToList();
        int gWins     = gAllRet.Count(r => r > 0);
        var gPort     = Simulator.SimulatePortfolio(gridTrades.Select(t => (t.Return, t.CoinConf)).ToList());

        Console.WriteLine();
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  GRID SUMMARY  (test 12.5%, 1h candles)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  Total trades: {gAllRet.Count}  ({gWins}W / {gAllRet.Count - gWins}L)");
        Console.WriteLine($"  Win rate:     {(double)gWins / gAllRet.Count:P1}");
        Console.WriteLine($"  Avg return:   {gAllRet.Average():+0.00}%");
        Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(gAllRet, gridTotalVCC):F2}");
        Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(gAllRet, gridTotalVCC):F2}");
        Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(gAllRet):F2}");
        Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(gAllRet):F2}");
        Console.WriteLine();
        Console.WriteLine($"  ── Portfolio sim ──");
        Console.WriteLine($"    End balance:  €{gPort.EndBalance:F2}  ({(gPort.EndBalance - gPort.StartBalance) / gPort.StartBalance * 100:+0.0;-0.0}%)");
        Console.WriteLine($"    Max drawdown: {gPort.MaxDrawdownPct:F1}%");
        Console.WriteLine();
        Console.WriteLine($"  Per-coin (sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in gridCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        // Combined FadeShort + Grid portfolio
        var combined = allTrades.Select(t => (t.Time, t.Return, t.CoinConf, "fs"))
            .Concat(gridTrades.Select(t => (t.Time, t.Return, t.CoinConf, "grid")))
            .OrderBy(t => t.Time)
            .Select(t => (t.Return, t.CoinConf))
            .ToList();
        var combPort = Simulator.SimulatePortfolio(combined);
        Console.WriteLine();
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  COMBINED (FadeShort + Grid) — €100 start, half-Kelly, 5% cap");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"    End balance:  €{combPort.EndBalance:F2}  ({(combPort.EndBalance - combPort.StartBalance) / combPort.StartBalance * 100:+0.0;-0.0}%)");
        Console.WriteLine($"    Max drawdown: {combPort.MaxDrawdownPct:F1}%");
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  YEARLY BREAKDOWN — per-calendar-year portfolio returns, full history
//  Note: 2021–2025 is in-sample for the genotype. Use to gauge year-to-year
//  variance, not as an unbiased performance estimate.
// ══════════════════════════════════════════════════════════════════════════════
async Task RunYearlyBreakdown()
{
    Console.WriteLine("=== Gravity-gen2 | YEARLY BREAKDOWN (full history, in-sample 2021-2025) ===\n");

    if (!File.Exists(FadeShortGenoFile))
    { Console.WriteLine("No FadeShort genotype — run train first."); return; }

    var gUniversal = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
    var clusterGenos = new Dictionary<CoinCluster, FadeShortGenotype>();
    foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
    {
        string clFile = CoinClusterHelper.GenoFile(cl);
        clusterGenos[cl] = File.Exists(clFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
            : gUniversal;
    }

    GridGenotype? gridG = File.Exists(GridGenoFile)
        ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype()
        : null;

    Console.WriteLine($"  Fetching {BacktestCoins.Length} coins...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = BacktestCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try { var m15 = await FetchFifteenMinCandlesCached(sym, batches: 113); return (sym, m15); }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);
    Console.WriteLine();

    // Collect all trades with timestamps across full history
    // Trades carry: timestamp (for concurrent overlap), conf (half-Kelly), riskCap (max
    // position so a worst-case stop-out never costs more than 1% of portfolio).
    const double RiskBudget = 0.004;  // 0.4% of portfolio per trade
    const double FsHoldHours   = 48;  // avg FadeShort hold (max=84h, exits early via TP/stop)
    const double GridHoldHours = 36;  // avg Grid hold (max=69h, grid cycles faster)

    var allTrades  = new List<(DateTime Time, double Return, double Conf, double RiskCap)>();
    var gridTrades = new List<(DateTime Time, double Return, double Conf, double RiskCap)>();
    var coinDiag   = new List<(string Sym, int Trades, double WL, double RC, double FullKPos)>();

    foreach (var (sym, m15List) in fetched)
    {
        if (m15List.Count < 600) continue;
        var m15 = m15List.ToArray();
        var h1  = FadeShortSimulator.AggregateCandles(m15, 4);

        var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
        double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
        if (medVol < MinMedianVolUsdM) continue;

        var coinCluster = CoinClusterHelper.Classify(h1);
        var g = clusterGenos[coinCluster];

        int trainEnd = (int)(h1.Length * 0.75);
        var trainRet = FadeShortSimulator.GetFadeShortReturns(g, h1[..trainEnd], m15[..(trainEnd * 4)])
                           .Select(t => t.Return).ToList();
        if (trainRet.Count < 10) continue;
        if (trainRet.Average() <= 0 || Simulator.ProfitFactor(trainRet) < 1.1) continue;
        double conf = Simulator.ComputeConfidence(trainRet);

        // Worst-case loss per unit of position: 99th-pct of |return| on all losing trades.
        // This is what a bad stop-out looks like. RiskCap = budget / worstLoss caps
        // the position so even the worst trade only costs 1% of portfolio.
        var fullTrades = FadeShortSimulator.GetFadeShortReturns(g, h1, m15);
        var losses = fullTrades.Where(t => t.Return < 0)
                               .Select(t => Math.Abs(t.Return) / 100.0)
                               .OrderBy(x => x).ToList();
        double worstLoss = losses.Count >= 5
            ? losses[Math.Min(losses.Count - 1, (int)(losses.Count * 0.99))]
            : 0.05;
        worstLoss = Math.Max(worstLoss, 0.005);
        double riskCap = RiskBudget / worstLoss;

        coinDiag.Add((sym, fullTrades.Count, worstLoss, riskCap, Math.Min(conf * 2.0, riskCap)));

        foreach (var (t, ret, _) in fullTrades)
            allTrades.Add((t, ret, conf, riskCap));

        if (gridG != null)
        {
            var gtTrain = GridSimulator.GetGridReturns(gridG, h1[..trainEnd]).Select(t => t.Return).ToList();
            if (gtTrain.Count >= 5 && gtTrain.Average() > 0 && Simulator.ProfitFactor(gtTrain) >= 1.15)
            {
                double gConf = Simulator.ComputeConfidence(gtTrain);
                var gLoss = GridSimulator.GetGridReturns(gridG, h1)
                    .Where(t => t.Return < 0).Select(t => Math.Abs(t.Return) / 100.0).OrderBy(x => x).ToList();
                double gWorstLoss = gLoss.Count >= 5
                    ? gLoss[Math.Min(gLoss.Count - 1, (int)(gLoss.Count * 0.99))] : 0.05;
                gWorstLoss = Math.Max(gWorstLoss, 0.005);
                double gRiskCap = RiskBudget / gWorstLoss;
                foreach (var (t, ret, _) in GridSimulator.GetGridReturns(gridG, h1))
                    gridTrades.Add((t, ret, gConf, gRiskCap));
            }
        }
    }

    if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

    var years     = allTrades.Select(t => t.Time.Year)
                    .Concat(gridTrades.Select(t => t.Time.Year))
                    .Distinct().OrderBy(y => y).ToList();
    var fullYears = years.Where(y => y >= 2022 && y <= 2025).ToList();
    var fsHold    = TimeSpan.FromHours(FsHoldHours);
    var gridHold  = TimeSpan.FromHours(GridHoldHours);

    // ── Per-year table: current (sequential ½-K) vs live (concurrent full-K risk-capped) ─
    Console.WriteLine($"  {"Year",-6}  {"Trades",6}  {"½-K seq ret",11}  {"½-K seq DD",10}  {"Full-K live ret",15}  {"Full-K live DD",14}");
    Console.WriteLine($"  {new string('-', 78)}");

    foreach (int yr in years)
    {
        var yrSeq = allTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))
            .Concat(gridTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))).ToList();

        var yrLive = allTrades.Where(t => t.Time.Year == yr)
            .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, fsHold))
            .Concat(gridTrades.Where(t => t.Time.Year == yr)
                .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, gridHold)))
            .OrderBy(t => t.Time).ToList();

        var seqPort  = Simulator.SimulatePortfolio(yrSeq, startBalance: 100.0, maxPositionPct: 0.05);
        var livePort = Simulator.SimulatePortfolioExposureCapped(yrLive, startBalance: 100.0,
            drawdownBrakeAt: 0.15, kellyMultiplier: 2.0);

        int n = yrSeq.Count;
        Console.WriteLine($"  {yr,-6}  {n,6}  {seqPort.EndBalance-100,+10:F1}%  {seqPort.MaxDrawdownPct,9:F1}%  {livePort.EndBalance-100,+14:F1}%  {livePort.MaxDrawdownPct,13:F1}%");
    }

    Console.WriteLine($"  {new string('-', 78)}");

    // ── Full-history scenario comparison ─────────────────────────────────────
    var allLive = allTrades.Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, fsHold))
        .Concat(gridTrades.Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, gridHold)))
        .OrderBy(t => t.Time).ToList();
    var allSeq = allTrades.Select(t => (t.Return, t.Conf))
        .Concat(gridTrades.Select(t => (t.Return, t.Conf))).ToList();

    Console.WriteLine($"\n{new string('═', 72)}");
    Console.WriteLine($"  SIZING SCENARIOS  (full 2021–2026, in-sample)");
    Console.WriteLine($"  Concurrent = overlapping positions tracked; brake = size→20% floor at 15% DD");
    Console.WriteLine($"  RiskCap = position capped so worst stop-out ≤ 0.4% of portfolio");
    Console.WriteLine($"{new string('═', 72)}");
    Console.WriteLine($"  {"Scenario",-38}  {"Total",6}  {"Max DD",7}  {"Worst yr",9}  {"Best yr",8}");
    Console.WriteLine($"  {new string('-', 72)}");

    void PrintScenario(string label,
        Func<List<(double Return, double Conf)>, Simulator.PortfolioResult> seqFn,
        Func<List<(DateTime Time, double Return, double Conf, double RiskCap, TimeSpan Hold)>, Simulator.PortfolioResult>? liveFn)
    {
        Simulator.PortfolioResult full = liveFn != null ? liveFn(allLive) : seqFn(allSeq);
        var yrRets = fullYears.Select(yr =>
        {
            var ys = allTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))
                .Concat(gridTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))).ToList();
            var yl = allTrades.Where(t => t.Time.Year == yr)
                .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, fsHold))
                .Concat(gridTrades.Where(t => t.Time.Year == yr)
                    .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, gridHold)))
                .OrderBy(t => t.Time).ToList();
            return liveFn != null ? liveFn(yl).EndBalance - 100.0 : seqFn(ys).EndBalance - 100.0;
        }).ToList();
        double worst = yrRets.Count > 0 ? yrRets.Min() : 0;
        double best  = yrRets.Count > 0 ? yrRets.Max() : 0;
        Console.WriteLine($"  {label,-38}  {full.EndBalance-100,+5:F1}%  {full.MaxDrawdownPct,6:F1}%  {worst,+8:F1}%  {best,+7:F1}%");
    }

    // Sequential reference (current backtest model)
    PrintScenario("½-K  seq  5%cap  no-brake  [current]",
        t => Simulator.SimulatePortfolio(t, maxPositionPct: 0.05), null);

    // Concurrent, half-Kelly, brake — base live model
    PrintScenario("½-K  concurrent  brake@15%",
        _ => default!, t => Simulator.SimulatePortfolioExposureCapped(t,
            drawdownBrakeAt: 0.15, kellyMultiplier: 1.0));

    // Concurrent, full-Kelly, NO risk-cap, brake — shows raw full-Kelly danger
    PrintScenario("Full-K  concurrent  brake@15%  no-riskcap",
        _ => default!, t => Simulator.SimulatePortfolioExposureCapped(
            t.Select(x => (x.Time, x.Return, x.Conf, x.Hold)).ToList(),
            drawdownBrakeAt: 0.15, kellyMultiplier: 2.0));

    // Concurrent, full-Kelly, risk-cap 1%, brake — recommended live scenario
    PrintScenario("Full-K  concurrent  brake@15%  riskcap0.4%  [live]",
        _ => default!, t => Simulator.SimulatePortfolioExposureCapped(t,
            drawdownBrakeAt: 0.15, kellyMultiplier: 2.0));

    Console.WriteLine($"\n  RiskCap per coin: worst 1% of stop-outs limits position so max loss = 0.4% of portfolio.");
    Console.WriteLine($"  Concurrent: positions overlap in time (48h avg FS hold, 36h avg grid hold).");
    Console.WriteLine($"  In-sample: 2021–2025 trained. OOS test = Oct 2025–Jun 2026.");

    // ── Per-coin risk cap diagnostics ────────────────────────────────────────
    Console.WriteLine($"\n  {"Coin",-10}  {"FS trades",9}  {"WorstLoss",9}  {"RiskCap",8}  {"FullK pos",9}  {"Cap binds?",10}");
    Console.WriteLine($"  {new string('-', 66)}");
    foreach (var (sym, trades, wl, rc, fkPos) in coinDiag.OrderBy(d => d.Sym))
        Console.WriteLine($"  {sym,-10}  {trades,9}  {wl*100,8:F1}%  {rc*100,7:F1}%  {fkPos*100,8:F1}%  {(rc < fkPos * 1.001 ? "YES — cap binds" : "no"),10}");
}

// ══════════════════════════════════════════════════════════════════════════════
//  PAPER TRADE — live swing signals on 4h candles, refreshes every 4h
// ══════════════════════════════════════════════════════════════════════════════
async Task RunPaperTrade()
{
    Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (4h candles) — Ctrl+C to stop ===\n");

    if (!File.Exists(FadeShortGenoFile))
    {
        Console.WriteLine($"No genotype at '{FadeShortGenoFile}'. Run 'dotnet run -- train' first.");
        return;
    }
    var gUniversalPt = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
    Console.WriteLine($"Universal genotype: {gUniversalPt}");

    var clusterGenosPt = new Dictionary<CoinCluster, FadeShortGenotype>();
    foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
    {
        string clFile = CoinClusterHelper.GenoFile(cl);
        clusterGenosPt[cl] = File.Exists(clFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
            : gUniversalPt;
        Console.WriteLine($"  [{CoinClusterHelper.Label(cl)}] {clusterGenosPt[cl]}");
    }
    Console.WriteLine();

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

            double px       = candles[^1].Close;
            var    coinCl   = CoinClusterHelper.ClassifyByName(sym);
            var    gForCoin = clusterGenosPt[coinCl];
            var    st       = FadeShortSimulator.GetFadeShortTradeState(gForCoin, candles.ToArray());

            string stateStr  = st.InTrade
                ? (st.TrailArmed ? "TRAIL ARMED" : $"SHORT b{st.HoldCount}")
                : "watching";
            string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
            string unreal    = st.InTrade
                ? $"{(st.Entry - px) / st.Entry * 100.0:+0.00}%"
                : "—";
            double effStop   = st.InTrade ? Math.Min(st.HardStop, st.MaeStop) : 0;
            string stopStr   = st.InTrade ? $"{effStop:F4}" : "—";
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
    Console.WriteLine($"  {label,-12} Sh={sh:F2}  Sort={sort:F2}  PF={pf:F2}  WR={wr:P0}  Tr={r.Count}  Avg={avg:+0.00;-0.00}%");
}

// ══════════════════════════════════════════════════════════════════════════════
//  TEST — statistical edge validation for both strategies on training coins
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTest()
{
    Console.WriteLine("=== Gravity-gen2 | STATISTICAL TEST (swing + grid, 26 training coins, val 20%) ===\n");

    // Load genotypes
    FadeShortGenotype? sg = null;
    GridGenotype?  gg = null;
    if (File.Exists(FadeShortGenoFile))
        sg = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
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
        var h1   = FadeShortSimulator.AggregateCandles(m15, 4);
        int split = (int)(h1.Length * 0.8);
        int m15Split = split * 4;
        var h1Val  = h1[split..];
        var m15Val = m15[m15Split..];
        totalH1Val += h1Val.Length;

        if (sg != null)
            swingRet.AddRange(FadeShortSimulator.GetFadeShortReturns(sg, h1Val, m15Val).Select(t => t.Return));
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

    // ── Crash stress test ─────────────────────────────────────────────────────
    Console.WriteLine("\n  Building full-history trade list for crash analysis...");

    var crashTrades = new List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)>();

    foreach (var (sym, m15List) in fetched)
    {
        if (m15List.Count < 600) continue;
        var m15   = m15List.ToArray();
        var h1    = FadeShortSimulator.AggregateCandles(m15, 4);
        int split = (int)(h1.Length * 0.8);
        var h1Train  = h1[..split];
        var m15Train = m15[..(split * 4)];

        if (sg != null)
        {
            var trainRets = FadeShortSimulator.GetFadeShortReturns(sg, h1Train, m15Train).Select(t => t.Return).ToList();
            var (_, hk) = StrategyStats.KellyFraction(trainRets);
            double swingHk = Math.Min(hk, 0.05);
            foreach (var (t, ret, _) in FadeShortSimulator.GetFadeShortReturns(sg, h1, m15))
                crashTrades.Add((t - TimeSpan.FromHours(sg.MaxHoldCandles), t, ret, swingHk, "swing"));
        }

        if (gg != null)
        {
            var trainRets = GridSimulator.GetGridReturns(gg, h1Train).Select(t => t.Return).ToList();
            var (_, hk) = StrategyStats.KellyFraction(trainRets);
            double gridHk = Math.Min(hk, 0.05);
            foreach (var (t, ret, _) in GridSimulator.GetGridReturns(gg, h1))
                crashTrades.Add((t - TimeSpan.FromHours(gg.MaxHoldCandles), t, ret, gridHk, "grid"));
        }
    }

    Console.WriteLine($"  Total trades for crash analysis: {crashTrades.Count}  (swing + grid, full history)");

    Console.WriteLine("  Fetching BTCUSDT for crash detection...");
    var btcM15 = await FetchFifteenMinCandlesCached("BTCUSDT", batches: 113);
    var btcH1  = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);

    var crashes = CrashAnalyser.DetectCrashes(btcH1);
    CrashAnalyser.Report(crashes, crashTrades);

    var rallies = CrashAnalyser.DetectRallies(btcH1);
    CrashAnalyser.ReportRallies(rallies, crashTrades);

    CrashAnalyser.SyntheticWorstCase(crashTrades);
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

    // Load swing genotype to enforce regime partition: grid ADX ceiling = swing threshold − 1.
    double adxCeiling = 20.0;
    if (File.Exists(FadeShortGenoFile))
    {
        var swingGeno = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
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
//  GRID BACKTEST — 93 coins, val 20%, 1h candles
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

    if (!File.Exists(FadeShortGenoFile))     { Console.WriteLine($"Missing swing genotype — run 'train' first.");     return; }
    if (!File.Exists(GridGenoFile)) { Console.WriteLine($"Missing grid genotype — run 'gridtrain' first."); return; }

    var swingG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
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
            var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
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

        {
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < MinMedianVolUsdM)
            {
                Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${MinMedianVolUsdM:F1}M)");
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
        if (tExp <= 0 || tSort < 0.3 || tPF < 1.1)
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

        {
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < MinMedianVolUsdM)
            {
                Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${MinMedianVolUsdM:F1}M)");
                continue;
            }
        }

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

// ══════════════════════════════════════════════════════════════════════════════
//  RANKED BACKTEST — top-N signal selection by quality, fixed 5% sizing
// ══════════════════════════════════════════════════════════════════════════════
async Task RunRankedBacktest()
{
    const int MaxSwing = 5;
    const int MaxGrid  = 5;
    const double PosSizePct = 0.05;

    Console.WriteLine($"=== Gravity-gen2 | RANKED BACKTEST (max {MaxSwing} swing · {MaxGrid} grid · {PosSizePct:P0}/pos) ===\n");

    if (!File.Exists(FadeShortGenoFile))     { Console.WriteLine("Missing swing genotype — run 'train' first.");     return; }
    if (!File.Exists(GridGenoFile)) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first."); return; }

    var swingG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(FadeShortGenoFile))!.ToGenotype();
    var gridG  = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(GridGenoFile))!.ToGenotype();
    Console.WriteLine($"Swing: {swingG}");
    Console.WriteLine($"Grid:  {gridG}\n");

    Console.WriteLine($"  Fetching {BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = BacktestCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try
        {
            var m15 = await FetchFifteenMinCandlesCached(sym, batches: 113);
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
        // Volume filter
        {
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < MinMedianVolUsdM) continue;
        }

        int h1Split  = (int)(h1.Length * 0.8);
        int m15Split = h1Split * 4;
        var h1Train  = h1[..h1Split];
        var h1Val    = h1[h1Split..];
        var m15Train = m15[..m15Split];
        var m15Val   = m15[m15Split..];

        // ── Swing ─────────────────────────────────────────────────────────────
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

        // ── Grid ──────────────────────────────────────────────────────────────
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

    // Val window for annualisation
    var sortedCands  = allCandidates.OrderBy(t => t.Entry).ToList();
    double valDays   = allCandidates.Count >= 2
                     ? (sortedCands[^1].Exit - sortedCands[0].Entry).TotalDays
                     : 1;
    double annFactor = valDays > 0 ? 365.0 / valDays : 1.0;
    int    valH1     = (int)(valDays * 24);  // approximate h1 candles for Sharpe normalisation

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

    // Unranked baseline: all signals, unlimited concurrent, same 5% fixed sizing
    var unranked = RankedPortfolioSim.Run(allCandidates, maxSwing: 9999, maxGrid: 9999, PosSizePct);
    // Ranked: capacity-gated
    var ranked   = RankedPortfolioSim.Run(allCandidates, MaxSwing, MaxGrid, PosSizePct);

    Console.WriteLine($"══════════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  RANKED BACKTEST SUMMARY  (val 20% · {PosSizePct:P0} fixed per position · {valDays:F0} val days)");
    Console.WriteLine($"══════════════════════════════════════════════════════════════════════");
    Console.WriteLine();
    PrintPort($"Unranked baseline (all {allCandidates.Count} signals, unlimited concurrent)", unranked);
    PrintPort($"Ranked (max {MaxSwing} swing + {MaxGrid} grid concurrent)", ranked);
}
// ── Type declarations ─────────────────────────────────────────────────────────

class FadeShortGenotypeDto
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
    public double MaeAtrMult                { get; set; }
    public double TakeProfitAtrMult         { get; set; }
    public double TrailingActivationAtrMult { get; set; }
    public double TrailingStopAtrMult       { get; set; }
    public int    MaxHoldCandles            { get; set; }
    public double PositionSizePct           { get; set; }
    public double Fitness                   { get; set; }

    public static FadeShortGenotypeDto From(FadeShortGenotype g) => new()
    {
        EmaPeriod      = g.EmaPeriod,
        RsiPeriod      = 7,
        AdxPeriod      = 7,
        AdxThreshold   = g.AdxThreshold,
        LookbackCandles  = g.LookbackCandles,
        RsiOverbought    = g.RsiOverbought,
        RsiDivThreshold  = g.RsiDivThreshold,
        MinRallyAtrMult  = g.MinRallyAtrMult,
        StopLossAtrMult           = g.StopLossAtrMult,
        MaeAtrMult                = g.MaeAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        Fitness                   = g.Fitness,
    };

    public FadeShortGenotype ToGenotype() => new FadeShortGenotype
    {
        EmaPeriod        = EmaPeriod,
        AdxThreshold     = AdxThreshold,
        LookbackCandles  = LookbackCandles  > 0 ? LookbackCandles  : 15,
        RsiOverbought    = RsiOverbought    > 0 ? RsiOverbought    : 68.0,
        RsiDivThreshold  = RsiDivThreshold  > 0 ? RsiDivThreshold  : 8.0,
        MinRallyAtrMult  = MinRallyAtrMult  > 0 ? MinRallyAtrMult  : 5.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? Math.Min(StopLossAtrMult, 2.0) : 0.8,
        MaeAtrMult                = MaeAtrMult                > 0 ? MaeAtrMult                : 2.5,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 4.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 3.0,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 1.5,
        MaxHoldCandles            = MaxHoldCandles            > 0 ? MaxHoldCandles            : 42,
        PositionSizePct           = PositionSizePct           > 0 ? PositionSizePct           : 0.03,
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
        AdxThreshold      = g.AdxThreshold,
        BbPeriod          = g.BbPeriod,
        BbWidthMaxPct     = g.BbWidthMaxPct,
        EmaPeriod         = g.EmaPeriod,
        GridStepAtrMult   = g.GridStepAtrMult,
        GridLevels        = g.GridLevels,
        TakeProfitAtrMult = g.TakeProfitAtrMult,
        HardStopAtrMult   = g.HardStopAtrMult,
        MaxHoldCandles    = g.MaxHoldCandles,
        Fitness           = g.Fitness,
    };

    public GridGenotype ToGenotype() => new GridGenotype
    {
        AdxThreshold      = AdxThreshold      > 0 ? AdxThreshold      : 16.0,
        BbPeriod          = BbPeriod          > 0 ? BbPeriod          : 20,
        BbWidthMaxPct     = BbWidthMaxPct     > 0 ? BbWidthMaxPct     : 1.8,
        EmaPeriod         = EmaPeriod         > 0 ? EmaPeriod         : 50,
        GridStepAtrMult   = GridStepAtrMult   > 0 ? GridStepAtrMult   : 0.8,
        GridLevels        = GridLevels        > 0 ? GridLevels        : 2,
        TakeProfitAtrMult = TakeProfitAtrMult > 0 ? TakeProfitAtrMult : 1.5,
        HardStopAtrMult   = HardStopAtrMult   > 0 ? HardStopAtrMult   : 2.2,
        MaxHoldCandles    = MaxHoldCandles    > 0 ? MaxHoldCandles    : 96,
        Fitness           = Fitness,
    }.ClampToBounds();
}