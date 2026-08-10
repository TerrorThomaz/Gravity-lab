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
// Return per level   = (exit − entry) / entry × 100 − TradeCost + funding.
// Return per session = mean of all level returns in one grid activation.
//
// Direction: LONG. Verified from the gross-return formula, not the file name — every exit
// books (exitPx − entryPrice[n]) / entryPrice[n], i.e. profit when price RISES, and levels are
// buy-limits placed BELOW the anchor with TP = entry + TakeProfitAtrMult × ATR. So funding is
// priced with isLong: true, which under the sign rule in FundingRateSession means a positive
// rate is a COST. (GridShortSimulator books (entry − exit) and passes isLong: false.)
//
// Funding used to be missing here entirely — grid was the only family booking ZERO funding
// while every other strategy paid at least the interest-rate floor, a systematic cost advantage
// of ~0.01%/8h on holds up to MaxHoldCandles that biased every Grid-vs-other comparison.
// One charge covers the whole grid activation: all levels are priced from the activation
// timestamp, matching GridShortSimulator's convention. Levels that fill later are therefore
// charged for ticks they were not open across — deliberately pessimistic, and identical on both
// sides of the long/short mirror so it cannot tilt a Grid-vs-GridShort comparison.
public static class GridSimulator
{
    private const int    AtrPeriod    = 14;
    private const int    AdxPeriod    = 14;  // fixed — not a gene; regime splice with swing uses AdxThreshold only
    private const int    MaxLevels    = 5;   // hard cap matching GridLevels gene upper bound

    // Cost model: see TradeCosts in src/core/Simulator.cs.
    // Stop exits close the whole ladder into a range that has just broken, so the gap premium
    // is an order of magnitude above the swing family's — that is a stop-structure difference,
    // not a slippage knob; the slippage magnitude comes from Config.SlippageBps alone.
    private const double StopGapAtrK = 0.18;   // stop exits: gap risk = k × atrPct

    // isStop=true applies the gap premium (hard stop blown through in a fast move).
    // isTp is retained on the signature but no longer changes the cost. It used to select
    // between three independent per-exit-quality slip constants (0.01 / 0.03 / 0.18 × atrPct),
    // which was a second slippage magnitude competing with Config.SlippageBps. Grid entries and
    // TP exits are resting limit orders and would genuinely slip less than a taker fill, but
    // that discount is deliberately NOT modelled: it is unprovable at bar resolution (a limit
    // that fills in a fast move fills badly), and the direction of the error matters here —
    // grid is the family this change is removing cost advantages from, so the conservative
    // choice is to charge it the same round trip as everyone else.
    internal static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrAtStart, entryPx), isStop, StopGapAtrK);

    // Per-fill returns — used for backtest display (trade count, per-trade stats).
    // funding: optional real rate series. Passing null does NOT mean "no funding" — the
    // fallback branch of FundingRateSession.PnlPct still charges the interest-rate floor
    // (-0.01pp per 8h settlement crossed), same as every other strategy.
    // EntryTime/EntryPrice: `Time` is the EXIT bar. For a session-level row the entry is the
    // session's start and the mean of its filled levels — a grid has no single entry.
    // Appended as NAMED fields so existing consumers compile unchanged.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetGridReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: false, funding: funding);
        return trades;
    }

    // Per-session returns — one record per grid activation, return = mean of all level fills.
    // Used by GridGA fitness to avoid inflating trade count and win-rate.
    // EntryTime/EntryPrice: `Time` is the EXIT bar. For a session-level row the entry is the
    // session's start and the mean of its filled levels — a grid has no single entry.
    // Appended as NAMED fields so existing consumers compile unchanged.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetGridSessionReturns(
        GridGenotype g, ReadOnlySpan<Candle> h1, FundingRateSession? funding = null)
    {
        var (trades, _) = RunGrid(g, h1, sessionLevel: true, funding: funding);
        return trades;
    }

    // Returns scored session trades — one per grid activation — for ranked portfolio sim.
    // Score = adxMargin × bbMargin: both factors in [0,1], higher = more clearly ranging.
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
                FundingRateSession? funding = null)
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
        double   sessionScore      = 0;    // signal quality at grid activation
        DateTime sessionEntryTime  = default;

        void AddReturn(int i, double ret, string kind, double entryPx)
        {
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((times[i], ret, kind, sessionEntryTime, entryPx));
        }

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
            result.Add((times[i], avg, "grid_session", sessionEntryTime, MeanFilled(entryPrice, filled)));
            scoredOut?.Add(new ScoredTrade(coin!, "grid", sessionEntryTime, times[i], avg, sessionScore));
            sessionFills.Clear();
        }

        void CloseAllFilled(int i, double exitPx, bool isStop = false)
        {
            // isLong: true — this grid buys below the anchor and books (exit − entry); a
            // positive funding rate is a cost to it. See the sign rule in FundingRateSession.
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
                        double fundingPnl = FundingRateSession.PnlPct(sessionEntryTime, times[i], funding, isLong: true);
                        double ret = (tp - entryPrice[n]) / entryPrice[n] * 100.0
                                   - TradeCost(atrAtStart, entryPrice[n], isStop: false, isTp: true)
                                   + fundingPnl;
                        AddReturn(i, ret, "grid_long", entryPrice[n]);
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
                if (adxNow >= g.AdxThreshold || bbWidth[i] >= g.BbWidthMaxPct) continue;

                int slopeLookback = 20;
                int slopeRef = Math.Max(0, i - slopeLookback);
                double emaSlope = ema[slopeRef] > 1e-10 ? (ema[i] - ema[slopeRef]) / ema[slopeRef] : 0;
                if (emaSlope < -0.005) continue;

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
