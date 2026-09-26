using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

// Runs the post-GA finalist screens on the COMMITTED genotypes, without a GA search.
//
// Why a separate command rather than just retraining: a `gridtrain` run records ~11,000 candidate
// evaluations in genotypes/ga_trials.json, and those trials deflate the Sharpe of every genotype in
// the book. A diagnostic must not cost that — nor should it overwrite the artefact it is examining,
// which is what a retrain does. This loads what is on disk, screens it, writes nothing.
//
// Both screens are REPORT-ONLY by design. Using either to select would make it one more fitted
// surface and one more unlogged trial. See docs/RIGOR_REWORK_2026-09.md §6.
public static class ScreenCommand
{
    // Same pool GridCommands.RunGridTrain uses, so the screen sees the data the GA selected on.
    private static readonly (string Sym, double Weight)[] GridCoins =
    {
        ("SOLUSDT",  1.0), ("ETHUSDT",  1.0), ("BNBUSDT",  1.0), ("XRPUSDT",  1.0),
        ("DOGEUSDT", 1.0), ("AVAXUSDT", 1.0), ("ADAUSDT",  1.0), ("LINKUSDT", 1.0),
        ("DOTUSDT",  1.0), ("MATICUSDT",1.0), ("ATOMUSDT", 0.9), ("NEARUSDT", 0.9),
        ("INJUSDT",  0.9), ("OPUSDT",   0.9), ("ARBUSDT",  0.9), ("UNIUSDT",  0.9),
        ("AAVEUSDT", 0.9), ("RUNEUSDT", 0.9), ("WIFUSDT",  0.8), ("1000PEPEUSDT", 0.8),
        ("APTUSDT",  0.8), ("SUIUSDT",  0.8), ("TIAUSDT",  0.8), ("SEIUSDT",  0.8),
        ("STXUSDT",  0.8), ("JUPUSDT",  0.8),
    };

    public static async Task Run(BybitRestClient client)
    {
        Console.WriteLine("\n=== Gravity-gen2 | FINALIST SCREEN (committed genotypes, no GA, no trials) ===\n");
        Console.WriteLine("  Two questions per genotype:");
        Console.WriteLine("    robustness          — do its NEIGHBOURS score similarly? A lone peak is noise.");
        Console.WriteLine("    outlier sensitivity — does its edge survive deleting the best 1% / 5% of trades?");
        Console.WriteLine("                          (Falck, Rej & Thesmar: a validated ex-ante decay predictor)\n");

        Console.WriteLine($"  Fetching {GridCoins.Length} train coins…");
        var sem = new SemaphoreSlim(4);
        var fetched = await Task.WhenAll(GridCoins.Select(async t =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, t.Sym, batches: 113);
                return (t.Sym, t.Weight, H1: FadeShortSimulator.AggregateCandles(m15.ToArray(), 4));
            }
            finally { sem.Release(); }
        }));
        var funding = await CandleFetcher.FetchFundingSessionsAsync(client, fetched.Select(f => f.Sym));
        var cfg = FitnessConfig.Load();

        // ── Grid ─────────────────────────────────────────────────────────────────────────────
        if (File.Exists(Config.GridGenoFile))
        {
            var g = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
            var coins = new List<GridGeneticAlgorithm.CoinData>();
            foreach (var (sym, w, h1) in fetched)
            {
                if (h1.Length < 150) continue;
                int split = DataSplit.Split(h1).Train.Length;
                coins.Add(new GridGeneticAlgorithm.CoinData(h1[..split], h1[split..], w, funding.For(sym)));
            }
            Console.WriteLine($"\n── Grid ── {g}");
            new GridGeneticAlgorithm(populationSize: 1, generations: 1, verbose: false, cfg: cfg)
                .ScreenFinalist(g, coins);
        }
        else Console.WriteLine("\n── Grid ── no genotype on disk, skipped");

        // ── GridShort ────────────────────────────────────────────────────────────────────────
        if (File.Exists(Config.GridShortGenoFile))
        {
            var g = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridShortGenoFile))!.ToGenotype();
            var coins = new List<GridShortGA.CoinData>();
            foreach (var (_, w, h1) in fetched)
            {
                if (h1.Length < 150) continue;
                int split = DataSplit.Split(h1).Train.Length;
                coins.Add(new GridShortGA.CoinData(h1[..split], h1[split..], w));
            }
            Console.WriteLine($"\n── GridShort ── {g}");
            new GridShortGA(populationSize: 1, generations: 1, verbose: false, cfg: cfg)
                .ScreenFinalist(g, coins);
        }
        else Console.WriteLine("\n── GridShort ── no genotype on disk, skipped");

        // ── FadeShort ────────────────────────────────────────────────────────────────────────
        if (File.Exists(Config.FadeShortGenoFile))
        {
            var g = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
            var coins = new List<FadeShortGA.CoinData>();
            foreach (var (_, w, h1) in fetched)
            {
                if (h1.Length < 150) continue;
                int split = DataSplit.Split(h1).Train.Length;
                coins.Add(new FadeShortGA.CoinData(h1[..split], h1[split..], w));
            }
            Console.WriteLine($"\n── FadeShort ── {g}");
            new FadeShortGA(populationSize: 1, generations: 1, verbose: false, cfg: cfg)
                .ScreenFinalist(g, coins);
        }
        else Console.WriteLine("\n── FadeShort ── no genotype on disk, skipped");

        Console.WriteLine("\n  Reading the numbers:");
        Console.WriteLine("    robustness ratio  ~100% = plateau (good) · <50% = sharp peak, likely fitted to trades");
        Console.WriteLine("    WORST neighbour   read this too — a healthy median hides a nearby cliff");
        Console.WriteLine("    drop on top 1%    >50% means the edge lives in a handful of trades");
        Console.WriteLine("\n  Neither screen can show a genotype is GOOD. Both can show one is fragile.");
    }
}
