namespace TradingGA;

// RipShort: bear-regime RSI relief-rally continuation short. Dual-TF (1h setup + 15m trigger).
// Invariant: stop/target are WICK-TRIGGERED (intrabar highs/lows, not closes) — bear-rally squeezes are the dominant tail risk.
public static class RipShortSimulator
{
    // Control switch: GRAVITY_RIPSHORT_NOADAPT=1 forces adapt=0 at use, preserving RNG stream for A/B comparison.
    private static readonly bool NoAdapt =
        Environment.GetEnvironmentVariable("GRAVITY_RIPSHORT_NOADAPT") == "1";
    private const int AtrPeriod         = 14;
    private const int RsiPeriod         = 7;
    private const int AdxPeriod         = 7;
    private const int SwingHighLookback = 20;   // h1 bars for stop-placement swing high
    private const double StopGapAtrK = 0.030;   // gap premium on stop exits only

    // Experimental loss-mitigation override (dormant — all production call sites pass null).
    public enum ExitOverrideMode
    {
        None,
        WaitForBreakeven,  // suppress time exit while underwater + calm vol; extends hold by at most MaxExtraHoldCandles
        DcaAndWait         // !! PRODUCTION-BROKEN: adds capital but reports as 1 trade, breaking concurrency caps
    }

    /// <summary>
    /// Dormant post-hoc exit override config. Null at every production call site.
    /// MaxLossPct: absolute loss cap (0=disabled); LockTriggerPct/LockProfitPct: min-profit ratchet.
    /// ATR-aware variants scale with volatility; flat pct is wrong shape for cross-coin universe.
    /// </summary>
    public record ExitOverrideConfig(
        ExitOverrideMode Mode,
        double AtrGateRatio        = 1.4,
        double DcaAtrMult          = 1.2,
        int    MaxExtraHoldCandles = 60,
        double MaxSizeMult         = 2.0,
        double MaxLossPct          = 0.0,
        double LockTriggerPct      = 0.0,
        double LockProfitPct       = 0.0,
        double MaxLossAtrMult      = 0.0,
        double MaxLossPctFloor     = 0.0,
        double LockTriggerAtrMult  = 0.0,
        double LockProfitAtrMult   = 0.0);

    // Time = EXIT bar. EntryPrice = blended entry if DCA fired.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetRipShortReturns(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null,
        ExitOverrideConfig? overrideCfg = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunRipShortMultiTF(g, h1, m15, funding, overrideCfg, ratchet);
        return trades.Select(t => (t.Item1, t.Item2, t.Item3, t.Item5, t.Item6)).ToList();
    }

    // Carries RegimeBarsActive for regime-conditional fold scoring.
    internal static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)> GetRipShortReturnsWithRegime(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null,
        ExitOverrideConfig? overrideCfg = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunRipShortMultiTF(g, h1, m15, funding, overrideCfg, ratchet);
        return trades;
    }

    public record RipShortTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailLow,    // lowest close since entry (trail reference for short)
        int    HoldCount);

    public static RipShortTradeState GetRipShortTradeState(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null)
    {
        var (_, state) = RunRipShortMultiTF(g, h1, m15, funding);
        return state;
    }

    private static (List<(DateTime, double, string, int, DateTime, double)> Trades, RipShortTradeState FinalState)
        RunRipShortMultiTF(RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding,
        ExitOverrideConfig? overrideCfg = null, RatchetConfig ratchet = default)
    {
        int h1Warmup = Math.Max(
                           Math.Max(g.RegimeLongEmaPeriod, Math.Max(g.EmaPeriod, RsiPeriod + 2)),
                           AdxPeriod * 2 + 1)
                       + g.RegimeSlopeLookback + 3;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new RipShortTradeState(false, 0, 0, 0, false, 0, 0));

        var h1Closes = CandleExt.Closes(h1);
        var h1Highs  = CandleExt.Highs(h1);
        var h1Lows   = CandleExt.Lows(h1);

        var h1RegimeEma = Trend.Ema(h1Closes, g.RegimeLongEmaPeriod);
        var h1Ema       = Trend.Ema(h1Closes, g.EmaPeriod);
        var h1Rsi       = Momentum.Rsi(h1Closes, RsiPeriod);
        var h1Adx       = Trend.Adx(h1Highs, h1Lows, h1Closes, AdxPeriod);

        var h4      = FadeShortSimulator.AggregateCandles(h1.ToArray(), 4);
        var h4Highs = CandleExt.Highs(h4);
        var h4Lows  = CandleExt.Lows(h4);
        var h4Cls   = CandleExt.Closes(h4);
        var h4Atr   = Volatility.Atr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        // Local h4 ATR ratio (current vs 20-bar avg) — coin-level vol elevation for wait decisions.
        double[] h4AtrRatio = new double[h4Atr.Length];
        if (overrideCfg != null)
        {
            const int atrRatioLookback = 20;
            double atrSum = 0;
            for (int i = 0; i < h4Atr.Length; i++)
            {
                atrSum += h4Atr[i];
                if (i >= atrRatioLookback) atrSum -= h4Atr[i - atrRatioLookback];
                double avgAtr = atrSum / Math.Min(i + 1, atrRatioLookback);
                h4AtrRatio[i] = avgAtr > 1e-10 ? h4Atr[i] / avgAtr : 1.0;
            }
        }

        var m15Closes = CandleExt.Closes(m15);
        var m15Highs  = CandleExt.Highs(m15);
        var m15Lows   = CandleExt.Lows(m15);

        // Consecutive bear-regime bars: close < RegimeLongEma AND EMA falling.
        int[] bearRegimeBarsAtBar = new int[h1.Length];
        int bearRunning = 0;
        int slopeLen = g.RegimeSlopeLookback;
        var regimeSlope = Signals.EmaSlope(h1RegimeEma, g.RegimeSlopeLookback);
        for (int i = h1Warmup; i < h1.Length; i++)
        {
            bool regimeBar = h1Closes[i] < h1RegimeEma[i] && regimeSlope[i] < 0;
            bearRunning = regimeBar ? bearRunning + 1 : 0;
            bearRegimeBarsAtBar[i] = bearRunning;
        }

        var result = new List<(DateTime, double, string, int, DateTime, double)>();

        double   cachedAdapt     = 0.0;   // 0 = pre-gene behaviour
        bool     inTrade         = false;
        double   entry           = 0;
        double   hardStop        = 0;
        double   target          = 0;
        double   trailLow        = 0;
        double   atrEntry        = 0;
        bool     trailArmed      = false;
        bool     lockArmed       = false;
        int      entryIH1        = 0;
        int      entryRegimeBars = 0;
        DateTime entryTime       = default;
        bool     dcaDone         = false;
        double   dcaSizeMult     = 1.0;
        bool     everWaited      = false;

        int    cachedH1Ref     = -1;
        bool   cachedSetupMet  = false;
        double cachedSwingHigh = 0;
        double cachedAtrH4     = 0;
        int    cachedRegimeBars = 0;

        int m15Start = (h1Warmup + 1) * 4;
        int m15Limit = h1.Length * 4;

        for (int im15 = m15Start; im15 < Math.Min(m15.Length, m15Limit); im15++)
        {
            int ih1   = im15 / 4;
            int h1Ref = ih1 - 1;

            if (h1Ref < h1Warmup || h1Ref >= h1.Length) continue;

            double m15Price = m15Closes[im15];

            if (!inTrade)
            {
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    // Previous completed h4 bar (current aggregates future h1 bars)
                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : h1Closes[h1Ref] * 0.08;

                    bool regimeOk = h1Closes[h1Ref] < h1RegimeEma[h1Ref]
                                 && h1RegimeEma[h1Ref] < h1RegimeEma[h1Ref - slopeLen];
                    bool trendOk = h1Closes[h1Ref] < h1Ema[h1Ref]
                                && h1Adx[h1Ref] >= g.AdxThreshold;

                    // Regime-adaptive threshold: maturity 0..1, adapt tightens/loosens entry for young regimes.
                    // Strength=0 is exact no-op.
                    double maturity = g.RegimeAdaptPivotBars <= 0
                        ? 1.0
                        : Math.Min(1.0, bearRegimeBarsAtBar[h1Ref] / (double)g.RegimeAdaptPivotBars);
                    double adapt = NoAdapt ? 0.0 : g.RegimeAdaptStrength * (1.0 - maturity);

                    // Rally zone: positive adapt raises bar for unproven regimes.
                    bool rallyOk = h1Rsi[h1Ref] >= g.RsiRallyThreshold * (1.0 + adapt);

                    if (regimeOk && trendOk && rallyOk)
                    {
                        int    lbStart   = Math.Max(0, h1Ref - SwingHighLookback);
                        double swingHigh = h1Highs[lbStart];
                        for (int j = lbStart + 1; j <= h1Ref; j++)
                            if (h1Highs[j] > swingHigh) swingHigh = h1Highs[j];

                        cachedSetupMet   = true;
                        cachedSwingHigh  = swingHigh;
                        cachedAtrH4      = atrH4;
                        cachedRegimeBars = bearRegimeBarsAtBar[h1Ref];
                        cachedAdapt      = adapt;
                    }
                }

                // 15m bearish BoS trigger; enter at next bar's open.
                if (cachedSetupMet && m15Closes[im15] < m15Lows[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    inTrade         = true;
                    entry           = m15[nextBar].Open;
                    atrEntry        = cachedAtrH4;
                    hardStop        = cachedSwingHigh + g.StopLossAtrMult * (1.0 - cachedAdapt) * atrEntry;
                    target          = entry - g.TakeProfitAtrMult * atrEntry;
                    trailLow        = entry;
                    trailArmed      = false;
                    lockArmed       = false;
                    entryIH1        = nextBar / 4;
                    entryRegimeBars = cachedRegimeBars;
                    entryTime       = m15[nextBar].Time;
                    dcaDone         = false;
                    dcaSizeMult     = 1.0;
                }
            }
            else
            {
                if (m15Price < trailLow) trailLow = m15Price;
                if (!trailArmed && entry - trailLow >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

                // Absolute loss cap (opt-in), wick-triggered, tighter of cap/ATR stop wins.
                double capPct = overrideCfg?.MaxLossPct ?? 0.0;
                if (capPct > 0.0 && (overrideCfg?.MaxLossAtrMult ?? 0.0) > 0.0 && entry > 1e-9)
                {
                    double atrPctNow = atrEntry / entry * 100.0;
                    capPct = Math.Clamp(overrideCfg!.MaxLossAtrMult * atrPctNow,
                                        overrideCfg.MaxLossPctFloor, overrideCfg.MaxLossPct);
                }
                double capPx = capPct > 0.0 ? entry * (1.0 + capPct / 100.0) : double.MaxValue;
                bool hitCap    = m15Highs[im15] >= capPx;

                // Min-profit ratchet: production (ExecContext) takes precedence over overrideCfg path.
                double lockPx = double.MaxValue;
                if (ratchet.Enabled)
                {
                    if (!lockArmed && ExitRatchet.ShouldArm(false, entry, atrEntry, trailLow, ratchet))
                        lockArmed = true;
                    if (lockArmed && ExitRatchet.LockPrice(false, entry, atrEntry, trailLow, ratchet) is double rsLk)
                        lockPx = rsLk;
                }
                else
                {
                    double lockTrig = overrideCfg?.LockTriggerPct ?? 0.0;
                    double lockProf = overrideCfg?.LockProfitPct  ?? 0.0;
                    if ((overrideCfg?.LockTriggerAtrMult ?? 0.0) > 0.0 && entry > 1e-9)
                    {
                        double atrPctE = atrEntry / entry * 100.0;
                        double tCeil = lockTrig > 0 ? lockTrig : double.MaxValue;
                        double pCeil = lockProf > 0 ? lockProf : double.MaxValue;
                        lockTrig = Math.Min(overrideCfg!.LockTriggerAtrMult * atrPctE, tCeil);
                        lockProf = Math.Min(overrideCfg.LockProfitAtrMult  * atrPctE, pCeil);
                    }
                    if (lockTrig > 0.0 && !lockArmed && entry > 1e-9
                        && (entry - trailLow) / entry * 100.0 >= lockTrig)
                        lockArmed = true;
                    if (lockArmed && lockProf > 0.0)
                        lockPx = entry * (1.0 - lockProf / 100.0);
                }

                bool hitStop   = m15Highs[im15] >= hardStop || hitCap
                                 || (lockArmed && m15Highs[im15] >= lockPx);
                bool hitTarget = m15Lows[im15]  <= target;
                bool hitTrail  = trailArmed && m15Price > trailLow + g.TrailingStopAtrMult * atrEntry;
                bool pastMaxHold = holdH1 >= g.MaxHoldCandles;

                // Loss-mitigation: wait past timeout if losing + calm vol; hard levels still cap risk.
                bool waiting = false;
                if (pastMaxHold && overrideCfg != null)
                {
                    int    h4RefNow    = Math.Min(h4AtrRatio.Length - 1, Math.Max(0, ih1 / 4 - 1));
                    double atrRatioNow = h4AtrRatio.Length > 0 ? h4AtrRatio[h4RefNow] : 1.0;
                    waiting = ShouldWaitPastTimeout(m15Price, entry, atrRatioNow, holdH1 - g.MaxHoldCandles, overrideCfg);
                    if (waiting) everWaited = true;

                    bool exitingThisBar = hitStop || hitTarget || hitTrail;

                    if (!exitingThisBar && ShouldDca(waiting, dcaDone, m15Price, entry, atrEntry, overrideCfg))
                    {
                        // Rebase to blended entry; reset trail; hardStop stays fixed (tail backstop).
                        entry       = BlendedEntry(entry, m15Price, overrideCfg.MaxSizeMult);
                        target      = entry - g.TakeProfitAtrMult * atrEntry;
                        trailLow    = entry;
                        trailArmed  = false;
                        dcaDone     = true;
                        dcaSizeMult = overrideCfg.MaxSizeMult;
                    }
                }
                bool timedOut = pastMaxHold && !waiting;

                // Time-decay stop: tolerated adverse move narrows to 0% at MaxHoldCandles.
                bool hitTimeStop = false;
                if (!hitStop && !hitTarget && !hitTrail && !timedOut && !waiting
                    && holdH1 >= g.TimeStopBars && g.MaxHoldCandles > g.TimeStopBars)
                {
                    double progress       = (double)(holdH1 - g.TimeStopBars) / (g.MaxHoldCandles - g.TimeStopBars);
                    double maxAdverseRatio = g.TimeStopLossPct * (1.0 - progress);
                    hitTimeStop = (m15Price - entry) / entry > maxAdverseRatio;
                }

                if (hitStop || hitTarget || hitTrail || timedOut || hitTimeStop)
                {
                    // Same bar: STOP wins. Fill at tighter of cap/ATR stop.
                    double stopPx = capPx < hardStop ? capPx : hardStop;
                    if (lockArmed && lockPx < stopPx) stopPx = lockPx;
                    double exitPx = hitStop   ? stopPx :
                                    hitTarget ? target   : m15Price;
                    double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[im15].Time, funding, isLong: false);
                    double ret = ((entry - exitPx) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry) + fundingPnl) * dcaSizeMult;
                    string kind = dcaDone ? "ripshort_dca" : everWaited ? "ripshort_wait" : "ripshort";
                    result.Add((m15[im15].Time, ret, kind, entryRegimeBars, entryTime, entry));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx    = m15Closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[^1].Time, funding, isLong: false);
            double ret = ((entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry) + fundingPnl) * dcaSizeMult;
            string kind = dcaDone ? "ripshort_dca" : everWaited ? "ripshort_wait" : "ripshort";
            result.Add((m15[^1].Time, ret, kind, entryRegimeBars, entryTime, entry));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new RipShortTradeState(inTrade, entry, hardStop, target, trailArmed, trailLow, finalHold));
    }

    // Wait past timeout? Losing + calm vol + under extra-hold cap.
    internal static bool ShouldWaitPastTimeout(
        double price, double entry, double atrRatioNow, int extraHoldBars, ExitOverrideConfig cfg)
    {
        if (cfg.Mode == ExitOverrideMode.None) return false;
        bool losing     = price > entry;
        bool calmEnough = atrRatioNow <= cfg.AtrGateRatio;
        bool underCap   = extraHoldBars < cfg.MaxExtraHoldCandles;
        return losing && calmEnough && underCap;
    }

    // DCA add? Only while waiting, once per trade, after DcaAtrMult×ATR adverse move. MaxSizeMult>1 required.
    internal static bool ShouldDca(
        bool waiting, bool dcaDone, double price, double entry, double atrEntry, ExitOverrideConfig cfg) =>
        waiting && cfg.Mode == ExitOverrideMode.DcaAndWait && !dcaDone
        && cfg.MaxSizeMult > 1.0
        && price - entry >= cfg.DcaAtrMult * atrEntry;

    // Size-weighted blended entry. maxSizeMult=2.0 = equal-size midpoint.
    internal static double BlendedEntry(double entry, double addPrice, double maxSizeMult)
    {
        double addSize = Math.Max(0.0, maxSizeMult - 1.0);
        return (entry + addPrice * addSize) / (1.0 + addSize);
    }

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx,
                                      double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);

}
