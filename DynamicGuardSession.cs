namespace TradingGA;

// Pre-computes per-4H-bar size multipliers from BTC H1 data.
// Three-tier response:
//   Normal:   ATR ≤ AtrTrigger  →  mult = 1.0 (unless momentum stress)
//   Stress:   ATR > AtrTrigger  →  mult ramps down toward SizeFloor
//   Panic:    ATR > PanicTrigger → mult = 0.0 (full halt, ignores floor)
// Recovery: after any trigger clears, stays at SizeFloor for RecoveryBars 4H bars.
// O(1) per-trade lookup via binary search on tick array.
public class DynamicGuardSession
{
    private readonly long[]   _ticks;
    private readonly double[] _mults;

    public DynamicGuardSession(Candle[] btcH1, DynamicGuardGenotype g)
    {
        var h4 = Aggregate4H(btcH1);
        int n  = h4.Length;

        var atr   = ComputeTrueRange(h4);
        _ticks    = new long[n];
        _mults    = new double[n];

        int atrN = (int)Math.Max(2, g.AtrLookback);
        int momN = (int)Math.Max(2, g.MomLookback);
        int recN = (int)Math.Round(g.RecoveryBars);

        double atrSum           = 0;
        int    recoveryCountdown = 0;

        for (int i = 0; i < n; i++)
        {
            // Running ATR baseline (O(1) rolling sum)
            atrSum += atr[i];
            if (i >= atrN) atrSum -= atr[i - atrN];
            int    cnt      = Math.Min(i + 1, atrN);
            double avgAtr   = atrSum / cnt;
            double atrRatio = avgAtr > 1e-9 ? atr[i] / avgAtr : 1.0;

            // Momentum: (close[i] − close[i−momN]) / close[i−momN]
            int    momIdx = Math.Max(0, i - momN);
            double momRef = h4[momIdx].Close;
            double mom    = momRef > 1e-9 ? (h4[i].Close - momRef) / momRef : 0.0;

            _ticks[i] = h4[i].Time.Ticks;

            double mult;
            if (atrRatio > g.PanicTrigger)
            {
                // Panic: full halt; reset the recovery countdown
                mult             = 0.0;
                recoveryCountdown = recN;
            }
            else
            {
                mult = g.ComputeMult(atrRatio, mom);

                // Any stress clears → still clamp to floor for RecoveryBars bars
                if (mult < 1.0 - 1e-9)
                    recoveryCountdown = recN;                  // reset on every stressed bar

                if (recoveryCountdown > 0 && mult > g.SizeFloor)
                    mult = g.SizeFloor;

                if (recoveryCountdown > 0) recoveryCountdown--;
            }

            _mults[i] = mult;
        }
    }

    // Returns the multiplier for the most recent 4H bar at or before `time`.
    public double GetMult(DateTime time)
    {
        if (_ticks.Length == 0) return 1.0;
        long t = time.Ticks;
        if (t < _ticks[0])   return 1.0;
        if (t >= _ticks[^1]) return _mults[^1];

        int lo = 0, hi = _ticks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_ticks[mid] <= t) lo = mid;
            else                  hi = mid - 1;
        }
        return _mults[lo];
    }

    // Strategies that the guard applies to (same set as DrawdownGuard).
    public static bool IsGuarded(string strategy) =>
        strategy is "grid" or "diplong" or "swing_long";

    // Aggregates 1H candles into 4H candles (groups of 4, aligned by array index).
    private static Candle[] Aggregate4H(Candle[] h1)
    {
        if (h1.Length == 0) return [];
        var result = new List<Candle>(h1.Length / 4 + 1);
        int i = 0;
        while (i < h1.Length)
        {
            int    end  = Math.Min(i + 4, h1.Length);
            double hi4  = double.MinValue, lo4 = double.MaxValue, vol4 = 0;
            for (int j = i; j < end; j++)
            {
                if (h1[j].High > hi4) hi4 = h1[j].High;
                if (h1[j].Low  < lo4) lo4 = h1[j].Low;
                vol4 += h1[j].Volume;
            }
            result.Add(new Candle(h1[i].Time, h1[i].Open, hi4, lo4, h1[end-1].Close, vol4));
            i += 4;
        }
        return [.. result];
    }

    // Per-bar true range (unsmoothed; smoothing done via rolling avg in constructor).
    private static double[] ComputeTrueRange(Candle[] bars)
    {
        var tr = new double[bars.Length];
        for (int i = 0; i < bars.Length; i++)
        {
            double range = bars[i].High - bars[i].Low;
            if (i > 0)
            {
                range = Math.Max(range, Math.Abs(bars[i].High  - bars[i-1].Close));
                range = Math.Max(range, Math.Abs(bars[i].Low   - bars[i-1].Close));
            }
            tr[i] = range;
        }
        return tr;
    }
}
