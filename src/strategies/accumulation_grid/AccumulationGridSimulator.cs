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
    public static List<(DateTime Time, double Return, string Kind)> GetAccumulationReturns(
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

    private static (List<(DateTime, double, string)> Trades, AccumulationTradeState FinalState)
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

        var result = new List<(DateTime, double, string)>();

        bool active = false;
        int filledLevels = 0;
        double[] entryPrices = new double[g.MaxLevels];
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
                result.Add((times[i], ret, "accumulation_long"));
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
                        result.Add((times[i], ret, "accumulation_long"));
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
}
