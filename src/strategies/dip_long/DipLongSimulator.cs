namespace TradingGA;

// Dip-long simulator — regime-gated bull pullback strategy.
//
// Structural complement of FadeShort: both fire in the same uptrend regime but
// at opposite ends of the RSI cycle. FadeShort fades overbought extensions;
// DipLong buys oversold/neutral pullbacks and rides the next leg up.
//
// The bull regime filter (RegimeLongEma must be rising AND close above it)
// ensures the strategy is silent during bear markets and dead-cat bounces —
// the exact periods where FadeLong or FadeShort operate.
//
// Entry (1h setup + 15m trigger):
//   1. Bull regime  — close > RegimeLongEma AND RegimeLongEma[now] > RegimeLongEma[slopeLookback bars ago]
//   2. Trend gate   — close > EmaPeriod EMA AND ADX(7) ≥ threshold
//   3. Dip setup    — RSI(7) ≤ RsiDipThreshold (pullback to 35–55 zone within uptrend)
//   4. Bullish BoS  — 15m close > previous 15m high (buyers committed to reversal)
//
// Exit (15m):
//   · Hard stop : recentSwingLow − StopLossAtrMult × h4ATR  (lowest low in last 20 h1 bars)
//   · Fixed TP  : entry + TakeProfitAtrMult × h4ATR
//   · Trailing  : armed after TrailingActivationAtrMult × ATR profit; trails at TrailingStopAtrMult × ATR
//   · Timeout   : MaxHoldCandles h1 bars
//
// RegimeBarsActive returned with each trade: consecutive h1 bars where the bull
// regime was confirmed at trade entry (used by DipLongGA for regime-conditional
// FoldScore filtering).
public static class DipLongSimulator
{
    // One open position. Multi-leg state must live at class scope — C# has no local structs.
    private struct Leg
    {
        public double   Entry, HardStop, Target, TrailHigh, AtrEntry;
        public bool     TrailArmed, LockArmed;
        public int      EntryIH1, EntryRegimeBars;
        public DateTime EntryTime;
    }

    private const int AtrPeriod        = 14;
    private const int RsiPeriod        = 7;
    private const int AdxPeriod        = 7;
    private const int SwingLowLookback = 20;   // h1 bars to locate the recent pullback low for stop placement

    // Cost model: see TradeCosts in src/core/Simulator.cs. Fee + slippage on BOTH sides
    // (magnitude from Config.SlippageBps alone) + this gap premium on stop exits only.
    // DipLong stops sit inside an established uptrend's pullback band, so the gap shape is
    // half the swing family's — that is a stop-placement difference, not a slippage knob.
    private const double StopGapAtrK = 0.015;

    // EntryTime/EntryPrice are exposed because `Time` is the EXIT bar on every simulator in this
    // repo, and four separate things need the ENTRY instead: variant selection by entry ATR,
    // leading-slice OOS sizing (aba8c1b documents the seam), accumulator acquisition quality,
    // and 1m execution. Appended as NAMED fields so existing t.Time / t.Return consumers are
    // untouched — the same low-risk shape already proven on AccumulationGridSimulator.
    // ratchet: opt-in minimum-profit floor (see src/core/ExitRatchet.cs). Default is disabled,
    // so production behaviour is bit-identical unless a caller asks for it.
    // ExecContext overload — the single point of contact. Delegates to the parameterised form
    // so behaviour is identical by construction; `default` reproduces production exactly.
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

        // Precompute consecutive bull-regime bar count at each h1 bar.
        // A bar qualifies when: close > RegimeLongEma AND RegimeLongEma rising over slopeLookback bars.
        // Counter resets to 0 on the first bar that breaks the regime condition.
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

        // ── Multi-leg position state ──────────────────────────────────────────────
        // maxLegs == 1 reproduces the previous single-position behaviour EXACTLY; that is the
        // regression check for this refactor, since the unit suite does not cover strategy P&L.
        //
        // A new leg is only permitted once EVERY open leg has armed its profit ratchet, i.e. is
        // already locked above breakeven and can no longer lose. That is what makes adding
        // near-risk-free: the incremental risk is the new leg's own stop, not compounded exposure
        // on an underwater position. This is pyramiding AFTER de-risking, the opposite of
        // averaging down, and is why it is safe on a long where DcaAndWait was not on a short.
        //
        // Legs are emitted as SEPARATE trades so PortfolioReplay counts each one against the
        // per-strategy, per-symbol and directional caps. A single trade secretly worth N legs is
        // the exact accounting hole that keeps RipShort's DcaAndWait disabled.
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

            // Entry is permitted when flat, or when every open leg is already locked in profit
            // and the leg budget allows another. Evaluated BEFORE exits above have run this bar's
            // removals, so a leg closing and a new one opening on the same bar are independent.
            bool canAdd = legs.Count < Math.Max(1, maxLegs)
                          && (legs.Count == 0 || legs.TrueForAll(l => l.LockArmed));
            if (canAdd)
            {
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    // Use the previous completed h4 bar — the current h4 bar aggregates future h1 bars
                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : h1Closes[h1Ref] * 0.08;

                    // Bull regime: close above long EMA AND long EMA is trending upward
                    bool regimeOk = h1Closes[h1Ref] > h1RegimeEma[h1Ref]
                                 && h1RegimeEma[h1Ref] > h1RegimeEma[h1Ref - slopeLen];

                    // Short-term trend: confirmed uptrend with momentum
                    bool trendOk = h1Closes[h1Ref] > h1Ema[h1Ref]
                                && h1Adx[h1Ref] >= g.AdxThreshold;

                    // Dip zone: RSI pulled back into 35–55 range (correcting, not reversing)
                    bool dipOk = h1Rsi[h1Ref] <= g.RsiDipThreshold;

                    if (regimeOk && trendOk && dipOk)
                    {
                        // Stop below the recent pullback low — if price breaks this, the dip became a trend reversal
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

                // 15m bullish BoS: close above previous 15m candle's high.
                // Enter at the OPEN of the next 15m bar — the BoS is only confirmed at bar close.
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
            // ── Exits: every open leg is evaluated independently ──────────────────────
            for (int li = legs.Count - 1; li >= 0; li--)
            {
                var leg = legs[li];
                if (m15Price > leg.TrailHigh) leg.TrailHigh = m15Price;
                if (!leg.TrailArmed && leg.TrailHigh - leg.Entry >= g.TrailingActivationAtrMult * leg.AtrEntry)
                    leg.TrailArmed = true;

                int holdH1 = ih1 - leg.EntryIH1;

                // Minimum-profit ratchet: once armed the stop only moves UP, so this leg can no
                // longer come back through breakeven — which is also the precondition for adding.
                // FloorsTrailOnly: the ratchet may only arm once the TRAIL has armed, and it
                // floors the trail's exit level rather than the hard stop — so the gene-tuned trail
                // still decides when to exit and the ratchet only stops it giving back past
                // breakeven. Otherwise (legacy) it floors the hard stop and pre-empts the trail.
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

        // Live state reports the OLDEST open leg, which is the one papertrade would be managing
        // first; with maxLegs == 1 this is identical to the previous single-position state.
        var st = legs.Count > 0 ? legs[0] : default;
        int finalHold = legs.Count > 0 ? h1.Length - 1 - st.EntryIH1 : 0;
        return (result, new DipLongTradeState(legs.Count > 0, st.Entry, st.HardStop, st.Target,
                                              st.TrailArmed, st.TrailHigh, finalHold));
    }

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx,
                                      double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);
}
