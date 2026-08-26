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
    // ── Per-trade venue data log (append-only JSONL) ─────────────────────────────────────────
    // Everything the exchange returns on every order, verbatim, one JSON object per line.
    // `orderResp` used to be console-logged and dropped, so nothing could later answer "did this
    // fill, at what price, was it rejected". This is the raw material for fill-rate measurement,
    // slippage-vs-model comparison and post-hoc performance attribution.
    //
    // JSONL (not JSON) so an interrupted write costs one line, not the whole file — the same
    // failure that left live_journal.json a 7-day fossil.
    private const string TradeDataLog = "live_tradedata.jsonl";

    static void LogTradeData(string evt, string strat, string sym, double entry, double mark,
                             double pnlPct,
                             (double Size, double Entry, double UnrealizedPnl) exch,
                             object venueResponse)
    {
        try
        {
            var rec = new
            {
                ts    = DateTime.UtcNow.ToString("O"),
                evt, strat, sym,
                hlCoin = HyperliquidClient.NormalizeToHyperliquidCoin(sym),
                simEntry = entry, simMark = mark, simPnlPct = pnlPct,
                // What the EXCHANGE says, alongside what the simulator believed — divergence
                // between these two columns is the thing worth grepping for later.
                exchSize = exch.Size, exchEntry = exch.Entry, exchUnrealizedPnl = exch.UnrealizedPnl,
                venue = venueResponse,
            };
            File.AppendAllText(TradeDataLog,
                JsonSerializer.Serialize(rec) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: trade-data log write failed: {ex.Message}");
        }
    }

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

        // ── Covariance-weighted position sizing ──────────────────────────────────────────────
        // Live sized every strategy identically while every backtest applied covariance weights —
        // a backtest/live mismatch that made published numbers describe a system that wasn't
        // running. Measured on the Kelly sim, covsize vs flat: return/DD 85.7 vs 24.9 on the val
        // window and 43.1 vs 37.4 on the test slice, with lower drawdown in BOTH (1.9% vs 4.3%,
        // 1.1% vs 1.3%). The return edge is regime-specific; the drawdown edge is not.
        //
        // Weights come from combinedbacktest (live has no trade history to compute a covariance
        // from) and are stable across windows, which is what makes an offline export defensible.
        //
        // NAME TRAP: the backtest labels FadeShort as "swing" while the live cap key is
        // "fade_short". An unresolved name silently returns 1.0 and no-ops the entire mechanism —
        // so every lookup is resolved once, up front, and printed.
        // REGIME-CONDITIONAL: the file is {regime -> {strategy -> weight}} with a "global" bucket.
        // Correlations are regime-dependent, so one full-history covariance averages over
        // structurally different states; the router already reports the current regime every cycle,
        // so switching buckets on regime change costs nothing here.
        //
        // The buckets are still computed OFFLINE by combinedbacktest. Live cannot estimate its own
        // covariance: a 5x5 needs far more observations than a live regime episode produces, and at
        // that sample size the estimation error would swamp the regime signal. What changes on a
        // regime flip is WHICH precomputed bucket applies, not a fresh estimate from live trades.
        var sizeWeights = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        {
            const string wf = "genotypes/strategy_size_weights.json";
            var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fade_short"] = "swing",      // backtest calls FadeShort "swing"
                ["swing_long"] = "swing_long",
                ["diplong"]    = "diplong",
                ["ripshort"]   = "ripshort",
                ["grid"]       = "grid",
                ["fadelong"]   = "fadelong",
            };
            if (File.Exists(wf))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(wf));
                    // Accept BOTH shapes: nested {regime:{strat:w}} and the older flat {strat:w},
                    // which is read as global-only. The flat file is what is deployed right now, so
                    // a version skew between this binary and the genotypes dir must not crash live.
                    var buckets = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
                    bool nested = doc.RootElement.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object);
                    if (nested)
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Object)
                                buckets[p.Name] = p.Value.EnumerateObject()
                                    .ToDictionary(x => x.Name, x => x.Value.GetDouble(), StringComparer.OrdinalIgnoreCase);
                    else
                        buckets["global"] = doc.RootElement.EnumerateObject()
                            .ToDictionary(x => x.Name, x => x.Value.GetDouble(), StringComparer.OrdinalIgnoreCase);

                    Console.WriteLine($"Sizing weights (covariance + correlation haircut, {(nested ? $"{buckets.Count} regime bucket(s)" : "FLAT FILE — global only")}):");
                    foreach (var (bucket, raw) in buckets.OrderBy(b => b.Key == "global" ? "" : b.Key))
                    {
                        var resolved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        var parts = new List<string>();
                        foreach (var (capKey, btKey) in alias)
                        {
                            if (!raw.TryGetValue(btKey, out double w)) continue;
                            // Clamp: a very small weight can fall under the venue's minimum order
                            // size, where the order is REJECTED rather than merely small. Grid sits
                            // at ~0.02, i.e. ~$21 on a $1000 base — above HL's ~$10 floor, but not
                            // by much.
                            double clamped = Math.Clamp(w, 0.05, 3.0);
                            resolved[capKey] = clamped;
                            parts.Add($"{capKey}={clamped:F2}" + (Math.Abs(clamped - w) > 1e-9 ? "*" : ""));
                        }
                        sizeWeights[bucket] = resolved;
                        Console.WriteLine($"  {bucket,-9} {string.Join("  ", parts)}");
                    }
                    var missing = alias.Keys.Where(k => !sizeWeights.GetValueOrDefault("global",
                                      new Dictionary<string, double>()).ContainsKey(k)).ToList();
                    if (missing.Count > 0)
                        Console.WriteLine($"  not in global bucket (weight 1.00, flat): {string.Join(", ", missing)}");
                }
                catch (Exception ex) { Console.WriteLine($"  Sizing weights load FAILED: {ex.Message} — flat sizing"); }
            }
            else Console.WriteLine($"  (no {wf} — flat sizing)");
            Console.WriteLine();
        }

        // Regime bucket first, then the global bucket, then flat. A regime with no bucket (HighVol —
        // 3 trades in the whole training book) degrades to the global weight, never to noise.
        double ResolveSizeWeight(string capKey, string? regime)
        {
            if (regime != null && sizeWeights.TryGetValue(regime, out var m) && m.TryGetValue(capKey, out var rw))
                return rw;
            return sizeWeights.TryGetValue("global", out var g) && g.TryGetValue(capKey, out var gw) ? gw : 1.0;
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

                    // ── FLATTEN ON HALT ──────────────────────────────────────────────────────
                    // The killswitch previously only blocked NEW entries. With no exit path in the
                    // loop, a drawdown halt left every open position running — the exact scenario
                    // the killswitch exists to prevent. Close everything the EXCHANGE says we hold,
                    // reduceOnly, market (Ioc): a halt is not the moment to wait for a limit fill.
                    try
                    {
                        var toFlatten = await client.FetchOpenPositionsAsync();
                        Console.WriteLine($"  [HALT] flattening {toFlatten.Count} exchange position(s)...");
                        foreach (var (coin, pos) in toFlatten)
                        {
                            string side = pos.Size > 0 ? "sell" : "buy";
                            try
                            {
                                var resp = await client.PlaceOrderAsync(coin, side, Math.Abs(pos.Size), pos.Entry,
                                    isLimit: false, reduceOnly: true, tif: "Ioc");
                                Console.WriteLine($"  [HALT-CLOSE] {coin,-8} {side} sz={Math.Abs(pos.Size)}  resp={JsonSerializer.Serialize(resp)}");
                                LogTradeData("halt_close", "killswitch", coin, pos.Entry, pos.Entry, 0.0, pos, resp);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"  [HALT-CLOSE FAILED] {coin}: {ex.Message} — STILL OPEN");
                                LogTradeData("halt_close_failed", "killswitch", coin, pos.Entry, pos.Entry, 0.0, pos,
                                             new Dictionary<string, object> { ["error"] = ex.Message });
                            }
                        }
                        // Cancel resting orders too — otherwise a GTC entry fills after the halt.
                        try { await client.CancelAllOrdersAsync(); Console.WriteLine("  [HALT] resting orders cancelled"); }
                        catch (Exception ex) { Console.WriteLine($"  [HALT] cancel-all failed: {ex.Message}"); }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [HALT] could not fetch positions to flatten: {ex.Message} — POSITIONS MAY REMAIN OPEN");
                    }
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
                    Console.WriteLine($"  Regime {routerTag}: {ptRouting.Regime}  conf={ptRouting.Confidence:P0}  ->  {RegimeRouter.Describe(ptRouting)}{durTag}");
                    // Which sizing bucket this cycle resolves to. Printed every cycle because a
                    // silent fall-through to "global" is indistinguishable from a working regime
                    // switch in the output, and that is exactly how the FadeShort/"swing" name trap
                    // no-opped the whole mechanism the first time.
                    string szBucket = sizeWeights.ContainsKey(ptRouting.Regime.ToString()) ? ptRouting.Regime.ToString() : "global";
                    Console.WriteLine($"  Sizing bucket: {szBucket}"
                        + (szBucket == "global" && sizeWeights.Count > 1 ? $"  (no '{ptRouting.Regime}' bucket — fallback)" : "") + "\n");
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
            // ── EXCHANGE IS AUTHORITATIVE ────────────────────────────────────────────────────
            // The loop's own view of "what I hold" was derived purely from simulator state and was
            // never checked against the venue. A missed fill, a partial fill, or a liquidation
            // would diverge silently and forever. Fetch the truth every cycle and reconcile.
            Dictionary<string, (double Size, double Entry, double UnrealizedPnl)> exchangePos = new();
            try { exchangePos = await client.FetchOpenPositionsAsync(); }
            catch (Exception ex) { Console.WriteLine($"  [RECON] position fetch FAILED: {ex.Message} — treating as unknown, no exits sent"); exchangePos = null!; }

            if (exchangePos != null)
                Console.WriteLine($"  [RECON] exchange reports {exchangePos.Count} open position(s)");

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

            // barNotionalUsd: the coin's median h1 traded notional, for the liquidity gate. Defaults
            // to 0 (gate inactive) so existing call sites are unaffected until they pass it.
            async Task ProcessSignalAsync(string strat, string capKey, int cap, string sym, string dir, string side,
                bool routed, double entry, double px, int holdBars, double stop, double target, bool trailArmed,
                double? sizeBasePct, double barNotionalUsd = 0.0)
            {
                double pnl = (dir == "Long" ? (px - entry) / entry * 100.0 : (entry - px) / entry * 100.0) - HlFeeRoundTripPct;
                double guardMult = guardSession?.GetMult(DateTime.UtcNow) ?? 1.0;
                double sizeWeight = ResolveSizeWeight(capKey, ptRouting?.Regime.ToString());
                double effectiveSize = sizeUsd * guardMult * sizeWeight;
                string key = $"{strat}:{sym}";
                currentOpen[key] = (strat, sym, dir, entry, px, pnl, routed, guardMult, effectiveSize, holdBars);

                // Journal "entry"/"exit" events (for ALL signals, routed or shadow) are written once,
                // generically, by the diff-against-prevOpen pass below — not here, to avoid double-writes.
                bool underCap = openCounts.GetValueOrDefault(capKey) < cap;
                bool isNew = !prevOpen.ContainsKey(key);
                // ── PRE-TRADE LIQUIDITY GATE ─────────────────────────────────────────────────
                // LiquidityModel is the only size-aware cost in the repo and was wired NOWHERE —
                // "without it, larger positions execute for free". Above 25% of a bar's traded
                // notional a fill is not realistic: you are the market. At $1k notional this never
                // binds; it exists so that when sizing scales toward the Kelly figures (0.17-0.94
                // by volatility band) an unfillable order is refused rather than assumed filled.
                bool liquidityOk = true;
                if (routed && isNew && barNotionalUsd > 0)
                {
                    if (!LiquidityModel.IsFillRealistic(effectiveSize, barNotionalUsd))
                    {
                        liquidityOk = false;
                        double maxOk = LiquidityModel.MaxRealisticOrder(barNotionalUsd);
                        Console.WriteLine($"  [LIQ-SKIP] {strat,-10} {sym,-8} ${effectiveSize:F0} is "
                                        + $"{effectiveSize / barNotionalUsd:P0} of bar notional "
                                        + $"(${barNotionalUsd:F0}); max realistic ${maxOk:F0}");
                        LogTradeData("liq_skip", strat, sym, entry, px, pnl, default,
                                     new Dictionary<string, object> {
                                         ["orderNotional"] = effectiveSize,
                                         ["barNotional"]   = barNotionalUsd,
                                         ["participation"] = effectiveSize / barNotionalUsd,
                                         ["maxRealistic"]  = maxOk });
                    }
                }

                if (routed && underCap && isNew && liquidityOk)
                {
                    try
                    {
                        var orderResp = await client.PlaceOrderAsync(sym, side, effectiveSize, entry, isLimit: true, reduceOnly: false, tif: "Gtc");
                        Console.WriteLine($"  [ENTRY] {strat,-10} {sym,-8} {dir.ToUpperInvariant()} b{holdBars} @ {entry:F4} pnl={pnl:+0.00}%  order={JsonSerializer.Serialize(orderResp)}");
                        // Full venue response, verbatim — fills, rejects, order ids. `orderResp` was
                        // previously logged to console and discarded, so nothing could later answer
                        // "did this actually fill, and at what price".
                        LogTradeData("entry", strat, sym, entry, px, pnl,
                                     exchangePos != null && exchangePos.TryGetValue(
                                         HyperliquidClient.NormalizeToHyperliquidCoin(sym), out var ep) ? ep : default,
                                     orderResp);
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
                        st.Entry, h1[^1].Close, st.HoldCount, Math.Min(st.HardStop, st.MaeStop), st.Target, st.TrailArmed, gForCoin.PositionSizePct, volM * 1_000_000.0);

                // ── Grid ──
                if (gridGPt != null)
                {
                    var coinGridG = StrategyPipeline.SelectVariant(gridVariantsPt, m15) ?? gridGPt;
                    var gst = GridSimulator.GetGridTradeState(coinGridG, h1);
                    if (gst.Active)
                        await ProcessSignalAsync("Grid", "grid", 12, sym, "Long", "B", gridRoutedOn,
                            gst.Anchor, h1[^1].Close, gst.HoldCount, gst.HardStop, 0.0, false, null, volM * 1_000_000.0);
                }

                // ── SwingLong ──
                if (slGenoPt != null)
                {
                    var coinSlG = StrategyPipeline.SelectVariant(slVariantsPt, m15) ?? slGenoPt;
                    var slSt = SwingLongSimulator.GetSwingLongTradeState(coinSlG, h1, m15, fundingSession);
                    if (slSt.InTrade)
                        await ProcessSignalAsync("SwingLong", "swing_long", 8, sym, "Long", "B", slRoutedOn,
                            slSt.Entry, h1[^1].Close, slSt.HoldCount, slSt.HardStop, slSt.Target, slSt.TrailArmed, coinSlG.PositionSizePct, volM * 1_000_000.0);
                }

                // ── DipLong ──
                if (dlGenoPt != null)
                {
                    var coinDlG = StrategyPipeline.SelectVariant(dlVariantsPt, m15) ?? dlGenoPt;
                    var dlSt = DipLongSimulator.GetDipLongTradeState(coinDlG, h1, m15, fundingSession);
                    if (dlSt.InTrade)
                        await ProcessSignalAsync("DipLong", "diplong", 8, sym, "Long", "B", dlRoutedOn,
                            dlSt.Entry, h1[^1].Close, dlSt.HoldCount, dlSt.HardStop, dlSt.Target, dlSt.TrailArmed, coinDlG.PositionSizePct, volM * 1_000_000.0);
                }

                // ── FadeLong ──
                if (flGenoPt != null)
                {
                    var coinFlG = StrategyPipeline.SelectVariant(flVariantsPt, m15) ?? flGenoPt;
                    var flSt = FadeLongSimulator.GetFadeLongTradeState(coinFlG, h1, m15, fundingSession);
                    if (flSt.InTrade)
                        await ProcessSignalAsync("FadeLong", "fadelong", 8, sym, "Long", "B", flRoutedOn,
                            flSt.Entry, h1[^1].Close, flSt.HoldCount, Math.Min(flSt.HardStop, flSt.MaeStop), flSt.Target, flSt.TrailArmed, coinFlG.PositionSizePct, volM * 1_000_000.0);
                }

                // ── RipShort ──
                if (rsGenoPt != null)
                {
                    var coinRsG = StrategyPipeline.SelectVariant(rsVariantsPt, m15) ?? rsGenoPt;
                    var rsSt = RipShortSimulator.GetRipShortTradeState(coinRsG, h1, m15);
                    if (rsSt.InTrade)
                        await ProcessSignalAsync("RipShort", "ripshort", 8, sym, "Short", "A", rsRoutedOn,
                            rsSt.Entry, h1[^1].Close, rsSt.HoldCount, rsSt.HardStop, rsSt.Target, rsSt.TrailArmed, coinRsG.PositionSizePct, volM * 1_000_000.0);
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

                    // ── ACTUALLY CLOSE IT ────────────────────────────────────────────────────
                    // This branch previously journalled the exit and sent NO order. The strategy
                    // simulator decided the trade was over; the real position stayed open forever.
                    // Harmless only because the wallet was never funded — once funded the failure
                    // mode is monotonic accumulation, since simulated exits free cap slots while
                    // the real positions remain.
                    if (kv.Value.Routed && exchangePos != null
                        && exchangePos.TryGetValue(HyperliquidClient.NormalizeToHyperliquidCoin(parts[1]), out var live)
                        && Math.Abs(live.Size) > 1e-12)
                    {
                        // Close in the opposite direction of what the EXCHANGE says we hold —
                        // not what the simulator thinks — and reduceOnly so it can never flip us.
                        string closeSide = live.Size > 0 ? "sell" : "buy";
                        try
                        {
                            var resp = await client.PlaceOrderAsync(parts[1], closeSide, Math.Abs(live.Size),
                                kv.Value.Mark, isLimit: false, reduceOnly: true, tif: "Ioc");
                            Console.WriteLine($"  [EXIT] {parts[0],-10} {parts[1],-8} close {closeSide} sz={Math.Abs(live.Size)} "
                                            + $"pnl={kv.Value.Pnl:+0.00}%  resp={JsonSerializer.Serialize(resp)}");
                            LogTradeData("exit", parts[0], parts[1], kv.Value.Entry, kv.Value.Mark,
                                         kv.Value.Pnl, live, resp);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [EXIT FAILED] {parts[0]} {parts[1]}: {ex.Message} — POSITION STILL OPEN");
                            LogTradeData("exit_failed", parts[0], parts[1], kv.Value.Entry, kv.Value.Mark,
                                         kv.Value.Pnl, live, new Dictionary<string, object> { ["error"] = ex.Message });
                        }
                    }
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
                // These report whether a strategy CAN trade, so they must reflect the loaded
                // genotype as well as the router flag. Reporting FadeLong=true off the router alone
                // while its genotype is absent would show an active strategy that cannot fire —
                // exactly the class of misleading status line that hid a dead pipeline for a week.
                FadeShort = gUniversalPt != null && ptRouting.FadeShortActive,
                Grid = gridGPt != null && ptRouting.GridActive,
                SwingLong = slGenoPt != null && ptRouting.SwingLongActive,
                DipLong = dlGenoPt != null && ptRouting.DipLongActive,
                FadeLong = flGenoPt != null && ptRouting.FadeLongActive,
                RipShort = rsGenoPt != null && ptRouting.RipShortActive,
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
