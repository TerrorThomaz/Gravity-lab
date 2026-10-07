using System.Text.Json;
using Bybit.Net.Clients;

namespace TradingGA;

// hmmgridbacktest — PROTOTYPE comparison: baseline Grid vs HMM-range Grid on the same val windows.
//
//   dotnet run -- hmmgridbacktest [--source=coin|btc] [--enter=0.6] [--exit=0.4]
//                                 [--minbars=24] [--tpfrac=0.5] [--stopatr=1.0]
//
// --source=coin  annotate each coin's own h1 with the (BTC-trained) HMM — features are scale-free,
//                so the model transfers, but alts' larger EMA deviations may read as "less ranging".
// --source=btc   use BTC's P(ranging) for every coin; levels still come from the coin's own lows/highs.
//
// Fairness: both variants get the same coins, the same DataSplit val window, the same cost and
// funding model (funding: null → interest-rate floor for both), and each gets the same train-window
// screen gridbacktest uses (exp > 0, PF >= 1.15, >= 5 trades). Unscreened totals are printed too,
// because the screen itself is a selection step that can flatter either side.
//
// Evidence status: the HMM was fitted on BTC's TRAIN split only (hmmtrain cuts at DataSplit), so the
// val window is out-of-time for the HMM. But the four HmmRangeGridParams were picked by hand, and
// any sweep over them on val turns val into a training set. Final word belongs to never-seen coins.
static class HmmGridCommands
{
    public static async Task RunHmmGridBacktest(BybitRestClient client, string[] args)
    {
        string source = "coin";
        double enter = HmmRangeLevels.DefaultEnterProb, exit = HmmRangeLevels.DefaultExitProb;
        var p = new HmmRangeGridParams();
        foreach (var a in args.Skip(1))
        {
            var kv = a.TrimStart('-').Split('=', 2);
            if (kv.Length != 2) continue;
            double v = double.TryParse(kv[1], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            switch (kv[0])
            {
                case "source":  source = kv[1].ToLowerInvariant(); break;
                case "enter":   enter = v; break;
                case "exit":    exit = v; break;
                case "minbars": p = p with { MinSegBars = (int)v }; break;
                case "tpfrac":  p = p with { TpFrac = v }; break;
                case "stopatr": p = p with { StopBufferAtr = v }; break;
            }
        }

        Console.WriteLine($"=== Gravity-gen2 | HMM-RANGE GRID vs GRID ({Config.BacktestCoins.Length} coins, {DataSplit.ValLabel}) ===");
        Console.WriteLine($"  source={source}  enter={enter:F2} exit={exit:F2}  {p}\n");

        if (!File.Exists(Config.GridGenoFile) || !File.Exists(Config.HmmGenoFile))
        {
            Console.WriteLine($"Need both '{Config.GridGenoFile}' and '{Config.HmmGenoFile}'.");
            return;
        }
        var g   = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        var hmm = JsonSerializer.Deserialize<HmmGenotypeDto>(File.ReadAllText(Config.HmmGenoFile))!.ToGenotype();
        Console.WriteLine($"  HMM: {hmm.StatesN} states, ranging-labelled: " +
                          string.Join(",", Enumerable.Range(0, hmm.StatesN).Where(s => hmm.StateLabels[s] == MarketRegime.Ranging)));

        var symbols = Config.BacktestCoins.Contains("BTCUSDT") ? Config.BacktestCoins : [.. Config.BacktestCoins, "BTCUSDT"];
        var sem = new SemaphoreSlim(4);
        var fetched = await Task.WhenAll(symbols.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                return (sym, h1: FadeShortSimulator.AggregateCandles(m15.ToArray(), 4));
            }
            finally { sem.Release(); }
        }));

        Dictionary<DateTime, double>? btcP = null;
        if (source == "btc")
        {
            var btc = fetched.First(f => f.sym == "BTCUSDT").h1;
            var btcLv = HmmRangeLevels.Compute(btc, hmm, enter, exit);
            btcP = new Dictionary<DateTime, double>(btc.Length);
            for (int i = 0; i < btc.Length; i++) btcP[btc[i].Time] = btcLv.PRange[i];
        }

        var baseAll = new List<double>(); var baseScr = new List<double>();
        var hmmAll  = new List<double>(); var hmmScr  = new List<double>();
        int baseCoins = 0, hmmCoins = 0, sessions = 0, stops = 0, regimeExits = 0, timeouts = 0;
        long valBars = 0, valInRange = 0;

        Console.WriteLine($"\n  {"Coin",-16} {"InRng%",6} | {"Grid n",6} {"PF",5} {"Avg%",6} | {"HMM n",6} {"PF",5} {"Avg%",6}");
        Console.WriteLine("  " + new string('-', 68));

        foreach (var (sym, h1) in fetched.Where(f => Config.BacktestCoins.Contains(f.sym)))
        {
            if (h1.Length < 300) continue;
            var (trainEnd, valEnd) = DataSplit.Bounds(h1.Length);
            var h1Train = h1[..trainEnd];
            var h1Val   = h1[trainEnd..valEnd];

            RangeLevels lv;
            if (btcP != null)
            {
                var aligned = h1.Select(c => btcP.TryGetValue(c.Time, out var q) ? q : 0.0).ToArray();
                lv = HmmRangeLevels.FromProbabilities(h1, aligned, enter, exit);
            }
            else lv = HmmRangeLevels.Compute(h1, hmm, enter, exit);
            var lvTrain = HmmRangeLevels.Slice(lv, 0, trainEnd);
            var lvVal   = HmmRangeLevels.Slice(lv, trainEnd, valEnd);

            valBars    += h1Val.Length;
            valInRange += lvVal.InRange.Count(b => b);

            var bTrain = GridSimulator.GetGridReturns(g, h1Train).Select(t => t.Return).ToList();
            var bVal   = GridSimulator.GetGridReturns(g, h1Val).Select(t => t.Return).ToList();
            var hTrain = HmmRangeGridSimulator.Run(g, h1Train, lvTrain, p).Trades.Select(t => t.Return).ToList();
            var hRes   = HmmRangeGridSimulator.Run(g, h1Val, lvVal, p);
            var hVal   = hRes.Trades.Select(t => t.Return).ToList();

            baseAll.AddRange(bVal); hmmAll.AddRange(hVal);
            if (PassesScreen(bTrain)) { baseScr.AddRange(bVal); baseCoins++; }
            if (PassesScreen(hTrain)) { hmmScr.AddRange(hVal); hmmCoins++; }
            sessions += hRes.Sessions; stops += hRes.StopOuts; regimeExits += hRes.RegimeExits; timeouts += hRes.Timeouts;

            double inRng = h1Val.Length > 0 ? (double)lvVal.InRange.Count(b => b) / h1Val.Length : 0;
            Console.WriteLine($"  {sym,-16} {inRng,6:P0} | {bVal.Count,6} {Pf(bVal),5:F2} {Avg(bVal),6:+0.00;-0.00} | " +
                              $"{hVal.Count,6} {Pf(hVal),5:F2} {Avg(hVal),6:+0.00;-0.00}");
        }

        Console.WriteLine($"\n{new string('═', 78)}");
        Console.WriteLine($"  SUMMARY ({DataSplit.ValLabel}; funding floor for both; costs identical)");
        Console.WriteLine($"{new string('═', 78)}");
        Console.WriteLine($"  Val bars in an HMM ranging segment: {(valBars > 0 ? (double)valInRange / valBars : 0):P1}");
        Console.WriteLine($"  HMM sessions: {sessions}  (stop-outs {stops}, regime exits {regimeExits}, timeouts {timeouts})\n");
        Console.WriteLine($"  {"",-30} {"Trades",7} {"WR",6} {"Avg%",7} {"PF",5} {"Sum%",8}");
        Row("Grid      — all coins",                   baseAll);
        Row("HMM-range — all coins",                   hmmAll);
        Row($"Grid      — screened ({baseCoins} coins)", baseScr);
        Row($"HMM-range — screened ({hmmCoins} coins)",  hmmScr);
        Console.WriteLine("\n  Sum% is an unweighted per-fill sum, not a portfolio return — use it to compare the two");
        Console.WriteLine("  rows, not as a P&L figure. Tuning the HMM params against these val numbers spends the val set.");
    }

    static bool PassesScreen(List<double> r) => r.Count >= 5 && r.Average() > 0 && Simulator.ProfitFactor(r) >= 1.15;
    static double Pf(List<double> r)  => r.Count > 0 ? Simulator.ProfitFactor(r) : 0;
    static double Avg(List<double> r) => r.Count > 0 ? r.Average() : 0;

    static void Row(string label, List<double> r)
    {
        double wr = r.Count > 0 ? (double)r.Count(x => x > 0) / r.Count : 0;
        Console.WriteLine($"  {label,-30} {r.Count,7} {wr,6:P0} {Avg(r),7:+0.000;-0.000} {Pf(r),5:F2} {r.Sum(),8:+0.0;-0.0}");
    }
}
