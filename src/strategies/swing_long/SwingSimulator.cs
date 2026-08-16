namespace TradingGA;

// FadeShortSimulator (defined here, not in fade_short/): fades overbought rallies in uptrends.
// Dual-TF: 1h setup + 15m entry/exit. h4 ATR for exit sizing. Invariant: stop = swingHigh + SL×ATR.
public static class FadeShortSimulator
{
    internal const int AtrPeriod    = 14;
    internal const int RsiPeriod    = 7;   // fixed, not a gene
    internal const int AdxPeriod    = 7;   // fixed, not a gene
    private const double StopGapAtrK = 0.030;  // gap premium on stop exits

    // Time = EXIT bar.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetFadeShortReturns(
        FadeShortGenotype g, ReadOnlySpan<Candle> candles)
    {
        var (trades, _) = RunSwing(g, candles);
        return trades.Select(t => (t.Item1, t.Item2, t.Item3, t.Item5, t.Item6)).ToList();
    }

    public record FadeShortTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,   // swingHigh + SL×ATR
        double MaeStop,    // entry + MAE×ATR ceiling
        double Target,
        bool   TrailArmed,
        double TrailLow,   // lowest close since entry
        int    HoldCount);

    public static FadeShortTradeState GetFadeShortTradeState(FadeShortGenotype g, ReadOnlySpan<Candle> candles)
    {
        var (_, state) = RunSwing(g, candles);
        return state;
    }

    private static (List<(DateTime, double, string, int, DateTime, double)> Trades, FadeShortTradeState FinalState)
        RunSwing(FadeShortGenotype g, ReadOnlySpan<Candle> candles)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                   + g.LookbackCandles;
        if (candles.Length <= warmup + 10)
            return ([], new FadeShortTradeState(false, 0, 0, 0, 0, false, 0, 0));

        var closes = CandleExt.Closes(candles);
        var highs  = CandleExt.Highs(candles);
        var lows   = CandleExt.Lows(candles);

        var ema = Trend.Ema(closes, g.EmaPeriod);
        var rsi = Momentum.Rsi(closes, RsiPeriod);
        var adx = Trend.Adx(highs, lows, closes, AdxPeriod);
        var atr = Volatility.Atr(highs, lows, closes, AtrPeriod);

        return SimulateCore(g, candles, closes, highs, lows, rsi, adx, atr, ema, warmup, candles.Length);
    }

    // GA fitness path: pre-computed indicators + caller-rented EMA buffer.
    internal static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetFadeShortReturnsPrecomputed(
        FadeShortGenotype g,
        ReadOnlySpan<Candle>  candles,
        double[]  closes, double[] highs, double[] lows,
        double[]  rsi,    double[] adx,   double[] atr,
        double[]  ema,   // caller fills this via Trend.EmaInto before each call
        int       rangeStart, int rangeEnd)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                   + g.LookbackCandles;
        int iStart = Math.Max(rangeStart, warmup);
        if (iStart >= rangeEnd || rangeEnd > candles.Length) return [];
        var (trades, _) = SimulateCore(g, candles, closes, highs, lows, rsi, adx, atr, ema, iStart, rangeEnd);
        return trades.Select(t => (t.Item1, t.Item2, t.Item3, t.Item5, t.Item6)).ToList();
    }

    // Regime-carrying variant for regime-conditional fold scoring.
    internal static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)>
        GetFadeShortReturnsPrecomputedWithRegime(
            FadeShortGenotype g,
            ReadOnlySpan<Candle>  candles,
            double[]  closes, double[] highs, double[] lows,
            double[]  rsi,    double[] adx,   double[] atr,
            double[]  ema,
            int       rangeStart, int rangeEnd)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                   + g.LookbackCandles;
        int iStart = Math.Max(rangeStart, warmup);
        if (iStart >= rangeEnd || rangeEnd > candles.Length) return [];
        var (trades, _) = SimulateCore(g, candles, closes, highs, lows, rsi, adx, atr, ema, iStart, rangeEnd);
        return trades;
    }

    // Core sim loop. iStart/iEnd are absolute indices; caller ensures iStart ≥ warmup.
    private static (List<(DateTime, double, string, int, DateTime, double)> Trades, FadeShortTradeState FinalState)
        SimulateCore(
            FadeShortGenotype g,
            ReadOnlySpan<Candle>  candles,
            double[]  closes, double[] highs, double[] lows,
            double[]  rsi,    double[] adx,   double[] atr, double[] ema,
            int       iStart, int      iEnd)
    {
        var result = new List<(DateTime, double, string, int, DateTime, double)>();

        // Consecutive uptrend bars (regime FadeShort fades). Separate regime EMA (100-500) from signal EMA.
        var regimeEma   = Trend.Ema(closes, g.RegimeEmaPeriod);
        var regimeSlope = Signals.EmaSlope(regimeEma, g.RegimeSlopeLookback);

        var bearBos        = Signals.BearishBoS(closes, lows);
        var bearDiv        = Signals.BearishDivergence(rsi, closes, g.LookbackCandles, g.RsiOverbought, g.RsiDivThreshold);
        var bigRallyArr    = Signals.MinMoveFilter(closes, lows, atr, g.LookbackCandles, g.MinRallyAtrMult);
        var strongTrendArr = Signals.AdxTrend(adx, closes, ema, g.AdxThreshold);

        bool     inTrade   = false;
        int      entryRegimeBars = 0;
        int      entryIdx  = 0;
        int      upBars    = 0;         // uptrend-bar counter, resets when regime breaks
        double   entry     = 0;
        DateTime entryTime = default;
        double hardStop   = 0;
        double maeStop    = 0;
        double target     = 0;
        double trailLow   = 0;
        bool   lockArmed  = false;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    holdCount  = 0;

        for (int i = iStart; i < iEnd; i++)
        {
            double price  = closes[i];
            double atrNow = atr[i] > 1e-10 ? atr[i] : price * 0.04;

            upBars = (price > regimeEma[i] && regimeSlope[i] > 0) ? upBars + 1 : 0;

            if (!inTrade)
            {
                // ── Regime check ─────────────────────────────────────────────────
                bool strongTrend = strongTrendArr[i];
                if (!strongTrend) continue;

                // ── Find swing high in lookback window ────────────────────────────
                var (swingHigh, highIdx, recentLow) = Signals.SwingHighLookback(
                    closes, highs, lows, i, g.LookbackCandles);

                // ── Min rally filter ──────────────────────────────────────────────
                bool bigRally = bigRallyArr[i];
                if (!bigRally) continue;

                // ── RSI divergence ────────────────────────────────────────────────
                double rsiAtHigh = rsi[highIdx];
                bool diverging   = bearDiv[i];
                if (!diverging) continue;

                // ── Structure break: close below previous candle's low ────────────
                bool bos = bearBos[i];
                if (!bos) continue;

                inTrade    = true;
                entry      = price;
                entryTime  = candles[i].Time;
                entryRegimeBars = upBars;
                entryIdx   = i;
                atrEntry   = atrNow;
                hardStop   = swingHigh + g.StopLossAtrMult * atrEntry;
                maeStop    = entry + g.MaeAtrMult * atrEntry;  // caps slow-grind losses
                target     = entry - g.TakeProfitAtrMult * atrEntry;
                trailLow   = price;
                trailArmed = false;
                lockArmed  = false;
                holdCount  = 0;
            }
            else
            {
                holdCount++;
                if (price < trailLow) trailLow = price;

                if (!trailArmed && entry - trailLow >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                bool hitHardStop = price >= hardStop;
                bool hitMae      = price >= maeStop;
                bool hitStop     = hitHardStop || hitMae;
                bool hitTarget   = price <= target;
                bool hitTrail    = trailArmed && price > trailLow + g.TrailingStopAtrMult * atrEntry;
                bool timedOut    = holdCount >= g.MaxHoldCandles;

                if (hitStop || hitTarget || hitTrail || timedOut)
                {
                    double exitPx = hitHardStop ? hardStop :
                                    hitMae      ? maeStop  :
                                    hitTarget   ? target   : price;
                    double ret = (entry - exitPx) / entry * 100.0
                               - TradeCost(hitStop, atrEntry, entry, EntryBarNotional(candles, entryIdx), g.PositionSizePct);
                    result.Add((candles[i].Time, ret, "fade_short", entryRegimeBars, entryTime, entry));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = closes[iEnd - 1];
            double ret = (entry - finalPx) / entry * 100.0
                       - TradeCost(false, atrEntry, entry, EntryBarNotional(candles, entryIdx), g.PositionSizePct);
            result.Add((candles[iEnd - 1].Time, ret, "fade_short", entryRegimeBars, entryTime, entry));
        }

        var finalState = new FadeShortTradeState(inTrade, entry, hardStop, maeStop, target,
            trailArmed, trailLow, holdCount);
        return (result, finalState);
    }

    // Multi-TF: h1 setup, 15m entry/exit, h4 ATR for exit sizing.
    public static Candle[] AggregateCandles(Candle[] candles, int factor)
    {
        var result = new List<Candle>(candles.Length / factor + 1);
        for (int i = 0; i + factor <= candles.Length; i += factor)
        {
            double high = candles[i].High, low = candles[i].Low, vol = 0;
            for (int j = i; j < i + factor; j++)
            {
                if (candles[j].High > high) high = candles[j].High;
                if (candles[j].Low  < low)  low  = candles[j].Low;
                vol += candles[j].Volume;
            }
            result.Add(new Candle(candles[i].Time, candles[i].Open, high, low, candles[i + factor - 1].Close, vol));
        }
        return result.ToArray();
    }

    // ExecContext overload for multi-TF path.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetFadeShortReturns(
        FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
        => GetFadeShortReturns(g, h1, m15, ctx.Funding, ctx.Ratchet);

    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetFadeShortReturns(
        FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
        FundingRateSession? funding = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunSwingMultiTF(g, h1, m15, funding: funding, ratchet: ratchet);
        return trades.Select(t => (t.Item1, t.Item2, t.Item3, t.Item5, t.Item6)).ToList();
    }

    // Regime-carrying variant for regime-conditional fold scoring.
    internal static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)>
        GetFadeShortReturnsWithRegime(
            FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
            FundingRateSession? funding = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunSwingMultiTF(g, h1, m15, funding: funding, ratchet: ratchet);
        return trades;
    }

    public static FadeShortTradeState GetFadeShortTradeState(FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunSwingMultiTF(g, h1, m15);
        return state;
    }

    // Scored trades for ranked portfolio. Score = rsiExcess × adxRatio × rallyRatio.
    public static List<ScoredTrade> GetScoredSwingTrades(string coin, FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var scored = new List<ScoredTrade>();
        RunSwingMultiTF(g, h1, m15, coin, scored);
        return scored;
    }

    private static (List<(DateTime, double, string, int, DateTime, double)> Trades, FadeShortTradeState FinalState)
        RunSwingMultiTF(FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
                        string? coin = null, List<ScoredTrade>? scoredOut = null,
                        FundingRateSession? funding = null, RatchetConfig ratchet = default)
    {
        int h1Warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                       + g.LookbackCandles + 2;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new FadeShortTradeState(false, 0, 0, 0, 0, false, 0, 0));

        var h1Closes = CandleExt.Closes(h1);
        var h1Highs  = CandleExt.Highs(h1);
        var h1Lows   = CandleExt.Lows(h1);

        var h1Ema = Trend.Ema(h1Closes, g.EmaPeriod);
        var h1Rsi = Momentum.Rsi(h1Closes, RsiPeriod);
        var h1Adx = Trend.Adx(h1Highs, h1Lows, h1Closes, AdxPeriod);
        var h1Atr = Volatility.Atr(h1Highs, h1Lows, h1Closes, AtrPeriod);

        // h4 ATR — exit sizing scaled to holding timeframe, not entry precision
        var h4      = AggregateCandles(h1.ToArray(), 4);
        var h4Highs = CandleExt.Highs(h4);
        var h4Lows  = CandleExt.Lows(h4);
        var h4Cls   = CandleExt.Closes(h4);
        var h4Atr   = Volatility.Atr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        var m15Closes = CandleExt.Closes(m15);
        var m15Lows   = CandleExt.Lows(m15);

        // Consecutive uptrend bars for regime-conditional fold scoring.
        int[] upRegimeBarsAtBar = new int[h1.Length];
        {
            var h1RegimeEma = Trend.Ema(h1Closes, g.RegimeEmaPeriod);
            var emaSlope    = Signals.EmaSlope(h1RegimeEma, g.RegimeSlopeLookback);
            int running = 0;
            for (int i = 0; i < h1.Length; i++)
            {
                bool regimeBar = h1Closes[i] > h1RegimeEma[i] && emaSlope[i] > 0;
                running = regimeBar ? running + 1 : 0;
                upRegimeBarsAtBar[i] = running;
            }
        }

        var result = new List<(DateTime, double, string, int, DateTime, double)>();

        bool   inTrade    = false;
        int    entryRegimeBars = 0;
        double entry      = 0;
        double hardStop   = 0;
        double maeStop    = 0;
        double target     = 0;
        double trailLow   = 0;
        bool   lockArmed  = false;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    entryIH1   = 0;

        int    cachedH1Ref     = -1;
        bool   cachedSetupMet  = false;
        double cachedSwingHigh = 0;
        double cachedAtrRef    = 0;
        double cachedScore     = 0;   // signal quality for ranked sim

        double   entryScore = 0;
        DateTime entryTime  = default;

        var bearDiv    = Signals.BearishDivergence(h1Rsi, h1Closes, g.LookbackCandles, g.RsiOverbought, g.RsiDivThreshold);
        var bigRallyArr = Signals.MinMoveFilter(h1Closes, h1Lows, h1Atr, g.LookbackCandles, g.MinRallyAtrMult);

        int m15Start = (h1Warmup + 1) * 4;
        int m15Limit = h1.Length * 4;

        for (int im15 = m15Start; im15 < Math.Min(m15.Length, m15Limit); im15++)
        {
            int ih1   = im15 / 4;
            int h1Ref = ih1 - 1;   // last fully-closed h1 bar — no look-ahead

            if (h1Ref < h1Warmup || h1Ref >= h1.Length) continue;

            double m15Price = m15Closes[im15];

            if (!inTrade)
            {
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    double atrH1 = h1Atr[h1Ref] > 1e-10 ? h1Atr[h1Ref] : h1Closes[h1Ref] * 0.02;
                    int h4Ref = Math.Max(0, h1Ref / 4 - 1);  // previous completed h4 bar
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : atrH1 * 4;

                    bool strongTrend = h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref];
                    if (strongTrend)
                    {
                        var (swingHigh, highIdx, recentLow) = Signals.SwingHighLookback(
                            h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);

                        double rsiAtHigh = h1Rsi[highIdx];
                        bool bigRally = bigRallyArr[h1Ref];
                        bool diverging = bearDiv[h1Ref];

                        if (bigRally && diverging)
                        {
                            cachedSetupMet  = true;
                            cachedSwingHigh = swingHigh;
                            cachedAtrRef    = atrH4;
                            double rsiExcess  = rsiAtHigh - g.RsiOverbought;
                            double adxRatio   = h1Adx[h1Ref] / g.AdxThreshold;
                            double rallyRatio = (swingHigh - recentLow) / (g.MinRallyAtrMult * atrH1);
                            cachedScore = rsiExcess * adxRatio * rallyRatio;
                        }
                    }
                }

                // 15m bearish BoS trigger; enter at next bar's open.
                if (cachedSetupMet && m15Closes[im15] < m15Lows[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    inTrade    = true;
                    entry      = m15[nextBar].Open;
                    atrEntry   = cachedAtrRef;
                    hardStop   = cachedSwingHigh + g.StopLossAtrMult * atrEntry;
                    maeStop    = entry + g.MaeAtrMult * atrEntry;
                    target     = entry - g.TakeProfitAtrMult * atrEntry;
                    trailLow   = entry;
                    trailArmed = false;
                    lockArmed  = false;
                    entryIH1   = nextBar / 4;
                    entryScore = cachedScore;
                    entryTime  = m15[nextBar].Time;
                    entryRegimeBars = entryIH1 < upRegimeBarsAtBar.Length ? upRegimeBarsAtBar[entryIH1] : 0;
                }
            }
            else
            {
                if (m15Price < trailLow) trailLow = m15Price;
                if (!trailArmed && entry - trailLow >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;
                if (ratchet.Enabled && !lockArmed
                    && ExitRatchet.ShouldArm(false, entry, atrEntry, trailLow, ratchet))
                    lockArmed = true;
                if (lockArmed && ExitRatchet.LockPrice(false, entry, atrEntry, trailLow, ratchet) is double lkPxS)
                    hardStop = ExitRatchet.Tighten(false, hardStop, lkPxS);

                bool hitHardStop = m15Price >= hardStop;
                bool hitMae      = m15Price >= maeStop;
                bool hitStop     = hitHardStop || hitMae;
                bool hitTarget   = m15Price <= target;
                bool hitTrail    = trailArmed && m15Price > trailLow + g.TrailingStopAtrMult * atrEntry;
                bool timedOut    = holdH1 >= g.MaxHoldCandles;

                if (hitStop || hitTarget || hitTrail || timedOut)
                {
                    double exitPx = hitHardStop ? hardStop :
                                    hitMae      ? maeStop  :
                                    hitTarget   ? target   : m15Price;
                    double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[im15].Time, funding, isLong: false);
                    double ret = (entry - exitPx) / entry * 100.0
                               - TradeCost(hitStop, atrEntry, entry, EntryBarNotional(h1, entryIH1), g.PositionSizePct)
                               + fundingPnl;
                    result.Add((m15[im15].Time, ret, "fade_short", entryRegimeBars, entryTime, entry));
                    scoredOut?.Add(new ScoredTrade(coin!, "swing", entryTime, m15[im15].Time, ret, entryScore));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[^1].Time, funding, isLong: false);
            double ret = (entry - finalPx) / entry * 100.0
                       - TradeCost(false, atrEntry, entry, EntryBarNotional(h1, entryIH1), g.PositionSizePct)
                       + fundingPnl;
            result.Add((m15[^1].Time, ret, "fade_short", entryRegimeBars, entryTime, entry));
            scoredOut?.Add(new ScoredTrade(coin!, "swing", entryTime, m15[^1].Time, ret, entryScore));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new FadeShortTradeState(inTrade, entry, hardStop, maeStop, target, trailArmed, trailLow, finalHold));
    }

    // Entry bar notional in quote currency. Zero when volume missing (impact term vanishes).
    internal static double EntryBarNotional(ReadOnlySpan<Candle> bars, int i)
        => (uint)i < (uint)bars.Length ? bars[i].Volume * bars[i].Close : 0.0;

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx,
                                      double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);

}

// SwingLongSimulator: bull-regime RSI bullish divergence + bullish BoS. Mirror of FadeShort.
public static class SwingLongSimulator
{
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetSwingLongReturns(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
        => GetSwingLongReturns(g, h1, m15, ctx.Funding, ctx.Ratchet);

    // Router-as-indicator: signed BTC alignment score (+Bull, -Bear, 0 otherwise). Null = absent.
    public static Func<DateTime, double>? BtcRegimeProbe;

    // Control switch: GRAVITY_SWINGLONG_NOBTC=1 forces gate open, preserving RNG stream.
    private static readonly bool NoBtcGate =
        Environment.GetEnvironmentVariable("GRAVITY_SWINGLONG_NOBTC") == "1";

    internal const int AtrPeriod = 14;
    internal const int RsiPeriod =  7;
    internal const int AdxPeriod =  7;

    private const double StopGapAtrK = 0.030;  // gap premium on stop exits

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx,
                                      double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);

    public record SwingLongTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailHigh,   // highest close since entry
        int    HoldCount);

    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetSwingLongReturns(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
        FundingRateSession? funding = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunSwingLongMultiTF(g, h1, m15, funding, ratchet);
        return trades;
    }

    public static SwingLongTradeState GetSwingLongTradeState(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunSwingLongMultiTF(g, h1, m15);
        return state;
    }

    public static SwingLongTradeState GetSwingLongTradeState(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
        FundingRateSession? funding)
    {
        var (_, state) = RunSwingLongMultiTF(g, h1, m15, funding);
        return state;
    }

    private static (List<(DateTime, double, string, DateTime, double)> Trades, SwingLongTradeState FinalState)
        RunSwingLongMultiTF(SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
                            FundingRateSession? funding = null, RatchetConfig ratchet = default)
    {
        int h1Warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                       + g.LookbackCandles + 2;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new SwingLongTradeState(false, 0, 0, 0, false, 0, 0));

        var h1Closes = CandleExt.Closes(h1);
        var h1Highs  = CandleExt.Highs(h1);
        var h1Lows   = CandleExt.Lows(h1);

        var h1Ema = Trend.Ema(h1Closes, g.EmaPeriod);
        var h1Rsi = Momentum.Rsi(h1Closes, RsiPeriod);
        var h1Adx = Trend.Adx(h1Highs, h1Lows, h1Closes, AdxPeriod);
        var h1Atr = Volatility.Atr(h1Highs, h1Lows, h1Closes, AtrPeriod);

        var h4      = FadeShortSimulator.AggregateCandles(h1.ToArray(), 4);
        var h4Highs = CandleExt.Highs(h4);
        var h4Lows  = CandleExt.Lows(h4);
        var h4Cls   = CandleExt.Closes(h4);
        var h4Atr   = Volatility.Atr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        var m15Closes = CandleExt.Closes(m15);
        var m15Highs  = CandleExt.Highs(m15);

        var result = new List<(DateTime, double, string, DateTime, double)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
        double target     = 0;
        double trailHigh  = 0;
        bool   lockArmed  = false;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    entryIH1   = 0;
        DateTime entryTime = default;

        int    cachedH1Ref    = -1;
        bool   cachedSetupMet = false;
        double cachedSwingLow = 0;
        double cachedAtrRef   = 0;

        var bullDiv    = Signals.BullishDivergence(h1Rsi, h1Closes, g.LookbackCandles, g.RsiOversold, g.RsiDivThreshold);
        var bigDropArr = Signals.MinMoveFilter(h1Highs, h1Closes, h1Atr, g.LookbackCandles, g.MinDeclineAtrMult);
        var h1BullBos  = Signals.BullishBoS(h1Closes, h1Highs);

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

                    double atrH1 = h1Atr[h1Ref] > 1e-10 ? h1Atr[h1Ref] : h1Closes[h1Ref] * 0.02;

                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);  // previous completed h4 bar
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref] : atrH1 * 4;

                    bool strongTrend = h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref];
                    if (strongTrend)
                    {
                        var (swingLow, _, recentHigh) = Signals.SwingLowLookback(
                            h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);

                        bool bigDrop = bigDropArr[h1Ref];
                        bool diverging  = bullDiv[h1Ref];
                        bool h1Bos = h1BullBos[h1Ref];

                        // BTC alignment gate: BtcAlignWeight=0 is exact no-op (required=-1, always passes).
                        double btcScore = BtcRegimeProbe?.Invoke(h1[h1Ref].Time) ?? 1.0;
                        double required = (NoBtcGate || g.BtcAlignWeight <= 0.0)
                            ? -1.0
                            : -1.0 + 2.0 * g.BtcAlignWeight;
                        bool btcOk = btcScore >= required;

                        if (bigDrop && diverging && h1Bos && btcOk)
                        {
                            cachedSetupMet = true;
                            cachedSwingLow = swingLow;
                            cachedAtrRef   = atrH4;
                        }
                    }
                }

                // 15m bullish BoS trigger; enter at next bar's open.
                if (cachedSetupMet && m15Closes[im15] > m15Highs[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    inTrade    = true;
                    entry      = m15[nextBar].Open;
                    atrEntry   = cachedAtrRef;
                    hardStop   = cachedSwingLow - g.StopLossAtrMult * atrEntry;
                    target     = entry + g.TakeProfitAtrMult * atrEntry;
                    trailHigh  = entry;
                    trailArmed = false;
                    lockArmed  = false;
                    entryIH1   = nextBar / 4;
                    entryTime  = m15[nextBar].Time;
                }
            }
            else
            {
                if (m15Price > trailHigh) trailHigh = m15Price;
                if (!trailArmed && trailHigh - entry >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

                // Min-profit ratchet (was missing — caught by conformance tests).
                if (ratchet.Enabled && !lockArmed
                    && ExitRatchet.ShouldArm(true, entry, atrEntry, trailHigh, ratchet))
                    lockArmed = true;
                if (lockArmed && ExitRatchet.LockPrice(true, entry, atrEntry, trailHigh, ratchet) is double lkPxL)
                    hardStop = ExitRatchet.Tighten(true, hardStop, lkPxL);

                bool hitStop   = m15Price <= hardStop;
                bool hitTarget = m15Price >= target;
                bool hitTrail  = trailArmed && m15Price < trailHigh - g.TrailingStopAtrMult * atrEntry;
                bool timedOut  = holdH1 >= g.MaxHoldCandles;

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
                    double exitPx   = hitStop   ? hardStop :
                                      hitTarget ? target   : m15Price;
                    double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[im15].Time, funding, isLong: true);
                    double ret      = (exitPx - entry) / entry * 100.0
                                    - TradeCost(hitStop, atrEntry, entry) + fundingPnl;
                    result.Add((m15[im15].Time, ret, "swing_long", entryTime, entry));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[^1].Time, funding, isLong: true);
            double ret     = (finalPx - entry) / entry * 100.0
                           - TradeCost(isStop: false, atrEntry, entry) + fundingPnl;
            result.Add((m15[^1].Time, ret, "swing_long", entryTime, entry));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new SwingLongTradeState(inTrade, entry, hardStop, target, trailArmed, trailHigh, finalHold));
    }
}
