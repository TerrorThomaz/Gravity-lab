namespace TradingGA;

// Boolean signal arrays derived by combining raw indicators.
// All array-returning functions take precomputed arrays — no raw candle scanning.
// SwingHighLookback / SwingLowLookback are per-bar value-tuple returns (called at entry only).
internal static class Signals
{
    // true at bar i when: swing-high RSI ≥ rsiOverbought AND current RSI ≤ swingHighRSI − divThreshold.
    // Scans closes (not highs) for the swing high — matches FadeShortSimulator and SwingSimulator.
    internal static bool[] BearishDivergence(
        double[] rsi, double[] closes, int lookback, double rsiOverbought, double divThreshold)
    {
        int n = rsi.Length;
        var result = new bool[n];
        for (int i = lookback; i < n; i++)
        {
            int    lbStart   = Math.Max(0, i - lookback);
            double swingHigh = closes[lbStart];
            int    highIdx   = lbStart;
            for (int j = lbStart + 1; j < i; j++)
                if (closes[j] > swingHigh) { swingHigh = closes[j]; highIdx = j; }
            double rsiAtHigh = rsi[highIdx];
            result[i] = rsiAtHigh >= rsiOverbought && rsi[i] <= rsiAtHigh - divThreshold;
        }
        return result;
    }

    // true at bar i when: swing-low RSI ≤ rsiOversold AND current RSI ≥ swingLowRSI + divThreshold.
    // Scans closes for the swing low — matches FadeLongSimulator and SwingLongSimulator.
    internal static bool[] BullishDivergence(
        double[] rsi, double[] closes, int lookback, double rsiOversold, double divThreshold)
    {
        int n = rsi.Length;
        var result = new bool[n];
        for (int i = lookback; i < n; i++)
        {
            int    lbStart  = Math.Max(0, i - lookback);
            double swingLow = closes[lbStart];
            int    lowIdx   = lbStart;
            for (int j = lbStart + 1; j < i; j++)
                if (closes[j] < swingLow) { swingLow = closes[j]; lowIdx = j; }
            double rsiAtLow = rsi[lowIdx];
            result[i] = rsiAtLow <= rsiOversold && rsi[i] >= rsiAtLow + divThreshold;
        }
        return result;
    }

    // closes[i] < lows[i-1] — bearish structure break.
    internal static bool[] BearishBoS(double[] closes, double[] lows)
    {
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 1; i < n; i++)
            result[i] = closes[i] < lows[i - 1];
        return result;
    }

    // closes[i] > highs[i-1] — bullish structure break.
    internal static bool[] BullishBoS(double[] closes, double[] highs)
    {
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 1; i < n; i++)
            result[i] = closes[i] > highs[i - 1];
        return result;
    }

    // Per-bar: find highest close and its index, and the lowest low in [i-lookback, i).
    internal static (double swingHigh, int highIdx, double recentLow) SwingHighLookback(
        double[] closes, double[] highs, double[] lows, int i, int lookback)
    {
        int    lbStart   = Math.Max(0, i - lookback);
        double swingHigh = closes[lbStart];
        int    highIdx   = lbStart;
        double recentLow = lows[lbStart];
        for (int j = lbStart + 1; j < i; j++)
        {
            if (closes[j] > swingHigh) { swingHigh = closes[j]; highIdx = j; }
            if (lows[j]   < recentLow)   recentLow = lows[j];
        }
        return (swingHigh, highIdx, recentLow);
    }

    // Per-bar: find lowest close and its index, and the highest high in [i-lookback, i).
    internal static (double swingLow, int lowIdx, double recentHigh) SwingLowLookback(
        double[] closes, double[] highs, double[] lows, int i, int lookback)
    {
        int    lbStart    = Math.Max(0, i - lookback);
        double swingLow   = closes[lbStart];
        int    lowIdx     = lbStart;
        double recentHigh = highs[lbStart];
        for (int j = lbStart + 1; j < i; j++)
        {
            if (closes[j] < swingLow)  { swingLow = closes[j]; lowIdx = j; }
            if (highs[j]  > recentHigh)  recentHigh = highs[j];
        }
        return (swingLow, lowIdx, recentHigh);
    }

    // (max(upper[i-lookback..i]) − min(lower[i-lookback..i])) / atr[i] ≥ minAtrMult.
    // Pass closes as both upper and lower when scanning for close-based swings.
    // Pass closes/lows when scanning rally size (high close to recent low).
    internal static bool[] MinMoveFilter(
        double[] upper, double[] lower, double[] atr, int lookback, double minAtrMult)
    {
        int n = upper.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int    start   = Math.Max(0, i - lookback);
            double maxUp   = upper[start];
            double minLow  = lower[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (upper[j] > maxUp)  maxUp  = upper[j];
                if (lower[j] < minLow) minLow = lower[j];
            }
            double atrNow = atr[i] > 1e-10 ? atr[i] : 1e-10;
            result[i] = (maxUp - minLow) / atrNow >= minAtrMult;
        }
        return result;
    }

    // (ema[i] − ema[i−lookback]) / ema[i−lookback]. Positive = rising.
    internal static double[] EmaSlope(double[] ema, int lookback)
    {
        int n = ema.Length;
        var slope = new double[n];
        for (int i = lookback; i < n; i++)
        {
            double prev = ema[i - lookback];
            slope[i] = prev > 1e-10 ? (ema[i] - prev) / prev : 0;
        }
        return slope;
    }

    // adx[i] ≥ threshold AND closes[i] > ema[i].
    internal static bool[] AdxTrend(double[] adx, double[] closes, double[] ema, double threshold)
    {
        int n = adx.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
            result[i] = adx[i] >= threshold && closes[i] > ema[i];
        return result;
    }

    // Ema(fast)[i] > Ema(mid)[i] > Ema(slow)[i] — bullish EMA stack.
    internal static bool[] EmaStack(double[] closes, int fastPeriod, int midPeriod, int slowPeriod)
    {
        var fast = Trend.Ema(closes, fastPeriod);
        var mid  = Trend.Ema(closes, midPeriod);
        var slow = Trend.Ema(closes, slowPeriod);
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
            result[i] = fast[i] > mid[i] && mid[i] > slow[i];
        return result;
    }

    // AtrRatio(short, long)[i] > threshold — convenience wrapper for HighVol detection.
    internal static bool[] AtrExpansion(
        double[] highs, double[] lows, double[] closes,
        int shortPeriod, int longPeriod, double threshold)
    {
        var ratio = Volatility.AtrRatio(highs, lows, closes, shortPeriod, longPeriod);
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
            result[i] = ratio[i] > threshold;
        return result;
    }
}
