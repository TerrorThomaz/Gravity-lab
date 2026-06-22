namespace TradingGA;

public record FundingBar(DateTime Time, double Rate);

// Pre-computed O(1) lookup of BTC perpetual funding rate at any timestamp.
// Bybit settles funding every 8h; between settlements the last known rate applies.
//
// Used as a hard binary gate on entries — thresholds are fixed market-microstructure
// constants, not GA-tuned, so they add zero overfit pressure:
//   Crowded long  (funding > +0.08%/8h ≈ 3× normal baseline) → skip DipLong / SwingLong
//   Crowded short (funding < -0.05%/8h)                       → skip FadeShort
public class FundingRateSession
{
    private readonly long[]   _ticks;
    private readonly double[] _rates;

    public const double CrowdedLongThreshold  = +0.0008;  // +0.08% per 8h
    public const double CrowdedShortThreshold = -0.0005;  // -0.05% per 8h

    public FundingRateSession(FundingBar[] funding)
    {
        var sorted = funding.OrderBy(f => f.Time).ToArray();
        _ticks = sorted.Select(f => f.Time.Ticks).ToArray();
        _rates = sorted.Select(f => f.Rate).ToArray();
    }

    public double GetRate(DateTime time)
    {
        if (_ticks.Length == 0) return 0.0;
        long t = time.Ticks;
        if (t <= _ticks[0])  return _rates[0];
        if (t >= _ticks[^1]) return _rates[^1];
        int lo = 0, hi = _ticks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_ticks[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return _rates[lo];
    }

    // Returns true when longs are extremely crowded — skip DipLong / SwingLong entries.
    public bool IsCrowdedLong(DateTime time)  => GetRate(time) > CrowdedLongThreshold;

    // Returns true when shorts are extremely crowded — skip FadeShort entries.
    public bool IsCrowdedShort(DateTime time) => GetRate(time) < CrowdedShortThreshold;

    public double CurrentRate => _rates.Length > 0 ? _rates[^1] : 0.0;
}
