using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class LongTrainCommands
{
    private const int BearWindowMinBars = 200; // ~8 days of 1h bars

    private static List<(DateTime Start, DateTime End)> GetBearWindows(RegimeBar[] series, int minBars, double minConf = 0.0)
    {
        var windows  = new List<(DateTime, DateTime)>();
        DateTime runStart = default;
        DateTime runEnd   = default;
        int      runLen   = 0;
        foreach (var bar in series)
        {
            if (bar.Regime == MarketRegime.Bear && bar.Confidence >= minConf)
            {
                if (runLen == 0) runStart = bar.Time;
                runEnd = bar.Time;
                runLen++;
            }
            else if (runLen > 0)
            {
                if (runLen >= minBars) windows.Add((runStart, runEnd));
                runLen = 0;
            }
        }
        if (runLen >= minBars) windows.Add((runStart, runEnd));
        return windows;
    }

    private static T[] FilterToWindows<T>(T[] items, Func<T, DateTime> getTime,
        List<(DateTime Start, DateTime End)> windows)
    {
        if (windows.Count == 0) return items;
        return items.Where(x => { var t = getTime(x); return windows.Any(w => t >= w.Start && t <= w.End); })
                    .ToArray();
    }

    public static async Task RunCoevolve(BybitRestClient client, string[]? args = null)
    {
        string variant = TrainCommands.ResolveVariant(args);
        var    cfg     = FitnessConfig.Load();
        Console.WriteLine("=== Gravity-gen2 | COEVOLVETRAIN (FadeLong + DipLong + SwingLong + Router + DynamicGuard, 4 cycles) ===");
        Console.WriteLine($"Training Coevolve / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        string flPath = TrainCommands.VariantGenoPath("fade_long",     variant, Config.FadeLongGenoFile);
        string dlPath = TrainCommands.VariantGenoPath("dip_long",      variant, Config.DipLongGenoFile);
        string slPath = TrainCommands.VariantGenoPath("swing_long",    variant, Config.SwingLongGenoFile);
        string rrPath = TrainCommands.VariantGenoPath("regime_router", variant, Config.RouterGenoFile);
        string dgPath = TrainCommands.VariantGenoPath("dynamic_guard", variant, Config.DynamicGuardGenoFile);

        FadeShortGenotype? fsSeed = File.Exists(Config.FadeShortGenoFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype()
            : null;
        if (fsSeed == null)
            Console.WriteLine("  No FadeShort genotype — FadeShort trade list will be empty this run.");
        else
            Console.WriteLine($"  FadeShort seed  : {fsSeed}");

        FadeLongGenotype? flSeed = File.Exists(flPath)
            ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(flPath))!.ToGenotype()
            : null;
        if (flSeed is { Fitness: > 0 }) Console.WriteLine($"  FadeLong seed   : {flSeed}");
        else { flSeed = null; Console.WriteLine("  FadeLong seed   : none (training from scratch)"); }

        DipLongGenotype? dlSeed = File.Exists(dlPath)
            ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(dlPath))!.ToGenotype()
            : null;
        if (dlSeed is { Fitness: > 0 }) Console.WriteLine($"  DipLong seed    : {dlSeed}");
        else { dlSeed = null; Console.WriteLine("  DipLong seed    : none (training from scratch)"); }

        SwingLongGenotype? slSeed = File.Exists(slPath)
            ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(slPath))!.ToGenotype()
            : null;
        if (slSeed is { Fitness: > 0 }) Console.WriteLine($"  SwingLong seed  : {slSeed}");
        else { slSeed = null; Console.WriteLine("  SwingLong seed  : none (training from scratch)"); }

        RegimeRouterGenotype? routerSeed = File.Exists(rrPath)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(rrPath))!.ToGenotype()
            : null;
        if (routerSeed is { Fitness: > 0 }) Console.WriteLine($"  Router seed     : {routerSeed}");
        else { routerSeed = null; Console.WriteLine("  Router seed     : none (training from scratch)"); }

        DynamicGuardGenotype? dgSeed = File.Exists(dgPath)
            ? JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(File.ReadAllText(dgPath))!.ToGenotype()
            : null;
        if (dgSeed != null) Console.WriteLine($"  DynamicGuard seed: {dgSeed}");
        else                Console.WriteLine("  DynamicGuard seed: none (training from scratch)");

        GridGenotype? gridSeed = File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()
            : null;
        if (gridSeed != null) Console.WriteLine($"  Grid seed       : {gridSeed}");
        else                  Console.WriteLine("  Grid seed       : not found — Grid excluded from router training");

        Console.WriteLine($"\n  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var semCo = new SemaphoreSlim(4);
        var coFetched = await Task.WhenAll(Config.BacktestCoins.Select(async sym =>
        {
            await semCo.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full  = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                return (sym, h1Full, m15: m15Full.ToArray());
            }
            finally { semCo.Release(); }
        }));

        var coPassed = new List<(string Sym, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1, m15) in coFetched)
        {
            if (h1.Length < 200) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) { Console.WriteLine($"  {sym}: skip (vol=${medVol:F2}M/h)"); continue; }
            coPassed.Add((sym, h1, m15));
        }
        Console.WriteLine($"  {coPassed.Count} coins pass volume filter\n");

        var btcEntry = coPassed.FirstOrDefault(x => x.Sym == "BTCUSDT");
        if (btcEntry.H1 is not { Length: > 200 })
        { Console.WriteLine("BTCUSDT data insufficient — cannot build regime series."); return; }

        var ethEntry = coPassed.FirstOrDefault(x => x.Sym == "ETHUSDT");
        var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.H1);
        var ethSeries = ethEntry.H1 is { Length: > 200 }
            ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.H1) : null;

        Console.WriteLine($"  BTC regime series: {btcSeries.Length} bars  " +
            $"({btcSeries[0].Time:yyyy-MM-dd} → {btcSeries[^1].Time:yyyy-MM-dd})");
        if (ethSeries != null) Console.WriteLine($"  ETH regime series: {ethSeries.Length} bars");

        var flCoins = new List<FadeLongGA.CoinData>();
        for (int ci = 0; ci < coPassed.Count; ci++)
        {
            var (sym, h1, m15) = coPassed[ci];
            if (ci % 5 == 0)
            {
                flCoins.Add(new FadeLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
            }
            else
            {
                var (valStart, valEnd) = CandleFetcher.FindLastRegimeBlock(h1, wantBull: false);
                if (valStart >= 0)
                {
                    int m15Ve = Math.Min((valEnd + 1) * 4, m15.Length);
                    flCoins.Add(new FadeLongGA.CoinData(
                        h1[..valStart],        h1[valStart..(valEnd + 1)],
                        m15[..(valStart * 4)], m15[(valStart * 4)..m15Ve]));
                }
                else
                {
                    flCoins.Add(new FadeLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                }
            }
        }
        Console.WriteLine($"  FadeLong: {flCoins.Count} coins " +
            $"({flCoins.Count(c => c.TrainH1.Length > 0)} training / {flCoins.Count(c => c.TrainH1.Length == 0)} held-out)");

        var dlCoins = new List<DipLongGA.CoinData>();
        foreach (var (sym, h1, m15) in coPassed)
        {
            if (sym == "BTCUSDT" || h1.Length < 500) continue;
            int split = (int)(h1.Length * 0.80);
            int m15sp = Math.Min(split * 4, m15.Length);
            dlCoins.Add(new DipLongGA.CoinData(
                h1[..split],      h1[split..],
                m15[..m15sp],     m15[m15sp..]));
        }
        Console.WriteLine($"  DipLong : {dlCoins.Count} coins (full 3yr, 80/20 split)");

        // SwingLong shares the same 80/20 split as DipLong — re-use the same coin windows.
        var slCoins = dlCoins
            .Select(c => new SwingLongGA.CoinData(c.TrainH1, c.ValH1, c.TrainM15, c.ValM15))
            .ToList();
        Console.WriteLine($"  SwingLong: {slCoins.Count} coins (same split as DipLong)\n");

        var allCoins = coPassed.Select(x => (x.H1, x.M15)).ToList<(Candle[] H1, Candle[] M15)>();

        var data   = new CoevolveGA.AllData(flCoins, dlCoins, slCoins, allCoins, btcSeries, ethSeries, btcEntry.H1, gridSeed);
        var result = new CoevolveGA().Run(data, fsSeed, flSeed, dlSeed, slSeed, routerSeed, dgSeed);

        File.WriteAllText(flPath,
            JsonSerializer.Serialize(FadeLongGenotypeDto.From(result.FadeLong, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n  Saved FadeLong   → {flPath}  {result.FadeLong}");

        File.WriteAllText(dlPath,
            JsonSerializer.Serialize(DipLongGenotypeDto.From(result.DipLong, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved DipLong    → {dlPath}  {result.DipLong}");

        File.WriteAllText(slPath,
            JsonSerializer.Serialize(SwingLongGenotypeDto.From(result.SwingLong, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved SwingLong  → {slPath}  {result.SwingLong}");

        File.WriteAllText(rrPath,
            JsonSerializer.Serialize(RegimeRouterGenotypeDto.From(result.Router),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved Router     → {rrPath}  {result.Router}");

        var dgDto = new DynamicGuardGenotypeDto(result.DynamicGuard.AtrLookback, result.DynamicGuard.AtrTrigger,
            result.DynamicGuard.MomLookback, result.DynamicGuard.MomThreshold,
            result.DynamicGuard.SizeFloor, result.DynamicGuard.Fitness,
            result.DynamicGuard.PanicTrigger, result.DynamicGuard.RecoveryBars);
        File.WriteAllText(dgPath,
            JsonSerializer.Serialize(dgDto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved DynGuard   → {dgPath}  {result.DynamicGuard}");

        Console.WriteLine("\nNext: dotnet run -- fulltest");
    }

    public static async Task RunFadeLongTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("fade_long", variant, Config.FadeLongGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | FADELONGTRAIN (oversold bounce, 1h setup + 15m entry/exit, {Config.BacktestCoins.Length} coins, ~3yr) ===");
        Console.WriteLine($"Training FadeLong / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var semFl = new SemaphoreSlim(4);
        var flFetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await semFl.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                Console.WriteLine($"  {sym}: {m15.Count} 15m → {h1.Length} h1 (~{h1.Length / 24.0:F0}d)");
                return (sym, h1, m15.ToArray());
            }
            finally { semFl.Release(); }
        });
        var flFetched = await Task.WhenAll(flFetchTasks);

        // Load router genotype to align bear-window thresholds with live routing
        RegimeRouterGenotype? flRouterG = File.Exists(Config.RouterGenoFile)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()
            : null;
        int    bearMinBars = flRouterG != null ? (int)flRouterG.BearMinBars : BearWindowMinBars;
        double bearMinConf = flRouterG != null ? flRouterG.BearMinConf      : 0.0;
        Console.WriteLine(flRouterG != null
            ? $"  Router bear thresholds: ≥{bearMinBars} bars / conf≥{bearMinConf:F2}"
            : $"  No router genotype — using default ≥{bearMinBars} bars");

        // Build BTC bear windows to restrict training data to regime-relevant periods
        var btcFetched = flFetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        var bearWindows = new List<(DateTime Start, DateTime End)>();
        if (btcFetched.h1 is { Length: > 220 })
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcFetched.h1);
            bearWindows = GetBearWindows(btcSeries, bearMinBars, bearMinConf);
            Console.WriteLine($"  BTC bear windows ({bearMinBars}+ bar runs, conf≥{bearMinConf:F2}): {bearWindows.Count}");
            foreach (var (s, e) in bearWindows)
                Console.WriteLine($"    {s:yyyy-MM-dd} → {e:yyyy-MM-dd}  ({(e - s).TotalDays:F0}d)");
        }
        else
        {
            Console.WriteLine("  BTC data insufficient for bear-window filtering — using full history");
            // Fetch BTC separately if not in BacktestCoins
            var btcM15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: 113);
            var btcH1  = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);
            if (btcH1.Length > 220)
            {
                var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
                bearWindows = GetBearWindows(btcSeries, bearMinBars, bearMinConf);
                Console.WriteLine($"  BTC bear windows ({bearMinBars}+ bar runs, conf≥{bearMinConf:F2}): {bearWindows.Count}");
            }
        }

        var flPassed = new List<(string Sym, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1, m15) in flFetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) { Console.WriteLine($"  {sym}: skip (vol=${medVol:F2}M/h)"); continue; }
            flPassed.Add((sym, h1, m15));
        }

        var flCoins = new List<FadeLongGA.CoinData>();
        int flHeld = 0, flRegime = 0, flFallback = 0;
        for (int ci = 0; ci < flPassed.Count; ci++)
        {
            var (sym, h1, m15) = flPassed[ci];
            if (ci % 5 == 0)
            {
                flCoins.Add(new FadeLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                flHeld++;
                Console.WriteLine($"  {sym}: held-out OOS ({h1.Length} h1 bars)");
            }
            else
            {
                var (valStart, valEnd) = CandleFetcher.FindLastRegimeBlock(h1, wantBull: false);
                if (valStart >= 0)
                {
                    int m15ValEnd = Math.Min((valEnd + 1) * 4, m15.Length);

                    // Apply bear-window filter to training candles
                    var h1TrainFiltered  = FilterToWindows(h1[..valStart],        c => c.Time, bearWindows);
                    var m15TrainFiltered = FilterToWindows(m15[..(valStart * 4)], c => c.Time, bearWindows);

                    if (h1TrainFiltered.Length < 50)
                    {
                        Console.WriteLine($"  {sym}: skip training (< 50 bear-window bars after filter)");
                        flHeld++;
                        continue;
                    }

                    flCoins.Add(new FadeLongGA.CoinData(
                        h1TrainFiltered,               h1[valStart..(valEnd + 1)],
                        m15TrainFiltered,              m15[(valStart * 4)..m15ValEnd]));
                    flRegime++;
                    Console.WriteLine($"  {sym}: regime-val bars [{valStart}..{valEnd}] ({valEnd - valStart + 1} bear bars)  train={h1TrainFiltered.Length} bear-window h1 bars");
                }
                else
                {
                    flCoins.Add(new FadeLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                    flHeld++;
                    Console.WriteLine($"  {sym}: held-out OOS (no sustained bear block — excluded from training)");
                }
            }
        }
        if (flCoins.Count == 0) { Console.WriteLine("No data."); return; }
        Console.WriteLine($"\n  {flCoins.Count} coins: {flCoins.Count - flHeld} training ({flRegime} regime-split + {flFallback} fallback) · {flHeld} held-out OOS\n");

        FadeLongGenotype? flSeed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
            if (candidate.Fitness > 0) { flSeed = candidate; Console.WriteLine($"  Seeding from {genoPath}: {flSeed}"); }
            else Console.WriteLine("  Skipping seed (fitness ≤ 0 — training from scratch)");
        }

        var flBest = new FadeLongGA(80, 150, verbose: true, cfg: cfg).Run(flCoins, flSeed);

        Console.WriteLine("\n─── Bayesian refinement for FadeLong (60 TPE iterations) ───");
        var flRng = new Random(42);
        var flBoHistory = new List<(double[] Params, double Fitness)>
        {
            (flBest.ToVector(), flBest.Fitness)
        };
        var flBoResult = BayesianOptimizer.Refine(
            flBoHistory,
            FadeLongGenotype.Bounds,
            v =>
            {
                var g  = FadeLongGenotype.FromVector(v);
                var ts = flCoins.Where(cd => cd.TrainH1.Length > 0)
                                .SelectMany(cd => FadeLongSimulator.GetFadeLongReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                                .Select(t => t.Return).ToList();
                return ts.Count > 0 ? ts.Average() : -1.0;
            },
            iterations: 60,
            rng: flRng);
        var flBoParams = flBoResult.OrderByDescending(h => h.Fitness).First().Params;
        var flBoGeno   = FadeLongGenotype.FromVector(flBoParams);
        flBoGeno.Fitness = flBoResult.OrderByDescending(h => h.Fitness).First().Fitness;
        if (flBoGeno.Fitness > flBest.Fitness) { flBest = flBoGeno; Console.WriteLine($"  TPE improved: {flBest}"); }
        else Console.WriteLine($"  GA elite kept");

        Console.WriteLine($"\nFrozen genotype:\n  {flBest}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(FadeLongGenotypeDto.From(flBest, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Overfit check ───");
        var flTRaw = flCoins.Where(cd => cd.TrainH1.Length > 0)
                     .SelectMany(cd => FadeLongSimulator.GetFadeLongReturns(flBest, cd.TrainH1.Span, cd.TrainM15.Span)).ToList();
        var flVRaw = flCoins.Where(cd => cd.TrainH1.Length > 0)
                     .SelectMany(cd => FadeLongSimulator.GetFadeLongReturns(flBest, cd.ValH1.Span,   cd.ValM15.Span  )).ToList();
        var flHRaw = flCoins.Where(cd => cd.TrainH1.Length == 0)
                     .SelectMany(cd => FadeLongSimulator.GetFadeLongReturns(flBest, cd.ValH1.Span,   cd.ValM15.Span  )).ToList();
        var flTRet = flTRaw.Select(t => t.Return).ToList();
        var flVRet = flVRaw.Select(t => t.Return).ToList();
        var flHRet = flHRaw.Select(t => t.Return).ToList();
        int flTValid = flTRaw.Count(t => t.RegimeBarsActive >= flBest.RegimeSustainedBars);
        int flVValid = flVRaw.Count(t => t.RegimeBarsActive >= flBest.RegimeSustainedBars);
        int flHValid = flHRaw.Count(t => t.RegimeBarsActive >= flBest.RegimeSustainedBars);
        Console.WriteLine($"  Regime-valid (≥{flBest.RegimeSustainedBars} bear bars):");
        Console.WriteLine($"    Train segments : {flTValid}/{flTRaw.Count}");
        Console.WriteLine($"    Val segments   : {flVValid}/{flVRaw.Count}  ← regime-aligned OOS");
        Console.WriteLine($"    Held-out coins : {flHValid}/{flHRaw.Count}  ← cross-asset OOS");
        int flTCC = flCoins.Where(cd => cd.TrainH1.Length > 0).Sum(cd => cd.TrainH1.Length) * 12;
        int flVCC = flCoins.Where(cd => cd.TrainH1.Length > 0).Sum(cd => cd.ValH1.Length)   * 12;
        int flHCC = flCoins.Where(cd => cd.TrainH1.Length == 0).Sum(cd => cd.ValH1.Length)  * 12;
        CandleFetcher.PrintSplitStats("Train segs", flTRet, flTCC);
        CandleFetcher.PrintSplitStats("Val segs  ", flVRet, flVCC);
        CandleFetcher.PrintSplitStats("Held-out  ", flHRet, flHCC);
        double flvExp = flVRet.Count > 0 ? flVRet.Average() : 0;
        double fltExp = flTRet.Count > 0 ? flTRet.Average() : 0;
        double flhExp = flHRet.Count > 0 ? flHRet.Average() : 0;
        Console.WriteLine(flvExp < fltExp * 0.4 || flvExp <= 0
            ? "\n  !! Possible overfit — val expectancy < 40% of train"
            : "\n  OK — val expectancy within acceptable range");
        Console.WriteLine(flhExp > 0
            ? "  Held-out OOS: positive expectancy ✓"
            : "  Held-out OOS: negative expectancy — strategy not generalising cross-asset");
        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    public static async Task RunRipShortTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("rip_short", variant, Config.RipShortGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | RIPSHORTTRAIN (bear-regime relief-rally short, 1h setup + 15m entry/exit, {Config.BacktestCoins.Length} coins, ~3yr) ===");
        Console.WriteLine($"Training RipShort / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var semRs = new SemaphoreSlim(4);
        var rsFetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await semRs.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                Console.WriteLine($"  {sym}: {m15.Count} 15m → {h1.Length} h1 (~{h1.Length / 24.0:F0}d)");
                return (sym, h1, m15.ToArray());
            }
            finally { semRs.Release(); }
        });
        var rsFetched = await Task.WhenAll(rsFetchTasks);

        // Load router genotype to align bear-window thresholds with live routing
        RegimeRouterGenotype? rsRouterG = File.Exists(Config.RouterGenoFile)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()
            : null;
        int    bearMinBars = rsRouterG != null ? (int)rsRouterG.BearMinBars : BearWindowMinBars;
        double bearMinConf = rsRouterG != null ? rsRouterG.BearMinConf      : 0.0;
        Console.WriteLine(rsRouterG != null
            ? $"  Router bear thresholds: ≥{bearMinBars} bars / conf≥{bearMinConf:F2}"
            : $"  No router genotype — using default ≥{bearMinBars} bars");

        // Build BTC bear windows to restrict training data to regime-relevant periods
        var btcFetched = rsFetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        var bearWindows = new List<(DateTime Start, DateTime End)>();
        if (btcFetched.h1 is { Length: > 220 })
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcFetched.h1);
            bearWindows = GetBearWindows(btcSeries, bearMinBars, bearMinConf);
            Console.WriteLine($"  BTC bear windows ({bearMinBars}+ bar runs, conf≥{bearMinConf:F2}): {bearWindows.Count}");
            foreach (var (s, e) in bearWindows)
                Console.WriteLine($"    {s:yyyy-MM-dd} → {e:yyyy-MM-dd}  ({(e - s).TotalDays:F0}d)");
        }
        else
        {
            Console.WriteLine("  BTC data insufficient for bear-window filtering — using full history");
            // Fetch BTC separately if not in BacktestCoins
            var btcM15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: 113);
            var btcH1  = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);
            if (btcH1.Length > 220)
            {
                var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
                bearWindows = GetBearWindows(btcSeries, bearMinBars, bearMinConf);
                Console.WriteLine($"  BTC bear windows ({bearMinBars}+ bar runs, conf≥{bearMinConf:F2}): {bearWindows.Count}");
            }
        }

        var rsPassed = new List<(string Sym, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1, m15) in rsFetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) { Console.WriteLine($"  {sym}: skip (vol=${medVol:F2}M/h)"); continue; }
            rsPassed.Add((sym, h1, m15));
        }

        var rsCoins = new List<RipShortGA.CoinData>();
        int rsHeld = 0, rsRegime = 0, rsFallback = 0;
        for (int ci = 0; ci < rsPassed.Count; ci++)
        {
            var (sym, h1, m15) = rsPassed[ci];
            if (ci % 5 == 0)
            {
                rsCoins.Add(new RipShortGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                rsHeld++;
                Console.WriteLine($"  {sym}: held-out OOS ({h1.Length} h1 bars)");
            }
            else
            {
                var (valStart, valEnd) = CandleFetcher.FindLastRegimeBlock(h1, wantBull: false);
                if (valStart >= 0)
                {
                    int m15ValEnd = Math.Min((valEnd + 1) * 4, m15.Length);

                    // Apply bear-window filter to training candles
                    var h1TrainFiltered  = FilterToWindows(h1[..valStart],        c => c.Time, bearWindows);
                    var m15TrainFiltered = FilterToWindows(m15[..(valStart * 4)], c => c.Time, bearWindows);

                    if (h1TrainFiltered.Length < 50)
                    {
                        Console.WriteLine($"  {sym}: skip training (< 50 bear-window bars after filter)");
                        rsHeld++;
                        continue;
                    }

                    rsCoins.Add(new RipShortGA.CoinData(
                        h1TrainFiltered,               h1[valStart..(valEnd + 1)],
                        m15TrainFiltered,              m15[(valStart * 4)..m15ValEnd]));
                    rsRegime++;
                    Console.WriteLine($"  {sym}: regime-val bars [{valStart}..{valEnd}] ({valEnd - valStart + 1} bear bars)  train={h1TrainFiltered.Length} bear-window h1 bars");
                }
                else
                {
                    rsCoins.Add(new RipShortGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                    rsHeld++;
                    Console.WriteLine($"  {sym}: held-out OOS (no sustained bear block — excluded from training)");
                }
            }
        }
        if (rsCoins.Count == 0) { Console.WriteLine("No data."); return; }
        Console.WriteLine($"\n  {rsCoins.Count} coins: {rsCoins.Count - rsHeld} training ({rsRegime} regime-split + {rsFallback} fallback) · {rsHeld} held-out OOS\n");

        RipShortGenotype? rsSeed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<RipShortGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
            if (candidate.Fitness > 0) { rsSeed = candidate; Console.WriteLine($"  Seeding from {genoPath}: {rsSeed}"); }
            else Console.WriteLine("  Skipping seed (fitness ≤ 0 — training from scratch)");
        }

        var rsBest = new RipShortGA(80, 150, verbose: true, cfg: cfg).Run(rsCoins, rsSeed);

        Console.WriteLine("\n─── Bayesian refinement for RipShort (60 TPE iterations) ───");
        var rsRng = new Random(42);
        var rsBoHistory = new List<(double[] Params, double Fitness)>
        {
            (rsBest.ToVector(), rsBest.Fitness)
        };
        var rsBoResult = BayesianOptimizer.Refine(
            rsBoHistory,
            RipShortGenotype.Bounds,
            v =>
            {
                var g  = RipShortGenotype.FromVector(v);
                var ts = rsCoins.Where(cd => cd.TrainH1.Length > 0)
                                .SelectMany(cd => RipShortSimulator.GetRipShortReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                                .Select(t => t.Return).ToList();
                return ts.Count > 0 ? ts.Average() : -1.0;
            },
            iterations: 60,
            rng: rsRng);
        var rsBoParams = rsBoResult.OrderByDescending(h => h.Fitness).First().Params;
        var rsBoGeno   = RipShortGenotype.FromVector(rsBoParams);
        rsBoGeno.Fitness = rsBoResult.OrderByDescending(h => h.Fitness).First().Fitness;
        if (rsBoGeno.Fitness > rsBest.Fitness) { rsBest = rsBoGeno; Console.WriteLine($"  TPE improved: {rsBest}"); }
        else Console.WriteLine($"  GA elite kept");

        Console.WriteLine($"\nFrozen genotype:\n  {rsBest}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(RipShortGenotypeDto.From(rsBest, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Overfit check ───");
        // NOTE: GetRipShortReturns yields (Time, Return, Kind) — no per-trade RegimeBarsActive,
        // so this check is expectancy-based (unlike FadeLong's regime-valid segment counts).
        var rsTRet = rsCoins.Where(cd => cd.TrainH1.Length > 0)
                     .SelectMany(cd => RipShortSimulator.GetRipShortReturns(rsBest, cd.TrainH1.Span, cd.TrainM15.Span))
                     .Select(t => t.Return).ToList();
        var rsVRet = rsCoins.Where(cd => cd.TrainH1.Length > 0)
                     .SelectMany(cd => RipShortSimulator.GetRipShortReturns(rsBest, cd.ValH1.Span,   cd.ValM15.Span  ))
                     .Select(t => t.Return).ToList();
        var rsHRet = rsCoins.Where(cd => cd.TrainH1.Length == 0)
                     .SelectMany(cd => RipShortSimulator.GetRipShortReturns(rsBest, cd.ValH1.Span,   cd.ValM15.Span  ))
                     .Select(t => t.Return).ToList();
        int rsTCC = rsCoins.Where(cd => cd.TrainH1.Length > 0).Sum(cd => cd.TrainH1.Length) * 12;
        int rsVCC = rsCoins.Where(cd => cd.TrainH1.Length > 0).Sum(cd => cd.ValH1.Length)   * 12;
        int rsHCC = rsCoins.Where(cd => cd.TrainH1.Length == 0).Sum(cd => cd.ValH1.Length)  * 12;
        CandleFetcher.PrintSplitStats("Train segs", rsTRet, rsTCC);
        CandleFetcher.PrintSplitStats("Val segs  ", rsVRet, rsVCC);
        CandleFetcher.PrintSplitStats("Held-out  ", rsHRet, rsHCC);
        double rsvExp = rsVRet.Count > 0 ? rsVRet.Average() : 0;
        double rstExp = rsTRet.Count > 0 ? rsTRet.Average() : 0;
        double rshExp = rsHRet.Count > 0 ? rsHRet.Average() : 0;
        Console.WriteLine(rsvExp < rstExp * 0.4 || rsvExp <= 0
            ? "\n  !! Possible overfit — val expectancy < 40% of train"
            : "\n  OK — val expectancy within acceptable range");
        Console.WriteLine(rshExp > 0
            ? "  Held-out OOS: positive expectancy ✓"
            : "  Held-out OOS: negative expectancy — strategy not generalising cross-asset");
        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    public static async Task RunRegimeRouterTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("regime_router", variant, Config.RouterGenoFile);
        Console.WriteLine("=== Gravity-gen2 | ROUTERTRAIN (RegimeRouter GA · BTC-anchored · full history) ===");
        Console.WriteLine($"Training RegimeRouter / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        if (!File.Exists(Config.FadeShortGenoFile))
        { Console.WriteLine("No FadeShort genotype — run train first."); return; }

        var fsGeno = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var clusterGenosRt = new Dictionary<CoinCluster, FadeShortGenotype>();
        foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
        {
            string clFile = CoinClusterHelper.GenoFile(cl);
            clusterGenosRt[cl] = File.Exists(clFile)
                ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                : fsGeno;
        }

        GridGenotype? gridGenoRt = File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype() : null;

        FadeLongGenotype? flGenoRt = File.Exists(Config.FadeLongGenoFile)
            ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype() : null;

        DipLongGenotype? dlGenoRt = File.Exists(Config.DipLongGenoFile)
            ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"  FadeShort : {fsGeno}");
        if (gridGenoRt != null) Console.WriteLine($"  Grid      : {gridGenoRt}");
        if (flGenoRt   != null) Console.WriteLine($"  FadeLong  : {flGenoRt}  {(flGenoRt.Fitness > 0 ? "✓ included" : "✗ excluded (F≤0)")}");
        if (dlGenoRt   != null) Console.WriteLine($"  DipLong   : {dlGenoRt}  {(dlGenoRt.Fitness > 0 ? "✓ included" : "✗ excluded (F≤0)")}");
        Console.WriteLine();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m candles, ~3yr)...");
        var semRt = new SemaphoreSlim(4);
        var fetchedRt = await Task.WhenAll(Config.BacktestCoins.Select(async sym =>
        {
            await semRt.WaitAsync();
            try { var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113); return (sym, m15); }
            finally { semRt.Release(); }
        }));
        Console.WriteLine();

        var btcFetch = fetchedRt.FirstOrDefault(x => x.sym == "BTCUSDT");
        var ethFetch = fetchedRt.FirstOrDefault(x => x.sym == "ETHUSDT");

        if (btcFetch.m15 is not { Count: > 500 })
        { Console.WriteLine("BTCUSDT data insufficient — cannot train router."); return; }

        var btcH1Rt = FadeShortSimulator.AggregateCandles(btcFetch.m15.ToArray(), 4);
        var ethH1Rt = ethFetch.m15 is { Count: > 500 }
            ? FadeShortSimulator.AggregateCandles(ethFetch.m15.ToArray(), 4) : null;

        Console.WriteLine($"  BTC h1 series: {btcH1Rt.Length} bars  ({btcH1Rt[0].Time:yyyy-MM-dd} → {btcH1Rt[^1].Time:yyyy-MM-dd})");
        if (ethH1Rt != null) Console.WriteLine($"  ETH h1 series: {ethH1Rt.Length} bars");

        Console.WriteLine("  Computing regime series...");
        var btcRegimeSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1Rt);
        var ethRegimeSeries = ethH1Rt != null ? RegimeClassifier.ClassifySeriesWithDuration(ethH1Rt) : null;

        var regimeGroups = btcRegimeSeries.Where(b => b.Duration > 0).GroupBy(b => b.Regime);
        foreach (var g in regimeGroups.OrderByDescending(g => g.Count()))
            Console.WriteLine($"    {g.Key,-10}: {g.Count(),5} bars  " +
                $"avgConf={g.Average(b => b.Confidence):P0}  " +
                $"maxRun={g.Max(b => b.Duration),4} bars");
        Console.WriteLine();

        var allTrades = new List<RegimeRouterGA.TradeRecord>();

        Console.WriteLine("  Running strategy simulators (full 3yr)...");
        foreach (var (sym, m15List) in fetchedRt)
        {
            if (m15List is not { Count: > 200 }) continue;
            var m15 = m15List.ToArray();
            var h1  = FadeShortSimulator.AggregateCandles(m15, 4);
            if (h1.Length < 200) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;

            var coinCl  = CoinClusterHelper.ClassifyByName(sym);
            var fsGenoC = clusterGenosRt[coinCl];
            try
            {
                var fsTrs = FadeShortSimulator.GetFadeShortReturns(fsGenoC, h1, m15);
                foreach (var t in fsTrs)
                    allTrades.Add(new(RegimeRouterGA.StrategyKind.FadeShort, t.Time, t.Return, fsGeno.PositionSizePct));
            }
            catch { }

            if (gridGenoRt?.Fitness > 0)
            {
                try
                {
                    var gridTrs = GridSimulator.GetGridReturns(gridGenoRt, h1);
                    foreach (var t in gridTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.Grid, t.Time, t.Return, 0.05));
                }
                catch { }
            }

            if (flGenoRt?.Fitness > 0)
            {
                try
                {
                    var flTrs = FadeLongSimulator.GetFadeLongReturns(flGenoRt, h1, m15)
                        .Where(t => t.RegimeBarsActive >= flGenoRt.RegimeSustainedBars);
                    foreach (var t in flTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.FadeLong, t.Time, t.Return, flGenoRt.PositionSizePct));
                }
                catch { }
            }

            if (dlGenoRt?.Fitness > 0)
            {
                try
                {
                    var dlTrs = DipLongSimulator.GetDipLongReturns(dlGenoRt, h1, m15)
                        .Where(t => t.RegimeBarsActive >= dlGenoRt.RegimeSustainedBars);
                    foreach (var t in dlTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.DipLong, t.Time, t.Return, dlGenoRt.PositionSizePct));
                }
                catch { }
            }
        }

        Console.WriteLine($"  Collected {allTrades.Count} total trades across all strategies.");
        Console.WriteLine();

        RegimeRouterGenotype? routerSeed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(genoPath))!.ToGenotype();
            if (candidate.Fitness > 0)
            {
                routerSeed = candidate;
                Console.WriteLine($"  Seeding from {genoPath}: {routerSeed}");
            }
        }

        Console.WriteLine("─── RegimeRouter GA training ───");
        var routerBest = new RegimeRouterGA(
            populationSize:    50,
            generations:       150,
            eliteCount:        10,
            migrationInterval: 10,
            verbose:           true)
            .Run(btcRegimeSeries, ethRegimeSeries, allTrades, routerSeed);

        Console.WriteLine($"\nFrozen router genotype:\n  {routerBest}\n");
        File.WriteAllText(genoPath,
            JsonSerializer.Serialize(RegimeRouterGenotypeDto.From(routerBest),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");
        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    public static async Task RunDipLongTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("dip_long", variant, Config.DipLongGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | DIPLONGTRAIN (bull pullback, 1h setup + 15m entry/exit, {Config.BacktestCoins.Length} coins, last 18 months) ===");
        Console.WriteLine($"Training DipLong / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var semDl = new SemaphoreSlim(4);
        var dlFetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await semDl.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full  = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                int h1Start  = Math.Max(0, h1Full.Length - TrainWindowH1);
                int m15Start = h1Start * 4;
                var h1  = h1Full[h1Start..];
                var m15 = m15Full.ToArray()[m15Start..];
                Console.WriteLine($"  {sym}: {h1Full.Length} h1 total, {h1.Length} in 18-month window");
                return (sym, h1Full, m15Full.ToArray(), h1, m15);
            }
            finally { semDl.Release(); }
        });
        var dlFetched = await Task.WhenAll(dlFetchTasks);

        var dlFull = new List<(string Sym, Candle[] H1Full, Candle[] M15Full, Candle[] H1, Candle[] M15)>();
        foreach (var (sym, h1Full, m15Full, h1, m15) in dlFetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            var volUsd = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) { Console.WriteLine($"  {sym}: skip (vol=${medVol:F2}M/h)"); continue; }
            dlFull.Add((sym, h1Full, m15Full, h1, m15));
        }

        RegimeRouterGenotype? dlRouterGeno = File.Exists(Config.RouterGenoFile)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()
            : null;

        var btcDlEntry   = dlFull.FirstOrDefault(x => x.Sym == "BTCUSDT");
        bool coRefinement = dlRouterGeno?.Fitness > 0 && btcDlEntry.H1Full is { Length: > 200 };

        var dlCoins = new List<DipLongGA.CoinData>();

        if (coRefinement)
        {
            Console.WriteLine($"\n  Router co-refinement active (full 3yr window) · BullMinBars={dlRouterGeno!.BullMinBars:F0} live gate");

            foreach (var (sym, h1Full, m15Full, _, _) in dlFull)
            {
                if (sym == "BTCUSDT") continue;
                if (h1Full.Length < 500) continue;

                int split = (int)(h1Full.Length * 0.80);
                int m15sp = Math.Min(split * 4, m15Full.Length);

                dlCoins.Add(new DipLongGA.CoinData(
                    h1Full[..split],      h1Full[split..],
                    m15Full[..m15sp],     m15Full[m15sp..]));
            }

            Console.WriteLine($"  Coins passed to GA: {dlCoins.Count} (full history · 80/20 split)\n");
            if (dlCoins.Count == 0) { Console.WriteLine("No data."); return; }
        }
        else
        {
            if (dlRouterGeno?.Fitness > 0)
                Console.WriteLine("  (BTCUSDT data missing — falling back to standard 18-month mode)");

            int dlHeld = 0, dlRegime = 0;
            for (int ci = 0; ci < dlFull.Count; ci++)
            {
                var (sym, _, _, h1, m15) = dlFull[ci];
                if (ci % 5 == 0)
                {
                    dlCoins.Add(new DipLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                    dlHeld++;
                    Console.WriteLine($"  {sym}: held-out OOS ({h1.Length} h1 bars)");
                }
                else
                {
                    var (valStart, valEnd) = CandleFetcher.FindLastRegimeBlock(h1, wantBull: true);
                    if (valStart >= 0)
                    {
                        int m15ValEnd = Math.Min((valEnd + 1) * 4, m15.Length);
                        dlCoins.Add(new DipLongGA.CoinData(
                            h1[..valStart],        h1[valStart..(valEnd + 1)],
                            m15[..(valStart * 4)], m15[(valStart * 4)..m15ValEnd]));
                        dlRegime++;
                        Console.WriteLine($"  {sym}: regime-val [{valStart}..{valEnd}] ({valEnd - valStart + 1} bull bars)");
                    }
                    else
                    {
                        dlCoins.Add(new DipLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                        dlHeld++;
                        Console.WriteLine($"  {sym}: held-out OOS (no sustained bull block)");
                    }
                }
            }
            if (dlCoins.Count == 0) { Console.WriteLine("No data."); return; }
            Console.WriteLine($"\n  {dlCoins.Count} coins: {dlCoins.Count - dlHeld} training ({dlRegime} regime-split) · {dlHeld} held-out OOS\n");
        }

        DipLongGenotype? dlSeed = null;
        if (dlRouterGeno?.Fitness > 0)
            Console.WriteLine("  Training from scratch (co-refinement — entry/exit specialisation within router-guaranteed bull)");
        else
            Console.WriteLine("  Training from scratch (18-month window — old 3yr genotype intentionally not seeded)");

        var dlBest = new DipLongGA(80, 150, verbose: true, cfg: cfg).Run(dlCoins, dlSeed);

        Console.WriteLine("\n─── Bayesian refinement for DipLong (60 TPE iterations) ───");
        var dlRng = new Random(42);
        var dlBoHistory = new List<(double[] Params, double Fitness)>
        {
            (dlBest.ToVector(), dlBest.Fitness)
        };
        var dlBoResult = BayesianOptimizer.Refine(
            dlBoHistory,
            DipLongGenotype.Bounds,
            v =>
            {
                var g  = DipLongGenotype.FromVector(v);
                var ts = dlCoins.Where(cd => cd.TrainH1.Length > 0)
                                .SelectMany(cd => DipLongSimulator.GetDipLongReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                                .Select(t => t.Return).ToList();
                return ts.Count > 0 ? ts.Average() : -1.0;
            },
            iterations: 60,
            rng: dlRng);
        var dlBoParams = dlBoResult.OrderByDescending(h => h.Fitness).First().Params;
        var dlBoGeno   = DipLongGenotype.FromVector(dlBoParams);
        dlBoGeno.Fitness = dlBoResult.OrderByDescending(h => h.Fitness).First().Fitness;
        if (dlBoGeno.Fitness > dlBest.Fitness) { dlBest = dlBoGeno; Console.WriteLine($"  TPE improved: {dlBest}"); }
        else Console.WriteLine($"  GA elite kept");

        Console.WriteLine($"\nFrozen genotype:\n  {dlBest}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(DipLongGenotypeDto.From(dlBest, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Overfit check ───");
        var dlTRaw = dlCoins.Where(cd => cd.TrainH1.Length > 0)
                     .SelectMany(cd => DipLongSimulator.GetDipLongReturns(dlBest, cd.TrainH1.Span, cd.TrainM15.Span)).ToList();
        var dlVRaw = dlCoins.Where(cd => cd.TrainH1.Length > 0)
                     .SelectMany(cd => DipLongSimulator.GetDipLongReturns(dlBest, cd.ValH1.Span,   cd.ValM15.Span  )).ToList();
        var dlHRaw = dlCoins.Where(cd => cd.TrainH1.Length == 0)
                     .SelectMany(cd => DipLongSimulator.GetDipLongReturns(dlBest, cd.ValH1.Span,   cd.ValM15.Span  )).ToList();
        var dlTRet = dlTRaw.Select(t => t.Return).ToList();
        var dlVRet = dlVRaw.Select(t => t.Return).ToList();
        var dlHRet = dlHRaw.Select(t => t.Return).ToList();
        int dlTValid = dlTRaw.Count(t => t.RegimeBarsActive >= dlBest.RegimeSustainedBars);
        int dlVValid = dlVRaw.Count(t => t.RegimeBarsActive >= dlBest.RegimeSustainedBars);
        int dlHValid = dlHRaw.Count(t => t.RegimeBarsActive >= dlBest.RegimeSustainedBars);
        Console.WriteLine($"  Regime-valid (≥{dlBest.RegimeSustainedBars} bull bars):");
        Console.WriteLine($"    Train segments : {dlTValid}/{dlTRaw.Count}");
        Console.WriteLine($"    Val segments   : {dlVValid}/{dlVRaw.Count}  ← regime-aligned OOS");
        Console.WriteLine($"    Held-out coins : {dlHValid}/{dlHRaw.Count}  ← cross-asset OOS");
        int dlTCC = dlCoins.Where(cd => cd.TrainH1.Length > 0).Sum(cd => cd.TrainH1.Length) * 12;
        int dlVCC = dlCoins.Where(cd => cd.TrainH1.Length > 0).Sum(cd => cd.ValH1.Length)   * 12;
        int dlHCC = dlCoins.Where(cd => cd.TrainH1.Length == 0).Sum(cd => cd.ValH1.Length)  * 12;
        CandleFetcher.PrintSplitStats("Train segs", dlTRet, dlTCC);
        CandleFetcher.PrintSplitStats("Val segs  ", dlVRet, dlVCC);
        CandleFetcher.PrintSplitStats("Held-out  ", dlHRet, dlHCC);
        double dlvExp = dlVRet.Count > 0 ? dlVRet.Average() : 0;
        double dltExp = dlTRet.Count > 0 ? dlTRet.Average() : 0;
        double dlhExp = dlHRet.Count > 0 ? dlHRet.Average() : 0;
        Console.WriteLine(dlvExp < dltExp * 0.4 || dlvExp <= 0
            ? "\n  !! Possible overfit — val expectancy < 40% of train"
            : "\n  OK — val expectancy within acceptable range");
        Console.WriteLine(dlhExp > 0
            ? "  Held-out OOS: positive expectancy ✓"
            : "  Held-out OOS: negative expectancy — strategy not generalising cross-asset");
        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    public static async Task RunSwingLongTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("swing_long", variant, Config.SwingLongGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | SWINGLONG TRAIN (bull divergence long, 1h+15m, {Config.BacktestCoins.Length} coins, ~3yr) ===");
        Console.WriteLine($"Training SwingLong / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]\n");

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, h1, m15.ToArray());
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coins = new List<SwingLongGA.CoinData>();
        foreach (var (sym, h1, m15) in fetched)
        {
            if (h1.Length < 150) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count > 0 && volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            // 20% val split from the end
            int valStart = (int)(h1.Length * 0.80);
            int m15Val   = valStart * 4;
            coins.Add(new SwingLongGA.CoinData(
                h1[..valStart],  h1[valStart..],
                m15[..m15Val],   m15[m15Val..]));
            Console.WriteLine($"  {sym}: {h1.Length} h1  val=[{valStart}..{h1.Length - 1}]");
        }
        if (coins.Count == 0) { Console.WriteLine("No data."); return; }

        SwingLongGenotype? seed = null;
        if (File.Exists(genoPath))
        {
            var candidate = JsonSerializer.Deserialize<SwingLongGenotypeDto>(
                File.ReadAllText(genoPath))!.ToGenotype();
            if (candidate.Fitness > 0) { seed = candidate; Console.WriteLine($"  Seeding: {seed}"); }
        }

        var best = new SwingLongGA(80, 150, verbose: true, cfg: cfg).Run(coins, seed);

        Console.WriteLine("\n─── Bayesian refinement (60 TPE iterations) ───");
        var slRng = new Random(42);
        var slBoHistory = new List<(double[] Params, double Fitness)>
        {
            (best.ToVector(), best.Fitness)
        };
        var slBoResult = BayesianOptimizer.Refine(
            slBoHistory,
            SwingLongGenotype.Bounds,
            v =>
            {
                var g  = SwingLongGenotype.FromVector(v);
                var ts = coins.Where(cd => cd.TrainH1.Length > 0)
                              .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                              .Select(t => t.Return).ToList();
                return ts.Count > 0 ? ts.Average() : -1.0;
            },
            iterations: 60,
            rng: slRng);
        var slBoParams = slBoResult.OrderByDescending(h => h.Fitness).First().Params;
        var slBoGeno   = SwingLongGenotype.FromVector(slBoParams);
        slBoGeno.Fitness = slBoResult.OrderByDescending(h => h.Fitness).First().Fitness;
        if (slBoGeno.Fitness > best.Fitness) { best = slBoGeno; Console.WriteLine($"  TPE improved: {best}"); }
        else Console.WriteLine($"  GA elite kept");

        Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
        File.WriteAllText(genoPath,
            JsonSerializer.Serialize(SwingLongGenotypeDto.From(best, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        // Quick overfit check
        var tRet = coins.Where(cd => cd.TrainH1.Length > 0)
                        .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.TrainH1.Span, cd.TrainM15.Span))
                        .Select(t => t.Return).ToList();
        var vRet = coins.Where(cd => cd.ValH1.Length > 0)
                        .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.ValH1.Span, cd.ValM15.Span))
                        .Select(t => t.Return).ToList();
        Console.WriteLine($"\n  Train: {tRet.Count} trades  avg={( tRet.Count>0 ? tRet.Average():0 ):+0.000;-0.000}%");
        Console.WriteLine($"  Val:   {vRet.Count} trades  avg={( vRet.Count>0 ? vRet.Average():0 ):+0.000;-0.000}%");
        double vExp = vRet.Count > 0 ? vRet.Average() : 0;
        double tExp = tRet.Count > 0 ? tRet.Average() : 0;
        Console.WriteLine(vExp < tExp * 0.4 || vExp <= 0
            ? "  !! Possible overfit — val expectancy < 40% of train"
            : "  OK — val expectancy within acceptable range");
    }
}
