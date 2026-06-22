namespace TradingGA;

// Ensemble market regime classifier — replaces the per-strategy hard-coded EMA slope rule
// with a multi-signal voting system that produces a regime label + confidence score.
//
// Regime taxonomy:
//   Bull     — sustained uptrend, EMA stack aligned, ADX trending, momentum positive
//   Bear     — sustained downtrend, EMA stack inverted, momentum negative
//   Ranging  — low ADX, price oscillating around EMAs, no clear directional bias
//   HighVol  — ATR spike >2× historical average, regardless of trend (crash/blow-off)
//
// Confidence (0–1): fraction of the weighted vote that agrees on the winning regime.
// Low confidence = conflicting signals = reduce position sizes.
//
// Features() returns a normalised feature vector suitable as MLP/XGBoost input later —
// the same signals the voting uses, just in numeric form.
public enum MarketRegime { Bull, Bear, Ranging, HighVol }

// One bar of the pre-computed regime series produced by ClassifySeriesWithDuration.
public record RegimeBar(DateTime Time, MarketRegime Regime, double Confidence, int Duration);

public static class RegimeClassifier
{
    private const int Warmup = 220; // bars needed before any classification is valid

    // Classify the regime at the last bar of h1.
    public static (MarketRegime Regime, double Confidence) Classify(ReadOnlySpan<Candle> h1)
    {
        if (h1.Length < Warmup) return (MarketRegime.Ranging, 0.0);
        var f = ComputeFeatures(h1);

        // ── High-volatility override — checked first ──────────────────────────
        // When ATR spikes far above its 100-bar mean the market is in a regime
        // where directional bets are unreliable (crash, blow-off, flash spike).
        if (f.AtrRatio > 2.5)
        {
            double conf = Math.Min(1.0, (f.AtrRatio - 2.5) / 1.5);
            return (MarketRegime.HighVol, conf);
        }

        // ── Weighted vote across four signal groups ───────────────────────────
        double bullScore = 0, bearScore = 0, rangingScore = 0;

        // 1. EMA stack alignment (weight=3): the most reliable long-horizon signal.
        //    Full stack: EMA20 > EMA50 > EMA200 = unambiguous bull.
        if (f.EmaStack == 3)       bullScore   += 3.0;
        else if (f.EmaStack == -3) bearScore   += 3.0;
        else if (f.EmaStack == 1)  bullScore   += 1.0;  // partial
        else if (f.EmaStack == -1) bearScore   += 1.0;
        else                       rangingScore += 1.5;

        // 2. EMA50 slope (weight=1.5): medium-term momentum direction.
        if      (f.Slope50 >  0.002) bullScore   += 1.5;
        else if (f.Slope50 < -0.002) bearScore   += 1.5;
        else                         rangingScore += 1.0;

        // 3. ADX level (weight=1): trend strength gate.
        if (f.Adx > 25)
        {
            // Trending — reinforce whichever direction the EMA says
            if (f.PriceVsEma50 > 0) bullScore   += 1.0;
            else                     bearScore   += 1.0;
        }
        else if (f.Adx < 18)         rangingScore += 2.0;  // clearly non-trending

        // 4. 20-bar momentum (weight=0.5): short-term confirmation.
        if      (f.Momentum20 >  0.04) bullScore   += 0.5;
        else if (f.Momentum20 < -0.04) bearScore   += 0.5;

        // 5. ATR moderate elevation — slightly trending, not crash (weight=0.5).
        if (f.AtrRatio > 1.5) bearScore += 0.5; // elevated vol in non-HighVol usually = sell pressure

        // ── Determine winner ─────────────────────────────────────────────────
        double total = bullScore + bearScore + rangingScore;
        if (total < 0.5) return (MarketRegime.Ranging, 0.0);

        if (bullScore >= bearScore && bullScore >= rangingScore)
            return (MarketRegime.Bull, bullScore / total);
        if (bearScore >= bullScore && bearScore >= rangingScore)
            return (MarketRegime.Bear, bearScore / total);
        return (MarketRegime.Ranging, rangingScore / total);
    }

    // Classify every bar in the series. Returns regime per bar (index < Warmup → Ranging).
    // NOTE: O(n²) — fine for diagnostics, not suitable for hot paths. Use ClassifySeriesWithDuration for training.
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

    // O(n) full-series classifier with consecutive-regime duration counter.
    // Computes all indicator arrays once, then classifies each bar in O(1).
    // Duration resets to 1 on every regime change — so duration=200 means the
    // current regime has been active for 200 consecutive h1 bars (~8 days).
    public static RegimeBar[] ClassifySeriesWithDuration(ReadOnlySpan<Candle> h1)
    {
        int n = h1.Length;
        if (n == 0) return [];

        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);

        var ema20  = Indicators.Ema(closes, 20);
        var ema50  = Indicators.Ema(closes, 50);
        var ema200 = Indicators.Ema(closes, 200);
        var atr14  = Indicators.Atr(highs, lows, closes, 14);
        var atr100 = Indicators.Atr(highs, lows, closes, 100);
        var adx    = Indicators.Adx(highs, lows, closes, 14);

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

    // Single-bar classification using pre-computed indicator arrays (used by ClassifySeriesWithDuration).
    // Mirrors the voting logic in Classify() exactly so both paths stay consistent.
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

        double total = bull + bear + ranging;
        if (total < 0.5) return (MarketRegime.Ranging, 0.0);

        if (bull >= bear && bull >= ranging)   return (MarketRegime.Bull,    bull    / total);
        if (bear >= bull && bear >= ranging)   return (MarketRegime.Bear,    bear    / total);
        return                                        (MarketRegime.Ranging, ranging / total);
    }

    // Numeric feature vector for ML consumption.
    // All values are normalised so an MLP or XGBoost can use them without scaling.
    //   [0] (price − EMA20) / EMA20          trend position (short)
    //   [1] (price − EMA50) / EMA50          trend position (medium)
    //   [2] (price − EMA200) / EMA200        trend position (long)
    //   [3] EMA50 slope (20-bar return)       medium trend momentum
    //   [4] ATR14 / ATR100                   vol regime ratio
    //   [5] ADX14 / 50                       normalised trend strength
    //   [6] 20-bar price momentum             short momentum
    //   [7] (EMA20 − EMA50) / EMA50          EMA separation
    //   [8] (EMA50 − EMA200) / EMA200        EMA separation (long)
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

    // ── Private ───────────────────────────────────────────────────────────────

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

        var ema20Arr  = Indicators.Ema(closes, 20);
        var ema50Arr  = Indicators.Ema(closes, 50);
        var ema200Arr = Indicators.Ema(closes, 200);
        var atr14Arr  = Indicators.Atr(highs, lows, closes, 14);
        var atr100Arr = Indicators.Atr(highs, lows, closes, 100);
        var adxArr    = Indicators.Adx(highs, lows, closes, 14);

        double price   = closes[n - 1];
        double ema20   = ema20Arr[n - 1];
        double ema50   = ema50Arr[n - 1];
        double ema200  = ema200Arr[n - 1];
        double atr14   = atr14Arr[n - 1];
        double atr100  = atr100Arr[n - 1];
        double adx     = adxArr[n - 1];

        // EMA50 slope: 20-bar return of EMA50 (normalised)
        double ema50Prev = ema50Arr[Math.Max(0, n - 21)];
        double slope50   = ema50Prev > 1e-10 ? (ema50 - ema50Prev) / ema50Prev : 0;

        // 20-bar momentum
        double pricePrev20 = closes[Math.Max(0, n - 21)];
        double momentum20  = pricePrev20 > 1e-10 ? (price - pricePrev20) / pricePrev20 : 0;

        // ATR vol ratio
        double atrRatio = atr100 > 1e-10 ? atr14 / atr100 : 1.0;

        // EMA relative positions (signed %)
        double priceVsEma20  = ema20  > 1e-10 ? (price - ema20)  / ema20  : 0;
        double priceVsEma50  = ema50  > 1e-10 ? (price - ema50)  / ema50  : 0;
        double priceVsEma200 = ema200 > 1e-10 ? (price - ema200) / ema200 : 0;
        double ema20VsEma50  = ema50  > 1e-10 ? (ema20 - ema50)  / ema50  : 0;
        double ema50VsEma200 = ema200 > 1e-10 ? (ema50 - ema200) / ema200 : 0;

        // EMA stack: +3 = fully bullish aligned, -3 = fully bearish, others partial
        int stack = 0;
        if (price > ema20)  stack++; else stack--;
        if (ema20 > ema50)  stack++; else stack--;
        if (ema50 > ema200) stack++; else stack--;

        return new(priceVsEma20, priceVsEma50, priceVsEma200,
                   slope50, atrRatio, adx, momentum20,
                   ema20VsEma50, ema50VsEma200, stack);
    }
}
