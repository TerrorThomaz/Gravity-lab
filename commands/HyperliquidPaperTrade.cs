using System.Linq;
using System.Text.Json;

namespace TradingGA;

/// <summary>
/// Paper trading via Hyperliquid's Python bridge (FastAPI).
/// 
/// Architecture:
///   C# strategy engine => HyperliquidClient (HTTP) => bot/hyperliquid_server.py
///   => hyperliquid-python-sdk => paper.hyperliquid.xyz
///
/// Env vars:
///   HYPERLIQUID_PRIVATE_KEY  - wallet private key (hex, no 0x prefix)
///   HYPERLIQUID_API_URL      - FastAPI bridge URL (default http://localhost:8765)
///   HYPERLIQUID_USD_SIZE     - default position size in USD (default 500)
/// </summary>
static class HyperliquidPaperTrade
{
    // ── Entry ───────────────────────────────────────────────────────────────
    public static async Task Run()
    {
        Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (Hyperliquid) - Ctrl+C to stop ===\n");

        if (!File.Exists(Config.FadeShortGenoFile))
        {
            Console.WriteLine($"No genotype at '{Config.FadeShortGenoFile}'. Run 'dotnet run -- train' first.");
            return;
        }

        // Check bridge health
        using var client = new HyperliquidClient();
        if (!await client.IsReadyAsync())
        {
            Console.WriteLine($"[ERROR] Hyperliquid bridge not reachable at {HyperliquidClient.DefaultBaseUrl}");
            Console.WriteLine("  Start it with: python3 bot/hyperliquid_server.py &");
            return;
        }

        // Load genotypes (same as Bybit papertrade)
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
            Console.WriteLine($"  (no grid genotype - run 'dotnet run -- gridtrain' to include grid)");

        RegimeRouterGenotype? routerGenoPt = null;
        if (File.Exists(Config.RouterGenoFile))
        {
            routerGenoPt = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(
                File.ReadAllText(Config.RouterGenoFile))!.ToGenotype();
            Console.WriteLine($"Router genotype:   (trained)");
        }
        else Console.WriteLine("  (no router genotype - using rule-based routing)");

        DynamicGuardGenotype? guardGenoPt = null;
        if (File.Exists(Config.DynamicGuardGenoFile))
        {
            guardGenoPt = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(
                File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype();
            Console.WriteLine($"Guard genotype:    (trained)");
        }
        else Console.WriteLine("  (no guard genotype - guard disabled)");

        VolatilityWeightedRotatorGenotype? rotatorGenoPt = null;
        if (File.Exists(Config.RotatorGenoFile))
        {
            rotatorGenoPt = JsonSerializer.Deserialize<VolatilityWeightedRotatorGenotypeDto>(
                File.ReadAllText(Config.RotatorGenoFile))!.ToGenotype();
            Console.WriteLine($"Rotator genotype:  (trained)");
        }
        else Console.WriteLine("  (no rotator genotype - rotation disabled)");

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

        // Universe: use BacktestCoins + OOS coins from Hyperliquid universe
        // For now, use BacktestCoins (the same set used by backtests).
        var coins = Config.BacktestCoins.ToList();

        // Hyperliquid lists a much smaller set of perps than Binance — filter out symbols that will
        // never resolve so they don't burn a failed request (and log warning) every cycle for a week.
        try
        {
            var universe = await client.FetchUniverseAsync();
            if (universe.TryGetValue("perpetuals", out var perpsObj) && perpsObj is JsonElement { ValueKind: JsonValueKind.Array } perpsEl)
            {
                var hlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in perpsEl.EnumerateArray())
                    if (p.TryGetProperty("name", out var nameEl))
                        hlNames.Add(nameEl.GetString() ?? "");

                var resolvable = coins.Where(c => hlNames.Contains(HyperliquidClient.NormalizeToHyperliquidCoin(c))).ToList();
                int skipped = coins.Count - resolvable.Count;
                if (skipped > 0)
                    Console.WriteLine($"Hyperliquid universe: {resolvable.Count}/{coins.Count} BacktestCoins resolve ({skipped} not listed on HL, skipped)\n");
                coins = resolvable;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: universe fetch failed, using unfiltered coin list: {ex.Message}\n");
        }

        const int RefreshSeconds = 900;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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

        var prevOpen = new Dictionary<string, (string Dir, double Entry, DateTime FirstSeen, double Mark, double Pnl, double Size, bool Routed, int HoldBars)>();
        foreach (var kv in OpenKeysFromJournal(journal))
            prevOpen[kv.Key] = (kv.Value.Dir, kv.Value.Entry, DateTime.UtcNow, kv.Value.Entry, 0.0, kv.Value.Size, kv.Value.Routed, 0);

        // Edge-proportional sizing: half-Kelly confidence per (strategy, coin).
        var liveConf    = new Dictionary<(string Strat, string Sym), double>();
        var stratWeight = new Dictionary<string, double>();;

        // ── Portfolio drawdown killswitch ──────────────────────────────────────
        // Halted trading when peak-to-trough drawdown exceeds GRAVITY_KILLDD%.
        // Persisted across restarts via a journal halt event. Resume with GRAVITY_RESUME=1.
        const double StartingCapital = 100_000.0;
        double startingCapital = double.TryParse(Environment.GetEnvironmentVariable("GRAVITY_STARTING_CAPITAL"), out var sc) ? sc : StartingCapital;
        double killDdPct       = double.TryParse(Environment.GetEnvironmentVariable("GRAVITY_KILLDD"), out var kd) ? kd : 7.0;
        bool   tradingHalted   = false;
        bool   resumeRequested = Environment.GetEnvironmentVariable("GRAVITY_RESUME") == "1";

        // Check for a prior halt event in the journal.
        foreach (var ev in journal.AsEnumerable().Reverse())
        {
            if (!ev.TryGetProperty("evt", out var evtEl)) continue;
            var evt = evtEl.GetString();
            if (evt == "halt" && !resumeRequested)
            {
                tradingHalted = true;
                Console.WriteLine($"  ⚠ Trading HALTED by portfolio DD killswitch (DD>{killDdPct:P0}% threshold).");
                break;
            }
        }

        // Running equity tracker: peak equity + current equity (realized + unrealized).
        double peakEquity = startingCapital;
        double currentEquity = startingCapital;
        string? lastHaltReason = null;
        int cycleNo = 0;
        double ddPct = 0.0;

        while (!cts.Token.IsCancellationRequested)
        {
            try { Console.Clear(); } catch { }
            Console.WriteLine($"=== Gravity-gen2 | Hyperliquid Paper Trade  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]  Ctrl+C to stop ===\n");

            // ── Portfolio drawdown killswitch check ────────────────────────────
            if (!tradingHalted || resumeRequested)
            {
                // Compute current equity: starting capital + unrealized PnL from open positions.
                // Realized PnL is tracked via exit events in the journal.
                double realizedPnl = 0.0;
                foreach (var ev in journal.AsEnumerable().Reverse())
                {
                    if (!ev.TryGetProperty("evt", out var evtEl)) continue;
                    var evt = evtEl.GetString();
                    if (evt != "exit") continue;
                    if (!ev.TryGetProperty("pnl", out var pnlEl)) continue;
                    if (pnlEl.TryGetDouble(out var pnl)) realizedPnl += pnl * startingCapital / 100.0;
                }
                double unrealizedPnl = 0.0;
                foreach (var kv in prevOpen)
                {
                    double posPnlPct = kv.Value.Pnl;
                    double posSizeUsd = kv.Value.Size > 0 ? kv.Value.Size : startingCapital * 0.05;
                    unrealizedPnl += posPnlPct / 100.0 * posSizeUsd;
                }
                currentEquity = startingCapital + realizedPnl + unrealizedPnl;
                peakEquity = Math.Max(peakEquity, currentEquity);
                ddPct = peakEquity > 0 ? (peakEquity - currentEquity) / peakEquity * 100.0 : 0.0;

                if (ddPct >= killDdPct && !resumeRequested)
                {
                    tradingHalted = true;
                    lastHaltReason = $"DD={ddPct:P1}% ≥ {killDdPct:P0}%";
                    // Write halt event to journal so restarts respect it.
                    var haltEv = new Dictionary<string, object>
                    {
                        { "time", DateTime.UtcNow.ToString("O") },
                        { "evt", "halt" },
                        { "reason", lastHaltReason },
                        { "ddPct", ddPct },
                    };
                    journal.Add(JsonSerializer.SerializeToElement(haltEv));
                    try { File.WriteAllText("live_journal.json", JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true })); }
                    catch { }
                    Console.WriteLine($"\n  ⚠ TRADING HALTED: {lastHaltReason}. Set GRAVITY_RESUME=1 to resume.\n");
                }
                else if (resumeRequested)
                {
                    tradingHalted = false;
                    lastHaltReason = null;
                    Console.WriteLine("\n  [RESUME] Trading resumed (GRAVITY_RESUME=1).\n");
                }
            }

            if (tradingHalted)
            {
                // Skip all strategy evaluation — just write a halted state and sleep.
                Console.WriteLine($"  Cycle #{cycleNo}: HALTED ({lastHaltReason}). Next refresh in {RefreshSeconds / 60}m\n");
                try { await Task.Delay(RefreshSeconds * 1000, cts.Token); }
                catch (TaskCanceledException) { }
                continue;
            }

            cycleNo++;

            // Fetch 15m candles for all coins (parallel HTTP calls to FastAPI bridge)
            var sem = new SemaphoreSlim(4);
            var coinTasks = coins.Select(async sym =>
            {
                await sem.WaitAsync(cts.Token);
                try
                {
                    var h1Candles = await client.FetchH1CandlesAsync(sym, batches: HyperliquidClient.LiveBatches);
                    if (h1Candles.Count < 200) return (sym, (Candle[]?)null, (Candle[]?)null, false, 0.0, 0.0);
                    
                    // Aggregate to 1h (4 x 15m)
                    var m15 = h1Candles.ToArray();
                    var h1 = FadeShortSimulator.AggregateCandles(m15, 4);
                    
                    // Screen on median volume
                    var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                    double volM = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                    bool passes = volM >= Config.MinMedianVolUsdM;
                    
                    // ATR% screen
                    var atrResult = CandleFetcher.CheckSwingCriteria(m15);
                    
                    return (sym: sym, h1: (Candle[]?)h1, m15: (Candle[]?)m15, passes: passes, atrPct: atrResult.AtrPct, volM: volM);
                }
                finally { sem.Release(); }
            }).ToList();
            
            var fetchResults = await Task.WhenAll(coinTasks);
            var coinData = fetchResults.Where(x => x.Item2 is not null).ToList();

            // A coin whose candles failed to arrive is indistinguishable from a coin with no signal
            // once it has been filtered out — and that is exactly how a 56% fetch-failure rate hid
            // for a week, presenting as "no trades" rather than "no data". Never let the drop count
            // be invisible: if most of the universe is missing, the cycle's emptiness means nothing.
            int dropped = fetchResults.Length - coinData.Count;
            if (dropped > 0)
            {
                double lossPct = 100.0 * dropped / Math.Max(1, fetchResults.Length);
                Console.WriteLine($"  [DATA] {coinData.Count}/{fetchResults.Length} coins usable — " +
                                  $"{dropped} dropped ({lossPct:F0}% no/short candles)"
                                  + (lossPct >= 25 ? "   *** DEGRADED: signals this cycle are not trustworthy ***" : ""));
            }
            else Console.WriteLine($"  [DATA] {coinData.Count}/{fetchResults.Length} coins usable");

            // Funding rate session
            FundingRateSession? fundingSession = null;
            try
            {
                var btcFunding = await client.FetchFundingRateAsync("BTC");
                if (btcFunding.HasValue && btcFunding.Value != 0)
                {
                    fundingSession = new FundingRateSession(Array.Empty<FundingBar>());
                    Console.WriteLine($"  Funding: {btcFunding.Value:+0.0000%;-0.0000%} (current rate)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Funding fetch failed: {ex.Message}");
            }

            // Dynamic guard session
            DynamicGuardSession? guardSession = null;
            var btcData = coinData.FirstOrDefault(x => x.Item1 == "BTCUSDT");
            if (guardGenoPt != null && btcData.Item2 is { Length: >= 50 })
            {
                guardSession = new DynamicGuardSession(btcData.Item2, guardGenoPt);
                double guardMult = guardSession.GetMult(DateTime.UtcNow);
                string guardTag = guardMult < 1.0 ? $"STRESS x{guardMult:F2}" : "normal";
                Console.WriteLine($"  Guard: {guardTag}");
            }

            // Regime routing
            StrategyActivation? ptRouting = null;
            try
            {
                var btcPt = coinData.FirstOrDefault(x => x.Item1 == "BTCUSDT");
                var ethPt = coinData.FirstOrDefault(x => x.Item1 == "ETHUSDT");
                if (btcPt.Item2 is { Length: > 220 })
                {
                    Candle[]? ethH1Pt = ethPt.Item2 is { Length: > 220 } ? ethPt.Item2 : null;
                    ptRouting = routerGenoPt != null
                        ? RegimeRouter.Route(btcPt.Item2, routerGenoPt, ethH1Pt)
                        : RegimeRouter.Route(btcPt.Item2, ethH1Pt);
                    string routerTag = routerGenoPt != null ? "(trained)" : "(rule-based)";
                    var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcPt.Item2);
                    int bearDur = btcSeries[^1].Regime == MarketRegime.Bear ? btcSeries[^1].Duration : 0;
                    int bullDur = btcSeries[^1].Regime == MarketRegime.Bull ? btcSeries[^1].Duration : 0;
                    string durTag = bearDur > 0 ? $"  bear={bearDur}h" : bullDur > 0 ? $"  bull={bullDur}h" : "";
                    Console.WriteLine($"  Regime {routerTag}: {ptRouting.Regime}  conf={ptRouting.Confidence:P0}  ->  {RegimeRouter.Describe(ptRouting)}{durTag}\n");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Router failed: {ex.Message}");
            }

            // Volatility-weighted rotator
            VolatilityWeightedRotator? rotator = null;
            double safetyScore = 0.0;
            if (rotatorGenoPt != null && guardSession != null && ptRouting != null)
            {
                rotator = new VolatilityWeightedRotator(rotatorGenoPt);
                double guardMult = guardSession.GetMult(DateTime.UtcNow);
                double atrRatio = guardSession.GetAtrRatio(DateTime.UtcNow);
                safetyScore = rotator.ComputeSafetyScore(guardMult, atrRatio, ptRouting.Regime);
                var (altShare, btcShare, ethShare) = rotator.ComputeAllocation(safetyScore);
                Console.WriteLine($"  Rotator: safety={safetyScore:F2} -> alts={altShare:P0} BTC={btcShare:P0} ETH={ethShare:P0}");
            }

            // ── Router gates (fallback true when router unavailable, matching PapertradeCommands) ──
            bool fsRoutedOn   = ptRouting == null || ptRouting.FadeShortActive;
            bool gridRoutedOn = ptRouting == null || ptRouting.GridActive;
            bool slRoutedOn   = ptRouting == null || ptRouting.SwingLongActive;
            bool dlRoutedOn   = ptRouting == null || ptRouting.DipLongActive;
            bool flRoutedOn   = ptRouting == null || ptRouting.FadeLongActive;
            bool rsRoutedOn   = ptRouting == null || ptRouting.RipShortActive;

            // ── Strategy evaluation + order execution ────────────────────────────
            // Every strategy is evaluated every cycle regardless of router gating (shadow mode,
            // matching PapertradeCommands) so `routed:false` positions stay visible — but only
            // `routed==true` signals ever reach PlaceOrderAsync. Concurrent-open caps mirror
            // PortfolioReplay.DefaultCaps to avoid unbounded correlated exposure on a live account.
            double sizeUsd = double.TryParse(Environment.GetEnvironmentVariable("HYPERLIQUID_USD_SIZE"), out var envSz) ? envSz : 1000.0;
            var positions = new List<object>();
            var currentOpen = new Dictionary<string, (string Strat, string Sym, string Dir, double Entry, double Mark, double Pnl, bool Routed, double GuardMult, double Size, int HoldBars)>();

            // Seed per-strategy open counts from already-open routed positions (carried from prior cycles).
            var openCounts = new Dictionary<string, int>();
            foreach (var kv in prevOpen)
            {
                if (!kv.Value.Routed) continue;
                var capKey0 = kv.Key.Split(':', 2)[0];
                openCounts[capKey0] = openCounts.GetValueOrDefault(capKey0) + 1;
            }

            // Hyperliquid base tier: 0.015% maker / 0.045% taker. Worst case (both legs taker) round
            // trip = 0.09%; our own limit orders (isLimit: true, GTC) never pay market-order slippage
            // the way the Bybit-calibrated backtest cost model (TradeCosts.FeeRoundTripPct=0.11) does
            // — there's no price degradation on a limit fill, only fill-or-no-fill risk. Charging the
            // full round trip up front (rather than half now / half at exit) is a deliberately
            // conservative simplification for a single running pnl% field.
            const double HlFeeRoundTripPct = 0.09;

            async Task ProcessSignalAsync(string strat, string capKey, int cap, string sym, string dir, string side,
                bool routed, double entry, double px, int holdBars, double stop, double target, bool trailArmed, double? sizeBasePct)
            {
                double pnl = (dir == "Long" ? (px - entry) / entry * 100.0 : (entry - px) / entry * 100.0) - HlFeeRoundTripPct;
                double guardMult = guardSession?.GetMult(DateTime.UtcNow) ?? 1.0;
                double effectiveSize = sizeUsd * guardMult;
                string key = $"{strat}:{sym}";
                currentOpen[key] = (strat, sym, dir, entry, px, pnl, routed, guardMult, effectiveSize, holdBars);

                // Journal "entry"/"exit" events (for ALL signals, routed or shadow) are written once,
                // generically, by the diff-against-prevOpen pass below — not here, to avoid double-writes.
                bool underCap = openCounts.GetValueOrDefault(capKey) < cap;
                bool isNew = !prevOpen.ContainsKey(key);
                if (routed && underCap && isNew)
                {
                    try
                    {
                        var orderResp = await client.PlaceOrderAsync(sym, side, effectiveSize, entry, isLimit: true, reduceOnly: false, tif: "Gtc");
                        Console.WriteLine($"  [ENTRY] {strat,-10} {sym,-8} {dir.ToUpperInvariant()} b{holdBars} @ {entry:F4} pnl={pnl:+0.00}%  order={JsonSerializer.Serialize(orderResp)}");
                        openCounts[capKey] = openCounts.GetValueOrDefault(capKey) + 1;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [ENTRY FAILED] {strat} {sym,-8}: {ex.Message}");
                    }
                }

                positions.Add(new
                {
                    sym, strat, dir,
                    routed, guardMult,
                    sizeBase = sizeBasePct,
                    entry = Math.Round(entry, 6),
                    mark  = Math.Round(px, 6),
                    pnl   = Math.Round(pnl, 2),
                    age   = $"{holdBars}h", ageBars = holdBars,
                    stop  = Math.Round(stop, 6),
                    target = Math.Round(target, 6),
                    trailArmed,
                });
            }

            foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
            {
                if (!passes) continue;

                // ── FadeShort ──
                var coinCl = CoinClusterHelper.ClassifyByName(sym);
                var gForCoin = clusterGenosPt.TryGetValue(coinCl, out var g) ? g : gUniversalPt;
                var st = FadeShortSimulator.GetFadeShortTradeState(gForCoin, h1, m15);
                if (st.InTrade)
                    await ProcessSignalAsync("FadeShort", "fade_short", 10, sym, "Short", "A", fsRoutedOn,
                        st.Entry, h1[^1].Close, st.HoldCount, Math.Min(st.HardStop, st.MaeStop), st.Target, st.TrailArmed, gForCoin.PositionSizePct);

                // ── Grid ──
                if (gridGPt != null)
                {
                    var coinGridG = StrategyPipeline.SelectVariant(gridVariantsPt, m15) ?? gridGPt;
                    var gst = GridSimulator.GetGridTradeState(coinGridG, h1);
                    if (gst.Active)
                        await ProcessSignalAsync("Grid", "grid", 12, sym, "Long", "B", gridRoutedOn,
                            gst.Anchor, h1[^1].Close, gst.HoldCount, gst.HardStop, 0.0, false, null);
                }

                // ── SwingLong ──
                if (slGenoPt != null)
                {
                    var coinSlG = StrategyPipeline.SelectVariant(slVariantsPt, m15) ?? slGenoPt;
                    var slSt = SwingLongSimulator.GetSwingLongTradeState(coinSlG, h1, m15, fundingSession);
                    if (slSt.InTrade)
                        await ProcessSignalAsync("SwingLong", "swing_long", 8, sym, "Long", "B", slRoutedOn,
                            slSt.Entry, h1[^1].Close, slSt.HoldCount, slSt.HardStop, slSt.Target, slSt.TrailArmed, coinSlG.PositionSizePct);
                }

                // ── DipLong ──
                if (dlGenoPt != null)
                {
                    var coinDlG = StrategyPipeline.SelectVariant(dlVariantsPt, m15) ?? dlGenoPt;
                    var dlSt = DipLongSimulator.GetDipLongTradeState(coinDlG, h1, m15, fundingSession);
                    if (dlSt.InTrade)
                        await ProcessSignalAsync("DipLong", "diplong", 8, sym, "Long", "B", dlRoutedOn,
                            dlSt.Entry, h1[^1].Close, dlSt.HoldCount, dlSt.HardStop, dlSt.Target, dlSt.TrailArmed, coinDlG.PositionSizePct);
                }

                // ── FadeLong ──
                if (flGenoPt != null)
                {
                    var coinFlG = StrategyPipeline.SelectVariant(flVariantsPt, m15) ?? flGenoPt;
                    var flSt = FadeLongSimulator.GetFadeLongTradeState(coinFlG, h1, m15, fundingSession);
                    if (flSt.InTrade)
                        await ProcessSignalAsync("FadeLong", "fadelong", 8, sym, "Long", "B", flRoutedOn,
                            flSt.Entry, h1[^1].Close, flSt.HoldCount, Math.Min(flSt.HardStop, flSt.MaeStop), flSt.Target, flSt.TrailArmed, coinFlG.PositionSizePct);
                }

                // ── RipShort ──
                if (rsGenoPt != null)
                {
                    var coinRsG = StrategyPipeline.SelectVariant(rsVariantsPt, m15) ?? rsGenoPt;
                    var rsSt = RipShortSimulator.GetRipShortTradeState(coinRsG, h1, m15);
                    if (rsSt.InTrade)
                        await ProcessSignalAsync("RipShort", "ripshort", 8, sym, "Short", "A", rsRoutedOn,
                            rsSt.Entry, h1[^1].Close, rsSt.HoldCount, rsSt.HardStop, rsSt.Target, rsSt.TrailArmed, coinRsG.PositionSizePct);
                }
            }

            int openCount = positions.Count;

            // Diff entries/exits against previous cycle
            var nextOpen = new Dictionary<string, (string Dir, double Entry, DateTime FirstSeen, double Mark, double Pnl, double Size, bool Routed, int HoldBars)>();
            var newEvents = new List<object>();
            string nowIso = DateTime.UtcNow.ToString("O");

            foreach (var kv in currentOpen)
            {
                var parts = kv.Key.Split(':', 2);
                string strat = parts[0];
                string sym = parts[1];
                string dir = kv.Value.Dir;
                double entry = kv.Value.Entry;
                double mark = kv.Value.Mark;
                double pnl = kv.Value.Pnl;

                DateTime firstSeen;
                if (prevOpen.TryGetValue(kv.Key, out var old))
                {
                    // Still open — update mark/pnl, keep original firstSeen
                    firstSeen = old.FirstSeen;
                    nextOpen[kv.Key] = (dir, entry, firstSeen, mark, pnl, kv.Value.Size, kv.Value.Routed, kv.Value.HoldBars);
                }
                else
                {
                    // New entry
                    firstSeen = DateTime.UtcNow;
                    nextOpen[kv.Key] = (dir, entry, firstSeen, mark, pnl, kv.Value.Size, kv.Value.Routed, kv.Value.HoldBars);

                    newEvents.Add(new { time = nowIso, evt = "entry", strat, sym, dir, entry, mark, pnl,
                        routed = kv.Value.Routed, guardMult = kv.Value.GuardMult, ageBars = kv.Value.HoldBars, size = kv.Value.Size });
                }
            }

            // Detect exits (positions that were open but no longer are)
            foreach (var kv in prevOpen)
            {
                if (!currentOpen.ContainsKey(kv.Key))
                {
                    var parts = kv.Key.Split(':', 2);
                    newEvents.Add(new { time = nowIso, evt = "exit", strat = parts[0], sym = parts[1],
                        dir = kv.Value.Dir, entry = kv.Value.Entry, mark = kv.Value.Mark, pnl = kv.Value.Pnl,
                        routed = kv.Value.Routed, guardMult = 1.0, ageBars = kv.Value.HoldBars, size = 0 });
                }
            }

            // Write events to journal
            if (newEvents.Count > 0)
            {
                try
                {
                    foreach (var ev in newEvents) journal.Add(JsonSerializer.SerializeToElement(ev));
                    File.WriteAllText("live_journal.json", JsonSerializer.Serialize(journal, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine($"  Journal: +{newEvents.Count} event(s)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: Failed to write journal: {ex.Message}");
                }
            }

            prevOpen = nextOpen;

            // Write live_state.json
            var positionsOut = positions.Select(o =>
            {
                var el  = JsonSerializer.SerializeToElement(o);
                var dict = el.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
                return dict;
            }).ToList();

            var regimeInfo = ptRouting != null ? new
            {
                state = ptRouting.Regime.ToString(),
                confidence = ptRouting.Confidence,
                FadeShort = ptRouting.FadeShortActive,
                Grid = ptRouting.GridActive,
                SwingLong = ptRouting.SwingLongActive,
                DipLong = ptRouting.DipLongActive,
                FadeLong = ptRouting.FadeLongActive,
                RipShort = ptRouting.RipShortActive,
                sizeMult = 0.03,
            } : new { state = "unknown", confidence = 0.0, FadeShort = false, Grid = false, SwingLong = false, DipLong = false, FadeLong = false, RipShort = false, sizeMult = 0.0 };

            var guardInfo = guardSession != null ? new
            {
                mult = Math.Round(guardSession.GetMult(DateTime.UtcNow), 4),
                atrRatio = Math.Round(guardSession.GetAtrRatio(DateTime.UtcNow), 4),
            } : new { mult = 1.0, atrRatio = 0.0 };

            var rotatorInfo = rotator != null ? new
            {
                safetyScore = Math.Round(safetyScore, 4),
                allocation = new { alts = 0, btc = 0, eth = 0 },
            } : new { safetyScore = 0.0, allocation = new { alts = 0, btc = 0, eth = 0 } };

            double fundingRate = fundingSession?.CurrentRate ?? 0;

            var liveState = new
            {
                timestamp   = DateTime.UtcNow.ToString("O"),
                cycle       = cycleNo,
                positions   = positionsOut,
                regime      = regimeInfo,
                guard       = guardInfo,
                rotator     = rotatorInfo,
                fundingRate = Math.Round(fundingRate, 6),
                openCount   = openCount,
                allocatedPct = 0.0,
                // Portfolio DD killswitch state
                ddPct       = Math.Round(ddPct, 2),
                halted      = tradingHalted,
                haltReason  = lastHaltReason,
                equity      = Math.Round(currentEquity, 2),
                peakEquity  = Math.Round(peakEquity, 2),
                nextRefresh = DateTime.UtcNow.AddSeconds(RefreshSeconds).ToString("HH:mm 'UTC'"),
            };

            try
            {
                File.WriteAllText("live_state.json", JsonSerializer.Serialize(liveState, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"\n  live_state.json written ({openCount} open positions)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n  Warning: Failed to write live_state.json: {ex.Message}");
            }
            
            await Task.Delay(RefreshSeconds * 1000, cts.Token);
        }

        Console.WriteLine("\n\n  Paper trade stopped.");
    }

    // Extract open position keys from the journal for restart seeding.
    // Walks backwards through journal entries looking for the latest "entry" event per (strat,sym),
    // skipping any subsequent "exit" events. Returns a map of key -> position data.
    static Dictionary<string, (string Dir, double Entry, DateTime FirstSeen, double Mark, double Pnl, double Size, bool Routed)> OpenKeysFromJournal(List<JsonElement> journal)
    {
        var result = new Dictionary<string, (string Dir, double Entry, DateTime FirstSeen, double Mark, double Pnl, double Size, bool Routed)>(StringComparer.OrdinalIgnoreCase);
        var seenExit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ev in journal.AsEnumerable().Reverse())
        {
            if (!ev.TryGetProperty("evt", out var evtEl)) continue;
            var evt = evtEl.GetString();
            // Positions are keyed strat:sym everywhere else, so the exit set must be too. Keyed on
            // sym alone, a single FadeLong exit on ADAUSDT also suppressed DipLong's and RipShort's
            // open ADAUSDT positions from being restored on restart — they would then be re-emitted
            // as fresh "entry" events, double-counting a position that never closed.
            static string KeyOf(JsonElement e) =>
                $"{(e.TryGetProperty("strat", out var s) ? s.GetString() : "?")}:" +
                $"{(e.TryGetProperty("sym", out var y) ? y.GetString() : "?")}";

            if (evt == "exit")
            {
                seenExit.Add(KeyOf(ev));
            }
            else if (evt == "entry" && !seenExit.Contains(KeyOf(ev)))
            {
                if (ev.TryGetProperty("sym", out var symEl2)
                    && ev.TryGetProperty("dir", out var dirEl2)
                    && ev.TryGetProperty("entry", out var entryEl2)
                    && ev.TryGetProperty("mark", out var markEl2)
                    && ev.TryGetProperty("pnl", out var pnlEl2)
                    && ev.TryGetProperty("size", out var sizeEl2))
                {
                    string key = $"{ev.GetProperty("strat").GetString()}:{symEl2.GetString()}";
                    if (!result.ContainsKey(key))
                    {
                        bool routed = !ev.TryGetProperty("routed", out var routedEl) || routedEl.ValueKind != JsonValueKind.False;
                        result[key] = (dirEl2.GetString(), entryEl2.GetDouble(), DateTime.UtcNow, markEl2.GetDouble(), pnlEl2.GetDouble(), sizeEl2.GetDouble(), routed);
                    }
                }
            }
        }
        return result;
    }
}
