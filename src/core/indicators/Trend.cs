namespace TradingGA;

internal static class Trend
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

    internal static void EmaInto(double[] closes, int period, double[] output)
    {
        double k = 2.0 / (period + 1);
        output[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            output[i] = closes[i] * k + output[i - 1] * (1 - k);
    }

    internal static double[] Sma(double[] closes, int period)
    {
        var sma = new double[closes.Length];
        double sum = 0;
        for (int i = 0; i < closes.Length; i++)
        {
            sum += closes[i];
            if (i >= period) sum -= closes[i - period];
            sma[i] = i >= period - 1 ? sum / Math.Min(i + 1, period) : sum / (i + 1);
        }
        return sma;
    }

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

    internal static (double[] macd, double[] sig, double[] hist) Macd(
        double[] closes, int fast, int slow, int signal)
    {
        var fastEma = Ema(closes, fast);
        var slowEma = Ema(closes, slow);
        var macd    = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            macd[i] = fastEma[i] - slowEma[i];
        var sig  = Ema(macd, signal);
        var hist = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            hist[i] = macd[i] - sig[i];
        return (macd, sig, hist);
    }

    internal static (double[] upper, double[] lower) DonchianChannel(
        double[] highs, double[] lows, int period)
    {
        int n = highs.Length;
        var upper = new double[n];
        var lower = new double[n];
        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - period + 1);
            double hi = highs[start], lo = lows[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (highs[j] > hi) hi = highs[j];
                if (lows[j]  < lo) lo = lows[j];
            }
            upper[i] = hi;
            lower[i] = lo;
        }
        return (upper, lower);
    }

    internal static (double[] upper, double[] mid, double[] lower) KeltnerChannel(
        double[] closes, double[] highs, double[] lows, int emaPeriod, int atrPeriod, double atrMult)
    {
        var mid   = Ema(closes, emaPeriod);
        var atr   = Volatility.Atr(highs, lows, closes, atrPeriod);
        int n     = closes.Length;
        var upper = new double[n];
        var lower = new double[n];
        for (int i = 0; i < n; i++)
        {
            upper[i] = mid[i] + atrMult * atr[i];
            lower[i] = mid[i] - atrMult * atr[i];
        }
        return (upper, mid, lower);
    }
}
