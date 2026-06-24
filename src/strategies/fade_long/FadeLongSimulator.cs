namespace TradingGA;

// Fade-long simulator — symmetric counterpart to FadeShortSimulator.
//
// Fades oversold drops in downtrends (the FadeShort fades overbought rallies in uptrends).
// Catches violent oversold bounces that are common during bear markets.
//
// Entry: all of the following must align:
//   1. Strong downtrend    — ADX(7) ≥ threshold AND close < EMA
//   2. Min drop filter     — recent low is ≥ MinDropAtrMult × h1ATR below the recent high
//                            (confirms a real extended drop to fade, not noise)
//   3. RSI divergence      — RSI at the swing low was ≤ RsiOversold AND current RSI has
//                            recovered by at least RsiDivThreshold pts (sellers losing steam)
//   4. Bullish BoS         — 15m close above the previous 15m candle's high (committed reversal)
//
// Exit: swing-low stop · MAE ceiling · fixed ATR target · trailing stop · max-hold timeout.
//   Stop = swingLow − StopLossAtrMult × h4ATR: price breaking the swing low invalidates thesis.
//   MAE  = entry − MaeAtrMult × h4ATR: caps slow-grind down that stays above the hard stop.
//   ATR multiples use h4 ATR at entry — same scale as FadeShortSimulator.
//
// RegimeBarsActive returned with each trade: consecutive h1 bars where the STRUCTURAL bear
// regime was confirmed at trade entry (EMA conditions only, not ADX — ADX is entry-level,
// the EMA structure is the regime). Used by FadeLongGA for regime-conditional FoldScore filtering.
public static class FadeLongSimulator
{
    private const int AtrPeriod = 14;
    private const int RsiPeriod = 7;
    private const int AdxPeriod = 7;

    private const double FeeExchange = 0.11;
    private const double SlipK       = 0.025;
    private const double SlipStopGap = 0.015;

    public static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive)> GetFadeLongReturns(
        FadeLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (trades, _) = RunFadeLongMultiTF(g, h1, m15);
        return trades;
    }

    public record FadeLongTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double MaeStop,
        double Target,
        bool   TrailArmed,
        double TrailHigh,
        int    HoldCount);

    public static FadeLongTradeState GetFadeLongTradeState(FadeLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunFadeLongMultiTF(g, h1, m15);
        return state;
    }

    private static (List<(DateTime, double, string, int)> Trades, FadeLongTradeState FinalState)
        RunFadeLongMultiTF(FadeLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        int h1Warmup = Math.Max(Math.Max(g.RegimePeriod, Math.Max(g.EmaPeriod, RsiPeriod)), AdxPeriod * 2 + 1)
                       + g.LookbackCandles + 2;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new FadeLongTradeState(false, 0, 0, 0, 0, false, 0, 0));

        var h1Closes = CandleExt.Closes(h1);
        var h1Highs  = CandleExt.Highs(h1);
        var h1Lows   = CandleExt.Lows(h1);

        var h1RegimeEma = Trend.Ema(h1Closes, g.RegimePeriod);
        var h1Ema       = Trend.Ema(h1Closes, g.EmaPeriod);
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

        // Precompute consecutive structural bear-regime bar count at each h1 bar.
        // Uses EMA conditions only (not ADX) — the EMA structure is the persistent regime;
        // ADX is a trade-entry gate, not a regime definition.
        // A bar qualifies when: close < EMA AND close < RegimeEma AND RegimeEma declining.
        int[] bearRegimeBarsAtBar = new int[h1.Length];
        int bearRunning = 0;
        int slopeLen = g.RegimePeriod / 4;
        for (int i = h1Warmup; i < h1.Length; i++)
        {
            bool bearEmaBar = h1Closes[i] < h1Ema[i]
                           && h1Closes[i] < h1RegimeEma[i]
                           && h1RegimeEma[i] < h1RegimeEma[i - slopeLen];
            bearRunning = bearEmaBar ? bearRunning + 1 : 0;
            bearRegimeBarsAtBar[i] = bearRunning;
        }

        var result = new List<(DateTime, double, string, int)>();

        bool   inTrade         = false;
        double entry           = 0;
        double hardStop        = 0;
        double maeStop         = 0;
        double target          = 0;
        double trailHigh       = 0;
        double atrEntry        = 0;
        bool   trailArmed      = false;
        int    entryIH1        = 0;
        int    entryRegimeBars = 0;

        int    cachedH1Ref    = -1;
        bool   cachedSetupMet = false;
        double cachedSwingLow = 0;
        double cachedAtrRef   = 0;
        int    cachedRegimeBars = 0;

        var bullDiv    = Signals.BullishDivergence(h1Rsi, h1Closes, g.LookbackCandles, g.RsiOversold, g.RsiDivThreshold);
        var bigDropArr = Signals.MinMoveFilter(h1Closes, h1Closes, h1Atr, g.LookbackCandles, g.MinDropAtrMult);

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
                    int h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : atrH1 * 4;

                    // Regime: sustained downtrend — below fast EMA, below declining slow EMA
                    bool regimeOk = h1Adx[h1Ref] >= g.AdxThreshold
                                 && h1Closes[h1Ref] < h1Ema[h1Ref]
                                 && h1Closes[h1Ref] < h1RegimeEma[h1Ref]
                                 && h1RegimeEma[h1Ref] < h1RegimeEma[h1Ref - slopeLen];
                    if (regimeOk)
                    {
                        var (swingHigh, _, _) = Signals.SwingHighLookback(
                            h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);
                        var (swingLow, lowIdx, _) = Signals.SwingLowLookback(
                            h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);

                        // Min drop: the fall from recent high to swing low must be meaningful
                        bool bigDrop = bigDropArr[h1Ref];
                        if (!bigDrop) continue;

                        // RSI divergence: RSI was oversold at the swing low AND has now recovered
                        double rsiAtLow = h1Rsi[lowIdx];
                        bool diverging  = bullDiv[h1Ref];
                        if (!diverging) continue;

                        cachedSetupMet  = true;
                        cachedSwingLow  = swingLow;
                        cachedAtrRef    = atrH4;
                        cachedRegimeBars = bearRegimeBarsAtBar[h1Ref];
                    }
                }

                // 15m bullish BoS: close above previous 15m high.
                // Enter at the OPEN of the next 15m bar — the BoS is only confirmed at bar close.
                if (cachedSetupMet && m15Closes[im15] > m15Highs[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    inTrade        = true;
                    entry          = m15[nextBar].Open;
                    atrEntry       = cachedAtrRef;
                    // Stop below swing low: price breaking below it invalidates the thesis.
                    hardStop       = cachedSwingLow - g.StopLossAtrMult * atrEntry;
                    // MAE floor: caps slow-grind down that stays above the hard stop.
                    maeStop        = entry - g.MaeAtrMult * atrEntry;
                    target         = entry + g.TakeProfitAtrMult * atrEntry;
                    trailHigh      = entry;
                    trailArmed     = false;
                    entryIH1       = nextBar / 4;
                    entryRegimeBars = cachedRegimeBars;
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
                    result.Add((m15[im15].Time, ret, "fade_long", entryRegimeBars));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double ret = (finalPx - entry) / entry * 100.0 - TradeCost(false, atrEntry, entry);
            result.Add((m15[^1].Time, ret, "fade_long", entryRegimeBars));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new FadeLongTradeState(inTrade, entry, hardStop, maeStop, target, trailArmed, trailHigh, finalHold));
    }

    private static double TradeCost(bool isStop, double atrEntry, double entryPx)
    {
        double atrPct = atrEntry / entryPx * 100.0;
        double slip   = SlipK * atrPct + (isStop ? SlipStopGap * atrPct : 0.0);
        return FeeExchange + slip;
    }
}
