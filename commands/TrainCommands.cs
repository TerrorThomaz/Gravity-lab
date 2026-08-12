using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

// ── Post-GA refinement: why there isn't any ───────────────────────────────────
// Every train command in commands/ used to run a 60-iteration TPE pass AFTER its GA:
//
//     var boHistory = new List<...> { (best.ToVector(), best.Fitness) };
//     var boResult  = BayesianOptimizer.Refine(boHistory, X.Bounds,
//         v => <mean per-trade return of X over the whole unfolded train span>, 60, rng);
//     if (boGeno.Fitness > best.Fitness) best = boGeno;   // decides what is written to disk
//
// That pass was removed from all six sites (FadeShort, FadeShortLowVol, FadeLong,
// RipShort, DipLong, SwingLong) because it was unsound in three independent ways:
//
//   1. WRONG OBJECTIVE. The lambda scored a genotype by the raw arithmetic mean of its
//      per-trade returns over the entire training span: no walk-forward folds, no
//      MinTradesPerFold floor, no drawdown / quality / retention multipliers, no regime
//      gate. `count > 0` was the only guard, so three lucky trades scored arbitrarily high.
//   2. INCOMMENSURABLE ACCEPTANCE TEST. `best.Fitness` coming out of a GA is that GA's
//      canonical fold-aggregated score (and, in every GA here, the HELD-OUT one — see e.g.
//      FadeShortGA.Run, which overwrites best.Fitness with the validation score before
//      returning). Comparing a mean per-trade return (~0.3–1.0) against that score
//      (order 10–10,000, negative for weak genotypes) is a category error: whether TPE
//      overwrote the GA winner was decided by the accidental relative magnitude of two
//      unrelated numbers, not by which genotype was better.
//   3. COLD START. The history was seeded with ONE observation, and
//      BayesianOptimizer.Suggest returns a uniform RandomPoint while history.Count < 12,
//      so 11 of the 60 iterations were uniform random over the whole bounds box and the
//      KDE was fitted on a handful of points for a while after that.
//
// The fix could not be "make the outer pass optimise the GA's objective": every GA keeps
// its Fitness(...) private (FadeShortGA's is `private static FitnessFromCache` over a
// private CoinCache record), and reconstructing it in the command would mean
// reimplementing fold windows, vol coverage, MinTradesPerFold and the FoldScoreHelper
// aggregation — i.e. re-creating the same class of bug the moment either copy drifts.
//
// Refinement therefore lives INSIDE the GA, where the canonical Fitness is reachable.
// FadeLong / RipShort / DipLong / SwingLong / Grid / GridShort / RegimeRouter already run
// a 60-iteration TPE pass there, seeded from the whole elite island and evaluated with
// `Fitness(g, coins, useValidation: false)` — same function, same data, same scale as the
// GA's own selection. FadeShortGA has no such inner pass; adding one belongs in
// src/strategies/fade_short/FadeShortGA.cs (see the handoff note in the review).
// ─────────────────────────────────────────────────────────────────────────────
static class TrainCommands
{
    public static async Task RunFadeShortTrain(BybitRestClient client, string[]? args = null, bool invertScreen = false)
    {
        string variant  = ResolveVariant(args);
        int?   rngSeed  = GaSearch.ResolveSeed(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = VariantGenoPath("fade_short", variant, Config.FadeShortGenoFile);
        string modeLabel = invertScreen ? "RETRAIN (generalisation pass — unknown coins)" : "TRAIN (1h setup + 15m entry/exit, 40 coins, ~3yr)";
        Console.WriteLine($"=== Gravity-gen2 | {modeLabel} ===");
        Console.WriteLine($"Training FadeShort / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        GaSearch.AnnounceCommandSeed(invertScreen ? "retrain" : "train", rngSeed);
        Console.WriteLine();

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

        DateTime globalFitCutoff = namedCoins.Count > 0
            ? namedCoins.Max(nc => nc.Cd.ValCandles.Span[^1].Time)
            : DateTime.MinValue;

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

        // --no-seed: train from scratch. FadeShort has NEVER had a fair search — the genotype was
        // created 2026-06-22 and every retrain since warm-started from the incumbent, which until
        // the seeded-init fix collapsed ~79% of the population to exact clones. Grid, the other
        // strategy in that position, improved materially once retrained from
        // scratch against the correct cost model.
        bool noSeed = args != null && Array.IndexOf(args, "--no-seed") >= 0;
        if (noSeed) Console.WriteLine("  [NO-SEED] training from scratch — no incumbent");

        FadeShortGenotype? seed = null;
        if (!noSeed && File.Exists(genoPath))
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
        var best = new FadeShortGA(80, 150, verbose: true, cfg: cfg, seed: rngSeed).Run(coinData, seed);

        // Post-GA TPE pass removed — see the "Post-GA refinement" note at the top of this
        // file. FadeShortGA has no inner BO pass yet, so FadeShort is GA-only for now.
        Console.WriteLine($"\n─── Refinement ───");
        Console.WriteLine("  No post-GA pass (FadeShortGA selects and freezes its own elite).");
        Console.WriteLine($"  Selected genotype fitness (GA scale, held-out): {best.Fitness:F4}");

        Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(FadeShortGenotypeDto.From(best, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Validation suite ───");
        try
        {
            var mcResult = MonteCarloTest.Run(
                coinData.SelectMany(cd => FadeShortSimulator.GetFadeShortReturns(best, cd.TrainCandles.Span).Select(t => t.Return)).ToList(),
                permutations: 1000);
            MonteCarloTest.PrintReport(mcResult, "FadeShort");
        }
        catch (Exception ex) { Console.WriteLine($"  MonteCarloTest skipped: {ex.Message}"); }

        try
        {
            var ewReport = ExpandingWindowValidation.RunFadeShort(coinData, cfg);
            ExpandingWindowValidation.PrintReport(ewReport);
        }
        catch (Exception ex) { Console.WriteLine($"  ExpandingWindowValidation skipped: {ex.Message}"); }

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

            // Offset per cluster so the cluster runs do not all replay the identical RNG stream
            // under one --seed; unchecked so a seed near int.MaxValue wraps instead of throwing.
            int? clusterRngSeed = rngSeed is int rs ? unchecked(rs + (int)clusterType + 1) : null;
            var clusterBest = new FadeShortGA(60, 100, verbose: false, seed: clusterRngSeed).Run(clusterCoins, clusterSeed);
            Console.WriteLine($"  [{clLabel}] best: {clusterBest}");
            File.WriteAllText(clFile, JsonSerializer.Serialize(FadeShortGenotypeDto.From(clusterBest),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved → {clFile}");
        }

        // ┌── KNOWN GAP: FUNDING IS NOT PRICED ANYWHERE IN THIS COMMAND ─────────────────────┐
        // │ Every figure below — the overfit check, the held-out coins, the time-embargoed   │
        // │ slice and the regime-stratified buckets — is produced by the SINGLE-TIMEFRAME    │
        // │ overload `FadeShortSimulator.GetFadeShortReturns(g, candles)`, which takes no    │
        // │ FundingRateSession parameter at all (only the h1+m15 overload does). So funding  │
        // │ is charged at the flat interest-rate floor here, while `combinedbacktest` and    │
        // │ `oosbacktest` now charge real per-symbol rates. The two are NOT comparable, and  │
        // │ this command's numbers are the optimistic ones.                                  │
        // │                                                                                  │
        // │ Wiring it requires adding a `FundingRateSession?` parameter to the single-TF     │
        // │ overload in src/strategies/swing_long/SwingSimulator.cs and a symbol field to    │
        // │ FadeShortGA.CoinData — both outside this change's ownership. Handoff, not an     │
        // │ oversight.                                                                       │
        // └──────────────────────────────────────────────────────────────────────────────────┘
        Console.WriteLine("\n─── Overfit check (train 75% vs val 12.5%) ───");
        Console.WriteLine("  (funding priced at the interest-rate floor — this command has no per-symbol");
        Console.WriteLine("   funding path; combinedbacktest/oosbacktest now charge real rates and will be worse)");
        var tRet = coinData.SelectMany(cd =>
            FadeShortSimulator.GetFadeShortReturns(best, cd.TrainCandles.Span).Select(t => t.Return)).ToList();
        var vRet = coinData.SelectMany(cd =>
            FadeShortSimulator.GetFadeShortReturns(best, cd.ValCandles.Span).Select(t => t.Return)).ToList();

        int tCC = coinData.Sum(cd => cd.TrainCandles.Length) * 12;
        int vCC = coinData.Sum(cd => cd.ValCandles.Length) * 12;
        CandleFetcher.PrintSplitStats("Train 75%", tRet, tCC);
        CandleFetcher.PrintSplitStats("Val  12.5%", vRet, vCC);

        var btcForRegime = heldOutCoins.FirstOrDefault(c => c.Sym == "BTCUSDT").H1;
        if (btcForRegime != null && btcForRegime.Length > 220)
        {
            int btcTrainEnd = (int)(btcForRegime.Length * 0.75);
            int btcValStart = btcTrainEnd;
            int btcValEnd = (int)(btcForRegime.Length * 0.875);
            if (btcValEnd > btcValStart && btcValEnd <= btcForRegime.Length)
            {
                var btcTrain = btcForRegime[..btcTrainEnd];
                var btcVal = btcForRegime[btcValStart..btcValEnd];
                PrintRegimeContext(btcTrain, "Train window", "Short");
                PrintRegimeContext(btcVal, "Val window", "Short");
            }
        }

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

        if (heldOutCoins.Count > 0)
        {
            Console.WriteLine($"\n─── Double validation, time-embargoed (held-out coins, dates ≥ {globalFitCutoff:yyyy-MM-dd} only) ───");
            Console.WriteLine("  Same held-out coins, but restricted to dates after every training coin's fit window ends —");
            Console.WriteLine("  a genuine forward-time test, not just a different-symbol test on the same historical window.");
            Console.WriteLine($"  {"Coin",-20}  {"Trades",6}  {"WR",5}  {"AvgRet",8}  {"PF",6}  {"Sortino",8}");
            Console.WriteLine($"  {"────",-20}  {"──────",6}  {"──",5}  {"──────",8}  {"──",6}  {"───────",8}");

            var allEmbRet = new List<double>();
            int allEmbCC  = 0;

            foreach (var (sym, h1) in heldOutCoins)
            {
                var embargoed = h1.Where(c => c.Time >= globalFitCutoff).ToArray();
                var r = FadeShortSimulator.GetFadeShortReturns(best, embargoed).Select(t => t.Return).ToList();
                allEmbRet.AddRange(r);
                allEmbCC += embargoed.Length * 12;

                double wr   = r.Count > 0 ? (double)r.Count(x => x > 0) / r.Count : 0;
                double avg  = r.Count > 0 ? r.Average() : 0;
                double pf   = r.Count > 0 ? Simulator.ProfitFactor(r) : 0;
                double sort = r.Count > 0 ? Simulator.SortinoRatio(r, embargoed.Length * 12) : 0;
                Console.WriteLine($"  {sym,-20}  {r.Count,6}  {wr,5:P0}  {avg,+8:F2}%  {pf,6:F2}  {sort,8:F2}");
            }

            Console.WriteLine();
            CandleFetcher.PrintSplitStats("Held-out agg (embargoed)", allEmbRet, allEmbCC);
        }

        var btcHeldOut = heldOutCoins.FirstOrDefault(c => c.Sym == "BTCUSDT").H1;
        if (heldOutCoins.Count > 0 && btcHeldOut != null && btcHeldOut.Length > 220)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcHeldOut);
            Console.WriteLine("\n─── Double validation, time-embargoed + regime-stratified ───");
            Console.WriteLine("  Same embargoed window, bucketed by BTC regime at each trade's entry time — a single");
            Console.WriteLine("  blended window can be net-Bull or net-Bear, which silently favors whichever strategy");
            Console.WriteLine("  direction matches it rather than proving real generalization.");

            const int minTradesForRegime = 20;
            foreach (var regime in new[] { MarketRegime.Bear, MarketRegime.Bull, MarketRegime.Ranging, MarketRegime.HighVol })
            {
                var bucketRet = new List<double>();
                int bucketCC  = 0;
                foreach (var (sym, h1) in heldOutCoins)
                {
                    var embargoed = h1.Where(c => c.Time >= globalFitCutoff).ToArray();
                    if (embargoed.Length == 0) continue;

                    var trades = FadeShortSimulator.GetFadeShortReturns(best, embargoed);
                    var tradeTags = RegimeBarLookup.TagRegimes(btcSeries, trades.Select(t => t.Time).ToList());
                    for (int i = 0; i < trades.Count; i++)
                        if (tradeTags[i] == regime) bucketRet.Add(trades[i].Return);

                    var candleTags = RegimeBarLookup.TagRegimes(btcSeries, embargoed.Select(c => c.Time).ToList());
                    bucketCC += candleTags.Count(t => t == regime) * 12;
                }

                if (bucketRet.Count < minTradesForRegime)
                    Console.WriteLine($"  {regime,-8}  insufficient data ({bucketRet.Count} trades, need ≥{minTradesForRegime}) — not reported");
                else
                    CandleFetcher.PrintSplitStats($"  {regime}", bucketRet, bucketCC);
            }
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

    internal static void PrintRegimeContext(Candle[] btcH1, string windowLabel, string strategyRegime)
    {
        if (btcH1.Length < 220)
        {
            Console.WriteLine($"  {windowLabel}: insufficient BTC data for regime classification");
            return;
        }
        var series = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
        var regimeCounts = series.GroupBy(s => s.Regime)
            .ToDictionary(g => g.Key, g => g.Count());
        int total = series.Length;
        
        Console.WriteLine($"  {windowLabel} regime distribution (BTC H1):");
        foreach (var regime in new[] { MarketRegime.Bull, MarketRegime.Bear, MarketRegime.Ranging, MarketRegime.HighVol })
        {
            int count = regimeCounts.GetValueOrDefault(regime, 0);
            double pct = 100.0 * count / total;
            Console.WriteLine($"    {regime,-8}: {count,5} bars ({pct:F1}%)");
        }
        
        bool regimeMismatch = (strategyRegime == "Bear" && regimeCounts.GetValueOrDefault(MarketRegime.Bull, 0) > total * 0.5) ||
                              (strategyRegime == "Bull" && regimeCounts.GetValueOrDefault(MarketRegime.Bear, 0) > total * 0.5) ||
                              (strategyRegime == "Short" && regimeCounts.GetValueOrDefault(MarketRegime.Bull, 0) > total * 0.6) ||
                              (strategyRegime == "Long" && regimeCounts.GetValueOrDefault(MarketRegime.Bear, 0) > total * 0.6);
        
        if (regimeMismatch)
            Console.WriteLine($"  ⚠ {windowLabel} regime mismatches {strategyRegime} strategy — low expectancy may be regime-driven, not overfit");
    }

}
