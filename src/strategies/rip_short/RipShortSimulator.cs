namespace TradingGA;

// Rip-short simulator — regime-gated bear trend-continuation strategy.
//
// Direction-mirror of DipLongSimulator: same dual-EMA regime filter and dual-TF
// setup, flipped to the short side. DipLong buys RSI dips in confirmed uptrends;
// RipShort shorts RSI relief rallies ("rips") in confirmed downtrends and rides
// the next leg down.
//
// The bear regime filter (RegimeLongEma must be falling AND close below it) keeps
// the strategy silent during bull markets — the exact periods where DipLong /
// SwingLong operate.
//
// Entry (1h setup + 15m trigger):
//   1. Bear regime  — close < RegimeLongEma AND RegimeLongEma[now] < RegimeLongEma[slopeLookback bars ago]
//   2. Trend gate   — close < EmaPeriod EMA AND ADX(7) ≥ threshold
//   3. Rally setup  — RSI(7) ≥ RsiRallyThreshold (relief bounce within the downtrend)
//   4. Bearish BoS  — 15m close < previous 15m low (sellers committed to continuation)
//
// Exit (15m) — WICK-TRIGGERED stop/target (pessimistic): shorts into bear rallies
//   are squeeze-prone, so intrabar highs/lows trigger the hard levels, not closes.
//   · Hard stop : recentSwingHigh + StopLossAtrMult × h4ATR  (highest high in last 20 h1 bars)
//                 triggered when the 15m HIGH ≥ stop
//   · Fixed TP  : entry − TakeProfitAtrMult × h4ATR, triggered when the 15m LOW ≤ target
//                 (same bar touches both → STOP wins)
//   · Trailing  : armed after TrailingActivationAtrMult × ATR profit; exits at market
//                 when 15m close climbs TrailingStopAtrMult × ATR above the trough
//   · TimeStop  : after TimeStopBars, tolerated adverse (above-entry) move narrows to 0 at MaxHoldCandles
//   · Timeout   : MaxHoldCandles h1 bars
//
// Funding: Bybit settles perp funding every 8h (00:00/08:00/16:00 UTC). A short
//   RECEIVES funding when the rate is positive and PAYS when negative. With a
//   FundingRateSession we use the actual per-settlement rate; without one we apply
//   a flat pessimistic −0.01%/8h estimate (shorts pay the baseline).
//
// RegimeBarsActive returned per trade: consecutive h1 bars where the bear regime
// was confirmed at entry (used by RipShortGA for regime-conditional FoldScore).
public static class RipShortSimulator
{
    private const int AtrPeriod         = 14;
    private const int RsiPeriod         = 7;
    private const int AdxPeriod         = 7;
    private const int SwingHighLookback = 20;   // h1 bars to locate the recent rally high for stop placement

    private const double FeeExchange = 0.11;
    private const double SlipK       = 0.025;
    private const double SlipStopGap = 0.030;

    // Experimental loss-mitigation override, applied post-hoc on top of a frozen
    // genotype (not GA-evolved — sparse bear-window data already overfits the
    // existing 14-gene search, adding more dimensions would make that worse).
    // MaxHoldCandles is a time-based exit, independent of the hard ATR stop —
    // suppressing it while calm+losing just gives the trade more time; the hard
    // stop (fixed at entry) remains the tail-risk backstop regardless of mode.
    public enum ExitOverrideMode { None, WaitForBreakeven, DcaAndWait }

    public record ExitOverrideConfig(
        ExitOverrideMode Mode,
        double AtrGateRatio        = 1.4,   // local h4 ATR ratio must be ≤ this to keep waiting ("calm")
        double DcaAtrMult          = 1.2,   // adverse move (in ATR) that triggers the single DCA add
        int    MaxExtraHoldCandles = 60);   // absolute safety cap on extra bars beyond MaxHoldCandles

    public static List<(DateTime Time, double Return, string Kind)> GetRipShortReturns(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null,
        ExitOverrideConfig? overrideCfg = null)
    {
        var (trades, _) = RunRipShortMultiTF(g, h1, m15, funding, overrideCfg);
        return trades.Select(t => (t.Item1, t.Item2, t.Item3)).ToList();
    }

    // 4-tuple variant carrying RegimeBarsActive — consumed by RipShortGA for
    // regime-conditional FoldScore filtering.
    internal static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive)> GetRipShortReturnsWithRegime(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null,
        ExitOverrideConfig? overrideCfg = null)
    {
        var (trades, _) = RunRipShortMultiTF(g, h1, m15, funding, overrideCfg);
        return trades;
    }

    public record RipShortTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailLow,    // lowest 15m close seen since entry (trail reference for a short)
        int    HoldCount);

    public static RipShortTradeState GetRipShortTradeState(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null)
    {
        var (_, state) = RunRipShortMultiTF(g, h1, m15, funding);
        return state;
    }

    private static (List<(DateTime, double, string, int)> Trades, RipShortTradeState FinalState)
        RunRipShortMultiTF(RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding,
        ExitOverrideConfig? overrideCfg = null)
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

        // Local h4 ATR ratio (current vs trailing 20-bar average) — same "is
        // volatility elevated" signal DynamicGuardSession computes from BTC, but
        // measured on the traded coin itself since that's what actually matters
        // for whether it's safe to keep waiting on this position.
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

        // Precompute consecutive bear-regime bar count at each h1 bar.
        // A bar qualifies when: close < RegimeLongEma AND RegimeLongEma falling over slopeLookback bars.
        // Counter resets to 0 on the first bar that breaks the regime condition.
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

        var result = new List<(DateTime, double, string, int)>();

        bool     inTrade         = false;
        double   entry           = 0;
        double   hardStop        = 0;
        double   target          = 0;
        double   trailLow        = 0;
        double   atrEntry        = 0;
        bool     trailArmed      = false;
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

                    // Use the previous completed h4 bar — the current h4 bar aggregates future h1 bars
                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : h1Closes[h1Ref] * 0.08;

                    // Bear regime: close below long EMA AND long EMA is trending downward
                    bool regimeOk = h1Closes[h1Ref] < h1RegimeEma[h1Ref]
                                 && h1RegimeEma[h1Ref] < h1RegimeEma[h1Ref - slopeLen];

                    // Short-term trend: confirmed downtrend with momentum
                    bool trendOk = h1Closes[h1Ref] < h1Ema[h1Ref]
                                && h1Adx[h1Ref] >= g.AdxThreshold;

                    // Rally zone: RSI bounced up into 40–60 range (relief rally, not new low)
                    bool rallyOk = h1Rsi[h1Ref] >= g.RsiRallyThreshold;

                    if (regimeOk && trendOk && rallyOk)
                    {
                        // Stop above the recent rally high — if price exceeds it, the rip became a reversal
                        int    lbStart   = Math.Max(0, h1Ref - SwingHighLookback);
                        double swingHigh = h1Highs[lbStart];
                        for (int j = lbStart + 1; j <= h1Ref; j++)
                            if (h1Highs[j] > swingHigh) swingHigh = h1Highs[j];

                        cachedSetupMet   = true;
                        cachedSwingHigh  = swingHigh;
                        cachedAtrH4      = atrH4;
                        cachedRegimeBars = bearRegimeBarsAtBar[h1Ref];
                    }
                }

                // 15m bearish BoS: close below previous 15m candle's low.
                // Enter at the OPEN of the next 15m bar — the BoS is only confirmed at bar close.
                if (cachedSetupMet && m15Closes[im15] < m15Lows[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    inTrade         = true;
                    entry           = m15[nextBar].Open;
                    atrEntry        = cachedAtrH4;
                    hardStop        = cachedSwingHigh + g.StopLossAtrMult * atrEntry;
                    target          = entry - g.TakeProfitAtrMult * atrEntry;
                    trailLow        = entry;
                    trailArmed      = false;
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

                // Wick-triggered hard levels (pessimistic — intrabar extremes trigger)
                bool hitStop   = m15Highs[im15] >= hardStop;
                bool hitTarget = m15Lows[im15]  <= target;
                bool hitTrail  = trailArmed && m15Price > trailLow + g.TrailingStopAtrMult * atrEntry;
                bool pastMaxHold = holdH1 >= g.MaxHoldCandles;

                // Loss-mitigation override: past MaxHoldCandles, still losing, and calm
                // enough (local ATR ratio ≤ gate) → keep waiting for breakeven instead of
                // forcing the exit. Hard stop/target/trail above are untouched and remain
                // the real risk cap; this only suppresses the time-based exit.
                bool waiting = false;
                if (pastMaxHold && overrideCfg != null)
                {
                    int    h4RefNow    = Math.Min(h4AtrRatio.Length - 1, Math.Max(0, ih1 / 4 - 1));
                    double atrRatioNow = h4AtrRatio.Length > 0 ? h4AtrRatio[h4RefNow] : 1.0;
                    waiting = ShouldWaitPastTimeout(m15Price, entry, atrRatioNow, holdH1 - g.MaxHoldCandles, overrideCfg);
                    if (waiting) everWaited = true;

                    if (ShouldDca(waiting, dcaDone, m15Price, entry, atrEntry, overrideCfg))
                    {
                        entry       = (entry + m15Price) / 2.0;   // blended cost basis, equal-size add
                        dcaDone     = true;
                        dcaSizeMult = 2.0;                        // total capital deployed doubled
                    }
                }
                bool timedOut = pastMaxHold && !waiting;

                // Time-decay stop: after TimeStopBars bars, tolerated adverse (above-entry) move
                // narrows linearly from TimeStopLossPct down to 0% at MaxHoldCandles.
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
                    // Same bar touches both stop and target → STOP wins (pessimistic)
                    double exitPx = hitStop   ? hardStop :
                                    hitTarget ? target   : m15Price;
                    double fundingPnl = FundingPnl(entryTime, m15[im15].Time, funding);
                    double ret = ((entry - exitPx) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry) + fundingPnl) * dcaSizeMult;
                    string kind = dcaDone ? "ripshort_dca" : everWaited ? "ripshort_wait" : "ripshort";
                    result.Add((m15[im15].Time, ret, kind, entryRegimeBars));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx    = m15Closes[^1];
            double fundingPnl = FundingPnl(entryTime, m15[^1].Time, funding);
            double ret = ((entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry) + fundingPnl) * dcaSizeMult;
            string kind = dcaDone ? "ripshort_dca" : everWaited ? "ripshort_wait" : "ripshort";
            result.Add((m15[^1].Time, ret, kind, entryRegimeBars));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new RipShortTradeState(inTrade, entry, hardStop, target, trailArmed, trailLow, finalHold));
    }

    // Pure decision: keep waiting past MaxHoldCandles instead of forcing the timeout exit?
    // Short is underwater (price > entry), local volatility is calm, and the safety cap
    // on extra bars hasn't been reached.
    internal static bool ShouldWaitPastTimeout(
        double price, double entry, double atrRatioNow, int extraHoldBars, ExitOverrideConfig cfg)
    {
        if (cfg.Mode == ExitOverrideMode.None) return false;
        bool losing     = price > entry;
        bool calmEnough = atrRatioNow <= cfg.AtrGateRatio;
        bool underCap   = extraHoldBars < cfg.MaxExtraHoldCandles;
        return losing && calmEnough && underCap;
    }

    // Pure decision: trigger the single DCA add? Only while waiting, only once per
    // trade, only after price has moved DcaAtrMult×ATR further against the position.
    internal static bool ShouldDca(
        bool waiting, bool dcaDone, double price, double entry, double atrEntry, ExitOverrideConfig cfg) =>
        waiting && cfg.Mode == ExitOverrideMode.DcaAndWait && !dcaDone
        && price - entry >= cfg.DcaAtrMult * atrEntry;

    private static double TradeCost(bool isStop, double atrEntry, double entryPx)
    {
        double atrPct = atrEntry / entryPx * 100.0;
        double slip   = SlipK * atrPct * 2.0 + (isStop ? SlipStopGap * atrPct : 0.0);
        return FeeExchange + slip;
    }

    // Funding PnL over the life of a short position, in percentage points.
    // Bybit settles every 8h at 00:00/08:00/16:00 UTC. A short receives the rate
    // when positive and pays when negative: fundingPnl += rate × 100 per boundary.
    // Without a session, apply a flat pessimistic −0.01%/8h (shorts pay baseline).
    private static double FundingPnl(DateTime entryTime, DateTime exitTime, FundingRateSession? funding)
    {
        if (exitTime <= entryTime) return 0.0;

        if (funding == null)
        {
            double heldHours = (exitTime - entryTime).TotalHours;
            return -0.01 * (heldHours / 8.0);
        }

        double pnl = 0.0;
        // First 8h settlement boundary strictly after entryTime (grid anchored at 00:00 UTC).
        DateTime t = entryTime.Date;
        while (t <= entryTime) t = t.AddHours(8);
        for (; t <= exitTime; t = t.AddHours(8))
            pnl += funding.GetRate(t) * 100.0;
        return pnl;
    }
}
