namespace TradingGA;

public record FundingBar(DateTime Time, double Rate);

// O(1) funding rate lookup. Binary crowding gates (fixed thresholds, not GA-tuned):
// Crowded long > +0.08%/8h → skip DipLong/SwingLong. Crowded short < -0.05%/8h → skip FadeShort.
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

    // Funding PnL: single source of truth. Do not re-inline into simulators (sign bug risk).
    // 8h settlement at 00:00/08:00/16:00 UTC.
    public const double FundingIntervalHours = 8.0;

    // Interest-rate floor (~0.01%/8h). Used when no real rate series available.
    public const double FallbackIntervalPct = 0.01;

    // Overflow guard for settlement loop.
    private static readonly DateTime LastAdvanceableSettlement =
        DateTime.MaxValue.AddHours(-FundingIntervalHours);

    // SIGN: positive rate = longs pay shorts. Returns percentage points (added to trade ret).
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
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime); t <= exitTime; )
        {
            pnl += funding.TryGetRate(t, out double rate)
                 ? dirSign * rate * 100.0
                 : -FallbackIntervalPct;
            if (t > LastAdvanceableSettlement) break;
            t = t.AddHours(FundingIntervalHours);
        }
        return pnl;
    }

    // Number of 8h settlement ticks a position is charged for.
    public static int CountSettlements(DateTime entryTime, DateTime exitTime)
    {
        if (exitTime <= entryTime) return 0;
        int n = 0;
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime); t <= exitTime; )
        {
            n++;
            if (t > LastAdvanceableSettlement) break;
            t = t.AddHours(FundingIntervalHours);
        }
        return n;
    }

    // First 8h settlement boundary strictly after `time`.
    private static DateTime FirstSettlementStrictlyAfter(DateTime time)
    {
        DateTime t = time.Date;
        while (t <= time) t = t.AddHours(FundingIntervalHours);
        return t;
    }
}
