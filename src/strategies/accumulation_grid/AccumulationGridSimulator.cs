using TradingGA;

namespace GravityGen2.Strategies.AccumulationGrid;

// EMA-anchored dynamic accumulation grid.
//
// Direction: LONG. Verified from the gross-return formula, not the file name — every exit
// books (exitPx − entryPrices[n]) / entryPrices[n], i.e. profit when price RISES, and levels
// are limit buys at emaNow − n × GridStepAtrMult × ATR with TP = entry + TakeProfitAtrMult ×
// ATR. So funding is priced with isLong: true, which under the sign rule in FundingRateSession
// makes a positive rate a COST.
//
// Funding used to be missing here entirely — this and GridSimulator were the only simulators
// booking ZERO funding while every other strategy paid at least the interest-rate floor, a
// systematic cost advantage of ~0.01%/8h over holds up to MaxHoldBars. One charge covers the
// whole accumulation session: every level is priced from the session's activation timestamp,
// matching the convention in GridShortSimulator and GridSimulator. Levels that fill later are
// therefore charged for ticks they were not open across — deliberately pessimistic, and
// identical across the grid family so it cannot tilt an intra-family comparison.
public static class AccumulationGridSimulator
{
    private const int AtrPeriod = 14;

    // Cost model: see TradeCosts in src/core/Simulator.cs. Same shape as the other two grid
    // simulators — the three must price a round trip identically. isTp is retained on the
    // signature but no longer changes the cost; see GridSimulator.TradeCost for why the
    // limit-fill discount is deliberately not modelled.
    private const double StopGapAtrK = 0.18;

    internal static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrAtStart, entryPx), isStop, StopGapAtrK);

    // funding: optional real rate series. Passing null does NOT mean "no funding" — the
    // fallback branch of FundingRateSession.PnlPct still charges the interest-rate floor
    // (-0.01pp per 8h settlement crossed), same as every other strategy.
    // EntryPrice is returned because an accumulator's success is an EXECUTION question — what did
    // it pay relative to the market over the same period — not a profit-factor question. Without
    // it the only available score was PF, which is a trading-edge yardstick and let this drift
    // from PF 1.32 to 1.03 unnoticed while nothing measured what it is actually for.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetAccumulationReturns(
        AccumulationGridGenotype g, ReadOnlySpan<Candle> h1, MarketRegime targetRegime,
        FundingRateSession? funding = null)
    {
        var (trades, _) = RunAccumulation(g, h1, targetRegime, funding);
        return trades;
    }

    public record AccumulationTradeState(
        bool Active,
        int FilledLevels,
        double CurrentEma,
        double TrailingStop,
        int HoldCount);

    public static AccumulationTradeState GetAccumulationTradeState(
        AccumulationGridGenotype g, ReadOnlySpan<Candle> h1, MarketRegime targetRegime)
    {
        var (_, state) = RunAccumulation(g, h1, targetRegime);
        return state;
    }

    private static (List<(DateTime, double, string, DateTime, double)> Trades, AccumulationTradeState FinalState)
        RunAccumulation(AccumulationGridGenotype g, ReadOnlySpan<Candle> candles, MarketRegime targetRegime,
                        FundingRateSession? funding = null)
    {
        int warmup = Math.Max((int)g.EmaPeriod, AtrPeriod) + 2;
        if (candles.Length <= warmup + 5)
            return ([], new AccumulationTradeState(false, 0, 0, 0, 0));

        var closes = CandleExt.Closes(candles);
        var highs = CandleExt.Highs(candles);
        var lows = CandleExt.Lows(candles);
        var times = CandleExt.Times(candles);

        var ema = Trend.Ema(closes, g.EmaPeriod);
        var atr = Volatility.Atr(highs, lows, closes, AtrPeriod);
        var regimeSeries = RegimeClassifier.ClassifySeriesWithDuration(candles.ToArray());

        var result = new List<(DateTime, double, string, DateTime, double)>();

        bool active = false;
        int filledLevels = 0;
        double[] entryPrices = new double[g.MaxLevels];
        // Per-LEVEL fill time. sessionStartTime is the funding reference for the whole session and
        // is NOT the entry of levels 2..n — a grid fills progressively as price falls, so using it
        // would misdate every added level. Needed for the unified trade shape and for any
        // acquisition metric that benchmarks each fill against its own local market.
        DateTime[] entryTimes = new DateTime[g.MaxLevels];
        double trailingStop = 0;
        double highestPrice = 0;
        int holdCount = 0;
        int regimeSustainCount = 0;
        MarketRegime currentRegime = MarketRegime.Ranging;
        DateTime sessionStartTime = default;

        void CloseAll(int i, double exitPx, bool isStop = false)
        {
            // isLong: true — levels are limit buys below the EMA and exits book (exit − entry),
            // so a positive funding rate is a cost. See the sign rule in FundingRateSession.
            double fundingPnl = FundingRateSession.PnlPct(sessionStartTime, times[i], funding, isLong: true);
            for (int n = 0; n < filledLevels; n++)
            {
                double ret = (exitPx - entryPrices[n]) / entryPrices[n] * 100.0
                           - TradeCost(atr[i], entryPrices[n], isStop)
                           + fundingPnl;
                result.Add((times[i], ret, "accumulation_long", entryTimes[n], entryPrices[n]));
            }
            filledLevels = 0;
            active = false;
        }

        for (int i = warmup; i < candles.Length; i++)
        {
            double atrNow = atr[i] > 1e-10 ? atr[i] : closes[i] * 0.02;
            double emaNow = ema[i];
            currentRegime = regimeSeries[i].Regime;

            if (active)
            {
                holdCount++;

                if (closes[i] > highestPrice)
                    highestPrice = closes[i];

                trailingStop = highestPrice - g.StopLossAtrMult * atrNow;

                if (lows[i] <= trailingStop)
                {
                    CloseAll(i, trailingStop, isStop: true);
                    continue;
                }

                if (currentRegime != targetRegime)
                {
                    CloseAll(i, closes[i]);
                    continue;
                }

                if (holdCount >= g.MaxHoldBars)
                {
                    CloseAll(i, closes[i]);
                    continue;
                }

                for (int n = 0; n < filledLevels; n++)
                {
                    double tp = entryPrices[n] + g.TakeProfitAtrMult * atrNow;
                    if (highs[i] >= tp)
                    {
                        double fundingPnl = FundingRateSession.PnlPct(sessionStartTime, times[i], funding, isLong: true);
                        double ret = (tp - entryPrices[n]) / entryPrices[n] * 100.0
                                   - TradeCost(atrNow, entryPrices[n], isStop: false, isTp: true)
                                   + fundingPnl;
                        result.Add((times[i], ret, "accumulation_long", entryTimes[n], entryPrices[n]));
                        for (int m = n; m < filledLevels - 1; m++)
                            entryPrices[m] = entryPrices[m + 1];
                        filledLevels--;
                        n--;
                    }
                }

                if (filledLevels < g.MaxLevels)
                {
                    double levelPrice = emaNow - (filledLevels + 1) * g.GridStepAtrMult * atrNow;
                    if (lows[i] <= levelPrice)
                    {
                        entryPrices[filledLevels] = levelPrice;
                        entryTimes[filledLevels]  = times[i];
                        filledLevels++;
                    }
                }

                if (filledLevels == 0)
                    active = false;
            }
            else
            {
                if (currentRegime != targetRegime)
                {
                    regimeSustainCount = 0;
                    continue;
                }

                regimeSustainCount++;
                if (regimeSustainCount < g.RegimeSustainBars)
                    continue;

                double level1Price = emaNow - g.GridStepAtrMult * atrNow;
                if (lows[i] <= level1Price)
                {
                    active = true;
                    filledLevels = 1;
                    entryPrices[0] = level1Price;
                    entryTimes[0]  = times[i];
                    highestPrice = closes[i];
                    trailingStop = highestPrice - g.StopLossAtrMult * atrNow;
                    holdCount = 0;
                    sessionStartTime = times[i];   // funding reference for every level in this session
                }
            }
        }

        var finalState = new AccumulationTradeState(
            active, filledLevels, ema[^1], trailingStop, holdCount);

        return (result, finalState);
    }

    // Acquisition quality vs a LOCAL VWAP window centred on each fill.
    //
    // A whole-series VWAP is not a valid execution benchmark over long spans: on a coin that
    // trended up for three years the full-period VWAP sits far below any recent price, so every
    // late fill scores "above VWAP" no matter how well it was executed. Measured for real —
    // the same accumulator scored +2.54% on a 20% val window and -26.86% on full history, and
    // that gap is the artifact, not the strategy. Comparing each fill to the market average
    // AROUND it answers the question actually being asked: did we buy below the local average?
    //
    // Returns mean % below local VWAP (positive = bought cheaper than the local market).
    public static double AcquisitionDiscountPct(
        IReadOnlyList<Candle> h1, IReadOnlyList<double> entryPrices, IReadOnlyList<DateTime> entryTimes,
        int halfWindowBars = 360)
    {
        if (h1.Count == 0 || entryPrices.Count == 0) return double.NaN;
        var times = new DateTime[h1.Count];
        for (int i = 0; i < h1.Count; i++) times[i] = h1[i].Time;

        double sum = 0; int n = 0;
        for (int k = 0; k < entryPrices.Count; k++)
        {
            double px = entryPrices[k];
            if (px <= 1e-9) continue;
            int idx = Array.BinarySearch(times, entryTimes[k]);
            if (idx < 0) idx = ~idx;
            int lo = Math.Max(0, idx - halfWindowBars);
            int hi = Math.Min(h1.Count - 1, idx + halfWindowBars);
            double pv = 0, vol = 0;
            for (int i = lo; i <= hi; i++)
            {
                double tp = (h1[i].High + h1[i].Low + h1[i].Close) / 3.0;
                pv += tp * h1[i].Volume; vol += h1[i].Volume;
            }
            if (vol <= 1e-9) continue;
            double vwap = pv / vol;
            if (vwap <= 1e-9) continue;
            sum += (vwap - px) / vwap * 100.0; n++;
        }
        return n > 0 ? sum / n : double.NaN;
    }


    // Causal variant: benchmark each fill against a TRAILING EMA of typical price.
    //
    // AcquisitionDiscountPct centres its VWAP window on the fill, so it includes bars AFTER the
    // trade. That is normal in post-hoc transaction-cost analysis ("did we get a good basis vs
    // where it subsequently traded"), but it is not causal — nothing at the fill could have known
    // those bars. The EMA version only ever looks backwards, so it answers the stricter question:
    // at the moment of the fill, was this below the market's own running average?
    //
    // Any gap between the two is informative rather than a discrepancy: EMA-only measures timing
    // skill available in real time; the centred window measures realised basis.
    public static double AcquisitionDiscountEmaPct(
        IReadOnlyList<Candle> h1, IReadOnlyList<double> entryPrices, IReadOnlyList<DateTime> entryTimes,
        int emaPeriod = 360)
    {
        if (h1.Count < emaPeriod + 2 || entryPrices.Count == 0) return double.NaN;

        var typical = new double[h1.Count];
        var times   = new DateTime[h1.Count];
        for (int i = 0; i < h1.Count; i++)
        {
            typical[i] = (h1[i].High + h1[i].Low + h1[i].Close) / 3.0;
            times[i]   = h1[i].Time;
        }
        var ema = new double[h1.Count];
        Trend.EmaInto(typical, emaPeriod, ema);

        double sum = 0; int n = 0;
        for (int k = 0; k < entryPrices.Count; k++)
        {
            double px = entryPrices[k];
            if (px <= 1e-9) continue;
            int idx = Array.BinarySearch(times, entryTimes[k]);
            if (idx < 0) idx = ~idx;
            // Step back one bar: the fill cannot use its own bar's completed average.
            idx = Math.Clamp(idx - 1, 0, h1.Count - 1);
            if (idx < emaPeriod) continue;          // EMA not warmed up yet
            double avg = ema[idx];
            if (avg <= 1e-9) continue;
            sum += (avg - px) / avg * 100.0; n++;
        }
        return n > 0 ? sum / n : double.NaN;
    }

}
