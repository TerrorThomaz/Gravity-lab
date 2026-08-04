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
    private readonly double[] _ltMom;          // 48-bar (8-day) 4H momentum for bull bypass
    private readonly double[] _atrRatios;      // raw ATR ratio per bar (for entry gate + diagnostics)
    private readonly double   _bullMomBypass;  // threshold above which longs skip the guard
    private readonly double   _entryAtrGate;   // ATR ratio above which DipLong/SwingLong entries are blocked

    public DynamicGuardSession(Candle[] btcH1, DynamicGuardGenotype g)
    {
        var h4 = Aggregate4H(btcH1);
        int n  = h4.Length;

        var atr          = ComputeTrueRange(h4);
        _ticks           = new long[n];
        _mults           = new double[n];
        _ltMom           = new double[n];
        _atrRatios       = new double[n];
        _bullMomBypass   = g.BullMomBypass;
        _entryAtrGate    = g.EntryAtrGate;

        int atrN = (int)Math.Max(2, g.AtrLookback);
        int momN = (int)Math.Max(2, g.MomLookback);
        int recN = (int)Math.Round(g.RecoveryBars);
        const int ltN = 48; // fixed 8-day (48×4H) lookback for bull-trend detection

        double atrSum            = 0;
        int    recoveryCountdown = 0;

        for (int i = 0; i < n; i++)
        {
            // Running ATR baseline (O(1) rolling sum)
            atrSum += atr[i];
            if (i >= atrN) atrSum -= atr[i - atrN];
            int    cnt      = Math.Min(i + 1, atrN);
            double avgAtr   = atrSum / cnt;
            double atrRatio = avgAtr > 1e-9 ? atr[i] / avgAtr : 1.0;

            // Short-term momentum: (close[i] − close[i−momN]) / close[i−momN]
            int    momIdx = Math.Max(0, i - momN);
            double momRef = h4[momIdx].Close;
            double mom    = momRef > 1e-9 ? (h4[i].Close - momRef) / momRef : 0.0;

            // Long-term momentum (8-day): used by bull-bypass logic in GetMult
            int    ltIdx  = Math.Max(0, i - ltN);
            double ltRef  = h4[ltIdx].Close;
            _ltMom[i]     = ltRef > 1e-9 ? (h4[i].Close - ltRef) / ltRef : 0.0;

            _ticks[i]     = h4[i].Time.Ticks;
            _atrRatios[i] = atrRatio;

            double mult;
            if (atrRatio > g.PanicTrigger)
            {
                // Panic: full halt; reset the recovery countdown
                mult              = 0.0;
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

    // Standard guard multiplier (no bull bypass). Used by ScenarioGA and legacy callers.
    public double GetMult(DateTime time) => GetMultInternal(time, applyBullBypass: false, "");

    // Strategy-aware multiplier: DipLong/SwingLong bypass the guard during 8-day bull trends.
    // Grid always gets the standard guard (fires in ranging, not bull-dependent).
    public double GetMult(DateTime time, string strategy)
    {
        bool isBullLong = strategy is "diplong" or "swing_long";
        return GetMultInternal(time, applyBullBypass: isBullLong, strategy);
    }

    private double GetMultInternal(DateTime time, bool applyBullBypass, string _strategy)
    {
        if (_ticks.Length == 0) return 1.0;
        long t = time.Ticks;
        if (t < _ticks[0])   return 1.0;
        if (t >= _ticks[^1]) return applyBullBypass && _ltMom[^1] > _bullMomBypass ? 1.0 : _mults[^1];

        int lo = 0, hi = _ticks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_ticks[mid] <= t) lo = mid;
            else                  hi = mid - 1;
        }

        if (applyBullBypass && _bullMomBypass > 1e-9 && _ltMom[lo] > _bullMomBypass)
            return 1.0;
        return _mults[lo];
    }

    // Raw 4H ATR ratio at a given time — used for diagnostics in FullTest Section 10.
    public double GetAtrRatio(DateTime time)
    {
        if (_ticks.Length == 0) return 1.0;
        long t = time.Ticks;
        if (t < _ticks[0])   return _atrRatios[0];
        if (t >= _ticks[^1]) return _atrRatios[^1];
        int lo = 0, hi = _ticks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_ticks[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return _atrRatios[lo];
    }

    // Hard entry block for DipLong/SwingLong when BTC 4H ATR is elevated.
    // Intentionally NOT bypassed by BullMomBypass: high ATR widens ATR-based stops
    // regardless of trend direction, so large per-trade losses are possible even in bull markets.
    public bool IsEntryBlocked(DateTime time, string strategy)
    {
        if (strategy is not ("diplong" or "swing_long")) return false;
        return GetAtrRatio(time) > _entryAtrGate;
    }

    // Strategies that the guard applies to (same set as DrawdownGuard).
    public static bool IsGuarded(string strategy) =>
        strategy is "grid" or "gridshort" or "diplong" or "swing_long";

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
