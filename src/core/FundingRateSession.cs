namespace TradingGA;

public record FundingBar(DateTime Time, double Rate);

// O(1) funding rate lookup. Binary crowding gates (fixed thresholds, not GA-tuned):
// Crowded long > +0.08%/8h → skip DipLong/SwingLong. Crowded short < -0.05%/8h → skip FadeShort.
// Settlement spacing varies by symbol: BTC/ETH=8h, most alts=4h, some caps=1h.
// PnlPct() advances by the ACTUAL spacing for each symbol, not hardcoded 8h.
public class FundingRateSession
{
    private readonly long[]   _ticks;
    private readonly double[] _rates;
    private readonly double   _intervalHours;  // Actual settlement spacing for this symbol

    public const double CrowdedLongThreshold  = +0.0008;  // +0.08% per 8h
    public const double CrowdedShortThreshold = -0.0005;  // -0.05% per 8h

    public FundingRateSession(FundingBar[] funding) : this(funding, 8.0) { }

    /// <summary>
    /// Construct funding session with explicit settlement spacing.
    /// HL symbols settle at different intervals: 8h (BTC/ETH), 4h (most alts), 1h (some caps).
    /// The spacing must match the symbol's actual settlement frequency to correctly count ticks.
    /// </summary>
    public FundingRateSession(FundingBar[] funding, double intervalHours)
    {
        var sorted = funding.OrderBy(f => f.Time).ToArray();
        _ticks = sorted.Select(f => f.Time.Ticks).ToArray();
        _rates = sorted.Select(f => f.Rate).ToArray();
        _intervalHours = intervalHours > 0 ? intervalHours : 8.0;
    }

    // Coverage: before first print → unknown (TryGetRate=false). After last → flat-extrapolated.
    // Unknown → not crowded (gates), interest-rate floor (cost model).
    public bool TryGetRate(DateTime time, out double rate)
    {
        rate = 0.0;
        if (_ticks.Length == 0) return false;
        long t = time.Ticks;
        if (t < _ticks[0]) return false;
        if (t >= _ticks[^1]) { rate = _rates[^1]; return true; }
        int lo = 0, hi = _ticks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_ticks[mid] <= t) lo = mid; else hi = mid - 1;
        }
        rate = _rates[lo];
        return true;
    }

    public double GetRate(DateTime time) => TryGetRate(time, out double r) ? r : 0.0;


    public bool CoversTime(DateTime time) => _ticks.Length > 0 && time.Ticks >= _ticks[0];


    public bool IsCrowdedLong(DateTime time)  => GetRate(time) > CrowdedLongThreshold;


    public bool IsCrowdedShort(DateTime time) => GetRate(time) < CrowdedShortThreshold;

    public double CurrentRate => _rates.Length > 0 ? _rates[^1] : 0.0;

    /// <summary>
    /// Actual settlement interval in hours for THIS symbol. Varies across HL:
    /// 8h for majors (BTC, ETH), 4h for most alts, 1h for some smaller caps.
    /// </summary>
    public double IntervalHours => _intervalHours;

    // Interest-rate floor (~0.01%/tick). Used when no real rate series available.
    public const double FallbackIntervalPct = 0.01;

    // Default 8h settlement boundary used for fallback calculations and model comparison.
    public const double FundingIntervalHours = 8.0;

    // Overflow guard for settlement loop.
    private static readonly DateTime LastAdvanceableSettlement =
        DateTime.MaxValue.AddHours(-FundingIntervalHours);

    // Funding PnL: single source of truth. Do not re-inline into simulators (sign bug risk).
    // Discrete charging: one full interval per settlement tick crossed, never prorated.
    public static double PnlPct(DateTime entryTime, DateTime exitTime, FundingRateSession? funding, bool isLong)
    {
        if (exitTime <= entryTime) return 0.0;

        if (funding == null)
        {
            // No real rates → floor cost per tick, both directions. Principle: never book
            // unprovable income; always book cost that can't be ruled out.
            int intervals = CountSettlements(entryTime, exitTime);
            return -FallbackIntervalPct * intervals;
        }

        // Real rates: sign-correct per tick. Uncovered ticks → floor (same principle as null branch).
        double dirSign = isLong ? -1.0 : +1.0;
        double pnl = 0.0;
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime, funding.IntervalHours); t <= exitTime; )
        {
            pnl += funding.TryGetRate(t, out double rate)
                 ? dirSign * rate * 100.0
                 : -FallbackIntervalPct;
            if (t > LastAdvanceableSettlement) break;
            t = t.AddHours(funding.IntervalHours);
        }
        return pnl;
    }

    // Number of settlement ticks a position is charged for (uses default 8h).
    public static int CountSettlements(DateTime entryTime, DateTime exitTime)
    {
        if (exitTime <= entryTime) return 0;
        int n = 0;
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime, FundingIntervalHours); t <= exitTime; )
        {
            n++;
            if (t > LastAdvanceableSettlement) break;
            t = t.AddHours(FundingIntervalHours);
        }
        return n;
    }

    /// <summary>
    /// Count settlement ticks using the actual spacing for a given session.
    /// </summary>
    public int CountSettlementsForThisSymbol(DateTime entryTime, DateTime exitTime)
    {
        if (exitTime <= entryTime) return 0;
        int n = 0;
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime, _intervalHours); t <= exitTime; )
        {
            n++;
            if (t > LastAdvanceableSettlement) break;
            t = t.AddHours(_intervalHours);
        }
        return n;
    }

    // First settlement boundary strictly after `time`, using the given interval spacing.
    private static DateTime FirstSettlementStrictlyAfter(DateTime time, double intervalHours)
    {
        DateTime t = time.Date;
        while (t <= time) t = t.AddHours(intervalHours);
        return t;
    }
}
