namespace TradingGA;

/// <summary>
/// One OHLCV candle — fill this from Binance.Net or a CSV.
/// </summary>
public record Candle(
    DateTime Time,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume
);

/// <summary>
/// Splits a candle series into discrete "pump segments" —
/// periods of significant upward movement followed by a BoS.
/// This is what your island model evaluates per segment.
/// </summary>
public static class PumpSegmenter
{
    /// <summary>
    /// Returns slices of candles, each representing one pump event.
    /// A pump starts when price rises more than <pumpThresholdPct>
    /// and ends when a lower high forms (BoS).
    /// </summary>
    public static List<Candle[]> Segment(
        IList<Candle> candles,
        double pumpThresholdPct = 0.5,
        int minSegmentLength = 10,
        int postBosCandleCount = 80)
    {
        var segments = new List<Candle[]>();
        int i = 0;

        while (i < candles.Count - minSegmentLength)
        {
            // Look for start of a pump
            int pumpStart = -1;
            double localLow = candles[i].Close;

            for (int j = i; j < candles.Count - 1; j++)
            {
                double rise = (candles[j].High - localLow) / localLow * 100.0;
                if (rise >= pumpThresholdPct)
                {
                    pumpStart = i;
                    break;
                }
                if (candles[j].Low < localLow) localLow = candles[j].Low;
            }

            if (pumpStart < 0) break;

            // Look for BoS: a lower high after the pump peak
            double peak = candles[pumpStart].High;
            int peakIdx = pumpStart;

            for (int j = pumpStart + 1; j < candles.Count; j++)
            {
                if (candles[j].High > peak)
                {
                    peak = candles[j].High;
                    peakIdx = j;
                }

                // Lower high detected → BoS
                bool lowerHigh = j > peakIdx && candles[j].High < peak * 0.995;
                if (lowerHigh && j - pumpStart >= minSegmentLength)
                {
                    int end = Math.Min(j + postBosCandleCount, candles.Count - 1);
                    segments.Add(candles.Skip(pumpStart).Take(end - pumpStart + 1).ToArray());
                    i = j;
                    break;
                }
            }

            i++;
        }

        return segments;
    }
}
