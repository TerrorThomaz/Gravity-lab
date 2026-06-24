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
        GridGenotype g, ReadOnlySpan<Candle> h1)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: false);
        return trades;
    }

    // Per-session returns — one record per grid activation, return = mean of all level fills.
    // Used by GridGA fitness to avoid inflating trade count and win-rate.
    public static List<(DateTime Time, double Return, string Kind)> GetGridSessionReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: true);
        return trades;
    }

    // Returns scored session trades — one per grid activation — for ranked portfolio sim.
    // Score = adxMargin × bbMargin: both factors in [0,1], higher = more clearly ranging.
    public static List<ScoredTrade> GetScoredGridTrades(string coin, GridGenotype g, ReadOnlySpan<Candle> h1)
    {
        var scored = new List<ScoredTrade>();
        RunGrid(g, h1, sessionLevel: true, coin: coin, scoredOut: scored);
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

    private static (List<(DateTime, double, string)> Trades, GridTradeState FinalState)
        RunGrid(GridGenotype g, ReadOnlySpan<Candle> candles, bool sessionLevel,
                string? coin = null, List<ScoredTrade>? scoredOut = null)
    {
        int warmup = Math.Max(Math.Max(Math.Max(g.EmaPeriod, AtrPeriod), AdxPeriod * 2 + 1), g.BbPeriod) + 2;
        if (candles.Length <= warmup + 5)
            return ([], new GridTradeState(false, 0, 0, 0, 0));

        var closes = CandleExt.Closes(candles);
        var highs  = CandleExt.Highs(candles);
        var lows   = CandleExt.Lows(candles);
        var times  = CandleExt.Times(candles);  // extracted upfront — spans can't be captured in local functions

        var ema     = Trend.Ema(closes, g.EmaPeriod);
        var adx     = Trend.Adx(highs, lows, closes, AdxPeriod);
        var atr     = Volatility.Atr(highs, lows, closes, AtrPeriod);
        var bbWidth = Volatility.BbWidth(closes, g.BbPeriod);

        var result       = new List<(DateTime, double, string)>();
        var sessionFills = new List<double>();

        bool     gridActive        = false;
        double   anchor            = 0;
        double   atrAtStart        = 0;
        double   hardStop          = 0;
        int      holdCount         = 0;
        int      levels            = Math.Clamp(g.GridLevels, 1, MaxLevels);
        var      filled            = new bool[MaxLevels];
        var      entryPrice        = new double[MaxLevels];
        double   sessionScore      = 0;    // signal quality at grid activation
        DateTime sessionEntryTime  = default;

        void AddReturn(int i, double ret, string kind)
        {
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((times[i], ret, kind));
        }

        void FlushSession(int i)
        {
            if (!sessionLevel || sessionFills.Count == 0) return;
            double avg = sessionFills.Average();
            result.Add((times[i], avg, "grid_session"));
            scoredOut?.Add(new ScoredTrade(coin!, "grid", sessionEntryTime, times[i], avg, sessionScore));
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

                // Bail-out: close all when price falls too far below the lowest filled level.
                // Cuts slow-grind losses before adding more levels — each new fill would
                // just DCA deeper into a directional move.
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

                anchor            = proposedAnchor;
                atrAtStart        = proposedAtr;
                hardStop          = proposedStop;
                holdCount         = 0;
                sessionEntryTime  = times[i];
                // Signal quality: both factors in [0,1]; higher = more clearly ranging/compressed.
                double adxMargin = g.AdxThreshold > 1e-10 ? (g.AdxThreshold - adxNow) / g.AdxThreshold : 0;
                double bbMargin  = g.BbWidthMaxPct > 1e-10 ? (g.BbWidthMaxPct - bbWidth[i]) / g.BbWidthMaxPct : 0;
                sessionScore     = Math.Max(0, adxMargin * bbMargin);
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

}
