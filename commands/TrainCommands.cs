using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class TrainCommands
{
    public static async Task RunFadeShortTrain(BybitRestClient client, string[]? args = null, bool invertScreen = false)
    {
        string variant  = ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = VariantGenoPath("fade_short", variant, Config.FadeShortGenoFile);
        string modeLabel = invertScreen ? "RETRAIN (generalisation pass — unknown coins)" : "TRAIN (1h setup + 15m entry/exit, 40 coins, ~3yr)";
        Console.WriteLine($"=== Gravity-gen2 | {modeLabel} ===");
        Console.WriteLine($"Training FadeShort / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        string[] heldOutSyms = ["BTCUSDT", "LTCUSDT", "FILUSDT", "DYDXUSDT", "ENAUSDT"];

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
            // expanded universe — increases OOS trade count by ~40%
            ("TRXUSDT",         1.0),
            ("XLMUSDT",         1.0),
            ("ETCUSDT",         1.0),
            ("ALGOUSDT",        0.9),
            ("HBARUSDT",        0.9),
            ("ICPUSDT",         0.9),
            ("MKRUSDT",         0.9),
            ("CRVUSDT",         0.9),
            ("SNXUSDT",         0.9),
            ("FETUSDT",         0.9),
            ("AXSUSDT",         0.9),
            ("IMXUSDT",         0.9),
            ("1000SHIBUSDT",    0.9),
            ("ORDIUSDT",        0.8),
        };

        var allSymsToFetch = trainCoins.Select(t => (t.Item1, t.Item2))
            .Concat(heldOutSyms.Select(s => (s, 0.0)))
            .ToArray();

        Console.WriteLine($"  Fetching {trainCoins.Length} training + {heldOutSyms.Length} held-out coins (15m → 1h, ~3yr)...");
        var semTrain = new SemaphoreSlim(4);
        var fetchTasks = allSymsToFetch.Select(async ((string sym, double weight) t) =>
        {
            await semTrain.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, t.sym, batches: 113);
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
                heldOutCoins.Add((sym, h1));
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

        {
            Console.WriteLine($"\n  Volume filter (min median 1h vol ≥ ${Config.MinMedianVolUsdM:F1}M):");
            var volFiltered = new List<(string Sym, FadeShortGA.CoinData Cd)>();
            foreach (var nc in namedCoins)
            {
                var volUsd = nc.Cd.TrainCandles.ToArray()
                    .Select(c => c.Close * c.Volume / 1_000_000.0)
                    .OrderBy(v => v)
                    .ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                bool pass = medVol >= Config.MinMedianVolUsdM;
                Console.WriteLine($"    {(pass ? "✓" : "✗")} {nc.Sym,-20} medVol=${medVol:F2}M/h");
                if (pass) volFiltered.Add(nc);
            }
            namedCoins = volFiltered;
            Console.WriteLine($"  → {namedCoins.Count} coins pass volume filter\n");
        }

        if (namedCoins.Count == 0) { Console.WriteLine("No coins passed volume filter."); return; }

        FadeShortGenotype? seed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
            if (candidate.Fitness > 0)
            {
                seed = candidate;
                Console.WriteLine($"  Seeding from {genoPath}: {seed}");
            }
            else
                Console.WriteLine($"  Skipping seed (fitness ≤ 0 — previous run failed)");
        }

        if (seed != null)
        {
            string screenLabel = invertScreen
                ? "Screening coins (inverted — seed-weak coins only, PF<1.3):"
                : "Screening coins (seed expectancy on train data):";
            Console.WriteLine($"\n  {screenLabel}");
            int before = namedCoins.Count;
            var screened = namedCoins
                .Where(nc =>
                {
                    var returns = FadeShortSimulator.GetFadeShortReturns(seed, nc.Cd.TrainCandles.Span)
                                               .Select(t => t.Return).ToList();
                    double exp = returns.Count >= 10 ? returns.Average() : double.NegativeInfinity;
                    double pf  = returns.Count >= 10 ? Simulator.ProfitFactor(returns) : 0;
                    bool pass  = invertScreen
                        ? returns.Count >= 10 && pf < 1.3
                        : exp > 0 && pf >= 1.3;
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

        Console.WriteLine("─── Swing GA training (universal — all coins) ───");
        var best = new FadeShortGA(80, 150, verbose: true, cfg: cfg).Run(coinData, seed);

        Console.WriteLine("\n─── Bayesian refinement (60 TPE iterations) ───");
        var rngBo  = new Random(42);
        var boHistory = new List<(double[] Params, double Fitness)>
        {
            (best.ToVector(), best.Fitness)
        };
        var boResult = BayesianOptimizer.Refine(
            boHistory,
            FadeShortGenotype.Bounds,
            v =>
            {
                var g = FadeShortGenotype.FromVector(v);
                return coinData.SelectMany(cd =>
                    FadeShortSimulator.GetFadeShortReturns(g, cd.TrainCandles.Span))
                    .Select(t => t.Return)
                    .DefaultIfEmpty(-1.0)
                    .Average();
            },
            iterations: 60,
            rng: rngBo);
        var boParams = boResult.OrderByDescending(h => h.Fitness).First().Params;
        var boGeno   = FadeShortGenotype.FromVector(boParams);
        boGeno.Fitness = boResult.OrderByDescending(h => h.Fitness).First().Fitness;
        if (boGeno.Fitness > best.Fitness) { best = boGeno; Console.WriteLine($"  TPE improved: {best}"); }
        else Console.WriteLine($"  GA elite kept (TPE did not improve)");

        Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(FadeShortGenotypeDto.From(best, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Per-cluster GA training ───");
        var clusterGroups = namedCoins
            .GroupBy(nc => CoinClusterHelper.Classify(nc.Cd.TrainCandles.ToArray()))
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
                : best;

            var clusterBest = new FadeShortGA(60, 100, verbose: false).Run(clusterCoins, clusterSeed);
            Console.WriteLine($"  [{clLabel}] best: {clusterBest}");
            File.WriteAllText(clFile, JsonSerializer.Serialize(FadeShortGenotypeDto.From(clusterBest),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {clFile}");
        }

        Console.WriteLine("\n─── Overfit check (train 75% vs val 12.5%) ───");
        var tRet = coinData.SelectMany(cd =>
            FadeShortSimulator.GetFadeShortReturns(best, cd.TrainCandles.Span).Select(t => t.Return)).ToList();
        var vRet = coinData.SelectMany(cd =>
            FadeShortSimulator.GetFadeShortReturns(best, cd.ValCandles.Span).Select(t => t.Return)).ToList();

        int tCC = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
        int vCC = coinData.Sum(cd => cd.ValCandles.Length) * 12;
        CandleFetcher.PrintSplitStats("Train 75%", tRet, tCC);
        CandleFetcher.PrintSplitStats("Val  12.5%", vRet, vCC);

        double vExp = vRet.Count > 0 ? vRet.Average() : 0;
        double tExp = tRet.Count > 0 ? tRet.Average() : 0;
        Console.WriteLine(vExp < tExp * 0.4 || vExp <= 0
            ? "\n  !! Possible overfit — val expectancy < 40% of train"
            : "\n  OK — val expectancy within acceptable range");

        Console.WriteLine("\n─── OOS test (last 12.5% — not seen by GA) ───");
        var oosRet = namedCoins
            .Where(nc => testBySymbol.ContainsKey(nc.Sym))
            .SelectMany(nc => FadeShortSimulator.GetFadeShortReturns(best, testBySymbol[nc.Sym]).Select(t => t.Return))
            .ToList();
        int oosCC = namedCoins
            .Where(nc => testBySymbol.ContainsKey(nc.Sym))
            .Sum(nc => testBySymbol[nc.Sym].Length * 12);
        CandleFetcher.PrintSplitStats("OOS  12.5%", oosRet, oosCC);

        if (heldOutCoins.Count > 0)
        {
            Console.WriteLine($"\n─── Double validation ({heldOutCoins.Count} held-out coins — never seen during training) ───");
            Console.WriteLine($"  {"Coin",-20}  {"Trades",6}  {"WR",5}  {"AvgRet",8}  {"PF",6}  {"Sortino",8}");
            Console.WriteLine($"  {"────",-20}  {"──────",6}  {"──",5}  {"──────",8}  {"──",6}  {"───────",8}");

            var allHeldRet = new List<double>();
            int allHeldCC  = 0;

            foreach (var (sym, h1) in heldOutCoins)
            {
                var r = FadeShortSimulator.GetFadeShortReturns(best, h1).Select(t => t.Return).ToList();
                allHeldRet.AddRange(r);
                allHeldCC += h1.Length * 12;

                double wr   = r.Count > 0 ? (double)r.Count(x => x > 0) / r.Count : 0;
                double avg  = r.Count > 0 ? r.Average() : 0;
                double pf   = r.Count > 0 ? Simulator.ProfitFactor(r) : 0;
                double sort = r.Count > 0 ? Simulator.SortinoRatio(r, h1.Length * 12) : 0;
                Console.WriteLine($"  {sym,-20}  {r.Count,6}  {wr,5:P0}  {avg,+8:F2}%  {pf,6:F2}  {sort,8:F2}");
            }

            Console.WriteLine();
            CandleFetcher.PrintSplitStats("Held-out agg", allHeldRet, allHeldCC);
        }

        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    internal static string ResolveVariant(string[]? args)
    {
        if (args != null)
        {
            int idx = Array.IndexOf(args, "--variant");
            if (idx >= 0 && idx + 1 < args.Length) return args[idx + 1];
        }
        return Environment.GetEnvironmentVariable("GRAVITY_VARIANT") ?? "default";
    }

    internal static string VariantGenoPath(string key, string variant, string defaultPath)
        => variant == "default" ? defaultPath : $"genotypes/{key}_{variant}_genotype.json";

}
