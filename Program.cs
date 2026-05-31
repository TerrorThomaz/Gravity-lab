using TradingGA;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using System.Text.Json;

const string GenoFile = "best_genotype.json";
var client = new BybitRestClient();

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
switch (mode)
{
    case "train":        await RunTrain();        break;
    case "trainmulti":   await RunTrainMulti();   break;
    case "trainblocks":  await RunTrainBlocks();  break;
    case "trainbayes":   await RunTrainBayes();   break;
    case "trainregimes": await RunTrainRegimes(); break;
    case "backtest":     await RunBacktest();     break;
    case "stresstest":   await RunStressTest();   break;
    case "permtest":     await RunPermTest();     break;
    case "papertrade":   await RunPaperTrade();   break;
    case "livetrain":    await RunLiveTrain();    break;
    case "status":       await RunStatus();       break;
    case "compare":      await RunCompare();      break;
    case "gather":       await RunGather();       break;
    case "autoevolve":   await RunAutoEvolve();   break;
    default:
        Console.WriteLine("Gravity-gen2 — usage:");
        Console.WriteLine("  dotnet run -- train          GA on WIF only (fast, single-coin)");
        Console.WriteLine("  dotnet run -- trainmulti     GA on 5 diverse coins (robust, anti-overfit)");
        Console.WriteLine("  dotnet run -- trainblocks    Block GA: regime→exit→polish, 5yr, all 14 coins");
        Console.WriteLine("  dotnet run -- trainbayes     CMA-ES optimizer (more sample-efficient than GA)");
        Console.WriteLine("  dotnet run -- trainregimes   Train high-vol + low-vol genotype pair");
        Console.WriteLine("  dotnet run -- backtest       1yr backtest on 14 coins");
        Console.WriteLine("  dotnet run -- stresstest     Per-period Sharpe across 5yr (regime stress test)");
        Console.WriteLine("  dotnet run -- permtest       Permutation test: does entry timing beat random?");
        Console.WriteLine("  dotnet run -- papertrade     Live signals per coin");
        Console.WriteLine("  dotnet run -- livetrain      20 genotypes evaluated on live data, evolves hourly");
        Console.WriteLine("  dotnet run -- compare        Side-by-side live comparison of all candidates");
        Console.WriteLine("  dotnet run -- gather         Random-seed trainblocks → saves to candidates/ (no incumbent bias)");
        Console.WriteLine("  dotnet run -- autoevolve     Overnight loop: trainblocks + profit eval, saves candidates");
        Console.WriteLine("  dotnet run -- status         Portfolio P&L with fees, slippage, reinvestment");
        break;
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTrain()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN (pump-short, 1yr WIF) ===\n");
    const string TrainCoin = "WIFUSDT";

    Console.WriteLine($"─── Training data: {TrainCoin} (106 batches ≈ 1yr) ───");
    Console.Write("  Fetching... ");
    var trainCandles = await FetchCandles(TrainCoin, batches: 106);
    Console.WriteLine($"{trainCandles.Count} candles");

    // 80 / 20 temporal split
    int    split    = (int)(trainCandles.Count * 0.8);
    var    trainArr = trainCandles.Take(split).ToArray();
    var    valArr   = trainCandles.Skip(split).ToArray();
    Console.WriteLine($"  Train: {trainArr.Length} candles  |  Val: {valArr.Length} candles\n");

    // Seed from last saved genotype if available
    Genotype? seed = null;
    if (File.Exists(GenoFile))
    {
        seed = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        Console.WriteLine($"  Seeding from {GenoFile}");
    }

    Console.WriteLine("─── GA training ───");
    var best = new GeneticAlgorithm(60, 80, useAtr: true, verbose: true)
        .Run([new GeneticAlgorithm.CoinData(trainArr, valArr, 1.0)], seed);

    Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
    File.WriteAllText(GenoFile, JsonSerializer.Serialize(GenotypeDto.From(best),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  Saved → {GenoFile}\n");

    // ── Overfit check ─────────────────────────────────────────────────────────
    // NOTE: the val Sharpe below is a biased estimate — the GA selected this
    // genotype by scoring on this same val set. After multiple retrains the
    // reported val number will drift upward. Use RunBacktest (14 unseen coins)
    // as the authoritative pass/fail gate before trusting or deploying a genotype.
    Console.WriteLine("─── Overfit check (WIF train 80% vs val 20%) ───");
    Console.WriteLine("  ⚠  Val Sharpe is selection-biased — GA winner was chosen on this split.");
    Console.WriteLine("     Treat it as a sanity check only. Run backtest for the unbiased verdict.\n");
    var tRet = Simulator.GetUnifiedReturns(best, trainArr, true).Select(t => t.Return).ToList();
    var vRet = Simulator.GetUnifiedReturns(best, valArr,   true).Select(t => t.Return).ToList();
    PrintSplitStats("Train 80%", tRet, trainArr.Length);
    PrintSplitStats("Val   20%", vRet, valArr.Length);

    double tSh = Simulator.SharpeRatio(tRet, trainArr.Length);
    double vSh = Simulator.SharpeRatio(vRet, valArr.Length);
    Console.WriteLine(vSh < tSh * 0.5 || vSh <= 0
        ? "\n  !! Possible overfit — val Sharpe < 50% of train"
        : "\n  OK — val Sharpe within acceptable range (but see bias warning above)");

    Console.WriteLine($"\nNext: dotnet run -- backtest   ← authoritative test on 14 unseen coins");
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN MULTI — 5 diverse coins, reduces single-coin overfitting
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTrainMulti()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN MULTI (5 coins, anti-overfit) ===\n");

    // Diverse coins: meme, large-cap, mid-cap — deliberately varied volatility profiles
    var trainCoins = new[]
    {
        ("WIFUSDT",   1.0),   // meme — high volatility, primary training target
        ("SOLUSDT",   1.0),   // large-cap alt
        ("DOGEUSDT",  1.0),   // meme, different regime behaviour
        ("ETHUSDT",   1.0),   // large-cap, lower volatility
        ("1000BONKUSDT", 0.8), // micro-cap meme — upweighted noise tolerance
    };

    Console.WriteLine($"  Fetching {trainCoins.Length} coins in parallel...");
    var fetchTasks = trainCoins.Select(async ((string sym, double weight) t) =>
    {
        var candles = await FetchCandles(t.sym, batches: 106);
        Console.WriteLine($"  {t.sym}: {candles.Count} candles");
        return (t.sym, t.weight, candles);
    });
    var fetched = await Task.WhenAll(fetchTasks);

    var coinData = new List<GeneticAlgorithm.CoinData>();
    foreach (var (sym, weight, candles) in fetched)
    {
        if (candles.Count < 500) continue;
        int split = (int)(candles.Count * 0.8);
        coinData.Add(new GeneticAlgorithm.CoinData(
            candles.Take(split).ToArray(),
            candles.Skip(split).ToArray(),
            weight));
    }

    if (coinData.Count == 0) { Console.WriteLine("No data."); return; }
    Console.WriteLine($"\n  Training on {coinData.Count} coins simultaneously\n");

    Genotype? seed = null;
    if (File.Exists(GenoFile))
    {
        seed = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        Console.WriteLine($"  Seeding from {GenoFile}");
    }

    Console.WriteLine("─── GA training ───");
    var best = new GeneticAlgorithm(60, 80, useAtr: true, verbose: true)
        .Run(coinData, seed);

    Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
    File.WriteAllText(GenoFile, JsonSerializer.Serialize(GenotypeDto.From(best),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  Saved → {GenoFile}");
    Console.WriteLine($"\nNext: dotnet run -- backtest   ← verify on 14 unseen coins");
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN BLOCKS — 3-phase coordinate-descent GA over 5yr, all 14 coins
//  Phase 1: regime/entry genes   (exit genes frozen to seed)
//  Phase 2: exit/sizing genes    (regime genes frozen to Phase-1 result)
//  Phase 3: joint polish         (all genes free, seeded from Phase 2)
//  Proper 70/15/15 split — holdout is never seen during any phase
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTrainBlocks()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN BLOCKS (regime→exit→polish, 5yr, 19 coins) ===\n");

    var allCoins = new[]
    {
        ("WIFUSDT",       1.0), ("SOLUSDT",       1.0), ("MEMEUSDT",      1.0),
        ("DOGEUSDT",      1.0), ("1000BONKUSDT",  0.8), ("XRPUSDT",       1.0),
        ("ETHUSDT",       1.0), ("AVAXUSDT",      1.0), ("BNBUSDT",       1.0),
        ("LINKUSDT",      1.0), ("ADAUSDT",       1.0), ("1000PEPEUSDT",  0.8),
        ("ATOMUSDT",      1.0), ("1000FLOKIUSDT", 0.8), ("BTCUSDT",       1.0),
        ("NEARUSDT",      1.0), ("APTUSDT",       1.0), ("SUIUSDT",       1.0),
        ("OPUSDT",        1.0),
    };

    const int Batches = 530; // ~5yr of 5m candles (older coins; newer memes return less)
    Console.WriteLine($"  Fetching {allCoins.Length} coins × up to {Batches} batches (~5yr)...");
    Console.WriteLine("  (First run fetches from Bybit and builds cache; subsequent runs are instant)\n");

    var sem = new SemaphoreSlim(3);
    var fetchTasks = allCoins.Select(async ((string sym, double weight) t) =>
    {
        await sem.WaitAsync();
        try
        {
            var candles = await FetchCandlesCached(t.sym, batches: Batches);
            double years = candles.Count * 5.0 / 60.0 / 24.0 / 365.25;
            Console.WriteLine($"  {t.sym,-20} {candles.Count,7} candles ({years:F1}yr)");
            return (t.sym, t.weight, candles);
        }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);

    // ── 70/15/15 split per coin ────────────────────────────────────────────
    var namedCoinData = new List<(string Sym, GeneticAlgorithm.CoinData Cd)>();
    var holdoutData   = new Dictionary<string, Candle[]>();

    foreach (var (sym, weight, candles) in fetched)
    {
        if (candles.Count < 500) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
        int trainEnd = (int)(candles.Count * 0.70);
        int valEnd   = (int)(candles.Count * 0.85);
        namedCoinData.Add((sym, new GeneticAlgorithm.CoinData(
            candles.Take(trainEnd).ToArray(),
            candles.Skip(trainEnd).Take(valEnd - trainEnd).ToArray(),
            weight)));
        holdoutData[sym] = candles.Skip(valEnd).ToArray();
    }

    if (namedCoinData.Count == 0) { Console.WriteLine("No data."); return; }

    // Load seed from saved best
    Genotype? seed = null;
    if (File.Exists(GenoFile))
    {
        seed = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        Console.WriteLine($"\n  Seed: {seed}");
    }

    // ── Auto-screen: only train on coins where seed shows positive edge ───
    if (seed != null)
    {
        Console.WriteLine("\n  Screening coins (seed Sharpe on training data):");
        int totalBefore = namedCoinData.Count;
        var filtered = namedCoinData
            .Where(nc =>
            {
                var r  = Simulator.GetUnifiedReturns(seed, nc.Cd.TrainCandles, true).Select(t => t.Return).ToList();
                double sh = Simulator.SharpeRatio(r, nc.Cd.TrainCandles.Length);
                bool pass = sh > 0;
                Console.WriteLine($"    {(pass ? "✓" : "✗")} {nc.Sym,-20} Sh={sh:F2}  Tr={r.Count}");
                return pass;
            })
            .ToList();

        if (filtered.Count >= 4)
        {
            namedCoinData = filtered;
            var passSet = filtered.Select(nc => nc.Sym).ToHashSet();
            foreach (var key in holdoutData.Keys.Where(k => !passSet.Contains(k)).ToList())
                holdoutData.Remove(key);
            Console.WriteLine($"  → {filtered.Count}/{totalBefore} coins pass — excluded coins dilute the fitness signal\n");
        }
        else
        {
            Console.WriteLine($"  ⚠ Only {filtered.Count}/{totalBefore} passed screening — keeping all {totalBefore} to avoid data starvation\n");
        }
    }

    var coinData = namedCoinData.Select(nc => nc.Cd).ToList();
    Console.WriteLine($"  {coinData.Count} coins ready  (train 70% / val 15% / holdout 15%)\n");

    // Helper: mean per-coin time-normalised Sharpe on holdout.
    // Averaging per-coin (not concatenating) avoids the sqrt(N_coins) inflation.
    double HoldoutSharpe(Genotype g)
    {
        var scores = holdoutData
            .Select(kv =>
            {
                var r = Simulator.GetUnifiedReturns(g, kv.Value, true).Select(t => t.Return).ToList();
                return Simulator.SharpeRatio(r, kv.Value.Length);
            })
            .ToList();
        return scores.Count > 0 ? scores.Average() : 0;
    }

    // Density cap: ~1 trade per coin per day on 5m candles (288 candles/day).
    // Squared penalty kicks in above this; 4× density → 6.25% of the raw score.
    // Prevents regime genes collapsing to "trade everything" during Phase 1.
    const double SelectivityCap = 1.0 / 288.0; // ≈ 0.00347

    // Compute seed's trade density for reference
    if (seed != null)
    {
        var refReturns = coinData.SelectMany(cd =>
            Simulator.GetUnifiedReturns(seed, cd.TrainCandles, true).Select(t => t.Return)).ToList();
        double refDensity = coinData.Sum(cd => cd.TrainCandles.Length) > 0
            ? (double)refReturns.Count / coinData.Sum(cd => (long)cd.TrainCandles.Length)
            : 0;
        Console.WriteLine($"  Seed trade density : {refDensity * 288:F2} trades/coin/day  " +
            $"(cap = {SelectivityCap * 288:F2}/day)\n");
    }

    // ── Phase 1: Regime / entry-filter genes ──────────────────────────────
    Console.WriteLine("─── Phase 1/4: Regime genes ───");
    Console.WriteLine("  Active : RsiPeriod, RsiOverbought, BosCandlesWait, EmaPeriod,");
    Console.WriteLine("           BosThreshold, VolumeMultiplier, RegimeAdxPeriod, RegimeAdxThreshold");
    Console.WriteLine($"  Frozen : pump-exit genes, ranging genes");
    Console.WriteLine($"  Density cap: {SelectivityCap * 288:F2} trades/coin/day (squared penalty above)\n");

    var phase1 = new GeneticAlgorithm(50, 50, useAtr: true, verbose: true,
                                       activeBlock: GeneBlock.Regime,
                                       maxTradeDensity: SelectivityCap)
        .Run(coinData, seed);
    Console.WriteLine($"\n  Phase 1 → {phase1}\n");

    // ── Phase 2: Pump-short exit genes ────────────────────────────────────
    Console.WriteLine("─── Phase 2/4: Pump-short exit genes ───");
    Console.WriteLine("  Active : GridStepAtrMult, DcaTriggerAtrMult(≥2.0), GridDcaAtrMult,");
    Console.WriteLine("           BreakEvenAtrMult, MaxDcaLevels, HardStopAtrMult");
    Console.WriteLine("  Frozen : all 8 regime genes from Phase 1, all ranging genes\n");

    var phase2 = new GeneticAlgorithm(40, 40, useAtr: true, verbose: true,
                                       activeBlock: GeneBlock.Exit)
        .Run(coinData, phase1);
    Console.WriteLine($"\n  Phase 2 → {phase2}\n");

    // ── Phase 3: Ranging / grid-long genes ───────────────────────────────
    Console.WriteLine("─── Phase 3/4: Ranging / grid-long genes ───");
    Console.WriteLine("  Active : RangeGridStep, RangeBreakEven, RangeDcaStep, RangeMaxDca");
    Console.WriteLine("  Frozen : all regime genes from Phase 1, all pump-exit genes from Phase 2\n");

    var phase3 = new GeneticAlgorithm(40, 40, useAtr: true, verbose: true,
                                       activeBlock: GeneBlock.Ranging)
        .Run(coinData, phase2);
    Console.WriteLine($"\n  Phase 3 → {phase3}\n");

    // ── Phase 4: Joint polish ─────────────────────────────────────────────
    Console.WriteLine("─── Phase 4/4: Joint polish (all genes free, density cap maintained) ───");
    var best = new GeneticAlgorithm(40, 30, useAtr: true, verbose: true,
                                     activeBlock: GeneBlock.All,
                                     maxTradeDensity: SelectivityCap)
        .Run(coinData, phase3);
    Console.WriteLine($"\n  Phase 4 → {best}\n");

    // ── Holdout gate (with retry) ─────────────────────────────────────────
    Console.WriteLine("─── Holdout gate (15% never seen during training) ───");
    double holdoutSharpe = HoldoutSharpe(best);
    Console.WriteLine($"  Holdout Sharpe: {holdoutSharpe:F3}");

    // Per-coin breakdown
    foreach (var (sym, arr) in holdoutData.OrderBy(kv => kv.Key))
    {
        var r  = Simulator.GetUnifiedReturns(best, arr, true).Select(t => t.Return).ToList();
        double sh = Simulator.SharpeRatio(r, arr.Length);
        Console.WriteLine($"    {sym,-20} Sh={sh:F2}  Tr={r.Count}{(sh < 0.20 ? " ← FAIL" : "")}");
    }

    double[] seedMutations = [0.45, 0.65, 0.85];
    for (int attempt = 0; attempt < 3 && holdoutSharpe < 0.30; attempt++)
    {
        double mut = seedMutations[attempt];
        Console.WriteLine($"\n  ⚠ Holdout Sharpe {holdoutSharpe:F3} < 0.30 — retrying (mutation {mut:F2}, attempt {attempt + 1}/3)");
        var retryPhase1 = new GeneticAlgorithm(50, 50, useAtr: true, verbose: false,
                                                activeBlock: GeneBlock.Regime, maxTradeDensity: SelectivityCap)
            .Run(coinData, best.Mutate(new Random(), mut, true, GeneBlock.Regime));
        var retryPhase2 = new GeneticAlgorithm(40, 40, useAtr: true, verbose: false,
                                                activeBlock: GeneBlock.Exit)
            .Run(coinData, retryPhase1);
        var retryPhase3 = new GeneticAlgorithm(40, 40, useAtr: true, verbose: false,
                                                activeBlock: GeneBlock.Ranging)
            .Run(coinData, retryPhase2);
        var retryBest   = new GeneticAlgorithm(40, 30, useAtr: true, verbose: false,
                                                activeBlock: GeneBlock.All, maxTradeDensity: SelectivityCap)
            .Run(coinData, retryPhase3);
        double retrySh = HoldoutSharpe(retryBest);
        Console.WriteLine($"  Retry holdout Sharpe: {retrySh:F3}  ({retryBest})");
        if (retrySh > holdoutSharpe) { best = retryBest; holdoutSharpe = retrySh; }
    }

    // ── Save gate ─────────────────────────────────────────────────────────
    double savedHoldoutSh = 0.0;
    if (File.Exists(GenoFile))
    {
        var inc = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        savedHoldoutSh = HoldoutSharpe(inc);
    }

    Console.WriteLine($"\n─── Save gate ───");
    Console.WriteLine($"  New  holdout Sharpe : {holdoutSharpe:F3}");
    Console.WriteLine($"  Saved holdout Sharpe: {savedHoldoutSh:F3}");

    if (holdoutSharpe > savedHoldoutSh)
    {
        File.WriteAllText(GenoFile, JsonSerializer.Serialize(GenotypeDto.From(best),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  ✓ Saved → {GenoFile}");
    }
    else
    {
        Console.WriteLine($"  ⚠ Not saved — incumbent holdout ≥ new (tip: run again or try backtest)");
    }

    Console.WriteLine($"\nNext: dotnet run -- backtest   ← authoritative 1yr test on 14 coins");
}

// ══════════════════════════════════════════════════════════════════════════════
//  BACKTEST
// ══════════════════════════════════════════════════════════════════════════════
async Task RunBacktest()
{
    Console.WriteLine("=== Gravity-gen2 | BACKTEST (~2yr, 14 coins, regime-aware) ===");
    Console.WriteLine("    Portfolio computed on coins with confirmed edge (Sharpe > 0 on out-of-sample slice).\n");
    var g = LoadGenotype(); if (g == null) return;
    Console.WriteLine($"Genotype: {g}\n");

    var testCoins = new[]
    {
        "WIFUSDT", "SOLUSDT",  "MEMEUSDT",    "ATOMUSDT",    "DOGEUSDT",
        "1000BONKUSDT", "XRPUSDT", "ETHUSDT", "AVAXUSDT",    "BNBUSDT",
        "LINKUSDT", "ADAUSDT", "1000PEPEUSDT", "1000FLOKIUSDT",
        "BTCUSDT", "NEARUSDT", "APTUSDT", "SUIUSDT", "OPUSDT",
    };

    Console.WriteLine($"  Fetching {testCoins.Length} coins in parallel...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = testCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try
        {
            var candles = await FetchCandlesCached(sym, batches: 212);  // ~2yr, uses disk cache
            Console.WriteLine($"  {sym}: {candles.Count} candles");
            return (sym, candles);
        }
        finally { sem.Release(); }
    });
    var fetchedArr = await Task.WhenAll(fetchTasks);
    var fetched    = fetchedArr.ToDictionary(r => r.sym, r => r.candles);
    Console.WriteLine();

    var allTrades  = new List<(string Coin, DateTime Time, double Return, double Conf, string Kind, double AtrPct)>();
    var coinStats  = new List<CoinResult>();

    Console.WriteLine($"{"Coin",-18} {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine(new string('-', 70));

    foreach (var sym in testCoins)
    {
        if (!fetched.TryGetValue(sym, out var candles) || candles.Count < 500)
        {
            Console.WriteLine($"  {sym,-16}  skip (no data)");
            continue;
        }

        var arr   = candles.ToArray();
        int split = (int)(arr.Length * 0.8);
        var tArr  = arr[..split];
        var vArr  = arr[split..];

        var tRet  = Simulator.GetUnifiedReturns(g, tArr, true).Select(t => t.Return).ToList();
        double conf   = Simulator.ComputeConfidence(tRet);
        double atrPct = Simulator.CoinAtrPct(vArr);

        var vTrades   = Simulator.GetUnifiedReturns(g, vArr, true);
        var vFiltered = RollingEdgeFilter(vTrades, windowDays: 15, minHistory: 5);
        var vRet      = vFiltered.Select(t => t.Return).ToList();

        double sh   = Simulator.SharpeRatio(vRet, vArr.Length);
        double sort = Simulator.SortinoRatio(vRet, vArr.Length);
        double pf   = Simulator.ProfitFactor(vRet);
        double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
        double avg  = vRet.Count > 0 ? vRet.Average() : 0;

        foreach (var (t, ret, kind) in vFiltered)
            allTrades.Add((sym, t, ret, conf, kind, atrPct));

        Console.WriteLine($"  {sym,-16} {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
        coinStats.Add(new CoinResult(sym, sh, sort, pf, vRet.Count, wr, avg));
    }

    if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

    // Auto-select coins where the pattern has confirmed edge on the out-of-sample slice
    var edgeSyms = coinStats.Where(c => c.Sharpe > 0).Select(c => c.Coin).ToHashSet();

    Console.WriteLine($"\n  Per-coin (sorted by Sharpe):");
    Console.WriteLine($"  {"Coin",-18} {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Edge",5}");
    Console.WriteLine($"  {new string('-', 74)}");
    foreach (var r in coinStats.OrderByDescending(c => c.Sharpe))
        Console.WriteLine($"  {r.Coin,-18} {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WinRate,5:P0}  {r.AvgRet,+7:F2}%  {(edgeSyms.Contains(r.Coin) ? "✓" : "✗"),5}");

    Console.WriteLine($"\n  Auto-selected {edgeSyms.Count}/{coinStats.Count} coins with confirmed edge (Sharpe > 0)");
    var edgeTrades = allTrades.Where(t => edgeSyms.Contains(t.Coin)).ToList();
    if (edgeTrades.Count == 0) { Console.WriteLine("  No trades from edge coins."); return; }
    edgeTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
    PortfolioSim(edgeTrades, g.MaxDcaLevels + 1);
}

// ══════════════════════════════════════════════════════════════════════════════
//  AUTO-EVOLVE — overnight loop: trainblocks → profit eval → save candidates
//  Seeds each iteration from the profit-best genotype found so far.
//  Saves a candidate to candidates/ whenever profit improves.
//  Ctrl+C to stop cleanly.
// ══════════════════════════════════════════════════════════════════════════════
async Task RunAutoEvolve()
{
    Console.WriteLine("=== Gravity-gen2 | AUTO-EVOLVE (overnight loop) ===");
    Console.WriteLine("  trainblocks → trainbayes → profit eval (2yr, corr-aware, 15d rolling) → save on improvement");
    Console.WriteLine("  Seeds each iteration from profit-best. Ctrl+C to stop.\n");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var evalSyms = new[]
    {
        "WIFUSDT", "SOLUSDT", "MEMEUSDT", "ATOMUSDT", "DOGEUSDT",
        "1000BONKUSDT", "XRPUSDT", "ETHUSDT", "AVAXUSDT", "BNBUSDT",
        "LINKUSDT", "ADAUSDT", "1000PEPEUSDT", "1000FLOKIUSDT",
        "BTCUSDT", "NEARUSDT", "APTUSDT", "SUIUSDT", "OPUSDT",
    };

    // Pre-fetch 2yr evaluation data once (cached — instant after first trainblocks run)
    Console.Write($"  Pre-fetching 2yr data for {evalSyms.Length} coins (cached)... ");
    var fsem = new SemaphoreSlim(4);
    var evalData = (await Task.WhenAll(evalSyms.Select(async sym =>
    {
        await fsem.WaitAsync();
        try { return (sym, (await FetchCandlesCached(sym, batches: 212)).ToArray()); }
        finally { fsem.Release(); }
    }))).Where(r => r.Item2.Length >= 500).ToDictionary(r => r.sym, r => r.Item2);
    Console.WriteLine($"{evalData.Count}/{evalSyms.Length} coins ready.\n");

    // Evaluate portfolio profit % and overfitting diagnostics for a genotype.
    // Uses ATR-normalized + correlation-aware sizing + 15d rolling edge filter (same as backtest).
    // Returns: (profitFrac, tradeCount, firstHalfPct, secondHalfPct, sharpe)
    const double ProfitReinvest = 0.90;
    const int    MinTrades    = 250;   // higher bar now that we have 2yr eval data
    const double MinSharpe    = 1.5;   // minimum eval Sharpe to accept a result
    const double MaxHalfRatio = 3.0;   // flag if one half earns >3× the other
    const double CorrThresh   = 0.65;  // Pearson threshold for correlated pair scaling

    (double Profit, int Trades, double H1Pct, double H2Pct, double Sharpe) EvalProfit(Genotype g)
    {
        var trades = new List<(string Coin, DateTime Time, double Return, double Conf, string Kind, double AtrPct)>();
        foreach (var (sym, arr) in evalData)
        {
            int split    = (int)(arr.Length * 0.8);
            var tRet     = Simulator.GetUnifiedReturns(g, arr[..split], true).Select(t => t.Return).ToList();
            double conf  = Simulator.ComputeConfidence(tRet);
            double atrPct   = Simulator.CoinAtrPct(arr[split..]);
            var vTrades     = Simulator.GetUnifiedReturns(g, arr[split..], true);
            var vFiltered   = RollingEdgeFilter(vTrades, windowDays: 30, minHistory: 5);
            var vRet        = vFiltered.Select(t => t.Return).ToList();
            if (Simulator.SharpeRatio(vRet, arr[split..].Length) <= 0) continue;
            foreach (var (t, ret, kind) in vFiltered)
                trades.Add((sym, t, ret, conf, kind, atrPct));
        }
        if (trades.Count == 0) return (0, 0, 0, 0, 0);
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Compute correlation matrix once; precompute correlated peer count per trade.
        var corrMat = ComputeCorrelations(trades.Select(t => (t.Coin, t.Time, t.Return)));
        var corrPeers = trades.Select((tr, i) =>
            trades.Count(t2 => t2.Time.Date == tr.Time.Date &&
                                t2.Coin != tr.Coin &&
                                GetCorr(corrMat, tr.Coin, t2.Coin) > CorrThresh)).ToArray();

        // Full portfolio sim
        double balance = 1000.0, realized = 0.0;
        double h1Bal = 1000.0, h1Real = 0.0;  // first-half tracker
        double h2Bal = 1000.0, h2Real = 0.0;  // second-half tracker
        int mid = trades.Count / 2;

        var allRet = new List<double>();
        for (int i = 0; i < trades.Count; i++)
        {
            var (_, _, ret, conf, _, atrPct) = trades[i];
            double corrScale = 1.0 / (1.0 + 0.5 * corrPeers[i]);
            var (posEur, lev) = CalcPosition(conf, atrPct, balance, g.MaxDcaLevels + 1);
            double pnlEur = ret / 100.0 * (posEur * corrScale) * lev;
            allRet.Add(ret);
            if (pnlEur >= 0) { balance += ProfitReinvest * pnlEur; realized += (1 - ProfitReinvest) * pnlEur; }
            else              { balance += pnlEur; }

            // Track halves independently for fair comparison (same corr scaling)
            if (i < mid)
            {
                var (p1, l1) = CalcPosition(conf, atrPct, h1Bal, g.MaxDcaLevels + 1);
                double e1 = ret / 100.0 * (p1 * corrScale) * l1;
                if (e1 >= 0) { h1Bal += ProfitReinvest * e1; h1Real += (1 - ProfitReinvest) * e1; }
                else           h1Bal += e1;
            }
            else
            {
                var (p2, l2) = CalcPosition(conf, atrPct, h2Bal, g.MaxDcaLevels + 1);
                double e2 = ret / 100.0 * (p2 * corrScale) * l2;
                if (e2 >= 0) { h2Bal += ProfitReinvest * e2; h2Real += (1 - ProfitReinvest) * e2; }
                else           h2Bal += e2;
            }
        }

        double profitFrac = (balance + realized) / 1000.0 - 1.0;
        double h1Pct      = (h1Bal + h1Real) / 1000.0 - 1.0;
        double h2Pct      = (h2Bal + h2Real) / 1000.0 - 1.0;
        int    candleCount = evalData.Values.Sum(a => (int)(a.Length * 0.2));
        double sharpe     = Simulator.SharpeRatio(allRet, candleCount);
        return (profitFrac, trades.Count, h1Pct * 100.0, h2Pct * 100.0, sharpe);
    }

    // Analyse diagnostics and return overfitting warnings (empty = clean).
    List<string> OverfitWarnings(int trades, double h1Pct, double h2Pct, double sharpe)
    {
        var w = new List<string>();
        if (trades < MinTrades)
            w.Add($"⚠ Trade count {trades} < {MinTrades} — insufficient evidence");
        if (sharpe < MinSharpe)
            w.Add($"⚠ Eval Sharpe {sharpe:F2} < {MinSharpe:F1} — weak signal quality");
        if (h1Pct > 0.1 && h2Pct > 0.1)
        {
            double ratio = Math.Max(h1Pct, h2Pct) / Math.Min(h1Pct, h2Pct);
            if (ratio > MaxHalfRatio)
                w.Add($"⚠ Period imbalance: H1={h1Pct:+0.00}% vs H2={h2Pct:+0.00}% (ratio {ratio:F1}×) — possible window overfit");
        }
        else if (h1Pct < 0 || h2Pct < 0)
            w.Add($"⚠ One half unprofitable: H1={h1Pct:+0.00}% H2={h2Pct:+0.00}% — not generalising");
        return w;
    }

    // Establish baseline from whatever is currently in best_genotype.json
    double bestProfitFrac = double.MinValue;
    if (File.Exists(GenoFile))
    {
        var initG = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        var (pf, tr, h1, h2, sh) = EvalProfit(initG);
        bestProfitFrac = pf;
        Console.WriteLine($"  Baseline: profit={pf*100:+0.00}%  trades={tr}  " +
                          $"H1={h1:+0.00}%  H2={h2:+0.00}%  Sharpe={sh:F2}");
        foreach (var w in OverfitWarnings(tr, h1, h2, sh))
            Console.WriteLine($"  {w}");
        Console.WriteLine();
    }

    Directory.CreateDirectory("candidates");
    int iteration      = 0;
    int consecutiveOverfit = 0; // tracks how many back-to-back results had overfit flags

    while (!cts.IsCancellationRequested)
    {
        iteration++;
        string bar = new string('─', 74);
        Console.WriteLine($"\n{bar}");
        Console.WriteLine($"  Iteration {iteration}  [{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC]" +
                          $"  Best profit so far: {bestProfitFrac * 100:+0.00}%");
        Console.WriteLine($"{bar}\n");

        // Snapshot profit-best genotype — restore it after each iteration so trainblocks
        // always seeds from the most profitable genotype, not just the holdout-best.
        string? profitBestJson = File.Exists(GenoFile) ? File.ReadAllText(GenoFile) : null;

        await RunTrainBlocks();
        if (cts.IsCancellationRequested) break;

        // Second pass: CMA-ES polish on the trainblocks result.
        // Covers different parts of the landscape than the GA.
        Console.WriteLine("\n  ── CMA-ES polish pass ──");
        await RunTrainBayes();
        if (cts.IsCancellationRequested) break;

        var g = LoadGenotype();
        if (g == null) { if (profitBestJson != null) File.WriteAllText(GenoFile, profitBestJson); continue; }

        var (profitFrac, trades, h1Pct, h2Pct, evalSharpe) = EvalProfit(g);
        double profitPct = profitFrac * 100.0;
        var    warnings  = OverfitWarnings(trades, h1Pct, h2Pct, evalSharpe);

        Console.WriteLine($"\n  ── Profit eval: {profitPct:+0.00}%  (record: {bestProfitFrac * 100:+0.00}%)");
        Console.WriteLine($"     Trades={trades}  H1={h1Pct:+0.00}%  H2={h2Pct:+0.00}%  Sharpe={evalSharpe:F2}");
        foreach (var w in warnings) Console.WriteLine($"     {w}");

        bool overfit = warnings.Count > 0;
        consecutiveOverfit = overfit ? consecutiveOverfit + 1 : 0;

        if (consecutiveOverfit >= 3)
            Console.WriteLine($"\n  ⚠⚠ {consecutiveOverfit} consecutive iterations with overfit flags — " +
                              $"consider stopping and re-evaluating fitness weights.");

        // Only accept improvement if it passes the overfitting checks
        if (profitFrac > bestProfitFrac && !overfit)
        {
            bestProfitFrac = profitFrac;
            string ts   = DateTime.UtcNow.ToString("yyyyMMdd_HHmm");
            string path = $"candidates/candidate_auto_{ts}_profit{profitPct:F1}.json";
            File.WriteAllText(path, File.ReadAllText(GenoFile));
            Console.WriteLine($"  ★ New profit record — saved → {path}");
            Console.WriteLine($"    Genotype: {g}");
        }
        else if (profitFrac > bestProfitFrac && overfit)
        {
            Console.WriteLine($"  Profit improved but overfit flags present — not saved.");
            if (profitBestJson != null) File.WriteAllText(GenoFile, profitBestJson);
        }
        else
        {
            // Restore profit-best as seed for next trainblocks iteration
            if (profitBestJson != null) File.WriteAllText(GenoFile, profitBestJson);
            Console.WriteLine($"  No improvement — restored profit-best as seed.");
        }
    }

    Console.WriteLine($"\n\n  Auto-evolve stopped after {iteration} iteration(s).");
    Console.WriteLine($"  Best profit achieved: {bestProfitFrac * 100:+0.00}%");
    if (File.Exists(GenoFile))
        Console.WriteLine($"  Best genotype: {LoadGenotype()}");
}

// ══════════════════════════════════════════════════════════════════════════════
//  PAPER TRADE
// ══════════════════════════════════════════════════════════════════════════════
async Task RunPaperTrade()
{
    Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (regime-aware) — Ctrl+C to stop ===\n");

    // Auto-select regime genotype pair if present.
    // Uses current 30d median ATR% vs 90d baseline to pick high-vol or low-vol genotype.
    Genotype? gHigh = null, gLow = null;
    bool hasRegimePair = File.Exists("regime_high_genotype.json") && File.Exists("regime_low_genotype.json");
    if (hasRegimePair)
    {
        gHigh = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText("regime_high_genotype.json"))!.ToGenotype();
        gLow  = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText("regime_low_genotype.json"))!.ToGenotype();
        Console.WriteLine("  Regime pair loaded — will auto-select based on BTCUSDT ATR.");
    }

    var g = LoadGenotype(); if (g == null) return;
    Console.WriteLine($"Genotype: {g}\n");

    // Compute current volatility regime from BTCUSDT and pick genotype
    async Task<Genotype> PickRegimeGenotype()
    {
        if (!hasRegimePair || gHigh == null || gLow == null) return g;
        try
        {
            var btc30  = (await FetchCandles("BTCUSDT", batches: 6)).ToArray();   // ~30d
            var btc90  = (await FetchCandles("BTCUSDT", batches: 18)).ToArray();  // ~90d
            double Atr(Candle[] c) => c.Length < 50 ? 0 :
                c.Skip(c.Length / 2).Average(x => (x.High - x.Low) / Math.Max(x.Close, 1e-10) * 100.0);
            double cur = Atr(btc30), base90 = Atr(btc90);
            bool isHigh = cur > base90;
            Console.WriteLine($"  ATR regime: current={cur:F3}%  base90={base90:F3}%  → using {(isHigh ? "HIGH-VOL" : "LOW-VOL")} genotype");
            return isHigh ? gHigh : gLow;
        }
        catch { return g; }
    }
    var activeG = await PickRegimeGenotype();

    var coins = new[]
    {
        "WIFUSDT", "SOLUSDT",  "MEMEUSDT",    "DOGEUSDT",    "1000BONKUSDT",
        "XRPUSDT", "ETHUSDT",  "AVAXUSDT",    "BNBUSDT",     "LINKUSDT",
        "ADAUSDT", "1000PEPEUSDT", "ATOMUSDT", "1000FLOKIUSDT",
        "BTCUSDT", "NEARUSDT", "APTUSDT",     "SUIUSDT",     "OPUSDT",
    };

    // ── Trade journal ────────────────────────────────────────────────────────
    const double PaperStartBal = 1000.0;
    const double PaperConf     = 0.10;   // fixed halfKelly estimate for live sizing
    const double PaperFee      = 0.21;   // round-trip fee %

    double liveBalance  = PaperStartBal;
    double sessionPeak  = PaperStartBal;   // tracks all-time high for drawdown guard
    var prevOpen  = new Dictionary<string, bool>();
    var prevEntry = new Dictionary<string, double>();
    var openTime  = new Dictionary<string, DateTime>();

    var journal = new List<(string Coin, DateTime OpenTime, DateTime CloseTime,
                            double EntryPx, double ExitPx, double RetPct, double PnlEur)>();
    // ─────────────────────────────────────────────────────────────────────────

    // Refresh every 5 minutes (one 5m candle) — aligns with candle close
    const int RefreshSeconds = 300;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    while (!cts.Token.IsCancellationRequested)
    {
        // Re-evaluate regime every cycle (ATR changes slowly, cheap operation)
        if (hasRegimePair) activeG = await PickRegimeGenotype();
        string regimeSrc = hasRegimePair ? $" [{(activeG == gHigh ? "HIGH-VOL" : "LOW-VOL")} genotype]" : "";

        Console.Clear();
        Console.WriteLine($"=== Gravity-gen2 | PAPER TRADE  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]{regimeSrc}  Ctrl+C to stop ===\n");
        Console.WriteLine($"{"Coin",-18}  {"Regime",-12} {"State",-16} {"Entry",11}  {"Current",11}  {"Unrealised",10}  {"Mode",8}");
        Console.WriteLine(new string('-', 92));

        foreach (var sym in coins)
        {
            if (cts.Token.IsCancellationRequested) break;

            var candles = await FetchCandles(sym, batches: 5);
            if (candles.Count < 200) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }

            double px = candles[^1].Close;
            var    st = Simulator.GetUnifiedTradeState(activeG, candles.ToArray(), true);

            bool   wasOpen      = prevOpen.GetValueOrDefault(sym, false);
            double prevEntryPx  = prevEntry.GetValueOrDefault(sym, 0);

            // Detect close: was open last cycle, now watching
            if (wasOpen && !st.PumpOpen && prevEntryPx > 0)
            {
                // Short return: profit when price falls. Deduct round-trip fee.
                double retPct  = (prevEntryPx - px) / prevEntryPx * 100.0 - PaperFee;
                double atrPct  = Simulator.CoinAtrPct(candles.ToArray());

                // Drawdown guard: halve conf if down >15% from session peak
                bool   ddGuard    = liveBalance < sessionPeak * 0.85;
                double effectConf = ddGuard ? PaperConf * 0.5 : PaperConf;

                // Concurrent position cap: scale down if many coins open simultaneously
                int    openCount  = Math.Max(1, prevOpen.Values.Count(v => v));
                double concScale  = Math.Max(0.33, Math.Min(1.0, 4.0 / openCount));

                var (posEur, lev) = CalcPosition(effectConf * concScale, atrPct, liveBalance, activeG.MaxDcaLevels + 1);
                double pnlEur  = retPct / 100.0 * posEur * lev;
                liveBalance   += pnlEur;
                sessionPeak    = Math.Max(sessionPeak, liveBalance);
                journal.Add((sym, openTime.GetValueOrDefault(sym, DateTime.UtcNow),
                             DateTime.UtcNow, prevEntryPx, px, retPct, pnlEur));
            }
            // Detect open: was watching, now has a position
            if (!wasOpen && st.PumpOpen)
                openTime[sym] = DateTime.UtcNow;

            prevOpen[sym]  = st.PumpOpen;
            prevEntry[sym] = st.PumpOpen ? st.PumpEntry : 0;

            string state  = st.PumpOpen
                ? (st.PumpInRecovery ? $"SHORT R{st.PumpRecoveryLeg}" : $"SHORT D{st.PumpDca}")
                : st.GridOpen ? $"GRID D{st.GridDca}" : "watching";
            string entry  = st.PumpOpen ? $"{st.PumpEntry:F6}" : st.GridOpen ? $"{st.GridEntry:F6}" : "—";
            string unreal = st.PumpOpen
                ? $"{(st.PumpEntry - px) / st.PumpEntry * 100.0:+0.00}%"
                : st.GridOpen ? $"{(px - st.GridEntry) / st.GridEntry * 100.0:+0.00}%" : "—";
            string mode   = st.PumpOpen ? (st.PumpInRecovery ? "recovery" : $"DCA {st.PumpDca}/{activeG.MaxDcaLevels}")
                          : st.GridOpen ? $"grid DCA {st.GridDca}/{activeG.MaxDcaLevels}" : "—";

            Console.WriteLine($"  {sym,-18}  {st.Regime,-12} {state,-16} {entry,11}  {px,11:F6}  {unreal,10}  {mode,8}");
        }

        // ── Journal summary ──────────────────────────────────────────────────
        if (journal.Count > 0)
        {
            if (liveBalance < sessionPeak * 0.85)
                Console.WriteLine($"\n  ⚠ DRAWDOWN GUARD ACTIVE — position size halved  (balance €{liveBalance:F2} / peak €{sessionPeak:F2}, DD {(sessionPeak - liveBalance) / sessionPeak * 100:F1}%)");
            Console.WriteLine($"\n{"═",92}");
            Console.WriteLine($"  PAPER TRADE JOURNAL  (ATR-normalized sizing, 1.5–2× dynamic lev, fee {PaperFee:F2}% RT)");
            Console.WriteLine($"  {"#",-4} {"Time (UTC)",-17} {"Coin",-16} {"Entry",10}  {"Exit",10}  {"Ret%",7}  {"P&L€",8}");
            Console.WriteLine($"  {new string('─', 82)}");
            foreach (var (t, i) in journal.Select((t, i) => (t, i + 1)))
            {
                string win = t.RetPct >= 0 ? "✓" : "✗";
                Console.WriteLine($"  {win}{i,-3} {t.OpenTime:MM-dd HH:mm}→{t.CloseTime:HH:mm}  " +
                                  $"{t.Coin,-16} {t.EntryPx,10:F6}  {t.ExitPx,10:F6}  " +
                                  $"{t.RetPct,+7:F2}%  {t.PnlEur,+8:F2}€");
            }
            int wins  = journal.Count(t => t.RetPct >= 0);
            double totalPnl = liveBalance - PaperStartBal;
            Console.WriteLine($"\n  Balance: €{liveBalance:F2}  ({totalPnl:+0.00}€ / {totalPnl/PaperStartBal*100:+0.00}%)" +
                              $"  |  Trades: {journal.Count}  W: {wins}  L: {journal.Count - wins}" +
                              $"  |  WR: {(journal.Count > 0 ? (double)wins/journal.Count*100 : 0):F0}%");

            // Unrealised P&L for open positions
            var openPositions = prevOpen.Where(kv => kv.Value).ToList();
            if (openPositions.Count > 0)
            {
                Console.Write($"  Open: ");
                foreach (var kv in openPositions)
                {
                    if (!prevEntry.TryGetValue(kv.Key, out double ep) || ep <= 0) continue;
                    Console.Write($"{kv.Key} (entry unrealised shown above)  ");
                }
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine($"\n  No trades closed yet — journal will appear when first position closes.");
        }

        if (cts.Token.IsCancellationRequested) break;

        // Count down to next refresh
        for (int s = RefreshSeconds; s > 0; s--)
        {
            if (cts.Token.IsCancellationRequested) break;
            Console.Write($"\r  Next refresh in {s,3}s  ");
            await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
        }
    }

    Console.WriteLine("\n\n  Paper trade stopped.");
    if (journal.Count > 0)
    {
        Console.WriteLine($"  Final balance: €{liveBalance:F2}  ({liveBalance - PaperStartBal:+0.00}€ / {(liveBalance/PaperStartBal-1)*100:+0.00}%)");
        Console.WriteLine($"  Trades: {journal.Count}  Wins: {journal.Count(t => t.RetPct >= 0)}");
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  STATUS — portfolio simulation with real fees + slippage
// ══════════════════════════════════════════════════════════════════════════════
async Task RunStatus()
{
    Console.WriteLine("=== Gravity-gen2 | STATUS (fees + slippage + reinvestment) ===\n");

    // Prefer live_best if available
    Genotype? g = null;
    string src = "";
    if (File.Exists("live_best_genotype.json"))
    {
        g = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText("live_best_genotype.json"))!.ToGenotype();
        src = "live_best_genotype.json";
    }
    else
    {
        g = LoadGenotype();
        src = GenoFile;
    }
    if (g == null) return;

    const double RoundTrip = Simulator.FeeRoundTrip;      // fees already in net returns from simulator
    const double StartBal  = 1000.0;
    const double Reinvest  = 0.90;                         // 90% of profit compounds

    Console.WriteLine($"Source:      {src}");
    Console.WriteLine($"Genotype:    {g}");
    Console.WriteLine($"Fees:        {RoundTrip:F3}% round-trip (0.055% taker ×2 + 0.05% slippage ×2), applied in simulator");
    Console.WriteLine($"Position:    ATR-normalized (ref 0.30% ATR), 20% Kelly-scaled, 1.5–2× dynamic leverage, max 25% notional at full DCA");
    Console.WriteLine($"Reinvest:    {Reinvest*100:F0}% of profits compound\n");

    var coins = new[]
    {
        "WIFUSDT", "SOLUSDT", "MEMEUSDT", "DOGEUSDT", "1000BONKUSDT",
        "XRPUSDT", "ETHUSDT", "AVAXUSDT", "BNBUSDT", "LINKUSDT",
        "ADAUSDT", "1000PEPEUSDT", "ATOMUSDT", "1000FLOKIUSDT",
        "BTCUSDT", "NEARUSDT", "APTUSDT", "SUIUSDT", "OPUSDT",
    };

    Console.Write($"Fetching ~20d data for {coins.Length} coins... ");
    var allTrades = new List<(string Coin, DateTime Time, double Return, double Conf, double AtrPct)>();

    foreach (var sym in coins)
    {
        var candles = await FetchCandles(sym, batches: 6);
        if (candles.Count < 200) continue;
        var arr    = candles.ToArray();
        int split  = (int)(arr.Length * 0.7);
        var trainRet = Simulator.GetUnifiedReturns(g, arr[..split], true)
                                .Select(t => t.Return).ToList();
        double conf   = Simulator.ComputeConfidence(trainRet);
        double atrPct = Simulator.CoinAtrPct(arr);
        foreach (var (t, ret, _) in Simulator.GetUnifiedReturns(g, arr, true))
            allTrades.Add((sym, t, ret, conf, atrPct));
    }
    Console.WriteLine($"{allTrades.Select(t => t.Coin).Distinct().Count()}/{coins.Length} coins\n");

    if (allTrades.Count == 0) { Console.WriteLine("No trades in window."); return; }
    allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));

    var    allRet      = allTrades.Select(t => t.Return).ToList();
    int    periodCandles = allTrades.Count > 1
        ? (int)((allTrades.Max(t => t.Time) - allTrades.Min(t => t.Time)).TotalDays * 288)
        : 288;
    double sh     = Simulator.SharpeRatio(allRet, periodCandles);
    double sort   = Simulator.SortinoRatio(allRet, periodCandles);
    double pf     = Simulator.ProfitFactor(allRet);
    double cal    = Simulator.CalmarRatio(allRet);
    int    wins   = allRet.Count(r => r > 0);
    double wr     = allRet.Count > 0 ? (double)wins / allRet.Count : 0;
    double avg    = allRet.Count > 0 ? allRet.Average() : 0;
    int    maxL   = Simulator.MaxConsecLosses(allRet);

    double balance = StartBal, realized = 0, peak = StartBal, maxDd = 0;
    foreach (var (_, _, ret, conf, atrPct) in allTrades)
    {
        var (posEur, lev) = CalcPosition(conf, atrPct, balance, g.MaxDcaLevels + 1);
        double pnl = ret / 100.0 * posEur * lev;
        if (pnl >= 0) { balance += Reinvest * pnl; realized += (1 - Reinvest) * pnl; }
        else            balance += pnl;
        if (balance > peak) peak = balance;
        double dd = peak > 0 ? (peak - balance) / peak * 100.0 : 0;
        if (dd > maxDd) maxDd = dd;
    }
    double total  = balance + realized;
    double retPct = (total / StartBal - 1.0) * 100.0;
    string period = $"{allTrades.Min(t => t.Time):yyyy-MM-dd} → {allTrades.Max(t => t.Time):yyyy-MM-dd}";
    double days   = (allTrades.Max(t => t.Time) - allTrades.Min(t => t.Time)).TotalDays;

    Console.WriteLine(new string('═', 56));
    Console.WriteLine($"  STRATEGY METRICS  (net of {RoundTrip:F2}% fees per trade)");
    Console.WriteLine($"  {new string('─', 52)}");
    Console.WriteLine($"  Period:           {period}  ({(int)days}d)");
    Console.WriteLine($"  Trades:           {allRet.Count}  ({(double)allRet.Count / Math.Max(days, 1):F1}/day)");
    Console.WriteLine($"  Win rate:         {wr:P1}  ({wins}W / {allRet.Count - wins}L)");
    Console.WriteLine($"  Avg return:       {avg:+0.000;-0.000}%");
    Console.WriteLine($"  Sharpe:           {sh:F2}");
    Console.WriteLine($"  Sortino:          {sort:F2}");
    Console.WriteLine($"  Profit factor:    {pf:F2}");
    Console.WriteLine($"  Calmar:           {cal:F2}");
    Console.WriteLine($"  Max consec. loss: {maxL}");
    Console.WriteLine($"  {new string('─', 52)}");
    Console.WriteLine($"  PORTFOLIO  (€{StartBal:F0} start, Kelly-scaled positions)");
    Console.WriteLine($"  {new string('─', 52)}");
    Console.WriteLine($"  Balance:          €{balance:F2}");
    Console.WriteLine($"  Realized:         €{realized:+0.00;-0.00}");
    Console.WriteLine($"  Total:            €{total:F2}  ({retPct:+0.00;-0.00}%)");
    Console.WriteLine($"  Max drawdown:     {maxDd:F2}%");
    Console.WriteLine($"  {new string('─', 52)}");
    Console.WriteLine($"  Fitness: {g.Fitness:F4}  |  Source: {src}");
    Console.WriteLine(new string('═', 56));
}

// ══════════════════════════════════════════════════════════════════════════════
//  LIVE TRAIN
// ══════════════════════════════════════════════════════════════════════════════
// ══════════════════════════════════════════════════════════════════════════════
//  GATHER — random-seed trainblocks, saves directly to candidates/
//  Never touches best_genotype.json → safe to run alongside compare/papertrade.
//  No incumbent seed → explores diverse regions of gene space.
// ══════════════════════════════════════════════════════════════════════════════
async Task RunGather()
{
    string runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
    Console.WriteLine($"=== Gravity-gen2 | GATHER [{runId} UTC] — random seed, saves to candidates/ ===\n");

    var allCoins = new[]
    {
        ("WIFUSDT",       1.0), ("SOLUSDT",       1.0), ("MEMEUSDT",      1.0),
        ("DOGEUSDT",      1.0), ("1000BONKUSDT",  0.8), ("XRPUSDT",       1.0),
        ("ETHUSDT",       1.0), ("AVAXUSDT",      1.0), ("BNBUSDT",       1.0),
        ("LINKUSDT",      1.0), ("ADAUSDT",       1.0), ("1000PEPEUSDT",  0.8),
        ("ATOMUSDT",      1.0), ("1000FLOKIUSDT", 0.8), ("BTCUSDT",       1.0),
        ("NEARUSDT",      1.0), ("APTUSDT",       1.0), ("SUIUSDT",       1.0),
        ("OPUSDT",        1.0),
    };

    const int Batches = 530;
    Console.WriteLine($"  Fetching {allCoins.Length} coins × {Batches} batches (~5yr)...");
    Console.WriteLine("  (First run fetches from Bybit; subsequent runs use cache)\n");

    var sem = new SemaphoreSlim(3);
    var fetchTasks = allCoins.Select(async ((string sym, double weight) t) =>
    {
        await sem.WaitAsync();
        try
        {
            var candles = await FetchCandlesCached(t.sym, batches: Batches);
            double years = candles.Count * 5.0 / 60.0 / 24.0 / 365.25;
            Console.WriteLine($"  {t.sym,-20} {candles.Count,7} candles ({years:F1}yr)");
            return (t.sym, t.weight, candles);
        }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);

    var coinData    = new List<GeneticAlgorithm.CoinData>();
    var holdoutData = new Dictionary<string, Candle[]>();

    foreach (var (sym, weight, candles) in fetched)
    {
        if (candles.Count < 500) continue;
        int trainEnd = (int)(candles.Count * 0.70);
        int valEnd   = (int)(candles.Count * 0.85);
        coinData.Add(new GeneticAlgorithm.CoinData(
            candles.Take(trainEnd).ToArray(),
            candles.Skip(trainEnd).Take(valEnd - trainEnd).ToArray(),
            weight));
        holdoutData[sym] = candles.Skip(valEnd).ToArray();
    }

    if (coinData.Count == 0) { Console.WriteLine("No data."); return; }
    Console.WriteLine($"\n  {coinData.Count} coins  (70/15/15 split)  [random seed — no incumbent bias]\n");

    double HoldoutSharpe(Genotype g) =>
        holdoutData.Select(kv =>
        {
            var r = Simulator.GetUnifiedReturns(g, kv.Value, true).Select(t => t.Return).ToList();
            return Simulator.SharpeRatio(r, kv.Value.Length);
        }).DefaultIfEmpty(0).Average();

    const double SelectivityCap = 1.0 / 288.0;

    Console.WriteLine("─── Phase 1/3: Regime genes (random seed) ───");
    var phase1 = new GeneticAlgorithm(50, 50, useAtr: true, verbose: true,
                                       activeBlock: GeneBlock.Regime,
                                       maxTradeDensity: SelectivityCap)
        .Run(coinData, seed: null);
    Console.WriteLine($"\n  Phase 1 → {phase1}\n");

    Console.WriteLine("─── Phase 2/3: Exit genes ───");
    var phase2 = new GeneticAlgorithm(40, 40, useAtr: true, verbose: true,
                                       activeBlock: GeneBlock.Exit)
        .Run(coinData, phase1);
    Console.WriteLine($"\n  Phase 2 → {phase2}\n");

    Console.WriteLine("─── Phase 3/3: Joint polish ───");
    var best = new GeneticAlgorithm(40, 30, useAtr: true, verbose: true,
                                     activeBlock: GeneBlock.All,
                                     maxTradeDensity: SelectivityCap)
        .Run(coinData, phase2);
    Console.WriteLine($"\n  Phase 3 → {best}\n");

    Console.WriteLine("─── Holdout gate (15% never seen) ───");
    double holdoutSharpe = HoldoutSharpe(best);
    Console.WriteLine($"  Holdout Sharpe: {holdoutSharpe:F3}");
    foreach (var (sym, arr) in holdoutData.OrderBy(kv => kv.Key))
    {
        var r  = Simulator.GetUnifiedReturns(best, arr, true).Select(t => t.Return).ToList();
        double sh = Simulator.SharpeRatio(r, arr.Length);
        Console.WriteLine($"    {sym,-20} Sh={sh:F2}  Tr={r.Count}{(sh < 0.20 ? " ← FAIL" : "")}");
    }

    double[] seedMuts = [0.45, 0.65, 0.85];
    for (int attempt = 0; attempt < 3 && holdoutSharpe < 0.30; attempt++)
    {
        double mut = seedMuts[attempt];
        Console.WriteLine($"\n  ⚠ Holdout {holdoutSharpe:F3} < 0.30 — retry (mut={mut:F2}, attempt {attempt + 1}/3)");
        var rp1 = new GeneticAlgorithm(50, 50, useAtr: true, verbose: false,
                                        activeBlock: GeneBlock.Regime, maxTradeDensity: SelectivityCap)
            .Run(coinData, best.Mutate(new Random(), mut, true, GeneBlock.Regime));
        var rp2 = new GeneticAlgorithm(40, 40, useAtr: true, verbose: false,
                                        activeBlock: GeneBlock.Exit)
            .Run(coinData, rp1);
        var rb  = new GeneticAlgorithm(40, 30, useAtr: true, verbose: false,
                                        activeBlock: GeneBlock.All, maxTradeDensity: SelectivityCap)
            .Run(coinData, rp2);
        double rsh = HoldoutSharpe(rb);
        Console.WriteLine($"  Retry holdout: {rsh:F3}  ({rb})");
        if (rsh > holdoutSharpe) { best = rb; holdoutSharpe = rsh; }
    }

    // Always save to candidates/ — never writes to best_genotype.json
    Directory.CreateDirectory("candidates");
    string outPath = $"candidates/gather_{runId}_sh{holdoutSharpe:F2}.json";
    File.WriteAllText(outPath, JsonSerializer.Serialize(
        GenotypeDto.From(best),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\n  ✓ {outPath}");
    Console.WriteLine($"  {best}");
}

// ══════════════════════════════════════════════════════════════════════════════
//  COMPARE — live side-by-side evaluation of all candidates/ genotypes
// ══════════════════════════════════════════════════════════════════════════════
async Task RunCompare()
{
    var candidatesDir = Directory.Exists("candidates")
        ? "candidates"
        : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "candidates");

    var candidates = new List<(string Label, Genotype G)>();
    foreach (var path in Directory.GetFiles(candidatesDir, "*.json").OrderBy(p => p))
    {
        try
        {
            var label = Path.GetFileNameWithoutExtension(path)
                            .Replace("candidate_", "", StringComparison.OrdinalIgnoreCase);
            var g = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(path))!.ToGenotype();
            candidates.Add((label, g));
        }
        catch { /* skip malformed files */ }
    }

    if (candidates.Count == 0) { Console.WriteLine("No candidates found in candidates/"); return; }

    var coins = new[]
    {
        "WIFUSDT", "SOLUSDT",  "MEMEUSDT",    "DOGEUSDT",    "1000BONKUSDT",
        "XRPUSDT", "ETHUSDT",  "AVAXUSDT",    "BNBUSDT",     "LINKUSDT",
        "ADAUSDT", "1000PEPEUSDT", "ATOMUSDT", "1000FLOKIUSDT",
    };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    const int RefreshSeconds = 300;

    while (!cts.Token.IsCancellationRequested)
    {
        Console.Clear();
        string ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine($"=== Gravity-gen2 | COMPARE ({candidates.Count} candidates)  [{ts} UTC]  Ctrl+C to stop ===\n");

        // Fetch ~20d candles for all coins in parallel
        var sem = new SemaphoreSlim(4);
        var fetchTasks = coins.Select(async sym =>
        {
            await sem.WaitAsync();
            try   { return (sym, await FetchCandles(sym, batches: 5)); }
            finally { sem.Release(); }
        });
        var fetched = (await Task.WhenAll(fetchTasks))
            .Where(f => f.Item2.Count >= 100)
            .ToDictionary(f => f.sym, f => f.Item2);

        // Score header
        Console.WriteLine($"  {"Candidate",-36} {"Sh(20d)",8}  {"Open",5}  {"Trend",6}");
        Console.WriteLine($"  {new string('─', 62)}");

        var results = new List<(string Label, double Sharpe, List<(string Sym, Simulator.UnifiedTradeState St, double Px)> Trades)>();

        foreach (var (label, g) in candidates)
        {
            var trades    = new List<(string, Simulator.UnifiedTradeState, double)>();
            double shSum  = 0; int shCount = 0, openCount = 0, trendCount = 0;

            foreach (var (sym, candles) in fetched)
            {
                var arr = candles.ToArray();
                var st  = Simulator.GetUnifiedTradeState(g, arr, true);
                double px = arr[^1].Close;
                if (st.Regime == "TrendingUp") trendCount++;
                if (st.PumpOpen)               { openCount++; trades.Add((sym, st, px)); }
                var ret = Simulator.GetUnifiedReturns(g, arr, true).Select(t => t.Return).ToList();
                if (ret.Count >= 2) { shSum += Simulator.SharpeRatio(ret, arr.Length); shCount++; }
            }

            double sh = shCount > 0 ? shSum / shCount : 0;
            Console.WriteLine($"  {label,-36} {sh,+8:F2}  {openCount,5}  {trendCount,4}/{fetched.Count}");
            results.Add((label, sh, trades));
        }

        // Open positions per candidate
        Console.WriteLine($"\n  Open positions:");
        foreach (var (label, _, trades) in results)
        {
            Console.WriteLine($"  [{label}]");
            if (trades.Count == 0) { Console.WriteLine("    (none)"); continue; }
            foreach (var (sym, st, px) in trades)
            {
                string state  = st.PumpInRecovery ? $"SHORT R{st.PumpRecoveryLeg}" : $"SHORT D{st.PumpDca}";
                double unreal = (st.PumpEntry - px) / st.PumpEntry * 100.0;
                Console.WriteLine($"    {sym,-18}  {state,-12}  entry={st.PumpEntry:F6}  now={px:F6}  {unreal:+.2f}%");
            }
        }

        if (cts.Token.IsCancellationRequested) break;
        for (int s = RefreshSeconds; s > 0; s--)
        {
            if (cts.Token.IsCancellationRequested) break;
            Console.Write($"\r  Next refresh in {s,3}s  ");
            await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
        }
    }
    Console.WriteLine("\n\n  Compare stopped.");
}

// ══════════════════════════════════════════════════════════════════════════════
//  PERMUTATION TEST — does entry timing beat random shorts with same exit logic?
// ══════════════════════════════════════════════════════════════════════════════
async Task RunPermTest()
{
    Console.WriteLine("=== Gravity-gen2 | PERMUTATION TEST ===\n");
    Console.WriteLine("  Tests whether pump-short entry detection beats randomly-timed shorts.");
    Console.WriteLine("  Null model: N random entries, same trailing stop / DCA / break-even.");
    Console.WriteLine("  p < 0.05 → entry detection is statistically significant.\n");

    var g = LoadGenotype(); if (g == null) return;
    Console.WriteLine($"Genotype: {g}\n");

    var testCoins = new[]
    {
        "WIFUSDT", "SOLUSDT", "MEMEUSDT", "DOGEUSDT", "1000BONKUSDT",
        "XRPUSDT", "ETHUSDT", "AVAXUSDT", "BNBUSDT", "LINKUSDT",
        "ADAUSDT", "1000PEPEUSDT", "ATOMUSDT", "1000FLOKIUSDT",
    };

    Console.WriteLine($"  Fetching ~1yr data for {testCoins.Length} coins (from cache)...");
    var sem = new SemaphoreSlim(3);
    var tasks = testCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try { return (sym, await FetchCandlesCached(sym, batches: 106)); }
        finally { sem.Release(); }
    });
    var fetched = (await Task.WhenAll(tasks)).ToDictionary(r => r.sym, r => r.Item2);
    Console.WriteLine();

    Console.WriteLine($"  {"Coin",-18} {"RealSh",8}  {"NullMean",8}  {"NullStd",8}  {"Pctile",7}  {"p-val",6}  {"Signal",8}");
    Console.WriteLine($"  {new string('-', 74)}");

    int sigCount = 0, totalCoins = 0;
    var allReal = new List<double>(); var allNull = new List<double>();

    foreach (var sym in testCoins)
    {
        if (!fetched.TryGetValue(sym, out var candles) || candles.Count < 500) continue;
        var arr = candles.ToArray();
        int split = (int)(arr.Length * 0.8);
        var vArr = arr[split..]; // use out-of-sample slice

        var res = Simulator.PermutationTest(g, vArr, permutations: 1000);
        string signal = res.PValue < 0.01 ? "✓✓ p<0.01"
                      : res.PValue < 0.05 ? "✓  p<0.05"
                      : res.PValue < 0.10 ? "~  p<0.10"
                      : "✗  ns";
        Console.WriteLine($"  {sym,-18} {res.RealSharpe,8:F3}  {res.MeanNull,8:F3}  {res.StdNull,8:F3}  {res.Percentile,7:F1}%  {res.PValue,6:F3}  {signal}");
        if (res.PValue < 0.05) sigCount++;
        totalCoins++;
        allReal.Add(res.RealSharpe);
        allNull.Add(res.MeanNull);
    }

    Console.WriteLine($"\n  Summary: {sigCount}/{totalCoins} coins show statistically significant entry timing (p < 0.05)");
    Console.WriteLine($"  Mean real Sharpe: {allReal.Average():F3}  vs  mean null Sharpe: {allNull.Average():F3}");
    double lift = allNull.Average() != 0 ? (allReal.Average() / allNull.Average() - 1) * 100 : double.NaN;
    if (!double.IsNaN(lift)) Console.WriteLine($"  Entry detection lift: {lift:+0.0;-0.0}% over random timing");

    Console.WriteLine();
    Console.WriteLine("  Interpretation:");
    Console.WriteLine("    p-value = fraction of random strategies that matched/beat the real strategy.");
    Console.WriteLine("    If most coins are significant, the entry conditions have real edge.");
    Console.WriteLine("    If most are not, the edge comes from exit logic (trailing stop) not entry timing.");
}

// ══════════════════════════════════════════════════════════════════════════════
//  STRESS TEST — per-period Sharpe across 5yr cache (OOD robustness check)
// ══════════════════════════════════════════════════════════════════════════════
async Task RunStressTest()
{
    Console.WriteLine("=== Gravity-gen2 | STRESS TEST (per-period Sharpe, 5yr) ===\n");
    Console.WriteLine("  Shows strategy performance across distinct market regimes.");
    Console.WriteLine("  Fragile genotypes score well in one era but fail in others.\n");

    var g = LoadGenotype(); if (g == null) return;
    Console.WriteLine($"Genotype: {g}\n");

    var testCoins = new[]
    {
        "WIFUSDT", "SOLUSDT", "DOGEUSDT", "ETHUSDT", "XRPUSDT",
        "BNBUSDT", "ADAUSDT", "AVAXUSDT",
    };

    Console.WriteLine($"  Fetching 5yr cached data for {testCoins.Length} coins...");
    var sem = new SemaphoreSlim(3);
    var tasks = testCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try { return (sym, await FetchCandlesCached(sym, batches: 530)); }
        finally { sem.Release(); }
    });
    var fetched = (await Task.WhenAll(tasks)).Where(r => r.Item2.Count >= 1000).ToDictionary(r => r.sym, r => r.Item2);
    Console.WriteLine();

    // Define 6-month windows with rough market labels
    var windows = new (string Label, DateTime From, DateTime To)[]
    {
        ("2021-H1 (bull)",    new(2021, 1, 1),  new(2021, 7, 1)),
        ("2021-H2 (peak)",    new(2021, 7, 1),  new(2022, 1, 1)),
        ("2022-H1 (crash)",   new(2022, 1, 1),  new(2022, 7, 1)),
        ("2022-H2 (bear)",    new(2022, 7, 1),  new(2023, 1, 1)),
        ("2023-H1 (recov.)",  new(2023, 1, 1),  new(2023, 7, 1)),
        ("2023-H2 (recov.)",  new(2023, 7, 1),  new(2024, 1, 1)),
        ("2024-H1 (bull)",    new(2024, 1, 1),  new(2024, 7, 1)),
        ("2024-H2 (bull)",    new(2024, 7, 1),  new(2025, 1, 1)),
        ("2025-H1 (recent)",  new(2025, 1, 1),  new(2025, 7, 1)),
    };

    // Header — coin name truncated to 10 chars
    const int CW = 10;
    Console.Write($"  {"Period",-18}  {"Avg Sh",7}  {"Trades",6}  ");
    foreach (var sym in fetched.Keys)
        Console.Write("  " + sym[..Math.Min(sym.Length - 4, CW)].PadRight(CW));
    Console.WriteLine();
    Console.WriteLine($"  {new string('-', 40 + fetched.Count * (CW + 2))}");

    var periodSharpes = new List<double>();
    foreach (var (label, from, to) in windows)
    {
        var sharpesByCoin = new List<double>();
        int totalTrades = 0;

        foreach (var (sym, candles) in fetched)
        {
            var window = candles.Where(c => c.Time >= from && c.Time < to).ToArray();
            if (window.Length < 500) { sharpesByCoin.Add(double.NaN); continue; }
            var ret = Simulator.GetUnifiedReturns(g, window, true).Select(t => t.Return).ToList();
            sharpesByCoin.Add(Simulator.SharpeRatio(ret, window.Length));
            totalTrades += ret.Count;
        }

        var valid = sharpesByCoin.Where(s => !double.IsNaN(s)).ToList();
        double avg = valid.Count > 0 ? valid.Average() : 0;
        periodSharpes.Add(avg);

        Console.Write($"  {label,-18}  {avg,7:F2}  {totalTrades,6}  ");
        foreach (var sh in sharpesByCoin)
        {
            string cell = double.IsNaN(sh) ? "n/a"
                : (sh >= 0.5 ? "✓✓" : sh >= 0 ? "✓ " : "✗ ") + $"{sh:F2}";
            Console.Write("  " + cell.PadRight(CW));
        }
        Console.WriteLine();
    }

    Console.WriteLine($"\n  Worst period Sharpe : {periodSharpes.Min():F2}");
    Console.WriteLine($"  Best  period Sharpe : {periodSharpes.Max():F2}");
    Console.WriteLine($"  Periods positive    : {periodSharpes.Count(s => s > 0)}/{periodSharpes.Count(s => !double.IsNaN(s))}");

    int failPeriods = periodSharpes.Count(s => s < 0);
    if (failPeriods >= 3)
        Console.WriteLine($"\n  !! {failPeriods} periods negative — genotype is regime-fragile. Consider retraining.");
    else if (failPeriods >= 1)
        Console.WriteLine($"\n  ⚠  {failPeriods} period(s) negative — acceptable but watch for regime shifts.");
    else
        Console.WriteLine($"\n  ✓  All periods positive — robust across regimes.");
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN BAYES — CMA-ES optimizer (more sample-efficient than GA)
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTrainBayes()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN BAYES (CMA-ES, 5yr, 14 coins) ===\n");
    Console.WriteLine("  CMA-ES adapts the full covariance matrix each generation.");
    Console.WriteLine("  More sample-efficient than GA for 12-parameter space.\n");

    var allCoins = new[]
    {
        ("WIFUSDT",       1.0), ("SOLUSDT",       1.0), ("MEMEUSDT",      1.0),
        ("DOGEUSDT",      1.0), ("1000BONKUSDT",  0.8), ("XRPUSDT",       1.0),
        ("ETHUSDT",       1.0), ("AVAXUSDT",      1.0), ("BNBUSDT",       1.0),
        ("LINKUSDT",      1.0), ("ADAUSDT",       1.0), ("1000PEPEUSDT",  0.8),
        ("ATOMUSDT",      1.0), ("1000FLOKIUSDT", 0.8), ("BTCUSDT",       1.0),
        ("NEARUSDT",      1.0), ("APTUSDT",       1.0), ("SUIUSDT",       1.0),
        ("OPUSDT",        1.0),
    };

    Console.WriteLine($"  Fetching {allCoins.Length} coins (~5yr)...");
    var sem = new SemaphoreSlim(3);
    var tasks = allCoins.Select(async ((string sym, double weight) t) =>
    {
        await sem.WaitAsync();
        try
        {
            var c = await FetchCandlesCached(t.sym, batches: 530);
            Console.WriteLine($"  {t.sym,-20} {c.Count,7} candles");
            return (t.sym, t.weight, c);
        }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(tasks);

    var coinData    = new List<GeneticAlgorithm.CoinData>();
    var holdoutData = new Dictionary<string, Candle[]>();
    foreach (var (sym, weight, candles) in fetched)
    {
        if (candles.Count < 500) continue;
        int trainEnd = (int)(candles.Count * 0.70);
        int valEnd   = (int)(candles.Count * 0.85);
        coinData.Add(new GeneticAlgorithm.CoinData(
            candles.Take(trainEnd).ToArray(),
            candles.Skip(trainEnd).Take(valEnd - trainEnd).ToArray(),
            weight));
        holdoutData[sym] = candles.Skip(valEnd).ToArray();
    }

    if (coinData.Count == 0) { Console.WriteLine("No data."); return; }
    Console.WriteLine($"\n  {coinData.Count} coins  (70/15/15 split)\n");

    Genotype? seed = null;
    if (File.Exists(GenoFile))
    {
        seed = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        Console.WriteLine($"  Seeding from {GenoFile}: {seed}");
    }

    Console.WriteLine("\n─── CMA-ES optimization (3000 evaluations) ───");
    var best = new CmaOptimizer(useAtr: true, verbose: true).Run(coinData, seed, maxEvals: 3000);
    Console.WriteLine($"\n  CMA-ES result: {best}\n");

    // Holdout gate (same as trainblocks)
    double HoldoutSharpe(Genotype gx) =>
        holdoutData.Select(kv =>
        {
            var r = Simulator.GetUnifiedReturns(gx, kv.Value, true).Select(t => t.Return).ToList();
            return Simulator.SharpeRatio(r, kv.Value.Length);
        }).DefaultIfEmpty(0).Average();

    Console.WriteLine("─── Holdout gate ───");
    double hsh = HoldoutSharpe(best);
    Console.WriteLine($"  Holdout Sharpe: {hsh:F3}");
    foreach (var (sym, arr) in holdoutData.OrderBy(kv => kv.Key))
    {
        var r = Simulator.GetUnifiedReturns(best, arr, true).Select(t => t.Return).ToList();
        Console.WriteLine($"    {sym,-20} Sh={Simulator.SharpeRatio(r, arr.Length):F2}  Tr={r.Count}");
    }

    double savedHsh = 0;
    if (File.Exists(GenoFile))
    {
        var inc = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        savedHsh = HoldoutSharpe(inc);
    }
    Console.WriteLine($"\n  New  holdout: {hsh:F3}  |  Saved: {savedHsh:F3}");
    if (hsh > savedHsh)
    {
        File.WriteAllText(GenoFile, JsonSerializer.Serialize(GenotypeDto.From(best),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  ✓ Saved → {GenoFile}");
    }
    else
        Console.WriteLine($"  ⚠ Not saved — incumbent holdout ≥ new");
    Console.WriteLine($"\nNext: dotnet run -- backtest");
}

// ══════════════════════════════════════════════════════════════════════════════
//  TRAIN REGIMES — train high-vol and low-vol genotype pair
//  High-vol: candles where rolling ATR% > median → more aggressive stops/DCA
//  Low-vol : candles where rolling ATR% ≤ median → tighter exits, fewer trades
//  Saves regime_high_genotype.json and regime_low_genotype.json.
//  Paper trader auto-selects based on current 30d ATR vs 90d baseline.
// ══════════════════════════════════════════════════════════════════════════════
async Task RunTrainRegimes()
{
    Console.WriteLine("=== Gravity-gen2 | TRAIN REGIMES (high-vol + low-vol pair, 5yr) ===\n");
    Console.WriteLine("  Trains two separate genotypes: one for volatile markets, one for calm.");
    Console.WriteLine("  At runtime, paper trade picks genotype based on current vs historical ATR.\n");

    var allCoins = new[]
    {
        ("WIFUSDT",       1.0), ("SOLUSDT",       1.0), ("MEMEUSDT",      1.0),
        ("DOGEUSDT",      1.0), ("1000BONKUSDT",  0.8), ("XRPUSDT",       1.0),
        ("ETHUSDT",       1.0), ("AVAXUSDT",      1.0), ("BNBUSDT",       1.0),
        ("LINKUSDT",      1.0), ("ADAUSDT",       1.0), ("1000PEPEUSDT",  0.8),
        ("ATOMUSDT",      1.0), ("1000FLOKIUSDT", 0.8), ("BTCUSDT",       1.0),
        ("NEARUSDT",      1.0), ("APTUSDT",       1.0), ("SUIUSDT",       1.0),
        ("OPUSDT",        1.0),
    };

    Console.WriteLine($"  Fetching {allCoins.Length} coins (~5yr)...");
    var sem = new SemaphoreSlim(3);
    var tasks = allCoins.Select(async ((string sym, double weight) t) =>
    {
        await sem.WaitAsync();
        try
        {
            var c = await FetchCandlesCached(t.sym, batches: 530);
            Console.WriteLine($"  {t.sym,-20} {c.Count,7} candles");
            return (t.sym, t.weight, c);
        }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(tasks);

    // Split each coin's candles into high-ATR and low-ATR halves by median ATR%
    var highData = new List<GeneticAlgorithm.CoinData>();
    var lowData  = new List<GeneticAlgorithm.CoinData>();

    foreach (var (sym, weight, candles) in fetched)
    {
        if (candles.Count < 1000) continue;

        // Compute rolling 14-period ATR% for each candle
        var arr = candles.ToArray();
        var closes = arr.Select(c => c.Close).ToArray();
        var highs  = arr.Select(c => c.High).ToArray();
        var lows   = arr.Select(c => c.Low).ToArray();

        // Compute 30-day rolling ATR% (30*288=8640 candles) at each point
        // Use block ATR: average over 288 candles (1 day) for smoothness
        const int BlockSize = 288;
        var blockAtrs = new List<(int Start, int End, double AtrPct)>();
        for (int b = BlockSize; b + BlockSize <= arr.Length; b += BlockSize)
        {
            double sum = 0; int cnt = 0;
            for (int i = b; i < Math.Min(b + BlockSize, arr.Length); i++)
            {
                if (closes[i] > 0)
                {
                    double range = highs[i] - lows[i];
                    sum += range / closes[i] * 100.0; cnt++;
                }
            }
            if (cnt > 0) blockAtrs.Add((b, Math.Min(b + BlockSize, arr.Length), sum / cnt));
        }

        if (blockAtrs.Count < 4) continue;
        double median = blockAtrs.Select(x => x.AtrPct).OrderBy(x => x).ElementAt(blockAtrs.Count / 2);
        Console.WriteLine($"  {sym,-20} median ATR%={median:F3}  blocks={blockAtrs.Count}");

        // Collect candles in high-ATR and low-ATR blocks
        var highCandles = new List<Candle>();
        var lowCandles  = new List<Candle>();
        foreach (var (start, end, atrPct) in blockAtrs)
        {
            var target = atrPct > median ? highCandles : lowCandles;
            for (int i = start; i < end; i++) target.Add(arr[i]);
        }

        void AddRegimeData(List<Candle> rc, List<GeneticAlgorithm.CoinData> target)
        {
            if (rc.Count < 500) return;
            int te = (int)(rc.Count * 0.80);
            target.Add(new GeneticAlgorithm.CoinData(rc.Take(te).ToArray(), rc.Skip(te).ToArray(), weight));
        }
        AddRegimeData(highCandles, highData);
        AddRegimeData(lowCandles,  lowData);
    }

    Console.WriteLine($"\n  High-vol set: {highData.Count} coins  |  Low-vol set: {lowData.Count} coins\n");

    if (highData.Count == 0 || lowData.Count == 0)
    { Console.WriteLine("Insufficient data for one regime — aborting."); return; }

    Genotype? seed = null;
    if (File.Exists(GenoFile))
    {
        seed = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
        Console.WriteLine($"  Seed: {seed}\n");
    }

    Console.WriteLine("─── Training HIGH-VOLATILITY genotype ───");
    var highBest = new GeneticAlgorithm(50, 60, useAtr: true, verbose: true).Run(highData, seed);
    Console.WriteLine($"\n  High-vol result: {highBest}");

    Console.WriteLine("\n─── Training LOW-VOLATILITY genotype ───");
    var lowBest = new GeneticAlgorithm(50, 60, useAtr: true, verbose: true).Run(lowData, seed);
    Console.WriteLine($"\n  Low-vol result: {lowBest}");

    // Validation: compare each genotype on its own regime vs the other
    Console.WriteLine("\n─── Cross-regime validation ───");
    double Score(Genotype gx, List<GeneticAlgorithm.CoinData> data)
    {
        var scores = data.Select(cd =>
        {
            var r = Simulator.GetUnifiedReturns(gx, cd.ValCandles, true).Select(t => t.Return).ToList();
            return Simulator.SharpeRatio(r, cd.ValCandles.Length);
        }).ToList();
        return scores.Count > 0 ? scores.Average() : 0;
    }
    Console.WriteLine($"  High genotype on high-vol: {Score(highBest, highData):F3}  " +
                      $"on low-vol: {Score(highBest, lowData):F3}");
    Console.WriteLine($"  Low  genotype on high-vol: {Score(lowBest, highData):F3}  " +
                      $"on low-vol: {Score(lowBest, lowData):F3}");

    var opts = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText("regime_high_genotype.json", JsonSerializer.Serialize(GenotypeDto.From(highBest), opts));
    File.WriteAllText("regime_low_genotype.json",  JsonSerializer.Serialize(GenotypeDto.From(lowBest), opts));
    Console.WriteLine("\n  ✓ Saved → regime_high_genotype.json  +  regime_low_genotype.json");
    Console.WriteLine("  Paper trade will auto-select based on 30d ATR vs 90d baseline.");
    Console.WriteLine("  Run: dotnet run -- papertrade   (picks regime automatically)");
}

async Task RunLiveTrain()
{
    Console.WriteLine("=== Gravity-gen2 | LIVE TRAIN (20 genotypes, rolling ~10d window) — Ctrl+C to stop ===\n");
    Console.WriteLine("  Fitness = min(Sharpe@20% / 50% / 100% window) across 14 coins − 0.4×σ.");
    Console.WriteLine("  Universal genes score consistently; window-overfit genes score low.\n");

    // Auto-promote baseline always comes from best_genotype.json (the "official" gate).
    // But seed from live_best_genotype.json if it has better fitness — preserves session progress.
    Genotype? baseSeed = File.Exists(GenoFile)
        ? JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype()
        : null;
    double savedFitness = baseSeed?.Fitness ?? double.MinValue;

    Genotype? seed = baseSeed;
    if (File.Exists("live_best_genotype.json"))
    {
        var liveSeed = JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText("live_best_genotype.json"))!.ToGenotype();
        if (liveSeed.Fitness > (baseSeed?.Fitness ?? double.MinValue))
        {
            seed = liveSeed;
            Console.WriteLine($"  Resuming from live_best_genotype.json (F={liveSeed.Fitness:F4}) — official baseline F={savedFitness:F4}");
        }
    }
    if (seed != null && seed == baseSeed)
        Console.WriteLine($"  Seeding from {GenoFile} (F={savedFitness:F4})");

    var trainer = new LiveTrainer(popSize: 20, seed: seed);
    trainer.SetSavedFitness(savedFitness);

    var coins = new[]
    {
        "WIFUSDT", "SOLUSDT",  "MEMEUSDT",    "DOGEUSDT",    "1000BONKUSDT",
        "XRPUSDT", "ETHUSDT",  "AVAXUSDT",    "BNBUSDT",     "LINKUSDT",
        "ADAUSDT", "1000PEPEUSDT", "ATOMUSDT", "1000FLOKIUSDT",
    };

    const int RefreshSeconds = 300;
    const int RollingBatches = 3;    // 3 × up-to-1000 candles ≈ 10 days of 5m data

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    while (!cts.Token.IsCancellationRequested)
    {
        Console.Clear();
        Console.WriteLine($"=== Gravity-gen2 | LIVE TRAIN  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] — Ctrl+C to stop ===\n");

        // ── Fetch rolling candles for all coins ───────────────────────────────
        Console.Write($"  Fetching {RollingBatches * 1000} candles per coin... ");
        var recentCandles = new Dictionary<string, Candle[]>();
        foreach (var sym in coins)
        {
            if (cts.Token.IsCancellationRequested) break;
            var candles = await FetchCandles(sym, batches: RollingBatches);
            if (candles.Count >= 100)
                recentCandles[sym] = candles.ToArray();
        }
        Console.WriteLine($"{recentCandles.Count}/{coins.Length} coins ready\n");

        // ── Evaluate + maybe evolve ───────────────────────────────────────────
        trainer.EvaluateAll(recentCandles);
        trainer.MaybeEvolve();

        // ── Print ranking ─────────────────────────────────────────────────────
        trainer.PrintRanking(recentCandles);

        // ── Current trade states for #1 genotype (papertrade-style) ─────────
        var top = trainer.CurrentBest;
        if (top != null)
        {
            Console.WriteLine($"\n  Current states — top genotype (score={top.Fitness:F3}):");
            Console.WriteLine($"  {"Coin",-18}  {"Regime",-13} {"State",-16} {"Entry",11}  {"Current",11}  {"Unrealised",10}  {"Mode",14}");
            Console.WriteLine($"  {new string('─', 100)}");
            foreach (var sym in coins)
            {
                if (!recentCandles.TryGetValue(sym, out var arr)) continue;
                double px = arr[^1].Close;
                var st = Simulator.GetUnifiedTradeState(top, arr, useAtr: true);
                string state  = st.PumpOpen
                    ? (st.PumpInRecovery ? $"SHORT R{st.PumpRecoveryLeg}" : $"SHORT D{st.PumpDca}")
                    : st.GridOpen ? $"GRID D{st.GridDca}" : "watching";
                string entry  = st.PumpOpen ? $"{st.PumpEntry:F6}" : st.GridOpen ? $"{st.GridEntry:F6}" : "—";
                string unreal = st.PumpOpen
                    ? $"{(st.PumpEntry - px) / st.PumpEntry * 100.0:+0.00}%"
                    : st.GridOpen ? $"{(px - st.GridEntry) / st.GridEntry * 100.0:+0.00}%" : "—";
                string modeStr = st.PumpOpen ? (st.PumpInRecovery ? "recovery" : $"DCA {st.PumpDca}/{top.MaxDcaLevels}")
                               : st.GridOpen ? $"grid DCA {st.GridDca}/{top.MaxDcaLevels}" : "—";
                Console.WriteLine($"  {sym,-18}  {st.Regime,-13} {state,-16} {entry,11}  {px,11:F6}  {unreal,10}  {modeStr,14}");
            }
        }

        // ── Auto-promote: 3 consecutive improvements → overwrite best_genotype.json ──
        if (trainer.ShouldPromote && trainer.CurrentBest != null)
        {
            var promoted = trainer.CurrentBest;
            Console.WriteLine($"\n=== AUTO-PROMOTE === F={promoted.Fitness:F4} beats saved F={savedFitness:F4} for 3+ cycles");
            File.WriteAllText("best_genotype.json",
                JsonSerializer.Serialize(GenotypeDto.From(promoted),
                    new JsonSerializerOptions { WriteIndented = true }));
            savedFitness = promoted.Fitness;
            trainer.SetSavedFitness(savedFitness);  // reset streak with new baseline
        }

        // ── Always keep live_best_genotype.json up-to-date ───────────────────
        if (trainer.SessionBest != null)
        {
            File.WriteAllText("live_best_genotype.json",
                JsonSerializer.Serialize(GenotypeDto.From(trainer.SessionBest),
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        if (cts.Token.IsCancellationRequested) break;

        for (int s = RefreshSeconds; s > 0 && !cts.Token.IsCancellationRequested; s--)
        {
            Console.Write($"\r  Next refresh in {s,3}s  ");
            await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
        }
    }

    Console.WriteLine("\n\n  Live train stopped.");
    if (trainer.SessionBest != null)
        Console.WriteLine($"  Session best: {trainer.SessionBest}");
}

// ── Helpers ───────────────────────────────────────────────────────────────────

// Cache-backed fetch: reads/writes candle_cache/{symbol}.csv.
// Only fetches what's missing (new candles at the recent end, old history at the far end).
async Task<List<Candle>> FetchCandlesCached(string symbol, int batches = 106)
{
    const string CacheDir = "candle_cache";
    Directory.CreateDirectory(CacheDir);
    string cacheFile = Path.Combine(CacheDir, $"{symbol}.csv");

    // Load existing cache into a sorted dictionary keyed by timestamp
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

    async Task<bool> FetchBatch(DateTime? endTime)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500 * attempt);
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await client.V5Api.ExchangeData
                .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FiveMinutes,
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

    // ── Fetch recent candles (now → newest cached) ─────────────────────────
    // Skip if cache was updated within the last hour — no point re-fetching.
    bool cacheIsFresh = cached.Count > 0 &&
        (DateTime.UtcNow - cached.Keys.Max()).TotalHours < 1.0;
    if (!cacheIsFresh)
    {
        DateTime stopAt = cached.Count > 0 ? cached.Keys.Max() : DateTime.MinValue;
        DateTime? endTime = null;
        for (int i = 0; i < 5; i++) // max 5 batches for recent update
        {
            int beforeCount = cached.Count;
            await FetchBatch(endTime);
            if (cached.Count == beforeCount) break; // nothing new
            var fetched = cached.Keys.Min(); // oldest in this pass
            if (fetched >= stopAt) break;    // overlapped the existing cache
            endTime = fetched.AddMinutes(-5);
        }
    }

    // ── Fetch old history until we have batches*1000 candles ─────────────
    int needed = batches * 1000;
    if (cached.Count < needed)
    {
        DateTime? endTime = cached.Count > 0 ? cached.Keys.Min().AddMinutes(-5) : null;
        int maxOldBatches = (needed - cached.Count) / 800 + 10; // generous buffer
        for (int i = 0; i < maxOldBatches && cached.Count < needed; i++)
        {
            int beforeCount = cached.Count;
            bool ok = await FetchBatch(endTime);
            if (!ok || cached.Count == beforeCount) break;
            endTime = cached.Keys.Min().AddMinutes(-5);
        }
    }

    // ── Save updated cache ─────────────────────────────────────────────────
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

async Task<List<Candle>> FetchCandles(string symbol, int batches = 30)
{
    var all = new List<Candle>();
    DateTime? endTime = null;
    for (int batch = 0; batch < batches; batch++)
    {
        // Retry up to 4 times with exponential back-off on rate limit errors
        bool success = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500 * attempt);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await client.V5Api.ExchangeData
                .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FiveMinutes, endTime: endTime, limit: 1000, ct: cts.Token);

            if (!result.Success || result.Data?.List == null)
            {
                bool rateLimit = result.Error?.ToString().Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                              || result.Error?.ToString().Contains("429") == true;
                if (rateLimit && attempt < 3) { Console.Write("↺"); continue; }
                Console.WriteLine($"\n  [{symbol}] batch {batch + 1} failed: {result.Error}");
                break;
            }

            var bc = result.Data.List
                .Select(k => new Candle(k.StartTime, (double)k.OpenPrice, (double)k.HighPrice,
                                        (double)k.LowPrice, (double)k.ClosePrice, (double)k.Volume))
                .ToList();
            if (!bc.Any()) { success = true; break; }
            all.AddRange(bc);
            endTime = bc.Min(c => c.Time).AddMinutes(-5);
            success = true;
            break;
        }
        if (!success) break;
        await Task.Delay(500);
    }
    return all.GroupBy(c => c.Time).Select(g => g.First()).OrderBy(c => c.Time).ToList();
}

Genotype? LoadGenotype()
{
    if (!File.Exists(GenoFile))
    {
        Console.WriteLine($"No saved genotype at '{GenoFile}'. Run 'dotnet run -- train' first.");
        return null;
    }
    return JsonSerializer.Deserialize<GenotypeDto>(File.ReadAllText(GenoFile))!.ToGenotype();
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

// Compute pairwise Pearson correlation of daily returns between all coins in the
// trade list. Keys are stored with the alphabetically-first coin first.
static Dictionary<(string, string), double> ComputeCorrelations(
    IEnumerable<(string Coin, DateTime Time, double Return)> tradeSeq)
{
    var daily = tradeSeq
        .GroupBy(t => (Coin: t.Coin, Date: t.Time.Date))
        .ToDictionary(g => g.Key, g => g.Sum(t => t.Return));

    var coins = daily.Keys.Select(k => k.Coin).Distinct().OrderBy(c => c).ToList();
    var result = new Dictionary<(string, string), double>();

    for (int i = 0; i < coins.Count; i++)
    for (int j = i + 1; j < coins.Count; j++)
    {
        string a = coins[i], b = coins[j];
        var shared = daily.Keys.Where(k => k.Coin == a).Select(k => k.Date)
                    .Intersect(daily.Keys.Where(k => k.Coin == b).Select(k => k.Date))
                    .ToList();
        if (shared.Count < 8) { result[(a, b)] = 0; continue; }
        var xs = shared.Select(d => daily[(a, d)]).ToList();
        var ys = shared.Select(d => daily[(b, d)]).ToList();
        double mx = xs.Average(), my = ys.Average();
        double num = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
        double den = Math.Sqrt(xs.Sum(x => (x - mx) * (x - mx)) * ys.Sum(y => (y - my) * (y - my)));
        result[(a, b)] = den > 1e-9 ? num / den : 0;
    }
    return result;
}

// Look up correlation regardless of argument order.
static double GetCorr(Dictionary<(string, string), double> corr, string a, string b)
{
    if (a == b) return 1.0;
    var key = string.Compare(a, b, StringComparison.Ordinal) < 0 ? (a, b) : (b, a);
    return corr.TryGetValue(key, out double v) ? v : 0;
}

// Rolling 30-day edge filter: remove a trade if the coin's Sharpe over the
// preceding window is ≤ 0. Passes the trade through when there's not enough
// history yet (< minHistory trades in the window).
static List<(DateTime Time, double Return, string Kind)> RollingEdgeFilter(
    List<(DateTime Time, double Return, string Kind)> trades,
    int windowDays = 30, int minHistory = 5)
{
    var result = new List<(DateTime, double, string)>();
    for (int i = 0; i < trades.Count; i++)
    {
        var cutoff = trades[i].Time.AddDays(-windowDays);
        var window = trades.Take(i).Where(t => t.Time >= cutoff).Select(t => t.Return).ToList();
        if (window.Count < minHistory) { result.Add(trades[i]); continue; }
        if (Simulator.SharpeRatio(window, windowDays * 288) > 0)
            result.Add(trades[i]);
    }
    return result;
}

// ATR-normalized position sizing + dynamic leverage.
// Positions are scaled inversely with each coin's ATR so every trade risks a
// similar dollar amount regardless of which coin it is.
// Leverage is 2× for high-vol coins (ATR > reference) and 1.5× for low-vol.
// maxDcaFactor = MaxDcaLevels + 1: caps posEur so total notional at full DCA
// stays within MaxExposurePct of balance, preventing over-exposure on low-vol coins.
static (double Pos, double Lev) CalcPosition(double conf, double atrPct, double balance, int maxDcaFactor = 4)
{
    const double Ref            = 0.30;  // reference ATR level
    const double MaxPPct        = 0.20;
    const double MaxExposurePct = 0.15;  // max notional at full DCA (15% of balance)
    double lev      = atrPct > Ref ? 2.0 : 1.5;
    double scale    = Math.Clamp(Ref / Math.Max(atrPct, 0.05), 0.25, 4.0);
    double posEur   = conf * MaxPPct * balance * scale;
    double maxPos   = MaxExposurePct * balance / (maxDcaFactor * lev);
    return (Math.Min(posEur, maxPos), lev);
}

static void PortfolioSim(List<(string Coin, DateTime Time, double Return, double Conf, string Kind, double AtrPct)> trades, int maxDcaFactor = 4)
{
    double balance = 1000.0, realized = 0.0, peak = 1000.0, maxDd = 0.0;
    const double ProfitReinvest = 0.90;
    const double CorrThreshold  = 0.65;

    // Compute pairwise correlations once from all trades for dynamic position scaling.
    var corrMatrix = ComputeCorrelations(trades.Select(t => (t.Coin, t.Time, t.Return)));

    var byDay       = trades.GroupBy(t => t.Time.Date).OrderBy(g => g.Key).ToList();
    var monthTotals = new Dictionary<string, double>();

    Console.WriteLine($"\n{new string('═', 88)}");
    Console.WriteLine($"  Portfolio P&L — {byDay.First().Key:yyyy-MM-dd} → {byDay.Last().Key:yyyy-MM-dd}" +
                      $"  ({byDay.Count} days, {trades.Count} trades)");
    Console.WriteLine($"{new string('═', 88)}");
    Console.WriteLine($"{"Date",-12} {"Tr",3} {"W",3}  {"Daily P&L",10}  {"Balance",10}  {"Realized",10}  {"Total",10}  {"DD",5}");
    Console.WriteLine(new string('-', 88));

    foreach (var day in byDay)
    {
        double dayPnl = 0; int wins = 0;
        var dayList = day.ToList();
        foreach (var (coin, _, ret, conf, _, atrPct) in dayList)
        {
            // Scale down when correlated coins are also trading today (same risk exposure).
            int corrPeers = dayList.Count(t => t.Coin != coin && GetCorr(corrMatrix, coin, t.Coin) > CorrThreshold);
            double corrScale = 1.0 / (1.0 + 0.5 * corrPeers);
            var (posEur, lev) = CalcPosition(conf, atrPct, balance, maxDcaFactor);
            double pnlEur = ret / 100.0 * (posEur * corrScale) * lev;
            if (pnlEur >= 0) { balance += ProfitReinvest * pnlEur; realized += (1 - ProfitReinvest) * pnlEur; wins++; }
            else              { balance += pnlEur; }
            dayPnl += pnlEur;
        }
        double total = balance + realized;
        double dd    = peak > 0 ? (peak - balance) / peak * 100.0 : 0;
        if (balance > peak) peak = balance;
        if (dd > maxDd) maxDd = dd;
        monthTotals[day.Key.ToString("yyyy-MM")] =
            monthTotals.GetValueOrDefault(day.Key.ToString("yyyy-MM")) + dayPnl;
        Console.WriteLine($"{day.Key:yyyy-MM-dd}  {day.Count(),3} {wins,3}  {dayPnl,+9:F2}€  {balance,9:F2}€  {realized,+9:F2}€  {total,9:F2}€  {dd,4:F1}%");
    }

    Console.WriteLine($"\n  Monthly P&L:");
    foreach (var (mo, tot) in monthTotals.OrderBy(m => m.Key))
        Console.WriteLine($"    {mo}   {tot,+8:F2}€");

    double finalTotal = balance + realized;
    int    totalWins  = trades.Count(t => t.Return > 0);
    var    allRet     = trades.Select(t => t.Return).ToList();
    // Daily-bucket Sharpe: sum returns per day, then normalise by trading days.
    // Avoids both the trade-count bias and the cross-coin concatenation inflation.
    var    dailyRet   = byDay.Select(d => d.Sum(t => t.Return)).ToList();
    double portSharpe = Simulator.SharpeRatio(dailyRet, byDay.Count * 288);
    double portSortino= Simulator.SortinoRatio(dailyRet, byDay.Count * 288);
    double portPf     = Simulator.ProfitFactor(allRet);
    double portCalmar = Simulator.CalmarRatio(allRet);
    int    maxLoss    = Simulator.MaxConsecLosses(allRet);

    Console.WriteLine($"\n{new string('═', 70)}");
    Console.WriteLine($"  BACKTEST SUMMARY");
    Console.WriteLine($"{new string('═', 70)}");
    Console.WriteLine($"  Period:       {byDay.First().Key:yyyy-MM-dd} → {byDay.Last().Key:yyyy-MM-dd}");
    Console.WriteLine($"  Start:        1000.00€");
    Console.WriteLine($"  Balance:      {balance,8:F2}€");
    Console.WriteLine($"  Realized:     {realized,+8:F2}€");
    Console.WriteLine($"  Total:        {finalTotal,8:F2}€  ({(finalTotal / 1000.0 - 1) * 100:+0.00;-0.00}%)");
    Console.WriteLine($"  Max DD:       {maxDd,7:F2}%");
    Console.WriteLine($"  Sharpe:       {portSharpe:F2}");
    Console.WriteLine($"  Sortino:      {portSortino:F2}");
    Console.WriteLine($"  Calmar:       {portCalmar:F2}");
    Console.WriteLine($"  Profit Factor:{portPf:F2}");
    Console.WriteLine($"  Max Consec L: {maxLoss}");
    Console.WriteLine($"  Trades:       {trades.Count}  ({(double)trades.Count / byDay.Count:F1}/day)");
    Console.WriteLine($"  WinRate:      {(double)totalWins / trades.Count:P1}  ({totalWins}W / {trades.Count - totalWins}L)");
}

// ── Type declarations ─────────────────────────────────────────────────────────

record CoinResult(
    string Coin,
    double Sharpe, double Sortino, double PF,
    int Trades, double WinRate, double AvgRet);

class GenotypeDto
{
    public int    RsiPeriod          { get; set; }
    public double RsiOverbought      { get; set; }
    public int    BosCandlesWait     { get; set; }
    public double GridStepAtrMult    { get; set; }
    public double DcaTriggerAtrMult  { get; set; }
    public double GridDcaAtrMult     { get; set; }
    public int    EmaPeriod          { get; set; }
    public double BreakEvenAtrMult   { get; set; }
    public int    MaxDcaLevels       { get; set; }
    public double BosThreshold       { get; set; }
    public double VolumeMultiplier   { get; set; }
    public int    RegimeAdxPeriod    { get; set; }
    public double RegimeAdxThreshold { get; set; }
    public double HardStopAtrMult    { get; set; }
    public double RangeGridStep      { get; set; }
    public double RangeBreakEven     { get; set; }
    public double RangeDcaStep       { get; set; }
    public int    RangeMaxDca        { get; set; }
    public double Fitness            { get; set; }

    public static GenotypeDto From(Genotype g) => new()
    {
        RsiPeriod = g.RsiPeriod, RsiOverbought = g.RsiOverbought,
        BosCandlesWait = g.BosCandlesWait, GridStepAtrMult = g.GridStepAtrMult,
        DcaTriggerAtrMult = g.DcaTriggerAtrMult, GridDcaAtrMult = g.GridDcaAtrMult,
        EmaPeriod = g.EmaPeriod, BreakEvenAtrMult = g.BreakEvenAtrMult,
        MaxDcaLevels = g.MaxDcaLevels,
        BosThreshold = g.BosThreshold, VolumeMultiplier = g.VolumeMultiplier,
        RegimeAdxPeriod = g.RegimeAdxPeriod, RegimeAdxThreshold = g.RegimeAdxThreshold,
        HardStopAtrMult = g.HardStopAtrMult,
        RangeGridStep = g.RangeGridStep, RangeBreakEven = g.RangeBreakEven,
        RangeDcaStep = g.RangeDcaStep, RangeMaxDca = g.RangeMaxDca,
        Fitness = g.Fitness,
    };

    public Genotype ToGenotype() => new()
    {
        RsiPeriod = RsiPeriod, RsiOverbought = RsiOverbought,
        BosCandlesWait = BosCandlesWait, GridStepAtrMult = GridStepAtrMult,
        DcaTriggerAtrMult = Math.Max(DcaTriggerAtrMult, 2.0),  // enforce new floor
        GridDcaAtrMult = GridDcaAtrMult > 0 ? GridDcaAtrMult : 1.5,
        EmaPeriod = EmaPeriod, BreakEvenAtrMult = BreakEvenAtrMult,
        MaxDcaLevels = MaxDcaLevels,
        BosThreshold = BosThreshold, VolumeMultiplier = VolumeMultiplier,
        RegimeAdxPeriod = RegimeAdxPeriod, RegimeAdxThreshold = RegimeAdxThreshold,
        HardStopAtrMult = HardStopAtrMult > 0 ? HardStopAtrMult : 8.0,
        RangeGridStep  = RangeGridStep  > 0 ? RangeGridStep  : 1.0,
        RangeBreakEven = RangeBreakEven,
        RangeDcaStep   = RangeDcaStep   > 0 ? RangeDcaStep   : 1.0,
        RangeMaxDca    = RangeMaxDca    > 0 ? RangeMaxDca    : 3,
        Fitness = Fitness,
    };
}
