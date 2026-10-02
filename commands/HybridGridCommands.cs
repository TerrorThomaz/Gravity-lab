using Bybit.Net.Clients;
using System.Text.Json;
using GravityGen2.Strategies.HybridGrid;

namespace TradingGA;

// hybridgridtrain [--sides both|long|short] [--seed N]
//
// Trains HybridGrid on Grid's 26-coin pool (train 80%), then reports — never selects on — the
// held-out 20% of those coins and the full history of Config.OosCoins, split by side. The side
// split is the point: a Both genotype's long and short books are scored separately, so a short
// side that is merely riding along on a profitable long side is visible rather than averaged away.
static class HybridGridCommands
{
    private static readonly string[] TrainCoins =
    [
        "SOLUSDT", "ETHUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "AVAXUSDT", "ADAUSDT", "LINKUSDT",
        "DOTUSDT", "MATICUSDT", "ATOMUSDT", "NEARUSDT", "INJUSDT", "OPUSDT", "ARBUSDT", "UNIUSDT",
        "AAVEUSDT", "RUNEUSDT", "WIFUSDT", "1000PEPEUSDT", "APTUSDT", "SUIUSDT", "TIAUSDT", "SEIUSDT",
        "STXUSDT", "JUPUSDT",
    ];

    public static HybridGridSides ResolveSides(string[]? args)
    {
        int k = args is null ? -1 : Array.IndexOf(args, "--sides");
        string v = k >= 0 && k + 1 < args!.Length ? args[k + 1].ToLowerInvariant() : "both";
        return v switch
        {
            "long"  => HybridGridSides.LongOnly,
            "short" => HybridGridSides.ShortOnly,
            _       => HybridGridSides.Both,
        };
    }

    public static string GenoPath(HybridGridSides sides) => sides switch
    {
        HybridGridSides.LongOnly  => "genotypes/hybrid_grid_long_genotype.json",
        HybridGridSides.ShortOnly => "genotypes/hybrid_grid_short_genotype.json",
        _                         => "genotypes/hybrid_grid_genotype.json",
    };

    private static async Task<(string Sym, Candle[] H1)[]> FetchH1(BybitRestClient client, IEnumerable<string> syms)
    {
        var sem = new SemaphoreSlim(4);
        return await Task.WhenAll(syms.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                return (sym, FadeShortSimulator.AggregateCandles(m15.ToArray(), 4));
            }
            finally { sem.Release(); }
        }));
    }

    public static async Task RunTrain(BybitRestClient client, string[]? args = null)
    {
        var sides   = ResolveSides(args);
        int? seed   = GaSearch.ResolveSeed(args);
        var cfg     = FitnessConfig.Load();
        string path = GenoPath(sides);
        Console.WriteLine($"=== Gravity-gen2 | HYBRIDGRIDTRAIN (EMA-side grid, sides={sides}, {TrainCoins.Length} coins) ===");
        GaSearch.AnnounceCommandSeed("hybridgridtrain", seed);

        var fetched = await FetchH1(client, TrainCoins);
        var funding = await CandleFetcher.FetchFundingSessionsAsync(client, fetched.Select(f => f.Sym));

        var coins = new List<HybridGridGA.CoinData>();
        var holdout = new List<(string Sym, Candle[] H1, FundingRateSession? F)>();
        foreach (var (sym, h1) in fetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            int split = DataSplit.Split(h1).Train.Length;
            coins.Add(new HybridGridGA.CoinData(h1[..split], h1[split..], funding.For(sym)));
            holdout.Add((sym, h1[split..], funding.For(sym)));
        }
        if (coins.Count == 0) { Console.WriteLine("No data."); return; }
        Console.WriteLine($"\n  Training on {coins.Count} coins (train {DataSplit.TrainFraction:P0})\n");

        var (best, fit) = new HybridGridGA(sides, cfg: cfg, seed: seed).Train(coins);
        Console.WriteLine($"\nBest: {best}\nFitness={fit:F3}");
        File.WriteAllText(path, JsonSerializer.Serialize(best, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved → {path}\n");

        Report("TRAIN-COIN HOLDOUT (last 20%, report-only)", best, sides, holdout);

        var oos = (await FetchH1(client, Config.OosCoins)).Where(x => x.H1.Length >= 300).ToArray();
        var oosFunding = await CandleFetcher.FetchFundingSessionsAsync(client, oos.Select(o => o.Sym));
        Report($"OOS COINS ({oos.Length} never-trained, full history)", best, sides,
               oos.Select(o => (o.Sym, o.H1, oosFunding.For(o.Sym))).ToList());
    }

    private static void Report(string title, HybridGridGenotype g, HybridGridSides sides,
                               List<(string Sym, Candle[] H1, FundingRateSession? F)> data)
    {
        var trades = data.SelectMany(d => HybridGridSimulator.GetHybridReturns(g, d.H1, sides, d.F)).ToList();
        Console.WriteLine($"─── {title} — per-rung returns, costs + funding included ───");
        Line("both",  trades.Select(t => t.Return).ToList());
        Line("long",  trades.Where(t => t.Kind == HybridGridSimulator.LongKind).Select(t => t.Return).ToList());
        Line("short", trades.Where(t => t.Kind == HybridGridSimulator.ShortKind).Select(t => t.Return).ToList());
        Console.WriteLine("  (rungs within a session are correlated — n overstates independent evidence)\n");

        static void Line(string label, List<double> r)
        {
            if (r.Count == 0) { Console.WriteLine($"  {label,-6} n=0"); return; }
            double gw = r.Where(x => x > 0).Sum(), gl = -r.Where(x => x < 0).Sum();
            string pf = gl > 1e-12 ? (gw / gl).ToString("F2") : "inf";
            double sd = r.Count > 1 ? Math.Sqrt(r.Sum(x => (x - r.Average()) * (x - r.Average())) / (r.Count - 1)) : 0;
            double t  = sd > 1e-12 ? r.Average() / (sd / Math.Sqrt(r.Count)) : 0;
            Console.WriteLine($"  {label,-6} n={r.Count,6}  PF={pf,5}  mean={r.Average(),7:F3}%  " +
                              $"win={100.0 * r.Count(x => x > 0) / r.Count,5:F1}%  t={t,6:F2}");
        }
    }
}
