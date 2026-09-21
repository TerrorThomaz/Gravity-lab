using System.Text.Json;

namespace TradingGA;

/// <summary>
/// Informational-only scan: did any strategy have a completed or open trade on the Hyperliquid
/// coin universe in the last N hours? Read-only — never places an order. Useful after fixing a
/// bug that would have blocked real order placement, to check whether anything was missed.
/// Every raw signal is also re-evaluated against the router genotype using BTC's actual regime
/// history at that trade's own entry time, so the report shows both the full shadow set and
/// which of them the router would actually have allowed through.
/// </summary>
static class HyperliquidLookback
{
    public static async Task Run(int lookbackHours = 48)
    {
        Console.WriteLine($"=== Gravity-gen2 | HYPERLIQUID LOOK-BACK (last {lookbackHours}h, informational — no orders) ===\n");

        using var client = new HyperliquidClient();
        if (!await client.IsReadyAsync())
        {
            Console.WriteLine($"[ERROR] Hyperliquid bridge not reachable at {HyperliquidClient.DefaultBaseUrl}");
            return;
        }

        var fsVariants = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariants = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariants = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariants = StrategyPipeline.LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariants = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        var gUniversal = fsVariants.Length > 0 ? fsVariants[0].Genotype : null;
        var clusterGenos = new Dictionary<CoinCluster, FadeShortGenotype>();
        foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
        {
            string clFile = CoinClusterHelper.GenoFile(cl);
            clusterGenos[cl] = File.Exists(clFile)
                ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                : gUniversal!;
        }
        var gridG = gridVariants.Length > 0 ? gridVariants[0].Genotype : null;
        var slG = slVariants.Length > 0 ? slVariants[0].Genotype : null;
        var dlG = dlVariants.Length > 0 ? dlVariants[0].Genotype : null;
        var flG = flVariants.Length > 0 ? flVariants[0].Genotype : null;
        var rsG = rsVariants.Length > 0 ? rsVariants[0].Genotype : null;

        var coins = Config.BacktestCoins.ToList();
        var universe = await client.FetchUniverseAsync();
        if (universe.TryGetValue("perpetuals", out var perpsObj) && perpsObj is JsonElement { ValueKind: JsonValueKind.Array } perpsEl)
        {
            var hlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in perpsEl.EnumerateArray())
                if (p.TryGetProperty("name", out var nameEl))
                    hlNames.Add(nameEl.GetString() ?? "");
            coins = coins.Where(c => hlNames.Contains(HyperliquidClient.NormalizeToHyperliquidCoin(c))).ToList();
        }
        Console.WriteLine($"Scanning {coins.Count} resolvable coins...\n");

        // BTC regime history over the lookback window, so each found trade can be gated by the
        // router exactly as it would have been at that trade's own entry time.
        RegimeRouterGenotype? routerGeno = File.Exists(Config.RouterGenoFile)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()
            : null;
        RegimeBar[]? btcSeries = null;
        MarketRegime[]? prevRegimeAt = null;
        try
        {
            var btcM15 = await client.FetchH1CandlesAsync("BTCUSDT", batches: HyperliquidClient.LiveBatches);
            var btcH1 = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);
            btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
            prevRegimeAt = new MarketRegime[btcSeries.Length];
            var lastRegime = btcSeries.Length > 0 ? btcSeries[0].Regime : MarketRegime.Ranging;
            var streakStartRegime = lastRegime;
            for (int i = 0; i < btcSeries.Length; i++)
            {
                if (btcSeries[i].Regime != lastRegime)
                {
                    streakStartRegime = lastRegime;
                    lastRegime = btcSeries[i].Regime;
                }
                prevRegimeAt[i] = streakStartRegime;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: BTC regime history fetch failed, router gating unavailable: {ex.Message}\n");
        }

        // When did each strategy's router gate actually flip on/off? Walk the series once and
        // print every transition — answers "since when" precisely instead of by inference.
        if (routerGeno != null && btcSeries != null && prevRegimeAt != null && btcSeries.Length > 0)
        {
            Console.WriteLine("Router gate transitions (BTC regime history):");
            string[] names = { "FadeShort", "Grid", "SwingLong", "DipLong", "FadeLong", "RipShort" };
            static bool[] Flags((bool FadeShort, bool Grid, bool GridShort, bool DipLong, bool FadeLong, bool RipShort, bool SwingLong, bool AccumulationGrid,
                bool FadeShortLowVol, bool DipLongLowVol, bool SwingLongLowVol, bool RipShortLowVol,
                bool FadeShortHighVol, bool DipLongHighVol, bool SwingLongHighVol, bool RipShortHighVol) a)
                => new[] { a.FadeShort, a.Grid, a.SwingLong, a.DipLong, a.FadeLong, a.RipShort };

            var lastFlags = Flags(RegimeRouter.ComputeActivation(btcSeries[0].Regime, btcSeries[0].Confidence, btcSeries[0].Duration, prevRegimeAt[0], routerGeno));
            for (int i = 1; i < btcSeries.Length; i++)
            {
                var flags = Flags(RegimeRouter.ComputeActivation(btcSeries[i].Regime, btcSeries[i].Confidence, btcSeries[i].Duration, prevRegimeAt[i], routerGeno));
                for (int s = 0; s < names.Length; s++)
                {
                    if (flags[s] != lastFlags[s])
                        Console.WriteLine($"  {btcSeries[i].Time:yyyy-MM-dd HH:mm}  {names[s],-10} {(flags[s] ? "ON " : "OFF")}  (regime={btcSeries[i].Regime} conf={btcSeries[i].Confidence:P0} dur={btcSeries[i].Duration}h)");
                }
                lastFlags = flags;
            }
            Console.WriteLine();
        }

        bool? RoutedAt(string strat, DateTime entryTime)
        {
            if (routerGeno == null || btcSeries == null || prevRegimeAt == null || btcSeries.Length == 0) return null;
            int idx = Array.FindLastIndex(btcSeries, b => b.Time <= entryTime);
            if (idx < 0) idx = 0;
            var bar = btcSeries[idx];
            var act = RegimeRouter.ComputeActivation(bar.Regime, bar.Confidence, bar.Duration, prevRegimeAt[idx], routerGeno);
            return strat switch
            {
                "FadeShort"  => act.FadeShort,
                "Grid"       => act.Grid,
                "SwingLong"  => act.SwingLong,
                "DipLong"    => act.DipLong,
                "FadeLong"   => act.FadeLong,
                "RipShort"   => act.RipShort,
                _ => (bool?)null,
            };
        }

        var cutoff = DateTime.UtcNow.AddHours(-lookbackHours);
        var found = new List<(string Strat, string Sym, DateTime EntryTime, double EntryPrice, DateTime ExitTime, double Return, string Kind)>();
        var lockObj = new object();
        var sem = new SemaphoreSlim(4);

        var tasks = coins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15List = await client.FetchH1CandlesAsync(sym, batches: HyperliquidClient.LiveBatches);
                if (m15List.Count < 900) return; // need enough for h1 aggregation + warmup
                var m15 = m15List.ToArray();
                var h1 = FadeShortSimulator.AggregateCandles(m15, 4);

                void Add(string strat, IEnumerable<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> trades)
                {
                    foreach (var t in trades)
                        if (t.EntryTime >= cutoff)
                            lock (lockObj) found.Add((strat, sym, t.EntryTime, t.EntryPrice, t.Time, t.Return, t.Kind));
                }

                var coinCl = CoinClusterHelper.ClassifyByName(sym);
                var gFs = clusterGenos.TryGetValue(coinCl, out var g) ? g : gUniversal;
                if (gFs != null) Add("FadeShort", FadeShortSimulator.GetFadeShortReturns(gFs, h1, m15));
                if (gridG != null) Add("Grid", GridSimulator.GetGridReturns(gridG, h1));
                if (slG != null) Add("SwingLong", SwingLongSimulator.GetSwingLongReturns(slG, h1, m15));
                if (dlG != null) Add("DipLong", DipLongSimulator.GetDipLongReturns(dlG, h1, m15)
                    .Select(t => (t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice)));
                if (flG != null) Add("FadeLong", FadeLongSimulator.GetFadeLongReturns(flG, h1, m15)
                    .Select(t => (t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice)));
                if (rsG != null) Add("RipShort", RipShortSimulator.GetRipShortReturns(rsG, h1, m15));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: {sym}: {ex.Message}");
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        if (found.Count == 0)
        {
            Console.WriteLine($"No trades in the last {lookbackHours}h across any strategy on the resolvable universe.");
            return;
        }

        Console.WriteLine($"{found.Count} raw signal(s) in the last {lookbackHours}h:\n");
        Console.WriteLine($"{"Entry Time",-17} {"Strat",-10} {"Coin",-10} {"Entry",10}  {"Exit Time",-17} {"Ret%",8}  Routed  Kind");
        var gatedRets = new List<double>();
        foreach (var t in found.OrderBy(t => t.EntryTime))
        {
            bool stillLive = t.ExitTime >= DateTime.UtcNow.AddHours(-1.5);
            bool? routed = RoutedAt(t.Strat, t.EntryTime);
            if (routed == true) gatedRets.Add(t.Return);
            string routedStr = routed switch { true => "  YES ", false => "  no  ", _ => "  ?   " };
            Console.WriteLine($"{t.EntryTime:yyyy-MM-dd HH:mm}  {t.Strat,-10} {t.Sym,-10} {t.EntryPrice,10:F4}  {t.ExitTime:yyyy-MM-dd HH:mm}  {t.Return,8:+0.00;-0.00}{routedStr}  {t.Kind}{(stillLive ? "  [recent — may still be live]" : "")}");
        }

        if (routerGeno != null)
        {
            Console.WriteLine($"\nRouter-gated: {gatedRets.Count}/{found.Count} would have passed the router.");
            if (gatedRets.Count > 0)
                Console.WriteLine($"  Gated: n={gatedRets.Count} winrate={gatedRets.Count(r => r > 0) * 100.0 / gatedRets.Count:F0}% sum={gatedRets.Sum():+0.00;-0.00}% avg={gatedRets.Average():+0.00;-0.00}%");
        }
    }
}
