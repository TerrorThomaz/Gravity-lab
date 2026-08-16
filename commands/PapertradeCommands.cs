using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class PapertradeCommands
{
    // Variant loading/selection live in StrategyPipeline (the reproducer-of-record).

    // Replays a saved journal to the set of currently-open positions (entry not yet
    // followed by an exit), so restarts don't re-emit "entry" events for open trades.
    static Dictionary<string, (string Dir, double Entry)> OpenKeysFromJournal(List<JsonElement> journal)
    {
        var open = new Dictionary<string, (string Dir, double Entry)>();
        foreach (var e in journal)
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("evt", out var evtP)) continue;
            if (!e.TryGetProperty("strat", out var stratP) || !e.TryGetProperty("sym", out var symP)) continue;
            string key = $"{stratP.GetString()}:{symP.GetString()}";
            switch (evtP.GetString())
            {
                case "entry":
                    string dir   = e.TryGetProperty("dir", out var dP) ? dP.GetString() ?? "" : "";
                    double entry = e.TryGetProperty("entry", out var enP) && enP.ValueKind == JsonValueKind.Number ? enP.GetDouble() : 0;
                    open[key] = (dir, entry);
                    break;
                case "exit":
                    open.Remove(key);
                    break;
            }
        }
        return open;
    }

    public static async Task RunPaperTrade(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (15m→1h candles) — Ctrl+C to stop ===\n");

        if (!File.Exists(Config.FadeShortGenoFile))
        {
            Console.WriteLine($"No genotype at '{Config.FadeShortGenoFile}'. Run 'dotnet run -- train' first.");
            return;
        }

        // Load variant arrays at startup (currently single-element; ready for multi-variant)
        var fsVariantsPt = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariantsPt = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariantsPt = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariantsPt = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariantsPt = StrategyPipeline.LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariantsPt = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        var gUniversalPt = fsVariantsPt.Length > 0 ? fsVariantsPt[0].Genotype! : null;
        if (gUniversalPt == null)
        {
            Console.WriteLine($"No genotype at '{Config.FadeShortGenoFile}'. Run 'dotnet run -- train' first.");
            return;
        }
        Console.WriteLine($"Universal genotype: {gUniversalPt}  [{fsVariantsPt.Length} variant(s)]");

        var clusterGenosPt = new Dictionary<CoinCluster, FadeShortGenotype>();
        foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
        {
            string clFile = CoinClusterHelper.GenoFile(cl);
            clusterGenosPt[cl] = File.Exists(clFile)
                ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                : gUniversalPt;
            Console.WriteLine($"  [{CoinClusterHelper.Label(cl)}] {clusterGenosPt[cl]}");
        }

        var gridGPt = gridVariantsPt.Length > 0 ? gridVariantsPt[0].Genotype : null;
        if (gridGPt != null)
            Console.WriteLine($"Grid genotype:     {gridGPt}  [{gridVariantsPt.Length} variant(s)]");
        else
            Console.WriteLine($"  (no grid genotype — run 'dotnet run -- gridtrain' to include grid)");

        RegimeRouterGenotype? routerGenoPt = null;
        if (File.Exists(Config.RouterGenoFile))
        {
            routerGenoPt = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(
                File.ReadAllText(Config.RouterGenoFile))!.ToGenotype();
            Console.WriteLine($"Router genotype:   (trained)");
        }
        else Console.WriteLine("  (no router genotype — using rule-based routing)");

        DynamicGuardGenotype? guardGenoPt = null;
        if (File.Exists(Config.DynamicGuardGenoFile))
        {
            guardGenoPt = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(
                File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype();
            Console.WriteLine($"Guard genotype:    (trained)");
        }
        else Console.WriteLine("  (no guard genotype — guard disabled)");

        VolatilityWeightedRotatorGenotype? rotatorGenoPt = null;
        if (File.Exists(Config.RotatorGenoFile))
        {
            rotatorGenoPt = JsonSerializer.Deserialize<VolatilityWeightedRotatorGenotypeDto>(
                File.ReadAllText(Config.RotatorGenoFile))!.ToGenotype();
            Console.WriteLine($"Rotator genotype:  (trained)");
        }
        else Console.WriteLine("  (no rotator genotype — rotation disabled)");

        var slGenoPt = slVariantsPt.Length > 0 ? slVariantsPt[0].Genotype : null;
        if (slGenoPt != null)
            Console.WriteLine($"SwingLong:         {slGenoPt}  [{slVariantsPt.Length} variant(s)]");

        var dlGenoPt = dlVariantsPt.Length > 0 ? dlVariantsPt[0].Genotype : null;
        if (dlGenoPt != null)
            Console.WriteLine($"DipLong:           {dlGenoPt}  [{dlVariantsPt.Length} variant(s)]");

        var flGenoPt = flVariantsPt.Length > 0 ? flVariantsPt[0].Genotype : null;
        if (flGenoPt != null)
            Console.WriteLine($"FadeLong:          {flGenoPt}  [{flVariantsPt.Length} variant(s)]");

        var rsGenoPt = rsVariantsPt.Length > 0 ? rsVariantsPt[0].Genotype : null;
        if (rsGenoPt != null)
            Console.WriteLine($"RipShort:          {rsGenoPt}  [{rsVariantsPt.Length} variant(s)]");

        Console.WriteLine();

        var coins = Config.BacktestCoins;

        const int RefreshSeconds = 900;

        // ── Stateful trade journal ───────────────────────────────────────────────
        // prevOpen tracks the previous cycle's open positions to diff entries/exits.
        // Seeded from any persisted journal so restarts don't re-emit open trades.
        var journal = new List<JsonElement>();
        try
        {
            if (File.Exists("live_journal.json"))
                journal = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText("live_journal.json")) ?? new List<JsonElement>();
        }
        catch { journal = new List<JsonElement>(); }

        var prevOpen = new Dictionary<string, (string Dir, double Entry, DateTime FirstSeen, double LastMark, double LastPnl, bool Routed, double GuardMult)>();
        foreach (var kv in OpenKeysFromJournal(journal))
            prevOpen[kv.Key] = (kv.Value.Dir, kv.Value.Entry, DateTime.UtcNow, kv.Value.Entry, 0.0, true, 1.0);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            try { Console.Clear(); } catch (System.IO.IOException) { }
            Console.WriteLine($"=== Gravity-gen2 | PAPER TRADE  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]  Ctrl+C to stop ===\n");

            // Fetch all coins in parallel (4 concurrent), then trim to last 2000 m15 bars.
            // Full CSV history (~28 000 h1 bars) is never loaded into simulator arrays —
            // 500 h1 / 2000 m15 gives a 2× margin over the worst-case warmup (FadeLong 212 bars).
            // 25 batches ≈ 3 months of 15m data (covers all indicator warmups).
            const int PtM15Window = 2000;
            const int PtBatches   = 25;
            var sem = new SemaphoreSlim(4);
            var fetchTasks = coins.Select(async sym =>
            {
                await sem.WaitAsync(cts.Token);
                try
                {
                    var m15Raw = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: PtBatches);
                    if (m15Raw.Count < 200) return (sym, (Candle[]?)null, (Candle[]?)null, false, 0.0, 0.0);
                    var trimmed = m15Raw.Count > PtM15Window
                        ? m15Raw.GetRange(m15Raw.Count - PtM15Window, PtM15Window)
                        : m15Raw;
                    // Screen on median 1h USD volume over full history (matches CombinedBacktest).
                    // h1Full/volM are screening + display only; simulators use the trimmed h1 below.
                    var (_, atrPct, _) = CandleFetcher.CheckSwingCriteria(m15Raw);
                    var h1Full  = FadeShortSimulator.AggregateCandles(m15Raw.ToArray(), 4);
                    var volUsd  = h1Full.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                    double volM = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                    bool   passes = volM >= Config.MinMedianVolUsdM;
                    var m15 = trimmed.ToArray();
                    var h1  = FadeShortSimulator.AggregateCandles(m15, 4);
                    return (sym, (Candle[]?)h1, (Candle[]?)m15, passes, atrPct, volM);
                }
                finally { sem.Release(); }
            }).ToList();
            var coinData = (await Task.WhenAll(fetchTasks))
                .Select(r => (Sym: r.sym, H1: r.Item2, M15: r.Item3, Passes: r.Item4, AtrPct: r.Item5, VolM: r.Item6))
                .ToList();

            // ── Funding rate session (BTC perpetual) ─────────────────────────────
            FundingRateSession? fundingSession = null;
            try
            {
                var btcFunding = await CandleFetcher.FetchFundingRateCachedAsync(client, "BTCUSDT");
                if (btcFunding.Length > 0)
                {
                    fundingSession = new FundingRateSession(btcFunding);
                    Console.WriteLine($"  Funding: {fundingSession.CurrentRate:+0.0000%;-0.0000%} (current rate)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ Funding fetch failed: {ex.Message}");
            }

            // ── Dynamic guard session (BTC H1 → 4H ATR stress detection) ────────
            DynamicGuardSession? guardSession = null;
            var btcData = coinData.FirstOrDefault(x => x.Sym == "BTCUSDT");
            if (guardGenoPt != null && btcData.H1 is { Length: >= 50 })
            {
                guardSession = new DynamicGuardSession(btcData.H1, guardGenoPt);
                double guardMult = guardSession.GetMult(DateTime.UtcNow);
                string guardTag = guardMult < 1.0 ? $"STRESS ×{guardMult:F2}" : "normal";
                Console.WriteLine($"  Guard: {guardTag}");
            }

            // ── Regime routing (BTC primary · ETH secondary) ─────────────────────
            StrategyActivation? ptRouting = null;
            {
                var btcPt = coinData.FirstOrDefault(x => x.Sym == "BTCUSDT");
                var ethPt = coinData.FirstOrDefault(x => x.Sym == "ETHUSDT");
                if (btcPt.H1 is { Length: > 220 })
                {
                    Candle[]? ethH1Pt = ethPt.H1 is { Length: > 220 } ? ethPt.H1 : null;
                    ptRouting = routerGenoPt != null
                        ? RegimeRouter.Route(btcPt.H1, routerGenoPt, ethH1Pt)
                        : RegimeRouter.Route(btcPt.H1, ethH1Pt);
                    string routerTag = routerGenoPt != null ? "(trained)" : "(rule-based)";
                    var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcPt.H1);
                    int bearDur = btcSeries[^1].Regime == MarketRegime.Bear ? btcSeries[^1].Duration : 0;
                    int bullDur = btcSeries[^1].Regime == MarketRegime.Bull ? btcSeries[^1].Duration : 0;
                    string durTag = bearDur > 0 ? $"  bear={bearDur}h" : bullDur > 0 ? $"  bull={bullDur}h" : "";
                    Console.WriteLine($"  Regime {routerTag}: {ptRouting.Regime}  conf={ptRouting.Confidence:P0}  →  {RegimeRouter.Describe(ptRouting)}{durTag}\n");
                }
            }

            // ── Volatility-weighted rotator (gradual alt↔BTC/ETH rotation) ──────
            VolatilityWeightedRotator? rotator = null;
            double safetyScore = 0.0;
            if (rotatorGenoPt != null && guardSession != null && ptRouting != null)
            {
                rotator = new VolatilityWeightedRotator(rotatorGenoPt);
                double guardMult = guardSession.GetMult(DateTime.UtcNow);
                double atrRatio = guardSession.GetAtrRatio(DateTime.UtcNow);
                safetyScore = rotator.ComputeSafetyScore(guardMult, atrRatio, ptRouting.Regime);
                var (altShare, btcShare, ethShare) = rotator.ComputeAllocation(safetyScore);
                Console.WriteLine($"  Rotator: safety={safetyScore:F2} → alts={altShare:P0} BTC={btcShare:P0} ETH={ethShare:P0}");
            }

            // ── FadeShort ─────────────────────────────────────────────────────────
            // Router-gated (suppressed in confirmed Bull), mirroring the backtests.
            bool fsRoutedOn = ptRouting == null || ptRouting.FadeShortActive;
            if (fsRoutedOn && !cts.Token.IsCancellationRequested)
            {
                Console.WriteLine($"── FadeShort {new string('─', 93)}");
                Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
                Console.WriteLine(new string('-', 105));

                foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
                    if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }

                    double px       = h1[^1].Close;
                    var    coinCl   = CoinClusterHelper.ClassifyByName(sym);
                    // Use VariantRouter to select per-coin ATR-regime variant; fall back to cluster genotype
                    var    gForCoin = StrategyPipeline.SelectVariant(fsVariantsPt, m15) ?? clusterGenosPt[coinCl];
                    var    st       = FadeShortSimulator.GetFadeShortTradeState(gForCoin, h1, m15);

                    string stateStr  = st.InTrade
                        ? (st.TrailArmed ? "TRAIL ARMED" : $"SHORT b{st.HoldCount}")
                        : "watching";
                    string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
                    string unreal    = st.InTrade ? $"{(st.Entry - px) / st.Entry * 100.0:+0.00}%" : "—";
                    double effStop   = st.InTrade ? Math.Min(st.HardStop, st.MaeStop) : 0;
                    string stopStr   = st.InTrade ? $"{effStop:F4}" : "—";
                    string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
                    string barsStr   = st.InTrade ? $"{st.HoldCount}" : "—";

                    Console.WriteLine($"  {sym,-18}  {stateStr,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {barsStr,5}  {stopStr,12}  {targetStr,12}");
                }
            }

            // ── Grid ──────────────────────────────────────────────────────────────
            bool gridRoutedOn = ptRouting == null || ptRouting.GridActive;
            if (gridGPt != null && gridRoutedOn && !cts.Token.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"── Grid {new string('─', 98)}");
                Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Anchor",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}");
                Console.WriteLine(new string('-', 93));

                foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (h1 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
                    if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }

                    double px       = h1[^1].Close;
                    var    coinGridG = (m15 != null ? StrategyPipeline.SelectVariant(gridVariantsPt, m15) : null) ?? gridGPt;
                    var    gst = GridSimulator.GetGridTradeState(coinGridG, h1);

                    string stateStr  = gst.Active ? $"GRID L{gst.FilledLevels}" : "watching";
                    string anchorStr = gst.Active ? $"{gst.Anchor:F4}" : "—";
                    string unreal    = gst.Active ? $"{(px - gst.Anchor) / gst.Anchor * 100.0:+0.00}%" : "—";
                    string stopStr   = gst.Active ? $"{gst.HardStop:F4}" : "—";
                    string barsStr   = gst.Active ? $"{gst.HoldCount}" : "—";

                    Console.WriteLine($"  {sym,-18}  {stateStr,-14} {anchorStr,12}  {px,12:F4}  {unreal,11}  {barsStr,5}  {stopStr,12}");
                }
            }

            int slOpen = 0, dlOpen = 0, flOpen = 0, rsOpen = 0;

            // ── SwingLong ─────────────────────────────────────────────────────────
            bool slRoutedOn = ptRouting == null || ptRouting.SwingLongActive;
            if (slGenoPt != null && slRoutedOn && !cts.Token.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"── SwingLong {new string('─', 92)}");
                Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
                Console.WriteLine(new string('-', 105));
                foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
                    if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
                    double px      = h1[^1].Close;
                    var coinSlG    = StrategyPipeline.SelectVariant(slVariantsPt, m15) ?? slGenoPt;
                    var st = SwingLongSimulator.GetSwingLongTradeState(coinSlG, h1, m15, fundingSession);
                    string capTag = (st.InTrade && slOpen >= PortfolioReplay.DefaultCaps["swing_long"]) ? " [CAP]" : "";
                    if (st.InTrade) slOpen++;
                    string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"LONG b{st.HoldCount}") : "watching";
                    string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
                    string unreal    = st.InTrade ? $"{(px - st.Entry) / st.Entry * 100.0:+0.00}%" : "—";
                    string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
                    string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
                    Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
                }
            }

            // ── DipLong ───────────────────────────────────────────────────────────
            bool dlRoutedOn = ptRouting == null || ptRouting.DipLongActive;
            if (dlGenoPt != null && dlRoutedOn && !cts.Token.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"── DipLong {new string('─', 94)}");
                Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
                Console.WriteLine(new string('-', 105));
                foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
                    if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
                    double px      = h1[^1].Close;
                    var coinDlG    = StrategyPipeline.SelectVariant(dlVariantsPt, m15) ?? dlGenoPt;
                    var st = DipLongSimulator.GetDipLongTradeState(coinDlG, h1, m15, fundingSession);
                    string capTag = (st.InTrade && dlOpen >= PortfolioReplay.DefaultCaps["diplong"]) ? " [CAP]" : "";
                    if (st.InTrade) dlOpen++;
                    string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"LONG b{st.HoldCount}") : "watching";
                    string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
                    string unreal    = st.InTrade ? $"{(px - st.Entry) / st.Entry * 100.0:+0.00}%" : "—";
                    string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
                    string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
                    Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
                }
            }

            // ── FadeLong ──────────────────────────────────────────────────────────
            bool flRoutedOn = ptRouting == null || ptRouting.FadeLongActive;
            if (flGenoPt != null && flRoutedOn && !cts.Token.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"── FadeLong {new string('─', 93)}");
                Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
                Console.WriteLine(new string('-', 105));
                foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
                    if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
                    double px      = h1[^1].Close;
                    var coinFlG    = StrategyPipeline.SelectVariant(flVariantsPt, m15) ?? flGenoPt;
                    var st = FadeLongSimulator.GetFadeLongTradeState(coinFlG, h1, m15, fundingSession);
                    string capTag = (st.InTrade && flOpen >= PortfolioReplay.DefaultCaps["fadelong"]) ? " [CAP]" : "";
                    if (st.InTrade) flOpen++;
                    string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"LONG b{st.HoldCount}") : "watching";
                    string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
                    string unreal    = st.InTrade ? $"{(px - st.Entry) / st.Entry * 100.0:+0.00}%" : "—";
                    // FadeLong has both HardStop and MaeStop — display the tighter one
                    string stopStr   = st.InTrade ? $"{Math.Min(st.HardStop, st.MaeStop):F4}" : "—";
                    string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
                    Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
                }
            }

            // ── RipShort ──────────────────────────────────────────────────────────
            bool rsRoutedOn = ptRouting == null || ptRouting.RipShortActive;
            if (rsGenoPt != null && rsRoutedOn && !cts.Token.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine($"── RipShort {new string('─', 93)}");
                Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
                Console.WriteLine(new string('-', 105));
                foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
                    if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
                    double px      = h1[^1].Close;
                    var coinRsG    = StrategyPipeline.SelectVariant(rsVariantsPt, m15) ?? rsGenoPt;
                    var st = RipShortSimulator.GetRipShortTradeState(coinRsG, h1, m15);
                    string capTag = (st.InTrade && rsOpen >= PortfolioReplay.DefaultCaps["ripshort"]) ? " [CAP]" : "";
                    if (st.InTrade) rsOpen++;
                    string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"SHORT b{st.HoldCount}") : "watching";
                    string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
                    // Short position: profit when price falls below entry
                    string unreal    = st.InTrade ? $"{(st.Entry - px) / st.Entry * 100.0:+0.00}%" : "—";
                    string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
                    string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
                    Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
                }
            }

            if (cts.Token.IsCancellationRequested) break;

            // ── Write live_state.json for frontend consumption ───────────────
            {
                var positions = new List<object>();

                // ── SHADOW MODE ───────────────────────────────────────────────────────────
                // Every strategy is evaluated every cycle, whether or not the router allows it,
                // and each position carries `routed` plus the guard multiplier it would have been
                // sized by.
                //
                // Previously a router-gated strategy was not computed AT ALL — the check wrapped
                // the computation instead of labelling its output. So a papertrade run recorded
                // only the trades the router permitted, and the two components whose value is
                // hardest to establish are exactly the two that are pure gates. Their worth lives
                // entirely in the counterfactual: what did the router suppress, and would it have
                // won? A log of permitted trades cannot answer that at any run length.
                //
                // Cost is one extra simulator pass per gated strategy per 15-minute cycle.
                //
                // NOTE ON READING IT LATER: `routed:false` rows are NOT paper trades. They are
                // positions the system declined to take. Summing their P&L alongside the real ones
                // gives a number the portfolio never had — compare the two sets, never merge them.
                double ptGuardMult = guardSession?.GetMult(DateTime.UtcNow) ?? 1.0;

                // FadeShort positions
                if (true)   // shadow: computed even when the router gates FadeShort off
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinCl = CoinClusterHelper.ClassifyByName(sym);
                        var gForCoin = StrategyPipeline.SelectVariant(fsVariantsPt, m15) ?? clusterGenosPt[coinCl];
                        var st = FadeShortSimulator.GetFadeShortTradeState(gForCoin, h1, m15);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "FadeShort", dir = "Short",
                            routed = fsRoutedOn,   guardMult = ptGuardMult,
                            entry = Math.Round(st.Entry, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round((st.Entry - px) / st.Entry * 100.0, 2),
                            age   = $"{st.HoldCount}h",
                            stop  = Math.Round(Math.Min(st.HardStop, st.MaeStop), 6),
                            target = Math.Round(st.Target, 6),
                            trailArmed = st.TrailArmed,
                        });
                    }
                }

                // Grid positions
                if (gridGPt != null)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || !passes) continue;
                        var coinGridG = (m15 != null ? StrategyPipeline.SelectVariant(gridVariantsPt, m15) : null) ?? gridGPt;
                        var gst = GridSimulator.GetGridTradeState(coinGridG, h1);
                        if (!gst.Active) continue;
                        double px = h1[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "Grid", dir = "Long",
                            routed = gridRoutedOn, guardMult = ptGuardMult,
                            entry = Math.Round(gst.Anchor, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round((px - gst.Anchor) / gst.Anchor * 100.0, 2),
                            age   = $"{gst.HoldCount}h",
                            stop  = Math.Round(gst.HardStop, 6),
                            target = 0.0,
                            trailArmed = false,
                        });
                    }
                }

                // SwingLong positions
                if (slGenoPt != null)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinSlG = StrategyPipeline.SelectVariant(slVariantsPt, m15) ?? slGenoPt;
                        var st = SwingLongSimulator.GetSwingLongTradeState(coinSlG, h1, m15, fundingSession);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "SwingLong", dir = "Long",
                            routed = slRoutedOn,   guardMult = ptGuardMult,
                            entry = Math.Round(st.Entry, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round((px - st.Entry) / st.Entry * 100.0, 2),
                            age   = $"{st.HoldCount}h",
                            stop  = Math.Round(st.HardStop, 6),
                            target = Math.Round(st.Target, 6),
                            trailArmed = st.TrailArmed,
                        });
                    }
                }

                // DipLong positions
                if (dlGenoPt != null)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinDlG = StrategyPipeline.SelectVariant(dlVariantsPt, m15) ?? dlGenoPt;
                        var st = DipLongSimulator.GetDipLongTradeState(coinDlG, h1, m15, fundingSession);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "DipLong", dir = "Long",
                            routed = dlRoutedOn,   guardMult = ptGuardMult,
                            entry = Math.Round(st.Entry, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round((px - st.Entry) / st.Entry * 100.0, 2),
                            age   = $"{st.HoldCount}h",
                            stop  = Math.Round(st.HardStop, 6),
                            target = Math.Round(st.Target, 6),
                            trailArmed = st.TrailArmed,
                        });
                    }
                }

                // FadeLong positions
                if (flGenoPt != null)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinFlG = StrategyPipeline.SelectVariant(flVariantsPt, m15) ?? flGenoPt;
                        var st = FadeLongSimulator.GetFadeLongTradeState(coinFlG, h1, m15, fundingSession);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "FadeLong", dir = "Long",
                            routed = flRoutedOn,   guardMult = ptGuardMult,
                            entry = Math.Round(st.Entry, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round((px - st.Entry) / st.Entry * 100.0, 2),
                            age   = $"{st.HoldCount}h",
                            stop  = Math.Round(Math.Min(st.HardStop, st.MaeStop), 6),
                            target = Math.Round(st.Target, 6),
                            trailArmed = st.TrailArmed,
                        });
                    }
                }

                // RipShort positions
                if (rsGenoPt != null)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinRsG = StrategyPipeline.SelectVariant(rsVariantsPt, m15) ?? rsGenoPt;
                        var st = RipShortSimulator.GetRipShortTradeState(coinRsG, h1, m15);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "RipShort", dir = "Short",
                            routed = rsRoutedOn,   guardMult = ptGuardMult,
                            entry = Math.Round(st.Entry, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round((st.Entry - px) / st.Entry * 100.0, 2),
                            age   = $"{st.HoldCount}h",
                            stop  = Math.Round(st.HardStop, 6),
                            target = Math.Round(st.Target, 6),
                            trailArmed = st.TrailArmed,
                        });
                    }
                }

                var regimeInfo = ptRouting != null ? new
                {
                    state      = ptRouting.Regime.ToString(),
                    confidence = Math.Round(ptRouting.Confidence, 2),
                    FadeShort  = ptRouting.FadeShortActive,
                    Grid       = ptRouting.GridActive,
                    SwingLong  = ptRouting.SwingLongActive,
                    DipLong    = ptRouting.DipLongActive,
                    FadeLong   = ptRouting.FadeLongActive,
                    RipShort   = ptRouting.RipShortActive,
                } : null;

                var guardInfo = guardSession != null ? new
                {
                    mult     = Math.Round(guardSession.GetMult(DateTime.UtcNow), 2),
                    atrRatio = Math.Round(guardSession.GetAtrRatio(DateTime.UtcNow), 2),
                } : null;

                var rotatorInfo = rotator != null ? new
                {
                    safetyScore = Math.Round(safetyScore, 2),
                    allocation  = rotator.ComputeAllocation(safetyScore) is var (alt, btc, eth) ? new
                    {
                        alts = Math.Round(alt, 2),
                        btc  = Math.Round(btc, 2),
                        eth  = Math.Round(eth, 2)
                    } : null
                } : null;

                double fundingRate = fundingSession?.CurrentRate ?? 0;

                var liveState = new
                {
                    timestamp   = DateTime.UtcNow.ToString("O"),
                    cycle       = 0,
                    positions,
                    regime      = regimeInfo,
                    guard       = guardInfo,
                    rotator     = rotatorInfo,
                    fundingRate = Math.Round(fundingRate, 6),
                    openCount   = positions.Count,
                    nextRefresh = DateTime.UtcNow.AddSeconds(RefreshSeconds).ToString("HH:mm 'UTC'"),
                };

                try
                {
                    File.WriteAllText("live_state.json",
                        JsonSerializer.Serialize(liveState,
                            new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"\n  live_state.json written ({positions.Count} open positions)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n  ⚠ Failed to write live_state.json: {ex.Message}");
                }

                // ── Journal: diff this cycle's open set against the previous one ──
                var currentOpen = new Dictionary<string, (string Strat, string Sym, string Dir, double Entry, double Mark, double Pnl, bool Routed, double GuardMult)>();
                foreach (var p in positions.Select(o => JsonSerializer.SerializeToElement(o)))
                {
                    string strat = p.GetProperty("strat").GetString()!;
                    string sym   = p.GetProperty("sym").GetString()!;
                    currentOpen[$"{strat}:{sym}"] = (strat, sym, p.GetProperty("dir").GetString()!,
                        p.GetProperty("entry").GetDouble(), p.GetProperty("mark").GetDouble(), p.GetProperty("pnl").GetDouble(),
                        p.GetProperty("routed").GetBoolean(), p.GetProperty("guardMult").GetDouble());
                }

                var newEvents  = new List<object>();
                string nowIso  = DateTime.UtcNow.ToString("O");
                string? regime = ptRouting?.Regime.ToString();

                foreach (var kv in currentOpen)
                    if (!prevOpen.ContainsKey(kv.Key))
                        newEvents.Add(new { time = nowIso, evt = "entry", strat = kv.Value.Strat, sym = kv.Value.Sym,
                            dir = kv.Value.Dir, entry = kv.Value.Entry, mark = kv.Value.Mark, pnl = kv.Value.Pnl, regime,
                            routed = kv.Value.Routed, guardMult = kv.Value.GuardMult });

                foreach (var kv in prevOpen)
                    if (!currentOpen.ContainsKey(kv.Key))
                    {
                        var parts = kv.Key.Split(':', 2);
                        newEvents.Add(new { time = nowIso, evt = "exit", strat = parts[0], sym = parts[1],
                            dir = kv.Value.Dir, entry = kv.Value.Entry, mark = kv.Value.LastMark, pnl = kv.Value.LastPnl, regime,
                            routed = kv.Value.Routed, guardMult = kv.Value.GuardMult });
                    }

                // Carry FirstSeen forward for still-open keys; cache last mark/pnl for future exits.
                var nextOpen = new Dictionary<string, (string Dir, double Entry, DateTime FirstSeen, double LastMark, double LastPnl, bool Routed, double GuardMult)>();
                foreach (var kv in currentOpen)
                {
                    DateTime firstSeen = prevOpen.TryGetValue(kv.Key, out var old) ? old.FirstSeen : DateTime.UtcNow;
                    // Routed is taken from the ENTRY, not the exit cycle: whether the router allowed
                    // this position is a fact about the decision that opened it. The router can flip
                    // mid-hold, and re-reading it at exit would relabel history.
                    bool routedAtEntry = prevOpen.TryGetValue(kv.Key, out var prev) ? prev.Routed : kv.Value.Routed;
                    nextOpen[kv.Key] = (kv.Value.Dir, kv.Value.Entry, firstSeen, kv.Value.Mark, kv.Value.Pnl,
                                        routedAtEntry, kv.Value.GuardMult);
                }
                prevOpen = nextOpen;

                if (newEvents.Count > 0)
                {
                    try
                    {
                        foreach (var ev in newEvents) journal.Add(JsonSerializer.SerializeToElement(ev));
                        File.WriteAllText("live_journal.json",
                            JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
                        Console.WriteLine($"  live_journal.json: +{newEvents.Count} event(s)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\n  ⚠ Failed to write live_journal.json: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"\n  Next refresh in {RefreshSeconds / 60}m");
            try { await Task.Delay(RefreshSeconds * 1000, cts.Token); }
            catch (TaskCanceledException) { }
        }

        Console.WriteLine("\n\n  Paper trade stopped.");
    }
}
