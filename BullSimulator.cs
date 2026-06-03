namespace TradingGA;

// Bull long simulator — symmetric counterpart to SwingSimulator.
//
// Entry: all of the following must align:
//   1. Uptrend regime — ADX(7) ≥ threshold AND close > EMA(period)
//   2. Min pullback filter — recent swing high is ≥ MinPullbackAtrMult × h1ATR above swing low
//                            (real corrective move, not noise)
//   3. RSI recovery — RSI at swing low ≤ RsiOversold AND current RSI ≥ swing-low RSI + RsiDivThreshold
//                     (buyers exhausted at the bottom, now recovering)
//   4. BoS on 15m — close above the previous 15m candle's high (committed to reversal)
//
// Exit: hard stop below swing low · MAE floor · fixed ATR target · trailing stop · max-hold.
//   Stop = swingLow − StopLossAtrMult × h4ATR: price below the swing low invalidates the bull thesis.
//   ATR multiples use h4 ATR at entry — same scale as SwingSimulator.
public static class BullSimulator
{
    private const int AtrPeriod = 14;
    private const int RsiPeriod = 7;
    private const int AdxPeriod = 7;

    // Fee and slippage model — identical to SwingSimulator (same exchange, same execution TF).
    private const double FeeExchange = 0.11;
    private const double SlipK       = 0.025;
    private const double SlipStopGap = 0.015;

    public static List<(DateTime Time, double Return, string Kind)> GetBullReturns(
        BullGenotype g, Candle[] h1, Candle[] m15)
    {
        var (trades, _) = RunBullMultiTF(g, h1, m15);
        return trades;
    }

    public record BullTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double MaeStop,
        double Target,
        bool   TrailArmed,
        double TrailHigh,
        int    HoldCount);

    public static BullTradeState GetBullTradeState(BullGenotype g, Candle[] h1, Candle[] m15)
    {
        var (_, state) = RunBullMultiTF(g, h1, m15);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, BullTradeState FinalState)
        RunBullMultiTF(BullGenotype g, Candle[] h1, Candle[] m15)
    {
        int h1Warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                       + g.LookbackCandles + 2;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new BullTradeState(false, 0, 0, 0, 0, false, 0, 0));

        var h1Closes = h1.Select(c => c.Close).ToArray();
        var h1Highs  = h1.Select(c => c.High).ToArray();
        var h1Lows   = h1.Select(c => c.Low).ToArray();

        var h1Ema = Indicators.Ema(h1Closes, g.EmaPeriod);
        var h1Rsi = Indicators.Rsi(h1Closes, RsiPeriod);
        var h1Adx = Indicators.Adx(h1Highs, h1Lows, h1Closes, AdxPeriod);
        var h1Atr = Indicators.Atr(h1Highs, h1Lows, h1Closes, AtrPeriod);

        var h4      = SwingSimulator.AggregateCandles(h1, 4);
        var h4Highs = h4.Select(c => c.High).ToArray();
        var h4Lows  = h4.Select(c => c.Low).ToArray();
        var h4Cls   = h4.Select(c => c.Close).ToArray();
        var h4Atr   = Indicators.Atr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        var m15Closes = m15.Select(c => c.Close).ToArray();
        var m15Highs  = m15.Select(c => c.High).ToArray();

        var result = new List<(DateTime, double, string)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
        double maeStop    = 0;
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

                    int h4Ref = h1Ref / 4;
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : atrH1 * 4;

                    // Regime: uptrend confirmed
                    if (h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref])
                    {
                        int    lb       = g.LookbackCandles;
                        int    lbStart  = Math.Max(0, h1Ref - lb);
                        double swingLow = h1Closes[lbStart];
                        int    lowIdx   = lbStart;
                        double recentHigh = h1Highs[lbStart];

                        for (int j = lbStart; j < h1Ref; j++)
                        {
                            if (h1Closes[j] < swingLow) { swingLow = h1Closes[j]; lowIdx = j; }
                            if (h1Highs[j]  > recentHigh) recentHigh = h1Highs[j];
                        }

                        // Min pullback: the drop from recent high to swing low must be meaningful
                        bool bigPullback = (recentHigh - swingLow) >= g.MinPullbackAtrMult * atrH1;
                        if (!bigPullback) continue;

                        // RSI recovery at swing low
                        double rsiAtLow = h1Rsi[lowIdx];
                        bool recovering = rsiAtLow <= g.RsiOversold
                                       && h1Rsi[h1Ref] >= rsiAtLow + g.RsiDivThreshold;
                        if (!recovering) continue;

                        cachedSetupMet  = true;
                        cachedSwingLow  = swingLow;
                        cachedAtrRef    = atrH4;
                    }
                }

                // 15m BoS: close above previous 15m candle's high (bullish structure break)
                if (cachedSetupMet && m15Closes[im15] > m15Highs[im15 - 1])
                {
                    inTrade    = true;
                    entry      = m15Price;
                    atrEntry   = cachedAtrRef;
                    // Stop below the swing low: price breaking below it invalidates the thesis.
                    hardStop   = cachedSwingLow - g.StopLossAtrMult * atrEntry;
                    // MAE floor: caps slow-grind down that stays above the hard stop.
                    maeStop    = entry - g.MaeAtrMult * atrEntry;
                    target     = entry + g.TakeProfitAtrMult * atrEntry;
                    trailHigh  = m15Price;
                    trailArmed = false;
                    entryIH1   = ih1;
                }
            }
            else
            {
                if (m15Price > trailHigh) trailHigh = m15Price;
                if (!trailArmed && trailHigh - entry >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

                bool hitHardStop = m15Price <= hardStop;
                bool hitMae      = m15Price <= maeStop;
                bool hitStop     = hitHardStop || hitMae;
                bool hitTarget   = m15Price >= target;
                bool hitTrail    = trailArmed && m15Price < trailHigh - g.TrailingStopAtrMult * atrEntry;
                bool timedOut    = holdH1 >= g.MaxHoldCandles;

                if (hitStop || hitTarget || hitTrail || timedOut)
                {
                    double exitPx = hitHardStop ? hardStop :
                                    hitMae      ? maeStop  :
                                    hitTarget   ? target   : m15Price;
                    double ret = (exitPx - entry) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry);
                    result.Add((m15[im15].Time, ret, "bull_long"));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double ret = (finalPx - entry) / entry * 100.0 - TradeCost(false, atrEntry, entry);
            result.Add((m15[^1].Time, ret, "bull_long"));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new BullTradeState(inTrade, entry, hardStop, maeStop, target, trailArmed, trailHigh, finalHold));
    }

    private static double TradeCost(bool isStop, double atrEntry, double entryPx)
    {
        double atrPct = atrEntry / entryPx * 100.0;
        double slip   = SlipK * atrPct + (isStop ? SlipStopGap * atrPct : 0.0);
        return FeeExchange + slip;
    }
}
