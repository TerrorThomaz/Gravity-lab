using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

// Throwaway diagnostic: buckets RipShort's RAW (ungated) trade returns by the BTC
// regime confidence + duration at entry time, to check whether the router's hard
// Bear-confirmation gate (duration>=BearMinBars && conf>=BearMinConf) is cutting
// off real edge in the "low confidence bear" zone, or correctly excluding noise.
// Not wired into Program.cs — run manually, delete when done.
static class RipShortConfDiag
{
    public static async Task Run(BybitRestClient client)
    {
        var rsG = JsonSerializer.Deserialize<RipShortGenotypeDto>(File.ReadAllText(Config.RipShortGenoFile))!.ToGenotype();

        var allSyms = Config.BacktestCoins.Concat(Config.OosCoins).Concat(new[] { "BTCUSDT" }).Distinct().ToArray();
        var sem = new SemaphoreSlim(4);
        var fetched = await Task.WhenAll(allSyms.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, m15: m15.ToArray(), h1);
            }
            finally { sem.Release(); }
        }));
        var map = fetched.ToDictionary(f => f.sym);

        var btcH1 = map["BTCUSDT"].h1;
        var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
        var idx = new Dictionary<long, int>();
        for (int i = 0; i < btcSeries.Length; i++) idx.TryAdd(btcSeries[i].Time.Ticks / TimeSpan.TicksPerHour, i);
        int Lookup(DateTime t)
        {
            long key = t.Ticks / TimeSpan.TicksPerHour;
            if (idx.TryGetValue(key, out int bar)) return bar;
            for (int d = 1; d <= 4; d++) if (idx.TryGetValue(key - d, out bar)) return bar;
            return btcSeries.Length - 1;
        }

        var rows = new List<(double Ret, MarketRegime Regime, double Conf, int Duration)>();
        foreach (var sym in Config.OosCoins)
        {
            if (!map.TryGetValue(sym, out var entry) || entry.h1.Length < 300) continue;
            foreach (var t in RipShortSimulator.GetRipShortReturns(rsG, entry.h1, entry.m15))
            {
                int bar = Lookup(t.Time);
                var b = btcSeries[bar];
                rows.Add((t.Return, b.Regime, b.Confidence, b.Duration));
            }
        }

        Console.WriteLine($"\n=== RipShort raw OOS trades by BTC regime: {rows.Count} total ===\n");

        Console.WriteLine("-- By regime --");
        foreach (var g in rows.GroupBy(r => r.Regime).OrderByDescending(g => g.Count()))
            Report(g.Key.ToString(), g.Select(r => r.Ret).ToList());

        Console.WriteLine("\n-- Bear trades only, by confidence bucket --");
        var bear = rows.Where(r => r.Regime == MarketRegime.Bear).ToList();
        double[] edges = [0.0, 0.5, 0.6, 0.65, 0.70, 0.74, 0.80, 0.90, 1.01];
        for (int i = 0; i < edges.Length - 1; i++)
        {
            var bucket = bear.Where(r => r.Conf >= edges[i] && r.Conf < edges[i + 1]).Select(r => r.Ret).ToList();
            Report($"conf [{edges[i]:F2},{edges[i + 1]:F2})", bucket);
        }

        Console.WriteLine("\n-- Bear trades only, by duration bucket (bars) --");
        int[] durEdges = [0, 25, 50, 100, 143, 200, 300, int.MaxValue];
        for (int i = 0; i < durEdges.Length - 1; i++)
        {
            var bucket = bear.Where(r => r.Duration >= durEdges[i] && r.Duration < durEdges[i + 1]).Select(r => r.Ret).ToList();
            Report($"dur [{durEdges[i]},{(durEdges[i+1]==int.MaxValue?"inf":durEdges[i+1].ToString())})", bucket);
        }

        Console.WriteLine("\n-- Bear trades, joint conf>=0.74 vs dur>=143 (current router gate) vs relaxed variants --");
        Report("conf>=0.74 & dur>=143 (current gate)", bear.Where(r => r.Conf >= 0.74 && r.Duration >= 143).Select(r => r.Ret).ToList());
        Report("conf>=0.60 & dur>=143             ", bear.Where(r => r.Conf >= 0.60 && r.Duration >= 143).Select(r => r.Ret).ToList());
        Report("conf>=0.74 & dur>=50               ", bear.Where(r => r.Conf >= 0.74 && r.Duration >= 50).Select(r => r.Ret).ToList());
        Report("conf>=0.60 & dur>=50               ", bear.Where(r => r.Conf >= 0.60 && r.Duration >= 50).Select(r => r.Ret).ToList());
        Report("conf<0.60  (excluded zone)         ", bear.Where(r => r.Conf < 0.60).Select(r => r.Ret).ToList());
    }

    static void Report(string label, List<double> r)
    {
        if (r.Count == 0) { Console.WriteLine($"  {label,-38}   n=0"); return; }
        double wr = (double)r.Count(x => x > 0) / r.Count;
        double pf = Simulator.ProfitFactor(r);
        Console.WriteLine($"  {label,-38}   n={r.Count,5}  WR={wr,5:P0}  PF={pf,6:F2}  Avg={r.Average(),+7:F2}%");
    }
}
