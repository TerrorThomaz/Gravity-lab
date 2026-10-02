using Bybit.Net.Clients;
using System.Text.Json;
using GravityGen2.Strategies.HybridGrid;

namespace TradingGA;

// signalstudy [--stride N] [--universe all|oos|train] [--placebos N]
//
// DIAGNOSTIC ONLY. Builds a (coin × h1 bar) panel of the signals the strategies are made of and
// measures which carry information about forward returns — see src/research/SignalStudy.cs for the
// method. Changes no genotype, feeds no GA, and records no GA trials: its constants are fixed, so it
// is one model, not a search. Strategy attribution runs on Config.OosCoins only (never-trained).
static class SignalStudyCommand
{
    private const int Batches = 113;

    public static async Task Run(BybitRestClient client, string[] args)
    {
        int stride = ArgInt(args, "--stride", 3);
        int placebos = ArgInt(args, "--placebos", 20);
        string universe = ArgStr(args, "--universe", "all");
        var oos = Config.OosCoins.ToHashSet();
        var symbols = (universe switch
        {
            "oos"   => Config.OosCoins,
            "train" => Config.BacktestCoins,
            _       => Config.BacktestCoins.Concat(Config.OosCoins).ToArray(),
        }).Where(s => s != "BTCUSDT").Distinct().ToList();

        Console.WriteLine($"\n═══ SIGNAL STUDY — {symbols.Count} coins, stride {stride}, universe={universe} ═══\n");

        var btcM15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: Batches);
        var btcH1  = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);
        var btc    = new SignalFeatures.BtcContext(btcH1);
        Console.WriteLine($"  BTC context: {btcH1.Length} h1 bars");

        var funding = await CandleFetcher.FetchFundingSessionsAsync(client, symbols);

        // Live genotypes for attribution (the three on disk + HybridGrid if trained).
        var fs = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>("fade_short", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var gr = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>("grid_best", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var gs = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>("grid_short", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        HybridGridGenotype? hy = File.Exists(HybridGridCommands.GenoPath(HybridGridSides.Both))
            ? JsonSerializer.Deserialize<HybridGridGenotype>(File.ReadAllText(HybridGridCommands.GenoPath(HybridGridSides.Both))) : null;

        var panel = new SignalPanel();
        var trades = new List<SignalStudy.AttributionTrade>();
        var gate = new object();
        var sem = new SemaphoreSlim(4);
        int done = 0, skipped = 0;
        await Task.WhenAll(symbols.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = (await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: Batches)).ToArray();
                var h1  = FadeShortSimulator.AggregateCandles(m15, 4);
                var vol = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                if (h1.Length < 1500 || vol.Count == 0 || vol[vol.Count / 2] < Config.MinMedianVolUsdM)
                { Interlocked.Increment(ref skipped); return; }

                var f = funding.For(sym);
                var local = new List<SignalStudy.AttributionTrade>();
                float[][] F;
                lock (gate) F = panel.AddCoin(sym, h1, f, btc, stride);

                if (oos.Contains(sym))
                {
                    var atr = Volatility.Atr(CandleExt.Highs(h1), CandleExt.Lows(h1), CandleExt.Closes(h1), 14);
                    void Add(string label, IEnumerable<(double Return, DateTime EntryTime, int Dir)> ts)
                    {
                        foreach (var t in ts)
                            if (Attribute(label, h1, F, atr, t.EntryTime, t.Return, t.Dir) is { } a) local.Add(a);
                    }
                    if (StrategyPipeline.SelectVariant(fs, m15) is { } g1)
                        Add("FadeShort", FadeShortSimulator.GetFadeShortReturns(g1, h1, m15).Select(t => (t.Return, t.EntryTime, -1)));
                    if (StrategyPipeline.SelectVariant(gr, m15) is { } g2)
                        Add("Grid", GridSimulator.GetGridSessionReturns(g2, h1, f).Select(t => (t.Return, t.EntryTime, +1)));
                    if (StrategyPipeline.SelectVariant(gs, m15) is { } g3)
                        Add("GridShort", GridShortSimulator.GetGridShortSessionReturns(g3, h1, f).Select(t => (t.Return, t.EntryTime, -1)));
                    if (hy is not null)
                        Add("HybridGrid", HybridGridSimulator.GetHybridReturns(hy, h1, HybridGridSides.Both, f)
                            .Select(t => (t.Return, t.EntryTime, t.Kind == HybridGridSimulator.LongKind ? +1 : -1)));
                }
                lock (gate) trades.AddRange(local);
                int d = Interlocked.Increment(ref done);
                if (d % 20 == 0) Console.WriteLine($"  … {d} coins in panel ({panel.Rows:N0} rows)");
            }
            finally { sem.Release(); }
        }));
        Console.WriteLine($"  Panel: {panel.CoinNames.Count} coins, {panel.Rows:N0} rows · skipped {skipped} (short history or < ${Config.MinMedianVolUsdM}M/h median volume)");
        Console.WriteLine($"  Attribution trades (OOS coins): {trades.Count:N0}\n");

        var result = SignalStudy.Analyze(panel, stride, trades, verbose: true, placebos: placebos);
        string text = SignalStudy.Format(result);
        Console.WriteLine();
        Console.WriteLine(text);

        Directory.CreateDirectory("reports");
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd");
        File.WriteAllText($"reports/signalstudy_{stamp}.txt", text);
        File.WriteAllText($"reports/signalstudy_ic_{stamp}.csv", SignalStudy.IcCsv(result));
        Console.WriteLine($"Saved → reports/signalstudy_{stamp}.txt, reports/signalstudy_ic_{stamp}.csv");
    }

    // Features from the last h1 bar that had CLOSED by the entry time (bar open + 1h ≤ entry).
    // Grid stamps EntryTime with the OPEN of its arming bar, so for grids this is the bar before the
    // one it armed on — one bar stale, never one bar early.
    internal static SignalStudy.AttributionTrade? Attribute(string label, Candle[] h1, float[][] F, double[] atr,
                                                           DateTime entry, double retPct, int dir)
    {
        long target = (entry - TimeSpan.FromHours(1)).Ticks;
        int lo = 0, hi = h1.Length - 1, j = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (h1[mid].Time.Ticks <= target) { j = mid; lo = mid + 1; } else hi = mid - 1; }
        if (j < SignalFeatures.Warmup) return null;
        double atrPct = TradeCosts.AtrPct(atr[j], h1[j].Close);
        if (atrPct < 1e-9) return null;
        var x = new float[SignalFeatures.Count];
        for (int k = 0; k < x.Length; k++) x[k] = F[k][j];
        return new SignalStudy.AttributionTrade(label, SignalPanel.DayOf(entry), x, retPct / atrPct, dir);
    }

    private static int ArgInt(string[] a, string name, int def)
    { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) && v > 0 ? v : def; }
    private static string ArgStr(string[] a, string name, string def)
    { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1].ToLowerInvariant() : def; }
}
