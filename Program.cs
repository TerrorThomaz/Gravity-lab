using TradingGA;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using System.Text.Json;

const string GenoFile = "best_genotype.json";
var client = new BybitRestClient();

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
switch (mode)
{
    case "train":      await RunTrain();      break;
    case "backtest":   await RunBacktest();   break;
    case "papertrade": await RunPaperTrade(); break;
    case "livetrain":  await RunLiveTrain();  break;
    default:
        Console.WriteLine("Gravity-gen2 — usage:");
        Console.WriteLine("  dotnet run -- train       GA on WIF (regime-aware pump+grid)");
        Console.WriteLine("  dotnet run -- backtest    1yr backtest on 14 coins");
        Console.WriteLine("  dotnet run -- papertrade  Live signals per coin");
        Console.WriteLine("  dotnet run -- livetrain   20 genotypes evaluated on live data, evolves hourly");
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
    PrintSplitStats("Train 80%", tRet);
    PrintSplitStats("Val   20%", vRet);

    double tSh = Simulator.SharpeRatio(tRet);
    double vSh = Simulator.SharpeRatio(vRet);
    Console.WriteLine(vSh < tSh * 0.5 || vSh <= 0
        ? "\n  !! Possible overfit — val Sharpe < 50% of train"
        : "\n  OK — val Sharpe within acceptable range (but see bias warning above)");

    Console.WriteLine($"\nNext: dotnet run -- backtest   ← authoritative test on 14 unseen coins");
}

// ══════════════════════════════════════════════════════════════════════════════
//  BACKTEST
// ══════════════════════════════════════════════════════════════════════════════
async Task RunBacktest()
{
    Console.WriteLine("=== Gravity-gen2 | BACKTEST (~1yr, 14 coins, regime-aware) ===");
    Console.WriteLine("    Authoritative test — these coins were not used in training or genotype selection.\n");
    var g = LoadGenotype(); if (g == null) return;
    Console.WriteLine($"Genotype: {g}\n");

    var testCoins = new[]
    {
        "WIFUSDT", "SOLUSDT",  "MEMEUSDT",    "ATOMUSDT",    "DOGEUSDT",
        "1000BONKUSDT", "XRPUSDT", "ETHUSDT", "AVAXUSDT",    "BNBUSDT",
        "LINKUSDT", "ADAUSDT", "1000PEPEUSDT", "1000FLOKIUSDT",
    };

    var allTrades  = new List<(string Coin, DateTime Time, double Return, double Conf, string Kind)>();
    var coinStats  = new List<CoinResult>();

    Console.WriteLine($"{"Coin",-18} {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine(new string('-', 70));

    foreach (var sym in testCoins)
    {
        Console.Write($"  {sym,-16}... ");
        var candles = await FetchCandles(sym, batches: 106);
        if (candles.Count < 500) { Console.WriteLine("skip (no data)"); continue; }

        var arr   = candles.ToArray();
        int split = (int)(arr.Length * 0.8);
        var tArr  = arr[..split];
        var vArr  = arr[split..];

        var tRet  = Simulator.GetUnifiedReturns(g, tArr, true).Select(t => t.Return).ToList();
        double conf = Simulator.ComputeConfidence(tRet);

        var vTrades = Simulator.GetUnifiedReturns(g, vArr, true);
        var vRet    = vTrades.Select(t => t.Return).ToList();

        double sh   = Simulator.SharpeRatio(vRet);
        double sort = Simulator.SortinoRatio(vRet);
        double pf   = Simulator.ProfitFactor(vRet);
        double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
        double avg  = vRet.Count > 0 ? vRet.Average() : 0;

        foreach (var (t, ret, kind) in vTrades)
            allTrades.Add((sym, t, ret, conf, kind));

        Console.WriteLine($"{sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
        coinStats.Add(new CoinResult(sym, sh, sort, pf, vRet.Count, wr, avg));
    }

    if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

    allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
    PortfolioSim(allTrades);

    Console.WriteLine($"\n  Per-coin (sorted by Sharpe):");
    Console.WriteLine($"  {"Coin",-18} {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
    Console.WriteLine($"  {new string('-', 66)}");
    foreach (var r in coinStats.OrderByDescending(c => c.Sharpe))
        Console.WriteLine($"  {r.Coin,-18} {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WinRate,5:P0}  {r.AvgRet,+7:F2}%");
}

// ══════════════════════════════════════════════════════════════════════════════
//  PAPER TRADE
// ══════════════════════════════════════════════════════════════════════════════
async Task RunPaperTrade()
{
    Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (regime-aware) — Ctrl+C to stop ===\n");
    var g = LoadGenotype(); if (g == null) return;
    Console.WriteLine($"Genotype: {g}\n");

    var coins = new[]
    {
        "WIFUSDT", "SOLUSDT",  "MEMEUSDT",    "DOGEUSDT",    "1000BONKUSDT",
        "XRPUSDT", "ETHUSDT",  "AVAXUSDT",    "BNBUSDT",     "LINKUSDT",
        "ADAUSDT", "1000PEPEUSDT", "ATOMUSDT", "1000FLOKIUSDT",
    };

    // Refresh every 5 minutes (one 5m candle) — aligns with candle close
    const int RefreshSeconds = 300;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    while (!cts.Token.IsCancellationRequested)
    {
        Console.Clear();
        Console.WriteLine($"=== Gravity-gen2 | PAPER TRADE  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]  Ctrl+C to stop ===\n");
        Console.WriteLine($"{"Coin",-18}  {"Regime",-12} {"State",-16} {"Entry",11}  {"Current",11}  {"Unrealised",10}  {"Mode",8}");
        Console.WriteLine(new string('-', 92));

        foreach (var sym in coins)
        {
            if (cts.Token.IsCancellationRequested) break;

            var candles = await FetchCandles(sym, batches: 5);
            if (candles.Count < 200) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }

            double px = candles[^1].Close;
            var    st = Simulator.GetUnifiedTradeState(g, candles.ToArray(), true);

            string state  = st.PumpOpen
                ? (st.PumpInRecovery ? $"SHORT R{st.PumpRecoveryLeg}" : $"SHORT D{st.PumpDca}")
                : "watching";
            string entry  = st.PumpOpen ? $"{st.PumpEntry:F6}" : "—";
            string unreal = st.PumpOpen
                ? $"{(st.PumpEntry - px) / st.PumpEntry * 100.0:+0.00}%" : "—";
            string mode   = st.PumpOpen ? (st.PumpInRecovery ? "recovery" : $"DCA {st.PumpDca}/{g.MaxDcaLevels}") : "—";

            Console.WriteLine($"  {sym,-18}  {st.Regime,-12} {state,-16} {entry,11}  {px,11:F6}  {unreal,10}  {mode,8}");
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
}

// ══════════════════════════════════════════════════════════════════════════════
//  LIVE TRAIN
// ══════════════════════════════════════════════════════════════════════════════
async Task RunLiveTrain()
{
    Console.WriteLine("=== Gravity-gen2 | LIVE TRAIN (20 genotypes, rolling ~10d window) — Ctrl+C to stop ===\n");
    Console.WriteLine("  Fitness = mean Sharpe across 14 coins − 0.4×σ (cross-coin variance penalty).");
    Console.WriteLine("  Threshold genes that overfit to one coin will rank low; universal genes survive.\n");

    var seed    = LoadGenotype();   // seed population from saved best if available
    var trainer = new LiveTrainer(popSize: 20, seed: seed);

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

        // ── Auto-save if session best improves on saved genotype ──────────────
        var savedFitness = LoadGenotype()?.Fitness ?? double.MinValue;
        if (trainer.SessionBest != null && trainer.SessionBestScore > savedFitness)
        {
            Console.WriteLine($"\n  ★ Session best beats saved genotype — writing live_best_genotype.json");
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

            var result = await client.V5Api.ExchangeData
                .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FiveMinutes, endTime: endTime, limit: 1000);

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

static void PrintSplitStats(string label, List<double> r)
{
    if (r.Count == 0) { Console.WriteLine($"  {label,-12} (no trades)"); return; }
    double sh   = Simulator.SharpeRatio(r);
    double sort = Simulator.SortinoRatio(r);
    double pf   = Simulator.ProfitFactor(r);
    double wr   = (double)r.Count(x => x > 0) / r.Count;
    double avg  = r.Average();
    Console.WriteLine($"  {label,-12} Sh={sh:F2}  Sort={sort:F2}  PF={pf:F2}  WR={wr:P0}  Tr={r.Count}  Avg={avg:+0.00}%");
}

static void PortfolioSim(List<(string Coin, DateTime Time, double Return, double Conf, string Kind)> trades)
{
    double balance = 1000.0, realized = 0.0, peak = 1000.0, maxDd = 0.0;
    const double MaxPosEur = 100.0, MaxPosPct = 0.05, ProfitReinvest = 0.90;

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
        foreach (var (_, _, ret, conf, _) in day)
        {
            double posEur = Math.Min(conf * MaxPosPct * balance, MaxPosEur);
            double pnlEur = ret / 100.0 * posEur;
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
    double portSharpe = Simulator.SharpeRatio(allRet);
    double portSortino= Simulator.SortinoRatio(allRet);
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
    public int    EmaPeriod          { get; set; }
    public double TimeframeBlend     { get; set; }
    public double BreakEvenAtrMult   { get; set; }
    public int    PumpThresholdIdx   { get; set; }
    public int    MaxDcaLevels       { get; set; }
    public double BosThreshold       { get; set; }
    public double VolumeMultiplier   { get; set; }
    public int    RegimeAdxPeriod    { get; set; }
    public double RegimeAdxThreshold { get; set; }
    public double Fitness            { get; set; }

    public static GenotypeDto From(Genotype g) => new()
    {
        RsiPeriod = g.RsiPeriod, RsiOverbought = g.RsiOverbought,
        BosCandlesWait = g.BosCandlesWait, GridStepAtrMult = g.GridStepAtrMult,
        DcaTriggerAtrMult = g.DcaTriggerAtrMult, EmaPeriod = g.EmaPeriod,
        TimeframeBlend = g.TimeframeBlend, BreakEvenAtrMult = g.BreakEvenAtrMult,
        PumpThresholdIdx = g.PumpThresholdIdx, MaxDcaLevels = g.MaxDcaLevels,
        BosThreshold = g.BosThreshold, VolumeMultiplier = g.VolumeMultiplier,
        RegimeAdxPeriod = g.RegimeAdxPeriod, RegimeAdxThreshold = g.RegimeAdxThreshold,
        Fitness = g.Fitness,
    };

    public Genotype ToGenotype() => new()
    {
        RsiPeriod = RsiPeriod, RsiOverbought = RsiOverbought,
        BosCandlesWait = BosCandlesWait, GridStepAtrMult = GridStepAtrMult,
        DcaTriggerAtrMult = DcaTriggerAtrMult, EmaPeriod = EmaPeriod,
        TimeframeBlend = TimeframeBlend, BreakEvenAtrMult = BreakEvenAtrMult,
        PumpThresholdIdx = PumpThresholdIdx, MaxDcaLevels = MaxDcaLevels,
        BosThreshold = BosThreshold, VolumeMultiplier = VolumeMultiplier,
        RegimeAdxPeriod = RegimeAdxPeriod, RegimeAdxThreshold = RegimeAdxThreshold,
        Fitness = Fitness,
    };
}
