namespace TradingGA;

// Shared indicator implementations used by FadeShortSimulator and GridSimulator.
// All methods are pure functions on raw price arrays — no state, no allocation beyond the result.
internal static class Indicators
{
    internal static double[] Ema(double[] closes, int period)
    {
        var ema = new double[closes.Length];
        double k = 2.0 / (period + 1);
        ema[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            ema[i] = closes[i] * k + ema[i - 1] * (1 - k);
        return ema;
    }

    // Wilder-smoothed RSI (same as TradingView default).
    // Warmup: first `period` diffs seed avgGain/avgLoss; subsequent bars use Wilder smoothing.
    internal static double[] Rsi(double[] closes, int period)
    {
        var rsi = new double[closes.Length];
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period && i < closes.Length; i++)
        {
            double d = closes[i] - closes[i - 1];
            if (d > 0) avgGain += d; else avgLoss -= d;
        }
        avgGain /= period;
        avgLoss /= period;
        for (int i = period; i < closes.Length; i++)
        {
            if (i > period)
            {
                double d = closes[i] - closes[i - 1];
                avgGain = (avgGain * (period - 1) + Math.Max(d,  0)) / period;
                avgLoss = (avgLoss * (period - 1) + Math.Max(-d, 0)) / period;
            }
            rsi[i] = avgLoss < 1e-12 ? 100.0 : 100.0 - 100.0 / (1.0 + avgGain / avgLoss);
        }
        return rsi;
    }

    // Wilder-smoothed ATR (14-period standard).
    internal static double[] Atr(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var tr  = new double[n];
        var atr = new double[n];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < n; i++)
            tr[i] = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i]  - closes[i - 1])));
        int init = Math.Min(period, n);
        double sum = 0;
        for (int i = 0; i < init; i++) sum += tr[i];
        atr[init - 1] = sum / init;
        for (int i = init; i < n; i++)
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
        return atr;
    }

    // Wilder-smoothed ADX.
    internal static double[] Adx(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var tr   = new double[n];
        var pDm  = new double[n];
        var mDm  = new double[n];
        for (int i = 1; i < n; i++)
        {
            double hd = highs[i] - highs[i - 1], ld = lows[i - 1] - lows[i];
            tr[i]  = Math.Max(highs[i] - lows[i],
                     Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                              Math.Abs(lows[i]  - closes[i - 1])));
            pDm[i] = hd > ld && hd > 0 ? hd : 0;
            mDm[i] = ld > hd && ld > 0 ? ld : 0;
        }
        var sTr  = new double[n]; var sPDm = new double[n]; var sMDm = new double[n];
        var dx   = new double[n]; var adx  = new double[n];
        if (period >= n) return adx;
        for (int i = 1; i <= period; i++) { sTr[period] += tr[i]; sPDm[period] += pDm[i]; sMDm[period] += mDm[i]; }
        for (int i = period + 1; i < n; i++)
        {
            sTr[i]  = sTr[i - 1]  - sTr[i - 1]  / period + tr[i];
            sPDm[i] = sPDm[i - 1] - sPDm[i - 1] / period + pDm[i];
            sMDm[i] = sMDm[i - 1] - sMDm[i - 1] / period + mDm[i];
            if (sTr[i] < 1e-10) continue;
            double pDi = 100.0 * sPDm[i] / sTr[i], mDi = 100.0 * sMDm[i] / sTr[i];
            double ds  = pDi + mDi;
            dx[i] = ds > 1e-10 ? 100.0 * Math.Abs(pDi - mDi) / ds : 0;
        }
        int adxStart = period * 2;
        if (adxStart >= n) return adx;
        double sumDx = 0;
        for (int i = period + 1; i <= adxStart && i < n; i++) sumDx += dx[i];
        adx[adxStart] = sumDx / period;
        for (int i = adxStart + 1; i < n; i++)
            adx[i] = (adx[i - 1] * (period - 1) + dx[i]) / period;
        return adx;
    }

    // In-place EMA write — caller supplies a rented buffer (length ≥ closes.Length).
    internal static void EmaInto(double[] closes, int period, double[] output)
    {
        double k = 2.0 / (period + 1);
        output[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            output[i] = closes[i] * k + output[i - 1] * (1 - k);
    }

    // Bollinger Band width = (Upper − Lower) / Middle × 100 = 4σ / SMA × 100.
    // Low value → compressed range; high value → expanding / trending.
    internal static double[] BbWidth(double[] closes, int period)
    {
        var width = new double[closes.Length];
        for (int i = period - 1; i < closes.Length; i++)
        {
            double sum = 0, sumSq = 0;
            for (int j = i - period + 1; j <= i; j++) { sum += closes[j]; sumSq += closes[j] * closes[j]; }
            double mean     = sum / period;
            double variance = sumSq / period - mean * mean;
            double std      = variance > 0 ? Math.Sqrt(variance) : 0;
            width[i] = mean > 1e-10 ? 4.0 * std / mean * 100.0 : 0;
        }
        return width;
    }
}
