using System.Text.Json;

namespace TradingGA;

/// <summary>
/// Diagnostic: verifies the candle data actually reaching Grid (and by extension every strategy,
/// since they all share the same fetch/aggregate path in HyperliquidPaperTrade.cs) is sane —
/// correct length, recent, and not silently empty/stale — then prints Grid's raw entry-gate
/// values (ADX, BB width, EMA slope, level-1 touch) per coin against the live genotype's
/// thresholds, so "no setup fired" can be confirmed against real numbers instead of trusted blind.
/// Read-only — no orders.
/// </summary>
static class HyperliquidGridDiag
{
    public static async Task Run()
    {
        Console.WriteLine("=== Gravity-gen2 | HYPERLIQUID GRID DIAG (candle-routing + entry-gate check) ===\n");

        using var client = new HyperliquidClient();
        if (!await client.IsReadyAsync())
        {
            Console.WriteLine($"[ERROR] Hyperliquid bridge not reachable at {HyperliquidClient.DefaultBaseUrl}");
            return;
        }

        var gridVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridG = gridVariants.Length > 0 ? gridVariants[0].Genotype : null;
        if (gridG == null)
        {
            Console.WriteLine("No grid genotype found.");
            return;
        }
        Console.WriteLine($"Grid genotype: {gridG}\n");

        var coins = Config.BacktestCoins.ToList();
        var universe = await client.FetchUniverseAsync();
        if (universe.TryGetValue("perpetuals", out var perpsObj) && perpsObj is JsonElement { ValueKind: JsonValueKind.Array } perpsEl)
        {
            var hlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in perpsEl.EnumerateArray())
                if (p.TryGetProperty("name", out var nameEl))
                    hlNames.Add(nameEl.GetString() ?? "");
            coins = coins.Where(c => hlNames.Contains(HyperliquidClient.NormalizeToHyperliquidCoin(c))).ToList();
        }
        Console.WriteLine($"Checking {coins.Count} resolvable coins...\n");

        Console.WriteLine($"{"Coin",-14} {"m15#",6} {"h1#",6} {"LastBar",-17} {"AgeMin",7}  {"ADX",6} {"<Thr",5}  {"BBW%",6} {"<Thr",5}  {"Slope%",7} {">=Thr",5}  L1Touch  Active");
        var sem = new SemaphoreSlim(4);
        var lockObj = new object();
        int staleCount = 0, emptyCount = 0, activeCount = 0;

        var tasks = coins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15List = await client.FetchH1CandlesAsync(sym, batches: HyperliquidClient.LiveBatches);
                if (m15List.Count == 0)
                {
                    lock (lockObj) { Console.WriteLine($"{sym,-14} EMPTY — no candles returned at all"); emptyCount++; }
                    return;
                }
                var m15 = m15List.ToArray();
                var h1 = FadeShortSimulator.AggregateCandles(m15, 4);
                if (h1.Length < 20)
                {
                    lock (lockObj) { Console.WriteLine($"{sym,-14} {m15.Length,6} {h1.Length,6}  too short for indicators"); }
                    return;
                }

                var closes = h1.Select(c => c.Close).ToArray();
                var highs  = h1.Select(c => c.High).ToArray();
                var lows   = h1.Select(c => c.Low).ToArray();

                var adxArr = Trend.Adx(highs, lows, closes, 14);
                var bbwArr = Volatility.BbWidth(closes, gridG.BbPeriod);
                var emaArr = Trend.Ema(closes, gridG.EmaPeriod);

                int last = h1.Length - 1;
                double adxNow = adxArr[last];
                double bbwNow = bbwArr[last];
                int slopeLookback = gridG.SlopeLookback > 0 ? gridG.SlopeLookback : 20;
                int slopeRef = Math.Max(0, last - slopeLookback);
                double emaSlope = emaArr[slopeRef] > 1e-10 ? (emaArr[last] - emaArr[slopeRef]) / emaArr[slopeRef] * 100.0 : 0;

                bool adxOk = adxNow < gridG.AdxThreshold;
                bool bbwOk = bbwNow < gridG.BbWidthMaxPct;
                bool slopeOk = emaSlope >= gridG.SlopeThreshold * 100.0;

                double atrNow = closes.Length > 0 ? Math.Abs(highs[last] - lows[last]) : 0; // rough, display only
                double proposedAnchor = emaArr[last];
                double level1Price = proposedAnchor - gridG.GridStepAtrMult * atrNow;
                bool level1Touch = lows[last] <= level1Price;

                var st = GridSimulator.GetGridTradeState(gridG, h1);

                var lastBarTime = h1[last].Time;
                double ageMin = (DateTime.UtcNow - lastBarTime).TotalMinutes;
                bool stale = ageMin > 180; // more than 3h old is suspicious for a 15m-fed h1 series

                lock (lockObj)
                {
                    if (stale) staleCount++;
                    if (st.Active) activeCount++;
                    Console.WriteLine($"{sym,-14} {m15.Length,6} {h1.Length,6} {lastBarTime:yyyy-MM-dd HH:mm} {ageMin,7:F0}  " +
                        $"{adxNow,6:F1} {(adxOk ? "yes" : "no"),5}  {bbwNow,6:F2} {(bbwOk ? "yes" : "no"),5}  " +
                        $"{emaSlope,7:F2} {(slopeOk ? "yes" : "no"),5}  {(level1Touch ? "yes" : "no"),7}  {(st.Active ? "ACTIVE" : "no")}" +
                        $"{(stale ? "  [STALE DATA]" : "")}");
                }
            }
            catch (Exception ex)
            {
                lock (lockObj) Console.WriteLine($"{sym,-14} ERROR: {ex.Message}");
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        Console.WriteLine($"\nSummary: {coins.Count} coins checked, {emptyCount} returned no data, {staleCount} stale (>3h old last bar), {activeCount} currently Grid-active.");
    }
}
