namespace TradingGA;

// Grid trading simulator — SHORT grid in ranging markets on 1h candles.
// Direction-mirror of GridSimulator: same ADX/BB compression regime gate, same laddered
// level/TP/stop/bailout structure, flipped to sell above the EMA anchor instead of
// buying below it. Reuses GridGenotype as-is — the gene set is directionally symmetric
// (spacing, levels, TP, stop, bailout, hold); only the simulator's price math flips.
//
// Grid mechanics:
//   Anchor  = EMA at the candle where level-1 fills (fixed for the life of the grid).
//   Levels  = sell-limit orders at anchor + n × GridStepAtrMult × ATR  (n = 1..GridLevels).
//   TP      = entry − TakeProfitAtrMult × ATR_at_anchor  (independent per level).
//   Stop    = anchor + HardStopAtrMult × ATR_at_anchor   (closes ALL levels on breach — range broke UP).
//   Timeout = MaxHoldCandles h1 bars from first fill → close all at current price.
//
// Fill detection: candle.High ≥ level price → filled at level price (limit sell).
// TP  detection: candle.Low  ≤ tp           → closed at tp.
// Stop detection: candle.High ≥ hard_stop   → closed at hard_stop.
public static class GridShortSimulator
{
    private const int    AtrPeriod    = 14;
    private const int    AdxPeriod    = 14;
    private const int    MaxLevels    = 5;

    private const double FeeExchange = 0.11;
    private const double SlipTpK     = 0.01;
    private const double SlipMarketK = 0.03;
    private const double SlipStopGap = 0.18;

    private static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false)
    {
        double atrPct = atrAtStart / entryPx * 100.0;
        double slip   = isStop ? SlipStopGap * atrPct
                       : isTp  ? SlipTpK     * atrPct
                               : SlipMarketK * atrPct;
        return FeeExchange + slip;
    }

    public static List<(DateTime Time, double Return, string Kind)> GetGridShortReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null)
    {
        var (trades, _) = RunGridShort(g, h1, sessionLevel: false, funding: funding);
        return trades;
    }

    public static List<(DateTime Time, double Return, string Kind)> GetGridShortSessionReturns(
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

    private static (List<(DateTime, double, string)> Trades, GridShortTradeState FinalState)
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

        var result       = new List<(DateTime, double, string)>();
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

        void AddReturn(int i, double ret, string kind)
        {
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((times[i], ret, kind));
        }

        void FlushSession(int i)
        {
            if (!sessionLevel || sessionFills.Count == 0) return;
            result.Add((times[i], sessionFills.Average(), "grid_short_session"));
            sessionFills.Clear();
        }

        // Short PnL: (entry - exit) / entry × 100 — profit when price falls.
        void CloseAllFilled(int i, double exitPx, bool isStop = false)
        {
            double fundingPnl = FundingRateSession.PnlPct(gridStartTime, times[i], funding, isLong: false);
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (entryPrice[n] - exitPx) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop)
                           + fundingPnl;
                AddReturn(i, ret, "grid_short");
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

                // Hard stop: range broke UPSIDE — close all
                if (highs[i] >= hardStop)
                {
                    CloseAllFilled(i, hardStop, isStop: true);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                // Bail-out: close all when price rises too far above the highest filled level.
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

                // Regime change: ADX went trending — get out cleanly
                if (adxNow >= g.AdxThreshold)
                {
                    CloseAllFilled(i, closes[i]);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                // Timeout
                if (holdCount >= g.MaxHoldCandles)
                {
                    CloseAllFilled(i, closes[i]);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                // Check TPs for filled levels (candle.Low hit TP — price fell to target)
                for (int n = 0; n < levels; n++)
                {
                    if (!filled[n]) continue;
                    double tp = entryPrice[n] - g.TakeProfitAtrMult * atrAtStart;
                    if (lows[i] <= tp)
                    {
                        double fundingPnl = FundingRateSession.PnlPct(gridStartTime, times[i], funding, isLong: false);
                        double ret = (entryPrice[n] - tp) / entryPrice[n] * 100.0
                                   - TradeCost(atrAtStart, entryPrice[n], isStop: false, isTp: true)
                                   + fundingPnl;
                        AddReturn(i, ret, "grid_short");
                        filled[n] = false;
                    }
                }

                // Fill unfilled levels if price rallied up to them
                for (int n = 0; n < levels; n++)
                {
                    if (filled[n]) continue;
                    double lvlPrice = anchor + (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice < hardStop && highs[i] >= lvlPrice)
                    {
                        filled[n]     = true;
                        entryPrice[n] = lvlPrice;
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

                // Only start a grid when price actually rallies up to level 1
                if (level1Price >= proposedStop || highs[i] < level1Price) continue;

                anchor       = proposedAnchor;
                atrAtStart   = proposedAtr;
                hardStop     = proposedStop;
                holdCount    = 0;
                gridStartTime = times[i];
                Array.Clear(filled);
                sessionFills.Clear();

                for (int n = 0; n < levels; n++)
                {
                    double lvlPrice = anchor + (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice < hardStop && highs[i] >= lvlPrice)
                    {
                        filled[n]     = true;
                        entryPrice[n] = lvlPrice;
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
                AddReturn(candles.Length - 1, ret, "grid_short");
            }
            FlushSession(candles.Length - 1);
        }

        int filledCount = filled.Take(levels).Count(f => f);
        return (result, new GridShortTradeState(gridActive, filledCount, anchor, hardStop, holdCount));
    }
}
