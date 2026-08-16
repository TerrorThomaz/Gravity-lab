using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

// Standalone walk-forward validation of the COMMITTED genotypes (the files in genotypes/), on
// never-trained coins. This complements ExpandingWindowValidation, which runs INSIDE each strategy's
// GA train loop and re-trains a fresh GA per window — that measures "does a freshly-trained model
// generalise forward?", not "does the genotype we would actually trade generalise forward?".
//
// The whole point of the committed-genotype variant is that it can run WITHOUT retraining: load the
// saved JSON, slice each OOS coin's own history into expanding train/test stages, run the committed
// simulators on both sides, and report IS/OOS trade-Sharpe efficiency per stage. Fast enough to run
// nightly on already-cached data.
//
// Caveat echoed from ExpandingWindowValidation: efficiency uses per-trade Sharpe (no time
// normalisation), so a perfectly-generalising formula measures 1.0; < 0.5 flags overfit. The IS and
// OOS windows have different trade counts, and per-trade Sharpe is count-scale-free, so the ratio is
// comparable (unlike the frequency-normalised SharpeRatio, whose sqrt(candleCount/288) telescopes).
public static class WalkForwardCommand
{
    private const int StageCount   = 4;
    private const int MinStageBars = 60;
    private const double OverfitEffThreshold = 0.5;

    public static async Task Run(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | WALK-FORWARD on COMMITTED genotypes (never-trained coins) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        var fsG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        GridGenotype? gridG = File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()
            : null;

        Console.WriteLine($"  FadeShort: {fsG}");
        if (gridG != null) Console.WriteLine($"  Grid:      {gridG}");
        Console.WriteLine();
        Console.WriteLine($"  {Config.OosCoins.Length} never-seen OOS coins · {StageCount} expanding stages each · no retraining\n");

        var allSyms = Config.OosCoins.Concat(new[] { "BTCUSDT" }).Distinct().ToArray();
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, allSyms, batches: 113);

        // Aggregate IS/OOS returns per stage across all coins.
        var fsIs = new List<double>[StageCount]; var fsOos = new List<double>[StageCount];
        var gridIs = new List<double>[StageCount]; var gridOos = new List<double>[StageCount];
        for (int i = 0; i < StageCount; i++) { fsIs[i] = new(); fsOos[i] = new(); gridIs[i] = new(); gridOos[i] = new(); }

        int lstages = 0;
        foreach (var (sym, m15List, _) in fetched)
        {
            if (m15List.Length < (StageCount + 1) * MinStageBars * 4) continue;
            var h1 = FadeShortSimulator.AggregateCandles(m15List.ToArray(), 4);

            for (int s = 0; s < StageCount; s++)
            {
                // Stage s: train = [0, trainEnd), test = [trainEnd, trainEnd+step), on THIS coin's array.
                int step = h1.Length / (StageCount + 1);
                int trainEnd = (s + 1) * step;                 // increasingly long train
                int testEnd  = Math.Min(trainEnd + step, h1.Length);
                if (trainEnd < MinStageBars || testEnd - trainEnd < MinStageBars) continue;

                lstages++;
                var trainSpan = new ReadOnlySpan<Candle>(h1, 0, trainEnd);
                var testSpan  = new ReadOnlySpan<Candle>(h1, trainEnd, testEnd - trainEnd);

                // FadeShort (run on h1; the committed path uses h1 for regime/setup + m15 for fill,
                // but the single-timeframe GetFadeShortReturns(h1) is the portable OOS signal used by
                // the backtests — keep this consistent with the backtest's FadeShort evaluation).
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fsG, trainSpan)) fsIs[s].Add(t.Return);
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(fsG, testSpan))  fsOos[s].Add(t.Return);

                if (gridG != null)
                {
                    foreach (var t in GridSimulator.GetGridReturns(gridG, trainSpan)) gridIs[s].Add(t.Return);
                    foreach (var t in GridSimulator.GetGridReturns(gridG, testSpan))  gridOos[s].Add(t.Return);
                }
            }
        }

        Console.WriteLine($"\n  {lstages} coin-stage evaluations across {Config.OosCoins.Length} coins\n");

        Print("FadeShort", fsIs, fsOos);
        if (gridG != null) Print("Grid", gridIs, gridOos);

        // Overall verdict.
        Console.WriteLine($"\n  OVERALL");
        Console.WriteLine($"    FadeShort efficiency:  {Eff(fsIs, fsOos),8:F3}  {(Eff(fsIs, fsOos) > OverfitEffThreshold ? "no overfit" : "OVERFIT")}");
        if (gridG != null)
            Console.WriteLine($"    Grid efficiency:       {Eff(gridIs, gridOos),8:F3}  {(Eff(gridIs, gridOos) > OverfitEffThreshold ? "no overfit" : "OVERFIT")}");
        Console.WriteLine("\n    (efficiency = mean(OOS Sharpe)/mean(IS Sharpe) across stages; < 0.5 flags overfit on never-seen coins)");
    }

    private static void Print(string name, List<double>[] isRet, List<double>[] oosRet)
    {
        Console.WriteLine($"── {name} (committed genotype, never-trained coins) ──");
        Console.WriteLine($"  {"Stage",6}  {"Train bars*",11}  {"Test bars*",9}  {"IS Sharpe",9}  {"OOS Sharpe",10}  {"Efficiency",11}");
        for (int s = 0; s < StageCount; s++)
        {
            double isS  = isRet[s].Count >= 10 ? FoldScoreHelper.PerTradeSharpe(isRet[s])    : 0;
            double oosS = oosRet[s].Count >= 10 ? FoldScoreHelper.PerTradeSharpe(oosRet[s])  : 0;
            bool usable = isS > 0.05;
            double eff = usable ? Math.Clamp(oosS / isS, -1.0, 2.0) : double.NaN;
            string effStr = usable ? eff.ToString("F3") : "— (IS <0.05)";
            string isStr  = isRet[s].Count >= 10 ? isS.ToString("F4") : "—";
            string oosStr = oosRet[s].Count >= 10 ? oosS.ToString("F4") : "—";
            Console.WriteLine($"  {s,6}  {isRet[s].Count,11}  {oosRet[s].Count,9}  {isStr,9}  {oosStr,10}  {effStr,11}");
        }
        double overall = Eff(isRet, oosRet);
        bool over = !double.IsNaN(overall) && overall < OverfitEffThreshold;
        Console.WriteLine($"  → mean efficiency {overall:F3}  {(over ? "OVERFIT" : "no overfit")}\n");
    }

    private static double Eff(List<double>[] isRet, List<double>[] oosRet)
    {
        var ratios = new List<double>();
        for (int s = 0; s < StageCount; s++)
        {
            if (isRet[s].Count < 10 || oosRet[s].Count < 10) continue;
            double isS = FoldScoreHelper.PerTradeSharpe(isRet[s]);
            if (isS <= 0.05) continue;
            double e = Math.Clamp(FoldScoreHelper.PerTradeSharpe(oosRet[s]) / isS, -1.0, 2.0);
            ratios.Add(e);
        }
        return ratios.Count > 0 ? ratios.Average() : double.NaN;
    }
}