using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class EmaTests
{
    [Fact]
    public void FlatSeries_ReturnsConstant()
    {
        var closes = Enumerable.Repeat(100.0, 20).ToArray();
        var ema = Trend.Ema(closes, 5);
        // Seeded from closes[0]=100, any constant input stays at 100
        foreach (var v in ema)
            Assert.Equal(100.0, v, precision: 8);
    }

    [Fact]
    public void Period1_EqualsTodaysClose()
    {
        var closes = new double[] { 10, 20, 30, 25, 15 };
        var ema = Trend.Ema(closes, 1);
        // k = 2/(1+1) = 1 → EMA[i] = closes[i]
        for (int i = 0; i < closes.Length; i++)
            Assert.Equal(closes[i], ema[i], precision: 8);
    }

    [Fact]
    public void Period3_KnownSequence()
    {
        // k = 2/(3+1) = 0.5
        // EMA[0] = 10, EMA[1] = 20*0.5 + 10*0.5 = 15, EMA[2] = 30*0.5 + 15*0.5 = 22.5
        var closes = new double[] { 10, 20, 30 };
        var ema = Trend.Ema(closes, 3);
        Assert.Equal(10.0,  ema[0], precision: 8);
        Assert.Equal(15.0,  ema[1], precision: 8);
        Assert.Equal(22.5,  ema[2], precision: 8);
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var closes = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var ema = Trend.Ema(closes, 5);
        Assert.Equal(closes.Length, ema.Length);
    }
}

public class RsiTests
{
    [Fact]
    public void AllRising_Returns100()
    {
        // All gains → avgLoss=0 → RSI=100
        var closes = Enumerable.Range(1, 20).Select(i => (double)i * 10).ToArray();
        var rsi = Momentum.Rsi(closes, 7);
        for (int i = 7; i < rsi.Length; i++)
            Assert.Equal(100.0, rsi[i], precision: 6);
    }

    [Fact]
    public void SymmetricUpDown_Returns50AtWarmup()
    {
        // period=2, closes=[100, 101, 100]: after warmup avgGain=0.5, avgLoss=0.5 → RSI=50
        var closes = new double[] { 100, 101, 100 };
        var rsi = Momentum.Rsi(closes, 2);
        Assert.Equal(50.0, rsi[2], precision: 6);
    }

    [Fact]
    public void AllEqual_ZeroGainAndLoss_Returns100()
    {
        // avgGain=0, avgLoss=0 → RSI=100 (no losses at all)
        var closes = Enumerable.Repeat(100.0, 15).ToArray();
        var rsi = Momentum.Rsi(closes, 7);
        for (int i = 7; i < rsi.Length; i++)
            Assert.Equal(100.0, rsi[i], precision: 6);
    }

    [Fact]
    public void WilderSmoothing_ThreeDownAfterUp()
    {
        // period=2, closes=[100, 101, 100, 99]
        // Warmup (i=1,2): avgGain=(1)/2=0.5, avgLoss=(1)/2=0.5
        // RSI[2]=50; i=3: d=-1, avgGain=(0.5*1+0)/2=0.25, avgLoss=(0.5*1+1)/2=0.75
        // RSI[3]=100-100/(1+0.25/0.75)=100-75=25
        var closes = new double[] { 100, 101, 100, 99 };
        var rsi = Momentum.Rsi(closes, 2);
        Assert.Equal(25.0, rsi[3], precision: 6);
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var closes = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        var rsi = Momentum.Rsi(closes, 14);
        Assert.Equal(closes.Length, rsi.Length);
    }
}

public class AtrTests
{
    private static Candle[] Flat(double price, int count)
    {
        var t = DateTime.UtcNow;
        return Enumerable.Range(0, count)
            .Select(i => new Candle(t.AddHours(i), price, price, price, price, 1000))
            .ToArray();
    }

    [Fact]
    public void FlatCandles_AtrIsZero()
    {
        var c = Flat(100, 30);
        var h = c.Select(x => x.High).ToArray();
        var l = c.Select(x => x.Low).ToArray();
        var cl = c.Select(x => x.Close).ToArray();
        var atr = Volatility.Atr(h, l, cl, 14);
        for (int i = 14; i < atr.Length; i++)
            Assert.Equal(0.0, atr[i], precision: 8);
    }

    [Fact]
    public void ConstantRange_AtrEqualsRange()
    {
        // high=10, low=5, close=7 → TR=5 every bar → ATR converges to 5
        var t = DateTime.UtcNow;
        var candles = Enumerable.Range(0, 30)
            .Select(i => new Candle(t.AddHours(i), 7, 10, 5, 7, 1000))
            .ToArray();
        var h  = candles.Select(c => c.High).ToArray();
        var l  = candles.Select(c => c.Low).ToArray();
        var cl = candles.Select(c => c.Close).ToArray();
        var atr = Volatility.Atr(h, l, cl, 14);
        Assert.Equal(5.0, atr[29], precision: 6);
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var c = Flat(50, 20);
        var atr = Volatility.Atr(c.Select(x => x.High).ToArray(),
                                  c.Select(x => x.Low).ToArray(),
                                  c.Select(x => x.Close).ToArray(), 14);
        Assert.Equal(c.Length, atr.Length);
    }
}

public class AdxTests
{
    [Fact]
    public void FlatCandles_AdxIsZero()
    {
        var t = DateTime.UtcNow;
        var candles = Enumerable.Range(0, 60)
            .Select(i => new Candle(t.AddHours(i), 100, 100, 100, 100, 1000))
            .ToArray();
        var h  = candles.Select(c => c.High).ToArray();
        var l  = candles.Select(c => c.Low).ToArray();
        var cl = candles.Select(c => c.Close).ToArray();
        var adx = Trend.Adx(h, l, cl, 14);
        // After full warmup (period*2) ADX should be 0 for a flat series
        for (int i = 28; i < adx.Length; i++)
            Assert.Equal(0.0, adx[i], precision: 6);
    }

    [Fact]
    public void StrongUptrend_AdxAbove25()
    {
        // Consistently rising highs, lows, closes with no retracement → strong trend
        var t = DateTime.UtcNow;
        var candles = Enumerable.Range(0, 80)
            .Select(i => new Candle(t.AddHours(i), 100 + i, 101 + i, 99 + i, 100.5 + i, 1000))
            .ToArray();
        var h  = candles.Select(c => c.High).ToArray();
        var l  = candles.Select(c => c.Low).ToArray();
        var cl = candles.Select(c => c.Close).ToArray();
        var adx = Trend.Adx(h, l, cl, 14);
        Assert.True(adx[79] > 25, $"Expected ADX > 25 in strong trend, got {adx[79]:F2}");
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var t = DateTime.UtcNow;
        var n = 50;
        var h  = Enumerable.Range(0, n).Select(i => (double)(100 + i)).ToArray();
        var l  = Enumerable.Range(0, n).Select(i => (double)(99  + i)).ToArray();
        var cl = Enumerable.Range(0, n).Select(i => (double)(100 + i)).ToArray();
        var adx = Trend.Adx(h, l, cl, 14);
        Assert.Equal(n, adx.Length);
    }
}

public class BbWidthTests
{
    [Fact]
    public void FlatSeries_WidthIsZero()
    {
        var closes = Enumerable.Repeat(100.0, 30).ToArray();
        var width = Volatility.BbWidth(closes, 20);
        for (int i = 19; i < width.Length; i++)
            Assert.Equal(0.0, width[i], precision: 8);
    }

    [Fact]
    public void KnownVariance_CorrectWidth()
    {
        // period=3, closes=[9,10,11]: mean=10, variance=(1+0+1)/3=2/3, std=sqrt(2/3)
        // BbWidth = 4*std/mean*100 = 4*sqrt(2/3)/10*100 = 40*sqrt(2/3)
        var closes = new double[] { 9.0, 10.0, 11.0 };
        var width = Volatility.BbWidth(closes, 3);
        double expected = 4.0 * Math.Sqrt(2.0 / 3.0) / 10.0 * 100.0;
        Assert.Equal(expected, width[2], precision: 6);
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var closes = Enumerable.Range(1, 40).Select(i => (double)i).ToArray();
        var width = Volatility.BbWidth(closes, 20);
        Assert.Equal(closes.Length, width.Length);
    }
}
