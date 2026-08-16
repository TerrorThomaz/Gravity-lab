namespace TradingGA;

// DipLong simulator: bull-regime RSI dip + bullish BoS. Mirror of RipShort.
// Supports multi-leg pyramiding (maxLegs>1) with ratchet-gated adds.
public static class DipLongSimulator
{
    private struct Leg  // multi-leg state at class scope (C# has no local structs)
    {
        public double   Entry, HardStop, Target, TrailHigh, AtrEntry;
        public bool     TrailArmed, LockArmed;
        public int      EntryIH1, EntryRegimeBars;
        public DateTime EntryTime;
    }

    private const int AtrPeriod        = 14;
    private const int RsiPeriod        = 7;
    private const int AdxPeriod        = 7;
    private const int SwingLowLookback = 20;   // h1 bars for stop-placement swing low
    private const double StopGapAtrK = 0.015;  // gap premium on stop exits

    // Time = EXIT bar. EntryPrice/EntryTime exposed for variant selection, OOS sizing, execution.
    public static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)> GetDipLongReturns(
        DipLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
        => GetDipLongReturns(g, h1, m15, ctx.Funding, ctx.Ratchet, ctx.MaxLegs);
    public static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)> GetDipLongReturns(
        DipLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
        FundingRateSession? funding = null, RatchetConfig ratchet = default, int maxLegs = 1)
    {
        var (trades, _) = RunDipLongMultiTF(g, h1, m15, funding, ratchet, maxLegs);
        return trades;
    }

    public record DipLongTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailHigh,
        int    HoldCount);

    public static DipLongTradeState GetDipLongTradeState(DipLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunDipLongMultiTF(g, h1, m15);
        return state;
    }

    public static DipLongTradeState GetDipLongTradeState(DipLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
        FundingRateSession? funding)
    {
        var (_, state) = RunDipLongMultiTF(g, h1, m15, funding);
        return state;
    }

    private static (List<(DateTime, double, string, int, DateTime, double)> Trades, DipLongTradeState FinalState)
        RunDipLongMultiTF(DipLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
                          FundingRateSession? funding = null, RatchetConfig ratchet = default, int maxLegs = 1)
    {
        int h1Warmup = Math.Max(
                           Math.Max(g.RegimeLongEmaPeriod, Math.Max(g.EmaPeriod, RsiPeriod + 2)),
                           AdxPeriod * 2 + 1)
                       + g.RegimeSlopeLookback + 3;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new DipLongTradeState(false, 0, 0, 0, false, 0, 0));

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

        var m15Closes = CandleExt.Closes(m15);
        var m15Highs  = CandleExt.Highs(m15);

        // Consecutive bull-regime bars: close > RegimeLongEma AND EMA rising.
        int[] bullRegimeBarsAtBar = new int[h1.Length];
        int bullRunning = 0;
        int slopeLen = g.RegimeSlopeLookback;
        var regimeSlope = Signals.EmaSlope(h1RegimeEma, g.RegimeSlopeLookback);
        for (int i = h1Warmup; i < h1.Length; i++)
        {
            bool regimeBar = h1Closes[i] > h1RegimeEma[i] && regimeSlope[i] > 0;
            bullRunning = regimeBar ? bullRunning + 1 : 0;
            bullRegimeBarsAtBar[i] = bullRunning;
        }

        var result = new List<(DateTime, double, string, int, DateTime, double)>();

        // Multi-leg: new leg only when all open legs armed ratchet (locked above breakeven).
        // Legs emitted separately for correct concurrency accounting.
        var legs = new List<Leg>(Math.Max(1, maxLegs));
        int    cachedH1Ref    = -1;
        bool   cachedSetupMet = false;
        double cachedSwingLow = 0;
        double cachedAtrH4    = 0;
        int    cachedRegimeBars = 0;

        int m15Start = (h1Warmup + 1) * 4;
        int m15Limit = h1.Length * 4;

        for (int im15 = m15Start; im15 < Math.Min(m15.Length, m15Limit); im15++)
        {
            int ih1   = im15 / 4;
            int h1Ref = ih1 - 1;

            if (h1Ref < h1Warmup || h1Ref >= h1.Length) continue;

            double m15Price = m15Closes[im15];

            bool canAdd = legs.Count < Math.Max(1, maxLegs)
                          && (legs.Count == 0 || legs.TrueForAll(l => l.LockArmed));
            if (canAdd)
            {
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);  // previous completed h4 bar
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : h1Closes[h1Ref] * 0.08;

                    bool regimeOk = h1Closes[h1Ref] > h1RegimeEma[h1Ref]
                                 && h1RegimeEma[h1Ref] > h1RegimeEma[h1Ref - slopeLen];
                    bool trendOk = h1Closes[h1Ref] > h1Ema[h1Ref]
                                && h1Adx[h1Ref] >= g.AdxThreshold;
                    bool dipOk = h1Rsi[h1Ref] <= g.RsiDipThreshold;

                    if (regimeOk && trendOk && dipOk)
                    {
                        int    lbStart  = Math.Max(0, h1Ref - SwingLowLookback);
                        double swingLow = h1Lows[lbStart];
                        for (int j = lbStart + 1; j <= h1Ref; j++)
                            if (h1Lows[j] < swingLow) swingLow = h1Lows[j];

                        cachedSetupMet  = true;
                        cachedSwingLow  = swingLow;
                        cachedAtrH4     = atrH4;
                        cachedRegimeBars = bullRegimeBarsAtBar[h1Ref];
                    }
                }

                // 15m bullish BoS trigger; enter at next bar's open.
                if (cachedSetupMet && m15Closes[im15] > m15Highs[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    double eN = m15[nextBar].Open;
                    legs.Add(new Leg
                    {
                        Entry           = eN,
                        AtrEntry        = cachedAtrH4,
                        HardStop        = cachedSwingLow - g.StopLossAtrMult * cachedAtrH4,
                        Target          = eN + g.TakeProfitAtrMult * cachedAtrH4,
                        TrailHigh       = eN,
                        TrailArmed      = false,
                        LockArmed       = false,
                        EntryIH1        = nextBar / 4,
                        EntryRegimeBars = cachedRegimeBars,
                        EntryTime       = m15[nextBar].Time,
                    });
                }
            }
            for (int li = legs.Count - 1; li >= 0; li--)
            {
                var leg = legs[li];
                if (m15Price > leg.TrailHigh) leg.TrailHigh = m15Price;
                if (!leg.TrailArmed && leg.TrailHigh - leg.Entry >= g.TrailingActivationAtrMult * leg.AtrEntry)
                    leg.TrailArmed = true;

                int holdH1 = ih1 - leg.EntryIH1;

                // Min-profit ratchet: FloorsTrailOnly = arm after trail, floor trail level (not hard stop).
                bool mayArm = ratchet.FloorsTrailOnly ? leg.TrailArmed : true;
                if (ratchet.Enabled && mayArm && !leg.LockArmed
                    && ExitRatchet.ShouldArm(true, leg.Entry, leg.AtrEntry, leg.TrailHigh, ratchet))
                    leg.LockArmed = true;

                double trailLevel = leg.TrailHigh - g.TrailingStopAtrMult * leg.AtrEntry;
                if (leg.LockArmed && ExitRatchet.LockPrice(true, leg.Entry, leg.AtrEntry, leg.TrailHigh, ratchet) is double lkPx)
                {
                    if (ratchet.FloorsTrailOnly) trailLevel = Math.Max(trailLevel, lkPx);
                    else                         leg.HardStop = ExitRatchet.Tighten(true, leg.HardStop, lkPx);
                }

                bool hitStop   = m15Price <= leg.HardStop;
                bool hitTarget = m15Price >= leg.Target;
                bool hitTrail  = leg.TrailArmed && m15Price < trailLevel;
                bool timedOut  = holdH1 >= g.MaxHoldCandles;

                bool hitTimeStop = false;
                if (!hitStop && !hitTarget && !hitTrail && !timedOut
                    && holdH1 >= g.TimeStopBars && g.MaxHoldCandles > g.TimeStopBars)
                {
                    double progress     = (double)(holdH1 - g.TimeStopBars) / (g.MaxHoldCandles - g.TimeStopBars);
                    double maxLossRatio = g.TimeStopLossPct * (1.0 - progress);
                    hitTimeStop = (m15Price - leg.Entry) / leg.Entry < -maxLossRatio;
                }

                if (hitStop || hitTarget || hitTrail || timedOut || hitTimeStop)
                {
                    double exitPx = hitStop   ? leg.HardStop :
                                    hitTarget ? leg.Target   : m15Price;
                    double fundingPnl = FundingRateSession.PnlPct(leg.EntryTime, m15[im15].Time, funding, isLong: true);
                    double ret = (exitPx - leg.Entry) / leg.Entry * 100.0
                               - TradeCost(hitStop, leg.AtrEntry, leg.Entry) + fundingPnl;
                    result.Add((m15[im15].Time, ret, "dip_long", leg.EntryRegimeBars, leg.EntryTime, leg.Entry));
                    legs.RemoveAt(li);
                }
                else legs[li] = leg;   // struct: write mutations back
            }
        }

        foreach (var leg in legs)
        {
            double finalPx = m15Closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(leg.EntryTime, m15[^1].Time, funding, isLong: true);
            double ret = (finalPx - leg.Entry) / leg.Entry * 100.0
                       - TradeCost(false, leg.AtrEntry, leg.Entry) + fundingPnl;
            result.Add((m15[^1].Time, ret, "dip_long", leg.EntryRegimeBars, leg.EntryTime, leg.Entry));
        }

        var st = legs.Count > 0 ? legs[0] : default;  // oldest leg for papertrade
        int finalHold = legs.Count > 0 ? h1.Length - 1 - st.EntryIH1 : 0;
        return (result, new DipLongTradeState(legs.Count > 0, st.Entry, st.HardStop, st.Target,
                                              st.TrailArmed, st.TrailHigh, finalHold));
    }

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx,
                                      double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);
}
