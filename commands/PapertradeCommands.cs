using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class PapertradeCommands
{
    public static async Task RunPaperTrade(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | PAPER TRADE (15m→1h candles) — Ctrl+C to stop ===\n");

        if (!File.Exists(Config.FadeShortGenoFile))
        {
            Console.WriteLine($"No genotype at '{Config.FadeShortGenoFile}'. Run 'dotnet run -- train' first.");
            return;
        }
        var gUniversalPt = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        Console.WriteLine($"Universal genotype: {gUniversalPt}");

        var clusterGenosPt = new Dictionary<CoinCluster, FadeShortGenotype>();
        foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
        {
            string clFile = CoinClusterHelper.GenoFile(cl);
            clusterGenosPt[cl] = File.Exists(clFile)
                ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                : gUniversalPt;
            Console.WriteLine($"  [{CoinClusterHelper.Label(cl)}] {clusterGenosPt[cl]}");
        }

        GridGenotype? gridGPt = null;
        if (File.Exists(Config.GridGenoFile))
        {
            gridGPt = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
            Console.WriteLine($"Grid genotype:     {gridGPt}");
        }
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

        SwingLongGenotype? slGenoPt = null;
        if (File.Exists(Config.SwingLongGenoFile))
        {
            slGenoPt = JsonSerializer.Deserialize<SwingLongGenotypeDto>(
                File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype();
            Console.WriteLine($"SwingLong:         {slGenoPt}");
        }

        DipLongGenotype? dlGenoPt = null;
        if (File.Exists(Config.DipLongGenoFile))
        {
            dlGenoPt = JsonSerializer.Deserialize<DipLongGenotypeDto>(
                File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype();
            Console.WriteLine($"DipLong:           {dlGenoPt}");
        }

        FadeLongGenotype? flGenoPt = null;
        if (File.Exists(Config.FadeLongGenoFile))
        {
            flGenoPt = JsonSerializer.Deserialize<FadeLongGenotypeDto>(
                File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype();
            Console.WriteLine($"FadeLong:          {flGenoPt}");
        }

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
                var    gForCoin = clusterGenosPt[coinCl];
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

                    double px  = h1[^1].Close;
                    var    gst = GridSimulator.GetGridTradeState(gridGPt, h1);

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
                    double px = h1[^1].Close;
                    var st = SwingLongSimulator.GetSwingLongTradeState(slGenoPt, h1, m15);
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
                    double px = h1[^1].Close;
                    var st = DipLongSimulator.GetDipLongTradeState(dlGenoPt, h1, m15);
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
                    double px = h1[^1].Close;
                    var st = FadeLongSimulator.GetFadeLongTradeState(flGenoPt, h1, m15);
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
