using TradingGA;

namespace GravityGen2.Strategies.AccumulationGrid;

public static class AccumulationGridSimulator
{
    private const int AtrPeriod = 14;
    private const double FeeExchange = 0.11;
    private const double SlipTpK = 0.01;
    private const double SlipMarketK = 0.03;
    private const double SlipStopGap = 0.18;

    private static double TradeCost(double atrAtStart, double entryPx, bool isStop, bool isTp = false)
    {
        double atrPct = atrAtStart / entryPx * 100.0;
        double slip = isStop ? SlipStopGap * atrPct
                    : isTp ? SlipTpK * atrPct
                           : SlipMarketK * atrPct;
        return FeeExchange + slip;
    }

    public static List<(DateTime Time, double Return, string Kind)> GetAccumulationReturns(
        AccumulationGridGenotype g, ReadOnlySpan<Candle> h1, MarketRegime targetRegime)
    {
        var (trades, _) = RunAccumulation(g, h1, targetRegime);
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
        RunAccumulation(AccumulationGridGenotype g, ReadOnlySpan<Candle> candles, MarketRegime targetRegime)
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

        void CloseAll(int i, double exitPx, bool isStop = false)
        {
            for (int n = 0; n < filledLevels; n++)
            {
                double ret = (exitPx - entryPrices[n]) / entryPrices[n] * 100.0
                           - TradeCost(atr[i], entryPrices[n], isStop);
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
                        double ret = (tp - entryPrices[n]) / entryPrices[n] * 100.0
                                   - TradeCost(atrNow, entryPrices[n], isStop: false, isTp: true);
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
                }
            }
        }

        var finalState = new AccumulationTradeState(
            active, filledLevels, ema[^1], trailingStop, holdCount);

        return (result, finalState);
    }
}
