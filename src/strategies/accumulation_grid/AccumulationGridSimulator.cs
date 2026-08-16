using TradingGA;

namespace GravityGen2.Strategies.AccumulationGrid;

// AccumulationGrid simulator: EMA-anchored dynamic accumulation, LONG.
// Funding: isLong=true. Regime-gated with sustain requirement.
public static class AccumulationGridSimulator
{
    private const int AtrPeriod = 14;

    private const double StopGapAtrK = 0.18;  // same as grid family

    internal static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false,
                                     double barNotional = 0.0, double posFrac = 0.0)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrAtStart, entryPx), isStop, StopGapAtrK,
                                   barNotional, posFrac);

    // null funding = floor fallback. EntryPrice returned for execution-quality benchmarking.
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
        // Per-level fill time (sessionStartTime is funding reference, not per-level entry).
        DateTime[] entryTimes = new DateTime[g.MaxLevels];
        double trailingStop = 0;
        double highestPrice = 0;
        int holdCount = 0;
        int regimeSustainCount = 0;
        MarketRegime currentRegime = MarketRegime.Ranging;
        DateTime sessionStartTime = default;

        void CloseAll(int i, double exitPx, bool isStop = false)
        {
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
                    sessionStartTime = times[i];
                }
            }
        }

        var finalState = new AccumulationTradeState(
            active, filledLevels, ema[^1], trailingStop, holdCount);

        return (result, finalState);
    }

    // Acquisition quality vs local VWAP. Returns mean % below local VWAP (positive = cheaper).
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


    // Causal variant: trailing EMA benchmark (backward-looking only, no future bars).
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
            idx = Math.Clamp(idx - 1, 0, h1.Count - 1);
            if (idx < emaPeriod) continue;
            double avg = ema[idx];
            if (avg <= 1e-9) continue;
            sum += (avg - px) / avg * 100.0; n++;
        }
        return n > 0 ? sum / n : double.NaN;
    }

}
