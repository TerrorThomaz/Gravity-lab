namespace TradingGA;

// Grid trading simulator — long grid in ranging markets on 1h candles.
//
// Regime gate: ADX < AdxThreshold → market is oscillating, not trending.
//              This is the dead-time complement of the swing strategy's ADX ≥ threshold uptrend.
//
// Grid mechanics:
//   Anchor  = EMA at the candle where level-1 fills (fixed for the life of the grid).
//   Levels  = buy-limit orders at anchor − n × GridStepAtrMult × ATR  (n = 1..GridLevels).
//   TP      = entry + TakeProfitAtrMult × ATR_at_anchor  (independent per level).
//   Stop    = anchor − HardStopAtrMult × ATR_at_anchor   (closes ALL levels on breach).
//   Timeout = MaxHoldCandles h1 bars from first fill → close all at current price.
//
// Fill detection: candle.Low ≤ level price → filled at level price (limit order).
// TP  detection: candle.High ≥ tp         → closed at tp.
// Stop detection: candle.Low ≤ hard_stop  → closed at hard_stop.
//
// Return per level   = (exit − entry) / entry × 100 − FeeRoundTrip.
// Return per session = mean of all level returns in one grid activation.
public static class GridSimulator
{
    private const int    AtrPeriod    = 14;
    private const int    AdxPeriod    = 14;  // fixed — not a gene; regime splice with swing uses AdxThreshold only
    private const int    MaxLevels    = 5;   // hard cap matching GridLevels gene upper bound

    // Grid entries are limit orders (price comes to you) → entry slip ≈ zero.
    // TP exits are limit sells → near-zero slip. Stop exits gap through the stop level.
    private const double FeeExchange = 0.11;   // 0.055% taker × 2 sides
    private const double SlipTpK     = 0.01;   // TP exits (maker-style): k × (atr/price × 100)
    private const double SlipMarketK = 0.03;   // regime-change / timeout exits
    private const double SlipStopGap = 0.18;   // stop exits: gap risk = k × (atr/price × 100)

    // isStop=true applies gap premium (hard stop blown through in a fast move).
    // isTp=true uses the minimal maker-side cost.
    private static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false)
    {
        double atrPct = atrAtStart / entryPx * 100.0;
        double slip   = isStop ? SlipStopGap * atrPct
                       : isTp  ? SlipTpK     * atrPct
                               : SlipMarketK * atrPct;
        return FeeExchange + slip;
    }

    // Per-fill returns — used for backtest display (trade count, per-trade stats).
    public static List<(DateTime Time, double Return, string Kind)> GetGridReturns(
        GridGenotype g, Candle[] h1)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: false);
        return trades;
    }

    // Per-session returns — one record per grid activation, return = mean of all level fills.
    // Used by GridGA fitness to avoid inflating trade count and win-rate.
    public static List<(DateTime Time, double Return, string Kind)> GetGridSessionReturns(
        GridGenotype g, Candle[] h1)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: true);
        return trades;
    }

    public record GridTradeState(
        bool   Active,
        int    FilledLevels,
        double Anchor,
        double HardStop,
        int    HoldCount);

    public static GridTradeState GetGridTradeState(GridGenotype g, Candle[] h1)
    {
        var (_, state) = RunGrid(g, h1, sessionLevel: false);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, GridTradeState FinalState)
        RunGrid(GridGenotype g, Candle[] candles, bool sessionLevel)
    {
        int warmup = Math.Max(Math.Max(Math.Max(g.EmaPeriod, AtrPeriod), AdxPeriod * 2 + 1), g.BbPeriod) + 2;
        if (candles.Length <= warmup + 5)
            return ([], new GridTradeState(false, 0, 0, 0, 0));

        var closes = candles.Select(c => c.Close).ToArray();
        var highs  = candles.Select(c => c.High).ToArray();
        var lows   = candles.Select(c => c.Low).ToArray();

        var ema     = ComputeEma(closes, g.EmaPeriod);
        var adx     = ComputeAdx(highs, lows, closes, AdxPeriod);
        var atr     = ComputeAtr(highs, lows, closes, AtrPeriod);
        var bbWidth = ComputeBbWidth(closes, g.BbPeriod);

        var result       = new List<(DateTime, double, string)>();
        var sessionFills = new List<double>();

        bool   gridActive   = false;
        double anchor       = 0;
        double atrAtStart   = 0;
        double hardStop     = 0;
        int    holdCount    = 0;
        int    levels       = Math.Clamp(g.GridLevels, 1, MaxLevels);
        var    filled       = new bool[MaxLevels];
        var    entryPrice   = new double[MaxLevels];

        void AddReturn(int i, double ret, string kind)
        {
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((candles[i].Time, ret, kind));
        }

        void FlushSession(int i)
        {
            if (!sessionLevel || sessionFills.Count == 0) return;
            result.Add((candles[i].Time, sessionFills.Average(), "grid_session"));
            sessionFills.Clear();
        }

        void CloseAllFilled(int i, double exitPx, bool isStop = false)
        {
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (exitPx - entryPrice[n]) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop);
                AddReturn(i, ret, "grid_long");
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

                // Hard stop: range broke to the downside — close all
                if (lows[i] <= hardStop)
                {
                    CloseAllFilled(i, hardStop, isStop: true);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                // Regime change: ADX went trending — get out cleanly
                if (adxNow >= g.AdxThreshold)
                {
                    CloseAllFilled(i, closes[i]);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                // Timeout: close all at current price
                if (holdCount >= g.MaxHoldCandles)
                {
                    CloseAllFilled(i, closes[i]);
                    gridActive = false;
                    FlushSession(i);
                    continue;
                }

                // Check TPs for filled levels (candle.High hit TP)
                for (int n = 0; n < levels; n++)
                {
                    if (!filled[n]) continue;
                    double tp = entryPrice[n] + g.TakeProfitAtrMult * atrAtStart;
                    if (highs[i] >= tp)
                    {
                        double ret = (tp - entryPrice[n]) / entryPrice[n] * 100.0
                                   - TradeCost(atrAtStart, entryPrice[n], isStop: false, isTp: true);
                        AddReturn(i, ret, "grid_long");
                        filled[n] = false;
                    }
                }

                // Fill unfilled levels if price dipped to them
                for (int n = 0; n < levels; n++)
                {
                    if (filled[n]) continue;
                    double lvlPrice = anchor - (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice > hardStop && lows[i] <= lvlPrice)
                    {
                        filled[n]     = true;
                        entryPrice[n] = lvlPrice;
                    }
                }

                // Grid complete — all levels hit their TPs, nothing open
                if (!filled.Take(levels).Any(f => f))
                {
                    gridActive = false;
                    FlushSession(i);
                }
            }
            else
            {
                // Not in a grid — check dual regime gate: low ADX AND compressed BB
                if (adxNow >= g.AdxThreshold || bbWidth[i] >= g.BbWidthMaxPct) continue;

                double proposedAnchor = ema[i];
                double proposedAtr    = atrNow;
                double proposedStop   = proposedAnchor - g.HardStopAtrMult * proposedAtr;
                double level1Price    = proposedAnchor - g.GridStepAtrMult * proposedAtr;

                // Only start a grid when price actually dips to level 1
                if (level1Price <= proposedStop || lows[i] > level1Price) continue;

                anchor     = proposedAnchor;
                atrAtStart = proposedAtr;
                hardStop   = proposedStop;
                holdCount  = 0;
                Array.Clear(filled);
                sessionFills.Clear();

                // Fill all levels touched on this entry candle
                for (int n = 0; n < levels; n++)
                {
                    double lvlPrice = anchor - (n + 1) * g.GridStepAtrMult * atrAtStart;
                    if (lvlPrice > hardStop && lows[i] <= lvlPrice)
                    {
                        filled[n]     = true;
                        entryPrice[n] = lvlPrice;
                    }
                }

                gridActive = true;
            }
        }

        // Mark open positions at last close
        if (gridActive)
        {
            double finalPx = closes[^1];
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double ret = (finalPx - entryPrice[n]) / entryPrice[n] * 100.0
                           - TradeCost(atrAtStart, entryPrice[n], isStop: false);
                AddReturn(candles.Length - 1, ret, "grid_long");
            }
            FlushSession(candles.Length - 1);
        }

        int filledCount = filled.Take(levels).Count(f => f);
        return (result, new GridTradeState(gridActive, filledCount, anchor, hardStop, holdCount));
    }

    // ── Indicators (mirrors SwingSimulator — private here to avoid coupling) ──────

    // BB width = (Upper − Lower) / Middle × 100 = 4σ / SMA × 100.
    // Low value → compressed range; high value → expanding / trending.
    private static double[] ComputeBbWidth(double[] closes, int period)
    {
        var width = new double[closes.Length];
        for (int i = period - 1; i < closes.Length; i++)
        {
            double sum = 0, sumSq = 0;
            for (int j = i - period + 1; j <= i; j++) { sum += closes[j]; sumSq += closes[j] * closes[j]; }
            double mean     = sum / period;
            double variance = sumSq / period - mean * mean;
            double std      = variance > 0 ? Math.Sqrt(variance) : 0;
            width[i] = mean > 1e-10 ? 4.0 * std / mean * 100.0 : 0;
        }
        return width;
    }

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
            double hd = highs[i] - highs[i - 1], ld = lows[i - 1] - lows[i];
            tr[i]  = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
            pDm[i] = hd > ld && hd > 0 ? hd : 0;
            mDm[i] = ld > hd && ld > 0 ? ld : 0;
        }
        var sTr = new double[n]; var sPDm = new double[n]; var sMDm = new double[n];
        var dx  = new double[n]; var adx  = new double[n];
        if (period >= n) return adx;
        for (int i = 1; i <= period; i++) { sTr[period] += tr[i]; sPDm[period] += pDm[i]; sMDm[period] += mDm[i]; }
        for (int i = period + 1; i < n; i++)
        {
            sTr[i]  = sTr[i - 1]  - sTr[i - 1]  / period + tr[i];
            sPDm[i] = sPDm[i - 1] - sPDm[i - 1] / period + pDm[i];
            sMDm[i] = sMDm[i - 1] - sMDm[i - 1] / period + mDm[i];
            if (sTr[i] < 1e-10) continue;
            double pDi = 100.0 * sPDm[i] / sTr[i], mDi = 100.0 * sMDm[i] / sTr[i];
            double ds  = pDi + mDi;
            dx[i] = ds > 1e-10 ? 100.0 * Math.Abs(pDi - mDi) / ds : 0;
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
