namespace TradingGA;

// Enriches a flat trade list with per-trade context features for the ExitModifier.
//
// Per-trade (requires the coin's h1 candles):
//   AtrAtEntry      — 14-period ATR at the entry bar
//   AtrRank         — percentile of AtrAtEntry in [bar-252, bar] ATR window (0=low, 1=high vol)
//   MaeAtr          — max adverse excursion in AtrAtEntry units over the hold window
//   MfeAtr          — max favorable excursion in AtrAtEntry units over the hold window
//   LiquidityScore  — volume[entry] / avg(volume[entry-20..entry-1])
//
// Cross-trade (computed across all trades):
//   OpenPositions   — trades opened before this one that haven't closed yet
//   TotalExposure   — sum of CoinConf for all such open trades
//   RecentTradeCount — trades opened in the 168h before this trade
public static class TradeEnricher
{
    public record EnrichedTrade(
        DateTime EntryTime,
        double   Return,
        double   CoinConf,
        string   Strategy,
        string   Symbol,
        TimeSpan HoldDuration,
        double   AtrAtEntry,
        double   AtrRank,
        double   MaeAtr,
        double   MfeAtr,
        double   LiquidityScore,
        int      OpenPositions,
        double   TotalExposure,
        int      RecentTradeCount);

    private static readonly HashSet<string> ShortStrategies = ["swing"];

    public static List<EnrichedTrade> Enrich(
        List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)> rawTrades,
        IReadOnlyDictionary<string, Candle[]> h1Map)
    {
        if (rawTrades.Count == 0) return [];

        var sorted = rawTrades.OrderBy(t => t.Time).ToList();
        var result = new List<EnrichedTrade>(sorted.Count);

        // Precompute per-symbol ATR and time arrays (avoid repeated Indicators.Atr calls)
        var symbolCache = new Dictionary<string, (double[] Atr, double[] Vol, DateTime[] Times, Candle[] H1)>();
        foreach (var sym in sorted.Select(t => t.Symbol).Distinct())
        {
            if (!h1Map.TryGetValue(sym, out var h1)) continue;
            var closes = CandleExt.Closes(h1);
            var highs  = CandleExt.Highs(h1);
            var lows   = CandleExt.Lows(h1);
            symbolCache[sym] = (
                Volatility.Atr(highs, lows, closes, 14),
                h1.Select(c => c.Volume).ToArray(),
                h1.Select(c => c.Time).ToArray(),
                h1);
        }

        var recentWindow = TimeSpan.FromHours(168);

        for (int i = 0; i < sorted.Count; i++)
        {
            var (time, ret, conf, strategy, symbol, hold) = sorted[i];

            double atrAtEntry = 0, atrRank = 0.5, maeAtr = 0, mfeAtr = 0, liqScore = 1.0;

            if (symbolCache.TryGetValue(symbol, out var cache))
            {
                var (atrArr, volArr, timesArr, h1) = cache;

                int bar = Array.BinarySearch(timesArr, time);
                if (bar < 0) bar = ~bar - 1;
                bar = Math.Clamp(bar, 14, h1.Length - 2);

                atrAtEntry = atrArr[bar];

                // ATR percentile rank in 252-bar window
                int rankStart = Math.Max(0, bar - 252);
                int below = 0, rankN = 0;
                for (int k = rankStart; k <= bar; k++)
                {
                    if (atrArr[k] <= atrAtEntry) below++;
                    rankN++;
                }
                atrRank = rankN > 0 ? (double)below / rankN : 0.5;

                // Liquidity score: vol[bar] / avg(vol[bar-20..bar-1])
                int    liqStart = Math.Max(0, bar - 20);
                double volSum   = 0;
                int    liqN     = 0;
                for (int k = liqStart; k < bar; k++) { volSum += volArr[k]; liqN++; }
                double vol20Avg = liqN > 0 ? volSum / liqN : 1.0;
                liqScore = vol20Avg > 1e-10 ? volArr[bar] / vol20Avg : 1.0;

                // MAE/MFE over hold window (h1 bars)
                bool   isShort  = ShortStrategies.Contains(strategy);
                double entryPx  = h1[bar].Close;
                int    holdBars = Math.Max(1, (int)hold.TotalHours);
                double maxFav   = 0, maxAdv = 0;
                for (int k = bar + 1; k < Math.Min(bar + holdBars + 1, h1.Length); k++)
                {
                    double fav = isShort ? entryPx - h1[k].Low  : h1[k].High - entryPx;
                    double adv = isShort ? h1[k].High - entryPx : entryPx - h1[k].Low;
                    if (fav > maxFav) maxFav = fav;
                    if (adv > maxAdv) maxAdv = adv;
                }
                mfeAtr = atrAtEntry > 1e-10 ? maxFav / atrAtEntry : 0;
                maeAtr = atrAtEntry > 1e-10 ? maxAdv / atrAtEntry : 0;
            }

            // Cross-trade fields: O(n²) over trade list — acceptable for thousands of trades
            int    openPos   = 0;
            double totalExp  = 0;
            int    recentCnt = 0;

            for (int j = 0; j < i; j++)
            {
                var other = sorted[j];
                if (other.Time < time && other.Time + other.HoldDuration > time)
                {
                    openPos++;
                    totalExp += other.Conf;
                }
                if (time - other.Time <= recentWindow)
                    recentCnt++;
            }

            result.Add(new EnrichedTrade(
                time, ret, conf, strategy, symbol, hold,
                atrAtEntry, atrRank, maeAtr, mfeAtr, liqScore,
                openPos, totalExp, recentCnt));
        }

        return result;
    }
}
