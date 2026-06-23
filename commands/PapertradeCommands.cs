using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class PapertradeCommands
{
    // ── Variant loading helpers ───────────────────────────────────────────────
    static VariantSpec<TG>[] LoadVariants<TDto, TG>(
        string strategyKey,
        Func<TDto, TG> toGenotype,
        Func<TDto, (double Low, double High)> getRange)
        where TG : class
    {
        var files = Directory.Exists("genotypes")
            ? Directory.GetFiles("genotypes", $"{strategyKey}_*_genotype.json")
            : Array.Empty<string>();
        string defaultFile = $"genotypes/{strategyKey}_genotype.json";
        if (File.Exists(defaultFile))
            files = files.Append(defaultFile).Distinct().ToArray();
        if (files.Length == 0) return Array.Empty<VariantSpec<TG>>();
        return files.Select(f =>
        {
            var dto = JsonSerializer.Deserialize<TDto>(File.ReadAllText(f))!;
            var (lo, hi) = getRange(dto);
            string variantId = Path.GetFileNameWithoutExtension(f)
                .Replace($"{strategyKey}_", "").Replace("_genotype", "");
            return new VariantSpec<TG>(variantId, lo, hi, toGenotype(dto));
        }).ToArray();
    }

    // Selects a genotype from variants using the last-bar ATR ratio of the m15 array.
    // Returns null when no variants are loaded or insufficient history.
    static TG? SelectVariant<TG>(VariantSpec<TG>[] variants, Candle[] m15)
        where TG : class
    {
        if (variants.Length == 0) return null;
        double[] highs  = m15.Select(c => c.High).ToArray();
        double[] lows   = m15.Select(c => c.Low).ToArray();
        double[] closes = m15.Select(c => c.Close).ToArray();
        double[] atr    = Indicators.Atr(highs, lows, closes, 14);
        int      bar    = atr.Length - 1;
        return VariantRouter.Select(atr, bar, variants) ?? variants[0].Genotype;
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
        var fsVariantsPt = LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariantsPt = LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariantsPt = LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariantsPt = LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariantsPt = LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

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

        var slGenoPt = slVariantsPt.Length > 0 ? slVariantsPt[0].Genotype : null;
        if (slGenoPt != null)
            Console.WriteLine($"SwingLong:         {slGenoPt}  [{slVariantsPt.Length} variant(s)]");

        var dlGenoPt = dlVariantsPt.Length > 0 ? dlVariantsPt[0].Genotype : null;
        if (dlGenoPt != null)
            Console.WriteLine($"DipLong:           {dlGenoPt}  [{dlVariantsPt.Length} variant(s)]");

        var flGenoPt = flVariantsPt.Length > 0 ? flVariantsPt[0].Genotype : null;
        if (flGenoPt != null)
            Console.WriteLine($"FadeLong:          {flGenoPt}  [{flVariantsPt.Length} variant(s)]");

        Console.WriteLine();

        var coins = Config.BacktestCoins;

        const int RefreshSeconds = 14400;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        while (!cts.Token.IsCancellationRequested)
        {
            Console.Clear();
            Console.WriteLine($"=== Gravity-gen2 | PAPER TRADE  [{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]  Ctrl+C to stop ===\n");

            // Fetch all coins in parallel (4 concurrent), then trim to last 2000 m15 bars.
            // Full CSV history (~28 000 h1 bars) is never loaded into simulator arrays —
            // 500 h1 / 2000 m15 gives a 2× margin over the worst-case warmup (FadeLong 212 bars).
            const int PtM15Window = 2000;
            var sem = new SemaphoreSlim(4);
            var fetchTasks = coins.Select(async sym =>
            {
                await sem.WaitAsync(cts.Token);
                try
                {
                    var m15Raw = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 5);
                    if (m15Raw.Count < 200) return (sym, (Candle[]?)null, (Candle[]?)null, false, 0.0, 0.0);
                    var trimmed = m15Raw.Count > PtM15Window
                        ? m15Raw.GetRange(m15Raw.Count - PtM15Window, PtM15Window)
                        : m15Raw;
                    var (passes, atrPct, volM) = CandleFetcher.CheckSwingCriteria(m15Raw);
                    var m15 = trimmed.ToArray();
                    var h1  = FadeShortSimulator.AggregateCandles(m15, 4);
                    return (sym, (Candle[]?)h1, (Candle[]?)m15, passes, atrPct, volM);
                }
                finally { sem.Release(); }
            }).ToList();
            var coinData = (await Task.WhenAll(fetchTasks))
                .Select(r => (Sym: r.sym, H1: r.Item2, M15: r.Item3, Passes: r.Item4, AtrPct: r.Item5, VolM: r.Item6))
                .ToList();

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
                    string sizeTag   = ptRouting.SizeMult < 1.0 ? $"  size×{ptRouting.SizeMult:F2}" : "";
                    Console.WriteLine($"  Regime {routerTag}: {ptRouting.Regime}  conf={ptRouting.Confidence:P0}  →  {RegimeRouter.Describe(ptRouting)}{sizeTag}\n");
                }
            }

            // ── FadeShort ─────────────────────────────────────────────────────────
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
                var    gForCoin = SelectVariant(fsVariantsPt, m15) ?? clusterGenosPt[coinCl];
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
                    var    coinGridG = (m15 != null ? SelectVariant(gridVariantsPt, m15) : null) ?? gridGPt;
                    var    gst = GridSimulator.GetGridTradeState(coinGridG, h1);

                    string stateStr  = gst.Active ? $"GRID L{gst.FilledLevels}" : "watching";
                    string anchorStr = gst.Active ? $"{gst.Anchor:F4}" : "—";
                    string unreal    = gst.Active ? $"{(px - gst.Anchor) / gst.Anchor * 100.0:+0.00}%" : "—";
                    string stopStr   = gst.Active ? $"{gst.HardStop:F4}" : "—";
                    string barsStr   = gst.Active ? $"{gst.HoldCount}" : "—";

                    Console.WriteLine($"  {sym,-18}  {stateStr,-14} {anchorStr,12}  {px,12:F4}  {unreal,11}  {barsStr,5}  {stopStr,12}");
                }
            }

            int slOpen = 0, dlOpen = 0, flOpen = 0;

            // ── SwingLong ─────────────────────────────────────────────────────────
            bool slRoutedOn = ptRouting == null || ptRouting.DipLongActive;
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
                    var coinSlG    = SelectVariant(slVariantsPt, m15) ?? slGenoPt;
                    var st = SwingLongSimulator.GetSwingLongTradeState(coinSlG, h1, m15);
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
                    var coinDlG    = SelectVariant(dlVariantsPt, m15) ?? dlGenoPt;
                    var st = DipLongSimulator.GetDipLongTradeState(coinDlG, h1, m15);
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
                    var coinFlG    = SelectVariant(flVariantsPt, m15) ?? flGenoPt;
                    var st = FadeLongSimulator.GetFadeLongTradeState(coinFlG, h1, m15);
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

            if (cts.Token.IsCancellationRequested) break;

            // ── Write live_state.json for frontend consumption ───────────────
            {
                var positions = new List<object>();
                var signals   = new List<object>();

                void CollectPositions(string strategy, string dir,
                    IEnumerable<(string Sym, Candle[]? H1, Candle[]? M15, bool Passes, double AtrPct, double VolM)> data,
                    Func<string, Candle[], Candle[], (bool InTrade, bool TrailArmed, double Entry, double HardStop, double MaeStop, double Target, int HoldCount)> getState)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in data)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var st = getState(sym, h1, m15);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        double pnl = dir == "Short"
                            ? (st.Entry - px) / st.Entry * 100.0
                            : (px - st.Entry) / st.Entry * 100.0;
                        double effStop = Math.Min(st.HardStop, st.MaeStop > 0 ? st.MaeStop : double.MaxValue);
                        positions.Add(new
                        {
                            sym, strat = strategy, dir,
                            entry = Math.Round(st.Entry, 6),
                            mark  = Math.Round(px, 6),
                            pnl   = Math.Round(pnl, 2),
                            age   = $"{st.HoldCount}h",
                            stop  = Math.Round(effStop, 6),
                            target = Math.Round(st.Target, 6),
                            trailArmed = st.TrailArmed,
                        });
                    }
                }

                // FadeShort positions
                foreach (var (sym, h1, m15, passes, _, _) in coinData)
                {
                    if (h1 == null || m15 == null || !passes) continue;
                    var coinCl = CoinClusterHelper.ClassifyByName(sym);
                    var gForCoin = SelectVariant(fsVariantsPt, m15) ?? clusterGenosPt[coinCl];
                    var st = FadeShortSimulator.GetFadeShortTradeState(gForCoin, h1, m15);
                    if (!st.InTrade) continue;
                    double px = m15[^1].Close;
                    positions.Add(new
                    {
                        sym, strat = "FadeShort", dir = "Short",
                        entry = Math.Round(st.Entry, 6),
                        mark  = Math.Round(px, 6),
                        pnl   = Math.Round((st.Entry - px) / st.Entry * 100.0, 2),
                        age   = $"{st.HoldCount}h",
                        stop  = Math.Round(Math.Min(st.HardStop, st.MaeStop), 6),
                        target = Math.Round(st.Target, 6),
                        trailArmed = st.TrailArmed,
                    });
                }

                // Grid positions
                if (gridGPt != null && gridRoutedOn)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || !passes) continue;
                        var coinGridG = (m15 != null ? SelectVariant(gridVariantsPt, m15) : null) ?? gridGPt;
                        var gst = GridSimulator.GetGridTradeState(coinGridG, h1);
                        if (!gst.Active) continue;
                        double px = h1[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "Grid", dir = "Long",
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
                if (slGenoPt != null && slRoutedOn)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinSlG = SelectVariant(slVariantsPt, m15) ?? slGenoPt;
                        var st = SwingLongSimulator.GetSwingLongTradeState(coinSlG, h1, m15);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "SwingLong", dir = "Long",
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
                if (dlGenoPt != null && dlRoutedOn)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinDlG = SelectVariant(dlVariantsPt, m15) ?? dlGenoPt;
                        var st = DipLongSimulator.GetDipLongTradeState(coinDlG, h1, m15);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "DipLong", dir = "Long",
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
                if (flGenoPt != null && flRoutedOn)
                {
                    foreach (var (sym, h1, m15, passes, _, _) in coinData)
                    {
                        if (h1 == null || m15 == null || !passes) continue;
                        var coinFlG = SelectVariant(flVariantsPt, m15) ?? flGenoPt;
                        var st = FadeLongSimulator.GetFadeLongTradeState(coinFlG, h1, m15);
                        if (!st.InTrade) continue;
                        double px = m15[^1].Close;
                        positions.Add(new
                        {
                            sym, strat = "FadeLong", dir = "Long",
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

                var regimeInfo = ptRouting != null ? new
                {
                    state      = ptRouting.Regime.ToString(),
                    confidence = Math.Round(ptRouting.Confidence, 2),
                    FadeShort  = ptRouting.FadeShortActive,
                    Grid       = ptRouting.GridActive,
                    SwingLong  = ptRouting.DipLongActive,
                    DipLong    = ptRouting.DipLongActive,
                    FadeLong   = ptRouting.FadeLongActive,
                    sizeMult   = Math.Round(ptRouting.SizeMult, 2),
                } : null;

                var liveState = new
                {
                    timestamp   = DateTime.UtcNow.ToString("O"),
                    cycle       = 0,
                    positions,
                    regime      = regimeInfo,
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
            }

            for (int s = RefreshSeconds; s > 0; s--)
            {
                if (cts.Token.IsCancellationRequested) break;
                Console.Write($"\r  Next refresh in {s / 3600}h {s % 3600 / 60}m {s % 60:00}s  ");
                await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
            }
        }

        Console.WriteLine("\n\n  Paper trade stopped.");
    }
}
