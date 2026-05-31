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
    public  const double FeeRoundTrip = 0.21;   // same as pump-short (0.055% taker ×2 + 0.05% slip ×2)

    public static List<(DateTime Time, double Return, string Kind)> GetSwingReturns(
        SwingGenotype g, Candle[] candles)
    {
        var (trades, _) = RunSwing(g, candles);
        return trades;
    }

    public record SwingTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailLow,    // lowest price seen since entry (trail reference for short)
        int    HoldCount);  // 4h bars held

    public static SwingTradeState GetSwingTradeState(SwingGenotype g, Candle[] candles)
    {
        var (_, state) = RunSwing(g, candles);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, SwingTradeState FinalState)
        RunSwing(SwingGenotype g, Candle[] candles)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, g.RsiPeriod), g.AdxPeriod * 2 + 1)
                   + g.LookbackCandles;
        if (candles.Length <= warmup + 10)
            return ([], new SwingTradeState(false, 0, 0, 0, false, 0, 0));

        var closes  = candles.Select(c => c.Close).ToArray();
        var highs   = candles.Select(c => c.High).ToArray();
        var lows    = candles.Select(c => c.Low).ToArray();

        var ema = ComputeEma(closes, g.EmaPeriod);
        var rsi = ComputeRsi(closes, g.RsiPeriod);
        var adx = ComputeAdx(highs, lows, closes, g.AdxPeriod);
        var atr = ComputeAtr(highs, lows, closes, AtrPeriod);

        var result = new List<(DateTime, double, string)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
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

                bool hitStop   = price >= hardStop;
                bool hitTarget = price <= target;
                bool hitTrail  = trailArmed && price > trailLow + g.TrailingStopAtrMult * atrEntry;
                bool timedOut  = holdCount >= g.MaxHoldCandles;

                if (hitStop || hitTarget || hitTrail || timedOut)
                {
                    double exitPx = hitStop   ? hardStop :
                                    hitTarget ? target   : price;
                    double ret = (entry - exitPx) / entry * 100.0 - FeeRoundTrip;
                    result.Add((candles[i].Time, ret, "swing_short"));
                    inTrade = false;
                }
            }
        }

        // Mark open position at last close
        if (inTrade)
        {
            double finalPx = closes[^1];
            double ret = (entry - finalPx) / entry * 100.0 - FeeRoundTrip;
            result.Add((candles[^1].Time, ret, "swing_short"));
        }

        var finalState = new SwingTradeState(inTrade, entry, hardStop, target,
            trailArmed, trailLow, holdCount);
        return (result, finalState);
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
