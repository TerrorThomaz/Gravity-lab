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

        const int RefreshSeconds = 900;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            try { Console.Clear(); } catch { }
            Console.WriteLine($"=== Gravity-gen2 | Hyperliquid Paper Trade  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]  Ctrl+C to stop ===\n");

            // Fetch 15m candles for all coins (parallel HTTP calls to FastAPI bridge)
            var sem = new SemaphoreSlim(4);
            var coinTasks = coins.Select(async sym =>
            {
                await sem.WaitAsync(cts.Token);
                try
                {
                    var h1Candles = await client.FetchH1CandlesAsync(sym, batches: 12);
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
            
            var coinData = (await Task.WhenAll(coinTasks)).Where(x => x.Item2 is not null).ToList();

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
            var btcData = coinData.FirstOrDefault(x => x.Item1 == "BTC");
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
                var btcPt = coinData.FirstOrDefault(x => x.Item1 == "BTC");
                var ethPt = coinData.FirstOrDefault(x => x.Item1 == "ETH");
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

            // Print open positions per strategy (simplified version of Bybit papertrade)
            // TODO: Wire actual order placement/cancel via client.PlaceOrderAsync / client.CancelOrderAsync
            
            // Write live_state.json (same format as Bybit papertrade)
            // TODO: Implement full journal/diff logic
            
            await Task.Delay(RefreshSeconds * 1000, cts.Token);
        }

        Console.WriteLine("\n\n  Paper trade stopped.");
    }
}
