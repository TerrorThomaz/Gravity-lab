namespace TradingGA;

// Grid simulator: long grid in ranging markets, 1h candles.
// Direction: LONG (profit when price rises). Funding: isLong=true.
public static class GridSimulator
{
    private const int    AtrPeriod    = 14;
    private const int    AdxPeriod    = 14;  // fixed, not a gene
    private const int    MaxLevels    = 5;
    private const double StopGapAtrK = 0.18;  // gap premium on stop exits (whole ladder)

    // isTp retained on signature but no longer changes cost (limit-fill discount not modelled).
    internal static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false,
                                     double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrAtStart, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);

    // Per-fill returns for backtest display. null funding = floor fallback (not zero).
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetGridReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: false, funding: funding);
        return trades;
    }

    // Per-session returns (mean of fills) for GA fitness.
    // maeOut: opt-in per-trade worst adverse excursion, parallel to the returned list.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetGridSessionReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null,
        List<double>? maeOut = null)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: true, funding: funding, maeOut: maeOut);
        return trades;
    }

    // Scored sessions for ranked portfolio. Score = adxMargin × bbMargin.
    public static List<ScoredTrade> GetScoredGridTrades(string coin, GridGenotype g, ReadOnlySpan<Candle> h1,
        FundingRateSession? funding = null)
    {
        var scored = new List<ScoredTrade>();
        RunGrid(g, h1, sessionLevel: true, coin: coin, scoredOut: scored, funding: funding);
        return scored;
    }

    public record GridTradeState(
        bool   Active,
        int    FilledLevels,
        double Anchor,
        double HardStop,
        int    HoldCount);

    public static GridTradeState GetGridTradeState(GridGenotype g, ReadOnlySpan<Candle> h1)
    {
        var (_, state) = RunGrid(g, h1, sessionLevel: false);
        return state;
    }

    private static (List<(DateTime, double, string, DateTime, double)> Trades, GridTradeState FinalState)
        RunGrid(GridGenotype g, ReadOnlySpan<Candle> candles, bool sessionLevel,
                string? coin = null, List<ScoredTrade>? scoredOut = null,
                FundingRateSession? funding = null,
                // Opt-in, same shape as scoredOut: worst adverse excursion per emitted trade, in
                // percent and <= 0. Feeds FoldScoreHelper.Canonical's drawdown term so the GA can
                // see time at risk instead of only the settled return.
                List<double>? maeOut = null)
    {
        int warmup = Math.Max(Math.Max(Math.Max(g.EmaPeriod, AtrPeriod), AdxPeriod * 2 + 1), g.BbPeriod) + 2;
        if (candles.Length <= warmup + 5)
            return ([], new GridTradeState(false, 0, 0, 0, 0));

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

        bool     gridActive        = false;
        double   anchor            = 0;
        double   atrAtStart        = 0;
        double   hardStop          = 0;
        int      holdCount         = 0;
        int      levels            = Math.Clamp(g.GridLevels, 1, MaxLevels);
        var      filled            = new bool[MaxLevels];
        var      entryPrice        = new double[MaxLevels];
        double   sessionScore      = 0;
        DateTime sessionEntryTime  = default;
        double   sessionMae        = 0.0;   // worst unrealised % of the filled rungs this session

        void AddReturn(int i, double ret, string kind, double entryPx)
        {
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((times[i], ret, kind, sessionEntryTime, entryPx));
        }

        // Running mean captured as levels fill (FlushSession clears filled[] first).
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
            double avg = sessionFills.Average();
            result.Add((times[i], avg, "grid_session", sessionEntryTime,
                        sessionEntryN > 0 ? sessionEntrySum / sessionEntryN : 0.0));
            scoredOut?.Add(new ScoredTrade(coin!, "grid", sessionEntryTime, times[i], avg, sessionScore));
            maeOut?.Add(sessionMae);
            sessionFills.Clear();
            sessionMae = 0.0;
        }

        void CloseAllFilled(int i, double exitPx, bool isStop = false)
        {
            double fundingPnl = FundingRateSession.PnlPct(sessionEntryTime, times[i], funding, isLong: true);
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (exitPx - entryPrice[n]) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop)
                           + fundingPnl;
                AddReturn(i, ret, "grid_long", entryPrice[n]);
                filled[n] = false;
            }
        }

        for (int i = warmup; i < candles.Length; i++)
        {
            double atrNow = atr[i] > 1e-10 ? atr[i] : closes[i] * 0.02;
            double adxNow = adx[i];

            if (gridActive)
            {
                // Re-anchoring: anchor follows live EMA; unfilled rungs reprice, filled levels keep entry.
                if (g.ReanchorAlpha > 0 && ema[i] > 1e-10)
                {
                    anchor   = anchor + g.ReanchorAlpha * (ema[i] - anchor);
                    hardStop = anchor - g.HardStopAtrMult * atrAtStart;
                }
                holdCount++;

                // Worst unrealised point of the open rungs this bar. The low is the adverse
                // extreme for a long grid, so this is a true MAE rather than a close-to-close proxy.
                {
                    double meanEntry = MeanFilled(entryPrice, filled);
                    if (meanEntry > 1e-10)
                    {
                        double unreal = (lows[i] - meanEntry) / meanEntry * 100.0;
                        if (unreal < sessionMae) sessionMae = unreal;
                    }
                }

                if (lows[i] <= hardStop)
                {
                    CloseAllFilled(i, hardStop, isStop: true);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                {
                    double lowestFill = double.MaxValue;
                    for (int n = 0; n < levels; n++)
                        if (filled[n] && entryPrice[n] < lowestFill) lowestFill = entryPrice[n];
                    if (lowestFill < double.MaxValue)
                    {
                        double bailoutPx = lowestFill - g.BailOutAtrMult * atrAtStart;
                        if (lows[i] <= bailoutPx)
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
                    // Rung-to-rung sell when RungSellFrac>0; else legacy TP.
                    double tp = g.RungSellFrac > 0
                        ? entryPrice[n] + g.RungSellFrac * g.GridStepAtrMult * atrAtStart
                        : entryPrice[n] + g.TakeProfitAtrMult * atrAtStart;
                    if (highs[i] >= tp)
                    {
                        double fundingPnl = FundingRateSession.PnlPct(sessionEntryTime, times[i], funding, isLong: true);
                        double ret = (tp - entryPrice[n]) / entryPrice[n] * 100.0
                                   - TradeCost(atrAtStart, entryPrice[n], isStop: false, isTp: true)
                                   + fundingPnl;
                        AddReturn(i, ret, "grid_long", entryPrice[n]);
                        filled[n] = false;
                    }
                }

                for (int n = 0; n < levels; n++)
                {
                    if (filled[n]) continue;
                    double lvlPrice = anchor - (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice > hardStop && lows[i] <= lvlPrice)
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

                int slopeLookback = g.SlopeLookback > 0 ? g.SlopeLookback : 20;
                int slopeRef = Math.Max(0, i - slopeLookback);
                double emaSlope = ema[slopeRef] > 1e-10 ? (ema[i] - ema[slopeRef]) / ema[slopeRef] : 0;
                if (emaSlope < g.SlopeThreshold) continue;

                double proposedAnchor = ema[i];
                double proposedAtr    = atrNow;
                double proposedStop   = proposedAnchor - g.HardStopAtrMult * proposedAtr;
                double level1Price    = proposedAnchor - g.GridStepAtrMult * proposedAtr;

                if (level1Price <= proposedStop || lows[i] > level1Price) continue;

                anchor            = proposedAnchor;
                atrAtStart        = proposedAtr;
                hardStop          = proposedStop;
                holdCount         = 0;
                sessionEntryTime  = times[i];
                double adxMargin = g.AdxThreshold > 1e-10 ? (g.AdxThreshold - adxNow) / g.AdxThreshold : 0;
                double bbMargin  = g.BbWidthMaxPct > 1e-10 ? (g.BbWidthMaxPct - bbWidth[i]) / g.BbWidthMaxPct : 0;
                sessionScore     = Math.Max(0, adxMargin * bbMargin);
                Array.Clear(filled);
                sessionEntrySum = 0; sessionEntryN = 0;
                sessionFills.Clear();

                for (int n = 0; n < levels; n++)
                {
                    double lvlPrice = anchor - (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice > hardStop && lows[i] <= lvlPrice)
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
            double fundingPnl = FundingRateSession.PnlPct(sessionEntryTime, times[^1], funding, isLong: true);
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (finalPx - entryPrice[n]) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop: false)
                           + fundingPnl;
                AddReturn(candles.Length - 1, ret, "grid_long", entryPrice[n]);
            }
            FlushSession(candles.Length - 1);
        }

        int filledCount = filled.Take(levels).Count(f => f);
        return (result, new GridTradeState(gridActive, filledCount, anchor, hardStop, holdCount));
    }

}
