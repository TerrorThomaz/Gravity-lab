namespace TradingGA;

// Swing trading simulator — operates on daily candles, targets multi-day to multi-week moves.
//
// Two strategies, regime-gated by ADX + EMA:
//   LONG  (moderate uptrend): enter on RSI bounce from oversold; ride with trailing stop + target.
//   SHORT (strong uptrend extended): enter on RSI overbought fade + daily Break of Structure.
//
// Exits: hard ATR stop · fixed ATR target · trailing ATR stop once activated · max-hold timeout.
// ATR multiples use the 14-period ATR at the entry candle (fixed for the life of each trade).
public static class SwingSimulator
{
    private const int AtrPeriod = 14;

    public static List<(DateTime Time, double Return, string Kind)> GetSwingReturns(
        SwingGenotype g, Candle[] candles)
    {
        var (trades, _) = RunSwing(g, candles);
        return trades;
    }

    public record SwingTradeState(
        bool   InTrade,
        string Side,        // "long" | "short" | ""
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailRef,    // tradeHigh for longs, tradeLow for shorts
        int    HoldCount);

    public static SwingTradeState GetSwingTradeState(SwingGenotype g, Candle[] candles)
    {
        var (_, state) = RunSwing(g, candles);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, SwingTradeState FinalState)
        RunSwing(SwingGenotype g, Candle[] candles)
    {
        int warmup = Math.Max(Math.Max(g.EmaPeriod, g.RsiPeriod), g.AdxPeriod * 2 + 1);
        if (candles.Length <= warmup + 10)
            return ([], new SwingTradeState(false, "", 0, 0, 0, false, 0, 0));

        var closes  = candles.Select(c => c.Close).ToArray();
        var highs   = candles.Select(c => c.High).ToArray();
        var lows    = candles.Select(c => c.Low).ToArray();

        var ema = ComputeEma(closes, g.EmaPeriod);
        var rsi = ComputeRsi(closes, g.RsiPeriod);
        var adx = ComputeAdx(highs, lows, closes, g.AdxPeriod);
        var atr = ComputeAtr(highs, lows, closes, AtrPeriod);

        var result = new List<(DateTime, double, string)>();

        // ── Trade state ───────────────────────────────────────────────────────
        bool   inTrade   = false;
        string side      = "";
        double entry     = 0;
        double hardStop  = 0;
        double target    = 0;
        double trailRef  = 0;   // tradeHigh (long) or tradeLow (short)
        double atrAtEntry = 0;
        bool   trailArmed = false;
        int    holdCount  = 0;

        // ── Entry-signal accumulators ─────────────────────────────────────────
        bool   rsiWasOversold   = false;
        bool   rsiWasOverbought = false;
        bool   bosDetected      = false;
        int    bosCandlesWaited = 0;
        double lastHigh         = 0;

        for (int i = warmup; i < candles.Length; i++)
        {
            double price  = closes[i];
            double atrNow = atr[i] > 1e-10 ? atr[i] : price * 0.04;

            if (!inTrade)
            {
                double rsiNow = rsi[i];
                double emaNow = ema[i];
                double adxNow = adx[i];

                // Uptrend: ADX strong + price above EMA
                bool strongTrend   = adxNow >= g.AdxThreshold && price > emaNow;
                // Moderate uptrend: ADX above 60% of threshold + price above EMA
                bool moderateTrend = adxNow >= g.AdxThreshold * 0.6 && price > emaNow;

                // Track RSI extremes for entry signals
                if (rsiNow < g.RsiOversold)   rsiWasOversold   = true;
                if (rsiNow > g.RsiOverbought)  rsiWasOverbought = true;

                // Track daily Break of Structure for short entries
                if (candles[i].High < lastHigh * g.BosThreshold && !bosDetected)
                    bosDetected = true;
                if (bosDetected) bosCandlesWaited++;

                // ── LONG entry: moderate uptrend + RSI bounce from oversold ──
                if (moderateTrend && rsiWasOversold && rsiNow > g.RsiOversold)
                {
                    inTrade       = true;
                    side          = "long";
                    entry         = price;
                    atrAtEntry    = atrNow;
                    hardStop      = entry - g.StopLossAtrMult * atrAtEntry;
                    target        = entry + g.TakeProfitAtrMult * atrAtEntry;
                    trailRef      = price;
                    trailArmed    = false;
                    holdCount     = 0;
                    rsiWasOversold   = false;
                    bosDetected      = false;
                    bosCandlesWaited = 0;
                    rsiWasOverbought = false;
                }
                // ── SHORT entry: strong uptrend extended, RSI overbought fade + BoS ──
                else if (strongTrend
                         && rsiWasOverbought && rsiNow < g.RsiOverbought
                         && bosDetected && bosCandlesWaited >= g.BosCandlesWait)
                {
                    inTrade       = true;
                    side          = "short";
                    entry         = price;
                    atrAtEntry    = atrNow;
                    hardStop      = entry + g.StopLossAtrMult * atrAtEntry;
                    target        = entry - g.TakeProfitAtrMult * atrAtEntry;
                    trailRef      = price;
                    trailArmed    = false;
                    holdCount     = 0;
                    rsiWasOverbought = false;
                    bosDetected      = false;
                    bosCandlesWaited = 0;
                    rsiWasOversold   = false;
                }
            }
            else
            {
                holdCount++;

                if (side == "long")
                {
                    if (price > trailRef) trailRef = price;

                    if (!trailArmed && trailRef - entry >= g.TrailingActivationAtrMult * atrAtEntry)
                        trailArmed = true;

                    bool hitStop   = price <= hardStop;
                    bool hitTarget = price >= target;
                    bool hitTrail  = trailArmed && price < trailRef - g.TrailingStopAtrMult * atrAtEntry;
                    bool timedOut  = holdCount >= g.MaxHoldCandles;

                    if (hitStop || hitTarget || hitTrail || timedOut)
                    {
                        double exitPx = hitStop ? hardStop : (hitTarget ? target : price);
                        result.Add((candles[i].Time, (exitPx - entry) / entry * 100.0, "swing_long"));
                        inTrade = false; side = "";
                    }
                }
                else  // short
                {
                    if (price < trailRef) trailRef = price;

                    if (!trailArmed && entry - trailRef >= g.TrailingActivationAtrMult * atrAtEntry)
                        trailArmed = true;

                    bool hitStop   = price >= hardStop;
                    bool hitTarget = price <= target;
                    bool hitTrail  = trailArmed && price > trailRef + g.TrailingStopAtrMult * atrAtEntry;
                    bool timedOut  = holdCount >= g.MaxHoldCandles;

                    if (hitStop || hitTarget || hitTrail || timedOut)
                    {
                        double exitPx = hitStop ? hardStop : (hitTarget ? target : price);
                        result.Add((candles[i].Time, (entry - exitPx) / entry * 100.0, "swing_short"));
                        inTrade = false; side = "";
                    }
                }
            }

            if (candles[i].High > lastHigh) lastHigh = candles[i].High;
        }

        // Mark open position to last close
        if (inTrade)
        {
            double finalPx = closes[^1];
            double ret = side == "long"
                ? (finalPx - entry) / entry * 100.0
                : (entry - finalPx) / entry * 100.0;
            result.Add((candles[^1].Time, ret, side == "long" ? "swing_long" : "swing_short"));
        }

        var finalState = new SwingTradeState(inTrade, side, entry, hardStop, target,
            trailArmed, trailRef, holdCount);
        return (result, finalState);
    }

    // ── Indicators ────────────────────────────────────────────────────────────

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
        avgGain /= period;
        avgLoss /= period;
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
        var tr   = new double[n];
        var pDm  = new double[n];
        var mDm  = new double[n];

        for (int i = 1; i < n; i++)
        {
            double hDiff = highs[i] - highs[i - 1];
            double lDiff = lows[i - 1] - lows[i];
            tr[i]  = Math.Max(highs[i] - lows[i],
                     Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                              Math.Abs(lows[i]  - closes[i - 1])));
            pDm[i] = hDiff > lDiff && hDiff > 0 ? hDiff : 0;
            mDm[i] = lDiff > hDiff && lDiff > 0 ? lDiff : 0;
        }

        var sTr  = new double[n];
        var sPDm = new double[n];
        var sMDm = new double[n];
        var dx   = new double[n];
        var adx  = new double[n];

        if (period >= n) return adx;

        for (int i = 1; i <= period; i++) { sTr[period] += tr[i]; sPDm[period] += pDm[i]; sMDm[period] += mDm[i]; }

        for (int i = period + 1; i < n; i++)
        {
            sTr[i]  = sTr[i-1]  - sTr[i-1]  / period + tr[i];
            sPDm[i] = sPDm[i-1] - sPDm[i-1] / period + pDm[i];
            sMDm[i] = sMDm[i-1] - sMDm[i-1] / period + mDm[i];
            if (sTr[i] < 1e-10) continue;
            double pDi   = 100.0 * sPDm[i] / sTr[i];
            double mDi   = 100.0 * sMDm[i] / sTr[i];
            double diSum = pDi + mDi;
            dx[i] = diSum > 1e-10 ? 100.0 * Math.Abs(pDi - mDi) / diSum : 0;
        }

        int adxStart = period * 2;
        if (adxStart >= n) return adx;
        double sumDx = 0;
        for (int i = period + 1; i <= adxStart && i < n; i++) sumDx += dx[i];
        adx[adxStart] = sumDx / period;
        for (int i = adxStart + 1; i < n; i++)
            adx[i] = (adx[i - 1] * (period - 1) + dx[i]) / period;

        return adx;
    }
}
