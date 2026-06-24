namespace TradingGA;

internal static class Momentum
{
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

    internal static (double[] k, double[] d) Stochastic(
        double[] highs, double[] lows, double[] closes, int kPeriod, int dPeriod)
    {
        int n = closes.Length;
        var k = new double[n];
        for (int i = kPeriod - 1; i < n; i++)
        {
            int    start = i - kPeriod + 1;
            double hi = highs[start], lo = lows[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (highs[j] > hi) hi = highs[j];
                if (lows[j]  < lo) lo = lows[j];
            }
            double range = hi - lo;
            k[i] = range < 1e-10 ? 50.0 : (closes[i] - lo) / range * 100.0;
        }
        var d = Trend.Sma(k, dPeriod);
        return (k, d);
    }

    internal static double[] Cci(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var cci = new double[n];
        for (int i = period - 1; i < n; i++)
        {
            int    start = i - period + 1;
            double sum = 0;
            for (int j = start; j <= i; j++)
                sum += (highs[j] + lows[j] + closes[j]) / 3.0;
            double mean = sum / period;
            double dev  = 0;
            for (int j = start; j <= i; j++)
                dev += Math.Abs((highs[j] + lows[j] + closes[j]) / 3.0 - mean);
            dev /= period;
            cci[i] = dev < 1e-10 ? 0 : (((highs[i] + lows[i] + closes[i]) / 3.0) - mean) / (0.015 * dev);
        }
        return cci;
    }

    internal static double[] Roc(double[] closes, int period)
    {
        int n = closes.Length;
        var roc = new double[n];
        for (int i = period; i < n; i++)
        {
            double prev = closes[i - period];
            roc[i] = prev < 1e-10 ? 0 : (closes[i] - prev) / prev * 100.0;
        }
        return roc;
    }

    internal static double[] NBarMomentum(double[] closes, int period)
    {
        int n = closes.Length;
        var mom = new double[n];
        for (int i = period; i < n; i++)
            mom[i] = closes[i] - closes[i - period];
        return mom;
    }
}
