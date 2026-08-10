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
    public static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)> GetDipLongReturns(
        DipLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
        FundingRateSession? funding = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunDipLongMultiTF(g, h1, m15, funding, ratchet);
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
                          FundingRateSession? funding = null, RatchetConfig ratchet = default)
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

        bool   inTrade       = false;
        double entry         = 0;
        double hardStop      = 0;
        double target        = 0;
        double trailHigh     = 0;
        double atrEntry      = 0;
        bool   trailArmed    = false;
        bool   lockArmed     = false;
        int    entryIH1      = 0;
        int    entryRegimeBars = 0;
        DateTime entryTime   = default;

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

            if (!inTrade)
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
                    inTrade        = true;
                    entry          = m15[nextBar].Open;
                    atrEntry       = cachedAtrH4;
                    hardStop       = cachedSwingLow - g.StopLossAtrMult * atrEntry;
                    target         = entry + g.TakeProfitAtrMult * atrEntry;
                    trailHigh      = entry;
                    trailArmed     = false;
                    lockArmed      = false;
                    entryIH1       = nextBar / 4;
                    entryRegimeBars = cachedRegimeBars;
                    entryTime      = m15[nextBar].Time;
                }
            }
            else
            {
                if (m15Price > trailHigh) trailHigh = m15Price;
                if (!trailArmed && trailHigh - entry >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

                // Minimum-profit ratchet: once armed, the stop can only move UP for a long, so the
                // trade can no longer come back through breakeven. trailHigh is already the running
                // favourable excursion, so nothing extra needs tracking.
                if (ratchet.Enabled && !lockArmed
                    && ExitRatchet.ShouldArm(true, entry, atrEntry, trailHigh, ratchet))
                    lockArmed = true;
                if (lockArmed && ExitRatchet.LockPrice(true, entry, atrEntry, ratchet) is double lkPx)
                    hardStop = ExitRatchet.Tighten(true, hardStop, lkPx);

                bool hitStop   = m15Price <= hardStop;
                bool hitTarget = m15Price >= target;
                bool hitTrail  = trailArmed && m15Price < trailHigh - g.TrailingStopAtrMult * atrEntry;
                bool timedOut  = holdH1 >= g.MaxHoldCandles;

                // Time-decay stop: after TimeStopBars bars, tolerated loss narrows linearly
                // from TimeStopLossPct down to 0% at MaxHoldCandles.
                bool hitTimeStop = false;
                if (!hitStop && !hitTarget && !hitTrail && !timedOut
                    && holdH1 >= g.TimeStopBars && g.MaxHoldCandles > g.TimeStopBars)
                {
                    double progress     = (double)(holdH1 - g.TimeStopBars) / (g.MaxHoldCandles - g.TimeStopBars);
                    double maxLossRatio = g.TimeStopLossPct * (1.0 - progress);
                    hitTimeStop = (m15Price - entry) / entry < -maxLossRatio;
                }

                if (hitStop || hitTarget || hitTrail || timedOut || hitTimeStop)
                {
                    double exitPx = hitStop   ? hardStop :
                                    hitTarget ? target   : m15Price;
                    double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[im15].Time, funding, isLong: true);
                    double ret = (exitPx - entry) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry) + fundingPnl;
                    result.Add((m15[im15].Time, ret, "dip_long", entryRegimeBars, entryTime, entry));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[^1].Time, funding, isLong: true);
            double ret = (finalPx - entry) / entry * 100.0 - TradeCost(false, atrEntry, entry) + fundingPnl;
            result.Add((m15[^1].Time, ret, "dip_long", entryRegimeBars, entryTime, entry));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new DipLongTradeState(inTrade, entry, hardStop, target, trailArmed, trailHigh, finalHold));
    }

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK);
}
