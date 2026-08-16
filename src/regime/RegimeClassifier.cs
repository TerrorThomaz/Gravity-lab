namespace TradingGA;

// Multi-signal weighted-vote regime classifier. Produces Bull/Bear/Ranging/HighVol + confidence (0–1).
// ClassifySeriesWithDuration is O(n) with a per-bar consecutive-regime duration counter.
public enum MarketRegime { Bull, Bear, Ranging, HighVol }

// One bar of the pre-computed regime series produced by ClassifySeriesWithDuration.
public record RegimeBar(DateTime Time, MarketRegime Regime, double Confidence, int Duration);

// Tags trade timestamps with the BTC regime active at that moment, for per-regime held-out bucketing.
public static class RegimeBarLookup
{
    public static MarketRegime[] TagRegimes(RegimeBar[] series, IReadOnlyList<DateTime> times)
    {
        var tags = new MarketRegime[times.Count];
        if (series.Length == 0) return tags;
        for (int i = 0; i < times.Count; i++)
        {
            long t = times[i].Ticks;
            if (t <= series[0].Time.Ticks) { tags[i] = series[0].Regime; continue; }
            if (t >= series[^1].Time.Ticks) { tags[i] = series[^1].Regime; continue; }
            int lo = 0, hi = series.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (series[mid].Time.Ticks <= t) lo = mid; else hi = mid - 1;
            }
            tags[i] = series[lo].Regime;
        }
        return tags;
    }
}

public static class RegimeClassifier
{
    // Public: PredictiveDistribution conditions on the same warmup the classifier uses.
    public const int Warmup = 220;

    // Classify the regime at the last bar of h1.
    public static (MarketRegime Regime, double Confidence) Classify(ReadOnlySpan<Candle> h1)
    {
        if (h1.Length < Warmup) return (MarketRegime.Ranging, 0.0);
        var f = ComputeFeatures(h1);

        // High-vol override: ATR far above its 100-bar mean → directional bets unreliable.
        if (f.AtrRatio > 2.5)
        {
            double conf = Math.Min(1.0, (f.AtrRatio - 2.5) / 1.5);
            return (MarketRegime.HighVol, conf);
        }

        double bullScore = 0, bearScore = 0, rangingScore = 0;

        // 1. EMA stack (weight=3): EMA20 > EMA50 > EMA200 = bull.
        if (f.EmaStack == 3)       bullScore   += 3.0;
        else if (f.EmaStack == -3) bearScore   += 3.0;
        else if (f.EmaStack == 1)  bullScore   += 1.0;  // partial
        else if (f.EmaStack == -1) bearScore   += 1.0;
        else                       rangingScore += 1.5;

        // 2. EMA50 slope (weight=1.5)
        if      (f.Slope50 >  0.002) bullScore   += 1.5;
        else if (f.Slope50 < -0.002) bearScore   += 1.5;
        else                         rangingScore += 1.0;

        // 3. ADX (weight=1)
        if (f.Adx > 25)
        {
            // Trending — reinforce EMA direction
            if (f.PriceVsEma50 > 0) bullScore   += 1.0;
            else                     bearScore   += 1.0;
        }
        else if (f.Adx < 18)         rangingScore += 2.0;  // clearly non-trending

        // 4. 20-bar momentum (weight=0.5)
        if      (f.Momentum20 >  0.04) bullScore   += 0.5;
        else if (f.Momentum20 < -0.04) bearScore   += 0.5;

        // 5. ATR moderate elevation (weight=0.5)
        if (f.AtrRatio > 1.5) bearScore += 0.5;

        if (f.AtrRatio < 0.8 && Math.Abs(f.Slope50) < 0.001)
            rangingScore += 1.5;

        // Determine winner
        double total = bullScore + bearScore + rangingScore;
        if (total < 0.5) return (MarketRegime.Ranging, 0.0);

        if (bullScore >= bearScore && bullScore >= rangingScore)
            return (MarketRegime.Bull, bullScore / total);
        if (bearScore >= bullScore && bearScore >= rangingScore)
            return (MarketRegime.Bear, bearScore / total);
        return (MarketRegime.Ranging, rangingScore / total);
    }

    // O(n²) per-bar classification. Use ClassifySeriesWithDuration for training.
    public static (MarketRegime Regime, double Confidence)[] ClassifySeries(ReadOnlySpan<Candle> h1)
    {
        int n      = h1.Length;
        var result = new (MarketRegime, double)[n];
        for (int i = 0; i < Math.Min(Warmup, n); i++)
            result[i] = (MarketRegime.Ranging, 0.0);

        for (int i = Warmup; i < n; i++)
            result[i] = Classify(h1[..( i + 1)]);  // slice; fine for backtest, not hot-path

        return result;
    }

    // O(n) full-series classifier. Computes indicators once, classifies each bar in O(1).
    // Duration = consecutive h1 bars in the current regime.
    public static RegimeBar[] ClassifySeriesWithDuration(ReadOnlySpan<Candle> h1)
    {
        int n = h1.Length;
        if (n == 0) return [];

        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);

        var ema20  = Trend.Ema(closes, 20);
        var ema50  = Trend.Ema(closes, 50);
        var ema200 = Trend.Ema(closes, 200);
        var atr14  = Volatility.Atr(highs, lows, closes, 14);
        var atr100 = Volatility.Atr(highs, lows, closes, 100);
        var adx    = Trend.Adx(highs, lows, closes, 14);

        var result       = new RegimeBar[n];
        var prevRegime   = MarketRegime.Ranging;
        int duration     = 0;

        for (int i = 0; i < n; i++)
        {
            if (i < Warmup)
            {
                result[i] = new(h1[i].Time, MarketRegime.Ranging, 0.0, 0);
                continue;
            }

            var (regime, conf) = ClassifyBar(i, closes, ema20, ema50, ema200, atr14, atr100, adx);

            duration  = regime == prevRegime ? duration + 1 : 1;
            prevRegime = regime;

            result[i] = new(h1[i].Time, regime, conf, duration);
        }

        return result;
    }

    // Single-bar classification from pre-computed arrays. Mirrors Classify() voting exactly.
    private static (MarketRegime, double) ClassifyBar(
        int i, double[] closes,
        double[] ema20, double[] ema50, double[] ema200,
        double[] atr14, double[] atr100, double[] adx)
    {
        double price    = closes[i];
        double e20      = ema20[i];
        double e50      = ema50[i];
        double e200     = ema200[i];
        double atrRatio = atr100[i] > 1e-10 ? atr14[i] / atr100[i] : 1.0;

        if (atrRatio > 2.5)
            return (MarketRegime.HighVol, Math.Min(1.0, (atrRatio - 2.5) / 1.5));

        double e50Prev = ema50[Math.Max(0, i - 20)];
        double slope50 = e50Prev > 1e-10 ? (e50 - e50Prev) / e50Prev : 0;

        double pPrev20 = closes[Math.Max(0, i - 20)];
        double mom20   = pPrev20 > 1e-10 ? (price - pPrev20) / pPrev20 : 0;

        double pvE50 = e50 > 1e-10 ? (price - e50) / e50 : 0;

        int stack = 0;
        if (price > e20) stack++; else stack--;
        if (e20   > e50) stack++; else stack--;
        if (e50  > e200) stack++; else stack--;

        double bull = 0, bear = 0, ranging = 0;

        if      (stack ==  3) bull    += 3.0;
        else if (stack == -3) bear    += 3.0;
        else if (stack ==  1) bull    += 1.0;
        else if (stack == -1) bear    += 1.0;
        else                  ranging += 1.5;

        if      (slope50 >  0.002) bull    += 1.5;
        else if (slope50 < -0.002) bear    += 1.5;
        else                       ranging += 1.0;

        double adxVal = adx[i];
        if (adxVal > 25)       { if (pvE50 > 0) bull += 1.0; else bear += 1.0; }
        else if (adxVal < 18)  ranging += 2.0;

        if      (mom20 >  0.04) bull += 0.5;
        else if (mom20 < -0.04) bear += 0.5;

        if (atrRatio > 1.5) bear += 0.5;

        if (atrRatio < 0.8 && Math.Abs(slope50) < 0.001)
            ranging += 1.5;

        double total = bull + bear + ranging;
        if (total < 0.5) return (MarketRegime.Ranging, 0.0);

        if (bull >= bear && bull >= ranging)   return (MarketRegime.Bull,    bull    / total);
        if (bear >= bull && bear >= ranging)   return (MarketRegime.Bear,    bear    / total);
        return                                        (MarketRegime.Ranging, ranging / total);
    }

    // Normalised 9-element feature vector for ML. Same signals as the vote, numeric form.
    public static double[] Features(ReadOnlySpan<Candle> h1)
    {
        if (h1.Length < Warmup) return new double[9];
        var f = ComputeFeatures(h1);
        return
        [
            Math.Clamp(f.PriceVsEma20,  -0.5, 0.5),
            Math.Clamp(f.PriceVsEma50,  -0.5, 0.5),
            Math.Clamp(f.PriceVsEma200, -0.5, 0.5),
            Math.Clamp(f.Slope50,       -0.10, 0.10),
            Math.Clamp(f.AtrRatio,       0.0,  5.0) / 5.0,
            Math.Clamp(f.Adx,            0.0, 60.0) / 60.0,
            Math.Clamp(f.Momentum20,    -0.5, 0.5),
            Math.Clamp(f.Ema20VsEma50,  -0.3, 0.3),
            Math.Clamp(f.Ema50VsEma200, -0.5, 0.5),
        ];
    }

    private record FeatureSet(
        double PriceVsEma20, double PriceVsEma50, double PriceVsEma200,
        double Slope50, double AtrRatio, double Adx,
        double Momentum20, double Ema20VsEma50, double Ema50VsEma200,
        int EmaStack);

    private static FeatureSet ComputeFeatures(ReadOnlySpan<Candle> h1)
    {
        int n = h1.Length;
        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);

        var ema20Arr  = Trend.Ema(closes, 20);
        var ema50Arr  = Trend.Ema(closes, 50);
        var ema200Arr = Trend.Ema(closes, 200);
        var atr14Arr  = Volatility.Atr(highs, lows, closes, 14);
        var atr100Arr = Volatility.Atr(highs, lows, closes, 100);
        var adxArr    = Trend.Adx(highs, lows, closes, 14);

        double price   = closes[n - 1];
        double ema20   = ema20Arr[n - 1];
        double ema50   = ema50Arr[n - 1];
        double ema200  = ema200Arr[n - 1];
        double atr14   = atr14Arr[n - 1];
        double atr100  = atr100Arr[n - 1];
        double adx     = adxArr[n - 1];


        double ema50Prev = ema50Arr[Math.Max(0, n - 21)];
        double slope50   = ema50Prev > 1e-10 ? (ema50 - ema50Prev) / ema50Prev : 0;


        double pricePrev20 = closes[Math.Max(0, n - 21)];
        double momentum20  = pricePrev20 > 1e-10 ? (price - pricePrev20) / pricePrev20 : 0;


        double atrRatio = atr100 > 1e-10 ? atr14 / atr100 : 1.0;


        double priceVsEma20  = ema20  > 1e-10 ? (price - ema20)  / ema20  : 0;
        double priceVsEma50  = ema50  > 1e-10 ? (price - ema50)  / ema50  : 0;
        double priceVsEma200 = ema200 > 1e-10 ? (price - ema200) / ema200 : 0;
        double ema20VsEma50  = ema50  > 1e-10 ? (ema20 - ema50)  / ema50  : 0;
        double ema50VsEma200 = ema200 > 1e-10 ? (ema50 - ema200) / ema200 : 0;


        int stack = 0;
        if (price > ema20)  stack++; else stack--;
        if (ema20 > ema50)  stack++; else stack--;
        if (ema50 > ema200) stack++; else stack--;

        return new(priceVsEma20, priceVsEma50, priceVsEma200,
                   slope50, atrRatio, adx, momentum20,
                   ema20VsEma50, ema50VsEma200, stack);
    }
}
