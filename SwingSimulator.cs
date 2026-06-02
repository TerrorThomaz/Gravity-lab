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
public static class SwingSimulator
{
    private const int AtrPeriod    = 14;
    private const int RsiPeriod    = 7;   // fixed — not a gene; GA always converges here
    private const int AdxPeriod    = 7;   // fixed — not a gene; GA always converges here (faster ADX, more reactive to trend onset)
    // Fee model: exchange taker fee + ATR-proportional slippage.
    // Execution is on 15m bars; ATR reference is h4. Calibrated so the average round-trip
    // stays ~0.21% for liquid coins (h4 ATR ≈ 2-3% of price), while volatile coins
    // (memes, h4 ATR ≈ 5-8%) pay proportionally more, especially on stop exits.
    //   SlipK       = 0.025 → slip per side = 0.025 × atrPct  (at 3% ATR → 0.075% each side)
    //   SlipStopGap = 0.015 → extra gap on stops               (at 3% ATR → +0.045% extra)
    // Liquid coin TP ≈ 0.185%, Stop ≈ 0.23%. Meme TP ≈ 0.26%, Stop ≈ 0.35%.
    private const double FeeExchange = 0.11;   // 0.055% taker × 2 sides
    private const double SlipK       = 0.025;  // entry + normal-exit slip = k × (atr/price × 100)
    private const double SlipStopGap = 0.015;  // stop gap premium = k × (atr/price × 100)

    public static List<(DateTime Time, double Return, string Kind)> GetSwingReturns(
        SwingGenotype g, Candle[] candles)
    {
        var (trades, _) = RunSwing(g, candles);
        return trades;
    }

    public record SwingTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,   // swingHigh + SL×ATR
        double MaeStop,    // entry + MAE×ATR (tighter ceiling on adverse excursion)
        double Target,
        bool   TrailArmed,
        double TrailLow,   // lowest price seen since entry (trail reference for short)
        int    HoldCount); // h1 bars held

    public static SwingTradeState GetSwingTradeState(SwingGenotype g, Candle[] candles)
    {
        var (_, state) = RunSwing(g, candles);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, SwingTradeState FinalState)
        RunSwing(SwingGenotype g, Candle[] candles)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                   + g.LookbackCandles;
        if (candles.Length <= warmup + 10)
            return ([], new SwingTradeState(false, 0, 0, 0, 0, false, 0, 0));

        var closes  = candles.Select(c => c.Close).ToArray();
        var highs   = candles.Select(c => c.High).ToArray();
        var lows    = candles.Select(c => c.Low).ToArray();

        var ema = ComputeEma(closes, g.EmaPeriod);
        var rsi = ComputeRsi(closes, RsiPeriod);
        var adx = ComputeAdx(highs, lows, closes, AdxPeriod);
        var atr = ComputeAtr(highs, lows, closes, AtrPeriod);

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

        for (int i = warmup; i < candles.Length; i++)
        {
            double price  = closes[i];
            double atrNow = atr[i] > 1e-10 ? atr[i] : price * 0.04;

            if (!inTrade)
            {
                // ── Regime check ─────────────────────────────────────────────────
                bool strongTrend = adx[i] >= g.AdxThreshold && price > ema[i];
                if (!strongTrend) continue;

                // ── Find swing high in lookback window ────────────────────────────
                int    lb       = g.LookbackCandles;
                int    start    = Math.Max(0, i - lb);
                double swingHigh = closes[start];
                int    highIdx   = start;
                double recentLow = lows[start];

                for (int j = start; j < i; j++)
                {
                    if (closes[j] > swingHigh) { swingHigh = closes[j]; highIdx = j; }
                    if (lows[j]   < recentLow)    recentLow = lows[j];
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
                    result.Add((candles[i].Time, ret, "swing_short"));
                    inTrade = false;
                }
            }
        }

        // Mark open position at last close
        if (inTrade)
        {
            double finalPx = closes[^1];
            double ret = (entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry);
            result.Add((candles[^1].Time, ret, "swing_short"));
        }

        var finalState = new SwingTradeState(inTrade, entry, hardStop, maeStop, target,
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

    public static List<(DateTime Time, double Return, string Kind)> GetSwingReturns(
        SwingGenotype g, Candle[] h1, Candle[] m15)
    {
        var (trades, _) = RunSwingMultiTF(g, h1, m15);
        return trades;
    }

    public static SwingTradeState GetSwingTradeState(SwingGenotype g, Candle[] h1, Candle[] m15)
    {
        var (_, state) = RunSwingMultiTF(g, h1, m15);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, SwingTradeState FinalState)
        RunSwingMultiTF(SwingGenotype g, Candle[] h1, Candle[] m15)
    {
        int h1Warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                       + g.LookbackCandles + 2;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new SwingTradeState(false, 0, 0, 0, 0, false, 0, 0));

        var h1Closes = h1.Select(c => c.Close).ToArray();
        var h1Highs  = h1.Select(c => c.High).ToArray();
        var h1Lows   = h1.Select(c => c.Low).ToArray();

        var h1Ema = ComputeEma(h1Closes, g.EmaPeriod);
        var h1Rsi = ComputeRsi(h1Closes, RsiPeriod);
        var h1Adx = ComputeAdx(h1Highs, h1Lows, h1Closes, AdxPeriod);
        var h1Atr = ComputeAtr(h1Highs, h1Lows, h1Closes, AtrPeriod);

        // h4 ATR — exit sizing scaled to holding timeframe, not entry precision
        var h4      = AggregateCandles(h1, 4);
        var h4Highs = h4.Select(c => c.High).ToArray();
        var h4Lows  = h4.Select(c => c.Low).ToArray();
        var h4Cls   = h4.Select(c => c.Close).ToArray();
        var h4Atr   = ComputeAtr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        var m15Closes = m15.Select(c => c.Close).ToArray();
        var m15Lows   = m15.Select(c => c.Low).ToArray();

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
                    int h4Ref = h1Ref / 4;
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
                        }
                    }
                }

                // BoS on 15m: close below the previous 15m candle's low
                if (cachedSetupMet && m15Closes[im15] < m15Lows[im15 - 1])
                {
                    inTrade    = true;
                    entry      = m15Price;
                    atrEntry   = cachedAtrRef;
                    hardStop   = cachedSwingHigh + g.StopLossAtrMult * atrEntry;
                    maeStop    = entry + g.MaeAtrMult * atrEntry;
                    target     = entry - g.TakeProfitAtrMult * atrEntry;
                    trailLow   = m15Price;
                    trailArmed = false;
                    entryIH1   = ih1;
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
                    result.Add((m15[im15].Time, ret, "swing_short"));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double ret = (entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry);
            result.Add((m15[^1].Time, ret, "swing_short"));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new SwingTradeState(inTrade, entry, hardStop, maeStop, target, trailArmed, trailLow, finalHold));
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

    // ── Indicators ────────────────────────────────────────────────────────────────

    private static double[] ComputeAtr(double[] highs, double[] lows, double[] closes, int period)
    {
        var tr  = new double[closes.Length];
        var atr = new double[closes.Length];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < closes.Length; i++)
            tr[i] = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i]  - closes[i - 1])));
        int init = Math.Min(period, closes.Length);
        double sum = 0;
        for (int i = 0; i < init; i++) sum += tr[i];
        atr[init - 1] = sum / init;
        for (int i = init; i < closes.Length; i++)
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
        return atr;
    }

    private static double[] ComputeRsi(double[] closes, int period)
    {
        var rsi = new double[closes.Length];
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            double diff = closes[i] - closes[i - 1];
            if (diff > 0) avgGain += diff; else avgLoss -= diff;
        }
        avgGain /= period; avgLoss /= period;
        for (int i = period; i < closes.Length; i++)
        {
            if (i > period)
            {
                double diff = closes[i] - closes[i - 1];
                avgGain = (avgGain * (period - 1) + Math.Max(diff,  0)) / period;
                avgLoss = (avgLoss * (period - 1) + Math.Max(-diff, 0)) / period;
            }
            rsi[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        }
        return rsi;
    }

    private static double[] ComputeEma(double[] closes, int period)
    {
        var ema = new double[closes.Length];
        double k = 2.0 / (period + 1);
        ema[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            ema[i] = closes[i] * k + ema[i - 1] * (1 - k);
        return ema;
    }

    private static double[] ComputeAdx(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var tr = new double[n]; var pDm = new double[n]; var mDm = new double[n];
        for (int i = 1; i < n; i++)
        {
            double hd = highs[i] - highs[i-1], ld = lows[i-1] - lows[i];
            tr[i]  = Math.Max(highs[i]-lows[i], Math.Max(Math.Abs(highs[i]-closes[i-1]), Math.Abs(lows[i]-closes[i-1])));
            pDm[i] = hd > ld && hd > 0 ? hd : 0;
            mDm[i] = ld > hd && ld > 0 ? ld : 0;
        }
        var sTr = new double[n]; var sPDm = new double[n]; var sMDm = new double[n];
        var dx  = new double[n]; var adx  = new double[n];
        if (period >= n) return adx;
        for (int i = 1; i <= period; i++) { sTr[period] += tr[i]; sPDm[period] += pDm[i]; sMDm[period] += mDm[i]; }
        for (int i = period + 1; i < n; i++)
        {
            sTr[i]  = sTr[i-1]  - sTr[i-1]  / period + tr[i];
            sPDm[i] = sPDm[i-1] - sPDm[i-1] / period + pDm[i];
            sMDm[i] = sMDm[i-1] - sMDm[i-1] / period + mDm[i];
            if (sTr[i] < 1e-10) continue;
            double pDi = 100.0*sPDm[i]/sTr[i], mDi = 100.0*sMDm[i]/sTr[i];
            double ds  = pDi + mDi;
            dx[i] = ds > 1e-10 ? 100.0 * Math.Abs(pDi - mDi) / ds : 0;
        }
        int adxStart = period * 2;
        if (adxStart >= n) return adx;
        double sumDx = 0;
        for (int i = period + 1; i <= adxStart && i < n; i++) sumDx += dx[i];
        adx[adxStart] = sumDx / period;
        for (int i = adxStart + 1; i < n; i++)
            adx[i] = (adx[i-1] * (period - 1) + dx[i]) / period;
        return adx;
    }
}
