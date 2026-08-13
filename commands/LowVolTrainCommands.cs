using Bybit.Net.Clients;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingGA;

static class LowVolTrainCommands
{
    // Persist a vol-variant genotype WITH the ATR band it was trained on.
    //
    // These four sites serialize the raw genotype record, which has no AtrLow/AtrHigh — those
    // fields live only on the DTO. A file written without them deserializes to the DTO defaults
    // [0, 9999], i.e. the BASE band. That is not a cosmetic tag: VariantRouter.Select picks the
    // NARROWEST band containing the current ATR ratio and breaks ties by array order, and the
    // glob puts variant files before the appended default — so a bandless "lowvol" file stops
    // specialising the low-vol regime and instead SHADOWS the base genotype at every ATR level.
    // This is exactly how the retired high-vol variants broke, and it is invisible in every
    // report: the symptom is a base genotype that is simply never selected.
    // NB: the band comes from Config, NOT from cfg — FitnessConfig.Load() returns the defaults
    // [0, 9999] here, so stamping cfg would write the base band and reintroduce the shadowing.
    static void SaveVariant(string path, object geno)
    {
        var node = JsonSerializer.SerializeToNode(geno)!.AsObject();
        node["AtrLow"]  = Config.LowVolAtrLow;
        node["AtrHigh"] = Config.LowVolAtrHigh;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task RunLowVolTrain(BybitRestClient client, string[]? args = null)
    {
        Console.WriteLine("=== Gravity-gen2 | LOWVOLTRAIN (Low-volatility optimized variants) ===");
        Console.WriteLine("Training low-vol variants: FadeShortLowVol, DipLongLowVol, SwingLongLowVol, RipShortLowVol");
        Console.WriteLine("ATR ratio threshold: < 0.8 (low volatility regime)");
        GaSearch.AnnounceCommandSeed("lowvoltrain", GaSearch.ResolveSeed(args));
        Console.WriteLine();

        await RunFadeShortLowVolTrain(client, args);
        await RunDipLongLowVolTrain(client, args);
        await RunSwingLongLowVolTrain(client, args);
        await RunRipShortLowVolTrain(client, args);

        Console.WriteLine("\n=== All low-vol variants trained successfully ===");
    }

    public static async Task RunFadeShortLowVolTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant = TrainCommands.ResolveVariant(args);
        int?   rngSeed = GaSearch.ResolveSeed(args);
        var cfg = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("fade_short_lowvol", variant, "genotypes/fade_short_lowvol_genotype.json");
        Console.WriteLine($"=== Gravity-gen2 | FADESHORTLOWVOLTRAIN (low-vol fade short, {Config.BacktestCoins.Length} coins) ===");
        Console.WriteLine($"Training FadeShortLowVol / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                int h1Start = Math.Max(0, h1Full.Length - TrainWindowH1);
                int m15Start = h1Start * 4;
                var h1 = h1Full[h1Start..];
                var m15 = m15Full.ToArray()[m15Start..];
                return (sym, h1Full, m15Full.ToArray(), h1, m15);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coins = new List<(string Sym, Candle[] H1Full, Candle[] M15Full, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1Full, m15Full, h1, m15) in fetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            var volUsd = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) { Console.WriteLine($"  {sym}: skip (vol=${medVol:F2}M/h)"); continue; }
            coins.Add((sym, h1Full, m15Full, h1, m15));
        }

        var gaCoins = new List<FadeShortGA.CoinData>();
        int held = 0;
        for (int ci = 0; ci < coins.Count; ci++)
        {
            var (sym, _, _, h1, m15) = coins[ci];
            if (ci % 5 == 0)
            {
                gaCoins.Add(new FadeShortGA.CoinData(Array.Empty<Candle>(), h1));
                held++;
                Console.WriteLine($"  {sym}: held-out OOS ({h1.Length} h1 bars)");
            }
            else
            {
                int split = DataSplit.Split(h1).Train.Length;
                int m15Split = Math.Min(split * 4, m15.Length);
                gaCoins.Add(new FadeShortGA.CoinData(h1[..split], h1[split..]));
                Console.WriteLine($"  {sym}: train/val split at {split}");
            }
        }
        if (gaCoins.Count == 0) { Console.WriteLine("No data."); return; }
        Console.WriteLine($"\n  {gaCoins.Count} coins: {gaCoins.Count - held} training · {held} held-out OOS\n");

        FadeShortGenotype? seed = File.Exists(genoPath)
            ? JsonSerializer.Deserialize<FadeShortGenotype>(File.ReadAllText(genoPath))
            : null;
        if (seed != null) Console.WriteLine($"  Seed: {seed}");
        else Console.WriteLine("  Training from scratch");

        var best = new FadeShortGA(80, 150, verbose: true, cfg: cfg, seed: rngSeed).RunLowVol(gaCoins, seed);

        // Post-GA TPE pass removed — see the "Post-GA refinement" note at the top of
        // commands/TrainCommands.cs. This site was the worst of the six: it did not even
        // guard the swap behind a comparison. It ALWAYS replaced the GA winner with the TPE
        // champion (selected on mean per-trade return, and on TRAIN+VAL candles at that),
        // then stamped the GA's own fitness onto that different genotype before saving — so
        // the fitness recorded in the JSON described a genotype that was never scored.
        Console.WriteLine("\n─── Refinement ───");
        Console.WriteLine("  No post-GA pass (FadeShortGA selects and freezes its own elite).");

        Console.WriteLine($"\n  Best: {best}");
        Console.WriteLine($"  Fitness (GA scale, held-out): {best.Fitness:F4}");

        SaveVariant(genoPath, best);
        Console.WriteLine($"  Saved to {genoPath}  (routing band [{Config.LowVolAtrLow}, {Config.LowVolAtrHigh}])");
    }

    public static async Task RunDipLongLowVolTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant = TrainCommands.ResolveVariant(args);
        int?   rngSeed = GaSearch.ResolveSeed(args);
        var cfg = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("dip_long_lowvol", variant, "genotypes/dip_long_lowvol_genotype.json");
        Console.WriteLine($"=== Gravity-gen2 | DIPLONGLOWVOLTRAIN (low-vol dip long, {Config.BacktestCoins.Length} coins) ===");
        Console.WriteLine($"Training DipLongLowVol / variant={variant}\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                int h1Start = Math.Max(0, h1Full.Length - TrainWindowH1);
                int m15Start = h1Start * 4;
                var h1 = h1Full[h1Start..];
                var m15 = m15Full.ToArray()[m15Start..];
                return (sym, h1Full, m15Full.ToArray(), h1, m15);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coins = new List<(string Sym, Candle[] H1Full, Candle[] M15Full, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1Full, m15Full, h1, m15) in fetched)
        {
            if (h1.Length < 150) continue;
            var volUsd = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;
            coins.Add((sym, h1Full, m15Full, h1, m15));
        }

        var gaCoins = new List<DipLongGA.CoinData>();
        for (int ci = 0; ci < coins.Count; ci++)
        {
            var (sym, _, _, h1, m15) = coins[ci];
            if (ci % 5 == 0)
            {
                gaCoins.Add(new DipLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
            }
            else
            {
                int split = DataSplit.Split(h1).Train.Length;
                int m15Split = Math.Min(split * 4, m15.Length);
                gaCoins.Add(new DipLongGA.CoinData(h1[..split], h1[split..], m15[..m15Split], m15[m15Split..]));
            }
        }
        if (gaCoins.Count == 0) { Console.WriteLine("No data."); return; }

        DipLongGenotype? seed = File.Exists(genoPath)
            ? JsonSerializer.Deserialize<DipLongGenotype>(File.ReadAllText(genoPath))
            : null;

        var best = new DipLongGA(80, 150, verbose: true, cfg: cfg, seed: rngSeed).RunLowVol(gaCoins, seed);

        Console.WriteLine($"\n  Best: {best}");
        Console.WriteLine($"  Fitness: {best.Fitness:F4}");

        SaveVariant(genoPath, best);
        Console.WriteLine($"  Saved to {genoPath}  (routing band [{Config.LowVolAtrLow}, {Config.LowVolAtrHigh}])");
    }

    public static async Task RunSwingLongLowVolTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant = TrainCommands.ResolveVariant(args);
        int?   rngSeed = GaSearch.ResolveSeed(args);
        var cfg = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("swing_long_lowvol", variant, "genotypes/swing_long_lowvol_genotype.json");
        Console.WriteLine($"=== Gravity-gen2 | SWINGLONGLOWVOLTRAIN (low-vol swing long, {Config.BacktestCoins.Length} coins) ===");
        Console.WriteLine($"Training SwingLongLowVol / variant={variant}\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                int h1Start = Math.Max(0, h1Full.Length - TrainWindowH1);
                int m15Start = h1Start * 4;
                var h1 = h1Full[h1Start..];
                var m15 = m15Full.ToArray()[m15Start..];
                return (sym, h1Full, m15Full.ToArray(), h1, m15);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coins = new List<(string Sym, Candle[] H1Full, Candle[] M15Full, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1Full, m15Full, h1, m15) in fetched)
        {
            if (h1.Length < 150) continue;
            var volUsd = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;
            coins.Add((sym, h1Full, m15Full, h1, m15));
        }

        var gaCoins = new List<SwingLongGA.CoinData>();
        for (int ci = 0; ci < coins.Count; ci++)
        {
            var (sym, _, _, h1, m15) = coins[ci];
            if (ci % 5 == 0)
            {
                gaCoins.Add(new SwingLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
            }
            else
            {
                int split = DataSplit.Split(h1).Train.Length;
                int m15Split = Math.Min(split * 4, m15.Length);
                gaCoins.Add(new SwingLongGA.CoinData(h1[..split], h1[split..], m15[..m15Split], m15[m15Split..]));
            }
        }
        if (gaCoins.Count == 0) { Console.WriteLine("No data."); return; }

        SwingLongGenotype? seed = File.Exists(genoPath)
            ? JsonSerializer.Deserialize<SwingLongGenotype>(File.ReadAllText(genoPath))
            : null;

        var best = new SwingLongGA(80, 150, verbose: true, cfg: cfg, seed: rngSeed).RunLowVol(gaCoins, seed);

        Console.WriteLine($"\n  Best: {best}");
        Console.WriteLine($"  Fitness: {best.Fitness:F4}");

        SaveVariant(genoPath, best);
        Console.WriteLine($"  Saved to {genoPath}  (routing band [{Config.LowVolAtrLow}, {Config.LowVolAtrHigh}])");
    }

    public static async Task RunRipShortLowVolTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant = TrainCommands.ResolveVariant(args);
        int?   rngSeed = GaSearch.ResolveSeed(args);
        var cfg = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("rip_short_lowvol", variant, "genotypes/rip_short_lowvol_genotype.json");
        Console.WriteLine($"=== Gravity-gen2 | RIPSHORTLOWVOLTRAIN (low-vol rip short, {Config.BacktestCoins.Length} coins) ===");
        Console.WriteLine($"Training RipShortLowVol / variant={variant}\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                int h1Start = Math.Max(0, h1Full.Length - TrainWindowH1);
                int m15Start = h1Start * 4;
                var h1 = h1Full[h1Start..];
                var m15 = m15Full.ToArray()[m15Start..];
                return (sym, h1Full, m15Full.ToArray(), h1, m15);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coins = new List<(string Sym, Candle[] H1Full, Candle[] M15Full, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1Full, m15Full, h1, m15) in fetched)
        {
            if (h1.Length < 150) continue;
            var volUsd = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;
            coins.Add((sym, h1Full, m15Full, h1, m15));
        }

        var gaCoins = new List<RipShortGA.CoinData>();
        for (int ci = 0; ci < coins.Count; ci++)
        {
            var (sym, _, _, h1, m15) = coins[ci];
            if (ci % 5 == 0)
            {
                gaCoins.Add(new RipShortGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
            }
            else
            {
                int split = DataSplit.Split(h1).Train.Length;
                int m15Split = Math.Min(split * 4, m15.Length);
                gaCoins.Add(new RipShortGA.CoinData(h1[..split], h1[split..], m15[..m15Split], m15[m15Split..]));
            }
        }
        if (gaCoins.Count == 0) { Console.WriteLine("No data."); return; }

        RipShortGenotype? seed = File.Exists(genoPath)
            ? JsonSerializer.Deserialize<RipShortGenotype>(File.ReadAllText(genoPath))
            : null;

        var best = new RipShortGA(80, 150, verbose: true, cfg: cfg, seed: rngSeed).RunLowVol(gaCoins, seed);

        Console.WriteLine($"\n  Best: {best}");
        Console.WriteLine($"  Fitness: {best.Fitness:F4}");

        SaveVariant(genoPath, best);
        Console.WriteLine($"  Saved to {genoPath}  (routing band [{Config.LowVolAtrLow}, {Config.LowVolAtrHigh}])");
    }
}
