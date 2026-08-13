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
        // --no-seed: start every genotype from scratch.
        //
        // Required for any honest --embargo experiment. The seeds live in genotypes/*.json and
        // encode everything learned in every previous run, INCLUDING the trailing slice --embargo
        // withholds — and since the seeded-init fix ~45% of the population is anchored on that
        // incumbent, so the seed's knowledge of the withheld window survives the run. An embargoed
        // run that still seeds is not a forward test; it is the old genotypes lightly refined.
        bool noSeed = args != null && Array.IndexOf(args, "--no-seed") >= 0;
        if (noSeed) Console.WriteLine("  [NO-SEED] all genotypes from scratch — starting population has seen no data");
        int?   rngSeed = GaSearch.ResolveSeed(args);
        var    cfg     = FitnessConfig.Load();
        Console.WriteLine("=== Gravity-gen2 | COEVOLVETRAIN (FadeLong + DipLong + SwingLong + RipShort + Router + DynamicGuard, 4 cycles) ===");
        Console.WriteLine($"Training Coevolve / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        // HANDOFF: CoevolveGA (src/coevolve/CoevolveGA.cs) constructs the Router and Guard GAs
        // itself and takes no seed parameter, so --seed cannot be forwarded to them from here.
        // Each GA still prints the seed it drew, so a coevolve run stays reproducible after the
        // fact; wiring --seed through CoevolveGA is a follow-up in a file this change did not own.
        GaSearch.AnnounceCommandSeed("coevolvetrain", rngSeed);
        Console.WriteLine();

        string flPath = TrainCommands.VariantGenoPath("fade_long",     variant, Config.FadeLongGenoFile);
        string dlPath = TrainCommands.VariantGenoPath("dip_long",      variant, Config.DipLongGenoFile);
        string slPath = TrainCommands.VariantGenoPath("swing_long",    variant, Config.SwingLongGenoFile);
        string rsPath = TrainCommands.VariantGenoPath("rip_short",     variant, Config.RipShortGenoFile);
        string gsPath = TrainCommands.VariantGenoPath("grid_short",    variant, Config.GridShortGenoFile);
        string rrPath = TrainCommands.VariantGenoPath("regime_router", variant, Config.RouterGenoFile);
        string dgPath = TrainCommands.VariantGenoPath("dynamic_guard", variant, Config.DynamicGuardGenoFile);

        FadeShortGenotype? fsSeed = noSeed ? null : (File.Exists(Config.FadeShortGenoFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype()
            : null);
        if (fsSeed == null)
            Console.WriteLine("  No FadeShort genotype — FadeShort trade list will be empty this run.");
        else
            Console.WriteLine($"  FadeShort seed  : {fsSeed}");

        FadeLongGenotype? flSeed = noSeed ? null : (File.Exists(flPath)
            ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(flPath))!.ToGenotype()
            : null);
        // A non-positive fitness here is the UNGATED score — precisely the misalignment coevolve
        // exists to repair. Nulling the seed removed the strategy from the run entirely (observed:
        // DipLong/SwingLong/RipShort/FadeLong all contributed 0 trades), so the gated re-adaptation
        // step had nothing to adapt. Keep the genotype; only a missing/unreadable file yields null.
        if (flSeed != null) Console.WriteLine($"  FadeLong seed   : {flSeed}"
            + (flSeed.Fitness <= 0 ? "   [ungated fitness <= 0 — will be re-adapted under the router gate]" : ""));
        else Console.WriteLine("  FadeLong seed   : none (file missing — training from scratch)");

        DipLongGenotype? dlSeed = noSeed ? null : (File.Exists(dlPath)
            ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(dlPath))!.ToGenotype()
            : null);
        // A non-positive fitness here is the UNGATED score — precisely the misalignment coevolve
        // exists to repair. Nulling the seed removed the strategy from the run entirely (observed:
        // DipLong/SwingLong/RipShort/FadeLong all contributed 0 trades), so the gated re-adaptation
        // step had nothing to adapt. Keep the genotype; only a missing/unreadable file yields null.
        if (dlSeed != null) Console.WriteLine($"  DipLong seed    : {dlSeed}"
            + (dlSeed.Fitness <= 0 ? "   [ungated fitness <= 0 — will be re-adapted under the router gate]" : ""));
        else Console.WriteLine("  DipLong seed    : none (file missing — training from scratch)");

        SwingLongGenotype? slSeed = noSeed ? null : (File.Exists(slPath)
            ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(slPath))!.ToGenotype()
            : null);
        // A non-positive fitness here is the UNGATED score — precisely the misalignment coevolve
        // exists to repair. Nulling the seed removed the strategy from the run entirely (observed:
        // DipLong/SwingLong/RipShort/FadeLong all contributed 0 trades), so the gated re-adaptation
        // step had nothing to adapt. Keep the genotype; only a missing/unreadable file yields null.
        if (slSeed != null) Console.WriteLine($"  SwingLong seed  : {slSeed}"
            + (slSeed.Fitness <= 0 ? "   [ungated fitness <= 0 — will be re-adapted under the router gate]" : ""));
        else Console.WriteLine("  SwingLong seed  : none (file missing — training from scratch)");

        RipShortGenotype? rsSeed = noSeed ? null : (File.Exists(rsPath)
            ? JsonSerializer.Deserialize<RipShortGenotypeDto>(File.ReadAllText(rsPath))!.ToGenotype()
            : null);
        // A non-positive fitness here is the UNGATED score — precisely the misalignment coevolve
        // exists to repair. Nulling the seed removed the strategy from the run entirely (observed:
        // DipLong/SwingLong/RipShort/FadeLong all contributed 0 trades), so the gated re-adaptation
        // step had nothing to adapt. Keep the genotype; only a missing/unreadable file yields null.
        if (rsSeed != null) Console.WriteLine($"  RipShort seed   : {rsSeed}"
            + (rsSeed.Fitness <= 0 ? "   [ungated fitness <= 0 — will be re-adapted under the router gate]" : ""));
        else Console.WriteLine("  RipShort seed   : none (file missing — training from scratch)");

        GridGenotype? gsSeed = noSeed ? null : (File.Exists(gsPath)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(gsPath))!.ToGenotype()
            : null);
        if (gsSeed is { Fitness: > 0 }) Console.WriteLine($"  GridShort seed  : {gsSeed}");
        else { gsSeed = null; Console.WriteLine("  GridShort seed  : none (training from scratch)"); }

        RegimeRouterGenotype? routerSeed = noSeed ? null : (File.Exists(rrPath)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(rrPath))!.ToGenotype()
            : null);
        if (routerSeed is { Fitness: > 0 }) Console.WriteLine($"  Router seed     : {routerSeed}");
        else { routerSeed = null; Console.WriteLine("  Router seed     : none (training from scratch)"); }

        DynamicGuardGenotype? dgSeed = noSeed ? null : (File.Exists(dgPath)
            ? JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(File.ReadAllText(dgPath))!.ToGenotype()
            : null);
        if (dgSeed != null) Console.WriteLine($"  DynamicGuard seed: {dgSeed}");
        else                Console.WriteLine("  DynamicGuard seed: none (training from scratch)");

        GridGenotype? gridSeed = noSeed ? null : (File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()
            : null);
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

        // ── Time embargo (`--embargo <frac>`) ────────────────────────────────────────
        // Truncating HERE is the point: every downstream consumer — the four regime-gated
        // strategies, FadeShort, Grid, the router's trade lists (data.AllCoins) and the guard's
        // — is built from coPassed, so one cut hides the trailing slice from all of them.
        //
        // Without it the router and guard train on data.AllCoins = FULL history, including the
        // val window combinedbacktest then scores. That makes the headline in-sample for the
        // component doing the work, which is exactly why an OOS PF of 2.11 on unseen COINS is
        // only symbol-generalization: same calendar window, and crypto's cross-sectional
        // correlation lets a regime-timing overfit travel across correlated symbols. Held-out
        // coins cannot falsify a timing overfit; a held-out FUTURE can.
        double embargoFrac = 0.0;
        int embIdx = Array.IndexOf(args ?? Array.Empty<string>(), "--embargo");
        if (embIdx >= 0 && args != null && embIdx + 1 < args.Length
            && double.TryParse(args[embIdx + 1], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double ef)
            && ef > 0 && ef < 0.9)
            embargoFrac = ef;

        if (embargoFrac > 0)
        {
            DateTime? cutoff = null;
            for (int i = 0; i < coPassed.Count; i++)
            {
                var (sym, h1, m15) = coPassed[i];
                int keepH1  = (int)(h1.Length  * (1.0 - embargoFrac));
                int keepM15 = (int)(m15.Length * (1.0 - embargoFrac));
                if (keepH1 < 200) continue;
                if (sym == "BTCUSDT") cutoff = h1[keepH1 - 1].Time;
                coPassed[i] = (sym, h1[..keepH1], m15[..keepM15]);
            }
            Console.WriteLine($"  [EMBARGO] trailing {embargoFrac:P0} of every coin's history withheld from ALL training");
            if (cutoff is DateTime c)
                Console.WriteLine($"  [EMBARGO] BTC training now ends {c:yyyy-MM-dd} — score after this date for a genuine forward test\n");
        }

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

        // RipShort shares FadeLong's bear-block split (same regime, opposite direction).
        var rsCoins = new List<RipShortGA.CoinData>();
        for (int ci = 0; ci < coPassed.Count; ci++)
        {
            var (sym, h1, m15) = coPassed[ci];
            if (ci % 5 == 0)
            {
                rsCoins.Add(new RipShortGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
            }
            else
            {
                var (valStart, valEnd) = CandleFetcher.FindLastRegimeBlock(h1, wantBull: false);
                if (valStart >= 0)
                {
                    int m15Ve = Math.Min((valEnd + 1) * 4, m15.Length);
                    rsCoins.Add(new RipShortGA.CoinData(
                        h1[..valStart],        h1[valStart..(valEnd + 1)],
                        m15[..(valStart * 4)], m15[(valStart * 4)..m15Ve]));
                }
                else
                {
                    rsCoins.Add(new RipShortGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
                }
            }
        }
        Console.WriteLine($"  RipShort: {rsCoins.Count} coins " +
            $"({rsCoins.Count(c => c.TrainH1.Length > 0)} training / {rsCoins.Count(c => c.TrainH1.Length == 0)} held-out)");

        var dlCoins = new List<DipLongGA.CoinData>();
        foreach (var (sym, h1, m15) in coPassed)
        {
            if (sym == "BTCUSDT" || h1.Length < 500) continue;
            int split = DataSplit.Split(h1).Train.Length;
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

        // FadeShort and Grid now co-adapt too — both are router-gated in production and so had
        // the same train-then-gate misalignment as the four regime-gated strategies. Same 80/20
        // split the other coevolve strategies use; both are h1-only.
        var fsCoins   = new List<FadeShortGA.CoinData>();
        var gridCoins = new List<GridGeneticAlgorithm.CoinData>();
        foreach (var (sym, h1, _) in coPassed)
        {
            if (sym == "BTCUSDT" || h1.Length < 500) continue;
            int split = DataSplit.Split(h1).Train.Length;
            fsCoins.Add(new FadeShortGA.CoinData(h1[..split], h1[split..]));
            gridCoins.Add(new GridGeneticAlgorithm.CoinData(h1[..split], h1[split..]));
        }
        Console.WriteLine($"  FadeShort/Grid: {fsCoins.Count} coins (full 3yr, 80/20 split)\n");

        var data   = new CoevolveGA.AllData(flCoins, dlCoins, slCoins, rsCoins, allCoins, fsCoins, gridCoins, btcSeries, ethSeries, btcEntry.H1, gridSeed, gsSeed);
        var result = new CoevolveGA().Run(data, fsSeed, flSeed, dlSeed, slSeed, rsSeed, routerSeed, dgSeed);

        // CoevolveGA FREEZES the four strategies and returns the seeds it was handed, so a null
        // here means no seed was loaded — the block above nulls any genotype whose Fitness <= 0.
        // Nothing was evolved, so there is nothing to write; keep whatever is on disk.
        //
        // This MUST NOT throw. CoevolveResult declares these non-nullable, but nullable reference
        // types are not enforced at runtime, and a null reached DipLongGenotypeDto.From and took
        // the whole command down AFTER FadeLong had been written and BEFORE the Router and Guard
        // saves below — discarding the only two things coevolvetrain actually evolves.
        void SaveIfEvolved<T>(string path, T? geno, Func<T, object> toDto, string label) where T : class
        {
            if (geno is null)
            {
                Console.WriteLine($"  Skipped {label,-9} — no seed loaded (fitness ≤ 0); on-disk genotype kept");
                return;
            }
            File.WriteAllText(path,
                JsonSerializer.Serialize(toDto(geno), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  Saved {label,-9} → {path}  {geno}");
        }

        Console.WriteLine();
        SaveIfEvolved(flPath, result.FadeLong,  g => FadeLongGenotypeDto.From(g, cfg),  "FadeLong");
        SaveIfEvolved(dlPath, result.DipLong,   g => DipLongGenotypeDto.From(g, cfg),   "DipLong");
        SaveIfEvolved(slPath, result.SwingLong, g => SwingLongGenotypeDto.From(g, cfg), "SwingLong");
        SaveIfEvolved(rsPath, result.RipShort,  g => RipShortGenotypeDto.From(g, cfg),  "RipShort");
        // FadeShort and Grid co-adapt now too. Writing them back is the whole point — computing
        // an evolved genotype and dropping it on the floor is the pattern this codebase keeps
        // producing, and adding another instance of it would be worse than not evolving them.
        SaveIfEvolved(TrainCommands.VariantGenoPath("fade_short", variant, Config.FadeShortGenoFile),
                      result.FadeShort, g => FadeShortGenotypeDto.From(g, cfg), "FadeShort");
        SaveIfEvolved(TrainCommands.VariantGenoPath("grid_best", variant, Config.GridGenoFile),
                      result.Grid,      g => GridGenotypeDto.From(g, cfg),      "Grid");

        File.WriteAllText(rrPath,
            JsonSerializer.Serialize(RegimeRouterGenotypeDto.From(result.Router),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved Router     → {rrPath}  {result.Router}");

        // Persist ALL 15 genes, not just the first 8. This previously constructed the DTO with
        // eight arguments and let the rest fall to their defaults, so a guard trained through
        // coevolvetrain was written back with BullMomBypass, EntryAtrGate, DdEntryGatePct,
        // ConfLossCapMin/Max and all three ProfitProtect* silently reset — discarding what the
        // GA had just selected. Keep this in sync with DynamicGuardTrainCommands' save path;
        // DynamicGuardGenotype.GeneCount is the authority on how many genes exist.
        var g = result.DynamicGuard;
        var dgDto = new DynamicGuardGenotypeDto(
            g.AtrLookback, g.AtrTrigger, g.MomLookback, g.MomThreshold, g.SizeFloor, g.Fitness,
            g.PanicTrigger, g.RecoveryBars, g.BullMomBypass, g.EntryAtrGate, g.DdEntryGatePct,
            g.ConfLossCapMin, g.ConfLossCapMax,
            g.ProfitProtectThreshold, g.ProfitProtectDrawback, g.ProfitProtectFactor);
        File.WriteAllText(dgPath,
            JsonSerializer.Serialize(dgDto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved DynGuard   → {dgPath}  {result.DynamicGuard}");

        Console.WriteLine("\nNext: dotnet run -- fulltest");
    }

    public static async Task RunFadeLongTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        int?   rngSeed  = GaSearch.ResolveSeed(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("fade_long", variant, Config.FadeLongGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | FADELONGTRAIN (oversold bounce, 1h setup + 15m entry/exit, {Config.BacktestCoins.Length} coins, ~3yr) ===");
        Console.WriteLine($"Training FadeLong / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        GaSearch.AnnounceCommandSeed("fadelongtrain", rngSeed);
        Console.WriteLine();

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
        RegimeBar[]? flBtcSeries = null;
        if (btcFetched.h1 is { Length: > 220 })
        {
            flBtcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcFetched.h1);
            bearWindows = GetBearWindows(flBtcSeries, bearMinBars, bearMinConf);
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
                flBtcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
                bearWindows = GetBearWindows(flBtcSeries, bearMinBars, bearMinConf);
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

        var flBest = new FadeLongGA(80, 150, verbose: true, cfg: cfg, btcSeries: flBtcSeries, seed: rngSeed).Run(flCoins, flSeed);

        // Post-GA TPE pass removed — see the "Post-GA refinement" note at the top of
        // commands/TrainCommands.cs. FadeLongGA already runs 60 TPE iterations internally
        // (FadeLongGA.cs ~:267), seeded from its full elite island and evaluated with its
        // own Fitness(g, coins, useValidation: false) — the same function, data and scale
        // the GA selects on. That is the refinement; this outer one only added noise.
        Console.WriteLine("\n─── Refinement ───");
        Console.WriteLine("  Handled inside FadeLongGA (60 TPE iterations on the GA's own fitness).");
        Console.WriteLine($"  Selected genotype fitness (GA scale, held-out): {flBest.Fitness:F4}");

        Console.WriteLine($"\nFrozen genotype:\n  {flBest}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(FadeLongGenotypeDto.From(flBest, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Validation suite ───");
        try
        {
            var mcResult = MonteCarloTest.Run(
                flCoins.Where(cd => cd.TrainH1.Length > 0)
                    .SelectMany(cd => FadeLongSimulator.GetFadeLongReturns(flBest, cd.TrainH1.Span, cd.TrainM15.Span).Select(t => t.Return)).ToList(),
                permutations: 1000);
            MonteCarloTest.PrintReport(mcResult, "FadeLong");
        }
        catch (Exception ex) { Console.WriteLine($"  MonteCarloTest skipped: {ex.Message}"); }

        try
        {
            var ewReport = ExpandingWindowValidation.RunFadeLong(flCoins, cfg);
            ExpandingWindowValidation.PrintReport(ewReport);
        }
        catch (Exception ex) { Console.WriteLine($"  ExpandingWindowValidation skipped: {ex.Message}"); }

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
        int?   rngSeed  = GaSearch.ResolveSeed(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("rip_short", variant, Config.RipShortGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | RIPSHORTTRAIN (bear-regime relief-rally short, 1h setup + 15m entry/exit, {Config.BacktestCoins.Length} coins, ~3yr) ===");
        Console.WriteLine($"Training RipShort / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        GaSearch.AnnounceCommandSeed("ripshorttrain", rngSeed);
        Console.WriteLine();

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
        int    bearMinBars = rsRouterG != null ? (int)rsRouterG.RipShortBearMinBars : BearWindowMinBars;
        double bearMinConf = rsRouterG != null ? rsRouterG.RipShortBearMinConf      : 0.0;
        Console.WriteLine(rsRouterG != null
            ? $"  Router bear thresholds: ≥{bearMinBars} bars / conf≥{bearMinConf:F2}"
            : $"  No router genotype — using default ≥{bearMinBars} bars");

        // Build BTC bear windows to restrict training data to regime-relevant periods
        var btcFetched = rsFetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        var bearWindows = new List<(DateTime Start, DateTime End)>();
        RegimeBar[]? rsBtcSeries = null;
        if (btcFetched.h1 is { Length: > 220 })
        {
            rsBtcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcFetched.h1);
            bearWindows = GetBearWindows(rsBtcSeries, bearMinBars, bearMinConf);
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
                rsBtcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
                bearWindows = GetBearWindows(rsBtcSeries, bearMinBars, bearMinConf);
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

        var rsBest = new RipShortGA(80, 150, verbose: true, cfg: cfg, btcSeries: rsBtcSeries, seed: rngSeed).Run(rsCoins, rsSeed);

        // Post-GA TPE pass removed — see the "Post-GA refinement" note at the top of
        // commands/TrainCommands.cs. RipShortGA already runs 60 TPE iterations internally
        // (RipShortGA.cs ~:305) against its own Fitness(..., useValidation: false).
        Console.WriteLine("\n─── Refinement ───");
        Console.WriteLine("  Handled inside RipShortGA (60 TPE iterations on the GA's own fitness).");
        Console.WriteLine($"  Selected genotype fitness (GA scale, held-out): {rsBest.Fitness:F4}");

        Console.WriteLine($"\nFrozen genotype:\n  {rsBest}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(RipShortGenotypeDto.From(rsBest, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Validation suite ───");
        try
        {
            var mcResult = MonteCarloTest.Run(
                rsCoins.Where(cd => cd.TrainH1.Length > 0)
                    .SelectMany(cd => RipShortSimulator.GetRipShortReturns(rsBest, cd.TrainH1.Span, cd.TrainM15.Span).Select(t => t.Return)).ToList(),
                permutations: 1000);
            MonteCarloTest.PrintReport(mcResult, "RipShort");
        }
        catch (Exception ex) { Console.WriteLine($"  MonteCarloTest skipped: {ex.Message}"); }

        try
        {
            var ewReport = ExpandingWindowValidation.RunRipShort(rsCoins, cfg);
            ExpandingWindowValidation.PrintReport(ewReport);
        }
        catch (Exception ex) { Console.WriteLine($"  ExpandingWindowValidation skipped: {ex.Message}"); }

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

        if (rsBtcSeries != null && rsCoins.Any(cd => cd.TrainH1.Length > 0))
        {
            DateTime rsFitCutoff = rsCoins.Where(cd => cd.TrainH1.Length > 0).Max(cd => cd.ValH1.Span[^1].Time);
            Console.WriteLine($"\n─── Held-out, time-embargoed + bear-regime-only (dates ≥ {rsFitCutoff:yyyy-MM-dd}) ───");
            Console.WriteLine("  Restricted to dates after every training coin's fit window ends, AND to BTC bear-regime");
            Console.WriteLine("  bars only — RipShort only ever fires live during a router-confirmed bear regime, so");
            Console.WriteLine("  off-regime false positives here would otherwise dilute the number in either direction.");

            var embRet = new List<double>();
            int embCC  = 0;
            foreach (var cd in rsCoins.Where(cd => cd.TrainH1.Length == 0))
            {
                var h1Emb  = cd.ValH1.ToArray().Where(c => c.Time >= rsFitCutoff).ToArray();
                var m15Emb = cd.ValM15.ToArray().Where(c => c.Time >= rsFitCutoff).ToArray();
                if (h1Emb.Length == 0 || m15Emb.Length == 0) continue;

                var trades = RipShortSimulator.GetRipShortReturns(rsBest, h1Emb, m15Emb);
                var tradeTags = RegimeBarLookup.TagRegimes(rsBtcSeries, trades.Select(t => t.Time).ToList());
                for (int i = 0; i < trades.Count; i++)
                    if (tradeTags[i] == MarketRegime.Bear) embRet.Add(trades[i].Return);

                var candleTags = RegimeBarLookup.TagRegimes(rsBtcSeries, h1Emb.Select(c => c.Time).ToList());
                embCC += candleTags.Count(t => t == MarketRegime.Bear) * 12;
            }

            const int minTradesForRegime = 20;
            if (embRet.Count < minTradesForRegime)
                Console.WriteLine($"  insufficient data ({embRet.Count} trades, need ≥{minTradesForRegime}) — not reported");
            else
                CandleFetcher.PrintSplitStats("Held-out (embargoed, bear)", embRet, embCC);
        }

        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    public static async Task RunRegimeRouterTrain(BybitRestClient client, string[]? args = null)
    {
        string variant  = TrainCommands.ResolveVariant(args);
        int?   rngSeed  = GaSearch.ResolveSeed(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("regime_router", variant, Config.RouterGenoFile);
        Console.WriteLine("=== Gravity-gen2 | ROUTERTRAIN (RegimeRouter GA · BTC-anchored · full history) ===");
        Console.WriteLine($"Training RegimeRouter / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        GaSearch.AnnounceCommandSeed("routertrain", rngSeed);
        Console.WriteLine();

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

        RipShortGenotype? rsGenoRt = File.Exists(Config.RipShortGenoFile)
            ? JsonSerializer.Deserialize<RipShortGenotypeDto>(File.ReadAllText(Config.RipShortGenoFile))!.ToGenotype() : null;

        GridGenotype? gsGenoRt = File.Exists(Config.GridShortGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridShortGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"  FadeShort : {fsGeno}");
        if (gridGenoRt != null) Console.WriteLine($"  Grid      : {gridGenoRt}");
        if (flGenoRt   != null) Console.WriteLine($"  FadeLong  : {flGenoRt}  {(flGenoRt.Fitness > 0 ? "✓ included" : "✗ excluded (F≤0)")}");
        if (dlGenoRt   != null) Console.WriteLine($"  DipLong   : {dlGenoRt}  {(dlGenoRt.Fitness > 0 ? "✓ included" : "✗ excluded (F≤0)")}");
        if (rsGenoRt   != null) Console.WriteLine($"  RipShort  : {rsGenoRt}  {(rsGenoRt.Fitness > 0 ? "✓ included" : "✗ excluded (F≤0)")}");
        if (gsGenoRt   != null) Console.WriteLine($"  GridShort : {gsGenoRt}  {(gsGenoRt.Fitness > 0 ? "✓ included" : "✗ excluded (F≤0)")}");
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

        // ── Per-symbol funding for the router's training trade pool ─────────────────────
        // The router is trained on the trade list assembled below, so the funding cost baked
        // into these returns IS the router's fitness landscape. Previously no session existed
        // anywhere in this file and every simulator ran the interest-rate-floor fallback, which
        // systematically understated the cost of the long strategies the router decides to
        // enable in bull regimes — i.e. the router was choosing when to turn longs on using
        // returns that had not paid for holding them. Expect the trained thresholds to move.
        //
        // NOTE — this is the ONLY training path here that can take per-symbol funding today.
        // The FadeLong / DipLong / SwingLong / RipShort GA paths route their candles through
        // `CoinData` records (defined in the *GA.cs files) that carry no symbol, and the GAs
        // re-simulate internally with `funding: null` regardless. Both fixes need changes inside
        // files this change does not own — see the handoff note in the change report.
        Console.WriteLine("  Fetching per-symbol funding rate history...");
        var fundingRt = await CandleFetcher.FetchFundingSessionsAsync(client, fetchedRt.Select(x => x.sym));
        fundingRt.PrintSummary();
        Console.WriteLine();

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
                var fsTrs = FadeShortSimulator.GetFadeShortReturns(fsGenoC, h1, m15, fundingRt.For(sym));
                foreach (var t in fsTrs)
                    allTrades.Add(new(RegimeRouterGA.StrategyKind.FadeShort, t.Time, t.Return, fsGeno.PositionSizePct));
            }
            catch { }

            if (gridGenoRt?.Fitness > 0)
            {
                try
                {
                    var gridTrs = GridSimulator.GetGridReturns(gridGenoRt, h1, fundingRt.For(sym));
                    foreach (var t in gridTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.Grid, t.Time, t.Return, 0.05));
                }
                catch { }
            }

            if (gsGenoRt?.Fitness > 0)
            {
                try
                {
                    var gsTrs = GridShortSimulator.GetGridShortReturns(gsGenoRt, h1, fundingRt.For(sym));
                    foreach (var t in gsTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.GridShort, t.Time, t.Return, 0.05));
                }
                catch { }
            }

            if (flGenoRt?.Fitness > 0)
            {
                try
                {
                    var flTrs = FadeLongSimulator.GetFadeLongReturns(flGenoRt, h1, m15, fundingRt.For(sym))
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
                    var dlTrs = DipLongSimulator.GetDipLongReturns(dlGenoRt, h1, m15, fundingRt.For(sym))
                        .Where(t => t.RegimeBarsActive >= dlGenoRt.RegimeSustainedBars);
                    foreach (var t in dlTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.DipLong, t.Time, t.Return, dlGenoRt.PositionSizePct));
                }
                catch { }
            }

            if (rsGenoRt?.Fitness > 0)
            {
                try
                {
                    var rsTrs = RipShortSimulator.GetRipShortReturnsWithRegime(rsGenoRt, h1, m15, fundingRt.For(sym))
                        .Where(t => t.RegimeBarsActive >= rsGenoRt.RegimeSustainedBars);
                    foreach (var t in rsTrs)
                        allTrades.Add(new(RegimeRouterGA.StrategyKind.RipShort, t.Time, t.Return, rsGenoRt.PositionSizePct));
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
            verbose:           true,
            seed:              rngSeed)
            .Run(btcRegimeSeries, ethRegimeSeries, allTrades, routerSeed);

        Console.WriteLine($"\nFrozen router genotype:\n  {routerBest}\n");
        File.WriteAllText(genoPath,
            JsonSerializer.Serialize(RegimeRouterGenotypeDto.From(routerBest),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");
        Console.WriteLine($"\nNext: dotnet run -- backtest");
    }

    // GRAVITY_RATCHET=<lockAtrMult> during training makes the GA select exits UNDER the profit
    // floor it will actually run with. Genes chosen without it are tuned for exits that no longer
    // happen once the floor is active — the same train-then-deploy gap the router gating exposed.
    static RatchetConfig TrainRatchet() =>
        double.TryParse(Environment.GetEnvironmentVariable("GRAVITY_RATCHET"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var lm) && lm > 0
            ? new RatchetConfig(TriggerPct: 8.0, LockPct: 3.0, TriggerAtrMult: 1.5, LockAtrMult: lm)
            : default;

    public static async Task RunDipLongTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant  = TrainCommands.ResolveVariant(args);
        int?   rngSeed  = GaSearch.ResolveSeed(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("dip_long", variant, Config.DipLongGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | DIPLONGTRAIN (bull pullback, 1h setup + 15m entry/exit, {Config.BacktestCoins.Length} coins, last 18 months) ===");
        Console.WriteLine($"Training DipLong / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        GaSearch.AnnounceCommandSeed("diplongtrain", rngSeed);
        Console.WriteLine();

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

                int split = DataSplit.Split(h1Full).Train.Length;
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

        var dlBtcSeries = btcDlEntry.H1Full is { Length: > 220 }
            ? RegimeClassifier.ClassifySeriesWithDuration(btcDlEntry.H1Full)
            : null;
        var dlBest = new DipLongGA(80, 150, verbose: true, cfg: cfg, btcSeries: dlBtcSeries, seed: rngSeed,
                          ratchet: TrainRatchet()).Run(dlCoins, dlSeed);

        // Post-GA TPE pass removed — see the "Post-GA refinement" note at the top of
        // commands/TrainCommands.cs. DipLongGA already runs 60 TPE iterations internally
        // (DipLongGA.cs ~:268) against its own Fitness(..., useValidation: false).
        Console.WriteLine("\n─── Refinement ───");
        Console.WriteLine("  Handled inside DipLongGA (60 TPE iterations on the GA's own fitness).");
        Console.WriteLine($"  Selected genotype fitness (GA scale, held-out): {dlBest.Fitness:F4}");

        Console.WriteLine($"\nFrozen genotype:\n  {dlBest}\n");
        File.WriteAllText(genoPath, JsonSerializer.Serialize(DipLongGenotypeDto.From(dlBest, cfg),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Validation suite ───");
        try
        {
            var mcResult = MonteCarloTest.Run(
                dlCoins.Where(cd => cd.TrainH1.Length > 0)
                    .SelectMany(cd => DipLongSimulator.GetDipLongReturns(dlBest, cd.TrainH1.Span, cd.TrainM15.Span).Select(t => t.Return)).ToList(),
                permutations: 1000);
            MonteCarloTest.PrintReport(mcResult, "DipLong");
        }
        catch (Exception ex) { Console.WriteLine($"  MonteCarloTest skipped: {ex.Message}"); }

        try
        {
            var ewReport = ExpandingWindowValidation.RunDipLong(dlCoins, cfg);
            ExpandingWindowValidation.PrintReport(ewReport);
        }
        catch (Exception ex) { Console.WriteLine($"  ExpandingWindowValidation skipped: {ex.Message}"); }

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
        int?   rngSeed  = GaSearch.ResolveSeed(args);
        var    cfg      = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("swing_long", variant, Config.SwingLongGenoFile);
        Console.WriteLine($"=== Gravity-gen2 | SWINGLONG TRAIN (bull divergence long, 1h+15m, {Config.BacktestCoins.Length} coins, ~3yr) ===");
        Console.WriteLine($"Training SwingLong / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW} AtrRange=[{cfg.AtrLow},{cfg.AtrHigh}]");
        GaSearch.AnnounceCommandSeed("swinglongtrain", rngSeed);
        Console.WriteLine();

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
            int valStart = DataSplit.Split(h1).Train.Length;
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

        var btcSwingEntry = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        var slBtcSeries = btcSwingEntry.h1 is { Length: > 220 }
            ? RegimeClassifier.ClassifySeriesWithDuration(btcSwingEntry.h1)
            : null;
        var best = new SwingLongGA(80, 150, verbose: true, cfg: cfg, btcSeries: slBtcSeries, seed: rngSeed).Run(coins, seed);

        // Post-GA TPE pass removed — see the "Post-GA refinement" note at the top of
        // commands/TrainCommands.cs. SwingLongGA already runs 60 TPE iterations internally
        // (SwingLongGA.cs ~:213) against its own Fitness(..., useValidation: false).
        Console.WriteLine("\n─── Refinement ───");
        Console.WriteLine("  Handled inside SwingLongGA (60 TPE iterations on the GA's own fitness).");
        Console.WriteLine($"  Selected genotype fitness (GA scale, held-out): {best.Fitness:F4}");

        Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
        File.WriteAllText(genoPath,
            JsonSerializer.Serialize(SwingLongGenotypeDto.From(best, cfg),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved → {genoPath}");

        Console.WriteLine("\n─── Validation suite ───");
        try
        {
            var mcResult = MonteCarloTest.Run(
                coins.Where(cd => cd.TrainH1.Length > 0)
                    .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.TrainH1.Span, cd.TrainM15.Span).Select(t => t.Return)).ToList(),
                permutations: 1000);
            MonteCarloTest.PrintReport(mcResult, "SwingLong");
        }
        catch (Exception ex) { Console.WriteLine($"  MonteCarloTest skipped: {ex.Message}"); }

        try
        {
            var ewReport = ExpandingWindowValidation.RunSwingLong(coins, cfg);
            ExpandingWindowValidation.PrintReport(ewReport);
        }
        catch (Exception ex) { Console.WriteLine($"  ExpandingWindowValidation skipped: {ex.Message}"); }

        // Quick overfit check
        var tRet = coins.Where(cd => cd.TrainH1.Length > 0)
                        .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.TrainH1.Span, cd.TrainM15.Span))
                        .Select(t => t.Return).ToList();
        var vRet = coins.Where(cd => cd.ValH1.Length > 0)
                        .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.ValH1.Span, cd.ValM15.Span))
                        .Select(t => t.Return).ToList();
        Console.WriteLine($"\n  Train: {tRet.Count} trades  avg={( tRet.Count>0 ? tRet.Average():0 ):+0.000;-0.000}%");
        Console.WriteLine($"  Val:   {vRet.Count} trades  avg={( vRet.Count>0 ? vRet.Average():0 ):+0.000;-0.000}%");

        var btcSwing = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        if (btcSwing.h1 != null && btcSwing.h1.Length > 220)
        {
            int btcValStart = DataSplit.Split(btcSwing.h1).Train.Length;
            var btcTrain = btcSwing.h1[..btcValStart];
            var btcVal = btcSwing.h1[btcValStart..];
            PrintRegimeContext(btcTrain, "Train window", "Long");
            PrintRegimeContext(btcVal, "Val window", "Long");
        }

        double vExp = vRet.Count > 0 ? vRet.Average() : 0;
        double tExp = tRet.Count > 0 ? tRet.Average() : 0;
        Console.WriteLine(vExp < tExp * 0.4 || vExp <= 0
            ? "  !! Possible overfit — val expectancy < 40% of train"
            : "  OK — val expectancy within acceptable range");
    }

    public static async Task RunAccumulationGridTrain(BybitRestClient client, string[]? args = null)
    {
        const int TrainWindowH1 = 12_960;
        string variant = TrainCommands.ResolveVariant(args);
        int?   rngSeed = GaSearch.ResolveSeed(args);
        var cfg = FitnessConfig.Load();
        string genoPath = TrainCommands.VariantGenoPath("accumulation_grid", variant, "genotypes/accumulation_grid_genotype.json");
        Console.WriteLine($"=== Gravity-gen2 | ACCUMGRIDTRAIN (EMA-based dynamic grid, replaces FadeShort, {Config.BacktestCoins.Length} coins) ===");
        Console.WriteLine($"Training AccumulationGrid / variant={variant} | SharpeW={cfg.SharpeW} CalmarW={cfg.CalmarW}");
        GaSearch.AnnounceCommandSeed("accumgridtrain", rngSeed);
        Console.WriteLine();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15Full = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1Full = FadeShortSimulator.AggregateCandles(m15Full.ToArray(), 4);
                int h1Start = Math.Max(0, h1Full.Length - TrainWindowH1);
                var h1 = h1Full[h1Start..];
                Console.WriteLine($"  {sym}: {h1Full.Length} h1 total, {h1.Length} in 18-month window");
                return (sym, h1Full, h1);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        var coins = new List<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGA.CoinData>();
        foreach (var (sym, h1Full, h1) in fetched)
        {
            if (h1.Length < 150) { Console.WriteLine($"  {sym}: skip (insufficient data)"); continue; }
            var volUsd = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) { Console.WriteLine($"  {sym}: skip (vol=${medVol:F2}M/h)"); continue; }

            int split = DataSplit.Split(h1Full).Train.Length;
            coins.Add(new GravityGen2.Strategies.AccumulationGrid.AccumulationGridGA.CoinData(
                h1Full[..split], h1Full[split..]));
        }

        Console.WriteLine($"\n  Coins passed to GA: {coins.Count} (80/20 split)\n");
        if (coins.Count == 0) { Console.WriteLine("No data."); return; }

        Console.WriteLine("Training Bull regime AccumulationGrid...");
        var bullGa = new GravityGen2.Strategies.AccumulationGrid.AccumulationGridGA(
            MarketRegime.Bull, populationSize: 60, generations: 100, eliteCount: 15, verbose: true, cfg: cfg,
            seed: rngSeed);
        var (bullBest, bullFitness) = bullGa.Train(coins);
        Console.WriteLine($"\nBull best: {bullBest}  fitness={bullFitness:F3}");

        Console.WriteLine("\nTraining Bear regime AccumulationGrid...");
        var bearGa = new GravityGen2.Strategies.AccumulationGrid.AccumulationGridGA(
            MarketRegime.Bear, populationSize: 60, generations: 100, eliteCount: 15, verbose: true, cfg: cfg,
            // +1 so Bull and Bear do not replay the identical RNG stream under one --seed.
            seed: rngSeed is int agSeed ? unchecked(agSeed + 1) : null);
        var (bearBest, bearFitness) = bearGa.Train(coins);
        Console.WriteLine($"\nBear best: {bearBest}  fitness={bearFitness:F3}");

        var result = new
        {
            Bull = bullBest,
            BullFitness = bullFitness,
            Bear = bearBest,
            BearFitness = bearFitness
        };

        File.WriteAllText(genoPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nSaved → {genoPath}");
    }

    private static void PrintRegimeContext(Candle[] btcH1, string windowLabel, string strategyRegime)
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
                              (strategyRegime == "Long" && regimeCounts.GetValueOrDefault(MarketRegime.Bear, 0) > total * 0.6) ||
                              (strategyRegime == "Short" && regimeCounts.GetValueOrDefault(MarketRegime.Bull, 0) > total * 0.6);
        
        if (regimeMismatch)
            Console.WriteLine($"  ⚠ {windowLabel} regime mismatches {strategyRegime} strategy — low expectancy may be regime-driven, not overfit");
    }
}
