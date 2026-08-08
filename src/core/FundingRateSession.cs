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

    // ┌── COVERAGE RULE — THE TWO ENDS OF THE SERIES ARE NOT SYMMETRIC ──────────────────┐
    // │ BEFORE the first print → UNKNOWN. TryGetRate reports false; GetRate returns 0.0. │
    // │ AFTER  the last print  → flat-extrapolated from the last known rate.             │
    // └──────────────────────────────────────────────────────────────────────────────────┘
    //
    // This used to flat-extrapolate BOTH ends (`if (t <= _ticks[0]) return _rates[0]`), so a
    // trade a year before the earliest funding print was billed that single print's rate at
    // every settlement it crossed — a fabricated constant whose sign and magnitude are whatever
    // the first row of the cache happened to be. The session is built from whatever the funding
    // cache holds, and all six strategies now price funding through this one object, so a cache
    // that starts later than the candles would silently paint a synthetic rate over the whole
    // leading window of every backtest.
    //
    // The trailing end is deliberately left extrapolating, because it is a different situation:
    // funding is strongly autocorrelated at the 8h scale, and the gap past the last print is
    // structurally small — the cache ends at the end of the backtest window, or at "now" in
    // papertrade — so an extrapolated tick sits adjacent to real data instead of an unbounded
    // distance from it. (Known limitation: nothing here caps how stale the last print may be.
    // Adding a staleness horizon would mean inventing a tunable; the leading-edge fix removes
    // the unbounded case that actually occurs in practice.)
    //
    // For the crowding gates, "unknown" reads as NOT crowded: a hard binary entry gate must not
    // block a trade on an invented rate. For the cost model, "unknown" reads as the interest-rate
    // floor — see PnlPct.
    public bool TryGetRate(DateTime time, out double rate)
    {
        rate = 0.0;
        if (_ticks.Length == 0) return false;
        long t = time.Ticks;
        if (t < _ticks[0]) return false;              // before the series begins → unknown
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

    // True when `time` is at or after the first known funding print.
    public bool CoversTime(DateTime time) => _ticks.Length > 0 && time.Ticks >= _ticks[0];

    // Returns true when longs are extremely crowded — skip DipLong / SwingLong entries.
    public bool IsCrowdedLong(DateTime time)  => GetRate(time) > CrowdedLongThreshold;

    // Returns true when shorts are extremely crowded — skip FadeShort entries.
    public bool IsCrowdedShort(DateTime time) => GetRate(time) < CrowdedShortThreshold;

    public double CurrentRate => _rates.Length > 0 ? _rates[^1] : 0.0;

    // ── Funding PnL ──────────────────────────────────────────────────────────────────────
    // Single source of truth. Previously this logic existed as SIX byte-identical private
    // copies (RipShort / DipLong / FadeLong / FadeShort / SwingLong / GridShort simulators),
    // all of which accumulated `+= rate` regardless of direction. That is correct for a short
    // and sign-inverted for a long, so the three long strategies were booking funding *income*
    // for a cost they were actually paying. Do not re-inline this anywhere.

    // Funding interval on Bybit-style perpetuals: 8h, settled at 00:00 / 08:00 / 16:00 UTC.
    public const double FundingIntervalHours = 8.0;

    // Interest-rate floor component of the funding rate, in PERCENTAGE POINTS per 8h interval.
    // Bybit funding = premium index + clamped interest-rate component; the latter baselines at
    // ~0.01% per 8h. Used only when no real rate series is available.
    public const double FallbackIntervalPct = 0.01;

    // Last settlement instant from which advancing one more interval still fits in a DateTime.
    // Both settlement loops below test the CURRENT tick against this before incrementing, rather
    // than incrementing and catching the overflow: the loop body runs per trade inside GA fitness
    // evaluation, so the guard should be a predictable compare, not exception handling.
    private static readonly DateTime LastAdvanceableSettlement =
        DateTime.MaxValue.AddHours(-FundingIntervalHours);

    // ┌── SIGN RULE — READ THIS BEFORE TOUCHING ANY FUNDING MATH ────────────────────────┐
    // │ A POSITIVE funding rate means LONGS PAY SHORTS.                                  │
    // │ A NEGATIVE funding rate means SHORTS PAY LONGS.                                  │
    // │ So, per settlement tick:      long:  pnl -= rate                                 │
    // │                               short: pnl += rate                                 │
    // │ i.e. the direction sign is  (isLong ? -1 : +1)  applied to the rate, never to the │
    // │ trade's own gross return. Getting `isLong` wrong does not produce a small error — │
    // │ it turns a genuine holding cost into fabricated profit and makes long strategies  │
    // │ look better the longer they hold.                                                │
    // └──────────────────────────────────────────────────────────────────────────────────┘
    //
    // Returns PERCENTAGE POINTS, the same unit as a trade's `ret`, so it is added directly:
    //     ret = grossPct - costPct + FundingRateSession.PnlPct(entry, exit, funding, isLong);
    //
    // Charging is DISCRETE, never prorated: a position is charged one full interval for each
    // settlement tick it is open across, and nothing for the time between ticks. A position
    // opened exactly on a tick is not charged for that tick (it was not open at the snapshot);
    // one closed exactly on a tick is charged for it.
    public static double PnlPct(DateTime entryTime, DateTime exitTime, FundingRateSession? funding, bool isLong)
    {
        if (exitTime <= entryTime) return 0.0;

        if (funding == null)
        {
            // No real rate series. Fall back to the interest-rate floor, counting the SAME
            // discrete 8h ticks as the real-rate branch below — a position is charged a whole
            // interval or nothing, never a prorated fraction of one. The old code returned
            // `-0.01 * heldHours / 8`, which was both direction-blind and continuously
            // prorated; a 1-hour trade that crossed no settlement was charged, and one that
            // straddled a settlement by minutes was charged almost nothing.
            //
            // Governing principle, applied one-sided:
            //     NEVER book funding income that cannot be proven.
            //     ALWAYS book funding cost that cannot be ruled out.
            //
            //   long  → -FallbackIntervalPct per tick. A long pays the floor in the modal case
            //           and pays MORE than the floor whenever the premium index is positive —
            //           precisely the bull / crowded-long conditions the long strategies trade
            //           in. The sign is not in doubt, so the floor is a lower bound on a real
            //           cost. This is the branch the bug inverted: it must never be a credit.
            //   short → -FallbackIntervalPct per tick, same as a long. The MODAL case is that a
            //           short receives the floor (funding prints positive >90% of the time), so
            //           this is deliberately pessimistic rather than expected-value. It is kept
            //           a cost for two reasons. First, the principle above is one-sided: a short
            //           genuinely pays in crowded-short bear regimes — exactly where RipShort and
            //           FadeShort operate — and measured crowded-short funding around -0.05%/8h
            //           makes the real cost several times this floor, so zero would be a worse
            //           understatement of that tail than the floor is an overstatement of the
            //           modal case. Second, RipShortGA and FadeShortGA train with funding: null
            //           (see RipShortGA's header), so this branch IS their fitness landscape;
            //           booking zero would quietly improve short-strategy selection on an
            //           assumption we cannot support without real per-symbol rates.
            //           Wire real funding through (see FundingRateSession construction in
            //           FullTest/Papertrade) and this fallback stops mattering.
            int intervals = CountSettlements(entryTime, exitTime);
            return -FallbackIntervalPct * intervals;
        }

        // Real rate series: apply sign-correct charges per settlement tick.
        // A POSITIVE funding rate means LONGS PAY, SHORTS RECEIVE.
        //
        // Ticks the series does not cover (i.e. settlements BEFORE the first known print — see
        // the coverage rule on TryGetRate) fall back to the interest-rate floor, exactly as the
        // `funding == null` branch above does. Two reasons this beats returning 0.0:
        //
        //   1. It obeys the same one-sided principle: a real position DID pay something at that
        //      settlement, we simply do not know how much. Booking zero is booking unprovable
        //      funding income relative to the floor, and it is the long strategies — which pay
        //      in the modal case — that hold through the most ticks.
        //   2. It makes the cost model COVERAGE-INDEPENDENT. A trade whose window predates the
        //      cache is now priced identically to one run with no session at all, so partially
        //      populating the funding cache can never make a strategy look cheaper to hold than
        //      leaving it empty. Under the old behaviour, and under a 0.0 rule, the reported
        //      edge moved with an artefact of how much history the cache happened to contain.
        double dirSign = isLong ? -1.0 : +1.0;   // long pays a positive rate, short receives it
        double pnl = 0.0;
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime); t <= exitTime; )
        {
            pnl += funding.TryGetRate(t, out double rate)
                 ? dirSign * rate * 100.0
                 : -FallbackIntervalPct;
            if (t > LastAdvanceableSettlement) break;   // next tick would overflow DateTime
            t = t.AddHours(FundingIntervalHours);
        }
        return pnl;
    }

    // Number of 8h settlement ticks a position spanning [entryTime, exitTime] is charged for.
    // Shared with PnlPct's real-rate loop so both branches agree on "how many times charged".
    public static int CountSettlements(DateTime entryTime, DateTime exitTime)
    {
        if (exitTime <= entryTime) return 0;
        int n = 0;
        for (DateTime t = FirstSettlementStrictlyAfter(entryTime); t <= exitTime; )
        {
            n++;
            if (t > LastAdvanceableSettlement) break;   // next tick would overflow DateTime
            t = t.AddHours(FundingIntervalHours);
        }
        return n;
    }

    // First 8h settlement boundary strictly after `time` (grid anchored at 00:00 UTC).
    private static DateTime FirstSettlementStrictlyAfter(DateTime time)
    {
        DateTime t = time.Date;
        while (t <= time) t = t.AddHours(FundingIntervalHours);
        return t;
    }
}
