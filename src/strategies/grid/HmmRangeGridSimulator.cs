namespace TradingGA;

// Long grid whose levels come from an HMM ranging segment instead of EMA ± ATR (PROTOTYPE).
//
// Baseline Grid (GridSimulator) anchors on a fast EMA, spaces rungs by GridStepAtrMult × ATR and
// gates on ADX + Bollinger width. This variant asks the HMM "are we ranging?" and, once the range
// has held for MinSegBars, buys a ladder from the range midpoint down to its support and sells
// each rung TpFrac × range-width higher (capped at resistance). The session ends when the HMM
// leaves the ranging state, the hard stop below support trades, or MaxHoldBars elapses.
//
// Deliberately a separate simulator, not a branch inside GridSimulator.RunGrid: the baseline
// stays bit-identical, and the comparison command runs both on the same windows.
//
// From the Grid genotype it reads ONLY GridLevels. Every other gene (ADX/BB gates, EMA anchor,
// ATR step/TP/stop) belongs to the mechanism this replaces. Costs and funding are priced exactly
// as the baseline: GridSimulator.TradeCost + FundingRateSession.PnlPct(isLong: true).
//
// Timing: decisions for bar i use RangeLevels index i-1 (fully closed), then fill on bar i. Limit
// fills that gap through the level fill at the open, not the level — both directions.
public sealed record HmmRangeGridParams(
    int    MinSegBars    = 24,    // range must have held a day before its extremes are trusted
    double MinWidthAtr   = 3.0,   // narrower: round-trip cost eats the rung spacing
    double MaxWidthAtr   = 20.0,  // wider: a trend the HMM has not caught up with yet
    double StopBufferAtr = 1.0,   // hard stop this many ATR below support
    double TpFrac        = 0.5,   // rung TP = entry + TpFrac × width, capped at resistance
    int    MaxHoldBars   = 168);

public static class HmmRangeGridSimulator
{
    private const int AtrPeriod = 14;
    private const int MaxLevels = 5;
    public  const string Kind   = "grid_hmm_range";

    public sealed record Result(
        List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> Trades,
        int Sessions, int StopOuts, int RegimeExits, int Timeouts);

    public static Result Run(GridGenotype g, Candle[] h1, RangeLevels lv, HmmRangeGridParams? p = null,
                             FundingRateSession? funding = null)
    {
        p ??= new HmmRangeGridParams();
        var trades = new List<(DateTime, double, string, DateTime, double)>();
        int sessions = 0, stops = 0, regimeExits = 0, timeouts = 0;
        if (h1.Length != lv.InRange.Length)
            throw new ArgumentException("RangeLevels length does not match candle array");
        if (h1.Length <= AtrPeriod + 2) return new(trades, 0, 0, 0, 0);

        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);
        var atr    = Volatility.Atr(highs, lows, closes, AtrPeriod);

        int levels = Math.Clamp(g.GridLevels, 1, MaxLevels);
        var filled   = new bool[MaxLevels];
        var entryPx  = new double[MaxLevels];
        var fillTime = new DateTime[MaxLevels];
        var rungPx   = new double[MaxLevels];

        bool   active = false;
        double support = 0, resistance = 0, width = 0, atrAtStart = 0, hardStop = 0;
        int    holdCount = 0;
        int    stoppedSegStart = -1;   // no re-entry into a segment that already stopped us out
        DateTime sessionStart = default;

        void Book(int i, int n, double exitPx, bool isStop)
        {
            double ret = (exitPx - entryPx[n]) / entryPx[n] * 100.0
                       - GridSimulator.TradeCost(atrAtStart, entryPx[n], isStop)
                       + FundingRateSession.PnlPct(fillTime[n], h1[i].Time, funding, isLong: true);
            trades.Add((h1[i].Time, ret, Kind, sessionStart, entryPx[n]));
            filled[n] = false;
        }

        void CloseAll(int i, double exitPx, bool isStop)
        {
            for (int n = 0; n < levels; n++) if (filled[n]) Book(i, n, exitPx, isStop);
            active = false;
        }

        for (int i = Math.Max(1, AtrPeriod + 1); i < h1.Length; i++)
        {
            int j = i - 1;   // last fully closed bar

            if (!active)
            {
                if (!lv.InRange[j] || lv.SegBars[j] < p.MinSegBars) continue;
                int segStart = j - lv.SegBars[j] + 1;
                if (segStart == stoppedSegStart) continue;

                double a = atr[j];
                if (a <= 1e-10) continue;
                double s = lv.Support[j], r = lv.Resistance[j], w = r - s;
                if (w < p.MinWidthAtr * a || w > p.MaxWidthAtr * a) continue;
                double mid = (s + r) / 2.0;
                if (closes[j] <= mid) continue;   // ladder sits below price; start from upper half

                active       = true;
                support      = s; resistance = r; width = w; atrAtStart = a;
                hardStop     = s - p.StopBufferAtr * a;
                holdCount    = 0;
                sessionStart = h1[i].Time;
                sessions++;
                double step = (mid - s) / levels;
                for (int n = 0; n < levels; n++) { rungPx[n] = mid - (n + 1) * step; filled[n] = false; }
            }
            else
            {
                // Regime left the range on the previous close → flatten at this bar's open.
                if (!lv.InRange[j])
                {
                    CloseAll(i, h1[i].Open, isStop: false);
                    regimeExits++;
                    continue;
                }
            }

            holdCount++;

            // Stop first (conservative intrabar ordering, same as the baseline).
            if (lows[i] <= hardStop)
            {
                CloseAll(i, Math.Min(hardStop, h1[i].Open), isStop: true);
                stops++;
                stoppedSegStart = j - lv.SegBars[j] + 1;
                continue;
            }

            // Take profits on rungs filled on an EARLIER bar, then re-arm them.
            for (int n = 0; n < levels; n++)
            {
                if (!filled[n]) continue;
                double tp = Math.Min(resistance, entryPx[n] + p.TpFrac * width);
                if (highs[i] >= tp) Book(i, n, Math.Max(tp, h1[i].Open), isStop: false);
            }

            for (int n = 0; n < levels; n++)
            {
                if (filled[n] || lows[i] > rungPx[n]) continue;
                filled[n]   = true;
                entryPx[n]  = Math.Min(rungPx[n], h1[i].Open);
                fillTime[n] = h1[i].Time;
            }

            if (holdCount >= p.MaxHoldBars)
            {
                CloseAll(i, closes[i], isStop: false);
                timeouts++;
            }
        }

        if (active) CloseAll(h1.Length - 1, closes[^1], isStop: false);
        return new(trades, sessions, stops, regimeExits, timeouts);
    }
}
