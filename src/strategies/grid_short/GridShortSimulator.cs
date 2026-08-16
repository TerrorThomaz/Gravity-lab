namespace TradingGA;

// GridShort simulator: SHORT grid in ranging markets. Mirror of GridSimulator.
// Direction: SHORT (profit when price falls). Funding: isLong=false.
public static class GridShortSimulator
{
    private const int    AtrPeriod    = 14;
    private const int    AdxPeriod    = 14;
    private const int    MaxLevels    = 5;

    private const double StopGapAtrK = 0.18;  // same as GridSimulator (direction mirrors)

    internal static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false,
                                     double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrAtStart, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);

    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetGridShortReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null)
    {
        var (trades, _) = RunGridShort(g, h1, sessionLevel: false, funding: funding);
        return trades;
    }

    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetGridShortSessionReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null)
    {
        var (trades, _) = RunGridShort(g, h1, sessionLevel: true, funding: funding);
        return trades;
    }

    public record GridShortTradeState(
        bool   Active,
        int    FilledLevels,
        double Anchor,
        double HardStop,
        int    HoldCount);

    public static GridShortTradeState GetGridShortTradeState(GridGenotype g, ReadOnlySpan<Candle> h1)
    {
        var (_, state) = RunGridShort(g, h1, sessionLevel: false);
        return state;
    }

    private static (List<(DateTime, double, string, DateTime, double)> Trades, GridShortTradeState FinalState)
        RunGridShort(GridGenotype g, ReadOnlySpan<Candle> candles, bool sessionLevel,
                     FundingRateSession? funding = null)
    {
        int warmup = Math.Max(Math.Max(Math.Max(g.EmaPeriod, AtrPeriod), AdxPeriod * 2 + 1), g.BbPeriod) + 2;
        if (candles.Length <= warmup + 5)
            return ([], new GridShortTradeState(false, 0, 0, 0, 0));

        var closes = CandleExt.Closes(candles);
        var highs  = CandleExt.Highs(candles);
        var lows   = CandleExt.Lows(candles);
        var times  = CandleExt.Times(candles);

        var ema     = Trend.Ema(closes, g.EmaPeriod);
        var adx     = Trend.Adx(highs, lows, closes, AdxPeriod);
        var atr     = Volatility.Atr(highs, lows, closes, AtrPeriod);
        var bbWidth = Volatility.BbWidth(closes, g.BbPeriod);

        var result       = new List<(DateTime, double, string, DateTime, double)>();
        var sessionFills = new List<double>();

        bool     gridActive   = false;
        double   anchor       = 0;
        double   atrAtStart   = 0;
        double   hardStop     = 0;
        int      holdCount    = 0;
        DateTime gridStartTime = default;
        int      levels       = Math.Clamp(g.GridLevels, 1, MaxLevels);
        var      filled       = new bool[MaxLevels];
        var      entryPrice   = new double[MaxLevels];

        void AddReturn(int i, double ret, string kind, double entryPx)
        {
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((times[i], ret, kind, gridStartTime, entryPx));
        }

        double sessionEntrySum = 0; int sessionEntryN = 0;

        static double MeanFilled(double[] px, bool[] fl)
        {
            double sum = 0; int n = 0;
            for (int k = 0; k < px.Length; k++) if (fl[k] && px[k] > 0) { sum += px[k]; n++; }
            return n > 0 ? sum / n : 0.0;
        }

        void FlushSession(int i)
        {
            if (!sessionLevel || sessionFills.Count == 0) return;
            result.Add((times[i], sessionFills.Average(), "grid_short_session", gridStartTime,
                        sessionEntryN > 0 ? sessionEntrySum / sessionEntryN : 0.0));
            sessionFills.Clear();
        }

        void CloseAllFilled(int i, double exitPx, bool isStop = false)
        {
            double fundingPnl = FundingRateSession.PnlPct(gridStartTime, times[i], funding, isLong: false);
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (entryPrice[n] - exitPx) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop)
                           + fundingPnl;
                AddReturn(i, ret, "grid_short", entryPrice[n]);
                filled[n] = false;
            }
        }

        for (int i = warmup; i < candles.Length; i++)
        {
            double atrNow = atr[i] > 1e-10 ? atr[i] : closes[i] * 0.02;
            double adxNow = adx[i];

            if (gridActive)
            {
                holdCount++;

                if (highs[i] >= hardStop)
                {
                    CloseAllFilled(i, hardStop, isStop: true);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                {
                    double highestFill = double.MinValue;
                    for (int n = 0; n < levels; n++)
                        if (filled[n] && entryPrice[n] > highestFill) highestFill = entryPrice[n];
                    if (highestFill > double.MinValue)
                    {
                        double bailoutPx = highestFill + g.BailOutAtrMult * atrAtStart;
                        if (highs[i] >= bailoutPx)
                        {
                            CloseAllFilled(i, bailoutPx, isStop: true);
                            gridActive = false;
                            FlushSession(i);
                            continue;
                        }
                    }
                }

                if (adxNow >= g.AdxThreshold)
                {
                    CloseAllFilled(i, closes[i]);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                if (holdCount >= g.MaxHoldCandles)
                {
                    CloseAllFilled(i, closes[i]);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                for (int n = 0; n < levels; n++)
                {
                    if (!filled[n]) continue;
                    // Rung-to-rung cover when RungSellFrac>0; else legacy TP.
                    double tp = g.RungSellFrac > 0
                        ? entryPrice[n] - g.RungSellFrac * g.GridStepAtrMult * atrAtStart
                        : entryPrice[n] - g.TakeProfitAtrMult * atrAtStart;
                    if (lows[i] <= tp)
                    {
                        double fundingPnl = FundingRateSession.PnlPct(gridStartTime, times[i], funding, isLong: false);
                        double ret = (entryPrice[n] - tp) / entryPrice[n] * 100.0
                                   - TradeCost(atrAtStart, entryPrice[n], isStop: false, isTp: true)
                                   + fundingPnl;
                        AddReturn(i, ret, "grid_short", entryPrice[n]);
                        filled[n] = false;
                    }
                }

                for (int n = 0; n < levels; n++)
                {
                    if (filled[n]) continue;
                    double lvlPrice = anchor + (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice < hardStop && highs[i] >= lvlPrice)
                    {
                        filled[n]     = true;
                        entryPrice[n] = lvlPrice;
                        sessionEntrySum += lvlPrice; sessionEntryN++;
                    }
                }

                if (!filled.Take(levels).Any(f => f))
                {
                    gridActive = false;
                    FlushSession(i);
                }
            }
            else
            {
                if (adxNow >= g.AdxThreshold || bbWidth[i] >= g.BbWidthMaxPct) continue;

                double proposedAnchor = ema[i];
                double proposedAtr    = atrNow;
                double proposedStop   = proposedAnchor + g.HardStopAtrMult * proposedAtr;
                double level1Price    = proposedAnchor + g.GridStepAtrMult * proposedAtr;

                if (level1Price >= proposedStop || highs[i] < level1Price) continue;

                anchor       = proposedAnchor;
                atrAtStart   = proposedAtr;
                hardStop     = proposedStop;
                holdCount    = 0;
                gridStartTime = times[i];
                Array.Clear(filled);
                sessionEntrySum = 0; sessionEntryN = 0;
                sessionFills.Clear();

                for (int n = 0; n < levels; n++)
                {
                    double lvlPrice = anchor + (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice < hardStop && highs[i] >= lvlPrice)
                    {
                        filled[n]     = true;
                        entryPrice[n] = lvlPrice;
                        sessionEntrySum += lvlPrice; sessionEntryN++;
                    }
                }

                gridActive = true;
            }
        }

        if (gridActive)
        {
            double finalPx = closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(gridStartTime, times[^1], funding, isLong: false);
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (entryPrice[n] - finalPx) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop: false)
                           + fundingPnl;
                AddReturn(candles.Length - 1, ret, "grid_short", entryPrice[n]);
            }
            FlushSession(candles.Length - 1);
        }

        int filledCount = filled.Take(levels).Count(f => f);
        return (result, new GridShortTradeState(gridActive, filledCount, anchor, hardStop, holdCount));
    }
}
