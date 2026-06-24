namespace TradingGA;

// Swing trading simulator — operates on 4h candles, short-only profit-taking thesis.
//
// Entry: all of the following must align on the same candle:
//   1. Strong uptrend    — ADX ≥ threshold AND close > EMA
//   2. Min rally filter  — recent high is ≥ MinRallyAtrMult × ATR above the recent low
//                          (confirms there's a real extended move to fade, not noise)
//   3. RSI divergence    — RSI at the current candle is ≥ RsiDivThreshold below the RSI
//                          at the most recent swing high, AND that swing-high RSI cleared
//                          RsiOverbought (buyers were exhausted at the top)
//   4. Structure break   — close below the previous candle's low (market committed to reversal)
//
// Exit: swing-high stop · fixed ATR target · trailing stop once armed · max-hold timeout.
//   Stop = swingHigh + StopLossAtrMult × ATR: invalidates the thesis (new high printed).
//   Wider than a fixed-from-entry stop but correct — wick noise below the swing high is
//   noise; price exceeding the swing high means the fade was wrong.
// ATR multiples use the 14-period ATR fixed at entry for the life of the trade.
public static class FadeShortSimulator
{
    internal const int AtrPeriod    = 14;
    internal const int RsiPeriod    = 7;   // fixed — not a gene; GA always converges here
    internal const int AdxPeriod    = 7;   // fixed — not a gene; GA always converges here (faster ADX, more reactive to trend onset)
    // Fee model: exchange taker fee + ATR-proportional slippage.
    // Execution is on 15m bars; ATR reference is h4. Calibrated so the average round-trip
    // stays ~0.21% for liquid coins (h4 ATR ≈ 2-3% of price), while volatile coins
    // (memes, h4 ATR ≈ 5-8%) pay proportionally more, especially on stop exits.
    //   SlipK       = 0.025 → slip per side = 0.025 × atrPct  (at 3% ATR → 0.075% each side)
    //   SlipStopGap = 0.030 → extra gap on stops               (at 3% ATR → +0.090% extra)
    // Liquid coin TP ≈ 0.185%, Stop ≈ 0.275%. Meme TP ≈ 0.26%, Stop ≈ 0.41%.
    private const double FeeExchange = 0.11;   // 0.055% taker × 2 sides
    private const double SlipK       = 0.025;  // entry + normal-exit slip = k × (atr/price × 100)
    private const double SlipStopGap = 0.030;  // stop gap premium = k × (atr/price × 100) — doubled to reflect fast-stop fill risk

    public static List<(DateTime Time, double Return, string Kind)> GetFadeShortReturns(
        FadeShortGenotype g, ReadOnlySpan<Candle> candles)
    {
        var (trades, _) = RunSwing(g, candles);
        return trades;
    }

    public record FadeShortTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,   // swingHigh + SL×ATR
        double MaeStop,    // entry + MAE×ATR (tighter ceiling on adverse excursion)
        double Target,
        bool   TrailArmed,
        double TrailLow,   // lowest price seen since entry (trail reference for short)
        int    HoldCount); // h1 bars held

    public static FadeShortTradeState GetFadeShortTradeState(FadeShortGenotype g, ReadOnlySpan<Candle> candles)
    {
        var (_, state) = RunSwing(g, candles);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, FadeShortTradeState FinalState)
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

    // Entry point for the GA fitness loop — uses pre-computed fixed-period indicators and a
    // caller-rented EMA buffer, simulating only over [rangeStart, rangeEnd).
    // This avoids per-individual allocations of rsi/adx/atr arrays and Candle→double LINQ copies.
    internal static List<(DateTime Time, double Return, string Kind)> GetFadeShortReturnsPrecomputed(
        FadeShortGenotype g,
        ReadOnlySpan<Candle>  candles,
        double[]  closes, double[] highs, double[] lows,
        double[]  rsi,    double[] adx,   double[] atr,
        double[]  ema,   // caller fills this via Indicators.EmaInto before each call
        int       rangeStart, int rangeEnd)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                   + g.LookbackCandles;
        int iStart = Math.Max(rangeStart, warmup);
        if (iStart >= rangeEnd || rangeEnd > candles.Length) return [];
        var (trades, _) = SimulateCore(g, candles, closes, highs, lows, rsi, adx, atr, ema, iStart, rangeEnd);
        return trades;
    }

    // Core simulation loop shared by RunSwing and GetFadeShortReturnsPrecomputed.
    // iStart/iEnd are absolute indices into the full arrays; caller ensures iStart ≥ warmup.
    private static (List<(DateTime, double, string)> Trades, FadeShortTradeState FinalState)
        SimulateCore(
            FadeShortGenotype g,
            ReadOnlySpan<Candle>  candles,
            double[]  closes, double[] highs, double[] lows,
            double[]  rsi,    double[] adx,   double[] atr, double[] ema,
            int       iStart, int      iEnd)
    {
        var result = new List<(DateTime, double, string)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
        double maeStop    = 0;
        double target     = 0;
        double trailLow   = 0;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    holdCount  = 0;

        for (int i = iStart; i < iEnd; i++)
        {
            double price  = closes[i];
            double atrNow = atr[i] > 1e-10 ? atr[i] : price * 0.04;

            if (!inTrade)
            {
                // ── Regime check ─────────────────────────────────────────────────
                bool strongTrend = adx[i] >= g.AdxThreshold && price > ema[i];
                if (!strongTrend) continue;

                // ── Find swing high in lookback window ────────────────────────────
                int    lb        = g.LookbackCandles;
                int    lbStart   = Math.Max(0, i - lb);
                double swingHigh = closes[lbStart];
                int    highIdx   = lbStart;
                double recentLow = lows[lbStart];

                for (int j = lbStart; j < i; j++)
                {
                    if (closes[j] > swingHigh) { swingHigh = closes[j]; highIdx = j; }
                    if (lows[j]   < recentLow)   recentLow = lows[j];
                }

                // ── Min rally filter ──────────────────────────────────────────────
                bool bigRally = (swingHigh - recentLow) >= g.MinRallyAtrMult * atrNow;
                if (!bigRally) continue;

                // ── RSI divergence ────────────────────────────────────────────────
                double rsiAtHigh = rsi[highIdx];
                bool diverging   = rsiAtHigh >= g.RsiOverbought
                                && rsi[i] <= rsiAtHigh - g.RsiDivThreshold;
                if (!diverging) continue;

                // ── Structure break: close below previous candle's low ────────────
                bool bos = closes[i] < lows[i - 1];
                if (!bos) continue;

                // ── Enter short ───────────────────────────────────────────────────
                inTrade    = true;
                entry      = price;
                atrEntry   = atrNow;
                // Stop above the swing high: if price exceeds that level the fade thesis is wrong.
                hardStop   = swingHigh + g.StopLossAtrMult * atrEntry;
                // MAE ceiling: caps loss on slow-grind rallies that stay below swingHigh stop.
                maeStop    = entry + g.MaeAtrMult * atrEntry;
                target     = entry - g.TakeProfitAtrMult * atrEntry;
                trailLow   = price;
                trailArmed = false;
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
                    double ret = (entry - exitPx) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry);
                    result.Add((candles[i].Time, ret, "fade_short"));
                    inTrade = false;
                }
            }
        }

        // Mark open position at last close of the simulated range
        if (inTrade)
        {
            double finalPx = closes[iEnd - 1];
            double ret = (entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry);
            result.Add((candles[iEnd - 1].Time, ret, "fade_short"));
        }

        var finalState = new FadeShortTradeState(inTrade, entry, hardStop, maeStop, target,
            trailArmed, trailLow, holdCount);
        return (result, finalState);
    }

    // ── Multi-timeframe: 1h setup + 15m entry + h4 exit sizing ──────────────────
    // h1  = 1h candles  — EMA/RSI/ADX regime gate, swing-high lookback, RSI divergence
    //                     MinRallyAtrMult uses h1 ATR (right scale for h1 rally detection)
    // m15 = 15m candles — BoS entry trigger, trailing stop tick-by-tick, timeout
    // h4  = aggregated from h1 (factor 4) — ATR reference for all EXIT sizing
    //       (stop, TP, trail activation, trail distance) so distances match holding TF
    // LookbackCandles and MaxHoldCandles are h1 bars.

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

    public static List<(DateTime Time, double Return, string Kind)> GetFadeShortReturns(
        FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (trades, _) = RunSwingMultiTF(g, h1, m15);
        return trades;
    }

    public static FadeShortTradeState GetFadeShortTradeState(FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunSwingMultiTF(g, h1, m15);
        return state;
    }

    // Returns scored trades — entry+exit times and signal quality score — for ranked portfolio sim.
    // Score = rsiExcess × adxRatio × rallyRatio (all > 1 at entry → higher = stronger signal).
    public static List<ScoredTrade> GetScoredSwingTrades(string coin, FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var scored = new List<ScoredTrade>();
        RunSwingMultiTF(g, h1, m15, coin, scored);
        return scored;
    }

    private static (List<(DateTime, double, string)> Trades, FadeShortTradeState FinalState)
        RunSwingMultiTF(FadeShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15,
                        string? coin = null, List<ScoredTrade>? scoredOut = null)
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

        var result = new List<(DateTime, double, string)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
        double maeStop    = 0;
        double target     = 0;
        double trailLow   = 0;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    entryIH1   = 0;

        // Cache h1 setup result — only recomputed when h1Ref changes (every 4 m15 bars)
        int    cachedH1Ref     = -1;
        bool   cachedSetupMet  = false;
        double cachedSwingHigh = 0;
        double cachedAtrRef    = 0;
        double cachedScore     = 0;   // signal quality at setup candle (used by ranked sim)

        double   entryScore = 0;
        DateTime entryTime  = default;

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
                // Recompute h1 setup only when h1Ref advances (4 m15 bars per h1 bar)
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    // h1 ATR: rally qualification (right scale for h1 price structure)
                    double atrH1 = h1Atr[h1Ref] > 1e-10 ? h1Atr[h1Ref] : h1Closes[h1Ref] * 0.02;

                    // h4 ATR: exit sizing (matches the multi-day holding timeframe)
                    // Use the previous completed h4 bar — the current h4 bar aggregates future h1 bars
                    int h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : atrH1 * 4;

                    if (h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref])
                    {
                        int    lb        = g.LookbackCandles;
                        int    lbStart   = Math.Max(0, h1Ref - lb);
                        double swingHigh = h1Closes[lbStart];
                        int    highIdx   = lbStart;
                        double recentLow = h1Lows[lbStart];

                        for (int j = lbStart; j < h1Ref; j++)
                        {
                            if (h1Closes[j] > swingHigh) { swingHigh = h1Closes[j]; highIdx = j; }
                            if (h1Lows[j]   < recentLow)   recentLow = h1Lows[j];
                        }

                        double rsiAtHigh = h1Rsi[highIdx];
                        if ((swingHigh - recentLow) >= g.MinRallyAtrMult * atrH1
                            && rsiAtHigh >= g.RsiOverbought
                            && h1Rsi[h1Ref] <= rsiAtHigh - g.RsiDivThreshold)
                        {
                            cachedSetupMet  = true;
                            cachedSwingHigh = swingHigh;
                            cachedAtrRef    = atrH4;   // exits use h4 ATR

                            // Signal quality: product of three independent strengths.
                            // Each factor > 1 at entry (threshold is the floor, not the target).
                            double rsiExcess  = rsiAtHigh - g.RsiOverbought;
                            double adxRatio   = h1Adx[h1Ref] / g.AdxThreshold;
                            double rallyRatio  = (swingHigh - recentLow) / (g.MinRallyAtrMult * atrH1);
                            cachedScore = rsiExcess * adxRatio * rallyRatio;
                        }
                    }
                }

                // BoS on 15m: close below the previous 15m candle's low.
                // Enter at the OPEN of the next 15m bar — the BoS is only confirmed at bar close,
                // so the earliest realistic fill is the following bar's open.
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
                    entryIH1   = nextBar / 4;
                    entryScore = cachedScore;
                    entryTime  = m15[nextBar].Time;
                }
            }
            else
            {
                if (m15Price < trailLow) trailLow = m15Price;
                if (!trailArmed && entry - trailLow >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;   // elapsed h1 bars since entry

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
                    double ret = (entry - exitPx) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry);
                    result.Add((m15[im15].Time, ret, "fade_short"));
                    scoredOut?.Add(new ScoredTrade(coin!, "swing", entryTime, m15[im15].Time, ret, entryScore));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double ret = (entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry);
            result.Add((m15[^1].Time, ret, "fade_short"));
            scoredOut?.Add(new ScoredTrade(coin!, "swing", entryTime, m15[^1].Time, ret, entryScore));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new FadeShortTradeState(inTrade, entry, hardStop, maeStop, target, trailArmed, trailLow, finalHold));
    }

    // ── Cost model ────────────────────────────────────────────────────────────────

    // Total round-trip cost for one trade.
    // isStop=true adds gap-risk premium: price often blows through the stop level in a volatile bar.
    private static double TradeCost(bool isStop, double atrEntry, double entryPx)
    {
        double atrPct  = atrEntry / entryPx * 100.0;
        double slip    = SlipK * atrPct + (isStop ? SlipStopGap * atrPct : 0.0);
        return FeeExchange + slip;
    }

}

// ── SwingLong simulator ──────────────────────────────────────────────────────────────────
// Mirror of FadeShortSimulator: RSI bullish divergence + bullish BoS.
// h1  = setup (EMA/RSI/ADX regime gate, swing-low lookback, RSI divergence)
// m15 = precision entry (bullish BoS: close > prev 15m high)
// h4  = exit sizing ATR (aggregated from h1; matches multi-day holding timeframe)
public static class SwingLongSimulator
{
    internal const int AtrPeriod = 14;
    internal const int RsiPeriod =  7;
    internal const int AdxPeriod =  7;

    private const double FeeExchange = 0.11;
    private const double SlipK       = 0.025;
    private const double SlipStopGap = 0.030;

    public record SwingLongTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailHigh,   // highest price seen since entry (trail reference for long)
        int    HoldCount);

    public static List<(DateTime Time, double Return, string Kind)> GetSwingLongReturns(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (trades, _) = RunSwingLongMultiTF(g, h1, m15);
        return trades;
    }

    public static SwingLongTradeState GetSwingLongTradeState(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunSwingLongMultiTF(g, h1, m15);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, SwingLongTradeState FinalState)
        RunSwingLongMultiTF(SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
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

        var result = new List<(DateTime, double, string)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
        double target     = 0;
        double trailHigh  = 0;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    entryIH1   = 0;

        int    cachedH1Ref    = -1;
        bool   cachedSetupMet = false;
        double cachedSwingLow = 0;
        double cachedAtrRef   = 0;

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

                    // Use the previous completed h4 bar — the current h4 bar aggregates future h1 bars
                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref] : atrH1 * 4;

                    // Trend gate: price above EMA and ADX trending
                    if (h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref])
                    {
                        int    lb         = g.LookbackCandles;
                        int    lbStart    = Math.Max(0, h1Ref - lb);
                        double swingLow   = h1Closes[lbStart];
                        int    lowIdx     = lbStart;
                        double recentHigh = h1Highs[lbStart];

                        for (int j = lbStart; j < h1Ref; j++)
                        {
                            if (h1Closes[j] < swingLow)  { swingLow = h1Closes[j]; lowIdx = j; }
                            if (h1Highs[j]  > recentHigh)  recentHigh = h1Highs[j];
                        }

                        // Min decline filter: real pullback, not noise
                        bool bigDrop = (recentHigh - swingLow) >= g.MinDeclineAtrMult * atrH1;

                        // RSI bullish divergence: swingLow RSI was oversold AND current RSI recovered
                        double rsiAtLow = h1Rsi[lowIdx];
                        bool diverging  = rsiAtLow <= g.RsiOversold
                                       && h1Rsi[h1Ref] >= rsiAtLow + g.RsiDivThreshold;

                        // 1h BoS: close above previous candle's high (bullish)
                        bool h1Bos = h1Closes[h1Ref] > h1Highs[h1Ref - 1];

                        if (bigDrop && diverging && h1Bos)
                        {
                            cachedSetupMet = true;
                            cachedSwingLow = swingLow;
                            cachedAtrRef   = atrH4;
                        }
                    }
                }

                // 15m bullish BoS: close above previous 15m candle's high.
                // Enter at the OPEN of the next 15m bar — the BoS is only confirmed at bar close.
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
                    entryIH1   = nextBar / 4;
                }
            }
            else
            {
                if (m15Price > trailHigh) trailHigh = m15Price;
                if (!trailArmed && trailHigh - entry >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

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
                    double exitPx   = hitStop   ? hardStop :
                                      hitTarget ? target   : m15Price;
                    double atrPct   = atrEntry / entry * 100.0;
                    double slip     = SlipK * atrPct;
                    double stopSlip = hitStop ? SlipStopGap * atrPct : 0;
                    double cost     = FeeExchange + slip * 2 + stopSlip;
                    double ret      = (exitPx - entry) / entry * 100.0 - cost;
                    result.Add((m15[im15].Time, ret, "swing_long"));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double atrPct  = atrEntry / entry * 100.0;
            double ret     = (finalPx - entry) / entry * 100.0 - (FeeExchange + SlipK * atrPct * 2);
            result.Add((m15[^1].Time, ret, "swing_long"));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new SwingLongTradeState(inTrade, entry, hardStop, target, trailArmed, trailHigh, finalHold));
    }
}
