namespace TradingGA;

internal static class Volatility
{
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

    internal static double[] BbWidth(double[] closes, int period)
    {
        var width = new double[closes.Length];
        for (int i = period - 1; i < closes.Length; i++)
        {
            double s = 0, sq = 0;
            for (int j = i - period + 1; j <= i; j++) { s += closes[j]; sq += closes[j] * closes[j]; }
            double mean = s / period;
            double std  = Math.Sqrt(Math.Max(0, sq / period - mean * mean));
            width[i] = mean > 1e-10 ? 4.0 * std / mean * 100.0 : 0;
        }
        return width;
    }

    internal static (double[] upper, double[] mid, double[] lower) BollingerBands(
        double[] closes, int period, double stdDevs)
    {
        int n = closes.Length;
        var upper = new double[n];
        var mid   = new double[n];
        var lower = new double[n];
        for (int i = period - 1; i < n; i++)
        {
            double s = 0, sq = 0;
            for (int j = i - period + 1; j <= i; j++) { s += closes[j]; sq += closes[j] * closes[j]; }
            double mean = s / period;
            double std  = Math.Sqrt(Math.Max(0, sq / period - mean * mean));
            mid[i]   = mean;
            upper[i] = mean + stdDevs * std;
            lower[i] = mean - stdDevs * std;
        }
        return (upper, mid, lower);
    }

    internal static double[] AtrRatio(
        double[] highs, double[] lows, double[] closes, int shortPeriod, int longPeriod)
    {
        var shortAtr = Atr(highs, lows, closes, shortPeriod);
        var longAtr  = Atr(highs, lows, closes, longPeriod);
        int n = closes.Length;
        var ratio = new double[n];
        for (int i = 0; i < n; i++)
            ratio[i] = longAtr[i] > 1e-10 ? shortAtr[i] / longAtr[i] : 1.0;
        return ratio;
    }
}
